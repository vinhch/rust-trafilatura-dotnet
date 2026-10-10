//! The ABI's input boundaries: the two size caps, null pointers, non-UTF-8 headers, deep nesting
//! and concurrent calls.
//!
//! Every test drives the exported functions the way the C# interop layer does and releases each
//! returned buffer with `rt_free`, so the exports are `unsafe` and every call sits in an `unsafe`
//! block. The one test that changes the process environment lives in `tests/caps.rs` instead: the
//! caps are read once per process, so a second test in that binary would race them.

#[cfg(feature = "test-hooks")]
use rust_trafilatura_ffi::rt_panic_for_test;
use rust_trafilatura_ffi::{rt_extract, rt_free};
use serde_json::Value;

/// The default payload cap in bytes. The cap is compared with `>`, so a payload exactly at the cap is
/// accepted.
const DEFAULT_PAYLOAD_CAP: usize = 64 * 1024 * 1024;

/// The default header cap in bytes.
const DEFAULT_HEADER_CAP: usize = 1024 * 1024;

/// The environment variables the caps are read from; a rejection names the variable it broke.
const PAYLOAD_CAP_ENV: &str = "RUSTTRAFILATURA_MAX_PAYLOAD_BYTES";
const HEADER_CAP_ENV: &str = "RUSTTRAFILATURA_MAX_HEADER_BYTES";

/// Enough article text for the core to keep the document instead of rejecting it as too short.
const ARTICLE: &str = r#"<html lang="en"><head><title>Inline article</title></head><body><article><h1>Inline article</h1><p>This inline article carries enough text for the extractor to keep it, because the core discards documents shorter than its minimum output size. The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#;

/// Requests only `contentText`, so a response depends on nothing but the payload.
const TEXT_ONLY: &[u8] = br#"{"responseFields":["contentText"]}"#;

/// The refusal reason of an `InvalidRequest` envelope, which is what code 2 carries when a guard
/// rejected the request rather than a null out-parameter.
fn rejection_reason(json: &Value) -> String {
    assert_eq!(json["schemaVersion"], 1, "{json}");
    assert_eq!(json["error"]["kind"], "InvalidRequest", "{json}");
    json["error"]["context"]["reason"]
        .as_str()
        .expect("an InvalidRequest carries its reason")
        .to_owned()
}

/// Calls `rt_extract` with the payload given as a pointer and a length.
fn call_raw(header: &[u8], payload: *const u8, payload_len: usize) -> (i32, Option<Value>) {
    let (mut out, mut len, mut cap) = (std::ptr::null_mut(), 0usize, 0usize);
    let code = unsafe {
        rt_extract(
            header.as_ptr(),
            header.len(),
            payload,
            payload_len,
            &mut out,
            &mut len,
            &mut cap,
        )
    };
    (code, take_json(out, len, cap))
}

/// Calls `rt_extract` with a borrowed payload.
fn call(header: &[u8], payload: &[u8]) -> (i32, Option<Value>) {
    call_raw(header, payload.as_ptr(), payload.len())
}

/// Reads the returned buffer as JSON and releases it.
fn take_json(out: *mut u8, len: usize, cap: usize) -> Option<Value> {
    if out.is_null() {
        return None;
    }
    let bytes = unsafe { std::slice::from_raw_parts(out, len) }.to_vec();
    unsafe { rt_free(out, len, cap) };
    Some(serde_json::from_slice(&bytes).expect("the response must be UTF-8 JSON"))
}

/// Calls the real extraction path and returns the response as its exact wire text.
fn extract_text(header: &[u8], payload: &[u8]) -> (i32, Option<String>) {
    let (code, json) = call(header, payload);
    (code, json.map(|value| value.to_string()))
}

/// A seeded xorshift, so the garbage below is reproducible.
fn pseudo_random(seed: &mut u64, len: usize) -> Vec<u8> {
    (0..len)
        .map(|_| {
            *seed ^= *seed << 13;
            *seed ^= *seed >> 7;
            *seed ^= *seed << 17;
            *seed as u8
        })
        .collect()
}

#[test]
fn header_over_cap_is_rejected() {
    // The same header shape under the cap parses and extracts, so only the length can explain the
    // rejection: a header the cap did not stop would reach the core, and the core never returns 2.
    let accepted = br#"{"options":{"pruneSelector":"aside"}}"#;
    let (code, json) = call(accepted, ARTICLE.as_bytes());
    assert_eq!(code, 0, "the control header must extract: {json:?}");

    // Not JSON on purpose. A run that parsed before it measured lengths would report serde's
    // message for these bytes at any length, so the same bytes below and above the cap separate the
    // two orders: under it the parser answers, over it the cap does.
    let unparseable = "a".repeat(DEFAULT_HEADER_CAP + 1);
    let (code, json) = call(
        &unparseable.as_bytes()[..DEFAULT_HEADER_CAP - 1],
        ARTICLE.as_bytes(),
    );
    assert_eq!(code, 2, "the control header must be parsed: {json:?}");
    let reason = rejection_reason(&json.expect("code 2 writes an InvalidRequest envelope"));
    assert!(reason.contains("invalid header"), "{reason}");
    assert!(!reason.contains(HEADER_CAP_ENV), "{reason}");

    let (code, json) = call(unparseable.as_bytes(), ARTICLE.as_bytes());
    assert_eq!(code, 2, "a header over the cap must be rejected: {json:?}");
    let reason = rejection_reason(&json.expect("code 2 writes an InvalidRequest envelope"));
    assert!(reason.contains(HEADER_CAP_ENV), "{reason}");
    assert!(
        reason.contains(&DEFAULT_HEADER_CAP.to_string()),
        "the reason must name the cap it enforced, not only the variable: {reason}"
    );
    assert!(
        !reason.contains("invalid header"),
        "the cap must be enforced before the header is parsed: {reason}"
    );
}

#[test]
fn payload_over_cap_is_rejected_before_parsing() {
    // A truncated gzip stream is a payload the core rejects with its own error, code 1. Sending the
    // same stream past the cap must give code 2 instead: only the wrapper returns 2, so a cap that
    // ran after parsing would have returned the core's 1.
    let truncated_gzip = [0x1f, 0x8b, 0x08, 0x00, 0x00];
    let (code, json) = call(TEXT_ONLY, &truncated_gzip);
    assert_eq!(code, 1, "the control payload must reach the core: {json:?}");

    let mut over = vec![0u8; DEFAULT_PAYLOAD_CAP + 1];
    over[..truncated_gzip.len()].copy_from_slice(&truncated_gzip);
    let (code, json) = call(TEXT_ONLY, &over);
    assert_eq!(code, 2, "a payload over the cap must be rejected");
    let reason = rejection_reason(&json.expect("code 2 writes an InvalidRequest envelope"));
    assert!(reason.contains(PAYLOAD_CAP_ENV), "{reason}");
    assert!(
        reason.contains(&DEFAULT_PAYLOAD_CAP.to_string()),
        "the reason must name the cap it enforced, not only the variable: {reason}"
    );
    assert!(
        reason.contains(&(DEFAULT_PAYLOAD_CAP + 1).to_string()),
        "{reason}"
    );
}

#[test]
fn thousand_level_nesting_returns_on_default_stack() {
    let html = format!("{}text{}", "<div>".repeat(1000), "</div>".repeat(1000));
    let (code, json) = call(TEXT_ONLY, html.as_bytes());
    assert!(
        matches!(code, 0 | 1),
        "1000 levels must return a result, not a fallback: code {code}"
    );
    let json = json.expect("code 0 or 1 writes the response");
    assert_eq!(json["schemaVersion"], 1);
}

#[test]
fn garbage_requests_never_panic() {
    let mut seed = 0x5eed_u64;
    for round in 0..500 {
        let bytes = pseudo_random(&mut seed, 64);
        let (code, json) = call(&bytes, &bytes);
        assert!(
            (0..=3).contains(&code),
            "round {round}: code {code} is outside the ABI"
        );
        let json = json.expect("every return code writes a caller-owned buffer");
        assert_eq!(json["schemaVersion"], 1, "round {round}: {json}");

        // The same bytes as the payload, under a header the contract accepts, so the core decodes
        // and parses them instead of the wrapper rejecting the request first.
        let (code, json) = call(TEXT_ONLY, &bytes);
        assert!(
            matches!(code, 0 | 1),
            "round {round}: a valid header with a garbage payload must reach the core, got code {code}"
        );
        let json = json.expect("every return code writes a caller-owned buffer");
        assert_eq!(json["schemaVersion"], 1, "round {round}: {json}");
        if code == 1 {
            assert_ne!(
                json["error"]["kind"], "InvalidRequest",
                "round {round}: the contract accepted the header: {json}"
            );
        }
    }

    // The rounds above prove nothing about the payload being read, because every garbage payload
    // and the empty one reach the same core error. These bytes are the gzip magic, which only the
    // core classifies: a call that dropped or ignored the payload would report the empty-input
    // OutputTooShort instead of the inflater's Io.
    let mut gzipish = vec![0x1f, 0x8b, 0x08, 0x00, 0x00];
    gzipish.extend_from_slice(&pseudo_random(&mut seed, 59));
    let (code, json) = call(TEXT_ONLY, &gzipish);
    assert_eq!(code, 1, "{json:?}");
    let json = json.expect("code 1 writes the error envelope");
    assert_eq!(json["error"]["kind"], "Io", "{json}");
}

#[test]
fn free_null_is_noop() {
    // A null pointer must short-circuit before the allocation is reconstructed, so the lengths it
    // travels with must not matter.
    unsafe { rt_free(std::ptr::null_mut(), 0, 0) };
    unsafe { rt_free(std::ptr::null_mut(), 16, 32) };
}

#[test]
fn payload_null_with_nonzero_length_is_invalid() {
    let (code, json) = call_raw(TEXT_ONLY, std::ptr::null(), 8);
    assert_eq!(code, 2, "a null payload with a length must be rejected");
    let reason = rejection_reason(&json.expect("code 2 writes an InvalidRequest envelope"));
    assert!(
        reason.contains("payload") && reason.contains("null"),
        "{reason}"
    );

    // A null payload with a zero length is the empty input, which reaches the core: the core's own
    // error, not the wrapper's rejection.
    let (code, json) = call_raw(TEXT_ONLY, std::ptr::null(), 0);
    assert_eq!(code, 1, "null with length zero is empty input: {json:?}");
}

#[test]
fn null_header_with_nonzero_length_is_invalid() {
    // A null header would otherwise read as the empty header and be rejected as missing, so the
    // reason is what separates the two.
    let (mut out, mut len, mut cap) = (std::ptr::null_mut(), 0usize, 0usize);
    let code = unsafe {
        rt_extract(
            std::ptr::null(),
            8,
            ARTICLE.as_ptr(),
            ARTICLE.len(),
            &mut out,
            &mut len,
            &mut cap,
        )
    };
    assert_eq!(code, 2, "a null header with a length must be rejected");
    let reason = rejection_reason(&take_json(out, len, cap).expect("code 2 writes the envelope"));
    assert!(
        reason.contains("header") && reason.contains("null"),
        "{reason}"
    );
}

#[test]
fn non_utf8_header_is_invalid_request() {
    let (code, json) = call(&[0xff, 0xfe], ARTICLE.as_bytes());
    assert_eq!(code, 2, "a non-UTF-8 header must be rejected");
    let reason = rejection_reason(&json.expect("code 2 writes an InvalidRequest envelope"));
    assert!(reason.contains("UTF-8"), "{reason}");
}

#[test]
fn empty_payload_returns_error_not_panic() {
    let (code, json) = call(TEXT_ONLY, &[]);
    assert_eq!(code, 1, "zero-length input is the core's own error");
    let json = json.expect("code 1 writes the error envelope");
    assert_eq!(json["schemaVersion"], 1);
    assert_eq!(json["error"]["kind"], "OutputTooShort", "{json}");
    assert_eq!(json["contentText"], "", "{json}");
}

#[test]
fn concurrent_calls_match_sequential() {
    const WORKERS: usize = 8;
    const CALLS: usize = 1_000;

    let expected = extract_text(TEXT_ONLY, ARTICLE.as_bytes());
    assert_eq!(
        expected.0, 0,
        "the fixture must extract, or equality proves nothing: {:?}",
        expected.1
    );

    let results: Vec<(i32, Option<String>)> = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..WORKERS)
            .map(|_| {
                scope.spawn(|| {
                    (0..CALLS / WORKERS)
                        .map(|_| extract_text(TEXT_ONLY, ARTICLE.as_bytes()))
                        .collect::<Vec<_>>()
                })
            })
            .collect();
        handles
            .into_iter()
            .flat_map(|handle| handle.join().expect("a caller thread must not panic"))
            .collect()
    });

    assert_eq!(results.len(), CALLS);
    for (index, result) in results.iter().enumerate() {
        assert_eq!(
            result, &expected,
            "call {index} differs from the sequential result"
        );
    }
}

/// The panic boundary, through the export the C# side never sees: `test-hooks` is off in every
/// release artifact, so the symbol below does not exist there.
#[cfg(feature = "test-hooks")]
#[test]
fn panic_is_caught() {
    let (mut out, mut len, mut cap) = (std::ptr::null_mut(), 0usize, 0usize);
    let code = unsafe { rt_panic_for_test(&mut out, &mut len, &mut cap) };
    assert_eq!(code, 3, "a panic must become a return code, not an unwind");

    let json = take_json(out, len, cap).expect("code 3 writes the Panic envelope");
    assert_eq!(json["schemaVersion"], 1);
    assert_eq!(json["error"]["kind"], "Panic", "{json}");
    let message = json["error"]["context"]["message"]
        .as_str()
        .expect("the Panic envelope carries the message");
    assert!(message.contains("panics on purpose"), "{message}");
    for key in [
        "contentText",
        "commentsText",
        "contentHtml",
        "commentsHtml",
        "metadata",
    ] {
        assert!(json[key].is_null(), "{key} must stay null on a panic");
    }

    // The null out-parameter guard runs before the body, so it rejects without panicking.
    let (mut out, mut len, mut cap) = (std::ptr::null_mut(), 0usize, 0usize);
    assert_eq!(
        unsafe { rt_panic_for_test(std::ptr::null_mut(), &mut len, &mut cap) },
        2
    );
    assert_eq!(
        unsafe { rt_panic_for_test(&mut out, std::ptr::null_mut(), &mut cap) },
        2
    );
    assert!(out.is_null(), "a rejected call must not write *out");
    assert_eq!(
        unsafe { rt_panic_for_test(&mut out, &mut len, std::ptr::null_mut()) },
        2
    );
    assert!(out.is_null(), "a rejected call must not write *out");
}
