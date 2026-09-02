# ADR-0102: xG Predict round generation tracks real matchday content instead of elapsed time

- **Status:** Accepted
- **Date:** 2026-09-02
- **Related requirements:** REQ-301, REQ-1301
- **Related components:** COMP-03 (Core.Rounds), COMP-15 (Games.XGPredict)

## Context

`RoundGenerationService.GenerateNextRoundIfNeededAsync`
(`backend/src/XGArcade.Core/Rounds/RoundGenerationService.cs`) chains
rounds as a fixed-period sequence: once the existing `latest` Round for a
`GameKey` has itself started (`latest.StartTime <= now`), it generates a
successor with `StartTime = latest.EndTime` and
`EndTime = StartTime + RoundDuration` — pure elapsed-time math, entirely
agnostic to what the owning `IGameModule`'s generated content actually
represents. This has always been fine for `"xg-grid"`/`"xg-path"`, whose
generated content (a grid of category cells, a set of guessable puzzles)
has no real-world timing of its own — any `RoundDuration` the product
wants is equally valid.

`XGPredictGameModule.GenerateInstanceAsync`
(`backend/src/XGArcade.Games.XGPredict/XGPredictGameModule.cs`) is
different: it calls `IFootballDataClient.GetUpcomingGameweekFixturesAsync`
fresh on every call. That client method itself is correctly real-world-
driven (it walks forward to the next matchday still fully in the future),
but nothing tracked which matchday a *previous* round already used, and
nothing connected `RoundGenerationService`'s chain-math `StartTime`/
`EndTime` to when the selected matches actually kick off.

**Root cause, traced through both failure directions:**

- **Duplicate (`RoundDuration` too short):** generation fires again before
  the real-world "next upcoming matchday" has changed → the same matchday
  gets selected into a second `PredictInstance`/`Round`.
- **Skip (`RoundDuration` too long) — the subtler failure, and the one pure
  dedup alone does NOT fix:** generation fires (once `latest.StartTime <=
  now`) and correctly resolves the real next matchday — the *content* is
  fine — but `RoundGenerationService` then schedules the new round's
  `StartTime` at `latest.EndTime` (pure chain math), which can land
  *after* that matchday's own kickoff has already passed, because chain
  math has zero relationship to real fixture timing. The round is
  generated with correct content but becomes "Active" only after its own
  matches have already kicked off — and per REQ-1303, `PredictInstance.
  LockInstant` (the earliest of its matches' own kickoffs) locks
  submissions immediately from the moment the round starts. Players never
  get a window to predict it.

  Worked example: `RoundDuration = 168h` (7 days, a natural-seeming
  default for "weekly" Premier League gameweeks). Gameweek N's round
  starts Saturday 15:00, so its chain-math successor is scheduled to start
  the *following* Saturday 15:00 — 7 days later. If a real midweek
  gameweek (a Tuesday, common around cup replays/European-competition
  weeks/rearranged fixtures) falls in between, e.g. the following Tuesday
  19:00, the generation call that would build that Tuesday round doesn't
  fire until the chain reaches Saturday 15:00 — four days *after* Tuesday
  19:00 already passed. `GetUpcomingGameweekFixturesAsync` has by then
  moved on to whatever matchday follows Tuesday's, so the Tuesday
  gameweek is never played at all: not delayed, silently skipped. No
  fixed `RoundDuration` avoids this for every real gameweek spacing — 48h
  (the historical default) and 168h both fail, in different real
  schedules, because real Premier League spacing is irregular by
  construction.

## Decision

Extend the existing periodic-chain abstraction at its actual seam, rather
than replacing it: `RoundGenerationService`'s round-lifecycle logic
(predecessor-closing, one-round-ahead check, `MAX(SequenceNumber)+1`
scoped to `GameKey`, persistence) is unchanged and stays shared across all
three games. Only the generation *contract* between `RoundGenerationService`
and `IGameModule` gains two small, backward-compatible extension points:

1. **`RoundConfig` gains a nullable `Guid? LatestGameInstanceId`.**
   `RoundGenerationService` already resolves `latest` (the current Round
   for this `GameKey`) before calling `GenerateInstanceAsync` — it now
   populates this field from `latest?.GameInstanceId` immediately before
   that call. `xg-grid`/`xg-path` never read it — an additive, no-op
   extension for those two games.

2. **`IGameModule.GenerateInstanceAsync` now returns `Task<GameInstance?>`.**
   Returning `null` means "no new round due for this `GameKey` right
   now" — `RoundGenerationService` treats it exactly like its existing
   "one round ahead already satisfied" no-op path: return `latest`
   unchanged, persist nothing new, never an error. **Contract:** a module
   must only return `null` when `config.LatestGameInstanceId` was
   non-null — never for a `GameKey`'s first-ever round.
   `RoundGenerationService` guards the contract-violation case (module
   returns `null` with `latest` also `null`) with a clear
   `InvalidOperationException` rather than letting a missing fallback
   throw a confusing `NullReferenceException`. `xg-grid`/`xg-path` never
   return `null` — same no-op extension.

3. **`GameInstance` gains `DateTime? SuggestedStartTime` and
   `DateTime? SuggestedEndTime`.** When a module supplies non-null values,
   `RoundGenerationService` uses them for the new Round's `StartTime`/
   `EndTime` instead of chain math:
   `startTime = instance.SuggestedStartTime ?? latest?.EndTime ?? now`;
   `EndTime = instance.SuggestedEndTime ?? (startTime + (roundDurationOverride ?? options.RoundDuration))`.
   `xg-grid`/`xg-path` continue returning both as `null` — zero behavior
   change for those two games.

`XGPredictGameModule.GenerateInstanceAsync` uses all three: after
selecting the tightest-kickoff-span cluster of matches (unchanged
selection logic), if `config.LatestGameInstanceId` is set, it loads that
instance and compares the **set of `ExternalFixtureId`s** against the
newly selected set. If they're identical, it returns `null` — no new
round due. Otherwise (a genuinely new matchday, or the GameKey's
first-ever round) it persists the `PredictInstance` as before and returns
`SuggestedStartTime = now` and
`SuggestedEndTime = <last selected match's kickoff> + PredictGradingOptions.TypicalMatchDuration`
(reusing the existing ~2h15m grading-margin constant rather than inventing
a new one).

`SuggestedStartTime = now` is deliberate: it makes the round "Active"
immediately on generation, matching "this is the current/upcoming round"
semantics. This has zero effect on REQ-1303's real submission gate —
`PredictInstance.LockInstant` (the earliest of the instance's own
matches' kickoffs) is the only thing that actually blocks a prediction
submission, entirely independent of `Round.StartTime`/`EndTime`
(ADR-0097 Decision §4). `SuggestedEndTime` anchored to the last match's
kickoff plus a typical-match-duration margin gives REQ-302's derived
`Closed` status a sensible real-world meaning ("closed once this
gameweek should be done playing") without gating or blocking grading in
any way — ADR-0097 already established `Closed`/`ClosedAt` are fully
decoupled from grading completeness, and this decision does not revisit
that.

**Accepted, deliberate limitation:** dedup is fixture-ID-**set** equality,
not a matchday-number concept — deliberately, so as not to widen
`IFootballDataClient`'s deliberately narrow, schema-unverified-against-live-
data contract (ADR-0099) just to expose a matchday number this comparison
doesn't need. If a match within an otherwise-unchanged matchday gets
postponed/rescheduled between two generation calls, the tightest-cluster
selection could shift slightly and this exact-set check would (correctly,
if conservatively) treat that as "a new round" rather than silently
missing the edge case. This is a reasonable trade-off, not a bug to chase
here.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| (a) As literally proposed in `docs/backlog.md`'s S-204 entry: `XGPredictGameModule` tracks which matchday it already used and `GenerateInstanceAsync` no-ops (returns "no new round due") when unchanged — **with `RoundGenerationService`'s chain-math `StartTime`/`EndTime` otherwise left in place** | Smallest possible interface change (just the no-op signal); stops the duplicate-round failure direction entirely | Does **not** fix the skip failure direction — a correctly-deduped, correctly-content'd round can still be scheduled (via chain math) to *start* after its own matches' kickoffs, per this ADR's own worked "168h" example. Dedup alone addresses only half the root cause: the timing decoupling is the deeper problem, not just the missing no-op signal | The skip case is the non-obvious, product-visible failure (a whole gameweek silently never played) — shipping only the dedup half would look like a complete fix while leaving that failure mode live for every real gameweek-spacing/`RoundDuration` combination that isn't a coincidental match |
| (b) As literally proposed: a fully separate `"xg-predict"`-specific generation path/service, entirely outside `RoundGenerationService`'s shared periodic chain | Free to model real fixture timing however xG Predict needs, with no compatibility constraint from xg-grid/xg-path's chain-math shape | Duplicates real, game-agnostic round-lifecycle machinery `RoundGenerationService` already owns and gets right: predecessor-closing or an equivalent, the one-round-ahead idempotency check, `MAX(SequenceNumber)+1` scoped to `GameKey`, and the actual `Round` persistence — none of that is xg-grid/xg-path-specific, so re-implementing it for a third `GameKey` is duplication for no architectural gain, and it diverges from the established "`Core.Rounds` owns round lifecycle, `IGameModule` supplies opaque instance content" split (ADR-0003) | The lifecycle machinery genuinely IS shared/game-agnostic; only the *timing-derivation* step needs to differ per game, which the chosen hybrid handles with a much smaller, additive contract change |
| **Chosen: extend the existing contract** — `RoundConfig.LatestGameInstanceId` (dedup input) + `IGameModule.GenerateInstanceAsync` returning `Task<GameInstance?>` (no-op signal) + `GameInstance.SuggestedStartTime`/`SuggestedEndTime` (timing override) | Reuses 100% of `RoundGenerationService`'s shared lifecycle logic unchanged; both failure directions (skip AND duplicate) fixed by the same mechanism, since the module now controls both "is a new instance due" and "what timing does it represent" in one place; fully backward-compatible for xg-grid/xg-path (all three new members are nullable, unread by those two modules) | A slightly larger interface surface than option (a) alone; `RoundSchedulingOptions:XGPredict:RoundDurationHours` becomes a dead fallback for this one `GameKey` (see Consequences) | Best fit: minimal, additive, backward-compatible change that fixes the actual root cause (timing decoupled from real content) rather than only its more visible symptom (duplicates) |

## Consequences

- Positive: both failure directions (silently-skipped midweek matchday,
  duplicated matchday) are fixed by the same mechanism — the module
  itself now decides both "is a new instance due" and "what does this
  instance's real-world timing look like," which chain math structurally
  could never answer correctly for content with independent real-world
  timing.
- Positive: `xg-grid`/`xg-path` are completely unaffected — all three new
  members (`RoundConfig.LatestGameInstanceId`, the nullable
  `GenerateInstanceAsync` return, `GameInstance.SuggestedStartTime`/
  `SuggestedEndTime`) are additive and nullable; both modules keep
  returning non-null instances with both suggested-time fields `null`.
- Positive: `RoundGenerationService`'s shared lifecycle logic
  (predecessor-closing, one-round-ahead, sequence numbering) is untouched
  — no duplication, no new parallel service.
- Negative / trade-off accepted: `RoundSchedulingOptions:XGPredict:RoundDurationHours`
  (`RoundScheduling:XGPredict:RoundDurationHours` / the deployed Container
  App's `RoundScheduling__XGPredict__RoundDurationHours` env var) **remains
  registered** — `IRoundSchedulingOptionsResolver.Resolve` is called
  unconditionally by `RoundGenerationService` for every `GameKey` it
  handles, and removing the registration would throw
  `InvalidOperationException` the next time an `"xg-predict"` round is
  generated — but its `RoundDuration` value is now a **dead fallback**
  for this one `GameKey`: `XGPredictGameModule` always supplies
  `SuggestedStartTime`/`SuggestedEndTime` once it returns a non-null
  instance, so `RoundGenerationService`'s chain-math `EndTime` formula
  (which is the only place this value would ever be read) is never
  actually reached for `"xg-predict"`. `/internal/generate-round`'s
  `roundDurationHours` per-call override, and `generate-predict-round.yml`'s
  `workflow_dispatch.round_duration_hours` input, are equally inert for
  this `GameKey` for the same reason. `ServiceRegistration.cs`'s
  registration comment and `RoundSchedulingOptions.cs`'s own class-level
  doc comment are both updated to say this plainly.
- Negative / trade-off accepted: the postponement/reschedule edge case
  noted above (Decision, "Accepted, deliberate limitation") is a known,
  intentionally unresolved gap, not a bug.
- Follow-up: none identified — revisit only if a future game needs a
  *third* kind of timing derivation this contract doesn't accommodate.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.

This ADR extends ADR-0003 (the opaque `IGameModule` boundary is fully
respected — Core still never inspects a game-specific instance shape; it
only reads the two new generic timing hints and the nullable return) and
ADR-0096/ADR-0097/ADR-0099 (xG Predict's existing entity/grading/data-source
decisions — none of them change here). It does **not** supersede ADR-0072
(the per-`GameKey` workflow-file split) — that reasoning still holds
unchanged; see ADR-0072's own dated amendment for what, if anything,
changes about its `RoundDuration`/cron-gap invariant reasoning as a result
of this ADR.

Specifically:

- Do not reintroduce chain-math timing for `"xg-predict"` (e.g. "simplify"
  `XGPredictGameModule.GenerateInstanceAsync` back to always returning a
  non-null `GameInstance` with null suggested times) without re-deriving
  this ADR's reasoning from scratch — that is exactly the bug this ADR
  fixes.
- Do not widen `IFootballDataClient` to expose matchday numbers (or any
  other new surface) to make the dedup check "cleaner" without a real,
  demonstrated need beyond what fixture-set equality already covers — see
  ADR-0099 on why that client's contract is deliberately narrow.
- A module implementing `IGameModule.GenerateInstanceAsync` must only ever
  return `null` when `RoundConfig.LatestGameInstanceId` was supplied
  (non-null) — returning `null` for a `GameKey`'s first-ever round is a
  contract violation that `RoundGenerationService` treats as a hard
  failure (`InvalidOperationException`), by design.
- Do not delete or make conditional the `RoundSchedulingOptions`
  registration for `"xg-predict"` in `ServiceRegistration.cs` — even
  though its `RoundDuration` value is a dead fallback for generation
  timing, `RoundSchedulingOptionsResolver.Resolve("xg-predict")` is still
  called unconditionally and must still resolve successfully.
