use super::*;
use crate::contract::Payload;

/// A pointer whose address is valid but whose bytes are not: [`check_input`] only compares
/// lengths, so an impossible buffer length is safe to test against it.
fn unread_buffer() -> *const u8 {
    std::ptr::NonNull::<u8>::dangling().as_ptr()
}

#[test]
fn cap_from_value_keeps_zero_and_falls_back_on_garbage() {
    assert_eq!(cap_from_value(None, 7), 7);
    for value in [
        "",
        "   ",
        "abc",
        "-1",
        "1e3",
        "0x10",
        "99999999999999999999",
    ] {
        assert_eq!(cap_from_value(Some(value.to_owned()), 7), 7, "{value:?}");
    }
    assert_eq!(
        cap_from_value(Some("0".to_owned()), 7),
        0,
        "0 disables the cap instead of falling back"
    );
    assert_eq!(cap_from_value(Some(" 1024 ".to_owned()), 7), 1024);
}

#[test]
fn check_input_rejects_a_null_buffer_with_a_length() {
    assert!(check_input(std::ptr::null(), 8, unread_buffer(), 0, 1024).is_err());
    assert!(check_input(unread_buffer(), 0, std::ptr::null(), 8, 1024).is_err());
    // A null pointer with a zero length is the empty input, which reaches the core.
    assert!(check_input(std::ptr::null(), 0, std::ptr::null(), 0, 1024).is_ok());
}

#[test]
fn check_input_accepts_a_header_exactly_at_its_cap() {
    assert!(check_input(unread_buffer(), 1024, unread_buffer(), 2048, 1024).is_ok());
}

#[test]
fn check_input_rejects_a_header_above_its_cap() {
    let error = check_input(unread_buffer(), 1025, unread_buffer(), 1, 1024)
        .expect_err("1025 bytes is above a 1024-byte header cap");
    assert!(
        matches!(error, RequestError::InvalidRequest(reason) if reason.contains(HEADER_CAP_ENV))
    );
}

#[test]
fn check_input_with_a_zero_header_cap_accepts_any_header_length() {
    assert!(check_input(unread_buffer(), usize::MAX, unread_buffer(), 0, 0).is_ok());
}

#[test]
fn on_worker_thread_without_a_stack_runs_on_the_caller_thread() {
    let caller = std::thread::current().id();
    assert_eq!(
        on_worker_thread(None, || std::thread::current().id()),
        caller
    );
}

#[test]
fn on_worker_thread_with_a_stack_runs_on_another_thread() {
    let caller = std::thread::current().id();
    let worker = on_worker_thread(Some(WORKER_STACK_BYTES), || std::thread::current().id());
    assert_ne!(worker, caller);
}

#[test]
fn on_worker_thread_resumes_a_panic_on_either_path() {
    for stack in [None, Some(WORKER_STACK_BYTES)] {
        let caught = catch_unwind(|| on_worker_thread::<()>(stack, || panic!("worker panic")));
        let message = panic_message(&*caught.expect_err("the panic must reach the caller"));
        assert_eq!(message, "worker panic", "stack {stack:?}");
    }
}

#[test]
fn worker_thread_carries_the_cancel_token() {
    const PAGE: &[u8] = br#"<html lang="en"><head><title>Worker page</title></head><body><article><h1>Worker page</h1><p>The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring.</p><p>A second paragraph adds enough body text for the extractor to keep the article instead of discarding it as too short.</p></article></body></html>"#;
    let request = contract::parse_request(br#"{"responseFields":["contentText"]}"#).unwrap();
    let token = CancelToken::new();
    token.cancel();
    let response = on_worker_thread(Some(WORKER_STACK_BYTES), || {
        engine::build(&request, Payload::Bytes(PAGE), None, Some(&token))
    });
    let error = response
        .error
        .expect("a cancelled token must stop the extraction on the worker thread");
    assert_eq!(error.kind, "Cancelled");
}
