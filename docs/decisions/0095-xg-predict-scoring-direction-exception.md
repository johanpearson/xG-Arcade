# ADR-0095: xG Predict is a named exception to ADR-0021's golf-style scoring

- **Status:** Accepted
- **Date:** 2026-08-30
- **Related requirements:** REQ-1304, REQ-401
- **Related components:** COMP-04 (Core.Scoring), COMP-15 (Games.XGPredict)

## Context

ADR-0021 made golf-style scoring (lower total is better, 0 is best, the
leaderboard sorts ascending) a mandatory, platform-wide convention, with an
explicit instruction to stop and flag any code or requirement that treats a
higher score as better. It has held for both games built since (xG Grid,
xG Path) with no exception.

While drafting xG Predict's requirements (REQ-1304, this session), the
product owner's own description used natural "points awarded for a correct
prediction" language — the opposite direction from ADR-0021.
`requirements-writer` treated ADR-0021 as the binding platform default and
translated that language into golf-style terms rather than transcribing it
literally, correctly flagging the translation rather than making it
silently.

Asked directly whether to keep that consistency or make xG Predict an
explicit exception, the product owner chose conventional higher-is-better
scoring for this game specifically. The reasoning: essentially every
real-world match-prediction product (Superbru, official league predictors,
fantasy football) works this way — more correct predictions produces a
bigger number that climbs the board — and forcing this specific genre into
an inverted mental model was judged to cost more in player confusion than
it gains in internal consistency, even though ADR-0021 was written as a
firm, non-negotiable rule.

## Decision

1. **xG Predict's `FinalPoints` accumulate in ascending, natural terms**:
   REQ-1304's three components (correct 1X2 outcome, correct home-goal
   count, correct away-goal count) each award points when correct and 0
   when not, additively. This is the first `IScoringStrategy`
   implementation to deviate from ADR-0021's direction.
2. **`IScoringStrategy` gains a new member, `LowerIsBetter` (bool)**.
   `UniquenessScoringStrategy` (xG Grid) and `ClueEfficiencyScoringStrategy`
   (xG Path) both return `true`, unchanged. The new xG Predict strategy
   returns `false`. This reuses the existing per-`GameKey` resolver
   (`IScoringStrategyResolver`, ADR-0040) rather than introducing a fourth
   resolution mechanism alongside it — consistent with how ADR-0040 and
   ADR-0051 already solved this same shape of problem for scoring and round
   scheduling respectively.
3. **`LeaderboardService`'s three currently-hardcoded `OrderBy(TotalPoints)`
   call sites must resolve sort direction per `GameKey`** (via the
   resolved strategy's `LowerIsBetter`) instead of assuming one
   platform-wide ascending order. This is the one real structural gap
   ADR-0021's original design didn't anticipate needing — it was written
   when "the platform" and "xG Grid" were the same thing.
   **Implementing this change (the `LowerIsBetter` member, the new xG
   Predict strategy, and `LeaderboardService`'s three call sites) is out of
   scope for this ADR and this session** — this ADR records the decision
   and its shape; the code change is a follow-up backend story alongside
   xG Predict's own scoring-strategy implementation.
4. **ADR-0021 itself is not reverted, edited in its own decision text, or
   weakened as the default.** It remains mandatory for every game unless a
   future ADR names an explicit exception the way this one does. Its own
   "for AI agents" instruction is amended in effect, not in text, solely
   for `GameKey == "xg-predict"` — nothing else changes. A short
   cross-reference note is added at the top of ADR-0021 pointing here, the
   same pattern already used elsewhere in this repo (e.g. ADR-0045's own
   partial-supersede notes from ADR-0073/0074).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep golf-style for xG Predict too, for full platform consistency | No new `LowerIsBetter` concept, no `LeaderboardService` change needed | Inverts the near-universal mental model of the entire prediction-game genre; the one game where "more correct guesses = bigger number" is the standard expectation would be the one game on this platform that breaks it | Rejected — explicit product decision, made after the trade-off was put directly to the product owner |
| Flip only the display (show an inverted or relabeled number in the UI while `FinalPoints`/sort stay golf-style underneath) | No backend scoring-model change | ADR-0021 already rejected the equivalent option for its own original flip, for the same reason: the number shown, what the player is trying to maximize, and how the leaderboard ranks them would keep contradicting each other | Rejected for the same reasoning ADR-0021 already established, applied here in reverse |
| Retroactively add a generic "direction" concept to ADR-0021's core formulas too, unifying all three games under one flexible model from the start | Slightly more "elegant" in the abstract | No evidence xG Grid or xG Path need to change; touches two working, already-tested formulas for the benefit of a third game's implementation shape only | Out of scope — don't rework a working system beyond what's actually needed |

## Consequences

- Positive: xG Predict's scoring matches how its entire genre already
  works, for every new player's very first guess at what a number means.
- Positive: ADR-0021 stays intact, unambiguous, and unedited as the default
  for xG Grid and xG Path — this is a named, narrow carve-out, not a
  reopening of that decision.
- Negative / trade-offs accepted: `Core.Scoring`/`LeaderboardService` no
  longer has a single, platform-wide sort-direction invariant to rely on —
  every future leaderboard read must resolve direction per `GameKey`
  rather than assuming ascending. This is a real, if narrow (one boolean
  property, one resolver already in place), increase in the surface a
  future change to leaderboard logic must consider.
- Follow-up: implement `IScoringStrategy.LowerIsBetter`, the new xG Predict
  strategy, and `LeaderboardService`'s three `OrderBy` call sites as part of
  xG Predict's own scoring-strategy backend story (not this scaffolding
  session). Extend `LeaderboardServiceTests` to cover a
  `LowerIsBetter == false` `GameKey`, not just xG Grid/xG Path's existing
  ascending cases.

## For AI agents

Do not assume ADR-0021's lower-is-better rule applies to
`GameKey == "xg-predict"` — check the resolved `IScoringStrategy.LowerIsBetter`
before writing any comparison, sort, or "best score so far" logic that
touches xG Predict. Do not extend this exception to any other game without
that game having its own equivalent ADR — this is a single, named
`GameKey` carve-out, not a reopening of ADR-0021 for the platform. Once
`LeaderboardService`'s three `OrderBy` call sites are migrated to resolve
direction per `GameKey`, do not hardcode `OrderBy`/`OrderByDescending` for
any new leaderboard read path — resolve it from the strategy the same way.
