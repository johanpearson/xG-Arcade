# ADR-0078: Mark a fully-swept, genuinely-low pair `ConfirmedLowMatchPair` without a live Wikidata round-trip

- **Status:** Accepted
- **Date:** 2026-08-18
- **Related requirements:** REQ-110, REQ-111, REQ-112
- **Related components:** COMP-05 (Games.XGGrid), COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients)

## Context

ADR-0077 (S-159) made `PlayerCareerPrefetchService`'s country/club pool
sweeps also write `PlayerAttribute` — every player in a seeded country's or
club's full Wikidata pool, not just the ones an actual pairwise grid lookup
happened to touch. Once a specific country AND a specific club have both
been fully swept this way, `PlayerAttributeRepository
.CountPlayersWithBothAttributesAsync`'s local join for that pair is no
longer a partial cache hint — it is the **true, final** count. Nothing a
live Wikidata query could return would change it: every player who could
possibly satisfy both attributes is already loaded locally, because the
pool sweep that produced each side was itself unfiltered and complete.

`PlayerCacheWarmingService.WarmAsync` doesn't know this yet. Its loop still
treats `cachedCount < options.MinValidAnswers` as "go query Wikidata live,"
identically for a pair neither side has ever been swept for and a pair
where both sides were fully swept minutes ago and the count is already
provably final. For the latter case, that live query is pure waste: it
costs a real WDQS round-trip (and, per the 2026-08-18 incident that
motivated ADR-0077 in the first place, is exactly the kind of query most
likely to be a large, slow, or failure-prone combinatorial join) to confirm
something the database can already prove without asking anyone.

This is the follow-up ADR-0077's own Consequences section flagged rather
than solved, and it is a genuinely new decision, not a mechanical extension
of that ADR: it requires a new signal — "has this specific reference value
been fully swept" — that does not exist anywhere in this codebase today.

## Decision

`CountryDefinition` and `ClubDefinition` each gain a nullable
`PlayerPoolSweptAt` (`DateTime?`) column. `PlayerCareerPrefetchService` sets
it to the current UTC time on the corresponding row the moment that
specific country's/club's pool sweep completes successfully in a given run
(i.e. inside the existing `countriesProcessed++`/`clubsProcessed++` success
path — never on a QID-skip or a caught `WikidataQueryException`, both of
which mean the pool was NOT actually fully fetched this run and must not
be marked swept).

`PlayerCacheWarmingService.WarmAsync`, in its `cachedCount <
options.MinValidAnswers` branch, checks whether **both** sides of the pair
already have a non-null `PlayerPoolSweptAt` (`country.PlayerPoolSweptAt is
not null && club.PlayerPoolSweptAt is not null` for the Country×Club loop;
the equivalent pair of `ClubDefinition` rows for the Club×Club loop). If
both are set, the pair's local count is already final: skip the live
Wikidata call entirely and call `playerDataQualityRepository
.RecordConfirmedLowAsync` directly with `cachedCount` as the confirmed
match count. If either side is null (never yet swept, or swept then
invalidated — see below), fall through to the existing live-query behavior
unchanged.

**Invalidation is not optional, and is the harder half of this decision.**
`PlayerPoolSweptAt` is a claim about data completeness that can go stale
the same way `ConfirmedLowMatchPair`/`PairLookupFailure` already can
(ADR-0050/ADR-0052's own history in this codebase — a persisted skip-signal
with no invalidation story caused a real incident once already, and this
ADR does not get to repeat it):

- `StaleClubAttributeCleaner.CleanAsync`/`CleanAllSeededClubsAsync`
  (REQ-111) — a `WikidataQid` correction or the truthy-`wdt:P54` all-clubs
  fix wipes a club's `PlayerAttribute`/`PlayerData` rows. It must now also
  null out that `ClubDefinition` row's `PlayerPoolSweptAt`, or a stale
  "already fully swept" claim would wrongly suppress the real re-sweep
  `warm-grid-cache`'s live-query fallback would otherwise still be able to
  perform for that club going forward.
- `purge-player-pool` (REQ-112/S-038, `CliVerbDispatcher
  .HandlePurgePlayerPoolAsync`) deletes every `Player` row (cascading
  through `PlayerAttribute`/`PlayerData`/etc.) but does not touch
  `CountryDefinition`/`ClubDefinition` rows at all today. It must now also
  reset `PlayerPoolSweptAt` to `null` on every `CountryDefinition`/
  `ClubDefinition` row, for the same reason at full-reset scope.

No new REQ ID: this extends REQ-110's existing "proactive player-attribute
cache warming" acceptance criteria with an "Extended (2026-08-18)"
criterion, the same pattern every prior REQ-110 extension in this codebase
already uses (persisted confirmed-low signal, technical-failure visibility,
persistent-failure tracking).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| A single "last successful prefetch run" timestamp, compared against each reference row's own creation/last-correction timestamp | One value to maintain instead of two per-row columns | `CountryDefinition`/`ClubDefinition` have no "last corrected" timestamp today (a `WikidataQid` fix is a plain field update with no audit trail) — would require adding that first, for both entities, just to make this comparison possible; also conflates "this specific club was swept" with "some prefetch run happened," which is wrong the moment a new club is seeded between two prefetch runs (it would read as "swept" for every club including the brand-new one that was never actually touched) | Rejected — solves a smaller problem (one shared timestamp) by creating a bigger one (false positives for never-actually-swept rows), and still needs new columns anyway |
| A new table (mirroring `ConfirmedLowMatchPair`'s own composite-key-row shape) recording "this country/this club was swept as of this timestamp" | Consistent with this codebase's existing pattern for exactly this kind of derived, invalidatable signal | The signal is 1:1 with a single `CountryDefinition`/`ClubDefinition` row, not a pair — a separate table keyed by a single reference value adds a join for no benefit over a column on the row it describes | Rejected — `ConfirmedLowMatchPair`'s table shape exists because ITS signal is inherently about a *pair*; this signal is about a single reference value, so a column is the simpler, equally-invalidatable fit |
| Infer "fully swept" implicitly from whether `prefetch-player-careers` has ever completed at all (a single global flag) | Simplest possible signal | Exactly the same false-positive problem as the timestamp-comparison option above, worse: a single global success flag would treat a country/club added *after* the last successful run as "swept" too, which is actively wrong, not just imprecise | Rejected outright — this is the shape ADR-0077's own scope note about "no fresh product decision without evidence" warns against: a coarse signal that's wrong in a case this codebase already knows happens routinely (S-159's own PR added new seeded clubs mid-project) |
| Do nothing — leave the follow-up unaddressed indefinitely | No new work | The real cost this ADR removes (a live Wikidata round-trip per fully-swept-but-low pair, every `warm-grid-cache` run, forever) persists — ADR-0077's whole point was reducing WDQS load on the failure-prone club×club shape; this specific waste is a self-inflicted subset of that same load this codebase can eliminate with certainty, not a hard external constraint | Rejected — the product owner already flagged this as the intended next step in S-159's own backlog entry (S-160) |

## Consequences

- Positive: a fully-swept, genuinely-low pair is confirmed on the very
  first `warm-grid-cache` run after both sides finish sweeping, with zero
  additional Wikidata load — not "eventually, after `ConfirmedLowMatchPair`
  happens to get set by a live query that succeeds," which today can take
  multiple runs if the live query itself keeps failing (the exact
  `PairLookupFailure` class of pair this ADR is most valuable for, since
  those are disproportionately the pairs that would otherwise never
  successfully confirm-low at all).
- Positive: directly reduces the live-query volume `warm-grid-cache` sends
  to WDQS, on top of ADR-0077's own reduction — compounding, not competing,
  with that ADR's goal.
- Negative / trade-off accepted: two new nullable columns and a migration;
  two new invalidation call sites that must be kept correct going forward
  (flagged explicitly in "For AI agents" below).
- Negative / trade-off accepted: `PlayerPoolSweptAt` being non-null does
  NOT mean "this club's data is fresh as of right now" — only "as of
  whenever it was last successfully swept." A player who moves clubs the
  day after a sweep is invisible to that club's attribute set until the
  next `prefetch-player-careers` run, same staleness profile ADR-0077
  already accepted for the underlying data; this ADR doesn't change that
  window, it only changes whether `warm-grid-cache` also pays a live-query
  cost to re-derive a count that's already final *as of that same window*.
- Follow-up: none currently identified — this closes the gap ADR-0077's
  Consequences section flagged.

## For AI agents

`PlayerPoolSweptAt` MUST be set only in `PlayerCareerPrefetchService`'s
success path — never on a null-QID skip, never on a caught
`WikidataQueryException`. Setting it on anything less than a genuinely
complete pool fetch would make `PlayerCacheWarmingService` silently trust
an incomplete pool as final, permanently suppressing a real re-check for
that reference value (the exact failure mode ADR-0050/ADR-0052's own
history already documents once).

Both invalidation sites — `StaleClubAttributeCleaner` (REQ-111) and
`purge-player-pool` (REQ-112/S-038) — MUST clear `PlayerPoolSweptAt`
alongside whatever they already clear. If you are adding a third tool that
clears `PlayerAttribute`/`PlayerData`/`ConfirmedLowMatchPair`/
`PairLookupFailure` for a country or club, it must also clear
`PlayerPoolSweptAt` for that same scope, or you are reintroducing this same
stale-skip-signal bug class in a new place.

Do not widen this to skip the live query for a pair where only ONE side is
swept — the decision requires BOTH sides swept, because a partial pool on
either side means the true match count is still unknown, not just
"probably low."
