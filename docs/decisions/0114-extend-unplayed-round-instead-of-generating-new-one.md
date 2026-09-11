# ADR-0114: Extend an unplayed round's schedule instead of generating a new one, uncapped, via a per-GameKey participation check

- **Status:** Accepted
- **Date:** 2026-09-11
- **Related requirements:** REQ-305, REQ-301, REQ-302, REQ-304
- **Related components:** COMP-03 (Core.Rounds), COMP-02 (Core.Leagues), COMP-04 (Core.Scoring)

## Context

A round that closes with zero players having played it still consumes a
fresh `SequenceNumber` and a freshly-generated game-content instance (a new
`GridInstance`, `HigherLowerInstance`, etc.) for its successor — the same
cost as a round that was actually played. The ask (raised directly by the
product owner, no REQ/trigger existed beforehand): if nobody played a
round, don't spend a new cycle on it — extend its schedule instead.

This is harder than it first looks because of REQ-301's "one round ahead"
rule (ADR-0022): rounds are generated a full cycle before they're needed,
so by the time `RoundGenerationService.GenerateNextRoundIfNeededAsync`
can observe that round N went unplayed (its `EndTime` has passed), round
N+1 has *already* been generated and has *already* started —
`previous.EndTime == latest.StartTime` is a structural invariant the
existing code already relies on. This rules out literally "reopening" the
round that went unplayed: doing so would require a second simultaneously
`Active` round for the same `GameKey`, which REQ-302 does not support (a
single derived-from-time active round is assumed everywhere — leaderboard
reads, `GET /rounds/current`, etc.).

ADR-0022's own alternatives-considered table already flagged a directly
relevant trap: inferring a round's closed/open state from `Guess`-row
presence is "ambiguous for a round nobody ever played... would look
permanently unclosed." Any renewal mechanism had to avoid repeating that
mistake — participation needed to stay a distinct signal from closed-ness,
computed after the time-based close decision, never a replacement for it.

A first implementation pass used `IGuessRepository.GetByRoundIdAsync`
directly inside `RoundGenerationService` (COMP-03) to check participation,
gated by a new `RoundSchedulingOptions.UsesModuleSuggestedTiming` flag
meant to exclude `GameKey`s using ADR-0102's module-suggested-timing
override path (currently only `"xg-predict"`). Architecture review caught
two problems with this: (1) `IGuessRepository`'s own doc comment states it
is "COMP-04's own persistence — the only path Core.Scoring reaches Guess
through," so COMP-03 reading it directly is a boundary drift; and (2) more
seriously, `"xg-higher-lower"` structurally never writes `Guess` rows at
all (it uses `HigherLowerAttempt` instead) but does *not* use
`UsesModuleSuggestedTiming` (correctly — its `EndTime` genuinely is
chain-math, not module-suggested). A direct `IGuessRepository` check would
therefore have found `"xg-higher-lower"`'s closing round *always* empty of
`Guess` rows regardless of real play, renewing every cycle forever and
permanently breaking that already-shipped game's round generation.

## Decision

Two decisions, made together:

1. **Renewal mechanism:** when the round `RoundGenerationService` is about
   to close (`previous`) has zero recorded participants, skip generating a
   new `Round` this cycle (no new `SequenceNumber`, no new game-content
   instance) and instead extend the currently-active round's (`latest`)
   own `EndTime` by one `RoundDuration` (or `roundDurationOverride`, when
   supplied — the same override semantics REQ-301 already defines). The
   closing round is still marked `Closed` exactly as REQ-302 requires —
   only its successor's *generation* is skipped, not its own closure. This
   is uncapped by design: it re-fires unchanged on every subsequent
   evaluation of the same still-unplayed round, for as long as it keeps
   going unplayed, with no renewal-count limit. A cap was considered and
   explicitly rejected (see Alternatives) — it would undercut the
   feature's own purpose exactly where it matters most, a `GameKey` nobody
   is playing at all. `GameKey`s using ADR-0102's module-suggested-timing
   override path (`RoundSchedulingOptions.UsesModuleSuggestedTiming`,
   currently only `"xg-predict"`) are excluded entirely — this REQ's
   chain-math extension only applies where `EndTime` is computed as
   `startTime + RoundDuration` in the first place.
2. **Participation check routing:** "did anyone participate in this round"
   is answered through the existing per-`GameKey` `IRoundScoreSource`
   abstraction (`Core.Scoring`, ADR-0100), via a new
   `HasAnyParticipantAsync(Round, CancellationToken)` method added to that
   interface and implemented by all three concrete sources —
   `GuessRoundScoreSource` (via `IGuessRepository.GetByRoundIdAsync`,
   `"xg-grid"`/`"xg-path"`), `HigherLowerRoundScoreSource` (via
   `IHigherLowerInstanceRepository.GetParticipantUserIdsByInstanceIdAsync`,
   `"xg-higher-lower"`), `PredictRoundScoreSource` (the same shape,
   `"xg-predict"` — implemented honestly even though REQ-305 never reaches
   it in production, since `UsesModuleSuggestedTiming` excludes that
   `GameKey` from the renewal check before this would ever be called).
   `RoundGenerationService` resolves the correct source via
   `IRoundScoreSourceResolver.Resolve(previous.GameKey)`, never
   `IGuessRepository` directly — keeping COMP-03 off COMP-04's owned data
   the same way ADR-0100 already moved `LeaderboardService` (COMP-02) off
   direct `IGuessRepository` calls for the identical reason (a
   game-agnostic Core component must not assume every `GameKey`'s
   participation data lives in one shared table).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Literally reopen/extend the round that went unplayed (`previous`) instead of extending its already-active successor (`latest`) | Most literal reading of "renew *that* round's date" | By the time participation is knowable, `previous`'s successor (`latest`) already exists and has already started — reopening `previous` would require two simultaneously `Active` rounds for one `GameKey`, which REQ-302's single-derived-active-round assumption doesn't support anywhere (leaderboards, `GET /rounds/current`) | Architecturally unsound; would need a REQ-302 redesign for no real behavioral gain over extending the already-live successor instead |
| Infer participation from whether the round's `Guess`/equivalent rows exist, treating an empty round as still-open rather than a distinct "renew" signal | No new interface method | Exactly the pattern ADR-0022's own alternatives table already rejected for closed-ness inference — conflates "has this round ended" with "did anyone play it," and would have made a round nobody ever played look permanently unclosed | Rejected on the same grounds ADR-0022 already established; participation must stay a separate, explicit signal computed *after* the time-based close decision |
| Check `IGuessRepository` directly from `RoundGenerationService` (the first implementation pass) | Simplest possible code, one existing repository call | (1) Boundary drift — COMP-03 reading COMP-04-owned data directly, contradicting `IGuessRepository`'s own documented ownership; (2) actively wrong for any `GameKey` whose participation data isn't `Guess`-shaped — confirmed to silently break `"xg-higher-lower"`, which never writes `Guess` rows at all, causing it to renew forever and never generate a new round again | Caught by architecture review before merge; fixed by routing through the already-existing `IRoundScoreSource`/`IRoundScoreSourceResolver` abstraction ADR-0100 built for exactly this per-`GameKey` data-shape problem |
| Cap consecutive renewals at a fixed number (e.g. 3), falling back to normal generation regardless of participation after that | Bounds how long a round's `SequenceNumber`/`StartTime` can sit static; gives an abandoned `GameKey` a periodic "fresh" round anyway | Spends a new `SequenceNumber`/game-content instance on a round that, by definition of having hit the cap, *still* has zero participants — undercuts the feature's own goal exactly in the one case (a genuinely abandoned `GameKey`) where avoiding that waste matters most | Rejected by explicit product decision, 2026-09-11 (recorded in REQ-305's "Repeated non-participation" clause) — uncapped, accepting that a dead `GameKey`'s round data can sit static indefinitely, since nothing today reads round age as a signal |

## Consequences

- Positive: a `GameKey` that goes genuinely unplayed no longer burns a
  fresh `SequenceNumber` or generates throwaway game content every cycle —
  the schedule simply stops advancing until real participation resumes.
- Positive: the participation check is now correct per-`GameKey` by
  construction (`IRoundScoreSource`), not dependent on every `GameKey`
  happening to store participation as `Guess` rows — a sixth game module
  with yet another participation shape (not `Guess`, not an
  instance-repository participant list) just implements
  `HasAnyParticipantAsync` on its own `IRoundScoreSource`, no
  `RoundGenerationService` change needed.
- Positive: extension only ever lengthens `EndTime`, never shrinks it, so
  ADR-0027's `RoundDuration >= cron's max gap` cron-safety invariant is
  preserved by construction — this change cannot reduce that margin.
- Negative / trade-off accepted: uncapped renewal means a genuinely
  abandoned `GameKey`'s round data (`SequenceNumber`, `StartTime`) can sit
  static indefinitely. Judged to have no functional consequence today
  (nothing reads "how long has this round been open" as a signal) — only
  a cosmetic one (an admin looking at that `GameKey` sees a round that
  never advances). Revisit if a real need for bounded round age surfaces
  (e.g. per-round reporting or admin tooling that assumes regular
  advancement).
- Negative / trade-off accepted: `RoundGenerationService`'s renewal branch
  updates `latest.EndTime` via a plain load-then-`UpdateAsync`, with no
  optimistic-concurrency token — consistent with `RoundCloseService`'s own
  existing update paths (no entity in this codebase has a concurrency
  column), not a new gap this ADR introduces. Worst case under a genuine
  race (two overlapping `generate-round` calls for the same `GameKey`) is
  one fewer `RoundDuration` extension applied than an ideal outcome —
  self-correcting on the next cron cycle given the mechanism is uncapped.
- Follow-up: `PredictRoundScoreSource.HasAnyParticipantAsync` is
  implemented but currently unreachable in production (REQ-305 excludes
  `"xg-predict"` via `UsesModuleSuggestedTiming` before the participation
  check ever runs). If a future change gives xG Predict its own
  module-driven equivalent of "don't advance to a new matchday when the
  previous one went unplayed," that's a new requirement against
  `Games.XGPredict`, not a change to this ADR's chain-math mechanism.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change. In
particular: never add a new `RoundGenerationService` dependency on
`IGuessRepository` or any other single game's repository directly — a
sixth game's participation data must be reached only through its own
`IRoundScoreSource.HasAnyParticipantAsync` implementation, resolved via
`IRoundScoreSourceResolver`, the same way every other per-`GameKey`
scoring/participation question in this codebase is already resolved
(ADR-0100).
