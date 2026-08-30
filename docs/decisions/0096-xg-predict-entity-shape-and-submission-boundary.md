# ADR-0096: xG Predict round/match/prediction entity shape and submission-path boundary

- **Status:** Accepted
- **Date:** 2026-08-30
- **Related requirements:** REQ-1301, REQ-1302, REQ-1303
- **Related components:** COMP-15 (Games.XGPredict), COMP-04 (Core.Scoring), COMP-07 (DataSync.Clients)

## Context

S-190/S-191 scaffolded xG Predict (COMP-15) and its API-Football client but
deliberately left one thing undecided, flagged back rather than invented
(see `XGPredictGameModule`'s own doc comment and `architecture-document.md`'s
COMP-15 row): what entities `GenerateInstanceAsync`/`ScoreSubmissionAsync`
(REQ-1301/1302/1303) actually persist to. This ADR follows ADR-0045's own
precedent (xG Path's entity-shape ADR) for the same kind of decision.

Three things needed a decision that could reasonably have gone another way:

1. **Round/match/cell shape.** Neither existing precedent fits. `GridCell`
   (COMP-05) has no fixed answer — it's two category constraints checked
   dynamically at guess time. `PathPuzzle` (COMP-11, ADR-0045) has exactly
   one fixed answer (`TargetPlayerId`), checked by direct comparison. An xG
   Predict "cell" is a fixed real-world match (two teams, a kickoff time, an
   external fixture id) with no "answer" at all until graded, asynchronously,
   sometime after the round locks (REQ-1305, a separate story) — categorically
   different from both.

2. **Where a submitted prediction is stored.** The obvious reuse candidate is
   `Guess` (COMP-04/Core.Scoring) — the table `GridGameModule` and
   `XGPathGameModule` both already write through via `GuessSubmissionService`.
   `Guess`'s own doc comment already flags it as "an accepted v1
   simplification... generalize... if/when a second game module needs a
   different submission shape" — but that generalization never happened when
   xG Path was added, because `Guess`'s shape (`SubmittedName` string,
   `AttemptCount` capped, `IsCorrect` known synchronously) happened to fit xG
   Path too. It does not fit xG Predict: REQ-1302 is two non-negative
   integers, not a name string; explicitly *no* attempt cap (unlimited
   resubmission before lock); and correctness is not knowable at submission
   time at all (REQ-1304/1305, a later story) — `Guess.IsCorrect`/
   `PlayerAnswerId` have nothing to be set to yet.

3. **How `IGameModule.ScoreSubmissionAsync`'s existing contract
   (`object submission` in, `ScoreResult` out) accommodates a game whose
   submission is neither a name lookup nor synchronously gradable.**
   `architecture-document.md` §6.11 already flags this exact question as
   unresolved in the scaffolding session ("REQ-1302's two-integer... shape
   doesn't obviously fit the existing `GuessSubmission(CellId, SubmittedName,
   ChosenPlayerId)` record... not decided in this scaffold"). This story
   (REQ-1301/1302/1303) implements the `IGameModule` methods themselves, but
   deliberately does **not** wire a real submission endpoint or a
   `GuessSubmissionService`-equivalent caller (that remains a follow-up
   story, same as round-generation endpoint wiring, ADR-0051's own
   precedent) — so this ADR's decision on the method contract only needs to
   make the methods themselves correct and testable in isolation, not decide
   the eventual HTTP-facing shape.

## Decision

1. **New entities `PredictTemplate`/`PredictInstance`/`PredictMatch`**,
   mirroring `GridTemplate`/`GridInstance`/`GridCell` and
   `PathTemplate`/`PathInstance`/`PathPuzzle`'s shape exactly (surrogate
   `Guid` ids, `PredictInstance.Matches` an owned collection cascade-deleted
   with its parent). `PredictMatch.Id` is the opaque "cell id"
   `IGameModule.GetCellIdsAsync` returns, same contract `GridCell.Id`/
   `PathPuzzle.Id` already fulfill. `PredictTemplate.MatchCount` mirrors
   `PathTemplate.PuzzleCount`'s shape (a stored config field) even though
   REQ-1301 currently only ever specifies 5 — same "config now, even if only
   one value is valid yet" precedent `PathTemplate` already sets.
   `PredictMatch` carries `ExternalFixtureId` (int, API-Football's own id —
   REQ-1305's future grading lookup key), `HomeTeamName`/`AwayTeamName`
   (string, display data), and `KickoffUtc` (`DateTime`) — enough to
   reconstruct REQ-1303's round-lock instant (`Matches.Min(m => m.KickoffUtc)`)
   without a second fetch.

2. **A new, separate entity `PredictMatchPrediction`** (COMP-15's own table,
   *not* `Guess`/Core.Scoring) holds one player's stored prediction for one
   match: `PredictMatchId` (real FK to `PredictMatch`, cascade — both tables
   are COMP-15-internal, so this is the same "no boundary reason to leave
   unconstrained" case `GridCell.GridInstanceId`/`PathPuzzle.PathInstanceId`
   already are, not `Guess.CellId`'s deliberately-opaque cross-game case),
   `UserId` (nullable, unconstrained — mirrors `Guess.UserId`'s own shape so
   REQ-710 account-deletion anonymization has an identical, already-proven
   path to reuse later), `HomeGoals`/`AwayGoals` (int), `CreatedAt`. Unique
   index on `(PredictMatchId, UserId)` — REQ-1302's "resubmission replaces,
   never inserts a second row," same precedent `Guess`'s own
   `(RoundId, UserId, CellId)` unique index already sets. Not owned by
   `PredictMatch` (no cascade-with-parent list) because, unlike a match's own
   static fields, predictions accumulate independently over the round's open
   window from many different users — the same reason `Guess` is a top-level
   table rather than an owned collection of `Round`.

3. **A new Core-owned DTO `PredictionSubmission(Guid CellId, int HomeGoals,
   int AwayGoals)`**, living in `XGArcade.Core.Games` alongside
   `GuessSubmission`/`ScoreResult` — not inside `Games.XGPredict`. Reasoning:
   `GuessSubmission` lives in Core specifically so a Core-side caller
   (`GuessSubmissionService`) can construct the concrete submission object
   without depending on any specific game's own project (ADR-0003's
   boundary: Core never references a game-specific type). The same
   constraint will apply to whatever Core-side service eventually calls
   `XGPredictGameModule.ScoreSubmissionAsync` in a follow-up story — placing
   `PredictionSubmission` in `Core.Games` now, even though nothing in Core
   constructs one yet, keeps that boundary available rather than requiring a
   later move.

4. **`ScoreSubmissionAsync`'s return/exception contract, explicitly a known
   compromise, not a full solution:**
   - Round-not-found / match-not-found in the instance: throws
     `PredictScoringException`, mirroring `PathScoringException`'s/
     `GuessScoringException`'s existing "not found" convention.
   - Submission after the round-level lock (REQ-1303, computed as
     `Matches.Min(m => m.KickoffUtc)` compared against an injectable
     `TimeProvider`, mirroring `XGPathGameModule`'s own `_timeProvider`
     field so tests can pin "now"): throws a new, distinguishable
     `PredictRoundLockedException`. No Core-side caller catches this yet
     (none exists — see Context §3) but the type exists now so the
     follow-up wiring story has a loud, specific signal to catch instead of
     inventing one under time pressure, the same "flag it now, resolve the
     mapping later" discipline ADR-0011/ADR-0057 already used for
     `LiveLookupUnavailableException`.
   - A successful store (before lock, valid two-integer prediction) returns
     `ScoreResult { IsCorrect = false, PlayerAnswerId = null }`. This is a
     **known, deliberate misfit**: `IsCorrect = false` does not mean "wrong"
     here — it means "accepted, not yet gradable" — and is
     indistinguishable, at the `ScoreResult` level, from Grid/Path's real
     "wrong guess" case. `ScoreResult`'s three fields (`IsCorrect`/
     `PlayerAnswerId`/`DisambiguationCandidates`) have no field for
     "accepted, pending" because no game needed one before now. This ADR
     does **not** resolve that mismatch (widening `ScoreResult`, or giving
     xG Predict a parallel interface, are both live options) — it is
     explicitly left to whichever follow-up story builds the real
     submission endpoint and needs a caller to actually interpret this
     value. Validation failure (non-integer/negative/missing goal count,
     REQ-1302) is the caller's job in this story's scope: `ScoreSubmissionAsync`
     itself still validates the two-integer shape (defensive — the DTO's own
     `int` fields already rule out non-numeric/missing at the C# type level,
     but negative values need an explicit check) and throws
     `PredictScoringException` for an invalid pair, since there is no
     `GuessSubmissionOutcome`-style rejection channel available to it yet
     either.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Reuse `Guess` for predictions, adding nullable `HomeGoals`/`AwayGoals` columns alongside the existing string/attempt-cap columns | One fewer table; reuses `GuessSubmissionService`'s existing plumbing | `Guess.SubmittedName` (required), `AttemptCount` (capped, REQ-1302 explicitly rules a cap out), and `IsCorrect` (assumed knowable synchronously) all actively contradict xG Predict's shape; every reader of `Guess` (uniqueness scoring, leaderboard, REQ-216) would need new "is this a Grid/Path row or a Predict row" branching it doesn't have today | Forcing a mismatched shape into a shared table for reuse's sake is exactly what ADR-0045's own precedent (and `Guess`'s own doc comment) warns against; a new table costs one migration, not a new branch in every existing reader |
| No entity shape decision yet — implement `GenerateInstanceAsync`/`ScoreSubmissionAsync` against ad hoc in-memory state, defer real persistence to the endpoint-wiring story | Avoids committing to a schema before the submission endpoint's real shape is known | REQ-1302 explicitly requires *stored/updated* predictions ("a stored prediction to be graded once that match finishes") — an in-memory implementation can't satisfy its own acceptance criteria, and would have to be thrown away, not extended, once real persistence is added | The requirement itself demands persistence now; deferring only the *entity shape* (this ADR's actual job) makes sense, deferring persistence entirely does not |
| Widen `IGameModule.ScoreSubmissionAsync`'s `ScoreResult` return type now (e.g. add an `Accepted`/`Pending` discriminator) to properly represent "stored, not yet gradable" | Fixes the known `IsCorrect=false` misfit immediately, once, for whoever wires the endpoint later | Touches every existing `IGameModule` implementation's return sites and every existing `ScoreResult` reader (`GuessSubmissionService`, tests) for a shape only one caller (not yet built) will ever consume differently; REQ-1304 (real scoring) isn't built yet either, so the "right" shape for how xG Predict eventually reports back isn't actually knowable now | Speculative widening for a caller that doesn't exist yet, on a shared interface every other game also implements — exactly the kind of decision `MVP-SCOPE.md`/CLAUDE.md ask to defer until a real caller makes the actual need concrete |

## Consequences

- Positive: `GenerateInstanceAsync`/`ScoreSubmissionAsync` (REQ-1301/1302/1303)
  can be implemented and unit-tested against a real, persisted schema in this
  story, without waiting on the submission-endpoint story's own design work.
- Positive: `Guess`/`GuessSubmissionService` stay completely unmodified — no
  new branching for a shape they were never designed to carry, and no risk
  to xG Grid/xG Path's existing behavior.
- Negative / trade-off accepted: `ScoreResult { IsCorrect = false }` for a
  successful prediction store is misleading if any future code path reads it
  the way Grid/Path readers do ("false" = "wrong"). Flagged loudly in this
  ADR and in `ScoreSubmissionAsync`'s own doc comment; must be resolved
  properly (not copied blindly) by whoever builds the real submission
  endpoint.
- Negative / trade-off accepted: `PredictRoundLockedException` has no
  catcher yet — a real caller must be added before REQ-1303's rejection is
  observable outside a unit test. This mirrors REQ-1301's own
  `GenerateInstanceAsync` being unreachable in production until its own
  follow-up wiring story, an already-accepted shape for this story
  (CLAUDE.md/this story's own scope: "do not wire this into
  `InternalRoundEndpoints`... yet").
- Follow-up: the submission-endpoint story must decide (a) the actual HTTP
  shape (`architecture-document.md` §6.11's own open question: reuse
  `POST /rounds/{roundId}/cells/{cellId}/guesses` with a new submission
  variant, or a dedicated `xg-predict`-only endpoint), (b) how it catches
  and maps `PredictRoundLockedException`/`PredictScoringException` to a
  rejection outcome, and (c) whether `ScoreResult` needs widening or xG
  Predict needs a parallel, non-`IGameModule.ScoreSubmissionAsync` entry
  point instead. All three are explicitly open, not implied answers here.

## For AI agents

Do not route xG Predict predictions through `Guess`/`GuessSubmissionService`
— they are structurally incompatible (string vs. two-integer, capped vs.
uncapped, synchronous vs. asynchronous correctness), not merely
inconvenient. Do not treat `ScoreResult.IsCorrect = false` returned by
`XGPredictGameModule.ScoreSubmissionAsync` as "the prediction was wrong" —
correctness for this game does not exist until REQ-1304/1305's grading
runs; `false` here only ever means "not graded yet." Do not wire
`XGPredictGameModule` into `InternalRoundEndpoints`, `GuessSubmissionService`,
or any real HTTP endpoint as part of implementing this ADR — that is a
separate, later story by this story's own explicit scope (mirrors
ADR-0051's precedent for deferred scheduling-config wiring). If a future
change needs `PredictMatchPrediction` to carry more per-user state (e.g. a
"confirmed and locked" flag for REQ-1306), add it there — do not resurrect
the "reuse `Guess`" option already rejected above.
