# ADR-0011: Wikidata-first waterfall for live lookups; API-Football as fallback only

- **Status:** Accepted — supersedes ADR-0010's budget model specifically
- **Date:** 2026-07-07
- **Related requirements:** REQ-103 (revised), REQ-211 (revised)
- **Related components:** COMP-07 (DataSync.Clients)

## Context

**This corrects a real error, not a refinement.** ADR-0001 established two
data sources for the incremental cache — Wikidata and API-Football — but
ADR-0010 (written later, same day) designed the guess-time live-lookup
budget around API-Football alone, as if it were the only source. That was
a mistake: it treated a 100-requests/day hard cap as the binding constraint
on a feature that was always supposed to have a second, far less
constrained source available.

Checked Wikidata's actual public SPARQL endpoint limits directly: there is
no small fixed daily request count. Throttling is by query *time* — 60
seconds of query time per minute per IP/user-agent, bursting to 120
seconds/minute — not a request count. For the kind of query this system
needs (single-player lookups: nationality, club history, trophies), each
query is fast, so this is enormously more headroom than API-Football's 100
requests/day, for effectively no cost. The trade-off: WDQS has been
reported as measurably less responsive under current load (some queries
taking 9-27 seconds or timing out), so it needs to be called with a
timeout and a fallback path, not treated as instant or 100% reliable.

## Decision

Every live lookup (both REQ-103's grid-generation fallback and REQ-211's
guess-time verification) now tries sources in order:

1. **Wikidata first.** Query with a reasonable timeout (e.g. 5-10s). This
   is the primary source precisely because it isn't meaningfully capped
   for this system's actual query volume.
2. **API-Football only if Wikidata times out, errors, or genuinely has no
   matching data.** This is now a true fallback, touched only for the
   subset of lookups Wikidata can't answer — expected to be a small
   fraction of total lookups, not a coequal source sharing a tight budget.

Consequences for ADR-0010's mechanics:

- The "shared daily budget, 80/20 split" model from ADR-0010 is replaced.
  There's no longer a meaningful reason to reserve headroom for grid
  generation against guess-time lookups, because the vast majority of both
  will resolve via Wikidata without touching API-Football's cap at all.
- `ExternalApiUsage` (the tracking entity) now tracks both sources by
  `Source`, but the `GuessTimeLookupThreshold` gate only applies to the
  `api_football` row — Wikidata usage is monitored for observability, not
  gated, since it isn't the constrained resource.
- The "fail closed as incorrect if budget exhausted" behavior (REQ-211)
  now only triggers if *both* Wikidata (timeout/error/no-match) *and*
  API-Football (threshold exhausted) fail to resolve a lookup — a
  meaningfully rarer case than before.
- Everything else from ADR-0010 is unchanged: the narrow trigger condition
  (only when a name matches a real `PlayerNameIndex` candidate with no
  existing attribute data), immediate persistence (never batched), and the
  general fail-closed philosophy.

Transfermarkt remains excluded, unchanged from ADR-0001's original
reasoning (no official API, scraping is a ToS/legal gray area) — this
correction doesn't reopen that question. If API-Football's cap ever
becomes a real bottleneck even as a fallback-only source, that's a
reason to revisit Transfermarkt (or a paid API-Football tier) explicitly,
not to quietly start scraping.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep ADR-0010's model, just raise the reserved threshold | Smaller change | Doesn't fix the actual error — API-Football would still be the primary lookup path for both call sites, when Wikidata should be absorbing most of the load | Treats the symptom, not the mistake |
| Query both sources in parallel, merge results | Slightly faster in the common case | Doubles API-Football calls for no benefit when Wikidata alone would have answered — actively worse for the constrained resource | Sequential waterfall is strictly better here: only pay the API-Football cost when actually needed |
| Make API-Football primary, Wikidata fallback (reverse order) | N/A | Exactly backwards — puts the tightly-capped source in the hot path and the generously-capped source in the rarely-used path | This is the mistake being corrected |

## Consequences

- Positive: the 100/day API-Football cap stops being the practical
  bottleneck on either grid generation or guess-time verification, which
  directly addresses the concern that motivated this ADR
- Negative / trade-offs accepted: Wikidata's variable/degraded response
  times (per the 2026 performance note) mean live lookups need a real
  timeout and fallback path, not a blind "call it and wait" — slightly
  more implementation complexity than a single-source call
- Follow-up: monitor the actual Wikidata-hit-rate vs. API-Football-fallback-rate
  once real usage exists; if API-Football is barely ever touched (likely),
  its cap may not need active management at all in practice

## For AI agents

Any live-lookup code path (grid generation, guess-time verification, or
any future one) must try Wikidata first and API-Football only as a
fallback — never the reverse, and never parallel calls to both by default.
Give the Wikidata call a real timeout; don't let a slow WDQS response block
a guess-check indefinitely. If you're implementing this and find yourself
calling API-Football first "for simplicity," stop — that's the exact
mistake this ADR exists to correct.
