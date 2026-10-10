#!/usr/bin/env bash
#
# Set or check the version the crate and the package carry.
#
# Cargo requires the crate version as a literal in rust/Cargo.toml and MSBuild takes the package
# version from the package project, so the number lives in two files, and cargo writes a third copy
# into rust/Cargo.lock for the crate itself. --set writes the one version it is given into all three;
# --check proves they agree, and that a tag names the same version. Neither commits and neither tags:
# the tag is the release act, so it stays a deliberate one.
set -euo pipefail

readonly script_name="version.sh"
readonly manifest="rust/Cargo.toml"
readonly lockfile="rust/Cargo.lock"
readonly project="dotnet/src/RustTrafilatura/RustTrafilatura.csproj"
readonly crate="rust-trafilatura-ffi"
# SemVer 2.0.0 without build metadata. NuGet does not carry build metadata in a package identity while
# cargo accepts it, so a version the two ecosystems read differently cannot be the one number the two
# manifests are checked against. The pre-release part is semver.org's own rule, leading zeros included:
# 1.0.0-rc.1 and 1.0.0-rc.1.2 are versions, 1.0.0- and 1.0.0+build.5 are not.
readonly version_pattern='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$'

usage() {
  cat <<EOF
Usage: scripts/$script_name --set <version> | --check [--tag v<version>]

The version the crate and the package carry lives in two files:
  $manifest, the literal cargo requires
  $project, the one MSBuild packs from
cargo writes a third copy into $lockfile. Both modes read all three.

  --set <version>  Write <version> into the manifest and the project, let cargo rewrite the
                   lockfile entry, then check that the three agree. Refuses a version that is not
                   <major>.<minor>.<patch>[-<prerelease>], and refuses to write into a file that
                   has uncommitted changes. It does not commit and does not tag.
  --check          Report the version the three files carry, and fail unless they agree and it is
                   <major>.<minor>.<patch>[-<prerelease>].
  --tag v<version> With --check: fail unless this tag names exactly that version, which is what a
                   release tag has to name.
  -h, --help       Show this message.
EOF
}

fail() {
  echo "$script_name: $*" >&2
  exit 1
}

# The `version = ` line cargo reads. Anchored, because `rust-version = "1.98.1"` carries the same
# words and is not the crate version.
manifest_version() {
  sed -n 's:^version = "\(.*\)"$:\1:p' "$manifest" | head -n 1
}

project_version() {
  sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$project" | head -n 1
}

# The crate is one [[package]] block among the dependency's, so the block is located by name and the
# version is read from it: the first `version = ` line in the file belongs to whichever dependency
# sorts first, not to the crate.
lock_version() {
  awk -v crate="$crate" '
    $1 == "name" && $3 == "\"" crate "\"" { inside = 1; next }
    $1 == "name" { inside = 0 }
    inside && $1 == "version" { gsub(/"/, "", $3); print $3; exit }
  ' "$lockfile"
}

check_version() {
  local version lock package

  version="$(manifest_version)"
  lock="$(lock_version)"
  package="$(project_version)"

  [ -n "$version" ] || fail "no version line in $manifest"
  [ -n "$lock" ] || fail "no [[package]] block for $crate in $lockfile"
  [ -n "$package" ] || fail "no <Version> element in $project"

  [[ "$version" =~ $version_pattern ]] ||
    fail "$version in $manifest is not <major>.<minor>.<patch>[-<prerelease>]"

  [ "$version" = "$package" ] ||
    fail "$manifest carries $version while $project carries $package. The native library reports the crate version and the package is named after the project's, so the same number has to be in both: set it with --set."

  [ "$version" = "$lock" ] ||
    fail "$manifest carries $version while $lockfile records $lock for $crate. Run a cargo command in rust/ that is not --locked to rewrite the entry, then check again."

  if [ "${tag:-}" != "" ]; then
    [ "$tag" = "v$version" ] ||
      fail "the tag is $tag while the sources carry $version, so this run would release $version under $tag. Tag v$version, or set the version the tag names with --set."
  fi

  echo "$script_name: $version in $manifest, $lockfile and $project"
  if [ "${tag:-}" != "" ]; then
    echo "$script_name: $tag names it"
  fi
}

set_version() {
  local new_version="$1" previous

  [[ "$new_version" =~ $version_pattern ]] ||
    fail "$new_version is not <major>.<minor>.<patch>[-<prerelease>], which is the shape a release tag names (v$new_version)"

  # The check is against HEAD rather than against the index: a staged edit is as uncommitted as an
  # unstaged one, and a rewrite laid over it would be indistinguishable from it in the diff, so one of
  # the two would be lost. The lockfile is named because --set rewrites it as well.
  git diff --quiet HEAD -- "$manifest" "$lockfile" "$project" ||
    fail "$manifest, $lockfile or $project has uncommitted changes, staged or not. Commit or stash them first, so this rewrite is the only change in the diff."

  # The counts use the same patterns the rewrites below use, so a version line this script cannot
  # rewrite is a refusal here rather than a rewrite that quietly rewrites only the other file.
  [ "$(grep -cE '^version = "[^"]*"$' "$manifest")" = "1" ] ||
    fail "$manifest does not carry exactly one version line this script can rewrite, so which one is the crate's is not a question it can answer"
  [ "$(grep -cE '<Version>[^<]*</Version>' "$project")" = "1" ] ||
    fail "$project does not carry exactly one <Version> element this script can rewrite, so which one the package uses is not a question it can answer"

  previous="$(manifest_version)"
  [ "$previous" != "$new_version" ] ||
    fail "$manifest already carries $new_version"

  # sed -i differs between GNU and BSD, and these scripts run on macOS too, so each rewrite goes
  # through a file beside its target. Both are written and checked before either is moved into place,
  # so a pattern that matches nothing cannot leave the pair disagreeing in the tree: it fails here,
  # with both targets untouched.
  trap 'rm -f "$manifest.tmp" "$project.tmp"' EXIT
  sed "s:^version = \"[^\"]*\"\$:version = \"$new_version\":" "$manifest" > "$manifest.tmp"
  sed "s:<Version>[^<]*</Version>:<Version>$new_version</Version>:" "$project" > "$project.tmp"
  grep -Fxq "version = \"$new_version\"" "$manifest.tmp" ||
    fail "$manifest has no version line to rewrite, so nothing was changed"
  grep -Fq "<Version>$new_version</Version>" "$project.tmp" ||
    fail "$project has no <Version> element to rewrite, so nothing was changed"
  mv "$manifest.tmp" "$manifest"
  mv "$project.tmp" "$project"
  trap - EXIT

  [ "$(manifest_version)" = "$new_version" ] ||
    fail "rewriting $manifest did not produce $new_version"
  [ "$(project_version)" = "$new_version" ] ||
    fail "rewriting $project did not produce $new_version"

  command -v cargo >/dev/null 2>&1 ||
    fail "$manifest carries $new_version now but cargo is not on PATH, so $lockfile still records $previous. Run a cargo command in rust/ without --locked, then re-run --check."

  # cargo writes the crate's own lockfile entry as part of a resolution rather than as a command of
  # its own, and --locked afterwards is what proves the entry it wrote matches the manifest.
  (cd rust && cargo metadata --format-version 1 >/dev/null) ||
    fail "cargo could not resolve rust/, so $lockfile was not rewritten"
  (cd rust && cargo metadata --locked --format-version 1 >/dev/null) ||
    fail "cargo resolved rust/ but $lockfile still disagrees with $manifest"

  check_version

  echo "$script_name: the staged native library is stale now, because the crate version is part of rust/ and the build stamp covers it: rebuild it with scripts/build.sh"
  echo "$script_name: commit $manifest, $lockfile and $project, then tag v$new_version"
}

mode=""
tag=""
new_version=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --set)
      shift
      [ "$#" -gt 0 ] || fail "--set needs a <version>"
      [ "$mode" != "check" ] || fail "--set and --check are separate runs"
      mode="set"
      new_version="$1"
      ;;
    --check)
      [ "$mode" != "set" ] || fail "--set and --check are separate runs"
      mode="check"
      ;;
    --tag)
      shift
      [ "$#" -gt 0 ] || fail "--tag needs a v<version>"
      tag="$1"
      ;;
    -h | --help)
      usage
      exit 0
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
  shift
done

case "$mode" in
  set | check) ;;
  *) fail "nothing to do: pass --set <version> or --check" ;;
esac

[ -z "$tag" ] || [ "$mode" = "check" ] || fail "--tag applies to --check"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

[ -f "$manifest" ] || fail "missing manifest: $manifest"
[ -f "$lockfile" ] || fail "missing lockfile: $lockfile"
[ -f "$project" ] || fail "missing package project: $project"

if [ "$mode" = "set" ]; then
  set_version "$new_version"
else
  check_version
fi
