#!/usr/bin/env bash
# S-237 (ADR-0084 CI backstop) — sibling-relative god-file check from
# docs/coding-guidelines.md's "Code health budget (per diff)". Runs for
# both frontend/ and backend/ (it's pure line counting, needs neither
# `dotnet` nor `npm`).
#
# Deliberately scoped to files the diff ADDS, not files it modifies: a
# pre-existing large-but-cohesive file (ServiceRegistration.cs,
# CliVerbDispatcher.cs, etc. — see docs/backlog.md's S-237 "Investigated
# and declined" list) must never be flagged just because a diff happens to
# touch it. Only a brand-new file that's already oversized on arrival
# trips this.
#
# Usage: check-new-file-sizes.sh <base-sha-or-empty>
#   <base-sha-or-empty>: the commit to diff against for "what did this diff
#   add" and "what existed before it" (both the added-file list and the
#   sibling line counts are read from this commit's tree, so a new file
#   never gets counted as its own sibling). Empty/omitted means there's no
#   diff base to compare against (e.g. workflow_dispatch) — report nothing
#   and exit 0, matching the duplicate-shape check's report-only handling
#   for the same case.
set -euo pipefail

BASE_SHA="${1:-}"

if [[ -z "$BASE_SHA" ]]; then
  echo "God-file/sibling-size check: no diff base available (e.g. manual run) — skipping."
  exit 0
fi

mapfile -t added_files < <(git diff --name-only --diff-filter=A "$BASE_SHA...HEAD" -- frontend backend)

if [[ ${#added_files[@]} -eq 0 ]]; then
  echo "God-file/sibling-size check: no new files added under frontend/ or backend/ by this diff."
  exit 0
fi

violations=0

for f in "${added_files[@]}"; do
  [[ -f "$f" ]] || continue
  dir=$(dirname "$f")
  new_lines=$(wc -l < "$f" | tr -d ' ')

  # Largest pre-existing sibling in the same directory, read from the
  # pre-diff tree ($BASE_SHA) — this excludes both the new file itself and
  # any other file this same diff adds to that directory, since neither
  # exists yet at that commit. Non-recursive (trailing slash = immediate
  # children only), matching "sibling" = same directory, not subtree.
  max_sibling=0
  max_sibling_path=""
  while IFS= read -r sibling_path; do
    [[ -z "$sibling_path" ]] && continue
    lines=$(git show "${BASE_SHA}:${sibling_path}" 2>/dev/null | wc -l | tr -d ' ') || continue
    if (( lines > max_sibling )); then
      max_sibling=$lines
      max_sibling_path="$sibling_path"
    fi
  done < <(git ls-tree --name-only "$BASE_SHA" -- "${dir}/" 2>/dev/null)

  if (( max_sibling == 0 )); then
    echo "SKIP: $f ($new_lines lines) — no pre-existing sibling in $dir/ to compare against."
    continue
  fi

  # >50% larger, per docs/coding-guidelines.md's "Code health budget"
  # rule of thumb. Integer arithmetic: new_lines * 2 > max_sibling * 3
  # is equivalent to new_lines > 1.5 * max_sibling without needing floats.
  if (( new_lines * 2 > max_sibling * 3 )); then
    # Leading doc-comment justification: this repo's convention (see
    # ServiceRegistration.cs and friends) is a doc comment citing the
    # REQ/ADR/COMP it backs. Treat any leading block/doc comment
    # mentioning size, cohesion, or a REQ/ADR/COMP citation as sufficient
    # — deliberately lenient, false positives are the thing to avoid here.
    if head -n 30 "$f" | grep -Eiq '(REQ|ADR|COMP)-[0-9]+|cohesiv|\bsize\b|\bsingle[- ]responsibility\b'; then
      echo "OK (justified): $f ($new_lines lines) is >50% larger than the largest existing sibling in $dir/ ($max_sibling_path, $max_sibling lines), but has a leading doc comment justifying its size."
      continue
    fi
    echo "VIOLATION: $f ($new_lines lines) is >50% larger than the largest pre-existing sibling in $dir/ ($max_sibling_path, $max_sibling lines) and has no leading doc comment citing a REQ/ADR/COMP or explaining its size/cohesion."
    violations=$((violations + 1))
  fi
done

if (( violations > 0 )); then
  echo ""
  echo "$violations new file(s) exceed the sibling-relative god-file threshold without a documented reason."
  echo 'See docs/coding-guidelines.md'"'"'s "Code health budget (per diff)" (ADR-0084): either split the file or add a leading doc comment explaining why the size is intentional.'
  exit 1
fi

echo "God-file/sibling-size check: no violations among newly added files."
exit 0
