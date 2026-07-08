# ADR-0008: Data provider terms-of-service compliance approach

- **Status:** Accepted (with one open action item before launch)
- **Date:** 2026-07-05
- **Related requirements:** none directly — this governs how ADR-0001/0007's data strategy is used, not a new user-facing behavior
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-10 (Data.PlayerNameIndex)

## Context

ADR-0001 and ADR-0007 are built around fetching data once and caching it
permanently. Some sports data providers restrict exactly that in their
terms specifically to prevent users from building a competing dataset.
Before relying on this architecture in production, it's worth actually
reading the terms rather than assuming.

Checked API-Football's terms and privacy policy directly (2026-07-05):

- They explicitly list building "applications, websites or any other
  products... such as fantasy soccer games" as data they intend to be used
  for — this project is squarely within their stated intended use.
- Logo/crest calls don't count against the request quota, and their own
  documentation recommends saving crest images on your own side rather
  than re-fetching — this directly supports the `ClubCrest` caching
  approach (implementation-document.md); it isn't a violation, it's what
  they suggest. Separately, the universe of distinct clubs ever needed as
  a category value is small and largely static (a few hundred well-known
  clubs, not thousands) compared to individual player lookups — fetched
  once per club, essentially never revisited. Both facts make crest
  fetching a genuinely low-risk addition whenever it's actually built
  (currently deferred to Phase 2, see requirements-document.md §6).
- What's prohibited: reselling their data directly, or building a product
  that competes with their own data offering. A gameplay product built on
  top of the data is different from redistributing the data itself — this
  project does the former.
- One clause is genuinely ambiguous: "We do not provide a 'license' for the
  use and publication of the data... on applications, websites or any
  other products made by the user" sits oddly next to the "fantasy soccer
  games" language elsewhere in the same document. This isn't necessarily a
  problem, but it's not clean enough to rely on without confirmation.

## Decision

- Proceed with the existing incremental-cache architecture (ADR-0001,
  ADR-0007) — nothing in the terms found so far contradicts it, and the
  crest-caching approach specifically is what they recommend.
- **Action item, required before public launch (not before development):**
  email API-Football support asking them to confirm in writing that
  building and permanently caching data for a gameplay product like this
  (not reselling the raw data, not offering a competing data API) is
  acceptable under their terms. Keep the written confirmation on file.
  This is a five-minute email, not a redesign — their own terms invite
  exactly this ("If you have any doubts about how you would like to use
  it, you can contact us directly by email").
- Apply the same "read before relying on" discipline to any future data
  source added to `DataSync.Clients` (COMP-07) — this isn't a one-time
  check specific to API-Football, it's a standing practice.
- Wikidata's structured data is CC0 (public domain equivalent) — no
  attribution legally required, though crediting sources in an "About/Data
  sources" page is good practice and costs nothing.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Proceed without checking terms at all | No effort | Exactly the risk this ADR exists to catch — could invalidate ADR-0001/0007's entire architecture after it's built | Cheap to check, expensive to discover after the fact |
| Switch to a provider with unambiguous terms before writing any code | Removes the ambiguity entirely | No evidence yet that another free-tier provider is actually clearer — Sportmonks/football-data.org have similar caching guidance and their own ambiguities; this could just relocate the problem | The specific ambiguous clause is resolvable with an email; not worth a provider switch on spec |

## Consequences

- Positive: development can proceed on the current architecture with
  reasonable confidence, backed by an actual reading of the terms rather
  than an assumption
- Negative / trade-offs accepted: there's a real, if small, dependency on
  getting a satisfactory written response from API-Football before public
  launch — if that response is unfavorable, `DataSync.Clients` would need
  a provider swap, which the existing architecture (COMP-07 as an
  isolated, swappable client layer) is designed to make cheap
- Follow-up: file the sent email and any response in the repo (e.g.
  `docs/decisions/correspondence/` or similar) so it isn't lost to an inbox

## For AI agents

Don't add a new external data source to `DataSync.Clients` (COMP-07)
without first checking its terms of service for caching/retention
restrictions, the same way this ADR did for API-Football. If a new
source's terms are ambiguous about long-term caching, flag it rather than
assuming it's fine by analogy to this ADR — each provider's terms are
their own.
