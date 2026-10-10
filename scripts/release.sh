#!/usr/bin/env bash
#
# Produce artifacts/RustTrafilatura.<version>.nupkg from the staged native libraries.
#
# Every RID in the package layout has to be staged before a package that claims to support it is
# built, and the README, the license files and the third-party notices have to be present, so a package is refused rather
# than published half-complete. Each staged library is also checked for the test-hooks exports, which
# exist only in a build of the test suite and must not reach a shipped artifact. Packing itself is
# delegated to the package project; this script decides what is packable and never pushes unless
# --push or --push-only says so.
set -euo pipefail

readonly script_name="release.sh"
readonly rids="win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64"
readonly package_files="README.md LICENSE THIRD-PARTY-NOTICES.md"
readonly project="dotnet/src/RustTrafilatura/RustTrafilatura.csproj"

# The two exports the `test-hooks` feature adds to rust/src/lib.rs. The panicking one is an export a
# release build must not carry, and the counter beside it comes from the same feature, so the shipped
# library must hold neither.
readonly forbidden_exports="rt_panic_for_test rt_live_buffers_for_test"

usage() {
  cat <<EOF
Usage: scripts/$script_name [--allow-partial] [--push | --push-only]

Packs artifacts/RustTrafilatura.<version>.nupkg from the native libraries staged under
dotnet/src/RustTrafilatura/runtimes/<rid>/native/, with the README, the license files and
the third-party notices at the package root. The library files are the package content: the
build stamp that sits beside each one is build evidence and is never packed. A staged library
that exports a test-hooks symbol is refused, because that feature is built only for the test suite.

  --allow-partial  Pack the RIDs that are staged and report them, instead of refusing when one
                   of the five is missing. The RIDs that are present must still be complete.
                   Refused together with --push and --push-only: what it packs is not a release
                   package.
  --push           Publish the package with the NUGET_API_KEY environment variable. A push
                   that is already on the feed is skipped, not an error.
  --push-only      Publish the package already in artifacts/ and pack nothing, which is what the
                   tag-gated publish job uses so the bytes it publishes are the ones the package
                   job built and attested. Requires NUGET_API_KEY and that file, and requires
                   RELEASE_TAG to name this version (v<version>).
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

# The build stamp cannot answer this: the feature changes no source path and no dependency the digest
# covers, so a test-hooks library and a release library of the same tree carry the same stamp. The
# export names are searched for in the library file rather than read through a symbol-table tool,
# because the linker writes each export's name into the file on every platform and `nm` is not
# installed on every host that runs this script.
assert_no_test_hooks() {
  local rid="$1" library="$2" symbol status
  for symbol in $forbidden_exports; do
    status=0
    LC_ALL=C grep -q -a -F "$symbol" "$library" || status=$?
    case "$status" in
      0)
        fail "the staged library for $rid exports $symbol, so it was built with the test-hooks feature. That export must not exist in a shipped artifact: rebuild it with scripts/build.sh (or scripts/build.ps1), which builds the default feature set."
        ;;
      1) ;;
      *)
        fail "could not read $library to check it for $symbol (grep exit $status), so the library cannot be declared free of the test-hooks feature"
        ;;
    esac
  done
}

allow_partial=false
push=false
push_only=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    --allow-partial) allow_partial=true ;;
    --push) push=true ;;
    --push-only) push_only=true ;;
    -h | --help)
      usage
      exit 0
      ;;
    *) fail "unknown argument: $1" ;;
  esac
  shift
done

if [ "$push" = true ] && [ "$push_only" = true ]; then
  fail "--push and --push-only are mutually exclusive: --push packs and pushes, --push-only pushes the package already in artifacts/."
fi

# A partial package is what this script itself calls "not a release package", so a push must not be
# able to carry one: publishing is the one step nothing later can undo.
if [ "$allow_partial" = true ] && { [ "$push" = true ] || [ "$push_only" = true ]; }; then
  fail "--allow-partial cannot be combined with --push or --push-only: --allow-partial packs the RIDs that happen to be staged and is not a release package, so a push would publish an incomplete one. Stage all five RIDs and pack without --allow-partial."
fi

# Checked before anything is packed: a missing key discovered after a successful pack would leave a
# package behind that looks like the result of a release that never happened.
if { [ "$push" = true ] || [ "$push_only" = true ]; } && [ -z "${NUGET_API_KEY:-}" ]; then
  fail "--push and --push-only need the NUGET_API_KEY environment variable, which is not set. Nothing was packed or pushed."
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_path="$repo_root/$project"
artifacts_dir="$repo_root/artifacts"

[ -f "$project_path" ] || fail "missing package project: $project_path"
command -v dotnet >/dev/null 2>&1 || fail "dotnet not found on PATH"

# The version is read from the project rather than repeated here, so the packed file and the assembly
# metadata cannot disagree.
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$project_path" | head -n 1)"
[ -n "$version" ] || fail "no <Version> in $project_path"
package="$artifacts_dir/RustTrafilatura.$version.nupkg"

# The package is pushed as it was packed, and only its presence is checked: repacking here would
# publish bytes the `package` job's provenance attestation does not cover, and what the file contains
# was already asserted where it was packed.
if [ "$push_only" = true ]; then
  [ -f "$package" ] ||
    fail "no package to push: missing file $package. --push-only publishes the package the package job built; pack one with scripts/release.sh first."
  # The file name comes from the project's <Version> and nothing else ties it to the tag that
  # triggered this run, so the tag is checked here: a release tagged against the wrong commit would
  # otherwise publish whichever version the checkout happens to carry.
  [ -n "${RELEASE_TAG:-}" ] ||
    fail "--push-only needs the RELEASE_TAG environment variable, the tag this release is for, so a mistagged run cannot publish a version nobody tagged. It must be v$version."
  [ "$RELEASE_TAG" = "v$version" ] ||
    fail "RELEASE_TAG is $RELEASE_TAG while the package is version $version, so this run would publish a version the tag does not name. Tag the commit that carries $version (v$version) and re-run."
  echo "$script_name: pushing   $package"
  if ! dotnet nuget push "$package" --api-key "$NUGET_API_KEY" --skip-duplicate; then
    fail "dotnet nuget push failed"
  fi
  echo "$script_name: pushed    $package"
  exit 0
fi

# The package README and the license files have to be at the repository root, which is where the pack
# entries in the project read them from.
for name in $package_files; do
  [ -f "$repo_root/$name" ] || fail "missing package file: $repo_root/$name. Every package must ship the README, the license files and the third-party notices."
done

# One property per staged RID plus the marker that tells the project a selection was made. Anything not
# staged is reported below and left out of the package rather than staged here.
staged=""
missing=""
pack_arguments="-p:RuntimeRidsSelected=true"
for rid in $rids; do
  library="$repo_root/dotnet/src/RustTrafilatura/runtimes/$rid/native/$(library_name "$rid")"
  if [ -f "$library" ]; then
    assert_no_test_hooks "$rid" "$library"
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
if ! dotnet test --project "$tests_project" -c Release --filter-trait 'Category=Package'; then
  fail "the package-content tests failed, so $package is not a package this repository will ship"
fi

if [ "$push" = true ]; then
  # --skip-duplicate makes a re-run of a release idempotent instead of a failure.
  if ! dotnet nuget push "$package" --api-key "$NUGET_API_KEY" --skip-duplicate; then
    fail "dotnet nuget push failed"
  fi
  echo "$script_name: pushed    $package"
fi
