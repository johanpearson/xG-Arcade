# ADR-0010: Live verification at guess time, sharing the API budget with grid generation

- **Status:** Accepted — the budget model described below (a shared
  API-Football budget split 80/20 between grid generation and guess-time
  lookups) is superseded by ADR-0011: it was designed around API-Football
  as if it were the only live-lookup source, when ADR-0001 had already
  established Wikidata as a second source. ADR-0011 corrects this to a
  Wikidata-first waterfall. The narrow trigger condition, immediate
  persistence, and fail-closed philosophy described below are unchanged —
  **except the trigger condition itself is further revised by ADR-0018**:
  Tier 0's first real implementation of this ADR (2026-07-10) omits the
  `PlayerNameIndex` pre-check described below as "what keeps this bounded,"
  because `PlayerNameIndex` (ADR-0007) isn't built yet and Wikidata isn't
  the scarce resource this gate was protecting (ADR-0011). Read ADR-0018
  before relying on the `PlayerNameIndex`-gated trigger described in this
  ADR's Decision/Alternatives sections as current behavior.
- **Date:** 2026-07-07
- **Related requirements:** REQ-211 (new), REQ-101, REQ-103, REQ-203
- **Related components:** COMP-05 (Games.XGGrid), COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-10 (Data.PlayerNameIndex)

## Context

`PlayerAttribute` (the narrow cache REQ-203 checks for correctness) is only
ever seeded with the specific combinations grid generation happened to need
(ADR-0001) — by design, it's never exhaustive. `PlayerNameIndex` (ADR-0007)
is deliberately broad and separate, covering many thousands of real
footballers, used only for autocomplete/name matching.

This creates a real gap: a player can submit a genuinely correct guess —
someone who really does satisfy both of the cell's categories — who simply
isn't in `PlayerAttribute` yet, because grid generation's minimum-match
threshold (REQ-101, default 3) only needed to find *some* valid answers,
not catalog every one. Without a fix, REQ-203 would wrongly mark a correct
guess as incorrect, which is a real trust problem, not an edge case to
shrug off.

Separately, API-Football's free tier is capped at 100 requests/day — a
budget REQ-103's grid-generation live-lookups already draw from. Adding a
second call site (guess-time verification) risks starving grid generation
of budget on a busy day, which would be worse than the problem being
solved (a failed grid generation blocks an entire round, REQ-101).

## Decision

- **When to trigger a live lookup at guess time:** only when the guessed
  name resolves to a specific candidate in `PlayerNameIndex` (i.e. a real,
  known player) **and** `PlayerAttribute`/`PlayerOverride` has *no* record
  at all — neither confirming nor denying — for that player against the
  cell's category types. A name that doesn't match anything in
  `PlayerNameIndex` at all is treated as incorrect without any live call —
  `PlayerNameIndex`'s breadth (ADR-0007) already means a real player almost
  always matches something there, so this keeps the live-call trigger
  narrow and deliberate, not "every unmatched guess."
- **Persist immediately, not batched.** When a live lookup happens (at
  either call site — grid generation or guess-time), the result is written
  to `PlayerData` as `unverified` in the same request, exactly like
  REQ-103 already does. No deferred/batched sync job. Two reasons: (a)
  consistency — introducing a second caching philosophy alongside
  ADR-0001's "fetch once, cache forever" adds complexity for no benefit;
  (b) correctness — deferring the write means the same gap can trigger
  another live call (and another API request) before the next batch runs,
  which is strictly worse for the shared budget this ADR is trying to protect.
- **Shared daily budget with a reserved buffer.** Both call sites draw from
  one tracked daily counter against API-Football's 100/day cap. Guess-time
  lookups stop triggering once daily usage crosses a conservative
  threshold (80 requests, reserving 20 for scheduled grid generation and
  data sync), rather than racing grid generation for the same quota with
  no coordination. Once the threshold is hit, a guess that would have
  needed a live lookup is evaluated against existing cached data only —
  correctness fails closed (marked incorrect) rather than blocking or
  erroring, since REQ-210's 2-attempt cap and REQ-501's admin-correction
  path both mean a wrongly-rejected rare correct guess is recoverable
  after the fact, not catastrophic.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Live lookup on every guess not found in `PlayerAttribute`, no `PlayerNameIndex` pre-check | Simple to reason about | Would trigger a live call for outright made-up names too, not just real unlisted players — burns budget fast for no reason | The `PlayerNameIndex` pre-check is what keeps this bounded |
| Batch/defer persistence (twice-daily sync, as originally proposed) | Keeps the guess-check request path free of a write | The live lookup itself still has to happen synchronously to answer the guess at all — deferring only the write adds complexity while leaving the exact same repeated-lookup risk this ADR needs to avoid | No real latency benefit, real correctness/budget cost |
| No shared budget coordination — let both call sites hit the API independently | Simpler to build | Grid generation could fail (REQ-101 abort) because guess-time lookups exhausted the day's quota first | Blocks an entire round; worse than the problem being solved |

## Consequences

- Positive: correct guesses are no longer wrongly rejected just because
  they weren't part of the original grid-generation sample; the existing
  "fetch once, cache forever" pattern extends cleanly to a second call
  site without a second philosophy
- Negative / trade-offs accepted: a small latency cost on the rare guess
  that triggers a live lookup; the 80/20 budget split is a guess at this
  stage, not measured against real usage
- Follow-up: once real usage data exists, revisit the 80/20 split — it
  may need to favor guess-time lookups more (if that's where the actual
  demand is) or less (if grid generation for larger N×N grids needs more
  headroom than expected)

## For AI agents

Don't trigger a live lookup for a guess that doesn't match anything in
`PlayerNameIndex` — that's an incorrect guess, not a data gap. Don't defer
or batch a live-lookup write — persist in the same request, same as
REQ-103. Don't let guess-time lookups consume the daily budget past the
reserved threshold — check the shared counter before calling out, and fail
closed (mark incorrect) rather than skipping the budget check under
pressure to "just answer the guess."
