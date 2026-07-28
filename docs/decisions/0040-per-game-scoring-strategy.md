# ADR-0040: Core.Scoring resolves a scoring strategy per GameKey

- **Status:** Accepted — this ADR's own Follow-up note (the `IScoringStrategy`
  parameter shape left unfixed) was resolved 2026-07-28 (S-083,
  `ClueEfficiencyScoringStrategy`); see ADR-0049 for the resulting decision
- **Date:** 2026-07-26
- **Related requirements:** REQ-204, REQ-205, REQ-1201-REQ-1206 (xG Path)
- **Related components:** COMP-04 (Core.Scoring), COMP-05 (Games.XGGrid), COMP-11 (Games.XGPath)

## Context

`ScoreLockingService.LockRoundScoresAsync` (`XGArcade.Core.Scoring`) today
applies exactly one formula to every correct guess in a round, regardless of
`Round.GameKey`: `UniquenessCalculator.Calculate` followed by
`ScoringRules.PointsFromUniqueScore` (ADR-0020, ADR-0021). This was never
written as "xG Grid's formula" — it was written as "the formula," because
xG Grid was the only game. "Uniqueness of the correct answer among
guessers" is a meaningful signal only when a cell has more than one valid
correct player (true for a Country×Club/Trophy cell); it has no signal when
a cell has exactly one correct player and every solver necessarily names the
same person, which is xG Path's puzzle shape (a specific target player,
guessed from a revealed career path). xG Path's natural scoring currency is
instead clue efficiency: how few reveals were needed before a correct
guess, still golf-style per ADR-0021 (fewer clues = fewer points).

Adding this second game is the concrete case ADR-0002's own follow-up
("use it as a test of whether the `IGameModule` boundary actually holds in
practice") anticipated for `IGameModule` itself — this ADR is the same test
applied to `Core.Scoring`, which turns out to have its own hidden
single-game assumption that `IGameModule` alone doesn't cover.

## Decision

`Core.Scoring` gains an `IScoringStrategy` abstraction, resolved by
`GameKey` through a new `IScoringStrategyResolver` — the same resolution
shape `IGameModuleResolver` already establishes for game logic. Each
strategy computes `FinalUniquenessScore` (nullable — a strategy may have no
uniqueness concept at all, as xG Path's won't) and `FinalPoints` for a
correct guess, given whatever the owning game module can report about that
guess. `ScoreLockingService.LockRoundScoresAsync` calls the resolved
strategy instead of calling `UniquenessCalculator`/`ScoringRules` directly.

xG Grid's existing formula becomes the first concrete implementation
(`UniquenessScoringStrategy`), wrapping `UniquenessCalculator.Calculate` +
`ScoringRules.PointsFromUniqueScore` unchanged — this is a pure extraction,
not a formula change, and every existing REQ-204/205 acceptance criterion
still holds for xG Grid. xG Path gets a second implementation
(`ClueEfficiencyScoringStrategy`, REQ-1205) computing
`round(cluesUsed / maxCluesForCell * MaxPointsPerCell)`, still bounded by
the same `ScoringRules.MaxPointsPerCell`/"unanswered scores worst-case"
convention ADR-0021 already established — no new points scale, just a new
formula feeding it.

The unanswered-cell penalty (`MaterializeUnansweredCellsAsync`,
ADR-0021) and the incorrect-guess-scores-worst rule are unaffected — they
run before any strategy is consulted and stay strategy-agnostic
(`FinalPoints = MaxPointsPerCell`, `FinalUniquenessScore = null`).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Add an `if (gameKey == "xg-path")` branch inline in `ScoreLockingService` | Smallest immediate diff | Reintroduces the exact problem this ADR fixes for the *next* game after xG Path; `Core.Scoring` grows compile-time knowledge of every game, same anti-pattern ADR-0003 rejected for `Round` | Doesn't scale past two games, contradicts the platform-above-games principle elsewhere in this codebase |
| Push scoring entirely into `IGameModule.ScoreSubmissionAsync` (each game computes its own `FinalPoints` directly) | No new resolver type | `ScoreResult` is returned at guess-submission time, before the round closes and the full correct-guess population is known — uniqueness-style formulas need that population, so scoring can't fully happen there | Wrong point in the lifecycle for any formula that depends on other players' guesses |
| Per-`GameKey` scoring strategy resolved like `IGameModuleResolver` (chosen) | Mirrors an already-proven pattern in this codebase; `Core.Scoring` gains zero compile-time knowledge of any specific game; a third game adds a new strategy, touches no existing one | One more resolver/interface pair to maintain | Best fit: consistent with how the codebase already solved this exact shape of problem for game logic |

## Consequences

- Positive: adding a game's scoring model never requires editing another
  game's strategy or `ScoreLockingService`'s control flow; xG Grid's formula
  is verified unchanged by the extraction (same tests, same inputs/outputs)
- Negative / trade-offs accepted: one more indirection to follow when
  reading `ScoreLockingService`; `FinalUniquenessScore` is now nullable for
  a reason beyond "not yet computed" (a strategy may define no such concept
  at all) — callers reading it must not assume "null" only ever means
  "round still open"
- Follow-up: `IScoringStrategy` needs a way to receive "clues used" for a
  guess (xG Path) versus "the cell's other correct guesses" (xG Grid) —
  the exact parameter shape is an implementation detail for the xG Path
  build, not fixed by this ADR; see ADR-0041 for the related attempt-cap
  change this depends on. If a third game needs a third fundamentally
  different input shape, revisit whether `IScoringStrategy` still fits or
  needs to become game-instance-aware rather than just `GameKey`-aware.

## For AI agents

Never add a `GameKey`/game-name conditional branch inside
`ScoreLockingService` or anywhere else in `Core.Scoring` to special-case a
game's scoring. If a new game needs different scoring, add a new
`IScoringStrategy` implementation and register it against that game's
`GameKey` — the same way a new `IGameModule` is registered, never by
branching on the key value inline.
