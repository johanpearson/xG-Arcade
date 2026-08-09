# ADR-0062: Admin by-name Wikidata lookup resolves the candidate player via `wikibase:mwapi` search, not a raw label scan

- **Status:** Accepted
- **Date:** 2026-08-09
- **Related requirements:** REQ-509, REQ-510
- **Related components:** COMP-07 (DataSync.Clients)

## Context

REQ-509's suggestion-review lookup (`POST /admin/suggestions/{id}/lookup`)
and REQ-510's standalone search (`POST /admin/player-search/lookup`) share
one Wikidata query, `IWikidataClient.QueryPlayerCareerAndNationalityByNameAsync`.
Its `BuildPlayerCareerAndNationalityByNameQuery` resolves the admin's typed
player name to a candidate `?player` by scanning every Wikidata footballer's
`rdfs:label`/`skos:altLabel` with a case-insensitive string filter
(`FILTER(LCASE(STR(?matchedLabel)) = LCASE("..."))`) — an unindexed,
population-wide literal comparison across the whole graph, not a narrow
per-cell lookup.

This was already known to be a heavier query shape than the 15s timeout
budget it originally shared with the client's narrow per-cell intersection
queries (ADR-0011) — fixed by giving it its own 45s budget
(`_adminLookupQueryTimeout`, 2026-08-09, no ADR — that was tuning, not a
structural change). That fix was necessary but not sufficient: a production
log captured the same admin lookup (player "Donny van de Beek") running for
**38.8 seconds and then returning HTTP 502 Bad Gateway** from
`query.wikidata.org` — not a client-side timeout. The request was never cut
off by our own budget; something in front of WDQS (most likely a
gateway/proxy enforcing its own upstream-response deadline, independent of
any client `CancellationTokenSource`) rejected the query once it ran long
enough. No client-side timeout increase can fix a failure that happens on
the far side of that gateway. The actual cost driver is the query shape
itself: a full, unindexed graph scan over every footballer's labels and
aliases.

## Decision

Replace the raw label/alias `FILTER` scan in
`BuildPlayerCareerAndNationalityByNameQuery`'s candidate-selection subquery
with a `SERVICE wikibase:mwapi { ... }` block using Wikidata's `EntitySearch`
API — the same indexed search engine behind Wikidata's own search box —
federated into the same SPARQL query, on the same `query.wikidata.org`
endpoint. The subquery still selects exactly one candidate `?player` (same
`LIMIT 1` shape, now applied after re-filtering the search API's ranked
candidates down to `wdt:P106 wd:Q937857`, i.e. footballers), and everything
downstream (`WikidataPlayerCareerLookupResult`,
`ParsePlayerCareerAndNationalityByNameBindings`, both endpoints' contracts)
is unchanged — this is a resolution-mechanism change, not a data-shape or
API-contract change.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Status quo (raw `rdfs:label`/`skos:altLabel` scan) | No change needed | Known broken in production — unindexed, population-wide scan is expensive enough to trigger a gateway-level 502 under load, independent of client timeout | Confirmed failing; not a real option |
| Backfill a `WikidataQid` column onto `PlayerNameIndex`, resolve locally, then reuse the existing by-QID query shape (e.g. `QueryPlayerCareerStintsByQidsAsync`'s pattern) | Zero live search cost for any already-indexed player; fastest possible admin lookup | `PlayerNameIndex.PlayerId` is a one-way deterministic hash of the QID today, not the QID itself (see the entity's own doc comment) — nothing reconciles the two ID spaces, and ADR-0007/COMP-10 treat that reconciliation as a deliberate future decision, not something to back into via this fix. Also only covers players already bulk-imported; a name Wikidata just gained wouldn't resolve until the next import | Bigger schema/reconciliation change than this fix warrants; a reasonable follow-up, not today's decision |
| Call Wikidata's REST `wbsearchentities` API directly (`www.wikidata.org/w/api.php`) as a first request, then a follow-up by-QID SPARQL query | Same indexed search benefit; a well-documented, simple REST call | Introduces a genuinely new external host/dependency, requiring its own ADR-0008-style terms-of-service review before being wired up; two round trips (search, then fetch) instead of one; a new REST client class alongside the existing SPARQL-only `WikidataClient` | Same underlying search benefit as `wikibase:mwapi` without leaving the existing single-host SPARQL client, so the federated-search option dominates it |
| `SERVICE wikibase:mwapi { ... }` `EntitySearch`, federated into the existing SPARQL query (**chosen**) | Uses the same indexed search as Wikidata's own search box; no new external host, no ADR-0008 review needed, no schema change; stays inside `WikidataClient`'s existing single-endpoint client | Cannot be empirically verified against the real Wikidata endpoint from this development sandbox (no live network access — see Consequences); federated `SERVICE` queries are a less common WDQS idiom than the plain triple patterns used elsewhere in this file, so future maintainers need this ADR's context to understand why | Best fit for the actual cost problem (query shape, not timeout) without introducing new architectural surface |

## Consequences

- Positive: the admin by-name lookup should become dramatically cheaper for
  WDQS to execute (indexed search vs. a full graph scan), which is the
  actual fix for the observed 502 — the prior timeout increase alone was
  necessary (it revealed the real error instead of masking it as a
  client-side timeout) but not sufficient.
- Positive: no new external dependency, no schema change, no change to
  either endpoint's HTTP contract or `WikidataPlayerCareerLookupResult`'s
  shape.
- Negative / trade-off accepted: this session has no live Wikidata/network
  access (the same standing sandbox limitation recorded throughout
  `NOTES.md`), so the exact `wikibase:mwapi`/`EntitySearch` SPARQL syntax
  cannot be run against the real endpoint before merge. Tests can only
  verify the request is well-formed and that response parsing still works
  against a mocked JSON payload — not that the real WDQS federated-search
  extension behaves as documented.
- Follow-up: **a human must run this query against the real
  `query.wikidata.org` endpoint (e.g. via the Wikidata Query Service UI)
  before this is trusted in production** — the same "unverified from
  sandbox, must be checked against live Wikidata" convention already
  applied to QID additions elsewhere in this codebase (see `NOTES.md`).
  If `wikibase:mwapi`'s `EntitySearch` API turns out not to preserve
  reliable relevance ordering once combined with the `wdt:P106` re-filter,
  the query may need an explicit `mwapi:limit` widened further, or a
  fallback path, revisited then.
- Follow-up: if this pattern proves out, `QueryPlayerPhotoByNameAsync`
  (REQ-216/ADR-0057, still on the same raw label-scan shape and 15s budget)
  is a candidate for the same rewrite later — deliberately out of scope
  here since it has its own different timeout/urgency constraints
  (live wrong-guess-flow path) that need their own consideration, not a
  drive-by change bundled into this ADR.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
