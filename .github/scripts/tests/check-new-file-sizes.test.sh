#!/usr/bin/env bash
# Regression test for .github/scripts/check-new-file-sizes.sh's blob-vs-
# tree filtering fix (quality-gate review of S-237, commit follow-up to
# d4fd11c): `git ls-tree --name-only -- "$dir/"` returns both blob (file)
# and tree (subdirectory) entries with no type filtering, and
# `git show base:path` on a *subdirectory* path does not fail — it exits
# 0 and prints that directory's own entry listing, which would then get
# silently miscounted as a "file"'s line count if not guarded against.
# This repo's C# namespace-per-folder layout makes "mostly subdirectories,
# one real file" a common shape (e.g. `backend/src/XGArcade.Core/`), so
# this is a real, not hypothetical, false-negative risk: a genuinely
# oversized new file compared against an inflated bogus "sibling" line
# count could silently pass.
#
# Self-contained: builds a throwaway git repo under a temp directory so
# this never touches xG-Arcade's own history. No test framework — plain
# bash + assertions, matching this story's own "no new tool dependency"
# preference for these scripts.
#
# Usage: bash .github/scripts/tests/check-new-file-sizes.test.sh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="${SCRIPT_DIR}/../check-new-file-sizes.sh"

tmp_repo=$(mktemp -d)
cleanup() { rm -rf "$tmp_repo"; }
trap cleanup EXIT

fail() {
  echo "FAIL: $1"
  exit 1
}

cd "$tmp_repo"
git init -q
git config user.email "test@example.com"
git config user.name "Test"

# --- Scenario: a directory with exactly one small real sibling file (3
# lines) and one subdirectory whose own immediate-entry listing is large
# enough (20 files -> a 22-line `git show` listing) to have masked the
# real file as the "largest sibling" before the blob-filter fix.
mkdir -p backend/src/Widgets/Big
printf 'line one\nline two\nline three\n' > backend/src/Widgets/Widgets.csproj
for i in $(seq 1 20); do : > "backend/src/Widgets/Big/File${i}.cs"; done

git add -A
git commit -q -m "base: directory that is mostly one subdirectory, one small real file"
base_sha=$(git rev-parse HEAD)

# New file: 15 lines. >50% larger than the *real* 3-line sibling (should
# fail), but smaller than the subdirectory's bogus ~22-line "count" (would
# have silently passed before the fix — the exact false negative this
# fixture targets).
for i in $(seq 1 15); do echo "new file line ${i}"; done > backend/src/Widgets/NewOversizedFile.cs
git add -A
git commit -q -m "add NewOversizedFile.cs"

output=$(bash "$SCRIPT" "$base_sha" 2>&1) && exit_code=0 || exit_code=$?
echo "$output"

[[ $exit_code -eq 1 ]] || fail "expected exit 1 (violation should be detected), got $exit_code"

echo "$output" | grep -q "Widgets.csproj, 3 lines" \
  || fail "expected the violation to cite the real 3-line sibling (Widgets.csproj) — got output that doesn't mention it, suggesting the subdirectory was miscounted as the sibling again"

echo "$output" | grep -q "Widgets/Big, " \
  && fail "the subdirectory (Big) was cited as if it were a file — blob-filtering regressed"

echo "PASS: check-new-file-sizes.sh correctly treats only blob (file) entries as siblings, not subdirectories."
