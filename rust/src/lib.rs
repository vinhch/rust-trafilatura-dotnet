//! C ABI glue for the published `rust-trafilatura` crate.
//!
//! Modules:
//!
//! - `contract`: request header and response JSON types, plus validation.
//! - `engine`: core invocation and response assembly.
//!
//! The exported symbols (`rt_extract`, `rt_free`, `rt_version` and the `test-hooks`-gated test
//! hooks) and the crate's only `unsafe` code belong in this file. Each export validates its
//! out-parameters and the size of its inputs before reading or writing anything, and runs its body
//! behind a panic boundary, so a panic becomes return code 3 with a `Panic` envelope instead of
//! unwinding into the host process.
//!
//! A panic hook installed on the first export call keeps the default hook's stderr output out of
//! the host process, because a library inside a .NET host must not write there.

pub mod contract;
pub mod engine;

use crate::contract::{Request, RequestError, SCHEMA_VERSION};
use crate::engine::{ErrorEnvelope, Response};
use serde::Serialize;
use serde_json::{json, Value};
use std::panic::{catch_unwind, resume_unwind, AssertUnwindSafe};
#[cfg(feature = "test-hooks")]
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::OnceLock;

/// The ABI version. Independent of both the crate version and [`SCHEMA_VERSION`].
const ABI_VERSION: u32 = 1;

/// Return codes, fixed by the C ABI contract.
const CODE_OK: i32 = 0;
const CODE_EXTRACTION_ERROR: i32 = 1;
const CODE_INVALID_REQUEST: i32 = 2;
const CODE_PANIC_CAUGHT: i32 = 3;

/// Environment variable holding the extraction stack size in bytes.
const STACK_BYTES_ENV: &str = "RUSTTRAFILATURA_STACK_BYTES";

/// Stack size used when the environment variable is absent or unusable.
const DEFAULT_STACK_BYTES: usize = 16 * 1024 * 1024;

/// The extraction stack size, read once so it cannot change between calls.
static STACK_BYTES: OnceLock<usize> = OnceLock::new();

/// Environment variable holding the payload cap in bytes; `0` disables the cap.
const PAYLOAD_CAP_ENV: &str = "RUSTTRAFILATURA_MAX_PAYLOAD_BYTES";

/// Payload cap used when the environment variable is absent or unusable.
const DEFAULT_MAX_PAYLOAD_BYTES: usize = 64 * 1024 * 1024;

/// Environment variable holding the header cap in bytes; `0` disables the cap.
const HEADER_CAP_ENV: &str = "RUSTTRAFILATURA_MAX_HEADER_BYTES";

/// Header cap used when the environment variable is absent or unusable.
const DEFAULT_MAX_HEADER_BYTES: usize = 1024 * 1024;

/// The payload cap, read once so it cannot change between calls.
static MAX_PAYLOAD_BYTES: OnceLock<usize> = OnceLock::new();

/// The header cap, read once so it cannot change between calls.
static MAX_HEADER_BYTES: OnceLock<usize> = OnceLock::new();

/// Set once the stderr-silencing panic hook is installed.
static PANIC_HOOK: OnceLock<()> = OnceLock::new();

/// Output buffers handed to the caller and not yet released, for the `test-hooks` allocation tests.
#[cfg(feature = "test-hooks")]
static LIVE_BUFFERS: AtomicUsize = AtomicUsize::new(0);

/// Extracts from a raw payload and returns the response JSON.
///
/// Return codes: `0` success, `1` extraction error (an `InvalidUrl` header or a core error), `2`
/// invalid request, `3` a caught panic. Code `2` has two cases: a null `out`, `out_len` or `out_cap`
/// writes nothing, while a rejected request writes an `InvalidRequest` envelope. Every code that
/// writes a buffer (`0`, `1`, `3`, and `2` when the request was rejected) hands the caller a UTF-8
/// JSON buffer of `*out_len` bytes that the caller owns and releases with [`rt_free`].
///
/// `header` is the UTF-8 JSON header (`schemaVersion`, `url`, `inputEncoding`, `options`,
/// `responseFields`) and `payload` is raw HTML or gzip bytes, never base64. A null `payload` with
/// `payload_len == 0` is empty input and reaches the core.
///
/// Both buffers are checked before either is read, and each check can reject the request on its own:
/// a null pointer beside a non-zero length, a header above 1 MiB, a payload above 64 MiB, or a
/// header the contract rejects. A buffer exactly at its cap is accepted, and the caps are therefore
/// enforced before the header is parsed and before anything is allocated in proportion to the
/// input. `RUSTTRAFILATURA_MAX_PAYLOAD_BYTES` and `RUSTTRAFILATURA_MAX_HEADER_BYTES` override the
/// caps; each is read once, so a value set after the first call has no effect, and `0` disables the
/// cap it names.
///
/// # Safety
///
/// `header` and `payload` must each point to the number of readable bytes their length argument
/// states, and `out`, `out_len` and `out_cap` must be valid, writable pointers.
#[no_mangle]
pub unsafe extern "C" fn rt_extract(
    header: *const u8,
    header_len: usize,
    payload: *const u8,
    payload_len: usize,
    out: *mut *mut u8,
    out_len: *mut usize,
    out_cap: *mut usize,
) -> i32 {
    silence_panics();
    if out.is_null() || out_len.is_null() || out_cap.is_null() {
        return CODE_INVALID_REQUEST;
    }
    let (code, bytes) = guarded(|| run_extract(header, header_len, payload, payload_len));
    write_out(out, out_len, out_cap, bytes);
    code
}

/// Releases a buffer returned by [`rt_extract`] or [`rt_version`].
///
/// A null `ptr` returns immediately, which is what makes a null `*out` safe to pass; in a
/// `test-hooks` build it also leaves the live-buffer counter alone, because nothing was handed out.
///
/// # Safety
///
/// `ptr` must be a buffer a previous call handed out, with the `len` and `cap` that call wrote,
/// and it must not have been released already: releasing a live pointer twice is undefined
/// behaviour.
#[no_mangle]
pub unsafe extern "C" fn rt_free(ptr: *mut u8, len: usize, cap: usize) {
    silence_panics();
    if ptr.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        drop(unsafe { Vec::from_raw_parts(ptr, len, cap) });
    }));
    #[cfg(feature = "test-hooks")]
    LIVE_BUFFERS.fetch_sub(1, Ordering::SeqCst);
}

/// Reports the ABI version, the JSON schema version and the crate version.
///
/// Returns `0` with the document, `2` for a null out-parameter (which writes nothing) or `3` with
/// the `Panic` envelope. The document carries `abi` 1, `schemaVersion` 1 and `crate` from
/// `env!("CARGO_PKG_VERSION")`. The buffer contract is [`rt_extract`]'s: the caller owns `*out` on
/// `0` and `3` and releases it with [`rt_free`].
///
/// # Safety
///
/// `out`, `out_len` and `out_cap` must be valid, writable pointers.
#[no_mangle]
pub unsafe extern "C" fn rt_version(
    out: *mut *mut u8,
    out_len: *mut usize,
    out_cap: *mut usize,
) -> i32 {
    silence_panics();
    if out.is_null() || out_len.is_null() || out_cap.is_null() {
        return CODE_INVALID_REQUEST;
    }
    let (code, bytes) = guarded(|| (CODE_OK, encode(&VersionJson::current())));
    write_out(out, out_len, out_cap, bytes);
    code
}

/// Panics on purpose, so the `test-hooks` suite can prove the panic boundary end to end.
///
/// The codes are [`rt_extract`]'s: `2` for a null out-parameter, which writes nothing, and `3` with
/// the `Panic` envelope. The symbol exists only in a build with the `test-hooks` feature, which the
/// specification requires to be absent from every release artifact.
///
/// # Safety
///
/// `out`, `out_len` and `out_cap` must be valid, writable pointers.
#[cfg(feature = "test-hooks")]
#[no_mangle]
pub unsafe extern "C" fn rt_panic_for_test(
    out: *mut *mut u8,
    out_len: *mut usize,
    out_cap: *mut usize,
) -> i32 {
    silence_panics();
    if out.is_null() || out_len.is_null() || out_cap.is_null() {
        return CODE_INVALID_REQUEST;
    }
    let (code, bytes) = guarded(|| panic!("rt_panic_for_test panics on purpose"));
    write_out(out, out_len, out_cap, bytes);
    code
}

/// Reports how many output buffers the caller has not released yet.
///
/// It counts the hand-offs in [`write_out`] against the releases in [`rt_free`], and exists only in
/// a build with the `test-hooks` feature.
#[cfg(feature = "test-hooks")]
#[no_mangle]
pub extern "C" fn rt_live_buffers_for_test() -> usize {
    LIVE_BUFFERS.load(Ordering::SeqCst)
}

/// The `rt_version` document.
///
/// The crate version comes from the manifest, so the reported value cannot drift from the
/// package version.
#[derive(Serialize)]
struct VersionJson {
    abi: u32,
    #[serde(rename = "schemaVersion")]
    schema_version: u32,
    #[serde(rename = "crate")]
    krate: &'static str,
}

impl VersionJson {
    fn current() -> Self {
        Self {
            abi: ABI_VERSION,
            schema_version: SCHEMA_VERSION,
            krate: env!("CARGO_PKG_VERSION"),
        }
    }
}

/// Parses the header, extracts and returns the return code with the response JSON.
fn run_extract(
    header: *const u8,
    header_len: usize,
    payload: *const u8,
    payload_len: usize,
) -> (i32, Vec<u8>) {
    if let Err(error) = check_input(
        header,
        header_len,
        payload,
        payload_len,
        header_cap(),
        payload_cap(),
    ) {
        return request_error(error);
    }
    // SAFETY: the buffer pointers and lengths are the caller's contract, stated in the exports'
    // `# Safety` sections; the header is read first, so a rejected header never touches the payload.
    let request = match contract::parse_request(unsafe { bytes(header, header_len) }) {
        Ok(request) => request,
        Err(error) => return request_error(error),
    };
    // SAFETY: as above, and the payload buffer is only read once the header was accepted.
    let response = on_worker_thread(&request, unsafe { bytes(payload, payload_len) });
    let code = if response.error.is_some() {
        CODE_EXTRACTION_ERROR
    } else {
        CODE_OK
    };
    (code, encode(&response))
}

/// Rejects the inputs the ABI refuses, before either buffer is read.
///
/// Neither pointer is dereferenced here, and each problem is reported in the order it is found: a
/// null pointer beside a non-zero length, the header cap, then the payload cap. Both caps are
/// compared with `>`, so a buffer exactly at its cap is accepted, and a cap of `0` is disabled.
fn check_input(
    header: *const u8,
    header_len: usize,
    payload: *const u8,
    payload_len: usize,
    header_cap: usize,
    payload_cap: usize,
) -> Result<(), RequestError> {
    if header.is_null() && header_len > 0 {
        return Err(RequestError::InvalidRequest(format!(
            "header is null with a non-zero length of {header_len}"
        )));
    }
    if payload.is_null() && payload_len > 0 {
        return Err(RequestError::InvalidRequest(format!(
            "payload is null with a non-zero length of {payload_len}"
        )));
    }
    if header_cap > 0 && header_len > header_cap {
        return Err(RequestError::InvalidRequest(format!(
            "header is {header_len} bytes, above the {header_cap}-byte cap in {HEADER_CAP_ENV}"
        )));
    }
    if payload_cap > 0 && payload_len > payload_cap {
        return Err(RequestError::InvalidRequest(format!(
            "payload is {payload_len} bytes, above the {payload_cap}-byte cap in {PAYLOAD_CAP_ENV}"
        )));
    }
    Ok(())
}

/// The return code and envelope for a header the contract rejected.
///
/// `InvalidUrl` is an extraction error (code 1) because the URL is the core's own input, and the
/// design fixes that mapping; everything else the header can get wrong is an `InvalidRequest`
/// (code 2).
fn request_error(error: RequestError) -> (i32, Vec<u8>) {
    match error {
        RequestError::InvalidRequest(reason) => {
            let context = json!({ "reason": reason.clone() });
            (
                CODE_INVALID_REQUEST,
                encode(&error_response("InvalidRequest", reason, Some(context))),
            )
        }
        RequestError::InvalidUrl(url) => {
            let context = json!({ "url": url.clone() });
            let message = format!("url is not an absolute http or https URL: {url}");
            (
                CODE_EXTRACTION_ERROR,
                encode(&error_response("InvalidUrl", message, Some(context))),
            )
        }
    }
}

/// Runs the extraction on a thread with the configured stack size.
///
/// The thread borrows the request and the payload, so the HTML is never copied to satisfy a
/// `'static` bound. A panic in the worker is resumed here so its message survives to the panic
/// boundary, and a thread that cannot start is a panic too: running the extraction on the host's
/// stack is the outcome this design exists to avoid.
fn on_worker_thread(request: &Request, payload: &[u8]) -> Response {
    std::thread::scope(|scope| {
        let handle = std::thread::Builder::new()
            .stack_size(stack_bytes())
            .spawn_scoped(scope, || engine::build(request, payload))
            .expect("the extraction thread must start");
        match handle.join() {
            Ok(response) => response,
            Err(cause) => resume_unwind(cause),
        }
    })
}

/// The extraction stack size: `RUSTTRAFILATURA_STACK_BYTES` when it parses to a positive number,
/// otherwise [`DEFAULT_STACK_BYTES`].
fn stack_bytes() -> usize {
    *STACK_BYTES.get_or_init(|| {
        std::env::var(STACK_BYTES_ENV)
            .ok()
            .and_then(|value| value.trim().parse::<usize>().ok())
            .filter(|bytes| *bytes > 0)
            .unwrap_or(DEFAULT_STACK_BYTES)
    })
}

/// The header cap: `RUSTTRAFILATURA_MAX_HEADER_BYTES` when it parses to a number, otherwise
/// [`DEFAULT_MAX_HEADER_BYTES`]. A cap of `0` is disabled.
fn header_cap() -> usize {
    *MAX_HEADER_BYTES.get_or_init(|| cap_from_env(HEADER_CAP_ENV, DEFAULT_MAX_HEADER_BYTES))
}

/// The payload cap: `RUSTTRAFILATURA_MAX_PAYLOAD_BYTES` when it parses to a number, otherwise
/// [`DEFAULT_MAX_PAYLOAD_BYTES`]. A cap of `0` is disabled.
fn payload_cap() -> usize {
    *MAX_PAYLOAD_BYTES.get_or_init(|| cap_from_env(PAYLOAD_CAP_ENV, DEFAULT_MAX_PAYLOAD_BYTES))
}

/// Reads a cap from the environment.
fn cap_from_env(name: &str, default: usize) -> usize {
    cap_from_value(std::env::var(name).ok(), default)
}

/// Turns an environment value into a cap.
///
/// Unlike the stack size, `0` is a value with a meaning here, so it is kept; a value that is
/// absent, blank or not a `usize` keeps the default instead.
fn cap_from_value(value: Option<String>, default: usize) -> usize {
    value
        .and_then(|value| value.trim().parse::<usize>().ok())
        .unwrap_or(default)
}

/// Installs the panic hook that keeps a caught panic's message out of the host's stderr.
///
/// The default hook prints the panic message to stderr, and a library inside a .NET host must not
/// write there. This hook silences that message and nothing else: the panic still unwinds to the
/// boundary in [`guarded`], still becomes return code 3, and its message still reaches the caller
/// in the `Panic` envelope.
fn silence_panics() {
    PANIC_HOOK.get_or_init(|| {
        std::panic::set_hook(Box::new(|_| {}));
    });
}

/// Runs one export body behind the panic boundary.
///
/// The handler itself cannot panic: it runs after `catch_unwind` returned, and an unwind from
/// there would cross the C ABI and abort the host.
fn guarded(body: impl FnOnce() -> (i32, Vec<u8>)) -> (i32, Vec<u8>) {
    match catch_unwind(AssertUnwindSafe(body)) {
        Ok(result) => result,
        Err(payload) => {
            let message = panic_message(&*payload);
            let context = json!({ "message": message.clone() });
            let response = error_response("Panic", message, Some(context));
            (CODE_PANIC_CAUGHT, encode(&response))
        }
    }
}

/// The message of a caught panic, for the `Panic` envelope.
fn panic_message(payload: &(dyn std::any::Any + Send)) -> String {
    if let Some(message) = payload.downcast_ref::<&str>() {
        (*message).to_owned()
    } else if let Some(message) = payload.downcast_ref::<String>() {
        message.clone()
    } else {
        "panic without a message".to_owned()
    }
}

/// A failure envelope in the engine's shape: every content field and `metadata` stay null, so
/// success and failure share one object shape.
fn error_response(kind: &str, message: String, context: Option<Value>) -> Response {
    Response {
        schema_version: SCHEMA_VERSION,
        content_text: None,
        comments_text: None,
        content_html: None,
        comments_html: None,
        metadata: None,
        error: Some(ErrorEnvelope {
            kind: kind.to_owned(),
            message,
            context,
        }),
    }
}

/// Serializes a response into the buffer the caller frees.
///
/// The envelope is a struct of strings, numbers, arrays and options of those, so serialization
/// cannot fail; were it to, the empty buffer keeps the ABI total where a panic would abort the
/// host process.
fn encode<T: Serialize>(value: &T) -> Vec<u8> {
    serde_json::to_vec(value).unwrap_or_default()
}

/// Views a caller-supplied buffer.
///
/// A null pointer never reaches `from_raw_parts`: it is the empty slice, which is this ABI's
/// empty-input case. Rejecting a null pointer beside a non-zero length is [`check_input`]'s job,
/// before any buffer is viewed.
///
/// # Safety
///
/// `ptr` must point to `len` readable bytes, or be null. The returned slice borrows the caller's
/// allocation, so it must not outlive it.
unsafe fn bytes<'a>(ptr: *const u8, len: usize) -> &'a [u8] {
    if ptr.is_null() || len == 0 {
        return &[];
    }
    unsafe { std::slice::from_raw_parts(ptr, len) }
}

/// Hands a buffer to the caller as `(ptr, len, cap)`.
///
/// The capacity travels with the pointer because `rt_free` reconstructs the allocation from all
/// three values, so the `Vec` must not be shrunk or reallocated first. This is the only place a
/// buffer leaves the library, so it is also where a `test-hooks` build counts one as live.
fn write_out(out: *mut *mut u8, out_len: *mut usize, out_cap: *mut usize, bytes: Vec<u8>) {
    let mut bytes = std::mem::ManuallyDrop::new(bytes);
    unsafe {
        *out = bytes.as_mut_ptr();
        *out_len = bytes.len();
        *out_cap = bytes.capacity();
    }
    #[cfg(feature = "test-hooks")]
    LIVE_BUFFERS.fetch_add(1, Ordering::SeqCst);
}

#[cfg(test)]
mod tests {
    use super::*;

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
        assert!(check_input(std::ptr::null(), 8, unread_buffer(), 0, 1024, 1024).is_err());
        assert!(check_input(unread_buffer(), 0, std::ptr::null(), 8, 1024, 1024).is_err());
        // A null pointer with a zero length is the empty input, which reaches the core.
        assert!(check_input(std::ptr::null(), 0, std::ptr::null(), 0, 1024, 1024).is_ok());
    }

    #[test]
    fn check_input_accepts_a_buffer_exactly_at_its_cap() {
        assert!(check_input(unread_buffer(), 1024, unread_buffer(), 2048, 1024, 2048).is_ok());
    }

    #[test]
    fn check_input_rejects_a_buffer_above_its_cap() {
        let error = check_input(unread_buffer(), 1025, unread_buffer(), 1, 1024, 2048)
            .expect_err("1025 bytes is above a 1024-byte header cap");
        assert!(
            matches!(error, RequestError::InvalidRequest(reason) if reason.contains(HEADER_CAP_ENV))
        );
        let error = check_input(unread_buffer(), 1, unread_buffer(), 2049, 1024, 2048)
            .expect_err("2049 bytes is above a 2048-byte payload cap");
        assert!(
            matches!(error, RequestError::InvalidRequest(reason) if reason.contains(PAYLOAD_CAP_ENV))
        );
    }

    #[test]
    fn check_input_with_a_zero_cap_accepts_any_length() {
        assert!(check_input(
            unread_buffer(),
            usize::MAX,
            unread_buffer(),
            usize::MAX,
            0,
            0
        )
        .is_ok());
    }
}
