//! The cap fixed by the first budgeted call, in a file of its own.
//!
//! This must not share a binary with any `rt_set_worker_cap` call: the scenario needs the
//! first budgeted call itself to fix the cap at the default, so that a later set reports
//! already-fixed. No `test-hooks` symbol is used, so the binary runs in every suite.

use rust_trafilatura_ffi::{rt_extract, rt_free, rt_set_worker_cap, rt_worker_cap};

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
fn set_after_a_budgeted_call_reports_already_fixed() {
    // Fresh process, no environment variable and no earlier set: the first budgeted call reads
    // the cap, fixing it at the default 16. The generous budget lets the extraction finish.
    let header = br#"{"responseFields":["contentText"],"timeBudgetMs":60000}"#;
    let (code, json) = call(header, ARTICLE.as_bytes());
    assert_eq!(code, 0);
    assert!(json.expect("code 0 writes a response")["error"].is_null());

    // The cap is fixed, so a later set reports already-fixed and the accessor still reports
    // the value the first call fixed.
    assert_eq!(rt_set_worker_cap(8), 1);
    assert_eq!(rt_worker_cap(), 16);
}
