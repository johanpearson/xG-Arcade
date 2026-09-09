# ADR-0110: xG Higher/Lower fits the existing `Round` model, as a fixed-length, generated-once-per-round comparison sequence

- **Status:** Accepted
- **Date:** 2026-09-09
- **Related requirements:** REQ-1501, REQ-1502, REQ-1503, REQ-1504, REQ-1505
- **Related components:** COMP-03 (Core.Rounds), COMP-04 (Core.Scoring),
  COMP-02 (Core.Leagues), COMP-06 (PlayerAttribute/PlayerOverride) — no
  game-module component number is assigned yet for xG Higher/Lower itself;
  none of this decision requires reserving one before real code exists.

## Context

`docs/requirements-document.md` §4.16 proposed xG Higher/Lower, a fifth
game, and deliberately left one structural question open rather than
assuming an answer (mirroring how §4.15's original xG Connect draft left
its own Round-fit question open until ADR-0103 resolved it): does a
session fit the existing shared `Round` model every other game
(xG Grid, xG Path, xG Predict) uses, or is it structurally different
enough to need its own first-class concept, the way ADR-0103 concluded for
`ConnectMatch`?

The first draft of §4.16 described an **anytime, replayable, single-player
session**: a player starts whenever they like, independently of any other
player or any scheduled event, and plays until an incorrect guess or
pool-exhaustion ends it — with no fixed upper bound on length beyond
"eventually the eligible player pool runs out." On review, the product
owner flagged two problems with that shape: it isn't Round-specific (no
shared, scheduled instance every participant plays the same version of),
and its open-ended length reads as "never-ending" even though it was
technically bounded by pool exhaustion — that bound is large and
incidental, not a deliberate small round size the way every other game
has one (xG Grid's grid size, xG Predict's fixed 5 matches).

This ADR resolves both problems at once, since they turn out to be the
same fix: adopt the `Round` model, which forces exactly the
generated-once, fixed-size shape the product owner wants.

## Decision

**xG Higher/Lower fits the existing `Round` model, the same way xG Grid,
xG Path, and xG Predict do — not a new on-demand concept like
`ConnectMatch`.**

Concretely, once implemented, `IGameModule.GenerateInstanceAsync` for
`"xg-higher-lower"` generates, once per `Round` (via the same
`RoundGenerationService`/`IRoundSchedulingOptionsResolver` cron path every
other `GameKey` already uses, COMP-03):

- exactly one stat category, fixed for that Round's entire lifetime
  (REQ-1501), and
- exactly one fixed-length, fully-ordered sequence of players — one
  starting baseline plus a configured number of comparators — generated
  in full at Round-generation time, satisfying REQ-1501/1502's eligibility
  rules (comparable value present, no exact tie between consecutive
  players, no player repeated within the sequence).

This sequence is identical for every participant of that Round, the same
"generate once, shared by all participants" pattern as xG Grid's grid
layout and xG Predict's five matches — not regenerated or re-randomized
per player. The Round's length (the fixed comparator count) is a
per-`GameKey` configuration value, the same kind of round-shaping option
`GridSize`/`PuzzleCount` already are for xG Grid/xG Path (ADR-0051) — this
ADR does not fix a specific number, only that it is small and fixed per
Round rather than open-ended.

**Gameplay stays streak-shaped, but bounded by the Round's fixed
sequence.** A participant plays through the pre-generated sequence one
comparison at a time (REQ-1504's correct/incorrect mechanics are
unchanged in shape): a correct guess advances to the next pre-generated
comparator and increments the streak; an incorrect guess ends that
participant's attempt at their current streak length; reaching the end of
the Round's fixed sequence with every guess correct also ends the attempt,
capped at the Round's configured length — there is no case where an
individual attempt can run longer than the Round it belongs to.
REQ-1502's old "pool-exhaustion ends the session" case no longer applies
at guess time — eligibility (no ties, no repeats) is instead a
Round-*generation*-time concern: if a valid full-length sequence cannot be
generated for a candidate category, generation for that category fails
the same way every other game's generation already fails closed when it
cannot produce a valid instance, rather than silently producing a
shorter-than-configured Round.

**Scoring fits `Core.Scoring`/`Core.Leagues` natively.** A participant's
`FinalPoints` for the Round is their streak length reached (0 up to the
Round's fixed length) — a single, directly comparable number per
participant per Round, exactly the shape `IScoringStrategy`/`FinalPoints`
already assumes (COMP-04), ranked by the same existing
Global/custom-league leaderboard scopes every other `GameKey` already
gets (COMP-02), highest-first with the existing name tie-break (REQ-409).
This resolves REQ-1505's previously-open "which league(s)" question
without a new decision: it is exactly the same leaderboard wiring xG Grid/
xG Path/xG Predict already have, because a Round-scoped streak score is a
`FinalPoints` value like any other.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep the original anytime/replayable, no-`Round` design, just add an explicit hard cap on session length (e.g. stop at N regardless of pool) | Smallest diff from the original draft; still no dependency on `Core.Rounds` scheduling | Doesn't address "not Round-specific" at all — sessions still start whenever a player wants, aren't shared/comparable across players the way a leaderboard implies, and would need an entirely new comparability story (comparing streaks from different random sequences) that `Round`/`FinalPoints` already solves for free | The product owner's ask was for both properties (Round-specific *and* bounded) — this only fixes one, and worse, it invents a new ad hoc leaderboard-comparability mechanism `Round` already provides |
| `ConnectMatch`-style new first-class concept, on-demand, still anytime-replayable, just capped in length | Reuses ADR-0103's precedent structure instead of inventing a third pattern | Same core mismatch as above — `ConnectMatch` was chosen specifically *because* xG Connect is two-named-participants and on-demand; xG Higher/Lower is single-player and has no analogous "which two players" scoping need, so borrowing that shape solves a problem xG Higher/Lower doesn't have while still not giving it a shared, comparable Round | Solves the wrong problem; `Round` is the correct fit once boundedness is required anyway |
| `Round`-based, but each participant's sequence independently randomized per-participant (only the category and length fixed, not the exact player sequence) | Slightly more replay variety across participants in the same Round | Breaks strict comparability of "who got the longer streak" if the sequences differ in difficulty (some player pairs are closer in value, hence harder to call, than others) — the same fairness reasoning REQ-1502 already applies within one sequence would need to additionally hold *across* sequences, which is a much harder guarantee to make | Fixed, shared sequence per Round is the simplest way to make every participant's streak length directly comparable, mirroring xG Grid's shared grid and xG Predict's shared match list exactly |

## Consequences

- Positive: `Core.Rounds`/`Core.Scoring`/`Core.Leagues` need zero changes
  to support xG Higher/Lower, the same "no `Core.Rounds` change needed"
  result already recorded for xG Path and (for the parts that do apply)
  xG Predict — `IGameModule.GenerateInstanceAsync` plus a standard
  `IScoringStrategy` implementation is the whole integration surface.
- Positive: resolves REQ-1505's previously-open leaderboard-placement
  question as a direct consequence, with no separate product decision
  needed — a Round-scoped `FinalPoints` value slots into the existing
  Global/custom-league scopes automatically.
- Positive: directly answers the "never-ending" concern — an individual
  attempt is capped at the Round's configured length by construction, not
  by an incidental, large pool-exhaustion bound.
- Negative / trade-offs accepted: loses the original design's "play
  literally anytime, as many times as you like, independent of any
  schedule" replayability — xG Higher/Lower now follows the same
  once-per-schedule Round cadence as Grid/Path/Predict, which is a real
  gameplay-feel change from the initial pitch, not just an implementation
  detail.
- Negative / trade-offs accepted: because the full comparator sequence is
  generated once and shared by every participant, a player who plays late
  in the Round's window and has seen results discussed by others (or seen
  the sequence spoiled) has an advantage — the same shared-instance spoiler
  risk xG Grid/xG Path/xG Predict already accept, now also true here.
- Follow-up: the exact configured Round length (comparator count) is a
  product/tuning decision for whoever implements REQ-1503, not fixed by
  this ADR — treat it the same as `GridSize`/`PuzzleCount`'s own tuning
  precedent (ADR-0051).
- Follow-up: `docs/requirements-document.md` §4.16 needs its REQ-1501
  through REQ-1505 text updated to match this decision (Round-scoped
  generation, fixed sequence, capped streak, resolved leaderboard scope)
  — tracked as a direct follow-up to this ADR, not a separate open
  question.

## For AI agents

Do not implement xG Higher/Lower as an anytime, on-demand, no-`Round`
session, and do not give it a `ConnectMatch`-style new first-class
concept — it must generate through the standard
`RoundGenerationService`/`IRoundSchedulingOptionsResolver` path
(COMP-03) like xG Grid/xG Path/xG Predict, with one fixed comparator
sequence per Round shared by every participant. Do not let an individual
attempt exceed the Round's configured comparator count. Do not leave the
leaderboard question open in new work — this ADR resolves it as a
standard `FinalPoints`-based Global/custom-league scope, same as every
`Round`-based game. If a task seems to require any of the above, stop and
flag it rather than working around this ADR.
