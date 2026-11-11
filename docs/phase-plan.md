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
| 3 | 3. Cancellation: `rt_cancel_new`, `rt_cancel`, `rt_cancel_free`, the `rt_extract` cancel parameter, `CancellationToken` on every extract method, `OperationCanceledException` | Not started | Full suites green; a cancel mid-extraction throws in under a quarter of the uncancelled run; a pre-cancelled token throws without a native call; a cancel between `rt_extract` returning and the registration dispose returns the normal result; the managed `SafeHandle` live count is zero after single and concurrent runs | 2 |
| 4 | 4. Release: CI (`quality.yml` core-untouched job removed), `deny.toml`, notices, Dependabot, README, `docs/` (release evidence, research pages), version 0.3.0, five RIDs packed and published | Not started | Tests green on Linux and Windows; package published; lapsed `.work/issue-*.md` drafts removed; release notes list the date format, `HtmlDateMode.Default`, the `max_nodes` rejection, empty input as `MissingDocument`, `Metadata.Language` always empty until crate OI-4, and extraction on a fixed 1 MiB worker thread on macOS (the caller's thread elsewhere); `cargo deny check` warns about three unused allowances in `rust/deny.toml` (lines 15, 17, 19), remove them | 2, 3 |

### Shipped phases

| Phase | Shipped in |
| --- | --- |
| 1. Removals on the old core: worker cap and time budget, `ExtractMany`, the dropped options and result fields, `InvalidUrl`; ABI and schema version 2 | `3d8960b..e004856` and the phase-closing commit |
| 2. Engine swap and error mapping on trafilatura-rust 0.2.0 from crates.io | `20645fc..c0b8b41` and the phase-closing commit |

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
| F-5 | Run the docs check from `docs/development.md` in CI on Ubuntu, so a broken pointer fails the pull request instead of relying on the closing agent, and a wrong-case path fails as it would on Linux. | Phase 2 |
| F-6 | README links into `docs/` are relative and break on the nuget.org package page; use absolute GitHub URLs for them. | Phase 4 |
