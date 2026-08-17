# ADR-0069: Widen player-career prefetch to sweep seeded clubs, not just seeded countries

- **Status:** Accepted
- **Date:** 2026-08-17
- **Related requirements:** REQ-103, REQ-110, REQ-1201 (xG Path eligibility)
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-11 (Games.XGPath)

## Context

ADR-0055 built `PlayerCareerPrefetchService` (`dotnet run --
prefetch-player-careers`) to proactively fill `PlayerCareerStint` for
players independent of whatever xG Grid's own query history has happened
to touch — the real fix for xG Path's target-pool bottleneck
(`XGPathGameModule.GetEligiblePlayerIdsAsync`, REQ-1201). That ADR
deliberately scoped the candidate pool to already-seeded countries only
(`ICategoryValueRepository.GetCountriesAsync`, via the new
`IWikidataClient.QueryPlayerPoolByNationalityAsync`), and its own "For AI
agents" section was explicit that widening the pool source needs "a fresh
product decision," not a silent change — a decision it deliberately left
for later rather than making by default.

The gap that decision left open: a player who never held citizenship of
(or represented, for the four UK nations) any seeded country, but who did
play for a seeded club, is invisible to both `warm-player-cache.yml`'s
pairwise Country×Club/Club×Club sweep (which only warms already-known
pairs) and `PlayerCareerPrefetchService`'s own nationality-only sweep.
That player can never become an xG Path target, and never contributes
cached `PlayerCareerStint` data that would make a live xG Grid guess about
them resolve faster. This is the same class of "this player/club can never
appear" gap ADR-0055's own move (1) (seeding Celtic directly) was a
one-off, hand-curated fix for — but structural, not fixable by seeding one
more club.

The product owner has now made the fresh decision ADR-0055 deferred:
widen `PlayerCareerPrefetchService`'s pool to also sweep seeded clubs.

## Decision

`PlayerCareerPrefetchService.PrefetchAsync` now runs two sweeps in the
same invocation, not two separate CLI verbs or workflows:

1. **The existing country sweep**, unchanged byte-for-byte —
   `ICategoryValueRepository.GetCountriesAsync` →
   `IWikidataClient.QueryPlayerPoolByNationalityAsync` per seeded country.
2. **A new, symmetric club sweep** — `ICategoryValueRepository.GetClubsAsync`
   → a new `IWikidataClient.QueryPlayerPoolByClubAsync` per seeded club.
   Both sweeps feed the same `FetchAndPersistBatchAsync` helper (already
   source-agnostic — it takes a batch of `WikidataNameIndexEntry`, with no
   assumption about which sweep produced it), so a player discovered by
   either sweep is get-or-created and has its career stints written
   through the exact same path, with the existing tuple-dedup logic
   (`PlayerCareerStintRefreshService.BuildNewStintsByPlayerId`) preventing
   duplicate stints regardless of which sweep (or an earlier xG Grid
   byproduct lookup) discovered the player first.

`PlayerCareerPrefetchResult` gained `ClubsProcessed`/`ClubsFailed`
alongside the existing `CountriesProcessed`/`CountriesFailed`.
`PlayersTouched`/`StintsAdded`/`CareerBatchesFailed` stay combined totals
across both sweeps — splitting those three by source would require
plumbing a "which sweep found this player first" distinction that nothing
downstream reads.

**Correctness-critical implementation constraint:** `QueryPlayerPoolByClubAsync`'s
new query must use P54's full statement path (`p:P54`/`ps:P54`, excluding
deprecated rank), never the truthy `wdt:P54` shortcut. Wikidata's truthy
`wdt:` graph exposes only best/preferred-rank statements, and editors
routinely mark a player's *current* club preferred — so `wdt:P54` would
silently return only current-squad members and hide everyone who used to
play there. This is the exact same rule every other P54-involving query in
this codebase already follows (`IntersectionQuerySpecs.
BuildCountryClubIntersectionQuery`'s own comment has the full incident —
Sandro Tonali x AC Milan, `NOTES.md` 2026-07-17), applied here for the
same reason: the whole point of this story is "everyone who ever played
for this club," not "this club's current squad."

A club with no `WikidataQid` yet is skipped, not a failure — same
precedent the existing country loop's `if (country.WikidataQid is null)
continue;` already set (REQ-109).

No new CLI verb or workflow: this widens the existing
`prefetch-player-careers` verb and `prefetch-player-careers.yml` workflow.

## Alternatives considered

| Option | Pros | Cons | Why (not) chosen |
|---|---|---|---|
| Do nothing — keep the nationality-only scope | No new work | The reported gap class ("this player is invisible to both sweeps") persists indefinitely for any player without a seeded nationality | Rejected — the product owner made the fresh decision ADR-0055 deferred, specifically to close this gap |
| Widen the pool source generically (e.g. every `PlayerNameIndex` entry) instead of adding a second seeded-reference sweep | Maximum completeness in one pass | `PlayerNameIndex` still has no `WikidataQid` column (ADR-0007) — the same structural blocker ADR-0055's own "Correction" note already discovered when this was tried for the country sweep; would also reopen the "no natural batch boundary, unknown WDQS load" risk ADR-0055 explicitly rejected | Not reconsidered — the underlying blocker and risk are identical to what ADR-0055 already ruled out; nothing has changed since then |
| A second CLI verb/workflow dedicated to the club sweep | Cleaner separation, independently schedulable | Two near-identical jobs to maintain, two near-identical workflows, and no actual reason the two sweeps can't share one invocation/run — `FetchAndPersistBatchAsync` is already source-agnostic | Rejected — adds process without adding value; the two sweeps are symmetric, not independent concerns |
| Widen the club sweep's pool query with the truthy `wdt:P54` shortcut instead of the full statement path | Simpler query | Reproduces the exact "current club hides historical clubs" bug this codebase already fixed once for every other P54-involving query (2026-07-17 incident) | Rejected outright — this is the one non-negotiable implementation detail this ADR exists partly to pin down for future maintainers |

## Consequences

- Positive: closes the "player with an unseeded nationality who played for
  a seeded club is invisible to both sweeps" gap ADR-0055 deliberately
  left open, without reopening the `PlayerNameIndex`-source blocker ADR-0055
  already ruled out
- Positive: both xG Grid (more cached data → fewer live 15-28s lookups)
  and xG Path (a wider target pool, REQ-1201) benefit, same as ADR-0055's
  original country sweep
- Negative / trade-off accepted: real new WDQS query volume, on top of
  ADR-0055's existing country-sweep volume — needs the same monitoring
  discipline ADR-0055's own Consequences section already established
  (watch for a persistent failure pattern against the same club, not just
  raw failure count)
- Negative / trade-off accepted: a large, historically significant club's
  full all-time squad (e.g. a top European club with a century-plus of
  documented players) is an unproven query size for this specific query
  shape — ADR-0055's own country-pool query was confirmed safe even for
  huge pools (United Kingdom: 18,460 players) after a real run, but that
  confirmation does not automatically transfer to a differently-shaped
  P54-statement-path query; flagged as an open risk, not assumed safe,
  same posture ADR-0055 took for its own new query before its first real
  run confirmed it
- Follow-up: once a real run confirms the club-pool query's cost/runtime
  the same way ADR-0055's country-pool query was confirmed, revisit moving
  `prefetch-player-careers.yml` off `workflow_dispatch`-only, per that
  ADR's own already-pending follow-up

## For AI agents

`PlayerCareerPrefetchService`'s candidate pool now sources from
`ICategoryValueRepository.GetCountriesAsync` AND `GetClubsAsync` — do not
widen it further (e.g. to an unscoped `PlayerNameIndex`-wide pool) without
another fresh product decision; ADR-0055's own "For AI agents" note about
that specific blocker still applies unchanged.

`IWikidataClient.QueryPlayerPoolByClubAsync`'s P54 clause MUST stay on the
full statement path (`p:P54`/`ps:P54`, excluding deprecated rank) — never
simplify it to the truthy `wdt:P54` shortcut. This is not a style
preference; doing so silently narrows "everyone who ever played for this
club" down to "this club's current squad," the exact bug
`IntersectionQuerySpecs.BuildCountryClubIntersectionQuery`'s own comment
documents for every other P54-involving query in this codebase. If you are
about to add or modify any query touching P54, use the full statement path
by default and only deviate with a comment explaining why that specific
case is safe (same as `BuildTrophyCountryIntersectionQuery`'s P166 comment
does for its own truthy-shortcut judgment call).
