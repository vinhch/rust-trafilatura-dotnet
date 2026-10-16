//! The runaway-worker cap, in a file of its own.
//!
//! This is deliberately the only test in this binary. It sets the cap environment variable before
//! the first call, and the cap is read once into a `OnceLock`, so a second test here would race
//! it and could hand a `WorkerCapExceeded` to an unrelated assertion.

#![cfg(feature = "test-hooks")]

use rust_trafilatura_ffi::{rt_extract, rt_free, rt_hold_budgeted_worker_for_test};
use std::time::{Duration, Instant};

/// Enough article text for the core to keep the document instead of rejecting it as too short.
const ARTICLE: &str = r#"<html lang="en"><head><title>Inline article</title></head><body><article><h1>Inline article</h1><p>This inline article carries enough text for the extractor to keep it, because the core discards documents shorter than its minimum output size. The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#;

/// Calls `rt_extract` with the given header and payload, and reads the buffer as JSON.
fn call(header: &[u8], payload: &[u8]) -> (i32, Option<serde_json::Value>) {
    let (mut out, mut len, mut cap) = (std::ptr::null_mut(), 0usize, 0usize);
    let code = unsafe {
        rt_extract(
            header.as_ptr(),
            header.len(),
            payload.as_ptr(),
            payload.len(),
            &mut out,
            &mut len,
            &mut cap,
        )
    };
    if out.is_null() {
        return (code, None);
    }
    let bytes = unsafe { std::slice::from_raw_parts(out, len) }.to_vec();
    unsafe { rt_free(out, len, cap) };
    (
        code,
        Some(serde_json::from_slice(&bytes).expect("the response must be UTF-8 JSON")),
    )
}

#[test]
fn second_call_at_the_cap_is_rejected_without_a_thread() {
    // The hold keeps the first call's worker alive (and its slot taken) while the second call
    // arrives. It is a fraction of the pre-third-call sleep so the first worker has long
    // finished — the extraction itself runs after the hold — before the slot must read free.
    rt_hold_budgeted_worker_for_test(300);
    std::env::set_var("RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS", "1");

    let header = br#"{"responseFields":["contentText"],"timeBudgetMs":50}"#;
    let (code, json) = call(header, ARTICLE.as_bytes());
    assert_eq!(code, 1);
    let json = json.expect("code 1 writes an envelope");
    assert_eq!(json["error"]["kind"], "TimeBudgetExceeded");

    // The first worker still holds the single slot, so the second call cannot spawn and must be
    // rejected without copying anything or starting a thread.
    let (code, json) = call(header, ARTICLE.as_bytes());
    assert_eq!(code, 1);
    let json = json.expect("code 1 writes an envelope");
    assert_eq!(json["error"]["kind"], "WorkerCapExceeded");
    assert_eq!(json["error"]["context"]["limit"], 1);

    // Once the first worker finishes it releases its slot, and a new call is accepted again —
    // any verdict but WorkerCapExceeded proves the guard decremented. A debug build's first
    // extraction can take seconds, so poll for the release instead of sleeping a fixed time:
    // every rejected poll is the cap still held, and the first accepted call still gets
    // TimeBudgetExceeded because the hold above outlasts the 50 ms budget.
    let deadline = Instant::now() + Duration::from_secs(30);
    loop {
        std::thread::sleep(Duration::from_millis(100));
        let (code, json) = call(header, ARTICLE.as_bytes());
        let json = json.expect("code 1 writes an envelope");
        let kind = json["error"]["kind"]
            .as_str()
            .expect("the kind is a string")
            .to_owned();
        assert_eq!(code, 1);
        if kind != "WorkerCapExceeded" {
            assert_eq!(
                kind, "TimeBudgetExceeded",
                "the hold must still outlast the budget for every accepted call"
            );
            break;
        }
        assert!(
            Instant::now() < deadline,
            "the first worker never released its slot"
        );
    }
}
