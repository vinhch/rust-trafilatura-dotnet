#!/usr/bin/env bash
#
# Build the native library for one RID and copy it to
# dotnet/src/RustTrafilatura/runtimes/<rid>/native/.
#
# The copy is accompanied by a build stamp that ties the artifact to the rust/ tree and the toolchain
# it was built from. --check verifies a stamped artifact instead of building one, which is how
# scripts/test.sh refuses to run the suite against a stale or placeholder library.
set -euo pipefail

readonly script_name="build.sh"
readonly rids="win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64"
readonly stamp_name="build-stamp.txt"

# Bumped when the stamp's fields or the digest's construction change, so an old stamp is rejected
# with a message about the format rather than one about a mismatched hash.
readonly stamp_format="1"

usage() {
  cat <<EOF
Usage: scripts/$script_name [--rid <rid>] [--check]

Builds rust-trafilatura-ffi for one RID and copies the shared library to
dotnet/src/RustTrafilatura/runtimes/<rid>/native/, beside a build stamp that
records the rust/ tree and the toolchain the library came from.

  --rid <rid>  Target RID (${rids// /, }). Defaults to the host RID.
  --check      Verify the staged library against its stamp and exit; build nothing.
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
check=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --rid)
      [ "$#" -ge 2 ] || fail "--rid requires a value"
      [ -z "$rid" ] || fail "--rid given more than once"
      [ -n "$2" ] || fail "--rid requires a non-empty value"
      rid="$2"
      shift 2
      ;;
    --check)
      check=true
      shift
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
command -v rustc >/dev/null 2>&1 || fail "rustc not found on PATH"

# The toolchain is part of the stamp: an artifact built by a different rustc is not the artifact the
# current tree would produce.
toolchain_version="$(rustc --version)"

# Cargo resolves a relative CARGO_TARGET_DIR against the working directory, which
# this script never changes, so the same value locates the artifact here.
target_dir="${CARGO_TARGET_DIR:-$rust_dir/target}"
artifact="$target_dir/$triple/release/$library"
dest_dir="$repo_root/dotnet/src/RustTrafilatura/runtimes/$rid/native"
dest_library="$dest_dir/$library"
stamp_path="$dest_dir/$stamp_name"

# ---------------------------------------------------------------------------------------------
# Build stamp
#
# The stamp is what makes "the staged library corresponds to the current sources" a checkable
# statement. A modification time cannot carry it: a fresh clone gives every file the same time, so
# an artifact staged before a source edit is indistinguishable from one staged after. The digest
# therefore covers the paths and contents of the files the cdylib is built from, Cargo.toml,
# Cargo.lock and the toolchain version.
#
# The construction here is duplicated in scripts/build.ps1 and has to stay byte-identical: the two
# shells write interchangeable stamps, so scripts/test.sh and scripts/test.ps1 each verify whichever
# build script wrote the one on disk.
# ---------------------------------------------------------------------------------------------

# macOS has shasum and no sha256sum; coreutils hosts have both. Selected once so a pipeline does not
# repeat the lookup.
if command -v sha256sum >/dev/null 2>&1; then
  hash_command="sha256sum"
elif command -v shasum >/dev/null 2>&1; then
  hash_command="shasum"
else
  fail "neither sha256sum nor shasum is on PATH, so the build stamp cannot be hashed"
fi

hash_file() {
  if [ "$hash_command" = sha256sum ]; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

hash_stdin() {
  if [ "$hash_command" = sha256sum ]; then
    sha256sum | cut -d' ' -f1
  else
    shasum -a 256 | cut -d' ' -f1
  fi
}

# The files the cdylib is built from, as paths relative to rust/, in byte order. tests/, benches/
# and examples/ are left out because none of them is linked into the cdylib, so editing one must not
# invalidate the stamp and force a rebuild; target/ is build output.
source_paths() {
  (
    cd "$rust_dir"
    find . -type f -name '*.rs' \
      ! -path './target/*' ! -path './tests/*' ! -path './benches/*' ! -path './examples/*' |
      sed 's|^\./||'
    printf '%s\n' Cargo.toml Cargo.lock
  ) | LC_ALL=C sort
}

# One digest over the toolchain version and every source file's path and content hash. The toolchain
# string is inside the digest rather than stored beside it, so an artifact built by a different
# rustc cannot verify.
sources_hash() {
  local path
  {
    printf 'toolchain %s\n' "$toolchain_version"
    while IFS= read -r path; do
      printf '%s %s\n' "$path" "$(hash_file "$rust_dir/$path")"
    done < <(source_paths)
  } | hash_stdin
}

stamp_field() {
  sed -n "s/^$1=//p" "$stamp_path"
}

write_stamp() {
  local artifact_hash sources_digest
  artifact_hash="$(hash_file "$dest_library")"
  # Computed outside the braces so a failure aborts the script instead of being masked by printf.
  sources_digest="$(sources_hash)"
  {
    printf 'stamp=%s\n' "$stamp_format"
    printf 'rid=%s\n' "$rid"
    printf 'toolchain=%s\n' "$toolchain_version"
    printf 'sources=%s\n' "$sources_digest"
    printf 'artifact=%s\n' "$artifact_hash"
  } >"$stamp_path"
}

check_stamp() {
  local stamp_format_value stamp_rid stamp_sources current_sources stamp_artifact current_artifact

  stamp_format_value="$(stamp_field stamp)"
  [ "$stamp_format_value" = "$stamp_format" ] ||
    fail "the build stamp $stamp_path is not format $stamp_format (or has no stamp= line). Rewrite it with scripts/build.ps1 (or scripts/build.sh)."

  stamp_rid="$(stamp_field rid)"
  [ "$stamp_rid" = "$rid" ] ||
    fail "the build stamp $stamp_path records rid $stamp_rid, but this is the $rid directory. Stage $rid with scripts/build.ps1 --rid $rid (or scripts/build.sh --rid $rid)."

  stamp_sources="$(stamp_field sources)"
  current_sources="$(sources_hash)"
  [ "$stamp_sources" = "$current_sources" ] ||
    fail "the staged library is stale: the build stamp $stamp_path records sources $stamp_sources, the current rust/ tree hashes to $current_sources. The library was built before the sources, Cargo.lock or the toolchain changed. Rebuild it with scripts/build.ps1 (or scripts/build.sh)."

  stamp_artifact="$(stamp_field artifact)"
  current_artifact="$(hash_file "$dest_library")"
  [ "$stamp_artifact" = "$current_artifact" ] ||
    fail "the staged library does not match its build stamp: $dest_library hashes to $current_artifact, the stamp records $stamp_artifact, so the file is not the one the build produced - typically a placeholder, a partial copy or a different build. Rebuild it with scripts/build.ps1 (or scripts/build.sh)."

  echo "$script_name: stamp ok      $stamp_path"
  echo "$script_name: toolchain     $(stamp_field toolchain)"
  echo "$script_name: sources       $current_sources"
  echo "$script_name: artifact      $current_artifact"
}

if [ "$check" = true ]; then
  echo "$script_name: checking $rid ($triple)"
  [ -d "$dest_dir" ] ||
    fail "the native library is not staged for $rid: missing directory $dest_dir. Stage it with scripts/build.ps1 (or scripts/build.sh)."
  [ -f "$dest_library" ] ||
    fail "the native library is not staged for $rid: missing file $dest_library. Stage it with scripts/build.ps1 (or scripts/build.sh)."
  [ -f "$stamp_path" ] ||
    fail "no build stamp beside the staged library: missing file $stamp_path. Nothing records which rust/ tree $dest_library came from, so a stale or placeholder copy would pass unnoticed. Rebuild and stamp it with scripts/build.ps1 (or scripts/build.sh)."
  check_stamp
  exit 0
fi

echo "$script_name: building $rid ($triple)"
echo "$script_name: toolchain     $toolchain_version"
cargo build --locked --release --manifest-path "$manifest" --target "$triple"

[ -f "$artifact" ] || fail "expected artifact not found: $artifact"

mkdir -p "$dest_dir"
cp "$artifact" "$dest_library"
write_stamp

echo "$script_name: source      $artifact"
echo "$script_name: destination $dest_library"
echo "$script_name: stamp       $stamp_path"
