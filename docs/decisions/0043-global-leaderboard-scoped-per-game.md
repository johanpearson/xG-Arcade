# ADR-0043: Global League's all-time ranking is scoped per GameKey

- **Status:** Accepted
- **Date:** 2026-07-26
- **Related requirements:** REQ-401, REQ-404, REQ-409, REQ-410 (new)
- **Related components:** COMP-02 (Core.Leagues), COMP-05 (Games.XGGrid), COMP-11 (Games.XGPath)

## Context

Planning xG Path's platform integration surfaced a gap in `Core.Leagues`
parallel to the ones ADR-0040/ADR-0041 found in `Core.Scoring`: most of
`ILeaderboardService` is already `GameKey`-scoped —
`GetActiveRoundLeaderboardAsync` takes a specific `Round` (which carries
its own `GameKey`), and `GetClosedRoundsAsync`/`GetClosedRoundLeaderboardAsync`/
`GetWindowedLeaderboardAsync` all take an explicit `gameKey` parameter
already. Only `GetGlobalLeaderboardAsync` (REQ-409's all-time median
ranking, the "Global League" leaderboard REQ-401/404 describe) does not —
it calls `IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync`, which
joins `Guess` to `Round` internally but never filters by `Round.GameKey`,
so it silently sums every game's rounds into one median. This was never
deliberately "cross-game by design" — it's the same shape of gap as the
scoring/attempt-cap ones: written correctly for a single-game platform,
never revisited for a second game because there wasn't one yet.

Left as-is, the moment xG Path ships its first round, a player's Global
League standing would blend two different scoring currencies (xG Grid's
uniqueness-derived points and xG Path's clue-efficiency points) into one
number, and a player who only plays one of the two games would be ranked
against players who play both — neither of which reflects a real skill
comparison.

## Decision

`GetGlobalLeaderboardAsync` gains a required `gameKey` parameter, matching
the shape every other `ILeaderboardService` method already has.
`IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync` gains the matching
`gameKey` parameter and adds `round.GameKey == gameKey` to its existing
`Guess`-`Round` join — no schema change and no new join, since the query
already joins to `Round` for `round.ClosedAt`.

`League` membership itself is unchanged: there is still exactly one
Global League, auto-joined at signup (REQ-401 untouched). What changes is
that *reading* its all-time ranking now always requires saying which
game's rounds to rank by — the frontend's leaderboard screen gains a game
switcher (mirroring the "Games" nav pattern already established for
navigation, REQ-720) rather than defaulting silently to one game or
attempting to merge two incompatible scoring currencies into one number.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Leave one cross-game total | No API/frontend change | Blends two different scoring formulas (ADR-0040) into one number; unfairly compares single-game players against multi-game players | Doesn't produce a meaningful ranking once a second scoring model exists |
| A second, separate `League(type="global-xg-path")` row | Reuses `League`/`LeagueMembership` machinery as-is, zero `ILeaderboardService` signature changes | Doubles global-league membership bookkeeping for no real benefit — membership was never the thing that needed to differ, only which rounds count towards the ranking; every user is auto-enrolled in both "global" leagues identically, so a second membership row carries no information a `gameKey` parameter doesn't already provide for free | Adds a redundant entity instead of extending an interface that already handles this exact pattern for three other methods |
| Add `gameKey` to `GetGlobalLeaderboardAsync` (chosen) | One `League` row, consistent with every other leaderboard scope in this file; small, well-contained query change (the join to `Round` already exists) | Every existing caller (frontend, tests) of `GetGlobalLeaderboardAsync` must now supply a `gameKey` | Smallest change that makes this method consistent with the other three, which already solved the identical problem |

## Consequences

- Positive: all four `ILeaderboardService` scopes are now uniformly
  `GameKey`-scoped; a third game needs no new leaderboard design, just
  another `gameKey` value passed through the same methods
- Negative / trade-offs accepted: the leaderboard screen needs a game
  switcher UI it didn't need before (tracked as a frontend follow-up, not
  designed by this ADR) — SCREEN-03 in `design-document.md` will need
  updating once that screen work happens
- Follow-up: REQ-404's still-deferred full per-custom-league leaderboard
  (tab switching, per-league reads) is a different, orthogonal axis
  (which *league*, vs. this ADR's which *game*) — when it's eventually
  built, it needs both axes (league × game), not just the one this ADR adds

## For AI agents

Do not add a new `League` row per game to solve this — membership is
identical across games (every signed-up user is in the one Global League
regardless of which games they play), so a second league row would carry
no real information. If a future leaderboard need seems to require
per-game membership specifically (not just per-game *ranking data*), stop
and flag it rather than assuming this ADR's shape extends to it.
