# ADR-0097: xG Predict grading trigger, entity/read-path shape, and how a locked-but-ungraded round interacts with `Closed`/the leaderboard

- **Status:** Accepted
- **Date:** 2026-08-30
- **Related requirements:** REQ-1301, REQ-1302, REQ-1303, REQ-1304, REQ-1305,
  REQ-302, REQ-401, REQ-408, REQ-410
- **Related components:** COMP-15 (Games.XGPredict), COMP-07
  (DataSync.Clients), COMP-03 (Core.Rounds), COMP-04 (Core.Scoring)

## Context

REQ-1305 (`docs/requirements-document.md` §4.14) specifies WHAT asynchronous
per-match grading must do — fetch a finished match's real score, grade every
stored prediction per REQ-1304, retry a not-yet-confirmed match, void a
postponed/abandoned one (confirmed by the product owner, 2026-08-30, not an
open question), and stay idempotent — but explicitly defers two structural
questions to a new ADR ("Needs an ADR", REQ-1305's own closing section):

1. **What triggers grading** — a new scheduled job, an event, or something
   else.
2. **How REQ-302's derived `Closed` status and REQ-401/404/410's leaderboard
   participation interact with a round that is locked (REQ-1303) but not yet
   fully graded** — whether a round can close with matches still ungraded,
   and whether the leaderboard shows a growing partial total or withholds it.

Resolving question 2 in practice also forces a third, narrower question this
ADR must settle to make REQ-1305 implementable at all: **where grading
results are persisted and read from**, since the obvious existing
mechanisms — `Guess.FinalPoints` and `IGuessRepository`'s
`GetTotalFinalPointsByRoundIdAsync`/`GetPerRoundFinalPointsByUserIdsAsync`,
which every other `GameKey`'s round-total leaderboard read already goes
through — do not apply. ADR-0096 already established that xG Predict never
writes `Guess` rows at all; `XGPredictScoringStrategy.ScoreCorrectGuess` is
architecturally unreachable for exactly this reason (see that class's own
doc comment). Something new has to hold "this match is graded, here is its
real score" and "this prediction's resulting points," and something new has
to read a round's total back out of it.

**What already exists to build on, confirmed by reading the actual code
before deciding, not assumed by analogy:**

- `IApiFootballClient.GetFixtureResultAsync` (COMP-07, ADR-0094, built in
  S-191) already returns exactly the three-way outcome REQ-1305 needs —
  `ApiFootballFixtureOutcome.NotYetConfirmed` / `Finished` /
  `PostponedOrAbandoned` — as a clean, distinguishable value, never a thrown
  exception for the first and third cases. Grading logic does not need to
  invent its own status classification; it consumes this enum directly.
- `XGPredictScoringStrategy.ScorePrediction(predictedHomeGoals,
  predictedAwayGoals, actualHomeGoals, actualAwayGoals)` (Core.Scoring,
  ADR-0095, built in S-193) already implements REQ-1304's three-component
  formula and is unit-tested directly. It is a concrete method on the
  concrete class, not a member of `IScoringStrategy` — REQ-1304's own status
  note flagged this as "left for whichever future story builds REQ-1305's
  asynchronous grading job to decide how it actually gets called." This ADR
  decides that: the grading service takes `XGPredictScoringStrategy`
  directly (registered as itself in DI, alongside its existing
  `IScoringStrategy` registration), not through `IScoringStrategyResolver`
  — widening `IScoringStrategy`'s interface for one `GameKey`'s one extra
  method is not warranted by a single caller, the same "don't widen a shared
  interface speculatively" reasoning ADR-0096's own alternatives table
  already used for `ScoreResult`.
- `PredictMatch.KickoffUtc` and REQ-1303's already-implemented lock instant
  (`instance.Matches.Min(m => m.KickoffUtc)`, `XGPredictGameModule`) give a
  key simplification: **a match's own kickoff having passed already implies
  its round is locked**, with no separate round-lock check needed. The round
  lock instant is the *minimum* kickoff across the round's 5 matches, so any
  individual match's kickoff time is, by construction, always `>=` that
  minimum. If a specific match's kickoff (+ typical duration) has passed
  `now`, the round's lock instant — being no later than that match's own
  kickoff — has necessarily passed too. Grading can therefore operate purely
  off `PredictMatch` rows (kickoff time, grading status), with no
  `Round`/`IRoundRepository` dependency at all.
- `ScoreLockingService.LockRoundScoresAsync` (REQ-205's generic round-close
  scoring path, called by `RoundCloseService.CloseRoundAsync`) is already a
  safe no-op for an xG Predict round today, verified by reading it rather
  than assumed: it fetches guesses via `IGuessRepository.GetByRoundIdAsync`,
  and an xG Predict round has none, so `MaterializeUnansweredCellsAsync`
  returns immediately (`participantIds.Count == 0`) and the main scoring
  loop iterates zero guesses. Closing an xG Predict round (setting
  `Round.ClosedAt`, REQ-408) already does not, and will not, touch
  `PredictMatch`/`PredictMatchPrediction` in any way. This matters directly
  for question 2 below.
- `ScoringRules.PredictPointsPerComponent` and REQ-1304's higher-is-better
  direction (ADR-0095) mean a player who never submitted a prediction for a
  match simply contributes 0 points for it once graded, with no
  materialization step needed — unlike ADR-0021's golf-style "unanswered
  counts as the worst case" rule (`MaterializeUnansweredCellsAsync`), which
  exists only because 0 is the *best* score under lowest-wins and therefore
  needs an explicit worst-case row to avoid rewarding non-participation.
  Under xG Predict's conventional higher-is-better scoring, "no row" and "0
  points" already coincide — no synthetic row is needed or wanted.

## Decision

### 1. Trigger: a new scheduled job, mirroring ADR-0072's per-`GameKey` shape

A new workflow, `.github/workflows/grade-predict-matches.yml`, independent of
`generate-grid-round.yml`/`generate-path-round.yml`, calls a new
bearer-token-gated endpoint, `POST /internal/grade-predict-matches`,
registered unconditionally (every environment, same posture as
`/internal/generate-round` — REQ-301's own `InternalJobAuthorization`
pattern, not an environment-gated test-data endpoint under ADR-0006).

This is a new job, not an extension of `generate-grid-round.yml`'s or
`generate-path-round.yml`'s pattern in the same file: ADR-0072's own "For AI
agents" section prohibits recoupling those two files or reintroducing a
shared/matrix trigger between them, but grading is not round *generation* at
all — it is a wholly separate concern (REQ-1305's own Context text: "this
requirement deliberately does not assume it is triggered by, or reuses,
`ScoreLockingService.LockRoundScoresAsync`'s existing round-close trigger
point... a distinct trigger is required"). A third, purpose-built workflow
is the natural extension of ADR-0072's reasoning (independent files, one
visible Action per concern), not a variation that needs to reuse either
existing file.

**Cadence: hourly (`0 * * * *`), plus `workflow_dispatch` for manual runs.**
Reasoning, weighed against ADR-0094's own budget estimate:

- API-Football's free tier is 100 requests/day (ADR-0094). A round has
  exactly 5 matches (REQ-1301). Once a match reaches `Finished` or
  `PostponedOrAbandoned` it is never queried again (idempotency, Decision
  §2 below) — the only repeat-query cost is the `NotYetConfirmed` retry
  window, documented as up to 48h for some competitions (ADR-0094's own
  Context section).
- Hourly polling means at most 48 calls per match across that entire
  worst-case 48h window, ×5 matches = 240 calls spread over ~2 days (~120/
  day) in the *rare* case every match in a round hits the maximum
  confirmation delay simultaneously — in the overwhelming common case
  (results confirmed within minutes to hours of full time), the real cost
  is a small number of calls per match, comfortably inside ADR-0094's
  existing "well under 100/day" estimate.
- A 5-10 minute cadence (ADR-0094's own live-window polling guidance) is for
  polling a fixture that is *currently in progress*, which this job
  deliberately does not do — grading only ever checks a match once its
  kickoff + typical duration has already passed (REQ-1305's own trigger
  condition), i.e. only when the match should already be over. Polling that
  frequently for a check that only needs to notice "has this been confirmed
  yet, hours after the final whistle" would burn budget for no benefit.
  Hourly is frequent enough that grading completes promptly in the normal
  case, without materially risking the free-tier cap in the documented
  worst case.
- **Accepted risk, flagged for revisit:** if real usage ever shows multiple
  concurrent matches simultaneously stuck at the 48h confirmation ceiling
  (e.g. overlapping rounds, which REQ-1301's one-round-per-gameweek design
  makes unlikely at Tier 0 scale), the ~120/day worst case above could
  approach or exceed the free tier. Revisit the cadence (or move to the
  paid tier, itself already flagged as a live option by ADR-0094) only if
  real telemetry shows this happening — not preemptively.

`PredictGradingOptions.TypicalMatchDuration` — a new, plain constant
(`TimeSpan`, not appsettings-bound), following `ScoringRules`'s own "exact
values are an implementation detail, not specified by the REQ text"
precedent (`PredictPointsPerComponent`, `MaxPointsPerCell`) — governs the
"kickoff + typical duration has passed" trigger condition. Not a
per-`GameKey` `RoundSchedulingOptions` field: it has nothing to do with
round scheduling/duration (`RoundDurationHours`), only with how long after
its own kickoff one specific match is expected to have finished playing.

### 2. Entity/read-path shape: extend `PredictMatch`/`PredictMatchPrediction`, no new Core-level abstraction

- **`PredictMatch` gains a grading-state discriminator** — a new
  `PredictMatchGradingStatus` enum (`Pending` / `Graded` / `Voided`) plus
  nullable `ActualHomeGoals`/`ActualAwayGoals` (set only when `Graded`).
  `Pending` is every match's initial state (including every match that
  exists today, via the migration's default). This single column is what
  makes grading idempotent (Decision §3) and is the sole source of truth
  for "has this match been graded" — never inferred from whether prediction
  rows happen to carry points.
- **`PredictMatchPrediction` gains a nullable `FinalPoints` (int?)** — the
  same shape and the same meaning as `Guess.FinalPoints`: null means "no
  points computed for this row," set once, by the grading job, exactly once
  per match (never recomputed afterward, matching REQ-205's own "closing a
  round never re-scores it" precedent, extended here to "grading a match
  never re-scores it"). A prediction belonging to a `Voided` match is never
  touched — it keeps `FinalPoints == null` permanently, indistinguishable at
  the row level from "not yet graded," which is exactly correct per REQ-1305's
  own text ("as if that match were not part of the round for scoring
  purposes").
- **No materialized "missing prediction" rows** (contrast
  `MaterializeUnansweredCellsAsync`) — see Context's last bullet above. A
  user with no `PredictMatchPrediction` row for a given match simply
  contributes nothing for it, forever; this is already the correct,
  final answer under higher-is-better scoring, not a placeholder needing
  later correction.
- **A new read method, `IPredictInstanceRepository.GetTotalPointsByInstanceIdAsync(Guid predictInstanceId)`**,
  returns `IReadOnlyDictionary<Guid, int>` (`UserId` -> summed
  `FinalPoints`), computed as `SUM(FinalPoints)` over predictions whose
  parent `PredictMatch.GradingStatus == Graded` only — `Pending` and
  `Voided` matches are excluded from the sum entirely, satisfying REQ-1305's
  "an ungraded match contributes no components (not a placeholder
  worst-case value)" criterion and its "round total-score... grow[s] as
  further matches are graded over time" criterion directly: calling this
  method again after another match is graded returns a larger sum for any
  user with predictions on it, with no other state to update.
- **Deliberately not wired into `ILeaderboardService`/`LeaderboardEndpoints`
  in this story.** `GetClosedRoundLeaderboardAsync`/
  `GetWindowedLeaderboardAsync`/`GetClosedRoundsAsync` are all
  `Guess`-based today and would need their own per-`GameKey` branch to also
  read Predict-based totals — a real, separate piece of work with its own
  design questions (e.g. how `GetClosedRoundsAsync`'s `ClosedAt`-gated
  browsing interacts with a round whose total is still growing), consistent
  with every prior xG Predict story's own scope note (S-190 through S-194:
  "real HTTP... wiring... deliberately deferred"). `GetTotalPointsByInstanceIdAsync`
  above exists so REQ-1305's own "round total-score reads" acceptance
  criterion and API/Integration test level are satisfiable and testable now,
  without that broader wiring. Flagged explicitly as follow-up in
  `docs/backlog.md`, not silently left undiscoverable.

Grading itself lives in a new `Games.XGPredict` (COMP-15) service,
`IPredictGradingService`/`PredictGradingService` — **not** `Core.Scoring`,
unlike `ScoreLockingService`. `ScoreLockingService` is generic across every
`GameKey` because it operates entirely through `IGuessRepository`/
`IGameModule`'s opaque abstractions (ADR-0003's boundary). Grading, by
contrast, reads and writes `PredictMatch`/`PredictMatchPrediction` directly
— entities that belong to COMP-15, not Core — so putting grading logic in
`Core.Scoring` would mean `XGArcade.Core` referencing xG-Predict-specific
entities, exactly what ADR-0003/the "xG Arcade/game boundary" rule in
CLAUDE.md forbids. `PredictGradingService` depends on
`IPredictInstanceRepository`, `IApiFootballClient`, `XGPredictScoringStrategy`
(concrete, per Context above), and `TimeProvider` — all already-established
dependencies, no new cross-component reference introduced.

### 3. Idempotency

The grading job's query is: every `PredictMatch` where `GradingStatus ==
Pending` and `KickoffUtc + PredictGradingOptions.TypicalMatchDuration <=
now`. For each:

- `IApiFootballClient.GetFixtureResultAsync(ExternalFixtureId)` ->
  `Finished`: grade every `PredictMatchPrediction` row for that match via
  `XGPredictScoringStrategy.ScorePrediction`, set each row's `FinalPoints`,
  set the match's `ActualHomeGoals`/`ActualAwayGoals`/`GradingStatus =
  Graded` — persisted together (same transaction/unit of work) so a crash
  mid-write cannot leave `FinalPoints` set on some predictions while the
  match itself is still `Pending` (which would make a retry re-grade and
  double-count) or vice versa (which would leave `Graded` predictions with
  `FinalPoints == null`, indistinguishable from a genuinely-absent
  prediction and silently under-counting that player).
- `NotYetConfirmed`: no write at all — the match stays `Pending`, picked up
  again next run. This is the plain, direct implementation of REQ-1305's
  "left ungraded... retried on a subsequent run" criterion; no separate
  retry-count/backoff state is needed since the job's own hourly cadence
  already is the retry loop.
- `PostponedOrAbandoned`: set `GradingStatus = Voided` only — no
  `ActualHomeGoals`/`ActualAwayGoals` write (API-Football's own values for
  these are explicitly untrustworthy for this outcome per
  `ApiFootballFixtureOutcome`'s own doc comment), no `FinalPoints` write on
  any prediction for this match, ever.
- A second run over an already-`Graded` or already-`Voided` match: excluded
  by the query above (`GradingStatus == Pending` is the only match this job
  ever selects), so it is never re-fetched from API-Football and never
  re-graded — REQ-1305's idempotency criterion by construction, not by a
  separate guard.
- One match's failure (e.g. `ApiFootballClientException` for a transient
  API problem) must not abort grading for the round's other matches, or
  other rounds' matches, in the same run — caught and logged per-match,
  mirroring `InternalRoundEndpoints`'s own per-failure-mode `catch`
  discipline, with the job's overall response summarizing counts (graded /
  voided / still-pending / failed) rather than a single pass/fail signal.

### 4. `Closed`/leaderboard interaction: fully decoupled, by design — not something to reconcile

REQ-302's derived `Closed` status (time-only,
`RoundStatusExtensions.GetStatus`) and `Round.ClosedAt`/`RoundCloseService`
(REQ-408's separate "browsable in past-rounds" concept) are **both entirely
unaffected by grading completeness**, and this is not a gap to patch — it is
the direct, intended consequence of REQ-1305's own Context text (grading is
"a genuinely separate, asynchronous concern," deliberately not hung off the
round-close trigger) combined with the already-verified fact (Context above)
that `LockRoundScoresAsync` is already a no-op for an xG Predict round:

- A round may become `Closed` per REQ-302, and/or have `ClosedAt` set via
  `RoundCloseService.CloseRoundAsync`, while some or all of its 5 matches
  remain `Pending`. Nothing gates either transition on grading state, and
  nothing needs to: REQ-1305's own acceptance criteria explicitly describe
  a round's total *growing after* grading continues, which only makes sense
  if closing does not wait for (or get blocked by) full grading.
  Building a "hold the round open/unclosed until every match grades"
  mechanism would contradict REQ-1305's own text and add machinery no
  acceptance criterion asks for — rejected as scope creep, not merely
  unbuilt.
- The leaderboard shows a **partial, growing total, never a withheld one**
  — `GetTotalPointsByInstanceIdAsync` (Decision §2) always returns whatever
  is graded so far, with no "wait until 5-of-5 matches are graded" gate.
  This is the same answer REQ-1305's acceptance text already gives
  directly ("a round's total-score contribution... reflects only matches
  that have actually been graded... grow[ing]... over time") — this ADR
  does not invent a new position, it records that this settles question 2
  cleanly rather than needing a design trade-off.
- Whether a *future* round-generation story for `"xg-predict"` should
  schedule `EndTime` generously enough that grading is *usually* complete
  by the time a round closes (REQ-1305's own "Needs an ADR" text raised this
  as a possibility) is **not decided here** — REQ-1305's acceptance
  criteria do not require it (a round closing with a still-growing total is
  explicitly an acceptable, described state, not a failure mode), and
  `RoundSchedulingOptions` for `"xg-predict"` remains unregistered/deferred
  regardless (unchanged from every prior xG Predict story's own scope
  note). Revisit only once round generation is actually wired for this
  `GameKey` and real `EndTime` values exist to tune.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Event-driven trigger (e.g. queue a "check this match" message keyed off each match's own kickoff + duration) | No wasted polling for matches nowhere near finishing | New infrastructure (a queue/scheduler) this codebase has never needed before, for a Tier-0-scale, 5-matches-per-gameweek game; a scheduled job is proven, already-running infrastructure (ADR-0072) that costs nothing new to add | Disproportionate complexity for the actual scale; the existing scheduled-job pattern already fits (REQ-1305's own "Needs an ADR" text lists this as one option among several, not a preferred one) |
| Reuse `ScoreLockingService.LockRoundScoresAsync`'s trigger point (fold grading into `RoundCloseService.CloseRoundAsync`) | One fewer trigger to maintain | REQ-1305's own Context section explicitly rules this out — a match isn't gradable until hours-to-days after the round closes in the general case, so tying grading to round-close would mean most matches are simply never graded (close happens once, grading needs to happen repeatedly, later) | Directly contradicted by the requirement text itself |
| Gate `Round.ClosedAt`/REQ-302's `Closed` status on "every match graded" | A closed round would always show a final, complete total — simpler mental model | Contradicts REQ-1305's explicit "grow[ing]... over time" acceptance criterion; requires inventing round-level grading-completeness tracking with no REQ asking for it; risks a round staying open indefinitely if one match's confirmation is delayed past the round's next-round-generation cadence | Scope creep beyond what REQ-1305 specifies; the requirement's own text already answers this the other way |
| Add grading to `Core.Scoring` (extend `ScoreLockingService` or `IScoringStrategy` itself to know about `PredictMatch`) | Single "scoring" home for all games | Requires `XGArcade.Core` to reference `PredictMatch`/`PredictMatchPrediction`, both COMP-15-owned entity types — a direct violation of ADR-0003/CLAUDE.md's xG Arcade/game boundary rule | Structurally forbidden, not just stylistically worse |
| Widen `IScoringStrategy` to add a `ScorePrediction`-shaped member so grading can depend on the interface, not the concrete class | Keeps the grading service's dependency abstract | Every other `IScoringStrategy` implementation (`UniquenessScoringStrategy`, `ClueEfficiencyScoringStrategy`) would need a meaningless implementation of a method that only ever applies to one `GameKey` — the same "don't widen a shared interface for one caller" reasoning ADR-0096 already used for `ScoreResult` | One concrete-class dependency for one game-specific service is simpler and costs nothing today; revisit only if a second game ever needs an equivalent asynchronous-grading formula |

## Consequences

- Positive: REQ-1305 is fully implementable against existing, already-proven
  building blocks (`IApiFootballClient`'s three-way outcome,
  `XGPredictScoringStrategy.ScorePrediction`, the kickoff-implies-lock
  simplification) — no speculative new abstraction invented beyond what the
  requirement's own acceptance criteria need.
- Positive: `Guess`, `ScoreLockingService`, `RoundCloseService`, and every
  other `GameKey`'s leaderboard path remain completely untouched — grading
  is additive, confined to COMP-15's own new service and two extended
  entities.
- Negative / trade-off accepted: `LeaderboardService`'s public read paths
  still cannot show an xG Predict round's total end-to-end after this story
  — `GetTotalPointsByInstanceIdAsync` exists at the repository level only.
  Tracked as an explicit `docs/backlog.md` follow-up (same deferred-wiring
  shape every prior xG Predict story has left behind), not silently
  resolved either way.
- Negative / trade-off accepted: the hourly cadence's worst-case API budget
  (Decision §1) is an estimate, not a guarantee, and has not been field
  verified (this sandbox cannot reach api-football.com — same standing
  caveat ADR-0094/ADR-0074's own client doc comments already carry).
  Revisit if real usage shows pressure.
- Follow-up: wire `GetTotalPointsByInstanceIdAsync` into
  `LeaderboardService`'s closed-round/windowed scopes for `"xg-predict"`,
  once that story also resolves how `GetClosedRoundsAsync`'s `ClosedAt`
  gating should read for a round whose total may still be growing (not
  decided here — flagged, not answered).
- Follow-up: `RoundSchedulingOptions`/round-generation wiring for
  `"xg-predict"` remains a separate, unstarted piece of work, unaffected by
  this ADR — `PredictGradingService`'s query (Decision §3) works without it,
  since it never reads `Round` at all.

## For AI agents

If code you are about to write would contradict this decision, stop and flag
it rather than silently working around it — either the decision needs a new
ADR that supersedes this one, or the approach needs to change.

- Do not put grading logic in `XGArcade.Core.Scoring` — it must live in
  `XGArcade.Games.XGPredict` (COMP-15), for the ADR-0003 boundary reason in
  Decision §2. `Core` must never reference `PredictMatch`/
  `PredictMatchPrediction`.
- Do not materialize a `PredictMatchPrediction` row for a user who never
  submitted one for a given match, at grading time or any other time — see
  Context's last bullet and Decision §2. This is a deliberate, permanent
  difference from `MaterializeUnansweredCellsAsync`'s ADR-0021 pattern, not
  an oversight to "fix" by copying that pattern here.
- Do not gate `Round.ClosedAt`/`RoundCloseService.CloseRoundAsync` or
  REQ-302's derived `Closed` status on grading completeness for
  `"xg-predict"` rounds. A round closing with ungraded matches remaining is
  the expected, correct behavior this ADR (and REQ-1305's own text)
  deliberately chose — do not add a completeness gate without a new ADR
  explicitly superseding this one.
- Do not re-derive the "does this match's kickoff having passed mean the
  round is locked" question with a separate `Round`/lock-instant lookup —
  Context above proves it algebraically from REQ-1301's tightest-clustering
  selection (the round lock instant is always the *minimum* of the round's
  5 kickoffs). `PredictGradingService` should never need to inject
  `IRoundRepository`.
- Do not call `IApiFootballClient.GetFixtureResultAsync` for a match whose
  `GradingStatus` is already `Graded` or `Voided` — the query in Decision
  §3 is the whole idempotency mechanism; re-deriving idempotency with a
  separate "already processed" flag would be redundant and could drift out
  of sync with `GradingStatus` itself.
- Before changing the grading job's cadence, re-derive the API-Football
  budget arithmetic in Decision §1 against the actual cron — do not assume
  a faster cadence is "safer," since REQ-1305's asynchronous, non-time-
  critical nature means slower is the side that costs nothing.
