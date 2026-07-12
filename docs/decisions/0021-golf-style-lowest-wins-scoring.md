# ADR-0021: xG Arcade is scored like golf — lower points is better, lowest total wins

- **Status:** Accepted
- **Date:** 2026-07-12
- **Related requirements:** REQ-203, REQ-204, REQ-205, REQ-206, REQ-401, REQ-404, REQ-405
- **Related components:** COMP-04 (Core.Scoring), COMP-05 (Games.XGGrid, `IGameModule`)

## Context

S-011 built REQ-204/205/206's scoring with an implicit "more points is
better" model: `FinalPoints = round(uniqueScore * MaxPointsPerCell)`, an
incorrect guess scored `0`, an unanswered cell contributed `0` by simply
having no `Guess` row, and the leaderboard (`LeaderboardService`) sorted
`SUM(FinalPoints)` descending. ADR-0020 (same day) fixed a real bug in
`uniqueScore` itself (self-comparison made a lone correct guesser score 0%
unique) but kept this same higher-is-better direction.

Direct product feedback (2026-07-12, immediately after ADR-0020 shipped)
asked for the opposite model: a rarer/more-unique correct answer should
earn **fewer** points, and a player's (and the leaderboard's) goal is to
**minimize** their total — golf scoring, not points-accumulation scoring.
This is a deliberate product decision, not a bug fix; nothing in the prior
design or docs described this direction, so it's recorded here as its own
choice, layered on top of (not reverting) ADR-0020's self-comparison fix.

Flipping the reward direction alone would have broken the game's core
incentive: under "lowest wins," `0` becomes the *best* possible score. Two
paths already produced `0` under the old model precisely because it meant
"no credit" — an incorrect guess, and (implicitly, by having no `Guess` row
at all) an unanswered cell. Left unchanged, both would become **free wins**
under the new model: a player could score better by refusing to guess than
by guessing correctly-but-commonly, and a wrong guess would tie the best
possible correct guess. This was raised explicitly and confirmed before
implementation, not discovered after the fact.

## Decision

1. **`ScoringRules.PointsFromUniqueScore`** is now `round((1 - uniqueScore)
   * MaxPointsPerCell)` (was `round(uniqueScore * MaxPointsPerCell)`).
   `uniqueScore` itself (ADR-0020's corrected fraction) is unchanged — only
   its mapping to points is inverted. The rarest possible correct answer
   now scores `0` (best); the most commonly-shared one scores
   `MaxPointsPerCell` (worst).
2. **An incorrect guess locks at `FinalPoints = MaxPointsPerCell`** (was
   `0`) — the worst score, matching the worst possible *correct* outcome,
   so failing is never better than succeeding no matter how common the
   right answer turns out to be.
3. **An unanswered cell, for a round a player participated in (≥1 guess in
   that round), is penalized the same as an incorrect guess.** Since there
   is no `Guess` row for an unattempted cell to carry a locked score at all,
   `ScoreLockingService` gained a new step run before locking,
   `MaterializeUnansweredCellsAsync`: for each round participant, it
   inserts a synthetic `Guess` row (`IsCorrect = false`, `AttemptCount = 0`,
   `SubmittedName = ""` — distinguishing it from a real wrong guess) for
   every cell of that round's grid instance they never attempted, resolved
   via a new `IGameModule.GetCellIdsAsync(instanceId)` method (never by
   Core reaching into a game-specific table directly — ADR-0003
   unaffected). A user who never opened the round at all is not a
   participant and is not penalized — this only completes a round someone
   actually played, it does not retroactively penalize non-participation.
4. **`LeaderboardService` sorts ascending** (`OrderBy(TotalPoints)`, was
   `OrderByDescending`) — rank #1 is the lowest total.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Full flip (chosen) | Matches the explicitly requested game feel; internally consistent (0 = best everywhere, `MaxPointsPerCell` = worst everywhere) | Larger diff (formula + incorrect-guess value + new materialization step + sort direction) | This is what was asked for |
| Flip only the formula, leave incorrect/unanswered at 0 | Smallest diff | Breaks the game's core incentive — not guessing (or guessing wrong) becomes optimal | Rejected — confirmed with the product owner before implementation; explicitly not acceptable |
| Penalize incorrect guesses but leave unanswered cells at 0 (accept the asymmetry) | No new `IGameModule` method or materialization step needed | A player could "protect" a good score by simply not attempting hard cells — same core-incentive problem, just narrower | Rejected — the product owner explicitly asked for unanswered cells to be penalized the same as a wrong guess, not left as a loophole |
| Keep everything descending/higher-is-better and just relabel the UI ("lower widget score is actually worse, sort visually") | No backend change | Doesn't match the requested mental model (player minimizes their number); leaderboard math and the number shown would keep contradicting each other | Rejected — this is a real scoring model change, not a display change |

## Consequences

- Positive: scoring now matches the requested mental model end to end —
  what a player sees, what they're trying to minimize, and how the
  leaderboard ranks them are all consistent. The unanswered-cell fix closes
  a real exploit that a formula-only flip would have introduced.
- Negative / trade-offs accepted: `ScoreLockingService` now depends on
  `IRoundRepository` and `IGameModuleResolver` in addition to
  `IGuessRepository` (previously scoring never needed to know about rounds
  or game modules at all) — a real, if modest, increase in COMP-04's
  coupling surface, justified by ADR-0003's existing `IGameModule` pattern
  being the correct (not a new) way to reach game-specific data. Every
  `IGameModule` implementation must now also implement `GetCellIdsAsync` —
  a breaking interface change for any future second game module, not just
  `GridGameModule`.
- Follow-up: Tier 0 has no real user history yet (`MVP-SCOPE.md`), so no
  backfill of already-locked `FinalPoints` rows from before this ADR was
  needed at the time of this decision — same as ADR-0020's own follow-up
  note. If a similar formula correction is needed after real history
  exists, revisit whether a backfill pass is warranted then.

## For AI agents

If code you are about to write would contradict this decision — e.g.
treating a higher point value as "better," summing/sorting a leaderboard
descending, or letting an unanswered cell default to a free `0` — stop and
flag it rather than silently working around it. `uniqueScore` itself
(ADR-0020) still means "higher = rarer answer"; only its mapping to points
is inverted by this ADR. Every new correct-guess scoring path must go
through `ScoringRules.PointsFromUniqueScore`, and every new
"guess missing entirely" path for a round participant must go through
`ScoreLockingService.MaterializeUnansweredCellsAsync` (or its equivalent)
rather than silently defaulting to 0.
