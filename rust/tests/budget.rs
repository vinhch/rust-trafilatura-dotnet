//! The time budget's expiry, boundary and concurrency behaviour, in a file of its own.
//!
//! The expiry and cap tests rely on the `test-hooks` hold, which is process-global, so every test
//! here takes the same mutex: a test that sets a hold must not run beside one whose deadline
//! depends on it. `tests/worker_cap.rs` pins the cap env var in its own binary, because the cap is
//! read once per process.

#![cfg(feature = "test-hooks")]

use rust_trafilatura_ffi::{rt_extract, rt_free, rt_hold_budgeted_worker_for_test};
use serde_json::Value;
use std::time::{Duration, Instant};

/// Serializes the whole binary: the hold is process-global.
static HOLD: std::sync::Mutex<()> = std::sync::Mutex::new(());

/// Enough article text for the core to keep the document instead of rejecting it as too short.
const ARTICLE: &str = r#"<html lang="en"><head><title>Inline article</title></head><body><article><h1>Inline article</h1><p>This inline article carries enough text for the extractor to keep it, because the core discards documents shorter than its minimum output size. The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#;

/// Calls `rt_extract` with the given header and payload, and reads the buffer as JSON.
fn call(header: &[u8], payload: &[u8]) -> (i32, Option<Value>) {
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

/// Holds every budgeted worker for `ms`, returning the guard that clears the hold.
fn hold_for(ms: usize) -> std::sync::MutexGuard<'static, ()> {
    let guard = HOLD.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    rt_hold_budgeted_worker_for_test(ms);
    guard
}

#[test]
fn held_extraction_returns_time_budget_exceeded_by_the_deadline() {
    let _hold = hold_for(800);
    let start = Instant::now();
    let (code, json) = call(
        br#"{"responseFields":["contentText"],"timeBudgetMs":50}"#,
        ARTICLE.as_bytes(),
    );
    // The deadline carries scheduler slack on every platform; 700 ms is far inside it while still
    // proving the call did not wait out the 800 ms hold.
    assert!(
        start.elapsed() < Duration::from_millis(700),
        "the call must return by the deadline plus scheduler slack, took {:?}",
        start.elapsed()
    );
    assert_eq!(code, 1);
    let json = json.expect("code 1 writes an envelope");
    assert_eq!(json["error"]["kind"], "TimeBudgetExceeded");
    assert_eq!(json["error"]["context"]["budgetMs"], 50);
    assert_eq!(json["contentText"], "");
    assert!(json["metadata"].is_null());
}

#[test]
fn generous_budget_returns_the_normal_result() {
    // No hold: an unset hold is zero, and the mutex alone keeps this test off the held ones.
    let _guard = hold_for(0);
    let header = br#"{"responseFields":["contentText"]}"#;
    let budgeted_header = br#"{"responseFields":["contentText"],"timeBudgetMs":60000}"#;
    let (plain_code, plain) = call(header, ARTICLE.as_bytes());
    let (budgeted_code, budgeted) = call(budgeted_header, ARTICLE.as_bytes());
    assert_eq!(plain_code, 0);
    assert_eq!(budgeted_code, 0);
    let plain = plain.expect("code 0 carries a response");
    let budgeted = budgeted.expect("code 0 carries a response");
    assert_eq!(
        plain["contentText"], budgeted["contentText"],
        "a generous budget must return byte-for-byte the no-budget result"
    );
}

#[test]
fn zero_and_overlarge_budgets_are_invalid_requests() {
    let _guard = hold_for(0);
    for header in [
        &br#"{"timeBudgetMs":0}"#[..],
        &br#"{"timeBudgetMs":4294967297}"#[..],
        &br#"{"timeBudgetMs":-1}"#[..],
    ] {
        let (code, json) = call(header, ARTICLE.as_bytes());
        assert_eq!(code, 2, "{:?} must be rejected", header);
        let json = json.expect("code 2 writes an InvalidRequest envelope");
        assert_eq!(json["error"]["kind"], "InvalidRequest");
    }
}

#[test]
fn parallel_budgeted_calls_each_get_their_own_verdict() {
    let _hold = hold_for(300);
    let header = br#"{"responseFields":["contentText"],"timeBudgetMs":50}"#;
    let payload = ARTICLE.as_bytes().to_vec();
    let verdicts: Vec<(i32, String)> = std::thread::scope(|scope| {
        let calls: Vec<_> = (0..4)
            .map(|_| {
                let payload = payload.clone();
                scope.spawn(move || {
                    let (code, json) = call(
                        br#"{"responseFields":["contentText"],"timeBudgetMs":50}"#,
                        &payload,
                    );
                    let kind = json.expect("every parallel call writes an envelope")["error"]
                        ["kind"]
                        .as_str()
                        .expect("the kind is a string")
                        .to_owned();
                    (code, kind)
                })
            })
            .collect();
        calls
            .into_iter()
            .map(|handle| handle.join().unwrap())
            .collect()
    });
    for (code, kind) in verdicts {
        assert_eq!(code, 1);
        assert_eq!(kind, "TimeBudgetExceeded");
    }
    let _ = header;
}
