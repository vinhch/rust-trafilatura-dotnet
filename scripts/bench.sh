#!/usr/bin/env bash
#
# Measure the rt_extract round trip against a direct core call on the host RID, in the bench
# profile, and exit non-zero when the ratio exceeds the budget in
# docs/specs/2026-10-08-dotnet-bindings-design.md (section 9).
#
# The same fixture is then run through dotnet/tools/AllocProbe, which prints the managed bytes one
# extraction call allocates: the number the specification's performance table quotes, on a command a
# reader can re-run.
set -euo pipefail

readonly script_name="bench.sh"
readonly rids="win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64"

usage() {
  cat <<EOF
Usage: scripts/$script_name

Runs rust/benches/roundtrip.rs for the host RID in the bench profile, which inherits
release, prints milliseconds per call for the direct core call and the two rt_extract
round trips, and exits non-zero when the round-trip ratio exceeds the 1.5x budget in
docs/specs/2026-10-08-dotnet-bindings-design.md (section 9). Then runs
dotnet/tools/AllocProbe, which prints the managed bytes one extraction call allocates
through the public API on the same fixture.

  -h, --help   Show this message.
EOF
}

fail() {
  echo "$script_name: $*" >&2
  exit 1
}

host_rid() {
  local os arch
  case "$(uname -s)" in
    Linux) os=linux ;;
    Darwin) os=osx ;;
    MINGW* | MSYS* | CYGWIN*) os=win ;;
    *) fail "unsupported host OS: $(uname -s)" ;;
  esac

  if [ "$os" = win ]; then
    # MSYS reports the shell's own architecture, which stays x86_64 under emulation.
    case "${PROCESSOR_ARCHITEW6432:-${PROCESSOR_ARCHITECTURE:-}}" in
      ARM64) arch=arm64 ;;
      *) arch=x64 ;;
    esac
  else
    case "$(uname -m)" in
      x86_64 | amd64) arch=x64 ;;
      arm64 | aarch64) arch=arm64 ;;
      *) fail "unsupported host architecture: $(uname -m)" ;;
    esac
  fi

  echo "$os-$arch"
}

triple_for_rid() {
  case "$1" in
    win-x64) echo "x86_64-pc-windows-msvc" ;;
    linux-x64) echo "x86_64-unknown-linux-gnu" ;;
    linux-arm64) echo "aarch64-unknown-linux-gnu" ;;
    osx-x64) echo "x86_64-apple-darwin" ;;
    osx-arm64) echo "aarch64-apple-darwin" ;;
    *) fail "no target triple mapped for RID: $1" ;;
  esac
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

rid="$(host_rid)"
case " $rids " in
  *" $rid "*) ;;
  *) fail "unsupported host RID: $rid (expected one of: ${rids// /, })" ;;
esac
triple="$(triple_for_rid "$rid")"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rust_dir="$repo_root/rust"
manifest="$rust_dir/Cargo.toml"

[ -d "$rust_dir" ] || fail "missing directory: $rust_dir"
[ -f "$manifest" ] || fail "missing manifest: $manifest"
command -v cargo >/dev/null 2>&1 || fail "cargo not found on PATH"

# Cargo resolves a relative CARGO_TARGET_DIR against the working directory, which this script never
# changes, so the same value locates the tree scripts/build.* filled. --target is what makes the
# bench reuse that tree instead of building a second set of dependencies for the host triple.
target_dir="${CARGO_TARGET_DIR:-$rust_dir/target}"

echo "$script_name: benching $rid ($triple)"
echo "$script_name: target directory $target_dir"

# The bench profile inherits release, so the ratio is measured on optimized code: a debug-profile
# number would be meaningless. Nothing here is piped, and a non-zero exit means the round trip is
# over budget or the build failed.
cargo bench --locked --manifest-path "$manifest" --target "$triple" --bench roundtrip

# The Rust bench never enters the managed side, so the allocation figure in the spec's performance
# table is measured here, on the same fixture, by a tool that is not part of the test run. It fails
# when the shipped read path stops allocating less than the string envelope it replaced.
probe="$repo_root/dotnet/tools/AllocProbe/AllocProbe.csproj"
[ -f "$probe" ] || fail "missing probe project: $probe"
command -v dotnet >/dev/null 2>&1 || fail "dotnet not found on PATH"

echo "$script_name: running the allocation probe"
dotnet run --project "$probe" --configuration Release ||
  fail "the allocation probe failed with exit code $?"
