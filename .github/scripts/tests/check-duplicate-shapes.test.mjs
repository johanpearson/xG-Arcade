#!/usr/bin/env node
// Regression test for .github/scripts/check-duplicate-shapes.mjs's
// overlap-merge + union-find grouping logic. Black-box: invokes the real
// script as a subprocess against a small synthetic jscpd report
// (fixtures/jscpd-report.sample.json), exactly as the code-health-budget
// CI job does, and asserts exit code + reported groups. No test
// framework — plain Node + assertions, matching this story's own "no new
// tool dependency" preference for these scripts.
//
// Fixture cases (see fixtures/jscpd-report.sample.json):
//   A/B/C — A.ts has two genuinely *overlapping* fragments (10-25,
//     14-30) each paired with a different file (B.ts, C.ts). These must
//     merge into a single location on A.ts's side, forming one 3-way
//     group (A.ts, B.ts, C.ts) — the "one block matched against two
//     other copies is 3 occurrences, not two separate 2-occurrence
//     pairs" case this script's grouping exists for.
//   D/E/F — D.ts has two fragments that only *touch* at a shared
//     boundary line (10-20, 20-30), each paired with a different file
//     (E.ts, F.ts). These must NOT merge (strict `<` overlap check, not
//     `<=`) — two distinct occurrences that happen to be adjacent, not
//     one repeated 3 times. Must NOT produce a 3+ group.
//   G/H — a single, ordinary 2-file pairwise clone. Must never be
//     flagged — a 2-occurrence match is exactly the genuinely-parallel-
//     but-distinct case this budget deliberately leaves alone.
//
// Usage: node .github/scripts/tests/check-duplicate-shapes.test.mjs
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";

const __dirname = dirname(fileURLToPath(import.meta.url));
const SCRIPT = join(__dirname, "..", "check-duplicate-shapes.mjs");
const FIXTURE = join(__dirname, "fixtures", "jscpd-report.sample.json");

let failures = 0;
const assert = (cond, message) => {
  if (!cond) {
    console.error(`FAIL: ${message}`);
    failures++;
  } else {
    console.log(`PASS: ${message}`);
  }
};

const run = (diffFilesPath, mode) => {
  const result = spawnSync("node", [SCRIPT, FIXTURE, diffFilesPath, mode], {
    encoding: "utf8",
  });
  return { code: result.status, stdout: result.stdout, stderr: result.stderr };
};

const tmpDir = mkdtempSync(join(tmpdir(), "check-duplicate-shapes-test-"));

try {
  // --- Case 1: diff touches B.ts (part of the merged A/B/C 3-way group)
  // -> must fail, and must report exactly one 3-occurrence group spanning
  // A.ts, B.ts, C.ts (proving the overlap-merge collapsed the two A.ts
  // fragments into one location rather than two).
  const diffAbc = join(tmpDir, "diff-abc.txt");
  writeFileSync(diffAbc, "frontend/src/cases/B.ts\n");
  const abc = run(diffAbc, "enforce");
  assert(abc.code === 1, "diff touching B.ts (in the merged A/B/C group) fails the job");
  assert(
    abc.stdout.includes("cases/A.ts") &&
      abc.stdout.includes("cases/B.ts") &&
      abc.stdout.includes("cases/C.ts"),
    "failing output cites all three files in the merged group (A.ts, B.ts, C.ts)",
  );
  assert(
    !/3 occurrence.*cases\/D\.ts|cases\/D\.ts.*3 occurrence/.test(abc.stdout),
    "the touching-not-overlapping D/E/F case is not reported as a 3-occurrence group",
  );

  // --- Case 2: diff touches E.ts (part of the touching-not-overlapping
  // D/E/F pair) -> must NOT fail, since D.ts's two fragments must stay
  // distinct locations (strict `<` overlap check), keeping each D-E/D-F
  // pair at size 2, never reaching the 3+ threshold.
  const diffDef = join(tmpDir, "diff-def.txt");
  writeFileSync(diffDef, "frontend/src/cases/E.ts\n");
  const def = run(diffDef, "enforce");
  assert(def.code === 0, "diff touching E.ts (touching, not overlapping, with D.ts) does not fail the job");

  // --- Case 3: diff touches G.ts (an ordinary 2-file pairwise clone) ->
  // must NOT fail — a 2-occurrence match is never a "rule of three"
  // violation on its own.
  const diffGh = join(tmpDir, "diff-gh.txt");
  writeFileSync(diffGh, "frontend/src/cases/G.ts\n");
  const gh = run(diffGh, "enforce");
  assert(gh.code === 0, "diff touching G.ts (a plain 2-file pairwise clone) does not fail the job");

  // --- Case 4: diff touches a file with no matches at all -> no failure.
  const diffNone = join(tmpDir, "diff-none.txt");
  writeFileSync(diffNone, "frontend/src/cases/Unrelated.ts\n");
  const none = run(diffNone, "enforce");
  assert(none.code === 0, "diff touching an unrelated file does not fail the job");

  // --- Case 5: report-only mode never fails, even when the diff would
  // otherwise match (workflow_dispatch has no diff base to filter with).
  const reportOnly = run(diffAbc, "report-only");
  assert(reportOnly.code === 0, "report-only mode never fails the job, even against a 3+ group's file");
  assert(
    reportOnly.stdout.includes("cases/A.ts"),
    "report-only mode still prints the groups it found, just doesn't fail on them",
  );
} finally {
  rmSync(tmpDir, { recursive: true, force: true });
}

if (failures > 0) {
  console.error(`\n${failures} assertion(s) failed.`);
  process.exit(1);
}
console.log("\nAll check-duplicate-shapes.mjs regression assertions passed.");
