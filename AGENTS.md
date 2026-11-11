# Agent guide

RustTrafilatura is a .NET binding for the Rust extraction core pinned in `rust/Cargo.toml`: the core
is linked into a native library per RID and called in process over a C ABI. Extraction behavior
belongs to the core; this repository owns the ABI, the JSON contract between the two sides and the
managed surface. A change is correct when the managed result equals what the core returns for the
same input and options, the contract stays versioned, and nothing escapes the boundary: a panic never
unwinds into the host (it becomes the panic return code with a `Panic` envelope) and nothing is
written to stderr. A migration to a new core is in progress; [docs/phase-plan.md](docs/phase-plan.md)
says what ships next.

## Documents and keeping them true

| Document | Read it when | Update it when |
| --- | --- | --- |
| [README.md](README.md) | for the public API, options, errors, platforms and limits, as a package user sees them | the public surface, an option, an error kind or a limit changes |
| [docs/architecture.md](docs/architecture.md) | before touching the ABI, the header, the response, errors, the version document, memory ownership or caps | any of those changes |
| [docs/development.md](docs/development.md) | before running or changing scripts, tests, benchmarks or CI, and to run the docs check | a script, flag, prerequisite, test convention or workflow changes |
| [docs/releasing.md](docs/releasing.md) | before setting a version, tagging or publishing | the release or publish flow changes |
| [docs/phase-plan.md](docs/phase-plan.md) | to find the next phase, open items (`OI-n`) and followups (`F-n`) | a phase ships or an item opens or closes |
| [docs/research/](docs/research/), [docs/release-evidence.md](docs/release-evidence.md) | for the reasoning behind past performance and release decisions | never for current state: both are dated records |

- A change that makes a line of this file, `docs/architecture.md` or `docs/development.md` false
  updates that line in the same commit. Values stay in the files these pages name; cite paths in the
  form `docs/development.md` (section "Docs check") defines.
- A phase closes only when the docs check in `docs/development.md` exits 0.
- `.work/` and `.superpowers/` are untracked working files (handoffs, designs, plans). A decision
  that outlives the work moves into README, `docs/` or this file.

## Layout

| Path | Holds |
| --- | --- |
| `rust/src/` | the native library: `rust/src/lib.rs` (exports, panic boundary, caps, all `unsafe`), `rust/src/contract.rs` (header types and validation, error kinds), `rust/src/engine.rs` (core call, response assembly) |
| `rust/tests/`, `rust/benches/`, `rust/examples/` | ABI integration tests, the round-trip bench, the stack-depth probe |
| `dotnet/src/RustTrafilatura/` | the package: the public types below, plus internal interop, contract check, header serialization and response and failure readers ([docs/architecture.md](docs/architecture.md) names each) |
| `dotnet/tests/RustTrafilatura.Tests/` | the .NET test suite |
| `dotnet/tests/RustTrafilatura.CompileFixture/` | compile-only: which call sites the public byte overloads accept |
| `dotnet/tests/RustTrafilatura.AotConsumer/` | published with Native AOT and run in CI; not in the solution |
| `dotnet/benchmarks/`, `dotnet/tools/AllocProbe/` | BenchmarkDotNet suite, managed allocation probe |
| `scripts/`, `.github/workflows/` | every build, test, bench and release task; CI ([docs/development.md](docs/development.md)) |

Public types, each in the file of its own name under `dotnet/src/RustTrafilatura/`, except where
the table names another:

| Type | Role |
| --- | --- |
| `Extractor` | the three extract entry points and `LibraryVersion` |
| `ExtractionOptions`; `ExtractionConfig`, `ExtractionFocus`, `HtmlDateMode` in `dotnet/src/RustTrafilatura/ExtractionOptions.cs` | per-call options, size thresholds and their enums |
| `ExtractionResult`; `Metadata` in `dotnet/src/RustTrafilatura/ExtractionResult.cs` | content text, comments text and metadata |
| `TrafilaturaException`; `TrafilaturaErrorKind` in `dotnet/src/RustTrafilatura/TrafilaturaException.cs` | one extraction's failure and its kind |
| `ContractVersionMismatchException` | the loaded library implements another ABI or schema |
| `NativeLibraryVersion` | what the loaded library reports about itself |

## Contract and boundary rules

- The ABI and schema versions bump together with their checks (row "ABI or schema bump" below).
- Unknown header keys are rejected at every level (`deny_unknown_fields` in `rust/src/contract.rs`);
  never loosen that.
- Every export checks its out-parameters and input sizes before reading or writing anything, and
  runs its body behind the panic boundary. A deliberate panic inside it is fine (the worker-thread
  `expect` in `rust/src/lib.rs::on_worker_thread`).
- The library does no I/O, writes nothing to stderr and never echoes the input HTML in an error.
- `rust/src/lib.rs::rt_free` gets the exact (pointer, length, capacity) triple a call handed out,
  once; the managed side frees in `finally`.
- `test-hooks` exports never reach a staged or packed library. `scripts/release.sh` and
  `scripts/release.ps1` refuse only the names in their own lists, so a new hook goes on both.
- Public members carry doc comments, except the wire-contract carriers, whose members the README
  documents. `dotnet/src/RustTrafilatura/RustTrafilatura.csproj` records that decision and suppresses
  CS1591 for it; never add per-property docs to a carrier.
- The managed surface stays trim and AOT clean, and the package declares no dependency.

## Change together

Owner is the source of truth; Copies is a command that finds every other place the fact sits (run it
from the repo root, replace `<old>` with the current value); the test fails when the two drift.

| Change | Owner | Copies | Pinning test |
| --- | --- | --- | --- |
| New option | `rust/src/contract.rs::OptionsJson` (size thresholds: `rust/src/contract.rs::ConfigJson`), mapped in `rust/src/engine.rs::options_from` / `rust/src/engine.rs::config_sizes_from` | `git grep -n -i -e excludeTables` (an existing option) shows each list: `dotnet/src/RustTrafilatura/ExtractionOptions.cs`, the README options table | `dotnet/tests/RustTrafilatura.Tests/OptionsTests.cs::Header_Json_Has_The_Wire_Shape` |
| Enum spelling | `rust/src/contract.rs::FocusJson`, `rust/src/contract.rs::HtmlDateModeJson` | `git grep -n -e favorRecall`: the `#if NET9_0_OR_GREATER` attributes in `dotnet/src/RustTrafilatura/ExtractionOptions.cs`, the net8 converters in `dotnet/src/RustTrafilatura/HeaderContext.cs` | `dotnet/tests/RustTrafilatura.Tests/OptionsTests.cs::Enum_Options_Use_The_Core_Spellings`, `rust/src/contract.rs::focus_and_html_date_mode_reject_unknown_spellings` |
| New top-level header key | `rust/src/contract.rs::Header` | `dotnet/src/RustTrafilatura/HeaderContext.cs::RequestHeader`, `dotnet/src/RustTrafilatura/Request.cs::Header`; a `[JsonIgnore]` member on `ExtractionOptions` only when callers set the key through options, never for a value an entry point supplies | `dotnet/tests/RustTrafilatura.Tests/OptionsTests.cs::Header_Keys_Are_Unknown_Inside_Options` |
| Response field | `rust/src/contract.rs::ResponseField` (with `DEFAULT_RESPONSE_FIELDS`), `rust/src/engine.rs::Response`, `rust/src/engine.rs::MetadataJson` | `git grep -n -e commentsText`: `dotnet/src/RustTrafilatura/Response.cs`, `dotnet/src/RustTrafilatura/ExtractionResult.cs`, the `ResponseFields` doc in `dotnet/src/RustTrafilatura/ExtractionOptions.cs`, the failure-envelope key lists in `rust/tests/`, the README | `dotnet/tests/RustTrafilatura.Tests/ResponseTests.cs::Every_Metadata_Member_Reads_Its_Own_Key` |
| Error kind | `rust/src/contract.rs::error_kind` (core errors), `rust/src/lib.rs::request_error` and `rust/src/lib.rs::error_response` (invalid request, panic) | `git grep -n -e MissingMetadata`: `dotnet/src/RustTrafilatura/TrafilaturaException.cs::TrafilaturaErrorKind`, `dotnet/src/RustTrafilatura/Failures.cs::ReadKind`, the README error table | `dotnet/tests/RustTrafilatura.Tests/ErrorTests.cs::Error_Kind_Enum_Matches_The_Contract`, `rust/src/contract.rs::error_kind_covers_every_core_variant` |
| New or changed export | the export in `rust/src/lib.rs` and the export list in its module doc | `git grep -n -e rt_version` (an existing export): the `LibraryImport` in `dotnet/src/RustTrafilatura/Interop.cs`, `docs/architecture.md`; a new test hook also goes on the forbidden lists in `scripts/release.sh` and `scripts/release.ps1`; a public signature change: see "Tests" below | `rust/tests/abi.rs::null_out_params_are_rejected_without_writing` (null out-parameters per export), `rust/tests/abi_hardening.rs` (null buffers, `rt_free`, the panic hook) |
| ABI or schema bump | `rust/src/lib.rs::ABI_VERSION`, `rust/src/contract.rs::SCHEMA_VERSION` | `git grep -n -i -E -e 'abi.{0,10}\b<old>\b' -e 'schema.?version.{0,20}\b<old>\b' -e 'Equal\(<old>, .*abi' -e 'Equal\(<old>, .*schema'`: `dotnet/src/RustTrafilatura/LibraryContract.cs`, `dotnet/src/RustTrafilatura/HeaderContext.cs`, doc comments and test literals; plus the README "Compatibility and Limitations" sentence | `dotnet/tests/RustTrafilatura.Tests/AbiContractTests.cs::RealLibrary_Passes_The_Contract_Check` |
| Input cap or env var | the cap constants and env names in `rust/src/lib.rs`, and the `rust/src/lib.rs::rt_extract` doc | `git grep -n -e RUSTTRAFILATURA_ -e MiB -e '1024 \* 1024'`: `rust/tests/abi_hardening.rs`, `rust/tests/caps.rs`, `rust/examples/depth_probe.rs`, the README | `rust/tests/caps.rs` |
| Package version | `rust/Cargo.toml`, `dotnet/src/RustTrafilatura/RustTrafilatura.csproj`, `rust/Cargo.lock` | change only through `scripts/version.sh --set`, which writes all three | `scripts/version.sh --check` (CI and the release guard) |
| Core dependency bump | the exact pin in `rust/Cargo.toml` | `git grep -n -E -e 'trafilatura[-_]rust'`: the lockfile readers `rust/build.rs` and `dotnet/tests/RustTrafilatura.Tests/Repository.cs::CoreVersion`, `use` paths, docs; the pinned core commit in `.github/workflows/quality.yml`; licenses in `rust/deny.toml` and `THIRD-PARTY-NOTICES.md` (run `cargo deny check advisories licenses bans` in `rust/`); a new package version; re-check the option and error kind rows | `dotnet/tests/RustTrafilatura.Tests/ExtractionTests.cs::LibraryVersion_Matches_TheCrateAndCoreItWasBuiltFrom`, `rust/src/contract.rs::error_kind_covers_every_core_variant` |
| Toolchain or SDK bump | Rust: `rust-version` in `rust/Cargo.toml` and the commit SHA every Rust toolchain action step pins (the version comment follows the SHA; Dependabot cannot move it); nextest: the `cargo-nextest@` pin in `.github/workflows/_ci.yml` and `.github/workflows/quality.yml`; .NET: `global.json` | `git grep -n -e '<old version>' -e '<old SHA>'`: `.github/workflows/`, `docs/development.md` prerequisites | CI; the rustc version also feeds the build stamp |

## Tests

- Rust integration tests live in `rust/tests/`, unit tests beside the code. A test that needs the
  `test-hooks` feature is gated per file (`#![cfg(feature = "test-hooks")]`, as
  `rust/tests/allocation.rs`) or per item (`#[cfg(feature = "test-hooks")]` in a mixed file, as
  `rust/tests/abi_hardening.rs`); never add a file-level gate to a mixed file.
- .NET tests live in `dotnet/tests/RustTrafilatura.Tests/`: xunit v3 on Microsoft.Testing.Platform,
  names in `Pascal_Snake_Case` stating the behavior, inline HTML in
  `dotnet/tests/RustTrafilatura.Tests/Fixtures.cs`.
- A public signature change also compiles in `dotnet/tests/RustTrafilatura.CompileFixture/` and runs
  in `dotnet/tests/RustTrafilatura.AotConsumer/`.

## Commands and CI

- Every task runs through `scripts/`; the table, flags and prerequisites are in
  [docs/development.md](docs/development.md). Usual loop: `scripts/build.sh`, then `scripts/test.sh`.
- `.sh` and `.ps1` twins take the same arguments and change together; the build-stamp logic in
  `scripts/build.sh` and `scripts/build.ps1` stays byte-identical.
- Any change to the stamp inputs (`source_paths` in `scripts/build.sh`: Rust sources outside
  `rust/tests/`, `rust/benches/` and `rust/examples/`, `rust/Cargo.toml`, `rust/Cargo.lock`, the
  toolchain), `scripts/version.sh --set` included, makes
  `scripts/test.sh` fail until `scripts/build.sh` runs again.
- The staged-library item in `dotnet/Directory.Build.props` must not use the item type
  `NativeLibrary`: Native AOT hands those to the linker, which fails with LNK1107.
- A green `scripts/test.sh` does not prove: the AOT publish with IL2026 and IL3050 as errors
  (`.github/workflows/_ci.yml`); `cargo deny`, mutants, the bench and the core-untouched build
  (`.github/workflows/quality.yml`, on pull requests touching `rust/` and weekly); the non-host RID
  builds and the five-RID pack (`.github/workflows/release.yml`). A `workflow_dispatch` rehearsal runs
  `.github/workflows/release.yml` except the attestation and the GitHub Release; it does not run
  `.github/workflows/quality.yml`.
- Never push a tag or run `.github/workflows/publish.yml`: a tag creates a public GitHub Release, and
  both are the user's call.

## Commits

Conventional commits (`feat:`, `fix:`, `refactor:`, `docs:`, `test:`, `ci:`, `chore:`), scope
optional, `!` for a breaking change. English for code, comments, docs and commit messages.
