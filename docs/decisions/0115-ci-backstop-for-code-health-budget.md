# ADR-0115: A CI job backstops two of ADR-0084's three per-diff code-health-budget checks

- **Status:** Accepted
- **Date:** 2026-09-14
- **Related requirements:** N/A (process/engineering-standards decision, not a product requirement)
- **Related components:** N/A (CI infrastructure; applies across `frontend/` and
  `backend/`, not a single component — see `docs/ai/agent-migration-plan.md`
  §4.3/§8 for the agent ownership model this extends)

## Context

ADR-0084 committed `quality-architect` to applying three code-health
heuristics on every diff it reviews (`docs/coding-guidelines.md`'s "Code
health budget (per diff)"): duplicated-shape rule-of-three,
sibling-relative god-file size, and a churn-aware hotspot check. That ADR
was explicit that this is a **judgment-based checklist**, not a
deterministic gate — its own "Negative / trade-off" says it "depends on
`quality-architect` actually applying it on every diff", and its
Follow-up named the exact trigger for reconsidering: "if a future sweep
shows new instances of these patterns still sprawled across several
commits before being caught... make the objectively-measurable half... a
deterministic script rather than relying on review judgment alone."

`docs/backlog.md`'s S-237 is that trigger being pulled deliberately (not
reactively — no new sprawl incident forced this, the backlog item asked
for the backstop directly): nothing in `ci.yml` today fails a build the
way `npm run test`/`tsc -b`/`oxlint` already do if a diff quietly
reintroduces a rule-of-three duplicate or adds a new, already-oversized
file, and `quality-architect`'s review is the only thing standing between
that pattern and `main` if the review step is skipped or misses it.

Of ADR-0084's three checks, two are objectively measurable without
judgment (duplicated-shape *detection*, though not judgment about whether
a given match is "genuinely parallel but distinct" code deserving an
exception; and sibling-relative file size). The third — churn-aware
hotspot risk ("is this file already high-churn, and does the diff make it
worse") — is not: it requires reading whether the diff *adds* complexity/
duplication versus reduces it, which a mechanical script cannot judge.
This ADR's scope is deliberately the first two only, matching the backlog
story; the churn check stays `quality-architect`'s manual review item,
untouched.

This sandbox has no `dotnet` SDK (`which dotnet` returns nothing) and no
Docker daemon, so a comparable C# duplicate-detection tool could not be
evaluated or prototyped as part of this pass — see Alternatives and the
Follow-up backlog item this ADR's implementation filed
(`docs/backlog.md`, next Epic 30 story after S-237) for the backend half.

## Decision

Add one new job, `code-health-budget`, to `.github/workflows/ci.yml`,
gated the same way as the three existing test jobs (`needs: [changes]`,
`if: needs.changes.outputs.code == 'true'`), running two independent
checks:

1. **Duplicate-shape scan, `frontend/src/**` only.** `npx jscpd@5.2.0`
   (pinned to this exact version — see "Tool choice and thresholds"
   below), scoped to TypeScript/TSX under `frontend/src`, excluding
   `*.test.ts(x)`/`*.d.ts`. jscpd only reports *pairwise* clones; a new
   script, `.github/scripts/check-duplicate-shapes.mjs`, turns that into
   occurrence groups (merging overlapping same-file fragments first, so
   one block matched against several other copies collapses into one
   group instead of several separate pairs), keeps only groups with 3+
   distinct occurrences (ADR-0084's "rule of three, not five"), and then
   — since this needs to behave like a per-diff check, not a whole-tree
   gate — only fails the job if the current diff actually touches a file
   inside one of those groups. jscpd has no native git-diff awareness, so
   this filtering is done in the script, not a jscpd flag.
2. **God-file/sibling-size check, `frontend/` + `backend/`.** A new
   script, `.github/scripts/check-new-file-sizes.sh` (bash, no new tool —
   this is pure line counting, needs neither `dotnet` nor `npm`), looks at
   every file the diff **adds** (`git diff --diff-filter=A`, never
   modifies — modified files are explicitly out of scope, see "Why added-
   only" below), compares its line count against the largest
   *pre-existing* sibling in the same directory (read from the base
   commit's tree, so neither the new file nor any other file the same
   diff adds can count as a sibling), and fails if it's >50% larger with
   no leading doc comment citing a REQ/ADR/COMP or otherwise explaining
   its size/cohesion.

Both checks resolve their diff base the same way, computed once in a
shared `Determine diff base and touched/added files` step: `pull_request`
uses `git merge-base origin/<base_ref> HEAD` (triple-dot semantics,
explicit refspec fetch since `actions/checkout` doesn't reliably leave
`origin/<base_ref>` behind); `push` uses `github.event.before`;
`workflow_dispatch` has no base to diff against, so both checks run in
**report-only mode** — they print whatever jscpd/the size scan finds, but
never fail the job, matching the `changes` job's own "manual dispatch
always runs the full suite, no path filtering" choice for the same event.

**Blocking vs. warning:** this job is **not** added to branch protection's
required-status-checks list. Per the `changes` job's own comment in
`ci.yml`, that list is confirmed to be exactly
`backend-tests`/`frontend-unit-tests`/`e2e-tests` — a new job existing (and
even going red) does not block merge unless someone separately changes
branch-protection settings, which this ADR does not do and this story did
not ask for. This gives the backlog's requested "start permissive /
soft-fail first" behavior for free, with no `continue-on-error` trick
needed: the job genuinely fails (non-zero exit) on a real violation and
shows red in the PR checks list, but auto-merge is not blocked by it.

## Tool choice and thresholds

**Why jscpd, and only for the frontend:** it's the de facto standard
copy/paste detector for JS/TS, needs no compilation step (works directly
against `.ts`/`.tsx` source), and is invokable via `npx` with zero
addition to `frontend/package.json`. No comparable, evaluable C# option
exists in this pass — this sandbox has no `dotnet` SDK to prototype
against (a strict requirement per `CLAUDE.md`'s "Testing without a local
dotnet SDK" and this repo's standing "don't run `dotnet` anything without
the SDK present" rule), so picking a C# tool blind, without confirming it
actually runs and reports something sane against this codebase's real
duplication patterns, risked shipping a backend check nobody had verified
that either does nothing or over-fires. The backend half is filed as a
follow-up story (`docs/backlog.md`, S-238) instead, explicitly blocked on
a session with `dotnet` access to survey real options (e.g. a Roslyn-based
analyzer, a CPD-style tool with a C# grammar) before committing to one.

**Thresholds landed on:**

| jscpd option | Value | Why |
|---|---|---|
| `--min-tokens` | 50 (jscpd's own default) | Validated against this tree — see "Validation" below — rather than assumed; kept explicit in `ci.yml` instead of implicit so a future jscpd default change can't silently retune this job |
| `--min-lines` | 5 (jscpd's own default) | Same reasoning |
| `--mode` | `mild` (default) | No `--ignore-identifiers`/`--ignore-literals` — this backstop only catches *exact* token matches, not "same shape, different names/values." The latter (recognizing a repeated *shape* despite different identifiers) is exactly the judgment call `code-health-auditor`'s sweeps and `quality-architect`'s manual review already do well; a mechanical backstop attempting it would risk exactly the false-positive/"genuinely parallel but distinct code" noise the backlog explicitly warned against |
| occurrence grouping | 3+ distinct locations (script-side, not a jscpd flag) | jscpd has no "minimum instances" concept — see Decision above |
| diff filter | only fail if a 3+ group includes a diff-touched file | Turns a whole-tree scan into something that behaves like a per-diff check, per the backlog's explicit ask |

`--min-tokens 50`/`--min-lines 5` are jscpd's shipped defaults, not a
custom permissive relaxation — they were kept rather than tightened
because the 3+-occurrence grouping and diff-touch filter (not the
per-pair token/line threshold) are what actually keep this from being
noisy; narrowing the per-pair threshold further risked missing real
shorter-but-real duplicated shapes for no measured benefit.

**God-file threshold:** >50% larger than the largest pre-existing sibling,
exactly ADR-0084's own documented rule of thumb — not independently
re-derived, since this check is a mechanical restatement of that existing
policy, not a new one.

## Validation

Run against `main` at implementation time (see the story's own acceptance
criteria):

- A whole-tree jscpd scan (no diff filter) over `frontend/src/social/*.tsx`
  specifically — the "rule of three working as intended" case, having
  already extracted `FetchListSection.tsx` — reports zero clones. Clean,
  as expected.
- A whole-tree scan over all of `frontend/src` does surface a handful of
  genuine 3+/4+/5+-occurrence groups elsewhere (e.g. `lib/leaderboard.ts`'s
  four near-identical query-param-building blocks; a leaderboard-screen
  family sharing a repeated result-list-rendering shape) — these are not
  false positives against the specific set `docs/backlog.md`'s S-237
  "Investigated and declined this pass" names (which is backend-only plus
  `frontend/src/social/*.tsx`/`PlayerRefreshFieldsList.tsx`), and are left
  as-is rather than fixed here — S-237 built the backstop, it didn't
  scope-creep into fixing every pre-existing finding it surfaces. Future
  diffs touching those files will see this job go red until someone
  extracts the shared shape, which is the intended behavior.
- The god-file check only evaluates *added* files, by design — every item
  in the "Investigated and declined" list (`ServiceRegistration.cs`,
  `CliVerbDispatcher.cs`, `WikidataClient.cs`, `XGPredictGameModule.cs`,
  the `ConnectChainStepDisputeService.cs`/`Endpoints.cs`/
  `RoundGenerationService.cs` group) is a pre-existing, already-tracked
  file, never a new addition in some diff being evaluated, so none of them
  can trigger this check regardless of their size.
- Acceptance criterion 1 (a deliberately reintroduced rule-of-three
  duplicate fails the job) was verified locally: a throwaway fixture file
  with three near-identical exported functions was added, scanned, and
  confirmed to produce a failing 3+-occurrence group whose file matched
  the simulated diff-touched-files list; a second fixture (a new,
  oversized file with no existing sibling in the same directory) was
  committed, verified to trip the god-file check, then verified again with
  a leading `// REQ-9999: ...` justification comment to confirm the
  doc-comment escape hatch works. Both fixtures and the temporary commit
  used to test them were removed immediately afterward (`git reset --hard`
  to the pre-fixture commit) — nothing from this validation pass remains
  in the tree.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Do nothing; keep relying on `quality-architect`'s manual review only | Zero added CI time/complexity | Exactly the gap ADR-0084 itself flagged as a Follow-up trigger — a missed or skipped review lets a rule-of-three violation or a new god-file land with nothing catching it | The backlog item (S-237) exists specifically because this gap was judged worth closing now, not deferred again |
| Make the new job a required status check (hard-blocking) from day one | Immediate enforcement, no risk of the finding being ignored | No measured false-positive rate yet for this exact tree/threshold combination — an over-eager scanner training reviewers to ignore it (the backlog's own explicit worry) is worse than a slower rollout; also out of scope (no branch-protection access from this session regardless) | Matches the backlog's explicit "start permissive... promote to blocking once false-positive rate is known" instruction; branch protection is deliberately untouched here |
| Full-tree gate (fail on *any* 3+-occurrence group or oversized file, not diff-scoped) | Simpler script, no diff-base computation needed | Would immediately fail on the pre-existing, not-yet-fixed findings this validation surfaced (the leaderboard family, `lib/leaderboard.ts`) on every single PR regardless of what it touches — turns a per-diff backstop into a whole-tree gate no one asked for, and duplicates `code-health-auditor`'s periodic-sweep role instead of complementing it | Contradicts both the backlog's "behaves like a per-diff check" framing and ADR-0084's own diff-scoped design this job is backstopping |
| A comparable C# duplicate scanner for `backend/`, guessed at without running it | One job covers both halves immediately | This sandbox cannot run `dotnet`, so the tool choice, its config, and its false-positive rate against real C# in this repo would all be unverified guesses shipped straight into CI | Filed as a follow-up story (S-238) instead, explicitly gated on `dotnet` access — guessing a backend tool blind is worse than not having one yet |
| `--ignore-identifiers`/looser jscpd similarity mode, to catch "same shape, different names" | Would catch more of what `code-health-auditor`'s sweeps call "duplicated shape" (which is about structure, not exact text) | Meaningfully raises false-positive risk on genuinely-different code that happens to share control-flow shape — exactly the class of finding that needs a human reading it, not a mechanical scanner | Left to `quality-architect`'s manual review and `code-health-auditor`'s periodic sweep, both of which already do this judgment call; the CI backstop stays a narrower, exact-match safety net underneath them |

## Consequences

- **Positive:** a rule-of-three violation or a new oversized file that
  slips past manual review (or is introduced when no review happens at
  all, e.g. a direct push) now shows up as a red CI check on the PR,
  closing exactly the gap ADR-0084's own Follow-up anticipated.
- **Positive:** built as a genuine per-diff check, not a whole-tree gate —
  it will not retroactively fail PRs for pre-existing findings this
  validation pass surfaced (the leaderboard family, `lib/leaderboard.ts`)
  unless a diff actually touches one of those files, at which point
  flagging it is the intended behavior, not a false positive.
- **Negative / trade-off:** frontend-only for now — `backend/`'s
  duplicate-shape check does not exist yet (the god-file check does cover
  `backend/`, since it needs no tooling). See Follow-up.
- **Negative / trade-off:** not a required status check, so a red
  `code-health-budget` job does not, by itself, block a merge — it relies
  on someone noticing the red check, the same reliability profile
  ADR-0084 already accepted for the manual review it backstops, just with
  a visible signal now instead of relying solely on review diligence.
- **Negative / trade-off:** the exact-match-only jscpd configuration means
  this backstop will not catch "same shape, different identifiers"
  duplication — that class of finding stays exclusively
  `quality-architect`'s/`code-health-auditor`'s judgment call, unchanged
  by this ADR.
- **Follow-up:** `docs/backlog.md` gained a new story (S-238, Epic 30) for
  a `backend/` duplicate-shape scanner, explicitly blocked on a session
  with `dotnet` SDK access to survey realistic C# options.
- **Follow-up:** promote `code-health-budget` to a required status check
  once it has run against a meaningful number of real PRs with a known,
  low false-positive rate — mirrors ADR-0084's own "revisit if... thresholds
  are miscalibrated" trigger, applied to this job's blocking status
  specifically.
- **Follow-up:** if `quality-architect`'s manual duplicate-shape/god-file
  findings and this job's findings start diverging materially on the same
  diffs (the job passing when a reviewer would flag something, or vice
  versa), that's a threshold-calibration signal for this ADR to revisit,
  not a reason to abandon either check.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision
needs a new ADR that supersedes this one, or the approach needs to
change. This ADR supersedes neither ADR-0084 (the per-diff code-health
budget policy itself, which is unchanged — this only automates two-thirds
of its enforcement) nor `docs/decisions/0000-template.md`'s scope; it adds
an automation layer on top of an already-decided policy.
