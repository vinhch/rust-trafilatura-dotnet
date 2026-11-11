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
| 5 | 5. Hardening: F-14, F-17, F-18 | Not started | Each of F-14, F-17 and F-18 has its test and the docs check passes | None |
| 6 | 6. Performance: F-1, F-9 | Not started | The bench decides each of F-1 and F-9 and the numbers are recorded | None |

### Shipped phases

| Phase | Shipped in |
| --- | --- |
| 1. Removals on the old core: worker cap and time budget, `ExtractMany`, the dropped options and result fields, `InvalidUrl`; ABI and schema version 2 | `3d8960b..e004856` and the phase-closing commit |
| 2. Engine swap and error mapping on trafilatura-rust 0.2.0 from crates.io | `b0eb0e4..c0b8b41` and the phase-closing commit |
| 3. Cancellation: `rt_cancel_new`, `rt_cancel`, `rt_cancel_free`, the `rt_extract` cancel parameter, `CancellationToken` on every extract method, `OperationCanceledException` | `74c968d..7abf8fb` and the phase-closing commit |
| 4. Release: CI, `deny.toml`, notices, Dependabot, README, `docs/` (release evidence, research pages), version 0.3.0, five RIDs packed and published | `698cfbf..9e346e5`, tag `v0.3.0` and the phase-closing commit |

## Open items register

| Id | Item | Needed by |
| --- | --- | --- |
| OI-3 | Consumer change in `content-search-connectors` (handoff section 8): thread the engine token through, drop the cap code and hook, re-run the quality sample before `Extraction:Engine` is set to `Trafilatura`. Runs in that repo. | After phase 4 |

## Followups

Work worth doing after the phases it depends on, not scheduled yet.

| Id | Followup | After |
| --- | --- | --- |
| F-1 | Cache one `Extractor` per distinct options value if the bench shows the per-call `Options::build` cost matters. | Phase 6 |
| F-2 | Re-add a batch method as a managed wrapper over `ExtractFromBytes` if a consumer asks; no native batch (the crate's `extract_batch` spawns its own threads). | A consumer asks |
| F-3 | `Metadata.Language` and `WrongLanguage`: reachable once the crate closes its OI-4 (language detection). | trafilatura-rust OI-4 |
| F-4 | Comments HTML: the crate records it as its F-80. | trafilatura-rust F-80 |
| F-7 | `DuplicateBody` is unreachable from `bare_extraction`: duplicate detection needs a `DedupCache` the binding does not pass. | A consumer asks for cross-call dedup |
| F-9 | On .NET 10 macOS the worker is not needed (1.5 MiB default). A check on the .NET version avoids one thread spawn per call there; measure the cost before adding it. | Phase 6 |
| F-12 | The stack claims and the `libc.so.6` import cover glibc only; musl is not a packed RID. Revisit when a musl RID is packed. | A musl RID is packed |
| F-14 | The null-handle branch of `CancelHandle.Create()` has no test: it needs the native `rt_cancel_new` to fail. Add the test once the native side can be forced to fail. | Phase 5 |
| F-17 | `Failures.Read` throws `InvalidOperationException` when the response root is a JSON array. Make it a clear error for a malformed response. Predates phase 3. | Phase 5 |
| F-18 | `scripts/docs-check.ps1` walks up from each `git.exe` to the first `bin\bash.exe`, which can find an unrelated `bash.exe` above a shim install. Bound the search to the install's own `bin`. | Phase 5 |
| F-20 | `BlacklistedUrl` is declared but never raised: the wrapper never sets the core's `url_blacklist`. Add an `ExtractionOptions` member for it if a consumer needs it, or drop the kind from the table. | A consumer asks |
