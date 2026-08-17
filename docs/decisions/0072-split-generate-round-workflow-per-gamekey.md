# ADR-0072: Split `generate-round.yml` into per-`GameKey` round-generation workflows

- **Status:** Accepted
- **Date:** 2026-08-17
- **Related requirements:** REQ-301, REQ-1202
- **Related components:** COMP-03 (Core.Rounds)

## Context

Since S-084 (ADR-0051), a single workflow file, `generate-round.yml`, ran
one job on one daily cron (`0 6 * * *`) that called a shared bash retry
function twice — once for `GameKey = "xg-grid"`, once for
`GameKey = "xg-path"` — each call independently retried 3x with backoff.
ADR-0051 deliberately chose to extend that single workflow rather than add
a second `on.schedule` entry or a matrix strategy, specifically to avoid
re-deriving ADR-0027's `RoundDuration >= cron's max gap` safety invariant
for a second cron cadence when nothing about REQ-1202 required a different
*schedule* for `"xg-path"` — only potentially a different `RoundDuration`,
which the shared-cron design already accommodated per-`GameKey`.

The user-facing ask for this story (S-136) is explicit: separate workflow
files per game, one action visible per `GameKey` in the Actions tab,
independent success/failure status, independent manual dispatch. This is a
different motivation than ADR-0051's own cron-cadence question — it's about
operational visibility and manual-dispatch isolation, not about the
`RoundDuration`/cron-gap invariant. Splitting also surfaces a latent bug in
the shared workflow: its single `workflow_dispatch.round_duration_hours`
input, when supplied for a manual dispatch, was passed to *both* the
`"xg-grid"` and `"xg-path"` calls in the same run — an operator wanting a
one-off override for one game's round generation had no way to avoid also
overriding the other's.

Splitting is safe now for a different reason than it would have been unsafe
at ADR-0051's time. At that point, `RoundSchedulingOptions` had only just
gained per-`GameKey` resolution (`IRoundSchedulingOptionsResolver`) in the
very same story that would have needed to also derive a second cron's
max-gap invariant — real, avoidable work for no benefit, since nothing
server-side needed two schedules yet. Today, `RoundSchedulingOptions` is
already fully per-`GameKey` and independent (two registered instances, each
with its own configured `RoundDuration`), and `/internal/generate-round`
already takes `gameKey` as a first-class query parameter with no
game-specific branching outside the narrow `templateId` switch
(`GridTemplateResolver`/`PathTemplateResolver`). Nothing server-side needs
to change to give each `GameKey` its own cron — the split is now purely a
workflow-file reorganization, not a design change to the scheduling
machinery ADR-0051 built.

## Decision

Delete `.github/workflows/generate-round.yml` and replace it with two fully
independent workflow files:

1. **`generate-grid-round.yml`** — one job, one `on.schedule` cron
   (`0 6 * * *`, unchanged cadence), one `workflow_dispatch` with its own
   `round_duration_hours` input, calling `/internal/generate-round` once
   for `GameKey = "xg-grid"` only, with the existing 3-attempt/backoff
   retry shape (unchanged, just no longer looped).
2. **`generate-path-round.yml`** — the same shape, independently, for
   `GameKey = "xg-path"` only.

Each workflow's own `on.schedule` cron is re-derived against ADR-0027's
`RoundDuration >= cron's max gap` invariant **independently**, against its
own `GameKey`'s configured `RoundDurationHours`, rather than assuming the
old shared-cron proof still holds now that the two schedules can in
principle diverge:

- `generate-grid-round.yml`: daily cron, constant 24h max gap between
  firings. xG Grid's configured `RoundDuration`
  (`RoundScheduling:RoundDurationHours`, currently `48` in
  `appsettings.json`) is comfortably `>= 24h`. Safe.
- `generate-path-round.yml`: daily cron, constant 24h max gap between
  firings. xG Path's configured `RoundDuration`
  (`RoundScheduling:XGPath:RoundDurationHours`, currently `48` in
  `appsettings.json`) is comfortably `>= 24h`. Safe.

Both workflows keep the same `0 6 * * *` cadence as each other and as the
workflow they replace — nothing about this split requires the two
schedules to actually diverge, only that they are no longer structurally
coupled to a single file/job/cron entry. Each workflow's own
`workflow_dispatch.round_duration_hours` input now affects only its own
`GameKey`'s single generation call, fixing the coupling bug described
above as a side effect of the split, not as a separate change.

**Verification that a manual dispatch of one workflow never affects the
other's round (documented manual verification, not a new automated test):**
each workflow now calls `/internal/generate-round` with a hardcoded,
single, literal `gameKey` value (`"xg-grid"` or `"xg-path"`) — there is no
shared bash function call site, loop, or `workflow_dispatch` input
definition between the two files for a mistake to leak through. Existing
backend coverage (`RoundGenerationServiceTests`, added under ADR-0051's
S-084) already proves `RoundGenerationService.GenerateNextRoundIfNeededAsync`
itself never lets one `GameKey`'s generation touch the other's data at the
service layer; this ADR's change is confined to which workflow file
supplies which `gameKey`/`roundDurationHours` pair to that already-isolated
service call, so no new backend test is needed to prove isolation that was
already proven one layer down. A human should still confirm this once
against the deployed dev environment by manually dispatching
`generate-grid-round.yml` with a `round_duration_hours` override and
checking that the next `xg-path` round's `RoundDuration` is unaffected
(mirroring the inverse for `generate-path-round.yml`) before this ADR's
`workflow_dispatch` coupling-bug-fix claim is treated as field-verified,
not just code-reviewed — flagged here rather than performed, since this
sandbox has no path to trigger a real GitHub Actions dispatch.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep the single `generate-round.yml`, looping over both `GameKey`s (status quo, ADR-0051's design) | No change needed; already proven safe and working | Doesn't meet this story's explicit ask (separate workflow files, independent Actions-tab status per game); keeps the `workflow_dispatch` coupling bug | The user-facing ask is unambiguous, and the coupling bug is a real, avoidable footgun once noticed |
| A matrix strategy (`strategy.matrix.game_key: [xg-grid, xg-path]`) inside one workflow file | Less duplication than two full files; still one `on.schedule` entry to reason about | Explicitly rejected by this story's own scope (S-136: "two fully separate, independent files"); a matrix still shares one `workflow_dispatch` definition, so the input-coupling bug is harder to fix cleanly (a matrix input still applies to every matrix leg unless additionally keyed, adding complexity back) | Doesn't give genuinely independent `workflow_dispatch` inputs without extra machinery, and doesn't match what was asked for |
| A second `on.schedule` entry inside the same job/file, keeping one workflow but with per-`GameKey` step conditionals | Single file to maintain | Doesn't give independent Actions-tab run status per `GameKey` (the actual ask) — a failure in one `GameKey`'s step still shows as a single workflow run mixing both; doesn't fix the `workflow_dispatch` input coupling either, since a single input still applies workflow-wide | Same shortfalls as the matrix option, worse ergonomics |

## Consequences

- Positive: each `GameKey`'s round generation is now independently
  visible, independently dispatchable, and independently retryable in the
  GitHub Actions UI — a failure or a stuck run for `"xg-path"` no longer
  shares a job/run with `"xg-grid"`'s, and vice versa.
- Positive: the `workflow_dispatch.round_duration_hours` coupling bug is
  fixed — a manual override for one `GameKey` can no longer silently affect
  the other's next generated round.
- Positive: no backend/C# change was required — `RoundSchedulingOptions`,
  `IRoundSchedulingOptionsResolver`, `RoundGenerationService`, and
  `/internal/generate-round` are all unchanged, confirming ADR-0051's
  per-`GameKey` design was already shaped correctly for this split.
- Negative / trade-off accepted: two workflow files with near-identical
  content (the retry-function shape is duplicated, not shared via a
  reusable workflow) — a deliberate choice per this story's scope, which
  explicitly ruled out a shared/reusable workflow in favor of genuinely
  independent files. A future change to the retry logic's shape needs to
  land in both files.
- Follow-up: if a third game is added, this ADR's reasoning (rather than
  ADR-0051's shared-cron reasoning) is the one to re-derive against — see
  "For AI agents" below.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.

This ADR extends ADR-0027 and ADR-0051; it supersedes neither. ADR-0027's
`RoundDuration >= cron's max gap` invariant and ADR-0051's per-`GameKey`
`RoundSchedulingOptions`/resolver design both remain the underlying
mechanisms this split relies on — only the workflow-file/cron-ownership
layer changed.

Specifically:

- Any future divergence in `RoundDurationHours` between `"xg-grid"` and
  `"xg-path"` (or a change to either workflow's cron cadence away from
  daily) must re-check **that workflow's own** cron against ADR-0027's
  `RoundDuration >= cron's max gap` invariant independently — do not assume
  the other workflow's check still applies, and do not assume the old
  shared-cron proof from ADR-0027's S-084 addendum still holds now that the
  two workflows are structurally independent.
- Do not add a third game's round-generation workflow/cron without
  re-deriving whether this ADR's reasoning (fully independent, per-`GameKey`
  files) still holds, or whether a third game's operational needs argue for
  something else — don't assume "just add a third file" is automatically
  correct without checking.
- Do not reintroduce a shared/reusable workflow or a matrix strategy across
  `generate-grid-round.yml`/`generate-path-round.yml` without a new ADR —
  this story deliberately chose fully independent files, and collapsing
  them back would reintroduce the `workflow_dispatch` coupling bug this ADR
  fixed.
