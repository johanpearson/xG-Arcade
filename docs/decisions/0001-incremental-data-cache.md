# ADR-0001: Incremental data cache instead of upfront database import

- **Status:** Accepted
- **Date:** 2026-07-04
- **Related requirements:** REQ-101, REQ-103, REQ-501, REQ-605
- **Related components:** COMP-05 (Games.Grid), COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients)

## Context

Generating a valid grid requires knowing which players satisfy a given pair
of category values (e.g. country = France, club = Arsenal). External
football data sources are largely player-centric (query by player, get
attributes) rather than intersection-queryable (query by attribute pair, get
matching players). A naive approach would either require (a) a large,
manually curated database built before launch, or (b) calling external APIs
live on every grid generation with no local storage at all.

Constraints: solo developer, must run within free hosting/database tiers
(REQ-602), and must not block shipping an MVP behind a data-engineering
project.

## Decision

Player attribute data is cached locally and built incrementally: when a grid
generator needs a combination not yet in the local cache, it performs a live
lookup, stores the result as `unverified`, and reuses it for all future
grids. No bulk upfront import is performed.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Bulk upfront database import | Full coverage from day one | Large data-engineering effort before any game can ship; still needs an override mechanism | Blocks MVP, disproportionate effort for unknown category coverage needs |
| Pure live queries, no cache | No storage to maintain | Most sources can't answer intersection queries directly; would require fetching and filtering large squad/nationality lists on every request; answer correctness could shift mid-round if source data changes | Technically impractical and violates REQ-203 (consistent correctness during a round) |
| Incremental cache (chosen) | No upfront effort, storage grows only with real usage, consistent correctness during a round, natural fit with override mechanism (REQ-501) | Early grids may be slower to generate on cache misses; coverage is initially uneven across category types | Best balance of developer effort, cost, and user experience |

## Consequences

- Positive: MVP can ship without a data import phase; storage and external
  API usage scale with actual usage, not speculative coverage
- Negative / trade-offs accepted: the first few weeks of grids may take
  longer to generate (more cache misses); category types with poor external
  source coverage (e.g. some historical trophies) may initially fail
  validation more often (REQ-101 retry/abort path)
- Follow-up: monitor cache-miss rate; if a specific category type has a
  persistently high miss rate, consider a targeted bulk import for that
  category type only, as a separate ADR

## For AI agents

Do not introduce a bulk data-import script or a large seed dataset as a
"convenience" without raising this as a new ADR — it contradicts this
decision. New game modules needing player-like data should reuse
COMP-06 (Data.PlayerStore) via the same incremental-fetch pattern rather
than inventing a separate cache.
