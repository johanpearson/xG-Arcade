# ADR-0020: Uniqueness formula excludes the guesser's own guess from the comparison

- **Status:** Accepted
- **Date:** 2026-07-12
- **Related requirements:** REQ-204, REQ-205, REQ-206, REQ-401, REQ-404
- **Related components:** COMP-04

## Context

`UniquenessCalculator.Calculate` (`XGArcade.Core.Scoring`, built S-011)
computed a guess's uniqueness as:

```
uniqueScore = 1 - (sameAnswerCount / totalCorrectGuessesForCell)
```

where both `sameAnswerCount` and `totalCorrectGuessesForCell` counted the
guesser's own guess. This was written deliberately, not accidentally — an
earlier test (`UniquenessCalculatorTests.
REQ204_Calculate_SingleCorrectGuessForCell_ReturnsZero`) explicitly asserted
and documented that a lone correct guesser scores 0% unique / 0 points as
"the intended, literal behavior of the formula, not a bug."

Real play-testing (reported directly by the product owner, 2026-07-12)
surfaced this as a genuine problem: a player who is the only (or first)
correct guesser for a cell — i.e. nobody else has picked their answer —
consistently saw "0% unique" and scored 0 points for it. Comparing a guess
against a population that consists only of itself is degenerate: with one
data point, that point trivially equals "100% of the population," which the
`1 - x` formula then reads as 0% unique. This inverted the actual intent
recorded elsewhere in the docs (`requirements-document.md`'s glossary:
"Uniqueness score | Share of players who did NOT give the same answer for a
cell") — with no other guessers to compare against, the share who gave a
*different* answer is vacuously 100%, not 0%.

At larger guesser counts the self-inclusive and self-exclusive formulas
converge (the self-comparison term becomes a rounding error), so this was
never visible as "the leaderboard feels wrong" in aggregate testing — it
only shows up sharply at the edges (1-3 correct guessers per cell), which is
exactly the common case for a newly-launched, low-traffic Tier 0 game.

## Decision

`UniquenessCalculator.Calculate` now excludes the guesser's own guess from
both the numerator and denominator:

```
otherCorrectGuessCount = totalCorrectGuessesForCell - 1
if otherCorrectGuessCount == 0:
    uniqueScore = 1.0   // no other correct guesser yet — maximally unique
else:
    othersWithSameAnswer = sameAnswerCount - 1
    uniqueScore = 1 - (othersWithSameAnswer / otherCorrectGuessCount)
```

A lone or first correct guesser for a cell now scores 100% unique / full
points (`ScoringRules.MaxPointsPerCell`), not 0. The formula's general
character — rarer answers score higher, `1 - (share who match you)` — is
unchanged; only the degenerate self-comparison is removed. This affects both
the live estimate (`GET /rounds/current`'s `UniquePercent`/`LivePoints`) and
the round-close lock (`ScoreLockingService`'s `FinalUniquenessScore`/
`FinalPoints`), since both call the same shared method — they can never
disagree by construction, unchanged from S-011/S-018's design.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Self-exclusion (chosen) | Matches the documented glossary definition; correct at every guesser count, not just a special case; minimal, localized code change | None significant | — |
| Special-case only `totalCorrectGuessesForCell == 1` (keep self-inclusive math otherwise) | Smaller diff | Papers over the real defect (self-comparison) instead of removing it; would still be subtly wrong at very low guesser counts (2-3), not just exactly 1 | Rejected — the underlying formula was wrong, not just its N=1 edge case |
| Leave as-is, document it as expected | No code change | Directly contradicts the product owner's explicit, tested feedback and the requirements glossary's own definition | Rejected |

## Consequences

- Positive: uniqueness/points now behave as the game's own glossary always
  said they should — rewarding rarity, not penalizing being first/alone.
  Early guessers on a freshly-generated grid (the most common real-world
  case at Tier 0's traffic level) are no longer systematically
  under-rewarded.
- Negative / trade-offs accepted: average points paid out per correct guess
  rise somewhat versus the old formula, since a wider range of guesses now
  land at or near 100% rather than being pulled down by self-comparison —
  this is the intended correction, not an unplanned side effect, but it
  does mean earlier-collected `FinalPoints`/leaderboard totals (from before
  this fix shipped) were computed under the old, incorrect formula.
  Reconciling or explicitly leaving those historical totals as-is was not
  in scope for this ADR — Tier 0 has no real users/history yet
  (`MVP-SCOPE.md`), so no backfill was needed at the time of this decision.
- Follow-up: if real user history exists when a similar formula correction
  is ever needed again, revisit whether a backfill/recompute pass over
  already-locked `Guess.FinalPoints` is warranted — REQ-205 doesn't
  currently define one.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
