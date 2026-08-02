# ADR-0056: xG Path target-player familiarity filter (Wikipedia sitelink count)

- **Status:** Accepted
- **Date:** 2026-08-02
- **Related requirements:** REQ-1201 (xG Path target-player eligibility), REQ-103
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-11 (Games.XGPath)

## Context

Direct player feedback on xG Path: "I got this Austrian guy that I had no
idea who he is" — the puzzle was fair (a real, well-documented career) but
un-fun, because none of the game's clues (REQ-1203: clubs, appearance
counts, position, nationality, birth year) reliably narrow down to someone
the player has ever heard of when the target itself isn't recognizable.

`XGPathGameModule.IsEligible` (REQ-1201) checks exactly three structural
properties of a candidate's `PlayerCareerStint` set — at least 3 documented
stints, a determinable chronological order, and at least one stint at a
seeded club above a minimum appearance count (ADR-0047). None of these are a
fame or recognizability signal: a long, obscure, journeyman career (the
reported case) passes every check exactly as easily as a star's. The
candidate pool itself (ADR-0055) was also recently widened — every eligible
player from every seeded country's full pool, not just players xG Grid
happened to query about — which increases, not decreases, the chance of
picking an obscure target without a familiarity check on top.

## Decision

Add a familiarity filter on top of REQ-1201's existing structural checks,
using **Wikipedia sitelink count** (how many language editions have an
article on the player) as the familiarity proxy — the product owner's
explicit choice among the options discussed (total career appearances: too
weak a proxy, the reported case already has a long career; a major
trophy/award: too aggressive a pool cut, skews toward a handful of big
clubs).

- `IWikidataClient.QuerySitelinkCountsByQidsAsync` — a new batched,
  direct-by-QID query using WDQS's `wikibase:sitelinks` computed predicate,
  same VALUES-clause shape as `QueryPlayerPhotosByQidsAsync`/
  `QueryPlayerPositionsAndBirthYearsByQidsAsync`.
- `PlayerFamiliarityService` (`XGArcade.DataSync.Wikidata`, behind a new
  `IPlayerFamiliarityService`) — a narrow, purpose-built service
  `XGPathGameModule` depends on, mirroring `IPlayerCareerStintRefreshService`'s
  existing "Games.XGPath never touches `IWikidataClient` directly" boundary
  (ADR-0054). Batches candidates in groups of 200 (same bounded-query
  discipline as every other batch job in this codebase), and judges a
  candidate familiar when its sitelink count resolves to at least
  `MinSitelinkCount` (15, a starting value — see Consequences).
- `XGPathGameModule.GetEligiblePlayerIdsAsync` runs the familiarity filter
  on top of the existing structural-eligibility result, before
  `PickDistinct` selects targets. `GenerateInstanceAsync`'s existing
  "not enough eligible players" abort is unchanged and now also covers "not
  enough *familiar* eligible players."
- **Fail-open, not fail-closed**, on both a genuine Wikidata failure and a
  systemic inability to check anyone (e.g. every candidate missing a
  `WikidataQid`) — the whole pool is returned unfiltered for that one
  generation rather than blocking it, the same REQ-103 "never block round
  generation on a Wikidata failure" reasoning `PlayerCareerStintRefreshService`
  already follows. A candidate that CAN be checked but whose sitelink count
  doesn't resolve, or resolves below the threshold, is excluded — "can't
  verify" is never treated as "assumed familiar" once checking is actually
  possible.
- No new persisted column and no caching of sitelink counts — every
  generation re-queries live. Deliberately deferred (see Follow-up).

## Alternatives considered

| Option | Pros | Cons | Why (not) chosen |
|---|---|---|---|
| Total career appearances (sum `AppearanceCount` across stints) | No new Wikidata query — reuses data already fetched | Weak proxy: the reported case (Patrick Müller) already has a long, well-documented career with real appearance counts at multiple clubs; a long career isn't the same as a recognizable one | Rejected — doesn't actually fix the reported problem |
| Major trophy/award won (reuse P166, already wired for xG Grid's trophy category) | Cheap to check, existing signal | Would shrink the pool a lot and skew heavily toward players from a handful of historically dominant clubs — a fair but non-trophy-winning international would be excluded | Rejected — too aggressive and biased a cut |
| Wikipedia sitelink count (chosen) | Directly measures real-world recognizability rather than a proxy for it; `wikibase:sitelinks` is a WDQS-native computed predicate, no new external data source (ADR-0008 doesn't need repeating — still Wikidata) | Extra live SPARQL round trip at round-generation time; threshold is a judgment call with no tuning data yet | Best fit — most direct match to what "familiar" actually means, and reuses this codebase's existing batched-VALUES-query pattern exactly |
| Do nothing, revisit after more play-testing | No new work | The reported problem recurs on essentially every round until fixed | Rejected — a real, specific, actionable complaint exists now |

## Consequences

- Positive: directly addresses the reported "obscure target" complaint
  without touching REQ-1203's clue content or REQ-1201's existing structural
  checks
- Positive: fails open on any failure mode, so this can never turn into a
  new "xG Path round generation is down" incident class
- Negative / trade-off accepted: `MinSitelinkCount = 15` is an untuned
  starting value, not derived from real usage data — it may turn out too
  strict (shrinking the pool more than intended) or too loose (still
  allowing borderline-obscure targets) once real puzzles are played against
  it
- Negative / trade-off accepted: an extra live Wikidata round trip (batched,
  200 QIDs at a time) on every round generation, against the full
  structurally-eligible pool rather than just the N selected targets —
  round generation is a scheduled/cron-triggered `/internal/generate-round`
  call (ADR-0051), not a latency-sensitive live-user request, so this is
  judged an acceptable cost, but it is real added query volume on top of
  ADR-0055's already-larger candidate pool
- Follow-up: revisit `MinSitelinkCount` once real puzzles have been played
  against it — this ADR does not claim 15 is correct, only reasonable as a
  starting point
- Follow-up: if round-generation latency or WDQS load from the per-generation
  live query becomes a problem, consider persisting a cached sitelink count
  on `Player` (refreshed on a recurring cadence, mirroring
  `PlayerPositionBirthYearBackfillService`'s pattern) instead of querying
  live every time — deliberately not built now, since no such problem has
  been observed yet and a live check is simpler

## For AI agents

`PlayerFamiliarityService`'s fail-open contract (return the whole candidate
pool unfiltered on a Wikidata failure or a systemic data gap) must not be
changed to fail-closed without a fresh product decision — that would turn a
Wikidata outage into an xG Path outage, which REQ-103's established
reasoning across this whole codebase exists specifically to prevent. If
`MinSitelinkCount` needs tuning, change the constant directly; it does not
need a new ADR unless the signal itself (sitelink count) is being replaced
with something else.
