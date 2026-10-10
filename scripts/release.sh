#!/usr/bin/env bash
#
# Produce artifacts/RustTrafilatura.<version>.nupkg from the staged native libraries.
#
# Every RID in the package layout has to be staged before a package that claims to support it is
# built, and the README and the three license files have to be present, so a package is refused rather
# than published half-complete. Packing itself is delegated to the package project; this script decides
# what is packable and never pushes unless --push says so.
set -euo pipefail

readonly script_name="release.sh"
readonly rids="win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64"
readonly package_files="README.md LICENSE LICENSE-GO.txt LICENSE-READABILITY-ENCODING"
readonly project="dotnet/src/RustTrafilatura/RustTrafilatura.csproj"

usage() {
  cat <<EOF
Usage: scripts/$script_name [--allow-partial] [--push]

Packs artifacts/RustTrafilatura.<version>.nupkg from the native libraries staged under
dotnet/src/RustTrafilatura/runtimes/<rid>/native/, with the README and the three license
files at the package root. The library files are the package content: the build stamp that
sits beside each one is build evidence and is never packed.

  --allow-partial  Pack the RIDs that are staged and report them, instead of refusing when one
                   of the five is missing. The RIDs that are present must still be complete.
  --push           Publish the package with the NUGET_API_KEY environment variable. A push
                   that is already on the feed is skipped, not an error.
  -h, --help       Show this message.
EOF
}

fail() {
  echo "$script_name: $*" >&2
  exit 1
}

# The library file one RID stages and the property that puts it in the package. The mapping lives here
# because this script is what decides which RIDs are packable, and the project reads one boolean per
# RID rather than a list: a semicolon-separated RID list cannot cross a command line as one MSBuild
# property value.
library_name() {
  case "$1" in
    win-x64) echo "rust_trafilatura_ffi.dll" ;;
    linux-x64 | linux-arm64) echo "librust_trafilatura_ffi.so" ;;
    osx-x64 | osx-arm64) echo "librust_trafilatura_ffi.dylib" ;;
    *) fail "unknown RID: $1" ;;
  esac
}

library_property() {
  case "$1" in
    win-x64) echo "RuntimeRid_win_x64" ;;
    linux-x64) echo "RuntimeRid_linux_x64" ;;
    linux-arm64) echo "RuntimeRid_linux_arm64" ;;
    osx-x64) echo "RuntimeRid_osx_x64" ;;
    osx-arm64) echo "RuntimeRid_osx_arm64" ;;
    *) fail "unknown RID: $1" ;;
  esac
}

allow_partial=false
push=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --allow-partial) allow_partial=true ;;
    --push) push=true ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) fail "unknown argument: $1" ;;
  esac
  shift
done

# Checked before anything is packed: a missing key discovered after a successful pack would leave a
# package behind that looks like the result of a release that never happened.
if [ "$push" = true ] && [ -z "${NUGET_API_KEY:-}" ]; then
  fail "--push needs the NUGET_API_KEY environment variable, which is not set. Nothing was packed or pushed."
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_path="$repo_root/$project"
artifacts_dir="$repo_root/artifacts"

[ -f "$project_path" ] || fail "missing package project: $project_path"
command -v dotnet >/dev/null 2>&1 || fail "dotnet not found on PATH"

# The package README and the license files have to be at the repository root, which is where the pack
# entries in the project read them from.
for name in $package_files; do
  [ -f "$repo_root/$name" ] || fail "missing package file: $repo_root/$name. Every package must ship the README and the three license files."
done

# The version is read from the project rather than repeated here, so the packed file and the assembly
# metadata cannot disagree.
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project_path" | head -n 1)"
[ -n "$version" ] || fail "no <Version> in $project_path"
package="$artifacts_dir/RustTrafilatura.$version.nupkg"

# One property per staged RID plus the marker that tells the project a selection was made. Anything not
# staged is reported below and left out of the package rather than staged here.
staged=""
missing=""
pack_arguments="-p:RuntimeRidsSelected=true"
for rid in $rids; do
  library="$repo_root/dotnet/src/RustTrafilatura/runtimes/$rid/native/$(library_name "$rid")"
  if [ -f "$library" ]; then
    staged="$staged $rid"
    pack_arguments="$pack_arguments -p:$(library_property "$rid")=true"
  else
    missing="$missing $rid ($library);"
  fi
done
staged="${staged# }"
missing="${missing# }"
missing="${missing%;}"

if [ -n "$missing" ]; then
  message="the native library is not staged for: $missing. Stage those RIDs with scripts/build.sh --rid <rid> (or scripts/build.ps1). A local pack can pack what is staged with --allow-partial, which is not a release package."
  if [ "$allow_partial" = false ]; then
    fail "$message"
  fi
  echo "$script_name: warning: $message" >&2
fi

[ -n "$staged" ] || fail 'no native library is staged for any RID, so the package would contain no library at all. Stage one with scripts/build.sh or scripts/build.ps1.'

echo "$script_name: version   $version"
echo "$script_name: packed    $staged"
[ -z "$missing" ] || echo "$script_name: omitted  $missing"

# A stale package from an earlier pack would be indistinguishable from this one, and `dotnet pack`
# overwrites in place, so the path it is about to write is removed first.
rm -f "$package"

# shellcheck disable=SC2086 # pack_arguments is a list of flags built above, each without whitespace.
if ! dotnet pack "$project_path" -c Release -o "$artifacts_dir" $pack_arguments; then
  fail "dotnet pack failed"
fi
[ -f "$package" ] || fail "dotnet pack reported success but wrote no package: $package"

size="$(wc -c <"$package" | tr -d ' ')"
echo "$script_name: package   $package ($size bytes)"

# The package-content tests read what was just written, so a package missing a RID, a license file,
# the README, an asset or carrying a build stamp fails here rather than in a consumer's install. The
# default suite excludes them because a checkout that has never packed has nothing for them to read.
tests_project="$repo_root/dotnet/tests/RustTrafilatura.Tests/RustTrafilatura.Tests.csproj"
[ -f "$tests_project" ] || fail "missing test project: $tests_project"
echo "$script_name: checking  $tests_project (Package category)"
if ! dotnet test "$tests_project" -c Release --filter 'Category=Package'; then
  fail "the package-content tests failed, so $package is not a package this repository will ship"
fi

if [ "$push" = true ]; then
  # --skip-duplicate makes a re-run of a release idempotent instead of a failure.
  if ! dotnet nuget push "$package" --api-key "$NUGET_API_KEY" --skip-duplicate; then
    fail "dotnet nuget push failed"
  fi
  echo "$script_name: pushed    $package"
fi
