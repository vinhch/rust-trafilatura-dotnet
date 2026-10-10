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

Runs every command CI runs: cargo fmt --check, cargo clippy --locked --all-targets -- -D warnings
and cargo test --locked in rust/, then dotnet build -c Release and dotnet test -c Release in dotnet/.
The staged native library is verified against its build stamp first, so a stale or placeholder
artifact fails the run instead of passing against nothing.

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

[ -d "$rust_dir" ] || fail "missing directory: $rust_dir"
[ -f "$manifest" ] || fail "missing manifest: $manifest"
[ -d "$dotnet_dir" ] || fail "missing directory: $dotnet_dir"
[ -f "$solution" ] || fail "missing solution: $solution"
[ -f "$build_script" ] || fail "missing script: $build_script"
command -v cargo >/dev/null 2>&1 || fail "cargo not found on PATH"
command -v dotnet >/dev/null 2>&1 || fail "dotnet not found on PATH"

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

echo "$script_name: cargo test --locked"
cargo test --locked --manifest-path "$manifest" || fail "cargo test failed with exit code $?"

echo "$script_name: dotnet build -c Release"
dotnet build "$solution" -c Release || fail "dotnet build failed with exit code $?"

echo "$script_name: dotnet test -c Release"
dotnet test "$solution" -c Release || fail "dotnet test failed with exit code $?"

echo "$script_name: Rust and C# suites passed"
