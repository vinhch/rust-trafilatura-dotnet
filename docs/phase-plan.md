# Phase plan

This plan is the durable record of what the binding ships next and the items still open. It holds current state only; history is in git.

How the plan is kept:

- A phase runs its own brainstorm and design gate before code. Design specs and implementation plans are working files under `.superpowers/` (not tracked); once a design is approved, the decisions that outlive the phase move into the README, the pages under `docs/` and `AGENTS.md`.
- A phase closes only when the docs check in `docs/development.md` exits 0.
- A row moves from Remaining to Shipped when its done-when criteria hold, citing the commit range or tag.
- A register row leaves the register when it closes. Ids are never reused: `OI-n` for open items, `F-n` for followups.
- A citation of this plan names an id or a heading, never a line.

## Roadmap

Migration to `trafilatura-rust` (design: `.superpowers/specs/2026-11-10-trafilatura-rust-migration-design.md`; source handoff: `.work/trafilatura-rust-migration-handoff.md`). The package id `RustTrafilatura` stays; the release is breaking.

### Remaining phases

Phase numbers are ids, not positions: the Order column gives the sequence, and Blocked by names phase ids or register ids.

| Order | Phase | Status | Done when | Blocked by |
| --- | --- | --- | --- | --- |
| 4 | 4. Release: CI (`quality.yml` core-untouched job removed), `deny.toml`, notices, Dependabot, README, `docs/` (release evidence, research pages), version 0.3.0, five RIDs packed and published | Not started | Tests green on Linux and Windows; package published; lapsed `.work/issue-*.md` drafts removed; release notes list the date format, `HtmlDateMode.Default`, the `max_nodes` rejection, empty input as `MissingDocument`, `Metadata.Language` always empty until crate OI-4, an oversized payload as `InputTooLarge` (code 1) instead of `InvalidRequest` (code 2), `ExtractFromHtml` skipping the binary check, decompression and charset detection so a string starting with `%PDF-` extracts instead of failing, and extraction on a fixed 1 MiB worker thread on macOS (the caller's thread elsewhere); README states that extraction on a host other than `dotnet.exe` or an apphost needs a 1 MiB caller thread stack on Windows and Linux, and that IIS `w3wp.exe` hosting is unchecked; `cargo deny check` warns about three unused allowances in `rust/deny.toml` (lines 15, 17, 19), remove them | 2 |

### Shipped phases

| Phase | Shipped in |
| --- | --- |
| 1. Removals on the old core: worker cap and time budget, `ExtractMany`, the dropped options and result fields, `InvalidUrl`; ABI and schema version 2 | `3d8960b..e004856` and the phase-closing commit |
| 2. Engine swap and error mapping on trafilatura-rust 0.2.0 from crates.io | `b0eb0e4..c0b8b41` and the phase-closing commit |
| 3. Cancellation: `rt_cancel_new`, `rt_cancel`, `rt_cancel_free`, the `rt_extract` cancel parameter, `CancellationToken` on every extract method, `OperationCanceledException` | `74c968d..7abf8fb` and the phase-closing commit |

## Open items register

| Id | Item | Needed by |
| --- | --- | --- |
| OI-3 | Consumer change in `content-search-connectors` (handoff section 8): thread the engine token through, drop the cap code and hook, re-run the quality sample before `Extraction:Engine` is set to `Trafilatura`. Runs in that repo. | After phase 4 |

## Followups

Work worth doing after the phases it depends on, not scheduled yet.

| Id | Followup | After |
| --- | --- | --- |
| F-1 | Cache one `Extractor` per distinct options value if the bench shows the per-call `Options::build` cost matters. | Phase 2 |
| F-2 | Re-add a batch method as a managed wrapper over `ExtractFromBytes` if a consumer asks; no native batch (the crate's `extract_batch` spawns its own threads). | Phase 4 |
| F-3 | `Metadata.Language` and `WrongLanguage`: reachable once the crate closes its OI-4 (language detection). | trafilatura-rust OI-4 |
| F-4 | Comments HTML: the crate records it as its F-80. | trafilatura-rust F-80 |
| F-6 | README links into `docs/` are relative and break on the nuget.org package page; use absolute GitHub URLs for them. | Phase 4 |
| F-7 | `DuplicateBody` is unreachable from `bare_extraction`: duplicate detection needs a `DedupCache` the binding does not pass. | A consumer asks for cross-call dedup |
| F-8 | The macOS worker selected by `extraction_stack()` runs on no CI host. Add a macOS job, or a test that runs the selected stack on every OS. | Phase 4 |
| F-9 | On .NET 10 macOS the worker is not needed (1.5 MiB default). A check on the .NET version avoids one thread spawn per call there; measure the cost before adding it. | Phase 4 |
| F-11 | The macOS .NET 8 stack value (512 KiB) comes from the libpthread main branch, not a release build. Confirm it before the phase 4 README states it. | Phase 4 |
| F-12 | The stack claims and the `libc.so.6` import cover glibc only; musl is not a packed RID. Revisit when a musl RID is packed. | A musl RID is packed |
| F-13 | ABI and schema stay at 2 although the `rt_extract` signature changed in phase 3, and the phase 3 design keeps both at 2 for the release. Decide the bump before the release; a stale ABI-2 library passes the contract check. | Phase 4 |
| F-14 | The null-handle branch of `CancelHandle.Create()` has no test: it needs the native `rt_cancel_new` to fail. Add the test once the native side can be forced to fail. | No trigger yet |
| F-15 | The cross-thread native cancel test and the managed latency test depend on timing. The latency floor is 200 ms; the development host measured 384 ms. The first GitHub CI run passed both on ubuntu-latest and windows-latest; keep watching them on later runs. The cross-thread test takes 8 to 16 s in debug. | Phase 4 (CI) |
| F-17 | `Failures.Read` throws `InvalidOperationException` when the response root is a JSON array. Make it a clear error for a malformed response. Predates phase 3. | No trigger yet |
| F-18 | `scripts/docs-check.ps1` walks up from each `git.exe` to the first `bin\bash.exe`, which can find an unrelated `bash.exe` above a shim install. Bound the search to the install's own `bin`. | No trigger yet |
| F-19 | The Windows AOT publish fails from a Bash shell with MSB3073 (vswhere) unless the Visual Studio Installer folder is on PATH. Note the prerequisite in `docs/development.md`. | Phase 4 |
