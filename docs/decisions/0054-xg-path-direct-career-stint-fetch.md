# ADR-0054: xG Path fetches its own targets' full career directly from Wikidata

- **Status:** Accepted
- **Date:** 2026-08-02
- **Related requirements:** REQ-1201, REQ-1203, REQ-1206 (xG Path)
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-11 (Games.XGPath)

## Context

ADR-0042 gave xG Path its own `PlayerCareerStint` table, but deliberately
populated it as a byproduct: `WikidataLookupService.LookupAndPersistAsync`
(the country/nationality × club intersection query xG Grid uses to fill a
grid cell) records career-stint qualifiers for whichever ONE club it was
scoped to, only when that exact `(nationality, club)` pair happens to get
queried — via `warm-player-cache` or a live guess-time miss. There is no
code path anywhere in this codebase that fetches a player's whole career.

This produced a real, reported gap: a live xG Path puzzle for Timothy Weah
showed no Juventus or Marseille stints (both real, documented on Wikipedia/
Wikidata) and no Celtic stint at all. Investigation found two compounding
causes: Celtic isn't in the seeded `ClubDefinition` reference table at all
(so no query could ever discover it, regardless of how much cache-warming
runs), and Juventus/Marseille — while seeded — simply hadn't had their
specific `(nationality, club)` pair queried yet. Both trace back to the same
structural fact: a player's `PlayerCareerStint` set is never more complete
than "whatever clubs xG Grid happened to ask about so far," which has
nothing to do with what xG Path actually needs (that specific player's real,
complete career).

## Decision

Give xG Path its own direct, per-player Wikidata query:
`IWikidataClient.QueryPlayerCareerStintsByQidsAsync` — a batched,
by-QID VALUES-clause query (same shape as `QueryPlayerPhotosByQidsAsync`/
`QueryPlayerPositionsAndBirthYearsByQidsAsync`) that fetches the FULL,
unrestricted P54 ("member of sports team") history for a batch of QIDs, not
just the one club a caller happens to already know about. Uses the same full
`p:P54`/`ps:P54` statement path (excluding deprecated rank) as every other
P54 query in this codebase — the truthy `wdt:P54` shortcut would make this
just as incomplete as the byproduct it replaces (see
`BuildCountryClubIntersectionQuery`'s own comment for why).

`XGPathGameModule.GenerateInstanceAsync` calls a new
`IPlayerCareerStintRefreshService.RefreshCareerStintsAsync`, in
`XGArcade.DataSync` (new project reference from `Games.XGPath`, mirroring
`Games.XGGrid`'s existing one), with exactly the N target-player ids it just
picked — immediately after `PickDistinct`, before the `PathInstance` is
persisted. The service fetches their full career, dedupes against whatever
`PlayerCareerStint` rows already exist (same tuple-based reconciliation
`WikidataLookupService.PersistCareerStintsAsync` already uses), and adds only
the genuinely new stints via the existing `AddCareerStintsBatchAsync`. It
never throws: a Wikidata failure logs a warning and leaves that player's
existing (possibly incomplete) data untouched, rather than failing the whole
round generation — the same REQ-103 "never block generation on a Wikidata
failure" reasoning xG Grid's own generation-time lookups already follow.

**Deliberately scoped to enrichment, not pool-widening.** `GetEligiblePlayerIdsAsync`
still decides eligibility from whatever `PlayerCareerStint` data already
existed BEFORE this refresh runs — the refresh can make an already-selected
target's own puzzle clues complete, but it cannot retroactively make a
previously-ineligible player (one who never triggered any xG Grid lookup at
all) eligible for the round currently being generated. Widening the
candidate pool itself is a separate, larger decision — see Follow-up below.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Do nothing; rely on `warm-player-cache` eventually covering more pairs | No new code | Never actually complete — xG Grid's query set is driven by grid-generation needs, not by "does this specific xG Path target have a full career recorded"; Celtic-class gaps (a club never seeded at all) can never close this way | Doesn't fix the reported bug, and never will on its own |
| Widen the candidate/eligibility pool itself (fetch full careers for every `PlayerNameIndex`/`Player` row, not just already-selected targets) | Fixes the deeper "which players can ever become targets" limitation too | Fetching career data for potentially thousands of players just to check eligibility is a much larger, riskier, slower change; changes the *probability distribution* of who becomes a target, a product-shape decision, not just a data-completeness one | Real follow-up (see below), but out of scope for fixing a live, reported data-completeness bug in *already-selected* puzzles |
| Refresh at request time (`GET /path/current`), not generation time | Always maximally fresh | A live Wikidata call on every read of an already-generated puzzle is unnecessary latency/cost for data that doesn't change between a puzzle's generation and its play window; `PathEndpoints` is a display-only read path (ADR-0016/ADR-0048) that has never made an external call | Generation-time is the natural "this player is now committed as a target" moment — one fetch per puzzle's whole life, not one per view |
| Direct per-player fetch at generation time (chosen) | Fixes the reported bug directly; small, bounded blast radius (N = `PathTemplate.PuzzleCount` players per generation); reuses the existing by-QID-batch query shape and stint-reconciliation logic; never blocks generation on failure | One more Wikidata call per round generation; `Games.XGPath` now depends on `DataSync` (a new edge, though already precedented by `Games.XGGrid`) | Best fit: fixes exactly the reported gap with a small, well-understood, already-precedented shape |

## Consequences

- Positive: an xG Path puzzle's clue data is now sourced from the target
  player's real, complete Wikidata career at the moment they're selected,
  not an accumulation of unrelated xG Grid lookups
- Positive: reuses every existing piece this codebase already has for this
  shape (by-QID VALUES query, tuple-based stint dedup, `AddCareerStintsBatchAsync`)
  — no new persistence model, no new external API surface
- Negative / trade-off accepted: one additional live Wikidata call per round
  generation (batched, small N) — never blocking (swallowed on failure), but
  a genuinely new external dependency in the generation path that didn't
  exist before this ADR
- Negative / trade-off accepted: `Games.XGPath` now has a compile-time
  dependency on `DataSync` — an intentional widening of COMP-11's dependency
  surface, precedented by `Games.XGGrid`'s identical relationship, not a new
  kind of edge in this codebase
- Follow-up: this does NOT widen who can ever become an xG Path target — a
  player who has never triggered any xG Grid lookup still can't be selected,
  since `GetEligiblePlayerIdsAsync` still reads only already-persisted
  `PlayerCareerStint` rows. If that's ever worth fixing, it needs its own
  design pass (likely: seed a broader career-stint pool proactively, not
  just react to what xG Grid happens to query) — not assumed or attempted
  here
- Follow-up (explicit product feedback, 2026-08-02): more generally, this
  codebase's whole player-data cache is built reactively/on-demand (a
  live-lookup waterfall, ADR-0011) rather than proactively — the product
  owner's stated preference is to invest in building up a broader, correct
  player dataset up front, which would make both xG Grid's guess-time
  fallback AND xG Path's target selection faster and more complete at once,
  rather than continuing to patch individual gaps (Celtic missing from
  `ClubDefinition`, specific `(nationality, club)` pairs never queried,
  players who never triggered any lookup at all) one at a time. This is a
  real, larger scope decision — widening the seeded club/country reference
  lists, and/or making `warm-player-cache` run on a recurring schedule
  instead of `workflow_dispatch`-only, and/or a genuinely different
  proactive-fetch strategy — that needs its own follow-up story and ADR, not
  bundled into this one.

## For AI agents

`GetEligiblePlayerIdsAsync`'s candidate pool must keep coming from
already-persisted `PlayerCareerStint` rows only — do not have it call
`IPlayerCareerStintRefreshService` to "check" a wider candidate set live;
that is the pool-widening decision explicitly deferred above, not something
to pull forward incidentally while touching this code. If a task seems to
need eligibility itself to depend on a live Wikidata call, stop and flag it
— that needs its own ADR, not a quiet extension of this one.
