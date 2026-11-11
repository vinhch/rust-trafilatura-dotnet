//! The payload cap's boundary, in a file of its own.
//!
//! Each test sets its own process environment before the first call, and each cap is read once into
//! a `OnceLock`. nextest runs every test in its own process, so the tests here cannot race each other;
//! plain `cargo test` would share one process and must not be used for this file.
//! `tests/abi_hardening.rs` covers the default caps, which need no environment change.

use rust_trafilatura_ffi::{rt_extract, rt_free};
use serde_json::Value;

/// The cap these tests pin the boundary against: small enough to build both sides of it.
const CAP: usize = 1024;

/// The environment variable the payload cap is read from.
const PAYLOAD_CAP_ENV: &str = "RUSTTRAFILATURA_MAX_PAYLOAD_BYTES";

/// Enough article text for the core to keep the document instead of rejecting it as too short.
const ARTICLE: &str = r#"<html lang="en"><head><title>Inline article</title></head><body><article><h1>Inline article</h1><p>This inline article carries enough text for the extractor to keep it, because the core discards documents shorter than its minimum output size. The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#;

/// The article padded with spaces to exactly `len` bytes. The padding is trailing whitespace after
/// `</html>`, which the core trims, so both sides of the cap extract the same document.
fn article_of_len(len: usize) -> Vec<u8> {
    let mut payload = ARTICLE.as_bytes().to_vec();
    assert!(
        payload.len() < len,
        "the fixture is {} bytes, not under {len}",
        payload.len()
    );
    payload.resize(len, b' ');
    payload
}

/// Calls `rt_extract` and reads the returned buffer as JSON.
fn call(payload: &[u8]) -> (i32, Option<Value>) {
    let header = br#"{"responseFields":["contentText"]}"#;
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
fn payload_over_cap_is_input_too_large() {
    std::env::set_var(PAYLOAD_CAP_ENV, CAP.to_string());

    let one_over = article_of_len(CAP + 1);
    let (code, json) = call(&one_over);
    assert_eq!(
        code, 1,
        "one byte over the cap is a core rejection: {json:?}"
    );
    let json = json.expect("code 1 writes an extraction envelope");
    assert_eq!(json["schemaVersion"], 2);
    assert_eq!(json["error"]["kind"], "InputTooLarge");
    assert_eq!(json["error"]["context"]["limit"], "maxInputBytes");
}

#[test]
fn payload_at_cap_is_accepted() {
    std::env::set_var(PAYLOAD_CAP_ENV, CAP.to_string());

    let at_cap = article_of_len(CAP);
    assert_eq!(at_cap.len(), CAP);
    let (code, json) = call(&at_cap);
    assert_eq!(
        code, 0,
        "a payload exactly at the cap must be accepted: {json:?}"
    );
}

#[test]
fn zero_cap_lifts_the_limit() {
    std::env::set_var(PAYLOAD_CAP_ENV, "0");

    let over_the_cap = article_of_len(CAP + 1);
    let (code, json) = call(&over_the_cap);
    assert_eq!(code, 0, "a cap of 0 disables the payload cap: {json:?}");
}
