//! The runaway-worker cap's programmatic surface, in a file of its own.
//!
//! The cap is one process-wide `OnceLock`, so the cases that share a fresh cap run here as a
//! single ordered test — the only shape several of them can take in one process, because each
//! needs the cap unfixed and only one `rt_set_worker_cap` can ever apply. Two cases cannot
//! share this binary at all: the one where the first budgeted call itself must fix the cap at
//! the default (no `rt_set_worker_cap` may precede it) is `worker_cap_default_fix.rs`, and the
//! one that poisons the environment variable is `worker_cap_u32_saturate.rs`.

#![cfg(feature = "test-hooks")]

use rust_trafilatura_ffi::{
    rt_extract, rt_free, rt_hold_budgeted_worker_for_test, rt_live_runaway_workers,
    rt_set_worker_cap, rt_worker_cap,
};
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
fn set_read_and_live_counts_in_order() {
    // A read before anything else reports the would-be default and fixes nothing, so the set
    // below is still free to apply.
    assert_eq!(rt_worker_cap(), 16);

    // Zero is invalid and fixes nothing.
    assert_eq!(rt_set_worker_cap(0), 2);

    // The set applies — proof the read above and the rejected zero fixed nothing — and the
    // accessor reports the applied value.
    assert_eq!(rt_set_worker_cap(2), 0);
    assert_eq!(rt_worker_cap(), 2);

    // The applied cap bounds workers. With a 300 ms hold and a 50 ms budget, two calls fill the
    // two slots and a third is refused without copying anything or starting a thread.
    rt_hold_budgeted_worker_for_test(300);
    let header = br#"{"responseFields":["contentText"],"timeBudgetMs":50}"#;
    let (code, json) = call(header, ARTICLE.as_bytes());
    assert_eq!(code, 1);
    assert_eq!(
        json.expect("code 1 writes an envelope")["error"]["kind"],
        "TimeBudgetExceeded"
    );
    let (code, json) = call(header, ARTICLE.as_bytes());
    assert_eq!(code, 1);
    assert_eq!(
        json.expect("code 1 writes an envelope")["error"]["kind"],
        "TimeBudgetExceeded"
    );
    let (code, json) = call(header, ARTICLE.as_bytes());
    assert_eq!(code, 1);
    let json = json.expect("code 1 writes an envelope");
    assert_eq!(json["error"]["kind"], "WorkerCapExceeded");
    assert_eq!(json["error"]["context"]["limit"], 2);

    // While the hold keeps the workers alive, the live count sees them.
    assert!(
        rt_live_runaway_workers() >= 1,
        "a held worker must be visible in the live count"
    );

    // Once a worker finishes it releases its slot, and a new call is accepted again — every
    // rejected poll is the cap still held, and the first accepted call still gets
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

    // The accepted poll spawned one more holding worker, so zero needs its own wait: every
    // worker from this test eventually finishes and the live count drains back to it.
    let deadline = Instant::now() + Duration::from_secs(60);
    while rt_live_runaway_workers() > 0 {
        assert!(Instant::now() < deadline, "a worker never finished");
        std::thread::sleep(Duration::from_millis(100));
    }

    // The cap was fixed by the set above, so a later set reports already-fixed and the
    // accessor keeps reporting the applied value.
    assert_eq!(rt_set_worker_cap(8), 1);
    assert_eq!(rt_worker_cap(), 2);
}
