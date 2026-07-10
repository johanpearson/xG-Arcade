# ADR-0016: Read-only display queries against an already-generated instance may bypass IGameModule

- **Status:** Accepted
- **Date:** 2026-07-10
- **Related requirements:** REQ-303
- **Related components:** COMP-03 (Core.Rounds), COMP-05 (Games.XGGrid, and any future game module)

## Context

S-010 (Grid UI) added `GET /rounds/current` (`XGArcade.Api.Rounds.RoundEndpoints`,
REQ-303) so the frontend can fetch the active round's grid content and the
caller's own guess state. To build the response, the endpoint reads the
full `GridInstance`/`GridCell` list directly via `IGridInstanceRepository`,
never going through `IGameModule`.

`IGameModule` (`XGArcade.Core.Games`) has exactly two methods today —
`GenerateInstanceAsync` and `ScoreSubmissionAsync` — no read/query method at
all. `architecture-reviewer` flagged this during S-010's review: ADR-0003's
boundary rule 2 says resolving `GameInstanceId` into an actual instance is
"the responsibility of the owning game module, reached through
`IGameModule`" — and unlike `GridTemplateResolver` (already blessed by
architecture-document.md §6.1 for a different reason: `GridTemplate` isn't
player data, and it's resolved *before* generation, not the instance
itself), reading `GridInstance`/`GridCell` content **is** exactly the
instance-resolution ADR-0003 was written about. The existing note that
blesses `GridTemplateResolver` does not, on its own terms, extend to this.

The available fix that keeps the boundary intact — adding a generic
read/view method to `IGameModule` — runs into the same tension ADR-0003
itself already named: any shape generic enough to serve a hypothetical
second game (which doesn't exist yet) either leaks the current game's
concepts into `Core.Games` anyway (e.g. "cells," "row/col category type" —
xG-Grid-specific vocabulary, not obviously meaningful to every possible
future game) or falls back to an untyped `object` return that the Api layer
would still have to downcast per game, which doesn't actually close the
coupling `architecture-reviewer` flagged — it just relocates it while
losing compile-time safety. ADR-0003's own "Follow-up" note anticipates
this exact situation: "when a second game module is actually built, use it
to verify this pattern holds — if `Core.Rounds` ends up needing
game-specific branching logic anyway, that's a signal this ADR needs
revisiting." Designing the generalized read interface now, before a second
game exists to prove what shape it actually needs, is the premature
abstraction `MVP-SCOPE.md`/`CLAUDE.md` explicitly warn against.

## Decision

Read-only, non-scoring queries against an already-generated game instance
— for display purposes only, never for generation or answer-checking — may
read the owning game module's own repository interface directly from the
Api layer, bypassing `IGameModule`. This is narrower than "the Api layer
may always bypass `IGameModule`": it does **not** apply to generation
(`GenerateInstanceAsync`) or scoring (`ScoreSubmissionAsync`), which remain
the only two paths through `IGameModule` and must never be bypassed. It
applies only to reads whose sole purpose is composing a client-facing
response — today, `RoundEndpoints.cs`'s `GET /rounds/current`.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Add a generic read/view method to `IGameModule` now (e.g. `GetInstanceViewAsync` returning a game-agnostic view DTO) | Keeps the Api layer fully decoupled from any one game's instance shape, matching ADR-0003's stated goal exactly | The "generic" shape can only be guessed at with one game module in existence — designing it now risks guessing wrong and having to redesign it against the second game's real needs anyway; contradicts ADR-0003's own "verify against a second game module" follow-up plan | Premature — no second game exists yet to validate the shape against |
| Return `object` from `IGameModule` and downcast per game in the Api layer | Technically routes through the interface | Doesn't close the coupling at all — the Api layer still has to know the concrete shape to downcast to, just with worse compile-time safety than calling the repository directly | Theater, not a real fix |
| Accept the direct repository read for display-only queries, documented here (chosen) | No premature interface design; matches the project's existing "flag and defer, don't speculatively build" pattern from ADR-0003 itself; the actual coupling is narrow (one Api endpoint, read-only, no scoring/generation logic) | `RoundEndpoints.cs` has compile-time knowledge of `GridInstance`/`GridCell`'s shape — a second game module would need its own equivalent endpoint (or an `if (round.GameKey == ...)` branch here), not a free ride through this one | Accepted as the Tier 0-scoped trade-off; revisit per the follow-up below |

## Consequences

- Positive: no interface designed against a single data point; `RoundEndpoints.cs` stays simple and matches the already-established `GridTemplateResolver` precedent for "the Api layer may read non-scoring, non-player data directly" — extended here to instance content specifically for display, not generation
- Negative / trade-offs accepted: `RoundEndpoints.cs` is coupled to xG Grid's `GridInstance`/`GridCell` shape; adding a second game module will require either a second, game-specific display-read endpoint, or revisiting this ADR to design a real `IGameModule` read method once there are two real shapes to generalize from
- Follow-up: when a second game module is actually built (same trigger ADR-0003 itself already named), use it to design `IGameModule`'s read method for real, informed by both games' actual instance shapes — supersede this ADR at that point rather than letting the direct-repository-read pattern spread to more endpoints

## For AI agents

This ADR covers **display reads only** — `GET /rounds/current` and any
future read-only, non-scoring endpoint in that same class. It does **not**
permit bypassing `IGameModule` for generation or scoring; those two paths
(`GenerateInstanceAsync`/`ScoreSubmissionAsync`) are still the only way
`Core.Rounds`/the Api layer may create or judge a game instance. If a task
seems to need a third case bypassing `IGameModule`, stop and flag it rather
than assuming this ADR already covers it.
