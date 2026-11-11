//! The C ABI contract: return codes, response envelopes and buffer ownership.
//!
//! Every test drives the exported functions the way the C# interop layer does: buffers cross as
//! `(ptr, len)` and a returned buffer is released with `rt_free` after its bytes are read. The
//! exports are `unsafe` because the caller owns the pointers, so every call sits in an `unsafe`
//! block.

use rust_trafilatura_ffi::{rt_extract, rt_free, rt_version};
use serde_json::Value;

/// Enough article text for the core to keep the document instead of rejecting it as too short.
const HTML: &str = r#"<html lang="en"><head><title>Inline article</title></head><body><article><h1>Inline article</h1><p>This inline article carries enough text for the extractor to keep it, because the core discards documents shorter than its minimum output size. The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#;

/// Calls `rt_extract` and returns its return code plus the parsed response.
fn extract(header: &[u8], payload: &[u8]) -> (i32, Option<Value>) {
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
    (code, take_json(out, len, cap))
}

/// Copies the returned buffer and releases it. A null buffer yields `None`.
fn take_bytes(out: *mut u8, len: usize, cap: usize) -> Option<Vec<u8>> {
    if out.is_null() {
        return None;
    }
    let bytes = unsafe { std::slice::from_raw_parts(out, len) }.to_vec();
    unsafe { rt_free(out, len, cap) };
    Some(bytes)
}

/// Reads the returned buffer as JSON.
fn take_json(out: *mut u8, len: usize, cap: usize) -> Option<Value> {
    let bytes = take_bytes(out, len, cap)?;
    Some(serde_json::from_slice(&bytes).expect("the response must be UTF-8 JSON"))
}

/// Asserts that a failure envelope carries every content field and `metadata` as an explicit null.
///
/// The key must be there as well as be null. `Value`'s index returns `Null` for an absent key, so an
/// assertion on the value alone cannot tell a documented null from a key that serialization
/// dropped, which is what the "every key is always serialized" promise on `engine::Response` rests on.
fn assert_failure_envelope_is_empty(json: &Value, case: &str) {
    for key in ["contentText", "commentsText", "metadata"] {
        assert!(
            json.get(key).is_some_and(Value::is_null),
            "{case}: {key} must be present and null, not absent"
        );
    }
}

#[test]
fn round_trip_returns_text_and_frees() {
    let header = br#"{"responseFields":["contentText"]}"#;
    let (code, json) = extract(header, HTML.as_bytes());
    assert_eq!(code, 0);
    let json = json.expect("code 0 returns a buffer");
    assert!(json["contentText"]
        .as_str()
        .expect("contentText is a string")
        .contains("article"));
}

#[test]
fn non_url_value_extracts() {
    let (code, json) = extract(br#"{"schemaVersion":2,"url":"not a url"}"#, HTML.as_bytes());
    assert_eq!(
        code, 0,
        "a url the core cannot parse must not fail the call: {json:?}"
    );
    let json = json.expect("code 0 returns a buffer");
    assert!(!json["contentText"]
        .as_str()
        .expect("contentText is a string")
        .is_empty());
}

#[test]
fn malformed_headers_return_invalid_request() {
    for header in [
        &br#"[]"#[..],
        &br#"{"unknownKey":true}"#[..],
        &br#"{"schemaVersion":3}"#[..],
        &br#"{"responseFields":[]}"#[..],
    ] {
        let (code, json) = extract(header, HTML.as_bytes());
        assert_eq!(code, 2, "{header:?} must be an invalid request");
        let json = json.expect("code 2 writes an InvalidRequest envelope the caller owns");
        assert_eq!(json["schemaVersion"], 2, "{header:?}");
        assert_eq!(json["error"]["kind"], "InvalidRequest", "{header:?}");
        assert!(
            json["error"]["context"]["reason"].is_string(),
            "{header:?} must carry its reason"
        );
        assert!(json["error"]["context"].get("url").is_none(), "{header:?}");
        assert_failure_envelope_is_empty(&json, &format!("{header:?}"));
    }
}

#[test]
fn payload_text_with_valid_utf8_extracts() {
    let header = br#"{"schemaVersion":2,"payloadText":true}"#;
    let (code, json) = extract(header, HTML.as_bytes());
    assert_eq!(
        code, 0,
        "a UTF-8 payload sent as text must extract: {json:?}"
    );
}

#[test]
fn payload_text_with_invalid_utf8_is_invalid_request() {
    let header = br#"{"schemaVersion":2,"payloadText":true}"#;
    let (code, json) = extract(header, &[0xff, 0xfe]);
    assert_eq!(code, 2);
    let json = json.expect("code 2 writes an InvalidRequest envelope the caller owns");
    assert_eq!(json["error"]["kind"], "InvalidRequest");
    let reason = json["error"]["context"]["reason"]
        .as_str()
        .expect("the reason is a string");
    assert!(reason.contains("not valid UTF-8"), "{reason:?}");
    assert_failure_envelope_is_empty(&json, "payloadText with invalid UTF-8");
}

#[test]
fn missing_metadata_reports_the_missing_fields() {
    let header = br#"{"responseFields":["contentText"],"options":{"hasEssentialMetadata":true}}"#;
    let (code, json) = extract(header, HTML.as_bytes());
    assert_eq!(code, 1, "{json:?}");
    let json = json.expect("code 1 writes an error envelope the caller owns");
    assert_eq!(json["error"]["kind"], "MissingMetadata");
    assert_eq!(
        json["error"]["context"],
        serde_json::json!({ "missing": ["date", "url"] })
    );
}

#[test]
fn rt_version_reports_abi_schema_crate_and_core() {
    let (mut out, mut len, mut cap) = (std::ptr::null_mut(), 0usize, 0usize);
    let code = unsafe { rt_version(&mut out, &mut len, &mut cap) };
    assert_eq!(code, 0);
    let bytes = take_bytes(out, len, cap).expect("code 0 returns a buffer");
    let text = String::from_utf8(bytes).expect("the document must be UTF-8");
    assert_eq!(
        text,
        format!(
            r#"{{"abi":2,"schemaVersion":2,"crate":"{}","core":"{}"}}"#,
            env!("CARGO_PKG_VERSION"),
            env!("RT_CORE_VERSION")
        )
    );
}

#[test]
fn null_out_params_are_rejected_without_writing() {
    // Sentinels rather than zeros: a rejected call that wrote a zero into the out-parameters would
    // be invisible against a zero starting point.
    const LEN: usize = 7;
    const CAP: usize = 9;

    let header = br#"{"responseFields":["contentText"]}"#;
    let payload = HTML.as_bytes();
    let extracted = |out: *mut *mut u8, out_len: *mut usize, out_cap: *mut usize| unsafe {
        rt_extract(
            header.as_ptr(),
            header.len(),
            payload.as_ptr(),
            payload.len(),
            out,
            out_len,
            out_cap,
        )
    };

    let (mut len, mut cap) = (LEN, CAP);
    assert_eq!(extracted(std::ptr::null_mut(), &mut len, &mut cap), 2);
    assert_eq!(
        (len, cap),
        (LEN, CAP),
        "a rejected call must not write *out_len or *out_cap"
    );

    let mut out: *mut u8 = std::ptr::null_mut();
    let mut cap = CAP;
    assert_eq!(extracted(&mut out, std::ptr::null_mut(), &mut cap), 2);
    assert!(out.is_null(), "a rejected call must not write *out");
    assert_eq!(cap, CAP, "a rejected call must not write *out_cap");

    let mut out: *mut u8 = std::ptr::null_mut();
    let mut len = LEN;
    assert_eq!(extracted(&mut out, &mut len, std::ptr::null_mut()), 2);
    assert!(out.is_null(), "a rejected call must not write *out");
    assert_eq!(len, LEN, "a rejected call must not write *out_len");

    let (mut len, mut cap) = (LEN, CAP);
    assert_eq!(
        unsafe { rt_version(std::ptr::null_mut(), &mut len, &mut cap) },
        2
    );
    assert_eq!(
        (len, cap),
        (LEN, CAP),
        "rt_version must not write the lengths"
    );

    let mut out: *mut u8 = std::ptr::null_mut();
    let mut cap = CAP;
    assert_eq!(
        unsafe { rt_version(&mut out, std::ptr::null_mut(), &mut cap) },
        2
    );
    assert!(out.is_null(), "a rejected call must not write *out");
    assert_eq!(cap, CAP, "rt_version must not write *out_cap");

    let mut out: *mut u8 = std::ptr::null_mut();
    let mut len = LEN;
    assert_eq!(
        unsafe { rt_version(&mut out, &mut len, std::ptr::null_mut()) },
        2
    );
    assert!(out.is_null(), "a rejected call must not write *out");
    assert_eq!(len, LEN, "rt_version must not write *out_len");
}
