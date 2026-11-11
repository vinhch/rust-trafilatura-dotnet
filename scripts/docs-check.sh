#!/usr/bin/env bash
#
# Check that the repo paths and relative Markdown links the docs rules cover still exist, and that each
# `::Name` after a path still occurs in its file. The rules are in docs/development.md, section "Docs
# check". Prints one line per broken pointer and exits 1 when there are any; prints nothing and exits 0
# when the docs are clean. The CI docs job runs it on Ubuntu, where a wrong-case path fails.
set -eu

readonly script_name="docs-check.sh"

usage() {
  echo "Usage: scripts/$script_name [-h | --help]: no arguments; prints one line per broken pointer and exits 1 if there are any."
}

fail() {
  echo "$script_name: $*" >&2
  exit 1
}

if [ "$#" -gt 0 ]; then
  case "$1" in
    -h | --help)
      usage
      exit 0
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

{
for f in AGENTS.md docs/architecture.md docs/development.md; do
  [ -f "$f" ] && grep -oE '`[^` ]*/[^` ]*`' "$f" | tr -d '`'
done | sort -u | while IFS= read -r t; do
  case "$t" in
    *'*'*|.work/*|.superpowers/*|artifacts/*|dotnet/benchmarks/corpus/*|dotnet/src/RustTrafilatura/runtimes/*) continue ;;
  esac
  p=${t%%::*}; n=${t#*::}
  if [ ! -e "$p" ]; then echo "missing path: $t"
  elif [ "$n" != "$t" ] && ! grep -qF -- "$n" "$p"; then echo "missing name: $t"
  fi
done
for f in AGENTS.md README.md docs/architecture.md docs/development.md; do
  [ -f "$f" ] || continue; d=$(dirname "$f")
  grep -oE '\]\([^)]+\)' "$f" | sed -E 's/^\]\(//; s/\)$//; s/#.*//' | while IFS= read -r l; do
    case "$l" in ''|*://*|mailto:*) continue ;; esac
    [ -e "$d/$l" ] || echo "broken link in $f: $l"
  done
done
} | { ! grep .; }
