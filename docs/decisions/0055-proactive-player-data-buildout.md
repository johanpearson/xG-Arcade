# ADR-0055: Build up the player-data cache proactively, not just reactively

- **Status:** Accepted
- **Date:** 2026-08-02
- **Related requirements:** REQ-103, REQ-110, REQ-1201 (xG Path eligibility)
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-11 (Games.XGPath)

## Context

Every piece of Wikidata-sourced player data in this codebase today is
fetched **reactively** — only when something specific needs it, never ahead
of time for its own sake:

- `PlayerAttribute`/`PlayerCareerStint`: populated only when a specific
  `(nationality, club)` or `(club, club)` pair is queried, either by a live
  guess-time miss (ADR-0011) or by `warm-player-cache.yml` sweeping the
  currently-seeded reference lists — and that workflow is
  `workflow_dispatch`-only, "run after any reference-data change," with no
  recurring schedule (its own header comment).
- `ClubDefinition`/`CountryDefinition`: a hand-curated list (32 clubs, 44
  countries + 4 national teams as of this ADR) that only grows when someone
  explicitly adds and verifies a new entry (S-036/S-037's precedent).
- xG Path's target-player pool (`XGPathGameModule.GetEligiblePlayerIdsAsync`,
  REQ-1201): reads only `PlayerCareerStint` rows that already exist — which,
  per the above, is itself gated entirely on what xG Grid has happened to
  query so far.

ADR-0054 (same date) fixed one visible symptom — an already-selected xG Path
target's own puzzle could be missing real career data — with a live,
per-target refresh at generation time. It explicitly did NOT widen who can
ever become a target, and flagged this exact question as a deferred
follow-up. Live product feedback during that investigation was blunt: the
system should be **building up a correct, broad player dataset up front**,
so that both games' guesses resolve from cache more often — a wrong guess in
xG Grid that already has cached data answers fast; one that doesn't triggers
a live 15-28s Wikidata round trip (ADR-0011/ADR-0046) — and so xG Path's
target pool isn't artificially bottlenecked by xG Grid's own query history.
This ADR is the "separate story and ADR" ADR-0054 pointed to.

## Decision

Three independent, separately-shippable moves — the product owner confirmed
all three, in this order, with move (3) scoped to already-seeded countries
first (see Resolved questions below):

**1. Widen the seeded reference lists.** Pure data entry
(`ReferenceDataSeeder.Clubs`), same S-036/S-037-precedented process. Celtic
(`Q19593`) added directly in response to the reported gap — same
"training-knowledge QID, not verified against a live Wikidata endpoint from
this sandbox, a human must verify before relying on it in a real deployment"
flag every other unverified addition in that file already carries. Directly
closes the "this club can never appear, full stop" class of gap — no other
move below fixes that, since every other move still only ever discovers
data through *some* reference club/country.

**2. `warm-player-cache.yml` and `import-player-name-index.yml` now run on a
weekly cron** (Sunday 03:00 UTC and 04:30 UTC respectively — offset so the
two bulk jobs don't compete for the same slot; no dependency between them),
alongside their existing `workflow_dispatch` trigger. `NOTES.md`'s
2026-08-02 entry already documented the failure mode this fixes: a code fix
landed, but "someone has to actually click Run workflow once" for it to take
effect, and that step gets missed. `ConfirmedLowMatchPair`/`PairLookupFailure`
(ADR-0050/ADR-0052) already make a repeat run cheap — a pair confirmed
low-match or persistently failing is skipped without a live query — so the
recurring run mostly costs time on genuinely-new or previously-failed pairs,
not a full re-sweep every time.

**3. A new, direct career-data prefetch, independent of xG Grid's query
history.** The real fix for xG Path's pool bottleneck:
`IWikidataClient.QueryPlayerPoolByNationalityAsync` (a new query, the
nationality-scoped sibling of `QueryPlayerPoolBirthYearAsync`) fetches every
eligible player for one seeded `CountryDefinition` row at a time; each
resulting QID batch is get-or-created as a `Player`
(`GetOrCreatePlayersByWikidataQidAsync`) and run through ADR-0054's
`QueryPlayerCareerStintsByQidsAsync`, writing new stints via the same
`AddCareerStintsBatchAsync`/tuple-dedup logic `PlayerCareerStintRefreshService`
uses (extracted into an internal shared helper, `BuildNewStintsByPlayerId`,
so the two callers can't drift apart). New `PlayerCareerPrefetchService`,
new `dotnet run -- prefetch-player-careers` CLI verb, new
`prefetch-player-careers.yml` (`workflow_dispatch` only for now — see
Consequences). This is what actually removes "never triggered an xG Grid
lookup" as a disqualifier for an xG Path target.

**Correction from this ADR's original proposal:** the candidate pool source
is `CountryDefinition` (iterate seeded countries, fetch each one's full
player pool), NOT `PlayerNameIndex` as first proposed. `PlayerNameIndex`
deliberately has no `WikidataQid` column (ADR-0007/`PlayerNameIndex.cs`'s own
doc comment: "nothing today reconciles" the two id spaces) — its `PlayerId`
is a one-way deterministic hash of the QID, not the QID itself, so it cannot
actually supply the QIDs `QueryPlayerCareerStintsByQidsAsync` needs. Iterating
`CountryDefinition` directly turned out to be a better fit anyway: it
naturally implements the product owner's chosen "seeded countries first"
scoping with no separate filtering step, and reuses
`QueryPlayerPoolBirthYearAsync`'s already-proven bounded-query shape (no
`ORDER BY`/`LIMIT`/`OFFSET`) rather than needing a new reconciliation between
two id spaces.

## Alternatives considered

| Option | Pros | Cons | Why (not) chosen |
|---|---|---|---|
| Do nothing beyond ADR-0054 | No new work | The reported class of bug (missing/incomplete data) keeps recurring, one club/pair at a time, forever — ADR-0054 only treats the symptom for already-selected targets | Rejected — explicit product feedback asked for the proactive direction |
| Widen candidate pool by relaxing `IsEligible`'s seeded-club requirement instead of fetching more data | No new Wikidata query | Doesn't fix the actual problem (missing/incomplete data) — it would just accept incomplete puzzles as eligible, the opposite of what was asked for | Wrong lever entirely — REQ-1201's seeded-club anchor exists so a puzzle's answer is checkable, not incidental |
| Fetch full careers for literally every Wikidata footballer matching ADR-0025's filter, unscoped | Maximum completeness | No natural batch boundary, no idempotency/resumability story, unknown WDQS load at that scale — this is exactly the class of "unfiltered pool" query that caused `import-player-name-index`'s original OFFSET-paging failure (NOTES.md 2026-07-17/18) before it was redesigned around a bounded per-year slice | Rejected explicitly by the product owner in favor of scoping to already-seeded countries first |
| Source move (3)'s candidate pool from `PlayerNameIndex` (COMP-10's ~90k-row bulk import) | Already a bounded, complete pool with no new query needed | `PlayerNameIndex` deliberately has no `WikidataQid` column (ADR-0007) — its `PlayerId` is a one-way hash, not the QID itself, so it structurally cannot supply what `QueryPlayerCareerStintsByQidsAsync` needs | Discovered to be unworkable during implementation — see the Decision section's "Correction" note |
| Three phased moves, sourcing move (3) from `CountryDefinition` (chosen) | Each phase is independently small, low-risk, and separately valuable; (1) and (2) are pure precedented patterns already used elsewhere in this codebase; (3) reuses ADR-0054's new query, `QueryPlayerPoolBirthYearAsync`'s proven bounded-query shape, and existing bulk-job infrastructure (`AddCareerStintsBatchAsync`) rather than inventing new mechanisms, and naturally implements the "seeded countries first" scoping with no extra filtering step | Three separate pieces of work, not one; (3)'s per-country query is unproven at scale for a large country's full pool (see Consequences) | Best fit: matches this codebase's own established pattern of phased, precedented extensions rather than one large unproven bulk change |

## Consequences

- Positive: closes the exact reported gap class (Celtic-style "this club can
  never appear") and the broader "xG Path's pool is bottlenecked by xG
  Grid's own history" limitation ADR-0054 explicitly deferred
- Positive: both xG Grid (more cached pairs → fewer live 15-28s lookups) and
  xG Path (a genuinely wide, complete target pool) benefit from the same
  underlying investment, not two separate efforts
- Negative / trade-off accepted: real new WDQS query volume, on a recurring
  basis once (2) ships — needs monitoring the same way ADR-0052's incident
  (a structural query-shape problem masquerading as "just needs more
  retries") was caught: watch for a long contiguous stretch of failures
  against the same clubs/QIDs, not just raw failure count
- Negative / trade-off accepted (now resolved — see below): (3)'s per-country
  pool query was unproven at the scale a large football nation (Brazil,
  England, ...) could produce when this ADR was written.
- **Resolved 2026-08-02, first real run:** `QueryPlayerPoolByNationalityAsync`
  (the per-country pool query) never failed once, including for huge pools
  (United Kingdom: 18,460 players; Brazil: 10,949; Germany: 10,128) — the
  flagged server-cap risk did not materialize; that query shape is proven
  safe. The run still went red, but from a different, more mundane cause:
  4 of the many 200-player `QueryPlayerCareerStintsByQidsAsync` batches hit
  `WikidataClient`'s 15s *default* timeout (tuned in ADR-0011 for much
  narrower per-cell queries) — the same class of bug as the 2026-07-17
  `import-player-name-index` timeout entry (NOTES.md), not the WDQS
  server-cap issue this ADR originally anticipated. `PlayerCareerPrefetchService`'s
  fail-loud-at-end contract worked exactly as designed: kept going, isolated
  the 4 batch failures without losing the other 49 countries' 177,872
  players/607,914 stints, and exited nonzero. Fixed by giving
  `prefetch-player-careers`'s own `WikidataClient` a 60s `queryTimeout`
  override, same fix `import-player-name-index` already needed for the
  identical reason — see NOTES.md's matching 2026-08-02 entry for the full
  incident.
- Follow-up: now that a real run has confirmed the country-pool query is
  safe and the batch-timeout bug is fixed, `prefetch-player-careers.yml` can
  reasonably move to move (2)'s recurring cron once a clean full run
  confirms the fix — still `workflow_dispatch`-only for now, pending that
  confirmation.
- Follow-up: once (1)-(3) have run for real, revisit whether
  `MinValidAnswers`/`MinAppearancesAtSeededClub` thresholds (ADR-0023/
  ADR-0047) still reflect the right trade-off now that the underlying data
  pool is much less sparse — this ADR does not change either threshold

## Resolved questions

Asked of the product owner before implementation started; all resolved
2026-08-02, same session:

1. **Which moves, and in what order?** All three, in the order listed above.
2. **Move (1): which clubs?** Celtic only, for now (the specifically
   reported gap) — a broader audit of other commonly-missing clubs is
   explicitly left as optional future work, not bundled in here.
3. **Move (2): what cadence?** Weekly — see the Decision section for the
   exact offset chosen between the two jobs.
4. **Move (3): pool scope?** Already-seeded countries first (not an
   unscoped/`PlayerNameIndex`-wide pool) — see the Decision section's
   "Correction" note for why the implementation ended up sourcing this from
   `CountryDefinition` rather than `PlayerNameIndex` as originally proposed.

## For AI agents

`PlayerCareerPrefetchService`'s candidate pool must keep coming from
`ICategoryValueRepository.GetCountriesAsync` (already-seeded countries) —
do not widen it to an unscoped pool (e.g. every `PlayerNameIndex` entry, or
every Wikidata footballer matching ADR-0025's filter) without a fresh
product decision; that was explicitly rejected above, not merely deferred.
If a future task wants broader coverage, the right lever is adding more
countries to `ReferenceDataSeeder.Countries` (move 1's own mechanism), not
loosening this service's own scope.
