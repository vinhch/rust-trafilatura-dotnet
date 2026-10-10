//! Probe P1: the HTML nesting depth at which extraction aborts the process.
//!
//! It calls the exported `rt_extract` rather than the core directly. The shipped artifact runs the
//! extraction on a thread with an explicit stack size (`RUSTTRAFILATURA_STACK_BYTES`, default 16 MiB),
//! while a direct core call would run on the main thread and measure its ~1 MiB
//! stack: a threshold from that would say nothing about the library a .NET host loads.
//!
//! Usage: `depth_probe <depth>`. It prints one line and exits 0 when the call returns; a depth the
//! worker stack cannot survive aborts the process, so there is no output and the exit code is not 0.
//! Sweep it with a doubling search and a bisection.

use rust_trafilatura_ffi::{rt_extract, rt_free};

/// The environment variable the library reads its extraction stack size from.
const STACK_BYTES_ENV: &str = "RUSTTRAFILATURA_STACK_BYTES";

/// Requests only `contentText`: the probe measures stack depth, not the response shape.
const HEADER: &[u8] = br#"{"responseFields":["contentText"]}"#;

/// Enough body text for the core to keep the document, so a depth below the stack limit returns
/// success rather than the core's `OutputTooShort`.
const BODY: &str = "The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published. Officials said the new framework will take effect next spring and that compliance will be reviewed annually, with a public report published each year.";

fn main() {
    let Some(argument) = std::env::args().nth(1) else {
        fail("usage: depth_probe <depth>");
    };
    let Ok(depth) = argument.parse::<usize>() else {
        fail(&format!(
            "depth must be a non-negative integer, got {argument:?}"
        ));
    };

    let html = nested_html(depth);
    let (mut out, mut len, mut cap) = (std::ptr::null_mut(), 0usize, 0usize);
    let code = unsafe {
        rt_extract(
            HEADER.as_ptr(),
            HEADER.len(),
            html.as_ptr(),
            html.len(),
            &mut out,
            &mut len,
            &mut cap,
        )
    };
    unsafe { rt_free(out, len, cap) };

    println!(
        "depth_probe: depth={depth} bytes={} code={code} stack={}",
        html.len(),
        stack_setting()
    );
}

/// A document with `depth` nested `div` levels, the shape P1 measures. The paragraph sits inside
/// the nesting so the deep tree is the one the extractor has to walk.
fn nested_html(depth: usize) -> String {
    let mut html = String::with_capacity(depth * 11 + BODY.len() * 2 + 256);
    html.push_str("<html lang=\"en\"><head><title>Depth probe</title></head><body><article><h1>Depth probe</h1><p>");
    html.push_str(BODY);
    html.push_str("</p>");
    for _ in 0..depth {
        html.push_str("<div>");
    }
    html.push_str("<p>");
    html.push_str(BODY);
    html.push_str("</p>");
    for _ in 0..depth {
        html.push_str("</div>");
    }
    html.push_str("</article></body></html>");
    html
}

/// The stack size the library runs with, as far as this process can observe it: the environment
/// variable when it is set, otherwise the library's own default. The resolved value is private to
/// the crate, so this reports the input rather than guessing the outcome.
fn stack_setting() -> String {
    match std::env::var(STACK_BYTES_ENV) {
        Ok(value) => format!("{STACK_BYTES_ENV}={value}"),
        Err(_) => format!("{STACK_BYTES_ENV}=unset (library default)"),
    }
}

/// Reports a usage error and exits with code 2, the ABI's invalid-request code.
fn fail(message: &str) -> ! {
    eprintln!("depth_probe: {message}");
    std::process::exit(2);
}
