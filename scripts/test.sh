#!/usr/bin/env bash
#
# Run the Rust and C# suites with the same commands CI calls, so a developer and a workflow run one
# thing. Fails on the first failing command, and refuses to run at all when the staged native library
# does not correspond to the current rust/ sources.
set -euo pipefail

readonly script_name="test.sh"

usage() {
  cat <<EOF
Usage: scripts/$script_name

Runs every command CI runs: cargo fmt --check, cargo clippy --locked --all-targets -- -D warnings,
cargo nextest run --locked and a second cargo nextest run --locked --features test-hooks in rust/,
a cargo test --locked --doc for the doc tests nextest does not run, then dotnet build -c Release and
one dotnet test -c Release per target framework in dotnet/.
Every test command writes a JUnit report under artifacts/test-results/, which is what the report step
of the CI workflow publishes; the directory is gitignored and holding the reports is its only purpose.
The staged native library is verified against its build stamp first, so a stale or placeholder
artifact fails the run instead of passing against nothing.

Tests in the Package category are excluded: they read the packed .nupkg, which only
scripts/release.sh produces, and this run has to pass on a checkout that has built the library and
never packed. scripts/release.sh runs them after packing, where a missing entry fails the release.

  -h, --help   Show this message.
EOF
}

fail() {
  echo "$script_name: $*" >&2
  exit 1
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    -h | --help)
      usage
      exit 0
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rust_dir="$repo_root/rust"
manifest="$rust_dir/Cargo.toml"
dotnet_dir="$repo_root/dotnet"
solution="$dotnet_dir/RustTrafilatura.sln"
build_script="$repo_root/scripts/build.sh"
report_dir="$repo_root/artifacts/test-results"

[ -d "$rust_dir" ] || fail "missing directory: $rust_dir"
[ -f "$manifest" ] || fail "missing manifest: $manifest"
[ -d "$dotnet_dir" ] || fail "missing directory: $dotnet_dir"
[ -f "$solution" ] || fail "missing solution: $solution"
[ -f "$build_script" ] || fail "missing script: $build_script"
command -v cargo >/dev/null 2>&1 || fail "cargo not found on PATH"
command -v dotnet >/dev/null 2>&1 || fail "dotnet not found on PATH"
# The Rust reports come from nextest's own JUnit reporter. It is the runner that can write one: libtest's
# machine-readable output is unstable, so `cargo test` has no supported way to produce a report.
command -v cargo-nextest >/dev/null 2>&1 ||
  fail "cargo-nextest not found on PATH: install it with 'cargo install cargo-nextest --locked'"

# The guard lives in the build script, whose stamp it checks: that keeps one implementation of the
# digest per shell, so the writer and the verifier cannot drift apart. It reports a missing staged
# directory, a missing stamp or a stale artifact, and its exit code is this script's.
echo "$script_name: verifying the staged native library"
"$build_script" --check

# No command below is piped: a pipeline would report the last stage's exit code instead of cargo's or
# dotnet's, and each failure has to name the step that failed.
echo "$script_name: cargo fmt --check"
cargo fmt --check --manifest-path "$manifest" || fail "cargo fmt --check failed with exit code $?"

echo "$script_name: cargo clippy --locked --all-targets -- -D warnings"
cargo clippy --locked --manifest-path "$manifest" --all-targets -- -D warnings ||
  fail "cargo clippy failed with exit code $?"

echo "$script_name: cargo nextest run --locked"
cargo nextest run --locked --manifest-path "$manifest" --profile junit-default ||
  fail "cargo nextest failed with exit code $?"

# The test-hooks feature gates four tests, three of them allocation-counter tests
# (`free_round_trip_restores_allocation_counter`, `allocation_counter_balances_over_many_calls` and
# `panic_is_caught`; the fourth, `concurrent_extract_and_free_returns_the_counter_to_baseline`, a
# concurrency test). Without this run they are compiled out, and
# `rust/tests/allocation.rs`, gated whole, runs nothing: a green suite that executed
# nothing. The staged library stays the default build, because scripts/release.* refuses an artifact
# that exports the feature's symbols.
echo "$script_name: cargo nextest run --locked --features test-hooks"
cargo nextest run --locked --manifest-path "$manifest" --features test-hooks --profile junit-test-hooks ||
  fail "cargo nextest --features test-hooks failed with exit code $?"

# nextest runs every test binary and no doc test, so the doc tests keep a step of their own: dropping
# them with `cargo test` would drop them silently, because a crate with no doc test reports a passing
# run of nothing.
echo "$script_name: cargo test --locked --doc"
cargo test --locked --manifest-path "$manifest" --doc || fail "cargo test --doc failed with exit code $?"

echo "$script_name: dotnet build -c Release"
dotnet build "$solution" -c Release || fail "dotnet build failed with exit code $?"

# The filter is xunit v3's own trait flag: the test project runs on Microsoft.Testing.Platform, opted
# in by global.json, and that platform does not accept the VSTest --filter expression syntax.
#
# One invocation per target framework: the report's file name is fixed, and a single invocation over
# both assets would have the second framework overwrite the first one's report. The framework list is
# read from the props that declare it rather than repeated here, so the two cannot disagree, and each
# name carries the framework so a reader can tell the two reports apart.
frameworks="$(sed -n 's/.*<TargetFrameworks>\([^<]*\)<\/TargetFrameworks>.*/\1/p' "$dotnet_dir/Directory.Build.props" | tr ';' ' ')"
[ -n "$frameworks" ] || fail "no TargetFrameworks in $dotnet_dir/Directory.Build.props"
for framework in $frameworks; do
  echo "$script_name: dotnet test -c Release -f $framework (Package category excluded)"
  dotnet test --solution "$solution" -c Release -f "$framework" --filter-not-trait "Category=Package" \
    --report-xunit-junit --report-xunit-junit-filename "dotnet-$framework.junit.xml" \
    --results-directory "$report_dir/dotnet" || fail "dotnet test failed with exit code $?"
done

echo "$script_name: Rust and C# suites passed"
