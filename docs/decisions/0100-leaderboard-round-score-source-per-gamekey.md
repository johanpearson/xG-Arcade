# ADR-0100: `LeaderboardService` sources round totals through a per-`GameKey` `IRoundScoreSource`, not `IGuessRepository` directly

- **Status:** Accepted
- **Date:** 2026-08-31
- **Related requirements:** REQ-404, REQ-405, REQ-406, REQ-407, REQ-408,
  REQ-409, REQ-411, REQ-1304, REQ-1305
- **Related components:** COMP-02 (Leagues/Leaderboard), COMP-04
  (Core.Scoring), COMP-15 (Games.XGPredict)

## Context

Every `LeaderboardService` scope (`GetRankedMembersAsync`/`GetUserStatsAsync`
via `IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync`;
`GetActiveRoundLeaderboardAsync` via `ILiveRoundContributionService`;
`GetClosedRoundLeaderboardAsync`/`GetWindowedLeaderboardAsync` via
`IGuessRepository.GetTotalFinalPointsByRoundIdAsync`/
`GetTotalFinalPointsByRoundIdsAsync`) sources its totals from
`IGuessRepository`, unconditionally, regardless of `GameKey`. ADR-0096
already established that `"xg-predict"` never writes a `Guess` row at all —
predictions live in `PredictMatchPrediction`, and `IPredictInstanceRepository
.GetTotalPointsByInstanceIdAsync` (built in S-195, ADR-0097 Decision §2)
already computes the equivalent per-instance total, summed only over
`Graded` matches. Nothing calls it from `LeaderboardService`, so every scope
above silently returns zero `"xg-predict"` participants — not a bug in any
one method, a structural gap in all four.

ADR-0097 explicitly deferred this wiring ("Deliberately not wired into
`ILeaderboardService`/`LeaderboardEndpoints` in this story... a real,
separate piece of work with its own design questions") and flagged two
follow-ups this ADR now resolves: (1) how `LeaderboardService` reads
Predict-backed totals at all, and (2) how the live/active-round scope
should treat a `"xg-predict"` round given `PredictMatchPrediction` has no
equivalent of Grid/Path's in-progress, pre-resolution guess state.

**Why this isn't a simple `if (gameKey == "xg-predict")` branch per call
site:** `PredictMatchPrediction`/`PredictInstance` are COMP-15-owned
entities, reached only through `IPredictInstanceRepository`
(`IPredictInstanceRepository`'s own doc comment: "the only path
Games.XGPredict reaches [these] through"). `LeaderboardService` lives in
`Core.Leagues` (COMP-02). CLAUDE.md's xG Arcade/game boundary rule and
ADR-0003 forbid `XGArcade.Core` referencing a game-specific type or
repository directly — `LeaderboardService` calling
`IPredictInstanceRepository`/returning `PredictMatchPrediction` shapes
itself would be exactly that violation, not a style problem. This is the
same boundary `ScoreLockingService.MaterializeUnansweredCellsAsync`
(REQ-206/ADR-0021, `architecture-document.md` §5's round-close block)
already resolved for round-close: `Core.Scoring` resolves `Round` →
`GameKey` → owning `IGameModule` via `IGameModuleResolver`, and calls into
the game module through its own opaque interface rather than reaching into
`GridInstance`/`GridCell` itself. This ADR applies the identical shape to
leaderboard reads.

## Decision

### 1. New `IRoundScoreSource`/`IRoundScoreSourceResolver` in `Core.Scoring`

Mirrors `IScoringStrategyResolver` (ADR-0040) exactly — a per-`GameKey`
resolver over a small interface, not a fourth ad hoc resolution mechanism:

```csharp
namespace XGArcade.Core.Scoring;

public interface IRoundScoreSource
{
    // REQ-409/411: per-round totals for each requested user, across every
    // *qualifying* round for this source's GameKey(s). closedRounds is
    // every closed Round for the GameKey(s) this source serves, resolved
    // by the caller (LeaderboardService already owns IRoundRepository);
    // members carries each candidate user's IsGuest/ClaimedAt so
    // REQ-717/ADR-0036 eligibility can be applied uniformly by whichever
    // implementation needs it. A user with zero qualifying rounds is
    // absent from the result (never present with an empty list) — same
    // "absent, not defaulted" convention IGuessRepository's existing
    // method already uses.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> GetPerRoundTotalsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Round> closedRounds,
        IReadOnlyCollection<User> members,
        CancellationToken cancellationToken = default,
        bool applyGuestEligibilityRules = true);

    // REQ-406/407: the active round's current per-participant total.
    Task<IReadOnlyDictionary<Guid, int>> GetActiveRoundTotalsByUserIdAsync(
        Round activeRound, CancellationToken cancellationToken = default);

    // REQ-408: one closed round's totals.
    Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundAsync(
        Round round, CancellationToken cancellationToken = default);

    // REQ-405: totals summed across a set of closed rounds (a calendar
    // window).
    Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundsAsync(
        IReadOnlyCollection<Round> rounds, CancellationToken cancellationToken = default);
}

public interface IRoundScoreSourceResolver
{
    IRoundScoreSource Resolve(string gameKey);
}
```

Every method takes already-resolved `Round`/`User` entities (Core's own
types), never a bare `Guid roundId` the implementation would have to
re-resolve — this is what keeps the boundary intact: `LeaderboardService`
(which already injects `IRoundRepository`/`IUserRepository`) is the only
thing that ever resolves Round/User data; the resolved `IRoundScoreSource`
just reads `Round.GameInstanceId`/`Round.GameKey`/`User.IsGuest` off what
it's handed. No implementation of this interface may inject
`IRoundRepository` or `IUserRepository` itself — if one seems to need to,
that's a sign the caller should be resolving and passing more, not that
this rule should bend.

The interface's own signature must never mention `PredictInstance`/
`PredictMatchPrediction`/`PredictMatch`, or any other game-specific type —
that is the whole point of it living in `Core.Scoring`.

### 2. Two implementations, registered like `IScoringStrategy`

- **`GuessRoundScoreSource`** (`Core.Scoring`) wraps the existing
  `IGuessRepository`/`ILiveRoundContributionService` calls verbatim — no
  behavior change for `"xg-grid"`/`"xg-path"`. Registered twice (once per
  `GameKey`, `GameKey` supplied at the composition root exactly like
  `UniquenessScoringStrategy`/`ClueEfficiencyScoringStrategy` are today),
  since both existing games share this one Guess-backed implementation.
  Ignores the `closedRounds`/`members` parameters on
  `GetPerRoundTotalsByUserIdsAsync` — it still delegates straight to
  `IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync(userIds, GameKey,
  cancellationToken, applyGuestEligibilityRules)`, which already does this
  more efficiently as a single DB-side join. This is a deliberate,
  accepted asymmetry (see Alternatives), not an oversight.
- **`PredictRoundScoreSource`** (`Games.XGPredict`, COMP-15) wraps
  `IPredictInstanceRepository` only. Registered once, against
  `XGPredictGameModule.XGPredictGameKey`. This is where the actual new
  logic lives — see §3/§4 below.

`IRoundScoreSourceResolver`/`RoundScoreSourceResolver` mirrors
`ScoringStrategyResolver` exactly (`FirstOrDefault(s => ...)` won't work
directly since `IRoundScoreSource` carries no `GameKey` property of its
own — the resolver is constructed with an
`IReadOnlyDictionary<string, IRoundScoreSource>` built at the composition
root instead, keyed by every `GameKey` each registered source serves,
which is what lets one `GuessRoundScoreSource` instance answer for two
keys without the interface itself needing to expose one).

`LeaderboardService`'s four call sites each replace their current
`IGuessRepository`/`ILiveRoundContributionService` call with
`roundScoreSourceResolver.Resolve(gameKey)` + the matching method above.
`ILiveRoundContributionService` itself is unchanged and stays exactly what
`GuessRoundScoreSource.GetActiveRoundTotalsByUserIdAsync` delegates to for
Guess-backed games — it is not touched or widened by this ADR.

### 3. `IPredictInstanceRepository` gains one new read: participation, separate from graded points

`GetTotalPointsByInstanceIdAsync` (already built) sums `FinalPoints` over
`Graded` matches only — a user with predictions but nothing graded yet is
**absent** from its result, not present with 0. That is exactly right for
"how many points has this round earned so far" (REQ-1305's own contract),
but wrong for REQ-409's "qualifying round" test if reused unchanged: a
closed `"xg-predict"` round where a user predicted all 5 matches but
grading hasn't run yet must still count as one of that user's qualifying
rounds (contributing 0, a real value, the same way `GetTotalsByRoundAsync`
already would once wired) — it must not silently fail to qualify, and
must not silently vanish once it does, only to have its value jump later
as grading catches up. `GetTotalPointsByInstanceIdAsync`'s absent-key
semantics can't distinguish "never predicted" from "predicted, ungraded"
for this purpose.

New method:

```csharp
// REQ-409: every user who submitted >=1 prediction for this instance,
// regardless of grading state — participation, not points. Used only to
// decide qualifying-round membership; PredictRoundScoreSource pairs this
// with GetTotalPointsByInstanceIdAsync (defaulting to 0 for a
// participant with nothing graded yet) to build each qualifying round's
// contributed value.
Task<IReadOnlyCollection<Guid>> GetParticipantUserIdsByInstanceIdAsync(
    Guid predictInstanceId, CancellationToken cancellationToken = default);
```

`PredictRoundScoreSource.GetPerRoundTotalsByUserIdsAsync` then, per closed
round in `closedRounds` whose `GameKey == "xg-predict"`: fetches
participants + graded totals for that round's instance, and for each
requested `userId` that is a participant, appends `gradedTotals
.GetValueOrDefault(userId, 0)` to that user's list — REQ-717/ADR-0036
eligibility (`members`' `IsGuest`/`ClaimedAt` vs. `round.ClosedAt`) is
applied the same way `IGuessRepository`'s own query already does it,
just in memory here instead of in SQL. A round with zero participants (no
one predicted at all) contributes nothing to anyone, same as any other
scope's "absent, not defaulted" rule.

### 4. Active/live round scope: same graded-so-far total, not a separate "live" formula, not exclusion

`GetActiveRoundTotalsByUserIdAsync` for `"xg-predict"` calls the exact
same query `GetTotalsByRoundAsync` does
(`GetTotalPointsByInstanceIdAsync(activeRound.GameInstanceId)`) — there is
no separate "live, in-progress" formula to build. Reasoning:

- ADR-0097 Decision §4 already answered the general question for this
  platform ("the leaderboard shows a partial, growing total, never a
  withheld one") without scoping that answer to closed rounds only —
  excluding `"xg-predict"` from the active-round scope specifically would
  reintroduce exactly the per-scope inconsistency ADR-0097 was written to
  head off, for no REQ-driven reason.
- `ILiveRoundContributionService`'s live-estimate concept
  (`ScoringRules.MaxPointsPerCell` for a zero-guess cell, a genuinely
  in-progress unresolved guess contributing nothing, etc.) exists because
  a Grid/Path guess has a real "attempted but not yet resolved" state
  during play. A `PredictMatchPrediction` has no equivalent third state —
  it is either not submitted (contributes nothing, forever, per ADR-0097),
  submitted-but-`Pending`-match (contributes nothing *yet*, per ADR-0097's
  own "no materialized placeholder" rule), or submitted with a `Graded`
  match (contributes `FinalPoints`). That is precisely what
  `GetTotalPointsByInstanceIdAsync` already returns — there is nothing
  left to model.
- A round being `Round`-active (not yet closed) is orthogonal to whether
  its matches are graded (ADR-0097 Decision §4: fully decoupled) — an
  active `"xg-predict"` round can have zero, some, or all of its matches
  graded already, exactly like a closed one. Using the same read for both
  scopes is the accurate reflection of that, not a shortcut.

### 5. `IRoundRepository.GetClosedIdsWithinWindowAsync` widens from ids-only to `(Round Id, GameInstanceId)`-shaped data

Its own doc comment currently justifies ids-only on "callers only ever
feed the result straight into
`IGuessRepository.GetTotalFinalPointsByRoundIdsAsync`" — that assumption
is exactly what this story breaks: `GetTotalsByRoundsAsync` needs each
round's `GameInstanceId` too when the resolved source is
`PredictRoundScoreSource`. Change its return shape to the full `Round`
(reusing the same shape `GetClosedByGameKeyAsync` already returns,
removing the asymmetry) rather than inventing a narrower projection type —
`Round` is a small, already-loaded-everywhere entity; there is no
performance case here for keeping the narrower shape now that a second
caller needs more of it.

### 6. Median-ranking's `closedRounds`/`members` inputs

`GetRankedMembersAsync`/`GetUserStatsAsync` must fetch "every closed round
for this `GameKey`" once, up front, before calling
`GetPerRoundTotalsByUserIdsAsync` — reuse the existing
`IRoundRepository.GetClosedByGameKeyAsync(gameKey, 0, take)` (its own
paginated shape, called with a large enough `take` for MVP scale; no new
repository method) rather than adding an "unwindowed, all closed rounds"
query. `GuessRoundScoreSource` ignores this list entirely (§2); only
`PredictRoundScoreSource` reads it. Paying for this fetch on every
`GameKey`, including the two that don't need it, is an accepted, small
cost for keeping the interface uniform (see Alternatives) — not something
to special-case away by branching on `GameKey` in `LeaderboardService`
itself, which is the exact per-call-site branching this ADR exists to
avoid.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Plain `if (gameKey == "xg-predict")` branch inline at each of the four call sites | No new interface/type | Four call sites, not one — the same duplication `RankByTotalPoints`'s own extraction (ADR-0095's quality-gate follow-up) already had to fix once; would also force `LeaderboardService`/`Core.Leagues` to reference `IPredictInstanceRepository`/`PredictMatchPrediction` directly, violating ADR-0003/CLAUDE.md's game boundary rule outright, not just stylistically | Structurally forbidden (boundary), and duplicative even if it weren't |
| Widen `IScoringStrategy` (already resolved per `GameKey`, already injected into `LeaderboardService`) to also own round-total sourcing | Reuses an existing resolver, no new interface | `IScoringStrategy`'s existing job (scoring formula + `LowerIsBetter` sort direction, ADR-0040/ADR-0095) is a different concern from "where do totals live" — `UniquenessScoringStrategy`/`ClueEfficiencyScoringStrategy` have no natural implementation for "fetch totals," and widening a shared interface for one caller's need is exactly what ADR-0096's own alternatives table already rejected for `ScoreResult` | Same "don't widen a shared interface speculatively" reasoning already established twice in this codebase (ADR-0096, ADR-0097) |
| Have `PredictRoundScoreSource` inject `IRoundRepository`/`IUserRepository` itself and resolve what it needs internally | Simpler-looking interface (no `closedRounds`/`members` parameters) | Inverts the established boundary direction: every prior case of Core/game data crossing this line (`ScoreLockingService.MaterializeUnansweredCellsAsync`, this ADR's own §1) has Core resolve Core-owned data and hand it down, never the game module reaching up into Core repositories on its own; would also make `PredictRoundScoreSource`'s dependency graph inconsistent with every other COMP-15 class, which only ever depends on `IPredictInstanceRepository`/`XGPredictScoringStrategy`/COMP-07 clients | Breaks a consistently-applied direction for a small ergonomic gain |
| Give `GuessRoundScoreSource` a real, non-degenerate use for `closedRounds`/`members` too (replace its `IGuessRepository` call with the same in-memory approach `PredictRoundScoreSource` uses) | Perfectly symmetric interface, no unused parameters | Throws away a working, tested, single-query DB-side join in favor of a slower in-memory recomputation, for symmetry's own sake, on the two games that don't need this story's fix at all | Not warranted — "don't rework a working system beyond what's actually needed" (this repo's own recurring principle, e.g. ADR-0095's alternatives table) |

## Consequences

- Positive: all four `LeaderboardService` scopes correctly source
  `"xg-predict"` totals once real rounds exist, closing the gap S-193/
  S-195/S-197/S-198 each flagged and left open.
- Positive: `Core.Leagues` gains zero compile-time knowledge of
  `PredictInstance`/`PredictMatchPrediction` — the boundary this ADR
  exists to protect holds structurally (enforced by which project the
  concrete type lives in), not just by convention.
- Positive: `"xg-grid"`/`"xg-path"` behavior is unchanged —
  `GuessRoundScoreSource` is a thin pass-through to the same, already-
  tested `IGuessRepository`/`ILiveRoundContributionService` calls
  `LeaderboardService` makes today.
- Negative / trade-off accepted: `GetPerRoundTotalsByUserIdsAsync`'s
  `closedRounds`/`members` parameters are dead weight for
  `GuessRoundScoreSource` — an unused-but-uniform interface, not a fully
  symmetric one. Flagged explicitly here rather than silently accepted.
- Negative / trade-off accepted: `PredictRoundScoreSource`'s per-round
  reads are N+1-shaped (one `GetTotalPointsByInstanceIdAsync`/
  `GetParticipantUserIdsByInstanceIdAsync` pair per closed round in scope)
  rather than a single joined query — acceptable at Tier-0/MVP scale (a
  gameweek-cadence game has very few closed rounds ever), revisit only if
  real usage shows this mattering.
- Follow-up: once round-generation/grading have run in production long
  enough for real `"xg-predict"` median-ranking data to exist, re-verify
  the REQ-409 five-round qualification floor still reads sensibly for a
  once-a-week-cadence game (a existing, unrelated MVP-SCOPE concern, not
  new to this ADR).

## For AI agents

- `LeaderboardService` (and anything else in `Core.Leagues`/`Core.Scoring`)
  must never reference `IPredictInstanceRepository`, `PredictInstance`,
  `PredictMatch`, or `PredictMatchPrediction` directly — always through
  the resolved `IRoundScoreSource`. If a future change seems to need that,
  stop and flag it rather than adding the reference.
- Do not add a third `IRoundScoreSource` implementation without first
  checking whether the new game actually writes `Guess` rows
  (`GuessRoundScoreSource` already covers that case) or needs its own,
  the same one-question test ADR-0096's own precedent already applies to
  entity-shape decisions.
- Do not let `PredictRoundScoreSource` (or any future game-owned
  `IRoundScoreSource` implementation) inject `IRoundRepository` or
  `IUserRepository` — all Round/User data it needs must be handed to it
  by the caller. This mirrors `ScoreLockingService`'s established
  direction and is not optional per-implementation.
- Do not reuse `GetTotalPointsByInstanceIdAsync`'s absent-key semantics as
  a stand-in for "did this user participate in this round" — it means
  "has at least one graded point," not "predicted at all." Use
  `GetParticipantUserIdsByInstanceIdAsync` for qualification checks.
- Do not build a separate "live, in-progress" formula for `"xg-predict"`'s
  active-round leaderboard scope — §4 above is the deliberate, final
  answer (same read as the closed-round scope), not a placeholder.
