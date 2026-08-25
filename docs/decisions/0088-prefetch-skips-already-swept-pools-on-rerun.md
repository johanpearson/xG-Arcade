# ADR-0088: `PlayerCareerPrefetchService` skips an already-swept country/club on re-run

- **Status:** Accepted
- **Date:** 2026-08-25
- **Related requirements:** REQ-110
- **Related components:** COMP-07 (DataSync.Clients)

## Context

The Supabase org backing this project (free tier) went over its 5GB/
billing-cycle egress quota (6.40GB used, 128%) during the current billing
cycle. Storage buckets were confirmed not the cause (Storage Size 0/1GB) —
this was Postgres/API egress driven by GitHub Actions CLI jobs. Root cause,
confirmed via GitHub Actions run history and source reading:
`PlayerCareerPrefetchService` (backs `prefetch-player-careers.yml`) had no
skip-already-processed shortcut, unlike every sibling bulk Wikidata job.
Every dispatch unconditionally re-swept every seeded `CountryDefinition`
and `ClubDefinition` row's full player pool from scratch — a live Wikidata
query per row, followed by a full `GetPlayerAttributesAsync`/
`GetCareerStintsByPlayerIdsAsync` dedup read-back against Supabase Postgres
before writing anything — regardless of whether that row had ever been
swept before, or how recently.

A player-pool purge on 2026-08-17 was followed by 9 manual re-dispatches of
`prefetch-player-careers.yml` in ~36 hours (chasing transient Wikidata/WDQS
failures under the job's fail-loud-at-end contract — see REQ-110's own
"idempotent — re-run it to retry what failed" framing, which assumes
re-running is cheap; it was not). One successful run alone persisted
193,382 players / 527,252 stints. The ~1.3GB single-day egress spike visible
in the Supabase usage dashboard around 2026-08-18 is the most likely
explanation, and closely tracks that dispatch burst. Every one of those 9
re-dispatches re-swept the entire seeded pool from scratch, including
countries/clubs whose pool had already completed successfully on an earlier
dispatch in the same burst.

ADR-0078 (2026-08-18, same incident window) had already introduced the
signal this ADR now also reads: `CountryDefinition`/
`ClubDefinition.PlayerPoolSweptAt`, a nullable timestamp
`PlayerCareerPrefetchService` itself stamps on the success path of a pool
sweep. ADR-0078 taught a *different* service, `PlayerCacheWarmingService`,
to trust that signal for a *different* question — whether a cached
Country×Club match-count is already final. It explicitly did not change
`PlayerCareerPrefetchService`'s own sweep loop, which kept re-sweeping
every row unconditionally regardless of `PlayerPoolSweptAt`. That gap is
what this ADR closes.

## Decision

`PlayerCareerPrefetchService.SweepAsync` (the shared "fetch -> mark swept ->
skip-empty -> dedup+chunk" loop both `SweepCountriesAsync` and
`SweepClubsAsync` use) now checks each row's own `PlayerPoolSweptAt`
(`getSweptAt`) immediately after the existing null-`WikidataQid` skip and
before calling `fetchPoolAsync`. A row whose `PlayerPoolSweptAt` is already
non-null is skipped entirely:

- no `fetchPoolAsync` call (no live Wikidata query),
- no `markSweptAsync` re-write (the existing timestamp already reflects a
  genuinely complete sweep, per ADR-0078's own "For AI agents" section on
  when it's allowed to be set — re-stamping it on a skip would not be
  wrong, but is simply unnecessary),
- and no `SweepPoolAsync` call at all — which means no
  `GetPlayerAttributesAsync`/`GetCareerStintsByPlayerIdsAsync` dedup
  read-back either, since those only ever run inside `SweepPoolAsync`,
  reached only after a pool is actually fetched.

The null-`WikidataQid` skip is checked first and still takes priority,
matching every other precedence rule already established in this method.
A skipped row increments a new `Skipped` counter on `SweepOutcome`,
surfaced via `PlayerCareerPrefetchResult.CountriesSkipped`/`ClubsSkipped`
in the CLI summary line, so an operator can see at a glance how much of a
re-dispatch was actually free.

**No staleness window.** "Ever successfully swept" is treated as
sufficient to skip — there is no re-sweep-after-N-days policy. See
Alternatives considered below for why.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Time-boxed staleness window (e.g. re-sweep a row if `PlayerPoolSweptAt` is older than N days) | Bounds how stale a swept pool can get without a manual invalidation; feels like a "safer" default than an unbounded skip | Requires picking and justifying an arbitrary N with no evidence it matters for this data; adds a second, calendar-driven re-sweep trigger alongside the existing invalidation contract (`StaleClubAttributeCleaner`/`purge-player-pool`), which is more moving parts to reason about, not fewer; a Wikidata career history is edited far less often than a calendar window would imply, so N would almost always fire on data that hasn't actually changed — burning the exact egress this ADR exists to stop, just on a timer instead of a manual re-dispatch | Rejected — this data's own volatility doesn't justify a second staleness mechanism, and ADR-0078 already established "ever swept is sufficient" as the right call for the same underlying signal on the sibling job; a different answer here would need a reason specific to this service, and none exists |
| Skip only within a single `PrefetchAsync` run (dedupe against rows already processed earlier in the same invocation), but still fully re-sweep every row on a fresh dispatch | Would have prevented some waste even without persisting anything new | Does nothing for the actual incident shape — 9 *separate* dispatches, not one run processing the same row twice — so it would not have prevented the egress spike at all; solves a problem this codebase didn't actually have | Rejected — doesn't address the root cause |
| Do nothing — rely on fix #2 (GitHub Actions `concurrency:` group preventing overlapping dispatches) alone | Simpler, no new logic in `SweepAsync` | Only prevents *overlapping* runs; does nothing to stop a second, non-overlapping re-dispatch (exactly what happened — 9 dispatches over 36 hours, not necessarily concurrent) from re-sweeping the entire pool again for zero new data | Rejected — the concurrency fix and this ADR's skip mechanism address different halves of the same incident and are both needed; neither alone is sufficient |

## Distinguishing this from ADR-0078

ADR-0078 and this ADR both read `PlayerPoolSweptAt`, but they are separate
decisions about separate questions in separate services, and neither widens
or relaxes the other:

- **ADR-0078** governs `PlayerCacheWarmingService.WarmAsync`
  (`XGArcade.Games.XGGrid`, COMP-05/COMP-06). Its question is *pairwise*:
  "is a specific Country×Club (or Club×Club) pair's locally-cached match
  count already final, or does it still need a live Wikidata intersection
  query?" It requires **both** sides of a pair to be swept before treating
  the cached count as trustworthy.
- **ADR-0088** (this ADR) governs `PlayerCareerPrefetchService.SweepAsync`
  (`XGArcade.DataSync`, COMP-07). Its question is *single-row*: "has this
  one country's or club's own pool sweep already completed successfully,
  such that re-running the sweep for it would produce nothing new?" There
  is no pairing involved — a row is skipped or not entirely on its own
  `PlayerPoolSweptAt`.

Both are readers of the same underlying signal, stamped by the same write
path (`PlayerCareerPrefetchService`'s own success path, unchanged by this
ADR). A future reader must not conflate the two: this ADR does not change
ADR-0078's "both sides must be swept" pairwise rule, and ADR-0078's
pairwise rule has no bearing on this ADR's single-row skip.

## Invalidation contract (reaffirmed, unchanged)

This ADR adds a new **reader** of `PlayerPoolSweptAt`, not a new writer and
not a new invalidator. The only two places that null `PlayerPoolSweptAt`
remain exactly what ADR-0078 established:

- `StaleClubAttributeCleaner` (REQ-111, named or `--all-clubs` mode) —
  nulls the affected `ClubDefinition` row(s)' `PlayerPoolSweptAt` alongside
  the `PlayerAttribute`/`PlayerData` rows it already clears.
- `purge-player-pool` (REQ-112/S-038) — nulls `PlayerPoolSweptAt` on every
  `CountryDefinition`/`ClubDefinition` row alongside the `Player` cascade
  it already deletes.

Because `SweepAsync`'s new skip checks the live column value on every run,
a row invalidated by either tool is `null` again by the time the next
`prefetch-player-careers` dispatch reaches it, and is therefore swept for
real rather than skipped — the same "purge and re-warm forces a real, full
re-check" invariant REQ-110's own history already documents for
`PlayerCacheWarmingService`. This is verified directly by a new test,
`REQ110_PrefetchAsync_CountryReSweptAfterInvalidation_QueriesWikidataAgain`
(`backend/tests/XGArcade.DataSync.Tests/Wikidata/PlayerCareerPrefetchServiceTests.cs`),
which nulls `PlayerPoolSweptAt` mid-test and confirms a second
`PrefetchAsync` call queries Wikidata again for that row.

## Consequences

- Positive: a burst of re-dispatches (whether manual, as in the incident,
  or a future automated retry) after a successful run costs approximately
  nothing — no live Wikidata query and no Supabase Postgres read-back for
  any row already swept — directly removing the confirmed root cause of
  the egress spike that triggered this ADR.
- Positive: `PlayerCareerPrefetchResult.CountriesSkipped`/`ClubsSkipped`
  gives an operator visible confirmation, in the job's own summary line,
  that a re-dispatch actually was cheap, rather than having to infer it
  from an absence of Wikidata query volume elsewhere.
- Negative / trade-off accepted: a re-dispatch can no longer be used as a
  crude way to "refresh everything" — it now only fills in what was never
  swept or was explicitly invalidated. This matches REQ-110's already-
  documented staleness profile ("as fresh as of whenever it was last
  successfully swept," ADR-0078's own Consequences section) and is not a
  new trade-off this ADR introduces, only one this ADR now also applies to
  `PlayerCareerPrefetchService` itself, not only to `PlayerCacheWarmingService`.
- Negative / trade-off accepted: `SweepOutcome`/`PlayerCareerPrefetchResult`
  gained a new `Skipped`/`CountriesSkipped`/`ClubsSkipped` field each,
  defaulted for backward compatibility with any existing caller that
  constructs these records positionally.
- Follow-up: none currently identified for this specific fix. The sibling
  fix (#3) considered in the same story — narrowing the dedup read-back
  queries' own column projection — was evaluated and explicitly deferred
  (see `docs/backlog.md` S-186's "Built as" section) since this ADR's skip
  already eliminates the dominant cost; it remains a candidate only if
  egress is still a concern after this fix and the workflow concurrency
  fix (#2, same story) are both live.

## For AI agents

Do not "helpfully" remove or weaken this skip during a future refactor of
`PlayerCareerPrefetchService.SweepAsync` (e.g. moving the `getSweptAt`
check after `fetchPoolAsync`, or dropping it as "dead code" because it
looks redundant with `markSweptAsync`'s own idempotent write) — doing so
would silently reintroduce the exact incident this ADR exists to prevent:
unconditional full re-sweeps on every dispatch, with no cost signal
visible until the next egress bill.

If you add a new writer of `PlayerPoolSweptAt` anywhere in this codebase,
confirm it correctly signals "this needs re-sweeping" (i.e. it either
leaves the column `null`/unchanged for an incomplete or invalidated pool,
or explicitly nulls it as part of a real invalidation, matching
`StaleClubAttributeCleaner`/`purge-player-pool` above) — this ADR's skip
and ADR-0078's pairwise skip both now trust that column at face value, in
two different services, and a writer that sets it incorrectly would
silently break both.

Do not read this ADR as widening ADR-0078's own pairwise rule, or as
license to relax ADR-0078's "both sides swept" requirement — they are
different questions in different services; see "Distinguishing this from
ADR-0078" above.
