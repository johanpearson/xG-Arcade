# ADR-0084: Fold `code-health-auditor`'s recurring per-diff-catchable heuristics into `quality-architect`'s standing review

- **Status:** Accepted
- **Date:** 2026-08-23
- **Related requirements:** N/A (process/engineering-standards decision, not a product requirement)
- **Related components:** N/A (applies across all components; see
  `docs/ai/agent-migration-plan.md` §4.3/§8 for the agent ownership model
  this extends)

## Context

`code-health-auditor` runs a periodic, whole-codebase health sweep
(`CODE_HEALTH_ASSESSMENT.md`, `CODEBASE_ANALYSIS.md`) and has, across its
four sweeps so far (Epics 7-9, 17, 21, 22), repeatedly caught the *same
small set of shapes*:

- A near-identical block repeated per case, each time only discovered
  after it had already been copy-pasted several times over:
  `WikidataClient.cs`'s HTTP handling (Epic 7), `GridGameModule.cs`'s
  multi-concern methods (Epic 9), `XGPathGameModule.cs`'s eligibility
  pipeline (Epic 17/ADR-0082), `PlayerCareerPrefetchService.cs`'s
  country/club sweep loops (Epic 21 S-165),
  `PlayerCacheWarmingService.cs`'s sweep loops (Epic 22 S-166,
  explicitly noted as "the *third* occurrence of this specific shape"),
  `CliVerbDispatcher.cs`'s per-handler Wikidata-client bootstrap (Epic 22
  S-167), and `frontend/src/lib/*.ts`'s 47 duplicated fetch call sites
  (Epic 22 S-168). `docs/coding-guidelines.md` already independently
  documented one instance of this exact pattern being fixed reactively —
  `useAuthedFetch` was only extracted once a fetch-classify-guard shape
  had been copy-pasted **five** times.
- A file or class quietly becoming the largest/most complex in its
  directory relative to its siblings (`CliVerbDispatcher.cs` at 769
  lines/13 commits — the single highest-churn backend file in the repo;
  `XGPathGameModule.cs` flagged as a "pre-emptive refactor candidate" at
  423 lines, then found to have grown +32% to 557 lines by the time the
  next sweep actually acted on it).
- Complexity × churn as the real risk signal, not either alone (the
  sweep's own explicit methodology, `CODE_HEALTH_ASSESSMENT.md` §
  "Method").

Each of these was, in principle, catchable at the diff that introduced
the second or third copy, or the diff that pushed a file past its
siblings — not just at the next periodic sweep, months of commits later.
But nothing in the standing per-diff process (`quality-architect`'s
review mode, wired into `/quality-gate`) currently applies any of these
heuristics; they were exclusively `code-health-auditor`'s, and that
agent is deliberately periodic/whole-tree by design
(`docs/ai/agent-migration-plan.md` §8), triggered by a request or a
stale tracking doc, not by a pending change.

## Decision

`quality-architect`'s Mode 1 (review) checklist gains a new, lightweight
**"Code health budget"** check, documented in
`docs/coding-guidelines.md`, applying three of `code-health-auditor`'s
own established heuristics at diff time:

1. **Duplicated-shape budget — rule of three, not five.** A diff that
   would create a third occurrence of the same near-identical block
   shape (in the diff itself, or by adding a second copy of a shape
   that already exists once elsewhere in the same file/directory) must
   extract a shared helper as part of that diff.
2. **God-file/god-class budget — sibling-relative, not absolute.** A
   file/class that becomes clearly the largest in its own directory
   without a documented reason (rule of thumb: ~50%+ larger than the
   next-largest sibling), or a constructor whose injected-dependency
   count crosses ~8-10 (the same god-class threshold
   `code-health-auditor`'s own scoring uses), is a split-or-justify
   decision to have in that review.
3. **Churn-aware hotspot check.** A single `git log --oneline -- <path>
   | wc -l` on touched files; a diff that adds complexity/duplication to
   an already-high-churn file is flagged as a hotspot-risk finding.

`quality-architect` owns this addition — it already owns
`docs/coding-guidelines.md` and the review checklist, per its existing
"engineering standards ownership" responsibility. `code-health-auditor`'s
periodic whole-tree sweep, 1.0-10.0 scoring, hotspot prioritization by
complexity×churn, and technical-debt epic planning are **unchanged** and
remain that agent's sole responsibility. This ADR does not merge the two
agents, does not change either agent's overall mode/scope, and does not
retroactively act on anything already tracked in Epic 21/22/23 — it only
makes the diff-scoped subset of these heuristics run on every diff
instead of being discovered solely by the next periodic sweep.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Do nothing; rely on periodic sweeps only | Zero added review overhead, no process change | Exactly the reactive gap this decision was asked to close — the same duplicated-shape pattern has now re-formed and been (re-)caught six separate times across four sweeps before anyone acted on it | Doesn't meet the explicit ask to make code health part of the *standing* per-diff process |
| Automate with a CI lint/static-analysis script enforcing line-count/duplication thresholds | Fully deterministic, no reliance on reviewer judgment | Near-identical-block-*shape* detection (same branching structure, different data plugged in) is not something a line-count or naive AST-diff rule reliably catches — every real finding in `CODE_HEALTH_ASSESSMENT.md` required reading the code, not just measuring it; a naive absolute threshold would false-positive on legitimately large-but-cohesive files (`WikidataClientTests.cs`, 3,973 lines; `PathCareerStintFilter.cs`, 544 lines, both explicitly judged "cohesive, watch-only" by the sweep) and false-negative on shape duplication a line-count tool can't see | Overbuilds a mechanism the task explicitly asked to keep lightweight; the objectively-measurable half (sibling-relative size, churn count) could still become a script later if judgment-based review proves to miss things — see Follow-up below |
| Fold `code-health-auditor`'s full methodology into every `quality-architect` review | Simplest single mental model, no duplicated heuristics across two docs | Contradicts the periodic/whole-tree vs. diff/single-story distinction `docs/ai/agent-migration-plan.md` §8 deliberately drew when `code-health-auditor` was created, and turns every PR review into a mini whole-tree sweep — the opposite of "lightweight" | Rejected by the task's own framing; a diff-scoped subset, not the whole methodology, is what belongs in a per-diff gate |
| New dedicated agent for this cross-cutting check | Clean separation of concerns | No orphaned responsibility here to justify a new agent (the §8 precedent for adding `code-health-auditor` was a genuinely different-shaped, different-triggered task); `quality-architect` already owns both `docs/coding-guidelines.md` and the review checklist this belongs in | Adds a third quality-flavored agent for a three-bullet checklist — disproportionate |

## Consequences

- **Positive:** the three patterns `code-health-auditor`'s sweeps have
  caught repeatedly (duplicated-shape, god-file/god-class,
  churn-hotspot) now get a chance to be flagged at the diff that
  introduces them, cutting the lag between "pattern formed" and "pattern
  flagged" from one sweep cycle to one review.
- **Positive:** no new agent, no new CI job or script — the check rides
  entirely inside `quality-architect`'s existing review mode and
  `/quality-gate`'s existing step 2, using only the diff plus a single
  `git log` command already available in this sandbox.
- **Negative / trade-off:** this is a judgment-based checklist, not a
  deterministic gate — it depends on `quality-architect` actually
  applying it on every diff, the same reliability profile as the rest of
  its review checklist (no new enforcement mechanism beyond that).
- **Negative / trade-off:** does not retroactively act on anything
  already flagged by Epic 21/22/23 — those remain
  `code-health-auditor`'s backlog items to execute, untouched by this
  ADR.
- **Follow-up:** revisit if `quality-architect`'s diff-time budget checks
  and `code-health-auditor`'s periodic sweep start finding materially
  different things on the same pattern (a sign the thresholds are
  miscalibrated), or if a future sweep shows new instances of these
  patterns still sprawled across several commits before being caught (a
  sign to make the objectively-measurable half — sibling-relative file
  size, churn count — a deterministic script rather than relying on
  review judgment alone).

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision
needs a new ADR that supersedes this one, or the approach needs to
change.
