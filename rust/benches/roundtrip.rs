//! The round-trip bench: a direct core call against the `rt_extract` ABI.
//!
//! `harness = false`, so this is a plain `fn main` timed with `std::time::Instant`: a bench framework
//! would add a dependency to a crate whose point is a small dependency tree.
//!
//! Three paths, one fixture, one pinned option set:
//!
//! - `core`: `rust::extract(payload, &options)`, which is what `rt_extract` calls internally.
//! - `roundtrip`: `rt_extract` plus `rt_free` with the default `responseFields`
//!   (`contentText`, `commentsText`, `metadata`).
//! - `roundtripHtml`: the same round trip with `contentHtml` added, the opt-in D11 keeps off by
//!   default.
//!
//! `roundtrip/core` is the budget gate: the round trip must stay
//! within 1.5x the direct call. `roundtripHtml/core` is printed but not gated, because the direct
//! call never serializes the extracted tree: that ratio covers the requested `contentHtml` work as
//! well as the wrapper, so gating it would gate the option rather than the ABI.
//!
//! The paths are measured in rotated rounds rather than one after another. A machine's throughput
//! drifts during a run — this one measured about 10% between its first seconds and its steady state
//! — and three consecutive measurements would hand the last path an advantage of the same order as
//! the overhead being measured.

use rust::Options;
use rust_trafilatura_ffi::{rt_extract, rt_free};
use serde_json::Value;
use std::fmt::Write as _;
use std::time::Instant;

/// The fixture size: a fixed 200 KB fixture.
const FIXTURE_BYTES: usize = 200 * 1024;

/// The length [`fixture`] actually produces, which is the fixture size.
/// It depends on the paragraph templates, so the assertion in [`main`] fails the bench loudly when a
/// generator edit changes it, instead of silently invalidating the number this bench quotes.
const FIXTURE_LEN: usize = 206_687;

/// Timed iterations per path, as specified.
const ITERATIONS: usize = 200;

/// Timed rounds. Each round times every path for `ITERATIONS / ROUNDS` iterations with the order
/// rotated, so which path runs first, and which machine state each path meets, changes every round.
const ROUNDS: usize = 8;

/// Untimed warmup iterations per path. Without them the first call of each path would contribute the
/// extraction thread's spawn, the `OnceLock`s' initialization, the panic hook and the first parse of
/// the fixture to a mean of 200 samples.
const WARMUP: usize = 50;

/// One further untimed round per path, in a timed round's own shape and order. The first timed round
/// otherwise starts on a machine that is still ramping, and it always measures the core path first,
/// which undersells the gated ratio: the clock now starts only after the paths have played again.
const SETTLE_ITERATIONS: usize = ITERATIONS / ROUNDS;

/// The ratio budget the round trip must stay within.
const BUDGET: f64 = 1.5;

/// The paths in report order.
const PATHS: [&str; 3] = ["core", "roundtrip", "roundtripHtml"];

/// Path indices, so the report order above and the call [`run`] makes cannot drift apart.
const CORE: usize = 0;
const ROUNDTRIP: usize = 1;
const ROUNDTRIP_HTML: usize = 2;

/// The header for the default `responseFields`. `enableFallback: false` is the FAST path, and it is
/// also the core's own default: the header states it rather than
/// relying on it, so the pinned set is visible in the output.
const HEADER_DEFAULT: &[u8] = br#"{"options":{"enableFallback":false}}"#;

/// The header for the `contentHtml` round trip: the default fields plus the opt-in HTML field.
const HEADER_HTML: &[u8] =
    br#"{"options":{"enableFallback":false},"responseFields":["contentText","commentsText","contentHtml","metadata"]}"#;

const _: () = assert!(
    ITERATIONS.is_multiple_of(ROUNDS),
    "the timed rounds must divide the iteration count"
);

fn main() {
    let fixture = fixture();
    let payload = fixture.as_bytes();
    let options = Options::default();

    // A generator edit must fail here rather than quietly change the size this bench records.
    assert_eq!(
        payload.len(),
        FIXTURE_LEN,
        "the fixture generator changed: update FIXTURE_LEN and the fixture size in the spec"
    );

    let core = rust::extract(payload, &options).expect("the fixture must extract");
    let (default_response, default_bytes) = response(HEADER_DEFAULT, payload);
    let (html_response, html_bytes) = response(HEADER_HTML, payload);

    // The ratios only compare the same work if both round trips extracted the same document, and
    // the third path only measures serialization if it really carries a serialized tree.
    assert_eq!(
        default_response["contentText"].as_str(),
        Some(core.content_text.as_str()),
        "the default round trip must return the direct call's text"
    );
    assert_eq!(
        html_response["contentText"].as_str(),
        Some(core.content_text.as_str()),
        "the contentHtml round trip must return the direct call's text"
    );
    assert!(
        default_response["contentHtml"].is_null(),
        "the default responseFields must not compute contentHtml"
    );
    assert!(
        !html_response["contentHtml"]
            .as_str()
            .unwrap_or_default()
            .is_empty(),
        "the contentHtml round trip must return a serialized tree"
    );

    println!(
        "bench: fixture {} bytes, {ITERATIONS} iterations per path in {ROUNDS} rotated rounds of {}, after {WARMUP} warmup iterations per path and one untimed settling round",
        payload.len(),
        ITERATIONS / ROUNDS
    );
    println!("bench: profile bench (cargo bench), which inherits release and lto = \"thin\"");
    println!(
        "bench: options (a) core            Options::default(), enableFallback = false (FAST)"
    );
    println!(
        "bench: options (b) roundtrip       {} responseFields = contentText, commentsText, metadata (default)",
        text(HEADER_DEFAULT)
    );
    println!("bench: options (c) roundtripHtml   {}", text(HEADER_HTML));
    println!("bench: response bytes default {default_bytes}, contentHtml {html_bytes}");

    let means = measure(payload, &options);
    for (index, name) in PATHS.iter().enumerate() {
        println!("bench: {name:<14} mean {:.4} ms/call", means[index]);
    }

    let default_ratio = means[ROUNDTRIP] / means[CORE];
    let html_ratio = means[ROUNDTRIP_HTML] / means[CORE];
    println!("bench: ratio roundtrip/core     = {default_ratio:.3}");
    println!(
        "bench: ratio roundtripHtml/core = {html_ratio:.3} (informational: the direct call does no HTML serialization)"
    );

    if default_ratio > BUDGET {
        eprintln!("bench: FAIL roundtrip/core {default_ratio:.3} exceeds the {BUDGET:.2} budget");
        std::process::exit(1);
    }
    println!("bench: PASS roundtrip/core {default_ratio:.3} within the {BUDGET:.2} budget");
}

/// Warms every path, plays one more untimed round so the clock starts on a settled machine, then
/// times the paths in rotated rounds and returns the mean milliseconds per call in [`PATHS`] order.
/// A whole round's slot is timed rather than each call, so the sample includes no per-call clock
/// read; the drift a shorter sampling window would pick up is spread across the paths by the
/// rotation instead.
fn measure(payload: &[u8], options: &Options) -> [f64; 3] {
    for _ in 0..WARMUP {
        for path in 0..PATHS.len() {
            run(path, payload, options);
        }
    }
    for path in 0..PATHS.len() {
        for _ in 0..SETTLE_ITERATIONS {
            run(path, payload, options);
        }
    }

    let per_round = ITERATIONS / ROUNDS;
    let mut totals = [0.0f64; PATHS.len()];
    for round in 0..ROUNDS {
        for slot in 0..PATHS.len() {
            let path = (slot + round) % PATHS.len();
            let start = Instant::now();
            for _ in 0..per_round {
                run(path, payload, options);
            }
            totals[path] += start.elapsed().as_secs_f64();
        }
    }
    totals.map(|total| total * 1000.0 / ITERATIONS as f64)
}

/// One call on the path with the given index, asserting that it extracted.
fn run(path: usize, payload: &[u8], options: &Options) {
    match path {
        CORE => {
            rust::extract(payload, options).expect("the fixture must extract");
        }
        ROUNDTRIP => {
            let (code, _) = round_trip(HEADER_DEFAULT, payload);
            assert_eq!(code, 0, "the default round trip must extract");
        }
        ROUNDTRIP_HTML => {
            let (code, _) = round_trip(HEADER_HTML, payload);
            assert_eq!(code, 0, "the contentHtml round trip must extract");
        }
        other => unreachable!("unknown path index {other}"),
    }
}

/// Calls `rt_extract` and releases the buffer it handed out.
///
/// The release is inside the measured window on purpose: the round trip through the ABI is over
/// when the caller has freed the buffer the call handed out. The response is not copied, so the
/// measurement is the ABI itself and not this bench's own bookkeeping.
fn round_trip(header: &[u8], payload: &[u8]) -> (i32, usize) {
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
    // A null pointer is the rejected-out-parameter case, which `rt_free` tolerates.
    unsafe { rt_free(out, len, cap) };
    (code, len)
}

/// Runs one `rt_extract` call for the untimed verification pass and returns the parsed response
/// with the byte length of the buffer the ABI handed out.
fn response(header: &[u8], payload: &[u8]) -> (Value, usize) {
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
    assert!(
        !out.is_null(),
        "every return code writes a caller-owned buffer"
    );
    let bytes = unsafe { std::slice::from_raw_parts(out, len) }.to_vec();
    unsafe { rt_free(out, len, cap) };
    assert_eq!(code, 0, "the fixture must extract");
    let json = serde_json::from_slice(&bytes).expect("the response must be UTF-8 JSON");
    (json, len)
}

/// Reads a header back as text for the printed option set.
fn text(header: &[u8]) -> &str {
    std::str::from_utf8(header).expect("the pinned headers are UTF-8")
}

/// A deterministic fixture of about [`FIXTURE_BYTES`]: a head with metadata, navigation, an article
/// of distinct paragraphs, an image, a table and a comments block. Nothing here is random, so every
/// run and every machine measures the same bytes.
fn fixture() -> String {
    const SENTENCES: [&str; 4] = [
        "The council approved the measure after a lengthy debate, citing the need for clearer rules on how public data is published.",
        "Officials said the new framework will take effect next spring and that compliance will be reviewed annually.",
        "Critics argued the timeline is too tight for smaller publishers and asked for the reporting burden to scale with the size of the organisation.",
        "Supporters replied that a public report each year is the least that makes the framework checkable by anyone outside the room.",
    ];
    let mut html = String::with_capacity(FIXTURE_BYTES + 2048);
    html.push_str("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Round-trip fixture</title><meta name=\"author\" content=\"Ada Lovelace\"><meta name=\"description\" content=\"A deterministic fixture for the round-trip bench.\"><meta property=\"og:site_name\" content=\"Example Journal\"><meta property=\"article:published_time\" content=\"2026-10-08T00:00:00Z\"></head><body>");
    html.push_str(
        "<nav><a href=\"/\">Home</a><a href=\"/news\">News</a><a href=\"/about\">About</a></nav>",
    );
    html.push_str("<main><article><h1>Round-trip fixture</h1>");
    html.push_str("<figure><img src=\"/photo.jpg\" alt=\"A council chamber\"><figcaption>The council chamber.</figcaption></figure>");
    let mut paragraph = 0usize;
    while html.len() < FIXTURE_BYTES {
        let sentence = SENTENCES[paragraph % SENTENCES.len()];
        let next = SENTENCES[(paragraph + 1) % SENTENCES.len()];
        // The index keeps every paragraph distinct, so duplicate detection cannot collapse the
        // fixture into a shorter document than the size printed above.
        write!(html, "<p>Paragraph {paragraph}: {sentence} {next}</p>").unwrap();
        if paragraph.is_multiple_of(16) {
            html.push_str("<table><tr><th>Year</th><th>Reports</th></tr><tr><td>2026</td><td>12</td></tr><tr><td>2027</td><td>14</td></tr></table>");
        }
        paragraph += 1;
    }
    html.push_str("</article><div id=\"comments\">");
    for comment in 0..8 {
        write!(
            html,
            "<div class=\"comment\"><p>Comment {comment}: the annual review is a sensible compromise, and I would rather see the framework start next spring than wait for a version that never arrives.</p></div>"
        )
        .unwrap();
    }
    html.push_str("</div></main><footer><p>Copyright 2026 Example Journal. All rights reserved.</p></footer></body></html>");
    html
}
