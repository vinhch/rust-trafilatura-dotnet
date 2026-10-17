//! C ABI glue for the published `rust-trafilatura` crate.
//!
//! Modules:
//!
//! - `contract`: request header and response JSON types, plus validation.
//! - `engine`: core invocation and response assembly.
//!
//! The exported symbols (`rt_extract`, `rt_free`, `rt_version`, the worker-cap exports and the
//! `test-hooks`-gated test hooks) and the library's `unsafe` code belong in this file: pointer dereference, the `Vec`
//! reconstruction in `rt_free` and the `catch_unwind` boundary. A caller opens an `unsafe` block at
//! the call site, because an `unsafe extern "C"` function cannot be called from safe code. Each
//! export validates its out-parameters and the size of its inputs before reading or writing
//! anything, and runs its body behind a panic boundary, so a panic becomes return code 3 with a
//! `Panic` envelope instead of unwinding into the host process.
//!
//! A panic hook installed on the first export call keeps the default hook's stderr output out of
//! the host process, because a library inside a .NET host must not write there.

pub mod contract;
pub mod engine;

use crate::contract::{Request, RequestError, ResponseField, SCHEMA_VERSION};
use crate::engine::{ErrorEnvelope, Response};
use serde::Serialize;
use serde_json::{json, Value};
use std::panic::{catch_unwind, resume_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::mpsc;
use std::sync::OnceLock;
use std::time::{Duration, Instant};

/// The ABI version. Independent of both the crate version and [`SCHEMA_VERSION`].
const ABI_VERSION: u32 = 1;

/// Return codes, fixed by the C ABI contract.
const CODE_OK: i32 = 0;
const CODE_EXTRACTION_ERROR: i32 = 1;
const CODE_INVALID_REQUEST: i32 = 2;
const CODE_PANIC_CAUGHT: i32 = 3;

/// Return code of [`rt_set_worker_cap`]: the cap was already fixed, by an earlier set or by
/// the environment read at the first budgeted call.
const CODE_WORKER_CAP_ALREADY_FIXED: i32 = 1;

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

/// The process-wide allocator, on by default.
///
/// The published throughput comes from mimalloc, and the core crate declares it only in its own
/// binary and example, so a cdylib built from this crate would otherwise fall back to the system
/// allocator. `default-features = false` drops the feature with the dependency.
#[cfg(feature = "mimalloc")]
#[global_allocator]
static GLOBAL_ALLOCATOR: mimalloc::MiMalloc = mimalloc::MiMalloc;

/// Output buffers handed to the caller and not yet released, for the `test-hooks` allocation tests.
#[cfg(feature = "test-hooks")]
static LIVE_BUFFERS: AtomicUsize = AtomicUsize::new(0);

/// Environment variable holding the runaway-worker cap; each budgeted call runs on a detached
/// worker, and this bounds how many may exist at once.
const WORKER_CAP_ENV: &str = "RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS";

/// Runaway-worker cap used when the environment variable is absent or unusable.
const DEFAULT_MAX_RUNAWAY_WORKERS: usize = 16;

/// The runaway-worker cap, read once so it cannot change between calls.
static MAX_RUNAWAY_WORKERS: OnceLock<usize> = OnceLock::new();

/// Live detached workers for budgeted calls; reserved before a worker spawns and released in the
/// worker's drop guard, so a panic or a spawn failure also frees its slot.
static LIVE_RUNAWAY_WORKERS: AtomicUsize = AtomicUsize::new(0);

/// The `test-hooks` hold for budgeted workers, in milliseconds; see [`rt_hold_budgeted_worker_for_test`].
#[cfg(feature = "test-hooks")]
static BUDGETED_WORKER_HOLD_MS: AtomicUsize = AtomicUsize::new(0);

/// Extracts from a raw payload and returns the response JSON.
///
/// Return codes: `0` success, `1` extraction error (an `InvalidUrl` header, a core error, a
/// `TimeBudgetExceeded` deadline or a `WorkerCapExceeded` pool), `2` invalid request, `3` a caught
/// panic. Code `2` has two cases: a null `out`, `out_len` or `out_cap` writes nothing, while a
/// rejected request writes an `InvalidRequest` envelope. Every code that writes a buffer (`0`,
/// `1`, `3`, and `2` when the request was rejected) hands the caller a UTF-8 JSON buffer of
/// `*out_len` bytes that the caller owns and releases with [`rt_free`].
///
/// `header` is the UTF-8 JSON header (`schemaVersion`, `url`, `inputEncoding`, `timeBudgetMs`,
/// `options`, `responseFields`) and `payload` is raw HTML or gzip bytes, never base64. A null
/// `payload` with `payload_len == 0` is empty input and reaches the core.
///
/// `timeBudgetMs`, when set to 1..=2^32, bounds this call: it returns by the deadline plus
/// scheduler slack with the `TimeBudgetExceeded` error, while the extraction finishes on a
/// detached worker that keeps running to completion and frees everything it allocated. The pool of
/// those workers is capped by `RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS` (default 16, read once) or by
/// [`rt_set_worker_cap`], whichever fixes the cap first; a call that finds it full returns
/// `WorkerCapExceeded` without copying the payload or starting a thread. Without `timeBudgetMs` the call runs the extraction to completion on a scoped thread,
/// as before.
///
/// Both buffers are checked before either is read, and each check can reject the request on its
/// own: a null pointer beside a non-zero length, a header above 1 MiB, a payload above 64 MiB, or
/// a header the contract rejects. A buffer exactly at its cap is accepted, and the caps are
/// therefore enforced before the header is parsed and before anything is allocated in proportion
/// to the input. `RUSTTRAFILATURA_MAX_PAYLOAD_BYTES` and `RUSTTRAFILATURA_MAX_HEADER_BYTES`
/// override the caps; each is read once, so a value set after the first call has no effect, and
/// `0` disables the cap it names.
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

/// Sets the runaway-worker cap for later budgeted calls.
///
/// Return codes: `0` applied, `1` already fixed, `2` invalid. The cap is read once, so this and
/// the first budgeted call are two writers of one value: whoever fixes it first wins, and the
/// loser is told so by `1` rather than left running on a value it never set. A `0` is invalid,
/// matching the environment variable's positive-integer rule, and fixes nothing. The cap stays
/// for the process lifetime; there is no reset.
#[no_mangle]
pub extern "C" fn rt_set_worker_cap(cap: u32) -> i32 {
    if cap == 0 {
        return CODE_INVALID_REQUEST;
    }
    match MAX_RUNAWAY_WORKERS.set(cap as usize) {
        Ok(()) => CODE_OK,
        Err(_) => CODE_WORKER_CAP_ALREADY_FIXED,
    }
}

/// Reports the runaway-worker cap without fixing it.
///
/// The value is the one [`rt_set_worker_cap`] or the first budgeted call would fix:
/// `RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS` when it parses to a positive number, otherwise the
/// default 16. A value above `u32::MAX` saturates to it rather than wrapping.
#[no_mangle]
pub extern "C" fn rt_worker_cap() -> u32 {
    MAX_RUNAWAY_WORKERS
        .get()
        .copied()
        .unwrap_or_else(worker_cap_from_env)
        .min(u32::MAX as usize) as u32
}

/// Reports how many detached budgeted workers are live right now.
///
/// The count is advisory: a worker reserves its slot before it spawns and releases it when it
/// finishes, so the value can change as soon as it is read.
#[no_mangle]
pub extern "C" fn rt_live_runaway_workers() -> u32 {
    LIVE_RUNAWAY_WORKERS.load(Ordering::SeqCst) as u32
}

/// Panics on purpose, so the `test-hooks` suite can prove the panic boundary end to end.
///
/// The codes are [`rt_extract`]'s: `2` for a null out-parameter, which writes nothing, and `3` with
/// the `Panic` envelope. The symbol exists only in a build with the `test-hooks` feature, which must
/// be absent from every release artifact.
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

/// Holds every budgeted worker for `ms` milliseconds before its extraction, so a test can cross a
/// call's deadline deterministically.
///
/// The hold is process-global, so tests that set it must not run concurrently with each other. The
/// symbol exists only in a build with the `test-hooks` feature, which must be absent from every
/// release artifact.
#[cfg(feature = "test-hooks")]
#[no_mangle]
pub extern "C" fn rt_hold_budgeted_worker_for_test(ms: usize) {
    BUDGETED_WORKER_HOLD_MS.store(ms, Ordering::SeqCst);
}

/// The `rt_version` document.
///
/// The crate version comes from the manifest, so the reported value cannot drift from the package
/// version, and the core version is injected by `build.rs` from the lockfile, so it is the version of
/// the code this library links rather than the version the manifest asks for.
#[derive(Serialize)]
struct VersionJson {
    abi: u32,
    #[serde(rename = "schemaVersion")]
    schema_version: u32,
    #[serde(rename = "crate")]
    krate: &'static str,
    core: &'static str,
}

impl VersionJson {
    fn current() -> Self {
        Self {
            abi: ABI_VERSION,
            schema_version: SCHEMA_VERSION,
            krate: env!("CARGO_PKG_VERSION"),
            core: env!("RT_CORE_VERSION"),
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
    let payload = unsafe { bytes(payload, payload_len) };
    match request.header.time_budget_ms {
        Some(budget) => budgeted_extract(&request, payload, budget),
        None => {
            let response = on_worker_thread(&request, payload);
            let code = if response.error.is_some() {
                CODE_EXTRACTION_ERROR
            } else {
                CODE_OK
            };
            (code, encode(&response))
        }
    }
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

/// What a budgeted worker sent back: a finished response, or the payload of a panic it caught.
/// The response is boxed to keep the two variants near one another in size.
enum WorkerOutcome {
    Done(Box<Response>),
    Panicked(Box<dyn std::any::Any + Send>),
}

/// Runs the extraction on a detached worker and waits for it only until the deadline.
///
/// The order is fixed: reserve a runaway-worker slot (a full pool rejects with
/// `WorkerCapExceeded` and copies nothing), copy the payload to an owned buffer, clone the
/// request, then spawn. The worker owns everything it reads, because a detached thread cannot
/// borrow the caller's memory the way [`on_worker_thread`] does. The clock starts here, so the
/// payload copy is inside the budget.
///
/// On deadline the caller returns the `TimeBudgetExceeded` envelope and the worker keeps running
/// to completion in the background, freeing everything it allocated. The core has no
/// cancellation checkpoints, so the budget bounds the caller's wait, not the worker's work.
fn budgeted_extract(request: &Request, payload: &[u8], budget_ms: u64) -> (i32, Vec<u8>) {
    let fields = request.fields.clone();
    let cap = worker_cap();
    if LIVE_RUNAWAY_WORKERS
        .try_update(Ordering::SeqCst, Ordering::SeqCst, |live| {
            (live < cap).then_some(live + 1)
        })
        .is_err()
    {
        let live = LIVE_RUNAWAY_WORKERS.load(Ordering::SeqCst);
        let message = format!(
            "{live} detached workers are already live at the {cap}-worker cap in {WORKER_CAP_ENV}"
        );
        return response_pair(
            engine::failure_envelope(
                "WorkerCapExceeded",
                message,
                Some(json!({ "limit": cap })),
                &fields,
            ),
            CODE_EXTRACTION_ERROR,
        );
    }

    // The clock starts here: the payload copy below is inside the budget.
    let deadline = Instant::now() + Duration::from_millis(budget_ms);
    let owned_payload = payload.to_vec();
    let owned_request = request.clone();
    let (sender, receiver) = mpsc::channel();
    let spawn = std::thread::Builder::new()
        .stack_size(stack_bytes())
        .spawn(move || {
            // The slot is held from the reserve above until this guard drops: the worker
            // finishing, panicking or failing to send all release it.
            let _guard = RunawayWorkerGuard;
            let _sender = sender;
            #[cfg(feature = "test-hooks")]
            {
                let hold = BUDGETED_WORKER_HOLD_MS.load(Ordering::SeqCst);
                if hold > 0 {
                    std::thread::sleep(Duration::from_millis(hold as u64));
                }
            }
            let outcome = catch_unwind(AssertUnwindSafe(|| {
                engine::build(&owned_request, &owned_payload)
            }))
            .map(|response| WorkerOutcome::Done(Box::new(response)))
            .unwrap_or_else(WorkerOutcome::Panicked);
            // A send failure means the caller already timed out and dropped the receiver; the
            // outcome is dropped with the sender here.
            let _ = _sender.send(outcome);
        });
    if spawn.is_err() {
        // The reservation above is this thread's to undo: the closure that owns the guard never
        // started, so the slot is released here rather than by a guard.
        LIVE_RUNAWAY_WORKERS.fetch_sub(1, Ordering::SeqCst);
        resume_unwind(Box::new("the budgeted extraction thread must start"));
    }
    await_budgeted(receiver, deadline, budget_ms, &fields)
}

/// Waits for the budgeted worker until the deadline, then reports the worker's verdict or the
/// budget expiry.
fn await_budgeted(
    receiver: mpsc::Receiver<WorkerOutcome>,
    deadline: Instant,
    budget_ms: u64,
    fields: &[ResponseField],
) -> (i32, Vec<u8>) {
    match receiver.recv_timeout(deadline.saturating_duration_since(Instant::now())) {
        Ok(WorkerOutcome::Done(response)) => {
            let code = if response.error.is_some() {
                CODE_EXTRACTION_ERROR
            } else {
                CODE_OK
            };
            (code, encode(&response))
        }
        Ok(WorkerOutcome::Panicked(cause)) => resume_unwind(cause),
        Err(mpsc::RecvTimeoutError::Timeout) => response_pair(
            engine::failure_envelope(
                "TimeBudgetExceeded",
                format!("extraction exceeded the {budget_ms} ms time budget"),
                Some(json!({ "budgetMs": budget_ms })),
                fields,
            ),
            CODE_EXTRACTION_ERROR,
        ),
        // A disconnected channel means the worker died without a verdict, which is a panic
        // reaching the caller, never a budget expiry.
        Err(mpsc::RecvTimeoutError::Disconnected) => resume_unwind(Box::new(
            "the budgeted worker disconnected without a verdict",
        )),
    }
}

/// Releases the runaway-worker slot when the worker finishes, whether it completed, panicked or
/// failed to send.
struct RunawayWorkerGuard;

impl Drop for RunawayWorkerGuard {
    fn drop(&mut self) {
        LIVE_RUNAWAY_WORKERS.fetch_sub(1, Ordering::SeqCst);
    }
}

/// The runaway-worker cap: `RUSTTRAFILATURA_MAX_RUNAWAY_WORKERS` when it parses to a positive
/// number, otherwise [`DEFAULT_MAX_RUNAWAY_WORKERS`]. Like [`stack_bytes`], read once.
fn worker_cap() -> usize {
    *MAX_RUNAWAY_WORKERS.get_or_init(worker_cap_from_env)
}

/// The cap the environment names, or the default: the value that applies when nothing has
/// fixed the cap yet.
fn worker_cap_from_env() -> usize {
    std::env::var(WORKER_CAP_ENV)
        .ok()
        .and_then(|value| value.trim().parse::<usize>().ok())
        .filter(|cap| *cap > 0)
        .unwrap_or(DEFAULT_MAX_RUNAWAY_WORKERS)
}

/// Pairs a failure envelope with its return code.
fn response_pair(response: Response, code: i32) -> (i32, Vec<u8>) {
    (code, encode(&response))
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

    #[test]
    fn budget_expiry_returns_code_1_time_budget_exceeded() {
        let (sender, receiver) = mpsc::channel();
        let start = Instant::now();
        let (code, bytes) = guarded(|| {
            await_budgeted(
                receiver,
                start + Duration::from_millis(50),
                50,
                &[ResponseField::ContentText],
            )
        });
        drop(sender);
        assert!(
            start.elapsed() < Duration::from_secs(2),
            "the wait must honour the deadline within scheduler slack"
        );
        assert_eq!(code, CODE_EXTRACTION_ERROR);
        let value: Value = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(value["error"]["kind"], "TimeBudgetExceeded");
        assert_eq!(value["error"]["context"]["budgetMs"], 50);
        assert_eq!(value["contentText"], "");
        assert!(value["metadata"].is_null());
    }

    #[test]
    fn worker_panic_before_deadline_resumes_into_code_3() {
        let (sender, receiver) = mpsc::channel();
        sender
            .send(WorkerOutcome::Panicked(Box::new(
                "the worker panicked for the test",
            )))
            .unwrap();
        drop(sender);
        let (code, bytes) = guarded(|| {
            await_budgeted(
                receiver,
                Instant::now() + Duration::from_secs(60),
                60_000,
                &[ResponseField::ContentText],
            )
        });
        assert_eq!(code, CODE_PANIC_CAUGHT);
        let value: Value = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(value["error"]["kind"], "Panic");
        assert_eq!(
            value["error"]["context"]["message"],
            "the worker panicked for the test"
        );
    }

    #[test]
    fn worker_result_before_deadline_returns_code_0() {
        let (sender, receiver) = mpsc::channel();
        sender
            .send(WorkerOutcome::Done(Box::new(Response {
                schema_version: SCHEMA_VERSION,
                content_text: Some("extracted".to_owned()),
                comments_text: None,
                content_html: None,
                comments_html: None,
                metadata: None,
                error: None,
            })))
            .unwrap();
        drop(sender);
        let (code, bytes) = guarded(|| {
            await_budgeted(
                receiver,
                Instant::now() + Duration::from_secs(60),
                60_000,
                &[ResponseField::ContentText],
            )
        });
        assert_eq!(code, CODE_OK);
        let value: Value = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(value["contentText"], "extracted");
        assert!(value["error"].is_null());
    }

    #[test]
    fn disconnected_channel_is_a_panic_not_a_timeout() {
        let (sender, receiver) = mpsc::channel();
        drop(sender);
        let (code, bytes) = guarded(|| {
            await_budgeted(
                receiver,
                Instant::now() + Duration::from_millis(50),
                50,
                &[ResponseField::ContentText],
            )
        });
        assert_eq!(code, CODE_PANIC_CAUGHT);
        let value: Value = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(value["error"]["kind"], "Panic");
    }
}
