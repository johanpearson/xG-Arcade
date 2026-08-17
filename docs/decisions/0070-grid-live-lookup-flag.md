# ADR-0070: REQ-211's guess-time live-lookup fallback is now a config flag, not always-on

- **Status:** Accepted
- **Date:** 2026-08-17
- **Related requirements:** REQ-211, REQ-103 (explicitly unaffected), REQ-509/510
- **Related components:** COMP-05 (Games.XGGrid)

## Context

REQ-211's guess-time live-lookup fallback (`GridGameModule.ScoreSubmissionAsync`,
ADR-0018) re-runs a cell's own Wikidata intersection query when a guess that
matched a real `PlayerNameIndex` candidate isn't already answered by cached
data. It was pulled into Tier 0 on 2026-07-10, specifically because real,
correct guesses (Messi for Argentina×Barcelona, among others) were being
wrongly rejected without it — see ADR-0018's own Context section and
`MVP-SCOPE.md`'s documented trigger for pulling it forward early.

S-127 just finished proactively widening `PlayerCareerPrefetchService`'s
sweep (nationality and, as of ADR-0069, club) so the local player-data cache
covers far more of the guessable pool before a round ever starts. The
product owner now wants to find out, empirically, whether that proactive
build is complete enough on its own to retire the guess-time fallback — but
given ADR-0018's own history (a real, reported correctness bug is exactly
what justified building this fallback in the first place), removing it
outright and finding out the hard way — via a fresh wave of wrongly-rejected
correct guesses — is too risky to do blind. The admin player-suggestion
approve/commit flow (REQ-509/510) already exists as a remediation path for
any genuine gap a player hits, giving the product owner a safety net to
lean on while testing with the fallback off.

## Decision

Add `GridLiveLookupOptions.Enabled` (default `true`), config-driven via
`GridLiveLookup:Enabled` (env var `GridLiveLookup__Enabled`), the same
override convention `RoundScheduling:RoundDurationHours` already
establishes. `GridGameModule.ScoreSubmissionAsync` checks this flag
immediately before its existing `PlayerNameIndex` gate (ADR-0046's status
note) — when `Enabled` is `false`, it returns the unresolved cached result
immediately, skipping both `IPlayerNameIndexRepository
.ExistsByNormalizedNameAsync` and `IGridLiveLookupDispatcher
.TryRefreshCellAsync` entirely. This is deliberately byte-for-byte the same
outcome an unresolved guess had before REQ-211 existed at all: fail closed
(scored incorrect), same `ScoreResult` shape, no new error/HTTP status, no
different UX — "off" is not a new code path, it's the absence of the one
REQ-211 added.

The flag gates only `GridGameModule`'s own call into
`IGridLiveLookupDispatcher.TryRefreshCellAsync` — REQ-103's
grid-generation-time live lookup (`GridGenerationService.GetMatchCountAsync`
→ `IGridLiveLookupDispatcher.LookupMatchesAsync`) is a separate call path
through the same shared dispatcher and is completely unaffected. The flag
lives one layer up, at `GridGameModule`'s call site, not inside
`GridLiveLookupDispatcher` itself — the dispatcher has no notion of which of
its two callers invoked it, and adding one there would risk silently
widening the flag's scope to REQ-103 by accident.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Remove the fallback outright | Simplest code, no new config surface | ADR-0018's own history shows this exact fallback exists because removing/never-building it produced real, reported wrong rejections; removing it blind risks repeating that with no fast way back except a redeploy | Too risky to do blind — the product owner wants to validate S-127's coverage empirically first, with an instant way back |
| Gate inside `GridLiveLookupDispatcher.TryRefreshCellAsync` itself | Fewer call sites to check | The dispatcher is shared by both REQ-103 (`LookupMatchesAsync`) and REQ-211 (`TryRefreshCellAsync`) call paths; while `TryRefreshCellAsync` alone is REQ-211-only today, putting flag logic inside the shared class blurs a boundary that's currently obvious from the call site, and risks a future refactor accidentally leaking the flag into `LookupMatchesAsync` | Keep the flag at the one call site (`GridGameModule.ScoreSubmissionAsync`) that owns the REQ-211-specific decision, not inside infrastructure both paths share |
| A new distinct `GuessSubmissionOutcome`/HTTP status for "fallback disabled" | Makes the toggle's effect visible to the client | REQ-211's whole premise is "never wrongly told I'm wrong" — a disabled flag isn't a new failure mode, it's reverting to pre-REQ-211 behavior; a new status would imply something broke, when nothing did | Fail closed exactly as before REQ-211 existed — no new outcome to handle across the boundary |

## Consequences

- Positive: the product owner can flip `GridLiveLookup:Enabled` (env var
  override, no redeploy of code) to test whether S-127's cache is complete
  enough on its own, and flip it back instantly if wrongly-rejected guesses
  start appearing again.
- Positive: default `true` means every existing deployment, and every
  existing test that doesn't construct `GridLiveLookupOptions` explicitly,
  is completely unaffected.
- Positive: REQ-103's grid-generation-time live lookup is unaffected by
  construction — the flag isn't threaded anywhere near
  `GridGenerationService`/`LookupMatchesAsync`.
- Negative / trade-offs accepted: while the flag is off, a genuinely correct
  guess for a player with no cached data for this specific cell will be
  wrongly scored incorrect — the exact bug ADR-0018 fixed. This is the
  deliberate cost of the experiment; REQ-509/510's suggestion flow is the
  intended remediation path for the product owner to observe and correct any
  gap surfaced this way, not a substitute for turning the flag back on if
  gaps turn out to be common rather than rare.
- Follow-up: once the product owner has enough signal, either flip the
  default to `false` (keeping the flag as a documented escape hatch) or
  retire the flag/fallback outright with a new ADR — this decision
  deliberately doesn't prejudge that outcome.

## For AI agents

Do not extend this flag to gate `GridGenerationService.GetMatchCountAsync`
or `IGridLiveLookupDispatcher.LookupMatchesAsync` directly — REQ-103's
grid-generation-time live lookup is a separate call path and out of scope
for `GridLiveLookupOptions`. If a future change needs a similar toggle for
REQ-103's own live lookup, that needs its own options type and its own ADR,
not a widened `GridLiveLookupOptions.Enabled`. Don't move this check inside
`GridLiveLookupDispatcher` — keep it at `GridGameModule`'s call site, per
this ADR's "why a flag, not inside the dispatcher" reasoning above. "Off"
must always mean the exact pre-REQ-211 fail-closed outcome — never a
different error, HTTP status, or `ScoreResult` shape.
