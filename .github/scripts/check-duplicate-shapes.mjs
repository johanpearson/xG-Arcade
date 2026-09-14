#!/usr/bin/env node
// S-237 (ADR-0084 CI backstop) — frontend half of
// docs/coding-guidelines.md's "Code health budget (per diff)" duplicated-
// shape check ("rule of three, not five"). See the ADR for this script's
// number for the tool/threshold rationale.
//
// jscpd only reports *pairwise* clones (two fragments at a time) — it has
// no "3+ occurrences" concept of its own. This script turns that pairwise
// output into occurrence groups (merging overlapping same-file fragments
// first, since one underlying block matched against N other copies shows
// up as N separate pairwise entries, not one N+1-way group), keeps only
// groups with 3+ distinct occurrences, and — because this is meant to
// behave like a per-diff check, not a whole-tree gate — only fails when a
// diff actually touches a file inside one of those groups.
//
// Usage:
//   node check-duplicate-shapes.mjs <jscpd-report.json> <diff-files.txt> <enforce|report-only>
//
// <diff-files.txt> is a newline-separated list of repo-root-relative paths
// the current diff touches (added/modified/renamed) — pass "-" or omit
// when there's no diff to filter against (report-only mode).
import { existsSync, readFileSync } from "node:fs";

const [, , reportPath, diffFilesPathArg, mode] = process.argv;

if (!reportPath || !existsSync(reportPath)) {
  console.error(`Duplicate-shape check: jscpd report not found at "${reportPath}".`);
  process.exit(1);
}

const report = JSON.parse(readFileSync(reportPath, "utf8"));
const duplicates = report.duplicates ?? [];

// jscpd's `name` field is relative to the path it was pointed at
// (frontend/src, see the workflow step that invokes it) — normalize back
// to repo-root-relative paths so these can be compared directly against
// `git diff --name-only` output.
const normalize = (name) => `frontend/src/${name}`;

// --- Step 1: merge overlapping same-file fragments into "location" nodes.
const fragmentsByFile = new Map();
const fragmentRefs = []; // fragmentRefs[2*i]/[2*i+1] = duplicates[i]'s two sides, in order

for (const dup of duplicates) {
  for (const side of ["firstFile", "secondFile"]) {
    const f = dup[side];
    const frag = { file: normalize(f.name), start: f.start, end: f.end };
    fragmentRefs.push(frag);
    if (!fragmentsByFile.has(frag.file)) fragmentsByFile.set(frag.file, []);
    fragmentsByFile.get(frag.file).push(frag);
  }
}

let nextLocationId = 0;
const fragmentToLocationId = new Map(); // frag object identity -> location id
const locationFiles = new Map(); // location id -> file

for (const [file, frags] of fragmentsByFile) {
  const sorted = [...frags].sort((a, b) => a.start - b.start);
  let current = null;
  let currentId = null;
  for (const frag of sorted) {
    // Strict `<` (not `<=`): two fragments that merely *touch* at a shared
    // boundary line (e.g. two back-to-back functions where jscpd's clone
    // range for one ends on the same line the next one's starts) are still
    // two distinct occurrences, not one — only a genuine line overlap
    // means "this is the same underlying block matched twice".
    if (current && frag.start < current.end) {
      current.end = Math.max(current.end, frag.end);
      fragmentToLocationId.set(frag, currentId);
    } else {
      currentId = nextLocationId++;
      current = { start: frag.start, end: frag.end };
      locationFiles.set(currentId, file);
      fragmentToLocationId.set(frag, currentId);
    }
  }
}

// --- Step 2: union-find over location ids, unioning the two sides of
// every jscpd duplicate entry.
const parent = new Map();
const find = (x) => {
  if (!parent.has(x)) parent.set(x, x);
  while (parent.get(x) !== x) {
    parent.set(x, parent.get(parent.get(x)));
    x = parent.get(x);
  }
  return x;
};
const union = (a, b) => {
  const ra = find(a);
  const rb = find(b);
  if (ra !== rb) parent.set(ra, rb);
};

for (let i = 0; i < duplicates.length; i++) {
  const firstId = fragmentToLocationId.get(fragmentRefs[i * 2]);
  const secondId = fragmentToLocationId.get(fragmentRefs[i * 2 + 1]);
  union(firstId, secondId);
}

// --- Step 3: group locations by connected component; keep 3+ occurrence
// groups only ("rule of three, not five" — a 2-occurrence pairwise clone
// is exactly the genuinely-parallel-but-distinct case this budget
// deliberately leaves alone).
const componentLocations = new Map(); // root id -> Set<location id>
for (const id of locationFiles.keys()) {
  const root = find(id);
  if (!componentLocations.has(root)) componentLocations.set(root, new Set());
  componentLocations.get(root).add(id);
}

const groups = [...componentLocations.values()]
  .filter((locs) => locs.size >= 3)
  .map((locs) => [...locs].map((id) => locationFiles.get(id)));

if (groups.length === 0) {
  console.log("Duplicate-shape check (jscpd): no 3+-occurrence groups found in frontend/src/**.");
  process.exit(0);
}

console.log(`Duplicate-shape check (jscpd): found ${groups.length} group(s) of 3+ near-identical occurrences:`);
for (const files of groups) {
  console.log(`  - ${files.length} occurrence(s) across: ${[...new Set(files)].join(", ")}`);
}

if (mode !== "enforce") {
  console.log(
    "\nReport-only run (no diff base available, e.g. workflow_dispatch) — findings above are informational, not failing the job.",
  );
  process.exit(0);
}

const diffFiles =
  diffFilesPathArg && diffFilesPathArg !== "-" && existsSync(diffFilesPathArg)
    ? readFileSync(diffFilesPathArg, "utf8")
        .split("\n")
        .map((l) => l.trim())
        .filter(Boolean)
    : [];
const diffFileSet = new Set(diffFiles);

const failing = groups.filter((files) => files.some((f) => diffFileSet.has(f)));

if (failing.length === 0) {
  console.log("\nNone of the 3+-occurrence groups involve a file this diff touches — not failing.");
  process.exit(0);
}

console.error(`\n${failing.length} duplicate-shape group(s) involve a file this diff touches:`);
for (const files of failing) {
  console.error(`  - ${[...new Set(files)].join(", ")}`);
}
console.error(
  '\nExtract a shared helper as part of this diff — see docs/coding-guidelines.md\'s "Code health budget (per diff)" (ADR-0084) and its CI backstop ADR.',
);
process.exit(1);
