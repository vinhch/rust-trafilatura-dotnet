# Release evidence

What the package's seven success criteria (spec section 14 of
`docs/specs/2026-10-08-dotnet-bindings-design.md`) are worth at commit
`69066054d9589474b5de2a262c83076f3cff7f06` on branch `feat/dotnet-bindings`. Every command below ran
for this page, on the host and date the page records, at that commit; a number that could not be
re-measured says where it came from instead of being presented as fresh. A criterion this host cannot
run names the CI job that owns it and says plainly that nothing has verified it.

Corrections made after those runs, on the current branch head: the four native tests this page listed
as unverified, because `scripts/test.*` passed no feature flag at the time, now run in a second
`cargo test --locked --features test-hooks` pass; the per-asset managed test count moved from 62 to 63
with the test pass A added for a version read that fails; and the package listing in section 4 is
recorded from the `.nupkg` currently in `artifacts/` rather than from the pack those runs made. Those
corrections are marked where they appear; the rest of the page still describes the runs listed above.

## Host and date

| | |
| --- | --- |
| Host | Intel Core i7-9850H (6 cores, 12 threads), 128 GB RAM, Windows 11 Pro build 26300, x64 |
| Date | 2026-10-10 |
| Toolchains | rustc/cargo 1.98.1 (48a229cea 2026-09-01 / 797e8a9bc 2026-08-05), clippy 0.1.98, rustfmt 1.9.0-stable, .NET SDK 10.0.401 |
| Shell | `pwsh`. The cargo rows ran with the 1.98.1 toolchain directory prepended to `PATH` and `CARGO_TARGET_DIR=C:\cargo-target\rust-trafilatura-dotnet`; the core-checkout build used its own target directory so the two could not race |
| Staged native library | `win-x64` only, from the build `scripts/build.ps1 --check` accepts (artifact sha256 `3cc41915569c40680d05f18d95b48f0faa14c2a7e384226113e98f4c13d8921b`) |
| Core checkout | a fresh clone of the sibling repository at the pinned commit `d4a26ff6352640a36bd83f6e471583e90badb38e` (tag `v2.2.8`), under `%TEMP%\t14-core` |

## State at this commit

Verified on this host:

- Criterion 1, Windows half: `scripts/test.ps1` exits 0, the Rust suite passes 63 tests without the
  `test-hooks` feature and 67 with it, and `dotnet test` passes 63 tests per asset, on `net8.0` and on
  `net10.0`. The two Rust counts and the per-asset count were re-measured at the branch head; the
  section 1 transcript below is the run recorded above, whose per-asset count was 62.
- Criterion 2: the Rust parity test passes for all 11 inline fixtures against the core crate.
- Criterion 3: `cargo fmt --check` and `cargo clippy --locked --all-targets -- -D warnings` are clean,
  and the core-checkout build leaves that checkout's `git status --porcelain` empty.
- Criterion 4, `win-x64` half: the packed `.nupkg` carries the staged RID, the licence and notices
  files and both managed assets, asserted by `Package_Contains_AllRidsAndLicenses` on both assets. The
  entry listing in section 4 is the `.nupkg` in `artifacts/`.
- Criterion 5: both 1,000-call tests pass, one on each side.
- Criterion 6: the recorded P1 depth is reproduced at this commit and the stack policy matches it.
- Criterion 7: the freshly packed package installs from a local source into a scratch console app
  outside the repository and extracts an inline page.

Verified by CI only, and nothing has run it yet:

- Criterion 1's `ubuntu-latest` half — `ci.yml` job `test`, matrix leg `ubuntu-latest`.
- Criterion 4's five-RID package — `bindings.yml` job `package`, which needs all five `native` legs.
- The four native builds this host cannot do (`linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`) —
  `bindings.yml` job `native`.
- The `linux-x64` AOT leg — `ci.yml` job `Aot_Publish_Extracts_A_Page`, matrix leg `linux-x64`.

Not verified by anything:

- Probe P3 (the Linux artifacts' required `GLIBC_*` versions, spec section 13) — it needs the
  `linux-x64` artifact from `bindings.yml`'s `native` job and `readelf --version-info`. Owner: the
  plan's Task 12 P3 step, still unchecked.
- Both workflows' own execution: matrix expansion, `needs`/`if` wiring, the artifact API, the
  provenance attestation, the `nuget` environment's reviewer and Dependabot's behaviour. No workflow
  run is recorded in this checkout and none was read for this page.

The four native tests that no gate ran when this page was written are now executed: `scripts/test.ps1`
and `scripts/test.sh` run `cargo test --locked --features test-hooks` after the default pass, which
takes the Rust suite from 63 tests to 67. The three in `rust/tests/allocation.rs` and `panic_is_caught`
in `rust/tests/abi_hardening.rs` are behind that feature, and without the second pass
`rust/tests/allocation.rs`, gated whole, reports `running 0 tests`. The allocation counter's atomicity
test is one of the three, so `concurrent_extract_and_free_returns_the_counter_to_baseline` runs in the
default gate today; what keeps the counter honest on the shipped library is that
`scripts/release.*` refuses a staged artifact exporting either `test-hooks` symbol.

## 1. `dotnet test` green on `windows-latest` and `ubuntu-latest`

Command: `pwsh -File scripts/test.ps1`

Observed (exit 0):

```text
test.ps1: verifying the staged native library
build.ps1: checking win-x64 (x86_64-pc-windows-msvc)
build.ps1: stamp ok      D:\codes\rust-trafilatura-dotnet\dotnet\src\RustTrafilatura\runtimes\win-x64\native\build-stamp.txt
test.ps1: cargo fmt --check
test.ps1: cargo clippy --locked --all-targets -- -D warnings
test.ps1: cargo test --locked
test.ps1: cargo test --locked --features test-hooks
test.ps1: dotnet build -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
test.ps1: dotnet test -c Release (Package category excluded)
Passed!  - Failed:     0, Passed:    63, Skipped:     0, Total:    63, Duration: 3 s - RustTrafilatura.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    63, Skipped:     0, Total:    63, Duration: 3 s - RustTrafilatura.Tests.dll (net10.0)
test.ps1: Rust and C# suites passed
```

What the local run covers: both target frameworks, one test assembly each, 63 tests per asset with
the `Package` category excluded; the same command ran the 63-test Rust suite, the second
`--features test-hooks` pass over its 67 tests, and the staged-library stamp check first. The managed
assembly carries one further test, the `Category=Package` one, which the release path runs
(criterion 4). The per-asset count was 62 in the run this section transcribes and is 63 at the branch
head; the extra test is the version-read one pass A added, and the two Rust commands are those the
scripts run today.

Host and date: this host, 2026-10-10.

The Linux half is not runnable here. It is owned by `ci.yml`'s `test` job, whose matrix leg
`ubuntu-latest` runs `scripts/build.sh` then `scripts/test.sh` — the same sequence. Nothing has run
that job.

## 2. Rust parity test for every inline fixture, against the core crate

Command:
`cargo test --locked --manifest-path rust/Cargo.toml --lib bytes_path_matches_core_on_inline_fixtures`

Observed (exit 0):

```text
running 1 test
test engine::tests::bytes_path_matches_core_on_inline_fixtures ... ok

test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 46 filtered out; finished in 3.70s
```

The test is `rust/src/engine.rs:341`. It loops over `FIXTURES`, a `[&str; 11]` of inline documents
(`engine.rs:277`), and for each one compares the response the wrapper produced with
`rust::extract(html.as_bytes(), &Options::default())` — the core crate called directly, with core
defaults — failing with the fixture index in the message. Spec section 11 asks for at least 10.

The same test also passed inside the full suite of criterion 1:

```text
running 47 tests
test engine::tests::bytes_path_matches_core_on_inline_fixtures ... ok
test result: ok. 47 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out; finished in 13.21s
```

Host and date: this host, 2026-10-10.

## 3. Clippy and fmt clean, and the core checkout unchanged

Commands, all run from the repository root:

```console
cargo fmt --check --manifest-path rust/Cargo.toml
cargo clippy --locked --manifest-path rust/Cargo.toml --all-targets -- -D warnings
git clone D:/codes/rust-trafilatura %TEMP%\t14-core
git -C %TEMP%\t14-core checkout --detach d4a26ff6352640a36bd83f6e471583e90badb38e
cargo build --manifest-path rust/Cargo.toml --release --config "patch.crates-io.rust-trafilatura.path='<clone>'"
git -C %TEMP%\t14-core status --porcelain
```

Observed:

```text
### cargo fmt --check ###
fmt exit=0
### cargo clippy ###
    Finished `dev` profile [unoptimized + debuginfo] target(s) in 0.40s
clippy exit=0
```

The core half used `CARGO_TARGET_DIR=C:\cargo-target\t14-core-patch` so it could not race the suites'
build tree. The patched build replaced the published crate with the checkout, which its own output
names, and exited 0:

```text
   Compiling rust-trafilatura v2.2.8 (C:\Users\vica\AppData\Local\Temp\t14-core)
   Compiling rust-trafilatura-ffi v2.2.8 (D:\codes\rust-trafilatura-dotnet\rust)
    Finished `release` profile [optimized] target(s) in 3m 42s
cargo exit: 0
core porcelain: []
```

`status --porcelain` was empty before the build and empty after it, and the wrapper checkout was
clean after it too (`rust/Cargo.lock` unchanged, sha256
`6ea4e012dbf040156c74eabef9231cda11286d2673c0e9c919752a5aeff9aeef`). The published crate was not the
one compiled: `cargo` named the checkout as the package source, which is what the patch is for.

Host and date: this host, 2026-10-10.

The Linux side of this criterion is `ci.yml`'s `core-untouched` job on `ubuntu-latest`, which runs
the same `--config patch.crates-io...` build and asserts the same empty `git status --porcelain`. It
does not pass `--locked`, because cargo refuses to resolve a patched package under it. Nothing has
run that job.

## 4. The `.nupkg` contains all five RIDs, the licence and the notices

Command: `pwsh -File scripts/release.ps1 --allow-partial`

Observed (exit 0):

```text
release.ps1: warning: the native library is not staged for 4 of 5 RIDs: linux-x64 (...librust_trafilatura_ffi.so); linux-arm64 (...); osx-x64 (...dylib); osx-arm64 (...)
release.ps1: version   2.2.8
release.ps1: packed    1 of 5 RIDs: win-x64
release.ps1: package   D:\codes\rust-trafilatura-dotnet\artifacts\RustTrafilatura.2.2.8.nupkg (11835138 bytes)
release.ps1: checking  ...\RustTrafilatura.Tests.csproj (Package category)
Passed!  - Failed:     0, Passed:    1, Skipped:     0, Total:     1, Duration: 25 ms - RustTrafilatura.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    1, Skipped:     0, Total:     1, Duration: 26 ms - RustTrafilatura.Tests.dll (net10.0)
```

What the local package contains, byte for byte — every entry of the 12-entry archive, with its
uncompressed size and its sha256 prefix:

```text
    size  sha256[:16]        entry
     482  5A9880A593D931C5   _rels/.rels
     659  B3E2C57533FD7DBC   [Content_Types].xml
    1062  EF5EDCDC6D383839   LICENSE
    1637  FFF344FE706D376A   THIRD-PARTY-NOTICES.md
     786  BCE3076195C9C127   package/services/metadata/core-properties/nuget.psmdcp
     972  4C2135B828B0BC5B   RustTrafilatura.nuspec
   19168  D6FD85C2F719D296   README.md
   56229  88999BAD2ABB84D6   lib/net10.0/RustTrafilatura.xml
   57732  DA8D805BB7FFBC73   lib/net8.0/RustTrafilatura.xml
   66560  6DEBED9D9721C69A   lib/net8.0/RustTrafilatura.dll
   71680  9F2C6C3F945D071B   lib/net10.0/RustTrafilatura.dll
24911360  3CC41915569C4068   runtimes/win-x64/native/rust_trafilatura_ffi.dll
```

The settled entries are the ones worth citing: the source-controlled files (`LICENSE`,
`THIRD-PARTY-NOTICES.md`, `README.md`, the `.nuspec`) and the native library. The two managed
assembly digests are not stable across rebuilds of the same tree: they moved between two packs of
this tree while the native library's digest did not. Cite the sizes and the native digest; treat the
managed digests as a record of one pack rather than as a reproducible identity.

`LICENSE` is this repository's MIT text and `THIRD-PARTY-NOTICES.md` is its statement about the crate
this package binds. Both are this repository's own files, so neither has an upstream tag to compare
against.

Four of the five RID paths are absent because only `win-x64` is staged here. The native library in the
package is the staged file byte for byte:

```text
staged native library  3CC41915569C40680D05F18D95B48F0FAA14C2A7E384226113E98F4C13D8921B
packed native library  3CC41915569C40680D05F18D95B48F0FAA14C2A7E384226113E98F4C13D8921B
```

The assertion is `Package_Contains_AllRidsAndLicenses` (`dotnet/tests/RustTrafilatura.Tests/PackageTests.cs`).
It drives its RID list from the staging tree rather than from five literal paths, so it asserts the
RIDs that were actually staged — one here. `Package_Declares_License_As_Mit_Expression` in the same
file reads the packed `.nuspec` and asserts the licence element is an `expression` whose id is `MIT`.
The five-RID case is the `bindings.yml` `package` job, which downloads all five `native` artifacts
into `runtimes/` and runs `scripts/release.sh` with no flags; the script refuses to pack at all unless
all five RIDs, the README, the licence and the notices are present, and that job has not run.

Host and date: this host, 2026-10-10.

## 5. 1,000 concurrent calls produce results identical to sequential calls, no abort

Commands:

```console
cargo test --locked --manifest-path rust/Cargo.toml --test abi_hardening concurrent_calls_match_sequential
dotnet test dotnet/RustTrafilatura.sln -c Release --no-build --filter "FullyQualifiedName~Parallel_1000_Calls_AreConsistent"
```

Observed (both exit 0):

```text
running 1 test
test concurrent_calls_match_sequential ... ok

test result: ok. 1 passed; 0 failed; 0 ignored; 0 measured; 9 filtered out; finished in 5.29s

Passed!  - Failed:     0, Passed:    1, Skipped:     0, Total:     1, Duration: 977 ms - RustTrafilatura.Tests.dll (net8.0)
Passed!  - Failed:     0, Passed:    1, Skipped:     0, Total:     1, Duration: 1 s - RustTrafilatura.Tests.dll (net10.0)
```

`concurrent_calls_match_sequential` (`rust/tests/abi_hardening.rs:273`) compares 1,000 `rt_extract`
calls across 8 scoped threads against the sequential result; `Parallel_1000_Calls_AreConsistent`
(`dotnet/tests/RustTrafilatura.Tests/ErrorTests.cs:155`) compares 1,000 `Parallel.For` calls through
the real ABI against the sequential result field for field. Both passed in the full suites of
criterion 1 as well.

Host and date: this host, 2026-10-10.

## 6. Probe P1's number and the stack policy

Documentation check, plus a re-run because a depth probe is cheap enough to settle the record with a
measurement rather than a citation.

Command:

```console
cargo build --locked --manifest-path rust/Cargo.toml --release --example depth_probe
depth_probe.exe 128000
```

Observed (exit 0, 265.1 s wall clock, `RUSTTRAFILATURA_STACK_BYTES` unset):

```text
depth_probe: depth=128000 bytes=1408676 code=0 stack=RUSTTRAFILATURA_STACK_BYTES=unset (library default)
```

The recorded value is spec section 13's P1 row: measured 2026-10-10, the last depth that returned was
128,000 nested `<div>` levels, 1,408,676 bytes of HTML, 200 s with the shipped allocator against 243 s
without it, no abort anywhere in the swept range. This run reproduced the depth, the payload size and
`code=0` exactly, so the record is not stale and section 13 was left as Task 6 wrote it. It did not
reproduce the wall clock: 265.1 s against the recorded 200 s, measured while other .NET work ran on
the same machine.

The policy reflects the measurement:

- `rust/src/lib.rs:41` reads `RUSTTRAFILATURA_STACK_BYTES` and `rust/src/lib.rs:44` sets
  `DEFAULT_STACK_BYTES = 16 * 1024 * 1024`; the probe reports the variable as unset, so the run above
  used that 16 MiB default.
- Spec section 5's Threading row records the 16 MiB default and the override.
- Spec section 9's security row records that no abort was reachable inside the payload cap, that
  depth is therefore not what consumes the stack (32,000 levels still return on a 128 KiB worker
  stack), and that the quadratic time and the 64 MiB payload cap are the practical bounds.

Host and date: this host, 2026-10-10.

## 7. A scratch console app installs the package from a local source and extracts a page

Commands, all under `%TEMP%\t14-scratch` and with `NUGET_PACKAGES` pointed at a fresh directory under
`%TEMP%` so the extraction is of this page's package:

```console
dotnet new console
dotnet add package RustTrafilatura --version 2.2.8 --source D:\codes\rust-trafilatura-dotnet\artifacts
dotnet run -c Release
```

The scratch project carries a `nuget.config` whose only source is
`D:\codes\rust-trafilatura-dotnet\artifacts` — nuget.org is cleared, so the install cannot fall back
to the feed. Observed (exit 0):

```text
AbiVersion: {"abi":1,"schemaVersion":1,"crate":"2.2.8"}
Title:  Council approves the new library
Author: Ada Lovelace
Date:   2026-10-08
Chars:  584
Text:   Council approves the new library
The council approved the new central library on Tuesday, after a debate that ran past midnight and filled every seat in the chamber with residents who had waited four hours to speak.
The building will open in the spring of 2028 on the site of the old bus depot, and the architects said the reading rooms will face the river so that the afternoon light reaches the desks.
Critics of the plan said the cost had risen by a fifth since the first estimate, and asked the council to publish the revised budget before the contract is signed later this month.
```

The page is a string in `Program.cs`; the program uses the README's Usage example unchanged
(`ExtractionOptions.Url`, `ResponseFields = ["contentText", "metadata"]`), so that example compiles
and runs against the package as packed. The assets NuGet extracted into the fresh packages folder are
the packaged bytes:

```text
lib/net8.0/RustTrafilatura.dll                 A1CBF0F20CCA3FB7E9098E517F3405CE1AFB8976993CB7ABBA9BB66D28630486
lib/net10.0/RustTrafilatura.dll                27EEC1FCE0AC30DAF4C0349CC401BF2C612112B0F124241F9E35F4D1C175574D
runtimes/win-x64/native/rust_trafilatura_ffi.dll  3CC41915569C40680D05F18D95B48F0FAA14C2A7E384226113E98F4C13D8921B
```

One measurement note, because it decides whether a local install proves anything: the first attempt
restored the 2.2.8 package from the user's global packages folder, which still held the package an
earlier probe installed (`lib/net8.0/RustTrafilatura.dll`, 21,504 bytes, no `net10.0` asset, native
library `025456c46da06976...`). NuGet treats a version as immutable, so it served that older package —
the compile failed on a member the current surface has — until the restore ran with a private
`NUGET_PACKAGES` folder. Any local probe of a package whose version did not change must start from an
empty package folder or clear the cached entry, or it measures the previous package.

Host and date: this host, 2026-10-10.
