# ADR-0095: xG Predict is a named exception to ADR-0021's golf-style scoring

- **Status:** Accepted — Decision §2/§3's code change (the `LowerIsBetter`
  member, `XGPredictScoringStrategy`, `LeaderboardService`'s three named
  `OrderBy` call sites) built 2026-08-30, same day, by the follow-up story
  this ADR's own Follow-up note queued — see that note's amendment below
  for what shipped and one scope gap it surfaced.
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

> **Amendment (2026-08-30, same-day follow-up story — built as specified):**
> `IScoringStrategy.LowerIsBetter` (true for `UniquenessScoringStrategy`/
> `ClueEfficiencyScoringStrategy`, unchanged), the new `XGPredictScoringStrategy`
> (`LowerIsBetter = false`; registered against `"xg-predict"` via the
> existing `IScoringStrategyResolver`, no new resolver type), and
> `LeaderboardService`'s three named `OrderBy(TotalPoints)` call sites
> (`GetActiveRoundLeaderboardAsync`/`GetClosedRoundLeaderboardAsync`/
> `GetWindowedLeaderboardAsync`) all now resolve sort direction per
> `GameKey` exactly as this Decision specified. `LeaderboardServiceTests`
> gained three `ADR0095_`-prefixed descending-sort cases, one per migrated
> method. `architecture-reviewer`/`quality-architect` both ran clean after
> one quality-gate fix round (a rule-of-three duplication across the three
> migrated call sites, extracted into a shared private helper in
> `LeaderboardService`).
>
> **One genuinely debatable design call, surfaced by `architecture-reviewer`,
> not treated as a blocker:** `XGPredictScoringStrategy.ScoreCorrectGuess` —
> the actual `IScoringStrategy` interface member — throws
> `NotSupportedException` rather than computing anything, because ADR-0096
> already established xG Predict never persists a `Guess` row at all
> (predictions live in `PredictMatchPrediction` instead), so
> `ScoreLockingService.LockRoundScoresAsync` (which only ever calls
> `ScoreCorrectGuess` for guesses it fetched via `IGuessRepository`) can
> never actually reach this method for `GameKey = "xg-predict"` — it is
> provably unreachable today, not merely unimplemented. REQ-1304's real
> three-component formula instead lives in a new, separate public method,
> `ScorePrediction(predictedHomeGoals, predictedAwayGoals, actualHomeGoals,
> actualAwayGoals)`, on the same class — exercised directly by this story's
> own unit tests, with no production caller yet (REQ-1305's grading job,
> which would be that caller, is a separate, later story). This mirrors
> ADR-0096's own precedent of deferring `ScoreResult`'s widening rather than
> speculatively solving a shape problem for a caller that doesn't exist yet.
> **Standing item, not resolved here:** when REQ-1305's grading job is
> built, that is the right moment to either (a) confirm `ScorePrediction` as
> `IScoringStrategy`'s permanent second/parallel entry point for this
> `GameKey`, recorded via a short ADR note, or (b) revisit `IScoringStrategy`'s
> shape itself, per ADR-0040's own follow-up note ("if a third game needs a
> fundamentally different input shape, revisit whether `IScoringStrategy`
> still fits"). Do not let a fourth game add a third such
> `NotSupportedException` escape hatch without that revisit happening first.
>
> **Scope gap surfaced by `quality-architect`, deliberately not fixed by
> this story — see REQ-1304's own new status note for the acceptance-text
> side of this:** REQ-1304's acceptance criteria state that xG Predict's
> Global League all-time ranking (REQ-401/409/410 — `GetGlobalLeaderboardAsync`/
> `GetRankedMembersAsync`, median-per-qualifying-round, ≥5 rounds) also
> ranks `"xg-predict"` descending. This Decision's §3 never named that
> method — only the three plain-`SUM`/`TotalPoints`-based call sites — and
> this story built exactly what was named, not more. `GetRankedMembersAsync`'s
> `OrderBy(m => m.Median)` (`LeaderboardService.cs`) remains unconditionally
> ascending regardless of `GameKey`, for every `GameKey` including
> `"xg-predict"`, as of this amendment. This is currently latent (no
> `"xg-predict"` round has ever been generated in production — round
> generation isn't wired yet, per `XGPredictGameModule`'s own doc comment),
> so nothing is observably wrong today, but it is a real, undecided gap
> between what REQ-1304 promises and what ADR-0095/this story actually
> scoped, and must be resolved (either extend this migration to that fourth
> call site, or narrow REQ-1304's text to match the actual scope) before
> REQ-1305/1306 make `"xg-predict"` rounds real. Queued as backlog follow-up
> (`docs/backlog.md`), not silently left inconsistent.

> **Gap closed (2026-08-30, same-day direct follow-up):** `GetRankedMembersAsync`
> now resolves `IScoringStrategy.LowerIsBetter` per `GameKey` too, the same
> mechanism as the other three call sites, rather than narrowing REQ-1304's
> text to match a smaller scope. Not implemented via `RankByTotalPoints` (the
> helper this ADR's first amendment's three call sites share) — that
> helper's tuple shape (`int TotalPoints`) and return type
> (`List<LeaderboardEntry>`) don't match `GetRankedMembersAsync`'s
> (`double Median`, a raw ranked tuple list); it gets its own small
> `OrderBy`/`OrderByDescending` branch instead, reviewed and confirmed not to
> cross this repo's rule-of-three duplication threshold (two structurally
> different shapes, not three of the same one). All four `LeaderboardService`
> ranking scopes now resolve sort direction per `GameKey` — REQ-1304's
> acceptance text is accurate as written, with no remaining gap.

> **Standing item closed (2026-09-02, S-205):** the first amendment above
> left open whether `ScorePrediction` is `XGPredictScoringStrategy`'s
> permanent second/parallel entry point for `GameKey = "xg-predict"`, or
> whether `IScoringStrategy` itself should be reshaped, deferring the call
> to "when REQ-1305's grading job is built." That job now exists —
> ADR-0097 (2026-08-30, same day as this ADR's own amendments, built for
> REQ-1305/S-197) already made and recorded the substantive decision while
> wiring `PredictGradingService`: `ScorePrediction` stays a concrete public
> method on the concrete `XGPredictScoringStrategy` class, injected into
> `PredictGradingService` directly (registered as itself in DI, alongside
> its existing `IScoringStrategy` registration) rather than through
> `IScoringStrategyResolver`; `IScoringStrategy` was deliberately **not**
> widened with a `ScorePrediction`-shaped member. ADR-0097's own
> alternatives table gives the reasoning: every other `IScoringStrategy`
> implementation (`UniquenessScoringStrategy`, `ClueEfficiencyScoringStrategy`)
> would need a meaningless implementation of a member that only ever
> applies to one `GameKey`, for the benefit of a single caller — the same
> "don't widen a shared interface for one caller" reasoning ADR-0096 already
> used for `ScoreResult`. This amendment exists only to close the standing
> item explicitly, since ADR-0097 recorded the decision without pointing
> back to resolve the open question this ADR itself had raised.
>
> **Confirmed, permanent, until a third real caller forces otherwise:**
> `ScoreCorrectGuess` continuing to throw `NotSupportedException` for
> `GameKey = "xg-predict"`, with `ScorePrediction` as this `GameKey`'s
> separate, concrete, non-`IScoringStrategy` entry point, is this
> interface's deliberate permanent shape — not a temporary gap awaiting a
> future revisit. `IScoringStrategy` has exactly two real implementations
> today (three counting xG Predict's dual-natured one), and no fourth game
> exists to force a different shape. Do not treat `ScoreCorrectGuess`'s
> `NotSupportedException` branch as unfinished work, and do not reshape
> `IScoringStrategy` speculatively on its account. Revisit only if a real
> third game needs a fundamentally different `IScoringStrategy` input
> shape — the same bar ADR-0040's own follow-up note and ADR-0097's
> alternatives table already set — at which point that story is the right
> place to decide whether to generalize the interface or add another
> concrete-class carve-out. A fourth game should not add a third such
> `NotSupportedException` escape hatch without that revisit happening
> first — this is the same limit ADR-0095's first amendment already named,
> restated here as the closing word on the standing item rather than an
> open one.

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
Do not treat `XGPredictScoringStrategy.ScoreCorrectGuess`'s
`NotSupportedException` as unfinished work or a reason to reshape
`IScoringStrategy` — its permanence, and `ScorePrediction`'s status as the
confirmed, permanent second entry point for this `GameKey`, are settled;
see the "Standing item closed" amendment above before proposing either an
interface change or a fix to that method.
