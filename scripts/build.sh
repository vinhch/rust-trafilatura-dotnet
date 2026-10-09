#!/usr/bin/env bash
#
# Build the native library for one RID and copy it to
# dotnet/src/RustTrafilatura/runtimes/<rid>/native/.
set -euo pipefail

readonly script_name="build.sh"
readonly rids="win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64"

usage() {
  cat <<EOF
Usage: scripts/$script_name [--rid <rid>]

Builds rust-trafilatura-ffi for one RID and copies the shared library to
dotnet/src/RustTrafilatura/runtimes/<rid>/native/.

  --rid <rid>  Target RID (${rids// /, }). Defaults to the host RID.
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

library_for_rid() {
  case "$1" in
    win-*) echo "rust_trafilatura_ffi.dll" ;;
    osx-*) echo "librust_trafilatura_ffi.dylib" ;;
    *) echo "librust_trafilatura_ffi.so" ;;
  esac
}

rid=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --rid)
      [ "$#" -ge 2 ] || fail "--rid requires a value"
      [ -z "$rid" ] || fail "--rid given more than once"
      [ -n "$2" ] || fail "--rid requires a non-empty value"
      rid="$2"
      shift 2
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
done

[ -n "$rid" ] || rid="$(host_rid)"
case " $rids " in
  *" $rid "*) ;;
  *) fail "unsupported RID: $rid (expected one of: ${rids// /, })" ;;
esac
triple="$(triple_for_rid "$rid")"
library="$(library_for_rid "$rid")"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rust_dir="$repo_root/rust"
manifest="$rust_dir/Cargo.toml"

[ -d "$rust_dir" ] || fail "missing directory: $rust_dir"
[ -f "$manifest" ] || fail "missing manifest: $manifest"
command -v cargo >/dev/null 2>&1 || fail "cargo not found on PATH"

# Cargo resolves a relative CARGO_TARGET_DIR against the working directory, which
# this script never changes, so the same value locates the artifact here.
target_dir="${CARGO_TARGET_DIR:-$rust_dir/target}"
artifact="$target_dir/$triple/release/$library"
dest_dir="$repo_root/dotnet/src/RustTrafilatura/runtimes/$rid/native"

echo "$script_name: building $rid ($triple)"
cargo build --locked --release --manifest-path "$manifest" --target "$triple"

[ -f "$artifact" ] || fail "expected artifact not found: $artifact"

mkdir -p "$dest_dir"
cp "$artifact" "$dest_dir/$library"

echo "$script_name: source      $artifact"
echo "$script_name: destination $dest_dir/$library"
