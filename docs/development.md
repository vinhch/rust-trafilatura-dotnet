# Development

How to build, test, benchmark and release-check this repository, and what CI runs. Releases are in
[releasing.md](releasing.md); the wire contract is in [architecture.md](architecture.md).

## Prerequisites

| Tool | Minimum | Needed by |
| --- | --- | --- |
| .NET SDK | 10.0.100 | everything under `dotnet/` |
| .NET 8 runtime | 8.0 | the `net8.0` tests |
| Rust toolchain | 1.98.1 | everything under `rust/` |
| `cargo-nextest` | 0.9.148 | `scripts/test.sh` |
| `cargo-mutants` | latest | the mutation runs in `.github/workflows/quality.yml` |
| `cargo-deny` | latest | the supply-chain job in `.github/workflows/quality.yml` |
| git | any | `scripts/corpus.sh`, which fetches the benchmark pages once |
| clang with the zlib headers, or the MSVC build tools | any | publishing the AOT consumer |

The SDK is pinned in `global.json`, which also opts the test projects into Microsoft.Testing.Platform.

## Scripts

Every task runs through `scripts/`. Each script comes as a `.sh` and `.ps1` pair with the same
arguments (the `.ps1` twins run on PowerShell 7); change both together. `--help` on any of
them prints its flags.

| Script | Does | Flags |
| --- | --- | --- |
| `scripts/build.sh` | Builds the native library for one RID and stages it, with a build stamp, under `dotnet/src/RustTrafilatura/runtimes/` | `--rid <rid>` (default: host RID), `--check` (verify the staged library against its stamp, build nothing) |
| `scripts/test.sh` | Verifies the staged library, then runs fmt, clippy, both nextest passes, the doc tests, the .NET build and one .NET test run per target framework | `--report` (JUnit files under `artifacts/test-results/`, the directory the CI report step publishes) |
| `scripts/bench.sh` | Runs `rust/benches/roundtrip.rs` and fails when the round-trip ratio against a direct core call exceeds its budget, then runs `dotnet/tools/AllocProbe/` | none |
| `scripts/corpus.sh` | Fetches the pinned benchmark corpus into the gitignored `dotnet/benchmarks/corpus/` | `--check` (verify instead of fetch) |
| `scripts/version.sh` | Keeps the crate version, the lockfile entry and the package version one number | `--set <version>` (refuses dirty files; neither commits nor tags), `--check [--tag v<version>]` |
| `scripts/release.sh` | Packs `artifacts/RustTrafilatura.<version>.nupkg` and runs the package tests against it | `--allow-partial` (pack only the staged RIDs), `--push-only` (publish the package already in `artifacts/`; used by `.github/workflows/publish.yml`) |

Typical order: `scripts/build.sh`, then `scripts/test.sh`. `scripts/release.sh` requires all five
RIDs to be staged unless given `--allow-partial`, and refuses a staged library that exports a
`test-hooks` symbol (the names are listed in `scripts/release.sh` and `scripts/release.ps1`).

## Tests

- Rust: `rust/tests/` holds the integration tests; `scripts/test.sh` runs them twice, the second time
  with the `test-hooks` feature, plus `cargo test --doc`.
- .NET: `dotnet/tests/RustTrafilatura.Tests/` is an xunit v3 executable on Microsoft.Testing.Platform
  (every `*.Tests` project, through `dotnet/Directory.Build.props`), so filters are xunit trait flags
  such as `--filter-not-trait "Category=Package"`, not the VSTest `--filter` expression.
- `Category=Package` tests read the packed `.nupkg`: `scripts/test.sh` leaves them out, a bare
  `dotnet test` includes them, and `scripts/release.sh` runs them after packing.
- A locally packed package whose version is already installed needs a fresh `NUGET_PACKAGES` folder
  or the cached entry cleared, because NuGet resolves by version.
- `dotnet/tests/RustTrafilatura.CompileFixture/` only has to compile: it pins which call sites the
  public byte overloads accept.
- `dotnet/tests/RustTrafilatura.AotConsumer/` is not in `dotnet/RustTrafilatura.sln`: CI publishes it
  with Native AOT and runs it, it is never tested and never packed.

## Benchmarks

- `scripts/bench.sh` measures a ratio, not throughput. The allocation and speed analysis is in
  [research/binding-performance.md](research/binding-performance.md).
- `dotnet/benchmarks/RustTrafilatura.Benchmarks/` is the BenchmarkDotNet project for the managed
  side: the three single-page entry points over pages across the corpus's size range, plus two
  whole-corpus benchmarks, each reporting time and managed bytes allocated. Run `scripts/corpus.sh`
  first, then `dotnet run --project dotnet/benchmarks/RustTrafilatura.Benchmarks -c Release -- --job short`
  against the staged native library. A run takes minutes, so `scripts/test.sh` and CI leave it out.

## CI

Actions are pinned to commit SHAs, and `.github/dependabot.yml` moves them, except the Rust toolchain
action, whose pin is a branch tip Dependabot cannot move.

| Workflow | Trigger | Runs |
| --- | --- | --- |
| `.github/workflows/ci.yml` | push and pull request to `main` | `.github/workflows/_ci.yml` |
| `.github/workflows/_ci.yml` | called by `ci.yml` and `release.yml` | `test` on Ubuntu and Windows: `scripts/version.sh --check`, `scripts/build.sh`, `scripts/test.sh --report`; `Aot_Publish_Extracts_A_Page` for `win-x64` and `linux-x64`: publishes the AOT consumer with IL2026 and IL3050 as errors and runs it |
| `.github/workflows/quality.yml` | pull requests touching `rust/`, and weekly | `core-untouched` (the core builds unchanged at its pinned commit), `mutants` (`cargo mutants`, in-diff on PRs), `bench` (`scripts/bench.sh`), `supply-chain` (`cargo deny check advisories licenses bans`) |
| `.github/workflows/release.yml` | a `v*` tag, or `workflow_dispatch` as a rehearsal | `guard` (`scripts/version.sh --check --tag`), `_ci.yml`, `native` (all five RIDs), `package` (`scripts/release.sh`); on a tag only, the attestation and `github-release` |
| `.github/workflows/publish.yml` | `workflow_dispatch` only, environment `nuget` | downloads a release's package, verifies its attestation and pushes it with `scripts/release.sh --push-only`; `dry_run` stops before the push |

A green `scripts/test.sh` does not cover the AOT publish, anything in `quality.yml`, the non-host
RID builds or the five-RID pack. A rehearsal dispatch of `release.yml` checks a release change
without creating a release.

## Native library staging

- `scripts/build.sh` copies the library into `dotnet/src/RustTrafilatura/runtimes/` under the RID
  and writes a build stamp beside it. The stamp digests the toolchain version and every file
  `source_paths` in `scripts/build.sh` lists: the `.rs` files under `rust/` outside `rust/tests/`,
  `rust/benches/` and the examples directory, plus
  `rust/Cargo.toml` and `rust/Cargo.lock`. Any change there, `scripts/version.sh --set` included,
  makes the stamp stale, and `scripts/test.sh` fails until `scripts/build.sh` runs again.
- The stamp logic in `scripts/build.sh` and `scripts/build.ps1` has to stay byte-identical.
- The `CopyNativeLibrary` target in `dotnet/Directory.Build.props` copies the host RID's staged files
  beside every test and benchmark assembly, which is how `[LibraryImport]` resolves. An unstaged RID
  is a build warning and a runtime `DllNotFoundException`. The item type it uses must not be named
  `NativeLibrary`: that is a Native AOT item type, and the linker then fails with LNK1107 on the stamp.

## Docs check

A phase of [phase-plan.md](phase-plan.md) closes only when this exits 0. Run from the repo root; it prints one line
per broken pointer or link. The first half checks every backticked path with `/` in `AGENTS.md`,
`docs/architecture.md` and this page (and the `::Name` after it); the second half checks relative
Markdown links in those pages and README.

```bash
{
for f in AGENTS.md docs/architecture.md docs/development.md; do
  [ -f "$f" ] && grep -oE '`[^` ]*/[^` ]*`' "$f" | tr -d '`'
done | sort -u | while IFS= read -r t; do
  case "$t" in
    *'*'*|.work/*|.superpowers/*|artifacts/*|dotnet/benchmarks/corpus/*|dotnet/src/RustTrafilatura/runtimes/*) continue ;;
  esac
  p=${t%%::*}; n=${t#*::}
  if [ ! -e "$p" ]; then echo "missing path: $t"
  elif [ "$n" != "$t" ] && ! grep -qF -- "$n" "$p"; then echo "missing name: $t"
  fi
done
for f in AGENTS.md README.md docs/architecture.md docs/development.md; do
  [ -f "$f" ] || continue; d=$(dirname "$f")
  grep -oE '\]\([^)]+\)' "$f" | sed -E 's/^\]\(//; s/\)$//; s/#.*//' | while IFS= read -r l; do
    case "$l" in ''|*://*|mailto:*) continue ;; esac
    [ -e "$d/$l" ] || echo "broken link in $f: $l"
  done
done
} | { ! grep .; }
```

Writing rules that keep it exact:

- Cite a tracked file or directory as one backtick span holding its full repo-relative path,
  optionally with `::Name` for a type, function, constant or test in it. Directories end in `/`. No
  bare file names, no line numbers.
- A backtick span with `/` is a repo path. External names with `/` (repositories, actions, URLs) are
  Markdown links or plain text.
- Name a heading in prose after the path; only Markdown links carry `#anchor`.
- A command with a flag stays in one span with its spaces, so the check skips it; so do globs.
- A grep in a table cell never contains `|`: write alternatives as `git grep -n -e A -e B`.
- Skipped as untracked or generated: `.work/`, `.superpowers/`, `artifacts/`,
  `dotnet/benchmarks/corpus/`, `dotnet/src/RustTrafilatura/runtimes/`.
- Root files (`AGENTS.md`, `README.md`) have no `/` and are not checked as pointers.
- On Windows `test -e` ignores case, so a wrong-case path passes locally and fails on Linux.
- A `::Name` matches as a substring anywhere in the file, so a short name (`::Read`) can pass after
  the symbol it meant is gone; prefer the most specific name.
