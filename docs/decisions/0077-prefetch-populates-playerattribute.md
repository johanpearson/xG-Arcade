# ADR-0077: Populate `PlayerAttribute` from `prefetch-player-careers`' bulk pool sweeps, eliminating live pairwise Wikidata queries for fully-swept pairs

- **Status:** Accepted
- **Date:** 2026-08-18
- **Related requirements:** REQ-110, REQ-103, REQ-1201
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-05 (Games.XGGrid)

## Context

`PlayerCacheWarmingService` (`warm-grid-cache.yml`, REQ-110) proactively
fills `PlayerAttribute` (COMP-06) for every seeded Country×Club and Club×Club
pair so real grid generation rarely has to gamble on a live lookup. For any
pair not already at `MinValidAnswers`
(`PlayerAttributeRepository.CountPlayersWithBothAttributesAsync` — a pure
local SQL join, no Wikidata involved), it falls back to a live pairwise
Wikidata SPARQL intersection query.

A 2026-08-18 run of `warm-grid-cache.yml` (2,145 pairs checked) showed this
fallback path costing more than intended: of the 199 pairs that needed a
live query, **all 199 (100%)** ended in a technical failure (timeout/HTTP/
parse error), concentrated entirely in the Club×Club loop on large,
historically-stacked clubs (Manchester City, Bayern Munich, Real Madrid,
Barcelona, PSG, etc. × dozens of other clubs) — the same class of
"combinatorial row explosion" query-shape incident `PairLookupFailure`'s own
doc comment already documents. `PersistentFailureThreshold` (2 consecutive
run-level failures, ADR-0052) means a first-time failure is retried live on
the very next run rather than skipped, so this class of pair burns real
WDQS query cost and CI time on every run without a structural fix.

Separately, `PlayerCareerPrefetchService` (`prefetch-player-careers.yml`,
ADR-0055/ADR-0069) already runs two *unpaired*, full-pool sweeps over the
exact same seeded reference data `warm-grid-cache` iterates pairwise:
`QueryPlayerPoolByNationalityAsync` per seeded country and
`QueryPlayerPoolByClubAsync` per seeded club. Every player either query
returns satisfies that attribute **by construction of the query's own WHERE
clause** — no separate Wikidata read-back is needed to know a pooled
player's nationality or club membership once the pool itself has been
fetched. Before this ADR, `PlayerCareerPrefetchService` discarded that fact:
it only used the pool to get-or-create `Player` rows and fetch career
stints for `PlayerCareerStint` (xG Path's data source, ADR-0042), never
writing anything to `PlayerAttribute`.

This meant two jobs already covering the identical seeded-reference universe
were structurally unable to help each other: `prefetch-player-careers`
already had the exact facts `warm-grid-cache`'s local join needed, and threw
them away.

## Decision

`PlayerCareerPrefetchService`'s country loop now also persists
`PlayerAttribute { AttributeType = "nationality", AttributeValue =
country.Name }` for every player in that country's pool; its club loop now
also persists `PlayerAttribute { AttributeType = "club", AttributeValue =
club.Name }` for every player in that club's pool. Both are deduped
per-country/per-club against what's already stored, using the same
fetch-once/`HashSet<Guid>`-gate pattern `WikidataLookupService.QueueAttribute`
already establishes for the pairwise-intersection write path, and written in
batches via the existing `AddPlayerAttributesBatchAsync` (never one row per
player).

`PlayerCacheWarmingService` itself is **unchanged**. Its existing
`cachedCount >= options.MinValidAnswers` pre-check was always a pure local
join; this decision doesn't add a new code path there, it makes the data
underneath that existing check complete for any pair where both reference
values have been swept by `prefetch-player-careers`. Once both sides of a
Country×Club or Club×Club pair are locally known, that check is the
complete, correct answer, and the live pairwise SPARQL query for that pair
is never issued — including the exact combinatorial big-club joins that were
failing 100% of the time.

The club-name value written here is sourced from the same
`clubNameByClubQid` map `PlayerCareerPrefetchService` already builds for
`PlayerCareerStint.ClubName` — this is load-bearing: it must stay
byte-identical to the `club.Name`/`ClubAttributeType` value
`PlayerCacheWarmingService`'s own club loop and
`CountPlayersWithBothAttributesAsync` match against, or the local join
silently misses.

This is a deliberate, narrow reversal of ADR-0001's "`PlayerAttribute` only
ever grows from combinations an actually-generated grid has needed"
principle, scoped strictly to the seeded-reference-data subset both jobs
already sweep — not a general bulk-import of `PlayerAttribute` for an
unbounded player pool. `PlayerNameIndex` (COMP-10, ADR-0007/ADR-0053) is
untouched by this change, in either direction.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Give `prefetch-player-careers.yml` its own skip-shortcut for previously-failed fetches (S-153, already backlogged) instead | Addresses that job's own re-sweep cost | Does nothing for `warm-grid-cache`'s pairwise combinatorial-join timeouts — a separate, unrelated failure mode on a separate job | Solves a different problem; not mutually exclusive with this decision, but doesn't substitute for it |
| Fix the Club×Club query shape itself (e.g. restructure `BuildClubClubIntersectionQuery` to avoid the combinatorial explosion) | Keeps the live-query architecture, fixes the root query-shape cost | Unknown whether a shape fix is even possible for very large clubs without changing what the query returns; doesn't reduce WDQS load, just makes each call cheaper; large clubs' pairwise counts still queried on every uncached run | Not ruled out for the future, but this decision gets a bigger win (eliminating the query entirely for the covered universe) for comparable implementation cost, without needing to re-derive a working SPARQL shape for an already-known-hard query class |
| Raise `PersistentFailureThreshold` or widen the cache-warming timeout further | Cheap, no new write path | Doesn't fix anything — a structurally-too-large join still fails, just after more wasted attempts/longer waits | Treats the symptom, not the cause; the run data shows 100% failure, not marginal timeout tightness |
| Do nothing; accept the current failure rate as a cost of using live pairwise queries | No new code | Real WDQS load and CI minutes spent every run re-attempting queries that are demonstrably certain to fail for large-club combinations, with the underlying data to avoid this already being fetched and thrown away by a sibling job | Rejected — the data this decision needs already exists in the exact response `prefetch-player-careers` already receives; discarding it was the actual bug |

## Consequences

- Positive: eliminates the live pairwise SPARQL intersection query — including
  the exact combinatorial-join queries that were failing 100% of the time on
  large-club pairs — for any Country×Club or Club×Club pair where both
  reference values have been fully swept by `prefetch-player-careers`, since
  that's the entire seeded-reference universe `warm-grid-cache` iterates.
- Positive: no code change needed in `PlayerCacheWarmingService` — its
  existing skip-logic simply becomes correct more often as the underlying
  data completes.
- Positive: shrinks and reshapes WDQS query volume from ~2,145 pairwise
  intersection queries down to the 49+33=82 broad per-reference queries
  `prefetch-player-careers` already runs.
- Negative / trade-off accepted: `ConfirmedLowMatchPair`/`PairLookupFailure`
  (ADR-0050/ADR-0052) become largely redundant for any pair fully covered by
  both sweeps — not removed in this change, left as a future simplification
  once the local-derivation coverage is confirmed in practice.
- Negative / trade-off accepted: staleness profile for `PlayerAttribute`
  rows written this way now matches `prefetch-player-careers`' own
  weekly-ish sweep cadence, not "as fresh as the last grid that happened to
  need this pair" — a transfer the day after a sweep is invisible until the
  next one. REQ-211's guess-time live-lookup fallback remains the safety net
  for any gap, unchanged by this decision.
- Negative / trade-off accepted: `PlayerCareerPrefetchService` now has a
  `IPlayerAttributeRepository` dependency it didn't have before, and its
  per-country/per-club sweep does one extra batched read
  (`GetPlayerAttributesAsync`) and up to one extra batched write
  (`AddPlayerAttributesBatchAsync`) per reference value — bounded, batched
  cost, not per-player round trips.
- Follow-up: once real production runs confirm `warm-grid-cache`'s live-query
  volume has actually dropped as predicted, revisit whether
  `ConfirmedLowMatchPair`/`PairLookupFailure` can be simplified or removed
  for the now-fully-covered pairs, and whether a swept-but-genuinely-low
  pair (below `MinValidAnswers` even with both sides fully known) should get
  a way to mark itself confirmed-low without ever issuing a live round trip.

## For AI agents

`PlayerCareerPrefetchService`'s `PlayerAttribute` writes are scoped
**strictly** to the seeded countries/clubs its two existing sweeps already
cover (`ICategoryValueRepository.GetCountriesAsync`/`GetClubsAsync`) — do not
extend this to a broader, unscoped player pool without a fresh ADR; that
would reopen ADR-0001's "no bulk upfront import" principle far more broadly
than this decision intends.

Do not write to `PlayerNameIndex` from `PlayerCareerPrefetchService` under
any circumstance — this decision only touches COMP-06's `PlayerAttribute`,
never COMP-10. See ADR-0007/ADR-0053 for that boundary.

The club-name value written by the club-loop sweep MUST come from the same
`clubNameByClubQid` source `PlayerCareerStint.ClubName` already uses, not a
second, independently-derived string — a divergence here silently breaks the
local-join `PlayerCacheWarmingService` depends on, with no error, just a
pair that never appears "fully swept" even though both sides were actually
fetched.

If you are about to change `PlayerCacheWarmingService`'s skip-check
speculatively "to take advantage of" this decision, stop — its existing
`cachedCount >= options.MinValidAnswers` check already does the right thing
once the data this decision writes exists; no new code path there is needed
for the core win. Only touch it for the separate, explicitly-deferred
follow-up above (a swept-but-genuinely-low pair skipping the live round
trip entirely).
