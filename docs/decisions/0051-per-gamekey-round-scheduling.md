# ADR-0051: Per-GameKey round scheduling — resolver, single endpoint, shared cron, config split

- **Status:** Accepted
- **Date:** 2026-07-28
- **Related requirements:** REQ-1202, REQ-301, REQ-302
- **Related components:** COMP-03 (Core.Rounds), COMP-11 (Games.XGPath), COMP-05 (Games.XGGrid)

## Context

S-084 needed `"xg-path"` rounds to be generated on a schedule, the same way
`"xg-grid"` rounds already are (REQ-1202's round-structure acceptance
criteria explicitly require this — REQ-301's "one round ahead" and REQ-302's
round-lifecycle rules must hold for `"xg-path"` exactly as they do for
`"xg-grid"`, proven by test). Before this story, the scheduling machinery
only ever supported one `GameKey`:

- `RoundSchedulingOptions` was registered exactly once, as a plain singleton
  directly injected into `RoundGenerationService` — there was no mechanism
  to hold a second, independently-configured instance for a second
  `GameKey`.
- `RoundGenerationService.GenerateNextRoundIfNeededAsync` had no `gameKey`
  parameter at all; it always operated against whichever single
  `RoundSchedulingOptions` was injected.
- `/internal/generate-round` hardcoded xG-Grid-specific template resolution
  inline (`GridTemplateResolver.GetOrCreateBySizeAsync`), with no way to
  produce an xG-Path `TemplateId` instead.
- `RoundSchedulingOptions` itself carried a `GridSize` field — xG-Grid-only
  generation config riding on an otherwise game-agnostic scheduling type.
- `generate-round.yml`'s single daily cron only ever triggered one
  generation call.
- ADR-0027 established `RoundDuration >= generate-round.yml`'s cron's max
  gap between firings (a constant 24h, since the cron is daily) as a safety
  invariant — derived under the assumption of exactly one `GameKey`/cron
  relationship.

This codebase had already solved an equivalent "resolve the right
game-specific thing by `GameKey` string" problem twice before: `IGameModule`
via `IGameModuleResolver`, and `IScoringStrategy` via `IScoringStrategyResolver`
(ADR-0040) — both backed by `IEnumerable<T>` registrations, each carrying its
own `GameKey` property, resolved by a simple linear lookup. `RoundSchedulingOptions`
already happened to carry a `GameKey` field, so it was already shaped
compatibly with that same pattern; it just wasn't wired that way yet.

Per this story's own text, the choice of how to wire `generate-round.yml`
(extend the existing job vs. add a second scheduled invocation) was
deliberately left open for `architecture-reviewer` to decide rather than
being decided silently — that consultation happened before implementation
started, and its recommendation is what this ADR records.

## Decision

Four related changes, made together as one coherent design:

1. **New `IRoundSchedulingOptionsResolver`** (`Core.Rounds`), mirroring
   `IScoringStrategyResolver`'s exact shape: `Resolve(string gameKey)`
   backed by `IEnumerable<RoundSchedulingOptions>`, throwing
   `InvalidOperationException` for an unregistered `GameKey`.
   `RoundGenerationService`'s constructor now takes this resolver instead of
   a directly-injected `RoundSchedulingOptions` singleton, and
   `GenerateNextRoundIfNeededAsync` gained a leading `string gameKey`
   parameter, resolving the right options internally. Two
   `RoundSchedulingOptions` instances are now registered in `Program.cs`
   (one per `GameKey`), each with its own `RoundDuration` sourced from a
   distinct configuration key (`RoundScheduling:RoundDurationHours` for
   `xg-grid`, unchanged for back-compat; `RoundScheduling:XGPath:RoundDurationHours`
   for `xg-path`, new).

2. **`/internal/generate-round` stays one endpoint**, gaining an optional
   `gameKey` query parameter (default `"xg-grid"`, so any caller that
   doesn't pass it keeps today's behavior unchanged). Inside the handler, a
   narrow `gameKey switch` — the *only* place in the entire request
   pipeline that branches on `GameKey` — resolves nothing but the opaque
   `RoundConfig.TemplateId`, dispatching to either `GridTemplateResolver`
   or the new `PathTemplateResolver` (mirroring it exactly for
   `PathTemplate`). Everything else in the handler (bearer-token auth,
   `roundDurationHours` floor validation, the new up-front `gameKey`
   validity check added as a quality-gate follow-up, calling
   `RoundGenerationService`, exception handling, response shape) stays
   fully generic across every `GameKey`. A second endpoint was rejected:
   every part of the handler except template resolution is already
   game-agnostic, so a second endpoint would duplicate that boilerplate for
   no boundary benefit.

3. **`generate-round.yml` extends its existing single daily-cron job**,
   looping over both `GameKey`s with independent per-`GameKey` retry logic
   (a bash function called once per `GameKey`, so one `GameKey`'s exhausted
   retries never block the other's attempt) — rather than adding a second
   `on.schedule` entry or a matrix strategy. **ADR-0027 addendum:** its
   `RoundDuration >= cron's max gap` invariant is now checked *per GameKey*
   against the same shared daily cron, not re-derived for a second cadence
   — this is exactly why the existing job was extended instead of a second
   schedule being added. A second cron (even at the same nominal cadence)
   would require re-deriving that invariant by hand for a new firing
   pattern, for no benefit: nothing about REQ-1202 requires `"xg-path"` to
   run on a different *schedule*, only potentially a different
   `RoundDuration`, and both games can safely share one daily trigger even
   with independent durations.

4. **`GridSize` moved off `RoundSchedulingOptions` onto
   `GridGenerationOptions`** (`Games.XGGrid`, which already exists for
   exactly this kind of xG-Grid-only generation config); a new
   `PathGenerationOptions` (`Games.XGPath`, holding `PuzzleCount`) was added
   rather than adding a `PuzzleCount` field to `RoundSchedulingOptions`.
   Leaving `GridSize` in place would have meant either a meaningless field
   on the `"xg-path"` instance, or adding a matching `PuzzleCount` sibling —
   accumulating one field per game on a type meant to be generic, the same
   anti-pattern ADR-0003/ADR-0040 already exist to prevent one layer up.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keyed/named DI (.NET keyed services) for `RoundSchedulingOptions` instead of an `IEnumerable`-backed resolver | Framework-native, no new interface | Inconsistent with the two existing precedents (`IGameModuleResolver`, `IScoringStrategyResolver`) already solving this exact "pick the game-specific thing by `GameKey` string" problem the same way | Consistency with established precedent outweighs the marginal convenience; introducing a third resolution mechanism for the same kind of problem would be its own inconsistency |
| A second `/internal/generate-round/xg-path` endpoint | Fully separates each game's template-resolution logic, zero shared branching | Duplicates auth, `roundDurationHours` validation, exception handling, and response shape across two endpoints for no boundary benefit — the shared code is the vast majority of the handler | The duplication cost is real and ongoing (every future generic change to this endpoint would need to land twice); the branching this avoids is already narrow and isolated |
| A second scheduled workflow/cron entry (or a matrix strategy) in `generate-round.yml` | Cleaner separation of "two independent jobs" conceptually; one `GameKey`'s failure can't affect the other's job-level status | Requires re-deriving ADR-0027's `RoundDuration >= cron max gap` safety invariant for a new/second cadence — real, avoidable work and a real, avoidable new failure mode, for no benefit since nothing requires a different *schedule* for `"xg-path"` | The reliability cost of a second cron (a second timing-drift surface to reason about) outweighs the marginal isolation benefit, especially since per-`GameKey` retry independence is already achieved via the bash-function-per-`GameKey` loop |
| Fully "zero branching" template resolution — add `Task<Guid> GetOrCreateDefaultTemplateIdAsync(CancellationToken)` to `IGameModule` itself, so the endpoint calls `gameModuleResolver.Resolve(gameKey).GetOrCreateDefaultTemplateIdAsync(ct)` with no per-`GameKey` switch at all | Architecturally the cleanest — genuinely zero `GameKey` branching anywhere in the API layer | A real `IGameModule` interface change touching `GridGameModule` too; bigger than S-084's stated scope with no evidence a third game needs it yet | `MVP-SCOPE.md`'s "don't pull forward more than needed" principle argues against this until a third game module actually makes the narrow two-armed switch unwieldy |
| Add `PuzzleCount` directly to `RoundSchedulingOptions` alongside the existing `GridSize` | Smallest possible diff — no new options class, no field relocation | `RoundSchedulingOptions` would accumulate one field per game module, exactly the anti-pattern a `GameKey`-resolved scoring/module pattern exists to avoid one layer up (ADR-0003/ADR-0040) | Explicitly rejected in favor of moving `GridSize` out and giving xG Path its own `PathGenerationOptions`, keeping `RoundSchedulingOptions` a genuinely generic, per-`GameKey` scheduling concern |

## Consequences

- Positive: a third game module can be scheduled in the future by
  registering one more `RoundSchedulingOptions` instance and adding one
  more arm to the endpoint's template-resolution switch — no change to
  `RoundGenerationService`, `IRoundSchedulingOptionsResolver`, or
  `generate-round.yml`'s retry mechanism.
- Positive: `"xg-grid"` and `"xg-path"` can each have an independently
  configured `RoundDuration` (proven by test, not just by code shape —
  `RoundGenerationServiceTests.cs`'s new two-`GameKey` tests) without
  either affecting the other's schedule or lifecycle.
- Positive: `RoundSchedulingOptions` stays a genuinely generic scheduling
  type; game-specific generation config lives on each game's own options
  class (`GridGenerationOptions`, `PathGenerationOptions`), matching the
  pattern this story extended rather than deviating from it partway.
- Negative / trade-off accepted: the `gameKey switch` in
  `InternalRoundEndpoints.cs` is a narrow, deliberate exception to
  "never branch on `GameKey` in code" — justified because it's isolated to
  producing one opaque ID (`TemplateId`) at the API composition-root layer,
  not spread into `Core.*`, and because the fully-generic alternative
  (`IGameModule.GetOrCreateDefaultTemplateIdAsync`) was judged premature for
  a two-game platform. Revisit if a third game module makes this switch
  unwieldy.
- Negative / trade-off accepted: `generate-round.yml`'s single job now does
  twice the scheduled work per firing (two `curl` calls with independent
  retries instead of one) — judged acceptable since each call is cheap and
  idempotent, the same trade-off ADR-0027 already accepted for the daily
  cron firing more often than strictly necessary for a single `GameKey`.
- Follow-up: if a third game is added, re-check whether the endpoint's
  `gameKey switch` (now three arms) is still preferable to the deferred
  `IGameModule.GetOrCreateDefaultTemplateIdAsync` alternative noted above —
  don't assume the two-arm answer still holds without re-deriving it.

**Amendment (2026-08-30, xG Predict wiring):** the third game arrived
(`"xg-predict"`, REQ-1301, ADR-0096) and this follow-up's re-derivation was
done rather than assumed. The three-armed switch stays preferable, unchanged
from the two-armed answer:

- The new arm is mechanically identical in shape to the existing two —
  `PredictTemplateResolver.GetOrCreateByMatchCountAsync` resolving
  `PredictGenerationOptions.MatchCount` to a `PredictTemplate.Id`, same
  one-line find-or-create-by-config-value pattern `GridTemplateResolver`/
  `PathTemplateResolver` already establish. Nothing about adding it grew the
  switch's shape or its surrounding handler.
- The switch is still confined to this one composition-root location,
  producing nothing but the opaque `TemplateId` — it has not spread into
  `Core.*`, and three near-identical one-line arms is not the "unwieldy"
  this ADR's Consequences section named as the actual trigger to revisit.
- The `IGameModule.GetOrCreateDefaultTemplateIdAsync` alternative would
  still require a real interface change touching `GridGameModule` and
  `XGPathGameModule` too (not just `XGPredictGameModule`), for a benefit
  (zero `GameKey` branching in the API layer) that remains marginal at
  three near-identical arms. `MVP-SCOPE.md`'s "don't pull forward more than
  needed" principle still argues against taking on that larger, riskier
  change now, with no new evidence since the original two-arm decision that
  it's actually needed.

No new ADR: this reconfirms an existing decision after the re-derivation its
own Follow-up note required, rather than changing it. Revisit again if a
fourth game or a genuinely different template-resolution shape ever makes
the switch actually unwieldy, not preemptively.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.

Specifically: do not add a third field to `RoundSchedulingOptions` for a
new game's generation config — add it to that game's own options class
instead (see `GridGenerationOptions`/`PathGenerationOptions` for the
precedent). Do not add a second `/internal/generate-round*` endpoint or a
second scheduled cron entry for a new `GameKey` without re-deriving why
this ADR's reasoning no longer applies. If `generate-round.yml`'s cron
cadence ever changes away from daily, ADR-0027's own "For AI agents"
section's re-derivation requirement still applies — this ADR's "checked
per-GameKey against the same shared cron" framing assumes ADR-0027's
current daily/24h invariant, not a replacement for it.
