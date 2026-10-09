//! The payload cap's boundary, in a file of its own.
//!
//! This is deliberately the only test in this binary. It sets the process environment before the
//! first call, and each cap is read once into a `OnceLock`, so a second test here would race it and
//! could hand code 2 to an unrelated assertion. `tests/abi_hardening.rs` covers the default caps,
//! which need no environment change.

use rust_trafilatura_ffi::{rt_extract, rt_free};
use serde_json::Value;

/// The cap this test pins the boundary against: small enough to build both sides of it.
const CAP: usize = 1024;

/// The environment variable the payload cap is read from; the rejection names it.
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
fn payload_at_cap_is_accepted_and_one_byte_over_is_rejected() {
    std::env::set_var(PAYLOAD_CAP_ENV, CAP.to_string());

    let at_cap = article_of_len(CAP);
    assert_eq!(at_cap.len(), CAP);
    let (code, json) = call(&at_cap);
    assert_eq!(
        code, 0,
        "a payload exactly at the cap must be accepted: {json:?}"
    );

    let one_over = article_of_len(CAP + 1);
    let (code, json) = call(&one_over);
    assert_eq!(code, 2, "one byte over the cap must be rejected: {json:?}");
    let json = json.expect("code 2 writes an InvalidRequest envelope");
    assert_eq!(json["schemaVersion"], 1);
    assert_eq!(json["error"]["kind"], "InvalidRequest");
    let reason = json["error"]["context"]["reason"]
        .as_str()
        .expect("an InvalidRequest carries its reason");
    assert!(reason.contains(PAYLOAD_CAP_ENV), "{reason}");
    assert!(reason.contains(&(CAP + 1).to_string()), "{reason}");
}
