# ADR-0092: Targeted, date-filtered recent-transfer sweep — a third, orthogonal freshness mechanism

- **Status:** Accepted
- **Date:** 2026-08-29
- **Related requirements:** REQ-110 (see "The REQ-110 tag" below for the accepted imperfection in this fit)
- **Related components:** COMP-07 (DataSync.Clients)

## Context

ADR-0088 (2026-08-25, S-186) fixed a confirmed Supabase free-tier egress
incident by making `PlayerCareerPrefetchService.SweepAsync` skip a
country/club forever once its `PlayerPoolSweptAt` is non-null. ADR-0090
(2026-08-29, S-187) partially reopened that skip-forever default with a
small, bounded, weekly rotation (`maxEntitiesToResweep`, default 2,
oldest-`PlayerPoolSweptAt`-first) so a transfer into an already-swept pool
is eventually noticed again — but explicitly accepted "freshness within
roughly a season, not real-time" as the trade-off: a full rotation cycle is
on the order of ~49 weeks for countries and ~15 weeks for clubs.

That rotation cadence is fine for general drift, but it is the wrong tool
for a specific, recurring operational need: an operator wanting to confirm
a club's roster reflects transfers that happened right around a known
transfer-window deadline day (the ~4-6-week windows FIFA/domestic leagues
run twice a year), not whenever that club's turn in a season-long rotation
happens to come up. Waiting out ADR-0090's rotation for that purpose could
mean anywhere from one week to most of a year, which defeats the purpose of
checking around a specific deadline at all.

This story (S-188, `docs/backlog.md` Epic 26) adds a third mechanism to
close that specific gap, without reopening or competing with either of the
first two.

## Decision

A new `IRecentTransferSweepService`/`RecentTransferSweepService`
(`XGArcade.DataSync`) iterates every seeded `ClubDefinition` with a
resolved `WikidataQid` and, for each, runs two new targeted,
date-filtered SPARQL queries — `SparqlQueryBuilders.BuildRecentClubArrivalsQuery`
(`pq:P580` "joined since" `FILTER`, mandatory bind) and
`BuildRecentClubDeparturesQuery` (`pq:P582` "departure recorded since"
`FILTER`, mandatory bind; `?startTime` OPTIONAL) — both using the full
`p:P54`/`ps:P54` statement path, since the qualifiers this query shape
depends on do not exist under the truthy `wdt:P54` shortcut at all. The
cutoff, `sinceUtc = DateTime.UtcNow.AddDays(-lookbackDays)`, is computed
once per run and applied identically to every seeded club; `lookbackDays`
is CLI-supplied with a default of 30 days (a full typical transfer
window's worth of overlap).

An arrival get-or-creates the `Player` row
(`GetOrCreatePlayersByWikidataQidAsync`, the same precedent
`PlayerCareerPrefetchService.FetchAndPersistBatchAsync` already uses) and
reconciles via `PlayerCareerStintRefreshService.BuildNewStintsByPlayerId` ->
`CareerStintReconciler.Reconcile` (ADR-0091) — reused verbatim, never
reimplemented. A brand-new `(ClubName, StartYear)` inserts a new
`PlayerCareerStint` row; a departure that matches an existing ongoing
stint's `(ClubName, StartYear)` completes it in place
(`UpdateCareerStintCompletionsAsync`) instead of inserting a duplicate,
exactly the mechanics ADR-0091 already established for the other two
reconciliation call sites.

A new CLI verb, `sweep-recent-transfers [lookbackDays]`, and a new
workflow, `sweep-recent-transfers.yml` (`workflow_dispatch` only — see
"Cadence decision" below), with the standard
`concurrency: { group: ${{ github.workflow }}, cancel-in-progress: false }`
guard every bulk Wikidata workflow already uses.

## Explicit reconciliation with ADR-0088 and ADR-0090

This is the third freshness mechanism in the same lineage, and it is
deliberately **orthogonal**, not a competing or overlapping one:

- It never reads or writes `PlayerPoolSweptAt` at all. It does not
  participate in ADR-0088's skip-forever check or ADR-0090's rotation
  selection in either direction — a club touched by this sweep is not
  marked "fully re-verified" (writing `PlayerPoolSweptAt` here would be
  actively wrong: it would tell ADR-0088's skip-forever check that the
  club's **entire** player pool was re-confirmed, when only a narrow
  recent-activity slice actually was), and a club's place in ADR-0090's
  rotation queue is completely unaffected by a `sweep-recent-transfers`
  run touching it.
- It is a narrower, operator-triggered supplement for a specific
  situation the other two mechanisms don't serve well: faster-than-the-
  rotation freshness around a known event (a transfer-window deadline),
  not a substitute for either the skip-forever default or the weekly
  rotation.
- **Why this cannot reintroduce the S-186 incident:** the cost is bounded
  on two independent axes. First, the club set itself is bounded — the
  ~15 seeded `ClubDefinition` rows, times 2 queries (arrivals +
  departures) each, is ~30 SPARQL queries per run, an order of magnitude
  below the ~64-entity full pool ADR-0088's incident re-swept
  unconditionally. Second, and more importantly, each individual query's
  own *result size* is bounded by real transfer activity in the lookback
  window, filtered server-side by WDQS (`FILTER(?startTime >= ...)`/
  `FILTER(?endTime >= ...)`) — not by squad size the way a full pool
  fetch is. A club with an unusually large squad costs exactly the same
  as a small one; only actual transfer volume in the window changes the
  result size. This is a fundamentally different cost shape from the
  incident's root cause (an unconditional full-pool re-fetch per
  dispatch), not just a smaller version of it.

## Cadence decision: `workflow_dispatch`-only, no cron, for now

This is a deliberate call with two independent reasons, not one:

1. **This is a brand-new, unproven query shape.** Neither
   `BuildRecentClubArrivalsQuery` nor `BuildRecentClubDeparturesQuery` has
   ever been run against the real `query.wikidata.org` endpoint from this
   sandbox. `prefetch-player-careers.yml` itself set the precedent for
   exactly this situation: it started `workflow_dispatch`-only and stayed
   that way until a real run's cost/runtime/failure pattern was confirmed
   — only then did `resweep-player-careers.yml`'s cron get added (ADR-0090,
   built directly on top of the now-proven `prefetch-player-careers`
   query shapes). This story follows that same bootstrapping discipline
   rather than assuming a new query shape is safe to run unattended from
   day one, even though a pessimistic cost estimate (~30 small,
   WDQS-server-filtered queries, a handful of Postgres reads/writes) looks
   safe on paper.
2. **The underlying product need is inherently event-driven, not
   continuous.** Transfer windows run ~4-6 weeks, twice a year. A
   365-day/year cron would spend the overwhelming majority of its runs
   finding nothing — near-zero Postgres writes outside a window — for a
   benefit concentrated in a few weeks. That is unnecessary operational
   surface (CI minutes, a routinely-green-but-pointless job, one more
   thing that can go red for no player-facing reason) for zero added
   freshness benefit outside the windows this mechanism actually exists
   to serve.

**Concrete trigger for revisiting this:** once a real dispatch confirms
this query shape's cost and behavior are as cheap as estimated, consider
adding a cron — but a narrow one scoped to the actual transfer-window date
ranges, not a naive daily/weekly schedule — and only once the product
owner actually wants always-on coverage rather than an operator-triggered
tool for known deadline dates. Until then, this stays manual, mirroring
`prefetch-player-careers.yml`'s own bootstrap-then-automate history.

## The `lookbackDays`-vs-`PlayerPoolSweptAt` cutoff choice

The cutoff is `DateTime.UtcNow.AddDays(-lookbackDays)`, a fixed,
CLI-supplied, operator-chosen day count — deliberately not tied to any
given club's own `PlayerPoolSweptAt`. Anchoring the window to
`PlayerPoolSweptAt` instead would make the window's actual size a function
of ADR-0090's own rotation state, which can be anywhere from freshly swept
to ~15 weeks stale depending on where that club currently sits in the
rotation queue — unpredictable and non-obvious to reason about from the
outside. A fixed, operator-chosen day count is simpler to reason about and
directly useful for the actual use case this mechanism exists for: someone
dispatching it specifically because a deadline day is approaching,
regardless of how recently the rotation last touched any given club.

## THE Grid-vs-Path freshness asymmetry (accepted, deliberate scope boundary)

This is the single most important boundary this ADR records, and it must
not be treated as an incidental implementation detail left only in code
comments.

`RecentTransferSweepService` **deliberately never writes**
`PlayerAttribute`/`PlayerData` — the table xG Grid's correctness-checking
path actually reads (ADR-0007's autocomplete/correctness separation: only
`PlayerAttribute`/`PlayerOverride` ever back a guess verdict) — and
**deliberately never touches** `CountryDefinition`/`ClubDefinition.PlayerPoolSweptAt`.

The consequence: a transfer this mechanism picks up becomes visible in xG
Path's career timeline (COMP-11, REQ-1203's clue-reveal) sooner than
ADR-0090's rotation would surface it, but it does **not** become a valid xG
Grid guess answer for that club any sooner than ADR-0090's rotation (or a
full unbounded `prefetch-player-careers` run) would make it.

This is a deliberate boundary, not an oversight, for a specific reason:
**`PlayerAttribute` is not safe to write ad hoc.** It is coupled to
machinery this service has no way to safely participate in:

- **`ConfirmedLowMatchPair`** (ADR-0050) persists a "confirmed genuinely
  low match count" marker for a specific Country×Club or Club×Club pair,
  trusted by `PlayerCacheWarmingService` as a final answer once persisted.
- **`PairLookupFailure`** (ADR-0052) persists cross-run technical-failure
  state for a pair's live lookup, with its own separate reset/invalidation
  trigger.
- **`PlayerPoolSweptAt`-gated "both sides swept ⇒ trust the cached pair
  count as final" logic** (ADR-0078): once both a Country's and a Club's
  pools are fully swept, `PlayerCacheWarmingService.WarmAsync` skips the
  live Wikidata call entirely and calls `RecordConfirmedLowAsync` directly
  with the locally-cached count, trusting it as provably final.

A naive "also write `PlayerAttribute` here" change — inserting a newly
arrived player into a Country×Club or Club×Club pair's local answer pool
— would insert that player **without going through any of that
invalidation machinery**. Concretely: if the pair this transfer just
affected was previously recorded `ConfirmedLowMatchPair` (or has a
`PairLookupFailure` row), that marker would now silently be stale — the
locally-cached count it was based on would have grown by one — but nothing
in this service's write path clears or re-verifies it. Worse, if both
sides of that pair were already `PlayerPoolSweptAt`-swept and being
trusted by ADR-0078's "both sides swept ⇒ final" logic, a raw
`PlayerAttribute` insert here would silently corrupt that cached-final
count without re-running the verification that made it trustworthy in the
first place. `RecentTransferSweepService` has no way to know, from where it
sits, whether the pair it just touched was previously confirmed-low or
swept-final, and would need its own re-verification/invalidation step —
mirroring what `StaleClubAttributeCleaner`/`purge-player-pool` already do
for other invalidation triggers — to do this safely. That is real,
separately-scoped design work, not a natural one-line extension of this
diff.

**Stated plainly:** if xG Grid answer-key freshness around a transfer
deadline is still wanted, it needs its own follow-up story with its own
ADR that addresses this `ConfirmedLowMatchPair`/`PairLookupFailure`/
`PlayerPoolSweptAt` invalidation risk directly — it must not be assumed to
already be solved by this one.

## The REQ-110 tag

Both `architecture-reviewer` and `quality-architect` independently flagged,
during this story's review, that REQ-110 ("Proactive player-attribute
cache warming") is specifically about `PlayerAttribute` cache warming —
this service never writes `PlayerAttribute` at all, so tagging this
story's code/tests REQ-110 throughout (matching S-186/S-187's own
precedent and `docs/backlog.md`'s S-188 heading) is a stretch. This is not
blocking: it is the established tag per the backlog entry, and renaming it
now would only create churn for no behavioral benefit. This is recorded
here as a known, accepted imperfection, not silently smoothed over.

Recommended (not required): `requirements-writer` could look at whether a
narrower REQ ID scoped to "xG Path career-stint freshness" — distinct from
REQ-110's xG Grid cache-warming scope — is worth filing later, covering
S-186/S-187/S-188's `PlayerCareerStint`-only pieces collectively, if that
turns out cleaner than continuing to retrofit REQ-110 onto work that isn't
really about `PlayerAttribute`.

## Related components

- **COMP-07 (DataSync.Clients)** — primary: `SparqlQueryBuilders`,
  `IWikidataClient`/`WikidataClient`, `IRecentTransferSweepService`/
  `RecentTransferSweepService` all live here.
- **COMP-11 (Games.XGPath)** — beneficiary: a freshly-transferred player's
  `PlayerCareerStint` becomes visible to xG Path's career-stint clue
  timeline sooner than ADR-0090's rotation would surface it.
- **COMP-05/COMP-06's `PlayerAttribute`/Grid-answer-key surface is
  explicitly NOT touched by this ADR's decision** — see "The Grid-vs-Path
  freshness asymmetry" above. This ADR intentionally does not list
  COMP-05/COMP-06 as related components for that reason: nothing in either
  component's read or write surface changes as a result of this decision.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Tie the lookback cutoff to each club's own `PlayerPoolSweptAt` instead of a fixed `lookbackDays` | One fewer CLI parameter; "since we last fully checked this club" reads as intuitively correct | Makes the window's actual size a function of ADR-0090's rotation state (0-15 weeks stale, unpredictable) — undermines the one thing this mechanism exists to provide: a predictable window an operator can reason about right before a known deadline | Rejected — predictability for the operator-triggered use case matters more than a marginally "smarter" cutoff |
| Also write `PlayerAttribute`/`PlayerData` for a newly arrived player, so the transfer is a valid Grid answer sooner too | Would close the Grid-vs-Path freshness gap in the same story | Risks silently corrupting a `ConfirmedLowMatchPair` row or an ADR-0078 "both sides swept ⇒ final" cached count with no re-verification step — a real invalidation-safety problem, not a trivial addition; see "The Grid-vs-Path freshness asymmetry" above | Rejected for this story — flagged explicitly as a candidate follow-up needing its own ADR, not solved here |
| Add this as a third mode of `PlayerCareerPrefetchService`/`prefetch-player-careers.yml` instead of a new service/workflow | Fewer new files | The query shape, cost profile, and write surface (`PlayerCareerStint`-only, no `PlayerPoolSweptAt`) are different enough from a full pool sweep that folding it in would blur `PlayerCareerPrefetchService`'s single existing responsibility and complicate its `PlayerPoolSweptAt` bookkeeping with a mode that must never touch that column | Rejected — a separate service keeps the "does/doesn't touch `PlayerPoolSweptAt`" boundary mechanically obvious rather than relying on an internal branch to get it right every time |
| Add a cron to `sweep-recent-transfers.yml` from day one, scoped to typical transfer-window dates | Immediate always-on coverage for the actual use case | This is an unproven query shape with zero real-world runs; adding unattended scheduling before a single manual run has confirmed cost/behavior repeats exactly the "assumed safe, discovered otherwise late" pattern ADR-0088's incident grew out of, just with a new query shape instead of the old unconditional one | Rejected for now — see "Cadence decision" above for the concrete trigger to revisit this |

## Consequences

- Positive: an operator can get near-real-time confirmation of transfers
  around a specific deadline day without waiting out ADR-0090's
  season-long rotation, and without reintroducing ADR-0088's unbounded
  full-pool-sweep cost profile.
- Positive: reuses ADR-0091's `CareerStintReconciler` machinery verbatim
  for both arrivals (insert) and departures (in-place completion) — no
  new reconciliation logic, no new duplicate-stint risk class.
- Positive: cost is bounded on two independent axes (small fixed club
  count, and WDQS server-side date filtering bounding each query's own
  result size by real activity, not squad size) — this mechanism cannot,
  by construction, reproduce the S-186 incident's shape.
- Negative / trade-off accepted: `workflow_dispatch`-only for now — no
  automatic coverage of a transfer window unless an operator remembers to
  dispatch it. See "Cadence decision" for the concrete revisit trigger.
- Negative / trade-off accepted (the most significant one): a transfer
  this mechanism picks up is visible in xG Path sooner but is **not** a
  valid xG Grid guess answer any sooner than ADR-0090's rotation would
  make it — a real, stated Grid-vs-Path freshness asymmetry, not an
  oversight. See "The Grid-vs-Path freshness asymmetry" above.
- Negative / trade-off accepted: this story's code/tests are tagged
  REQ-110 despite this service never writing `PlayerAttribute` — an
  accepted, non-blocking imperfection in the tag's fit. See "The REQ-110
  tag" above.
- Follow-up: closing the Grid-vs-Path freshness gap (making a
  recently-transferred player a valid Grid answer sooner than ADR-0090's
  rotation) needs its own follow-up story and its own ADR addressing the
  `ConfirmedLowMatchPair`/`PairLookupFailure`/`PlayerPoolSweptAt`
  invalidation risk described above — not assumed solved by this ADR.
- Follow-up: once a real dispatch confirms cost/behavior, consider a
  narrow cron scoped to actual transfer-window date ranges (see "Cadence
  decision" above) — not a structural change to this ADR's design, just a
  scheduling addition once proven safe and wanted.

## For AI agents

Do not "helpfully" extend `RecentTransferSweepService` to also write
`PlayerAttribute`/`PlayerData` (e.g. to make a freshly-discovered transfer
immediately usable as a Grid guess answer) without first reading and
solving the `ConfirmedLowMatchPair`/`PairLookupFailure`/`PlayerPoolSweptAt`
invalidation risk described in "The Grid-vs-Path freshness asymmetry"
above. That risk is real, not hypothetical caution — a naive write there
could silently corrupt a `ConfirmedLowMatchPair` row or an ADR-0078
"both sides swept ⇒ final" cached pair count with no re-verification.
Closing that gap needs a new ADR of its own, not a silent addition to this
service.

Do not have this service write or read `PlayerPoolSweptAt` "for
consistency with the other prefetch jobs" — doing so would misrepresent a
narrow, date-filtered slice as a full pool re-verification to ADR-0088's
skip-forever check, silently causing it to skip a real future full sweep
that a narrow slice cannot substitute for.

Do not add a cron to `sweep-recent-transfers.yml` "since the cost looks
cheap on paper" without re-reading "Cadence decision" above — the call to
stay manual-only for now rests on this being an unproven query shape with
zero real-world runs, not only on cost, and that needs a real dispatch to
resolve first.
