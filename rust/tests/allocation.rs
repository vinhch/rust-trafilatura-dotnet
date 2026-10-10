#![cfg(feature = "test-hooks")]
//! The live-output-buffer counter, in a file of its own.
//!
//! Every test here asserts on one process-global counter, so they live in a binary that measures
//! nothing else and they serialize against each other: a second test in this binary running in
//! parallel would hand its +1 and -1 across a measurement window and make a balanced counter look
//! unbalanced. The two `test-hooks` exports this needs are absent from every build without the
//! feature, and this whole file compiles out with it.

use rust_trafilatura_ffi::{rt_extract, rt_free, rt_live_buffers_for_test};
use std::sync::Mutex;

/// Serializes the tests below: both assert on the same counter.
static COUNTER: Mutex<()> = Mutex::new(());

/// Cycles in the balance test; the same 1,000 the specification's test table names.
const CALLS: usize = 1_000;

/// Requests only `contentText`, so each cycle does a real extraction and a small serialization.
const HEADER: &[u8] = br#"{"responseFields":["contentText"]}"#;

/// Enough article text for the core to keep the document instead of rejecting it as too short.
const ARTICLE: &str = r#"<html lang="en"><head><title>Inline article</title></head><body><article><h1>Inline article</h1><p>This inline article carries enough text for the extractor to keep it, because the core discards documents shorter than its minimum output size. The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#;

/// The counter of buffers the caller has not released yet.
fn live_buffers() -> usize {
    rt_live_buffers_for_test()
}

/// Runs one extraction and returns the buffer it handed out, still owned by the caller.
fn extract_owned() -> (*mut u8, usize, usize) {
    let (mut out, mut len, mut cap) = (std::ptr::null_mut(), 0usize, 0usize);
    let code = unsafe {
        rt_extract(
            HEADER.as_ptr(),
            HEADER.len(),
            ARTICLE.as_ptr(),
            ARTICLE.len(),
            &mut out,
            &mut len,
            &mut cap,
        )
    };
    assert_eq!(code, 0, "the fixture must extract");
    assert!(!out.is_null(), "code 0 hands out a buffer");
    (out, len, cap)
}

/// Takes the counter lock, tolerating a poison left by a failed sibling test.
fn lock_counter() -> std::sync::MutexGuard<'static, ()> {
    COUNTER
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

#[test]
fn free_round_trip_restores_allocation_counter() {
    let _guard = lock_counter();
    let baseline = live_buffers();

    let (out, len, cap) = extract_owned();
    assert_eq!(
        live_buffers(),
        baseline + 1,
        "the buffer handed to the caller must count as live"
    );

    unsafe { rt_free(out, len, cap) };
    assert_eq!(
        live_buffers(),
        baseline,
        "rt_free must return the counter to where it started"
    );
}

#[test]
fn allocation_counter_balances_over_many_calls() {
    let _guard = lock_counter();
    let baseline = live_buffers();

    for call in 0..CALLS {
        let (out, len, cap) = extract_owned();
        assert_eq!(
            live_buffers(),
            baseline + 1,
            "call {call} handed out no buffer"
        );
        unsafe { rt_free(out, len, cap) };
        assert_eq!(live_buffers(), baseline, "call {call} did not balance");
    }

    assert_eq!(
        live_buffers(),
        baseline,
        "{CALLS} extract and free cycles must return the counter to its starting value"
    );
}

#[test]
fn concurrent_extract_and_free_returns_the_counter_to_baseline() {
    const WORKERS: usize = 8;
    const CALLS_PER_WORKER: usize = 100;

    // The sequential tests above cannot separate an atomic update from a read-modify-write: with one
    // thread there is no interleaving to lose. Only the final value is asserted, so the test does not
    // race the counter it measures.
    let _guard = lock_counter();
    let baseline = live_buffers();

    std::thread::scope(|scope| {
        for _ in 0..WORKERS {
            scope.spawn(|| {
                for _ in 0..CALLS_PER_WORKER {
                    let (out, len, cap) = extract_owned();
                    unsafe { rt_free(out, len, cap) };
                }
            });
        }
    });

    assert_eq!(
        live_buffers(),
        baseline,
        "{} cycles on {WORKERS} threads must return the counter to its starting value",
        WORKERS * CALLS_PER_WORKER
    );
}
