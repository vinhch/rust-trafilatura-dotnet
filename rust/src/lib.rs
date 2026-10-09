//! C ABI glue for the published `rust-trafilatura` crate.
//!
//! Modules:
//!
//! - `contract`: request header and response JSON types, plus validation.
//! - `engine`: core invocation and response assembly.
//!
//! The three exported symbols (`rt_extract`, `rt_free`, `rt_version`) and the crate's
//! only `unsafe` code belong in this file. Each export validates its out-parameters before
//! writing anything and runs its body behind a panic boundary, so a panic becomes return code 3
//! with a `Panic` envelope instead of unwinding into the host process.

pub mod contract;
pub mod engine;

use crate::contract::{Request, RequestError, SCHEMA_VERSION};
use crate::engine::{ErrorEnvelope, Response};
use serde::Serialize;
use serde_json::{json, Value};
use std::panic::{catch_unwind, resume_unwind, AssertUnwindSafe};
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

/// Extracts from a raw payload and returns the response JSON.
///
/// Return codes: `0` success, `1` extraction error (an `InvalidUrl` header or a core error), `2`
/// invalid request, `3` a caught panic. Code `2` has two cases: a null `out`, `out_len` or `out_cap`
/// writes nothing, while a malformed header writes an `InvalidRequest` envelope. Every code that
/// writes a buffer (`0`, `1`, `3`, and `2` when the header is malformed) hands the caller a UTF-8
/// JSON buffer of `*out_len` bytes that the caller owns and releases with [`rt_free`].
///
/// `header` is the UTF-8 JSON header (`schemaVersion`, `url`, `inputEncoding`, `options`,
/// `responseFields`) and `payload` is raw HTML or gzip bytes, never base64. A null `payload` with
/// `payload_len == 0` is empty input and reaches the core.
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
    if out.is_null() || out_len.is_null() || out_cap.is_null() {
        return CODE_INVALID_REQUEST;
    }
    let (code, bytes) = guarded(|| run_extract(header, header_len, payload, payload_len));
    write_out(out, out_len, out_cap, bytes);
    code
}

/// Releases a buffer returned by [`rt_extract`] or [`rt_version`].
///
/// A null `ptr` returns immediately, which is what makes a null `*out` safe to pass.
///
/// # Safety
///
/// `ptr` must be a buffer a previous call handed out, with the `len` and `cap` that call wrote,
/// and it must not have been released already: releasing a live pointer twice is undefined
/// behaviour.
#[no_mangle]
pub unsafe extern "C" fn rt_free(ptr: *mut u8, len: usize, cap: usize) {
    if ptr.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        drop(unsafe { Vec::from_raw_parts(ptr, len, cap) });
    }));
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
    if out.is_null() || out_len.is_null() || out_cap.is_null() {
        return CODE_INVALID_REQUEST;
    }
    let (code, bytes) = guarded(|| (CODE_OK, encode(&VersionJson::current())));
    write_out(out, out_len, out_cap, bytes);
    code
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
/// empty-input case. Rejecting a null pointer beside a non-zero length is left to the input caps.
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
/// three values, so the `Vec` must not be shrunk or reallocated first.
fn write_out(out: *mut *mut u8, out_len: *mut usize, out_cap: *mut usize, bytes: Vec<u8>) {
    let mut bytes = std::mem::ManuallyDrop::new(bytes);
    unsafe {
        *out = bytes.as_mut_ptr();
        *out_len = bytes.len();
        *out_cap = bytes.capacity();
    }
}
