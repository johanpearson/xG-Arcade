# ADR-0012: Category value reference tables, each with resolved external IDs (Wikidata QID / API-Football team ID)

- **Status:** Accepted
- **Date:** 2026-07-07
- **Related requirements:** REQ-109 (new)
- **Related components:** COMP-05 (Games.XGGrid), COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients)

## Context

Grid generation's pseudocode has always said "pick random categories"
without specifying where the pool of actual values (which countries, which
clubs) comes from — that gap was never filled in. Separately, querying
Wikidata requires its property/entity ID vocabulary (e.g. `Q142` for
France, `Q9617` for Arsenal F.C.), not the plain strings ("France",
"Arsenal") this system actually stores as category values. Something has
to resolve string → Wikidata QID, and it has to happen as a one-time
lookup, not as part of answering a guess or generating a grid under time
pressure — repeating that resolution on every query would be wasteful and
add latency for no reason, given category values (a few hundred countries
and clubs) barely ever change.

## Decision

Two new reference tables, following the same pattern `TrophyDefinition`
already established, each the source of truth for "what values exist for
this category type" **and** the place external IDs are cached once they're
resolved:

- **`CountryDefinition`** (`Name`, `WikidataQid` nullable): seeded via a
  **one-time bulk import** — countries are a small (~200), extremely
  stable set, so this is a deliberate, narrow exception to ADR-0001's
  "no bulk import" principle, the same class of exception ADR-0007 already
  made for `PlayerNameIndex`. A public Wikidata property (P297, ISO
  3166-1 alpha-2 code) makes this a clean one-time query, not manual curation.
- **`ClubDefinition`** (`Name`, `WikidataQid` nullable, `ApiFootballTeamId`
  nullable): **not** bulk-imported — clubs are added incrementally, one at
  a time, when an admin adds a new club as an allowed category value
  (through the same admin review flow REQ-503 already established). At
  that moment, the system resolves the club's Wikidata QID (via Wikidata's
  entity search — the `wikibase:mwapi`/`EntitySearch` SPARQL service) and
  API-Football team ID (via its `/teams?search=` endpoint), storing both.
  Resolved once, reused forever — consistent with every other caching
  decision in this system.
- **`TrophyDefinition`** gains a `WikidataQid` field (nullable). Given the
  tiny size of this table (a handful of trophies), QIDs are resolved
  manually when a trophy is added — no automated resolution needed.

**Grid generation now picks candidate category values from these three
reference tables**, not from an undefined "pick random categories" step —
this fills the gap directly.

**Missing QID is not a blocker.** If a category value's `WikidataQid` is
still null (not yet resolved, or a club whose Wikidata page doesn't have
a clean match), the live-lookup waterfall (ADR-0011) simply skips the
Wikidata step for that value and goes straight to the API-Football
fallback, which doesn't need a Wikidata QID at all. This keeps the system
degrading gracefully rather than blocking generation on a resolution step.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Resolve QIDs live, on every query, no caching | Simplest to implement first | Repeats the same lookup for the same country/club constantly — directly contradicts the "fetch once, cache forever" philosophy this whole system is built around | Wasteful and inconsistent with every other data-fetching decision made so far |
| Derive category values ad hoc from `PlayerAttribute` instead of dedicated reference tables | No new tables needed | Never actually answers "what values are allowed" cleanly — `PlayerAttribute` only contains what's been fetched so far, so the pool of pickable categories would shrink/grow unpredictably as an accident of caching rather than a deliberate admin decision | Category values should be a deliberate, curated list (like `TrophyDefinition` already is), not an emergent side effect |
| Bulk-import clubs too, same as countries | Consistent with countries' approach | The effective universe of "clubs that could ever be a category value" is much fuzzier and larger than "countries" (which league tiers? historical clubs?) — a bulk import here risks re-introducing exactly the speculative-import problem ADR-0001 avoids | Countries are naturally bounded and stable in a way clubs aren't; treat them differently |

## Consequences

- Positive: fills a real, previously-unaddressed gap in the grid-generation
  design; Wikidata queries can actually be constructed now; the "resolve
  once, cache forever" pattern extends cleanly to a third kind of data
  (category value → external ID) without a new philosophy
- Negative / trade-offs accepted: adding a new club as a category value now
  has a resolution step (small latency, one-time, admin-facing) rather than
  being instant; a club with an ambiguous or missing Wikidata page may need
  manual QID correction, similar to `PlayerOverride`'s existing pattern
- Follow-up: if club QID resolution turns out to be unreliable in practice
  (ambiguous search matches), consider requiring an admin to confirm the
  matched Wikidata page before saving the QID, rather than trusting the
  top search result automatically

## For AI agents

Grid generation must pick candidate category values from
`CountryDefinition`/`ClubDefinition`/`TrophyDefinition`, never from an
ad hoc derivation off `PlayerAttribute`. Never treat a null `WikidataQid`
as an error — it's an expected, valid state that just means the
API-Football fallback handles that value instead. Don't build a
per-query Wikidata QID resolution step — resolution happens once, at the
point a category value is added, not on every grid generation or guess check.
