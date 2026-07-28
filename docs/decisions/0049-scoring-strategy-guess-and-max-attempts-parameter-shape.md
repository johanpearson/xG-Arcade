# ADR-0049: IScoringStrategy's parameter shape is Guess + per-cell max-attempts, not a new IGameModule dependency

- **Status:** Accepted
- **Date:** 2026-07-28
- **Related requirements:** REQ-1206
- **Related components:** COMP-04 (Core.Scoring), COMP-05 (Games.XGGrid), COMP-11 (Games.XGPath)

## Context

ADR-0040 introduced `IScoringStrategy`/`IScoringStrategyResolver` so
`Core.Scoring` could support more than one game's scoring formula, but
deliberately left one question open in its own **Follow-up** note:
`IScoringStrategy` needs a way to receive whatever game-specific input a
strategy actually needs — xG Grid's "the cell's other correct guesses"
for its uniqueness formula versus xG Path's "clues used before the
correct guess" for its clue-efficiency formula (REQ-1206) — and "the
exact parameter shape is an implementation detail for the xG Path build,
not fixed by this ADR."

Building `ClueEfficiencyScoringStrategy` (S-083) required resolving this
for real. Two inputs were needed that the original
`ScoreCorrectGuess(IReadOnlyCollection<Guess> correctGuessesForCell, Guid
myAnswerPlayerId)` signature didn't carry: which specific `Guess` is being
scored (to read its `AttemptCount`, standing in for `cluesUsed` — see
below) and the cell's max-attempts value (`maxCluesForThisPuzzle` in
REQ-1206's formula), which ADR-0041 already made available through
`IGameModule.GetMaxAttemptsForCellAsync` for an entirely different reason
(the per-cell attempt cap enforced at guess-submission time).

## Decision

`IScoringStrategy.ScoreCorrectGuess` changes from
`(IReadOnlyCollection<Guess> correctGuessesForCell, Guid
myAnswerPlayerId)` to `(Guess guess, IReadOnlyCollection<Guess>
correctGuessesForCell, int maxAttemptsForCell)`.

- `guess` is the specific correct `Guess` row being scored (always
  `IsCorrect == true` — `ScoreLockingService` never calls this method for
  an incorrect or synthesized-unanswered guess). It replaces the bare
  `myAnswerPlayerId` parameter; `UniquenessScoringStrategy` now reads
  `guess.PlayerAnswerId` instead. For xG Path, `ClueEfficiencyScoringStrategy`
  reads `guess.AttemptCount` as `cluesUsed` — not a new column: xG Path
  maintains exactly one `Guess` row per (round, user, cell), incrementing
  `AttemptCount` by 1 per submission, so a correct guess's `AttemptCount`
  at the moment it's submitted already equals the number of clues that had
  been revealed.
- `maxAttemptsForCell` is a plain `int`, resolved once per cell (not once
  per guess) by `ScoreLockingService` itself, via the already-existing
  `IGameModule.GetMaxAttemptsForCellAsync` (ADR-0041), before any strategy
  is invoked for that round. `IScoringStrategy` implementations never call
  `IGameModule` themselves — they receive the precomputed value as plain
  data.
- `correctGuessesForCell` is unchanged from ADR-0040's original shape.

`IScoringStrategy` gains zero new compile-time dependency on
`IGameModule`/`Core.Games` from this change — it stays plain-data-in, the
same discipline ADR-0040 established. `UniquenessScoringStrategy` ignores
`maxAttemptsForCell` (xG Grid's attempt cap has no bearing on its
formula); `ClueEfficiencyScoringStrategy` ignores `correctGuessesForCell`
(xG Path has no uniqueness concept).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Give `IScoringStrategy` a direct dependency on `IGameModule`, so each strategy calls `GetMaxAttemptsForCellAsync` itself | No new parameter on `ScoreCorrectGuess`; each strategy fetches only what it needs | Every future strategy implementation — including `UniquenessScoringStrategy`, which has no use for the cap — would carry a dependency on a different component's interface; `Core.Scoring` strategies would gain an async-call responsibility (and a `CancellationToken`/instance-id threading requirement) they don't otherwise need; duplicates the lookup per strategy instead of once per cell | Rejected — leaks a game-module dependency into an abstraction ADR-0040 deliberately kept plain-data-in, for no real benefit over resolving it once in `ScoreLockingService` |
| Add a new `Guess.CluesUsed` column, duplicating `AttemptCount` | Makes the "clues used" concept explicit by name on the entity | Needless duplication of a value (`AttemptCount`) that is already exactly right for this purpose — two columns that must always agree, with no mechanism enforcing that beyond code discipline | Rejected — `AttemptCount` already means exactly this for a winning xG Path guess; a second column adds drift risk with no new information |
| Whole `Guess` + plain precomputed `int maxAttemptsForCell` (chosen) | `IScoringStrategy` stays free of any `Core.Games` dependency; `ScoreLockingService` computes the per-cell lookup once, not once per guess; no new `Guess` column; mirrors ADR-0041's own "resolve once, pass as plain data" shape | Every strategy receives the full `Guess` and `maxAttemptsForCell` regardless of whether it uses them (today, `UniquenessScoringStrategy` ignores the latter, `ClueEfficiencyScoringStrategy` ignores `correctGuessesForCell`) | Accepted — the "everyone gets the same shape" cost is small and already an accepted trade-off pattern (ADR-0041 accepted the same shape for `IGameModule.GetMaxAttemptsForCellAsync` itself) |

## Consequences

- Positive: `IScoringStrategy` still has zero compile-time knowledge of
  `IGameModule`/`Core.Games` — the boundary ADR-0040 established is
  preserved, not eroded by this second game's real requirements.
  `ScoreLockingService` resolves `maxAttemptsForCell` exactly once per cell
  present in the round's correct-guess population, not once per guess,
  avoiding an avoidable per-guess cost. No new `Guess` column — xG Path's
  `cluesUsed` reuses `AttemptCount`, a value that already means exactly
  the right thing.
- Negative / trade-offs accepted: every `IScoringStrategy` implementation
  receives the same three-parameter shape regardless of whether it needs
  all of it — today, `UniquenessScoringStrategy` ignores
  `maxAttemptsForCell` and `ClueEfficiencyScoringStrategy` ignores
  `correctGuessesForCell`. This is the same "one shared shape, not every
  implementation minimal" trade-off ADR-0041 already accepted for
  `IGameModule.GetMaxAttemptsForCellAsync` (xG Grid returns a fixed `2`
  unconditionally, never branching on the cell it's asked about).
- Follow-up: if a third game's scoring formula needs an input this shape
  genuinely can't express (not just "an input this strategy will ignore"),
  that is the trigger to revisit this ADR with real evidence — not to
  quietly add a `GameKey` branch inside `ScoreLockingService` or bolt a new
  bare parameter onto every strategy for one game's sake.

## For AI agents

Do not give `IScoringStrategy` a direct dependency on `IGameModule` (or
any other `Core.Games` type) to solve a future game's input needs — extend
the plain-data parameter shape on `ScoreCorrectGuess` instead, resolving
any new per-cell/per-instance value once in `ScoreLockingService` and
passing it in as plain data, the same way `maxAttemptsForCell` is resolved
here. If a third game's scoring needs genuinely don't fit this shape at
all, that's the signal to revisit this ADR explicitly, not to quietly add
a game-specific branch inside `Core.Scoring`.
