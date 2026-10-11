#!/usr/bin/env bash
#
# Fetch the pages the C# benchmarks read: markusmobius/content-extractor-benchmark at a pinned commit,
# sparse-checked out to its files/ directory under dotnet/benchmarks/corpus/. Fetching that one commit
# with a blob filter downloads about 30 MB rather than the whole repository history and result files.
set -euo pipefail

readonly script_name="corpus.sh"
readonly commit="6bb08131deacc9a32860605018f74991b076518b"
readonly repository="https://github.com/markusmobius/content-extractor-benchmark.git"
# What that commit holds under files/, read from the repository's own tree. The byte total is the
# blobs' own size, which is why the checkout below turns line-ending conversion off: a converted
# checkout is 892 KB larger and would look like a different corpus.
readonly expected_pages=1057
readonly expected_bytes=151009417

usage() {
  cat <<EOF
Usage: scripts/$script_name [--check]

Fetches the benchmark corpus into dotnet/benchmarks/corpus/$commit: that commit of
$repository, checked out sparsely to its files/ directory, about 30 MB
and under a minute. A second run finds the corpus in place and fetches nothing.

  --check      Verify the corpus instead of fetching it, and exit non-zero when it is
               missing or does not match the pin.
  -h, --help   Show this message.
EOF
}

fail() {
  echo "$script_name: $*" >&2
  exit 1
}

check=false
while [ "$#" -gt 0 ]; do
  case "$1" in
    -h | --help)
      usage
      exit 0
      ;;
    --check)
      check=true
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
  shift
done

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="$repo_root/rust/Cargo.toml"
cache="$repo_root/dotnet/benchmarks/corpus/$commit"

[ -f "$manifest" ] || fail "missing manifest: $manifest (this script has to run from its repository)"
command -v git >/dev/null 2>&1 || fail "git not found on PATH"

page_count() {
  find "$1/files" -maxdepth 1 -type f -name '*.html' | wc -l | tr -d ' '
}

# `find -printf` is GNU-only, so the bytes come from the files themselves rather than from their
# metadata.
byte_count() {
  find "$1/files" -maxdepth 1 -type f -name '*.html' -exec cat {} + | wc -c | tr -d ' '
}

# Both halves of the pin, plus the commit when the cache still carries its own git directory.
verified() {
  [ -d "$1/files" ] || return 1
  [ "$(page_count "$1")" = "$expected_pages" ] || return 1
  [ "$(byte_count "$1")" = "$expected_bytes" ] || return 1
  if [ -d "$1/.git" ]; then
    [ "$(git -C "$1" rev-parse HEAD 2>/dev/null || true)" = "$commit" ] || return 1
  fi
  return 0
}

if [ "$check" = true ]; then
  verified "$cache" ||
    fail "the corpus in $cache is missing or does not match the pinned commit $commit; run scripts/$script_name"
  echo "$script_name: corpus ok   $cache"
  exit 0
fi

if verified "$cache"; then
  echo "$script_name: corpus already fetched   $cache"
  exit 0
fi

echo "$script_name: fetching $commit"
rm -rf "$cache"
mkdir -p "$cache"
git -C "$cache" init --quiet
git -C "$cache" remote add origin "$repository"
# The commit is the pin: fetching it by hash means a moved branch cannot change what is measured.
git -C "$cache" fetch --quiet --depth 1 --filter=blob:none origin "$commit" ||
  fail "fetching $commit from $repository failed"
git -C "$cache" config core.autocrlf false
git -C "$cache" config core.eol lf
git -C "$cache" sparse-checkout set files
git -C "$cache" checkout --quiet FETCH_HEAD || fail "checking out the fetched commit failed"

verified "$cache" ||
  fail "the fetched corpus does not match the pin ($expected_pages pages, $expected_bytes bytes): $cache"

echo "$script_name: corpus $expected_pages pages, $expected_bytes bytes   $cache"
