# ADR-0055: Build up the player-data cache proactively, not just reactively

- **Status:** Proposed
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

## Decision (proposed — not yet accepted; see Open questions)

Three independent, separately-shippable moves, in the order they'd most
plausibly be built:

**1. Widen the seeded reference lists.** Pure data entry (`ReferenceDataSeeder.Clubs`/`Countries`),
same S-036/S-037-precedented process: hand-pick candidate clubs/countries,
verify each QID against a live Wikidata page before merging (this sandbox
cannot do that verification itself — no `wikidata.org` access, see
`NOTES.md`'s repeated network-policy notes). Directly closes the
"this club can never appear, full stop" class of gap (e.g. Celtic) — no
other move below fixes that, since every other move still only ever
discovers data through *some* reference club/country.

**2. Put `warm-player-cache.yml` (and `import-player-name-index.yml`) on a
recurring cron**, not `workflow_dispatch`-only. `NOTES.md`'s 2026-08-02 entry
already documents the failure mode this causes: a code fix landed, but
"someone has to actually click Run workflow once" for it to take effect,
and that step gets missed. `ConfirmedLowMatchPair`/`PairLookupFailure`
(ADR-0050/ADR-0052) already make a repeat run cheap — a pair confirmed
low-match or persistently failing is skipped without a live query — so a
recurring run mostly costs time/Action-minutes on the genuinely-new or
previously-failed pairs, not a full re-sweep every time.

**3. A new, direct career-data prefetch, independent of xG Grid's query
history.** The real fix for xG Path's pool bottleneck: reuse ADR-0054's new
`IWikidataClient.QueryPlayerCareerStintsByQidsAsync` as a genuine bulk job —
batched by-QID full-career fetch over a broad player pool (candidate source:
`PlayerNameIndex`, COMP-10's ~90k-row broad import, already filtered to
ADR-0025's male/born-1939-or-later pool and already independent of
`ClubDefinition`/`PlayerAttribute`) — writing directly into
`PlayerCareerStint` via the same `AddCareerStintsBatchAsync`
`PlayerCareerStintRefreshService` already uses. This is what actually
removes "never triggered an xG Grid lookup" as a disqualifier for an xG Path
target. Structurally a new CLI verb (`dotnet run -- prefetch-player-careers`
or similar), same ADR-0024 "long-running bulk job is a CLI verb, not an HTTP
endpoint or background task" shape as `warm-player-cache`/
`import-player-name-index`/the two backfill services.

## Alternatives considered

| Option | Pros | Cons | Why (not) chosen |
|---|---|---|---|
| Do nothing beyond ADR-0054 | No new work | The reported class of bug (missing/incomplete data) keeps recurring, one club/pair at a time, forever — ADR-0054 only treats the symptom for already-selected targets | Rejected — explicit product feedback asked for the proactive direction |
| Widen candidate pool by relaxing `IsEligible`'s seeded-club requirement instead of fetching more data | No new Wikidata query | Doesn't fix the actual problem (missing/incomplete data) — it would just accept incomplete puzzles as eligible, the opposite of what was asked for | Wrong lever entirely — REQ-1201's seeded-club anchor exists so a puzzle's answer is checkable, not incidental |
| Fetch full careers for literally every Wikidata footballer matching ADR-0025's filter, unscoped | Maximum completeness | No natural batch boundary, no idempotency/resumability story, unknown WDQS load at that scale — this is exactly the class of "unfiltered pool" query that caused `import-player-name-index`'s original OFFSET-paging failure (NOTES.md 2026-07-17/18) before it was redesigned around a bounded per-year slice | `PlayerNameIndex` is the already-solved version of "a bounded, complete pool to iterate" — reuse it rather than re-inventing the same bounded-iteration problem |
| Three phased moves (chosen) | Each phase is independently small, low-risk, and separately valuable; (1) and (2) are pure precedented patterns already used elsewhere in this codebase; (3) reuses ADR-0054's new query and existing bulk-job infrastructure (`AddCareerStintsBatchAsync`, `PlayerNameIndex` as the bounded pool) rather than inventing new mechanisms | Three separate pieces of work, not one; (3) in particular needs its own real design pass (batch size, runtime budget vs. the 90-minute Actions ceiling, resumability across runs) before it's buildable | Best fit: matches this codebase's own established pattern of phased, precedented extensions rather than one large unproven bulk change |

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
- Negative / trade-off accepted: (3) is real new scope, not a quick
  follow-up — batch sizing, runtime budget, and resumability across runs all
  need actual design work, not just "reuse the query"
- Follow-up: once (1)-(3) ship, revisit whether `MinValidAnswers`/
  `MinAppearancesAtSeededClub` thresholds (ADR-0023/ADR-0047) still reflect
  the right trade-off now that the underlying data pool is much less sparse
  — this ADR does not change either threshold

## Open questions (for the product owner, before implementation starts)

1. Which of the three moves should be built first — all three as one
   effort, or staged, and in what order?
2. For move (1): are there specific clubs/countries to add now (Celtic was
   the reported case — likely others exist), or should this wait for a
   broader audit of commonly-missing clubs?
3. For move (2): what cadence for the recurring cron — weekly, matching
   `generate-round.yml`'s daily cadence, something else? Cost is bounded by
   ADR-0050/ADR-0052's skip logic, but a cadence still needs picking.
4. For move (3): is the full `PlayerNameIndex` pool (~90k rows) the right
   scope, or should a first pass filter to something smaller (e.g. only
   nationalities already in `CountryDefinition`) to bound the first real
   run's cost/runtime before committing to the full pool?

## For AI agents

Do not implement move (3) (the bulk career prefetch) without a resolved
answer to Open question 4 (pool scope) — an unscoped "fetch every
`PlayerNameIndex` row's career" run risks repeating the exact
unbounded-query failure class `import-player-name-index`'s original design
already hit once (NOTES.md 2026-07-17/18). If asked to "just build the
proactive fetch," treat that as a signal to confirm scope first, not to pick
a default silently.
