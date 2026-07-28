# ADR-0050: Persist a "confirmed genuinely low" signal for cache-warming pairs in a new ConfirmedLowMatchPair table

- **Status:** Accepted
- **Date:** 2026-07-28
- **Related requirements:** REQ-110
- **Related components:** COMP-05 (Games.XGGrid), COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients)

## Context

`PlayerCacheWarmingService.WarmAsync` (REQ-110, ADR-0024) iterates every
reference Country×Club and Club×Club pair and skips any pair already
cached at or above `MinValidAnswers`. It could not, until now, skip a pair
that is cached *below* `MinValidAnswers` — REQ-110's original text accepted
this as a known gap, since there was no persisted way to tell "checked,
genuinely below threshold" apart from "never checked at all."

The reason this distinction didn't already exist for free:
`WikidataLookupService`'s contract is that a query finding truly zero
matches persists nothing (`PersistMatchesAsync`'s early
`if (matches.Count == 0) return [];`). A pair with *some* real matches below
threshold already has an implicit signal — its matches exist as ordinary
`PlayerAttribute` rows, so `CountPlayersWithBothAttributesAsync` can already
report "checked, N < MinValidAnswers real matches." A pair with *zero* real
matches has no such trace at all, so it is indistinguishable from a pair
never queried in the first place.

This gap was tolerable while cache warming ran occasionally. It stopped
being tolerable once it started running roughly daily: one measured run
(2026-07-27) live-queried 1214 pairs, of which 1207 were pairs Wikidata had
already answered successfully and confirmed genuinely below
`MinValidAnswers` on a prior run — re-queried for zero possible benefit,
burning real CI minutes and Wikidata load on every single run.

REQ-110's own "Extended (2026-07-28) — persisted confirmed-low signal"
criterion requires closing this gap but explicitly leaves the persistence
mechanism open: "The exact persistence mechanism (new table, new column,
reuse of an existing one) is an implementation detail for
`backend-implementer`, not specified here — but [the re-check-after-a-purge
invariant] is not." That is a real, could-have-gone-another-way structural
choice — this ADR formalizes it.

## Decision

Add a new entity/table, `ConfirmedLowMatchPair` (`XGArcade.Data.Entities`),
owned by COMP-06 (Data.PlayerStore) alongside `PlayerData`/`PlayerOverride`/
`PlayerAttribute`/`PlayerAlias`, reachable only through two new
`IPlayerStoreRepository` methods — `IsConfirmedLowAsync` (read) and
`RecordConfirmedLowAsync` (write, upsert) — never through a direct
`DbContext` query from `Games.XGGrid`. This keeps boundary rule 1
(`architecture-document.md` §5) intact: `PlayerCacheWarmingService` (COMP-05)
reaches this new state exactly the way it already reaches every other piece
of COMP-06's data.

One row per checked pair, composite primary key `(FirstAttributeType,
FirstAttributeValue, SecondAttributeType, SecondAttributeValue)` — the same
four-argument shape `CountPlayersWithBothAttributesAsync` already uses, since
that is the read this table's presence/absence short-circuits. `MatchCount`
(the real observed count, 0 for the genuine-zero case) is stored for operator
diagnostics only, never read by the skip check itself — presence of the row
is the only signal that matters. Deliberately **no foreign key to `Player`**:
a confirmed-low pair — especially the zero-match case this table exists
for — usually has no `Player` rows to reference at all.

`PlayerCacheWarmingService.WarmAsync` checks `IsConfirmedLowAsync` only after
a freshly-computed `cachedCount` for that pair has already shown it below
`MinValidAnswers` *this run* — so the check is safe even if `MinValidAnswers`
itself changed since the pair was confirmed (a lower current threshold means
the freshly-computed cached count already clears it and the confirmed-low
check is never reached; a higher one means the previously-observed real
`MatchCount`, an objective fact about Wikidata independent of any threshold,
is still below the new threshold too). It writes `RecordConfirmedLowAsync`
only after a live query returns a real (possibly zero-match) answer still
below `MinValidAnswers` — never after a technical failure, which is a
separate, already-distinguished signal (the same 2026-07-28 extension's
`onTechnicalFailure` hook) that must not be conflated with a genuine
low-match confirmation.

**Invalidation.** This table is deliberately not self-expiring — nothing in
it knows when reference data or a query shape changes. It is cleared by the
same two tools that already force a full re-check after such a change:
`StaleClubAttributeCleaner.CleanAsync`/`CleanAllSeededClubsAsync` (REQ-111,
both named-club and `--all-clubs` modes — now also deletes any
`ConfirmedLowMatchPair` row with a matching club on either side) and the
`purge-player-pool` CLI verb (REQ-112/S-038 — now also does an unscoped
`ConfirmedLowMatchPairs.ExecuteDeleteAsync()`, since `Player`'s cascade delete
does not reach this table, having no FK to it). A "purge and re-warm" cycle
still means a real, full re-check of every affected pair — never a warm run
trusting a stale confirmed-low marker left over from before the correction.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| New column on `PlayerAttribute` or `PlayerData` (e.g. a `ConfirmedLowAt` marker row, or a boolean flag) | Reuses an existing table, no new entity/migration | The genuine-zero case — the bulk of what this closes — has no corresponding `PlayerAttribute`/`PlayerData` row to hang a column off at all; would require inserting a synthetic sentinel row into a table whose entire existing meaning is "a real match was found," corrupting that meaning for every other reader (`HasEffectiveAttributeAsync`, guess correctness-checking, REQ-501's override precedence) | The natural key this state needs (a *pair*, not a single attribute value) doesn't fit either table's existing per-player row shape, and a sentinel row risks leaking into correctness-checking paths that were never designed to filter it out |
| No persistence — accept the re-querying cost as a permanent, documented trade-off (status quo) | Zero code change | Directly contradicts REQ-110's own updated acceptance criteria; burns real CI minutes and Wikidata load re-querying ~1200 already-known-low pairs on every roughly-daily run, for zero possible benefit — the exact problem this extension exists to fix | The whole point of this REQ extension is to stop paying this cost; leaving it unresolved abandons the requirement it's meant to satisfy |
| In-memory/ephemeral signal (e.g. compute and cache within a single `WarmAsync` run only) | No schema change, no invalidation surface to maintain | `PlayerCacheWarmingService` runs as a fresh CLI-verb process per invocation (ADR-0024) — nothing survives between runs without persistence, so an in-memory cache would only deduplicate work *within* one run (pairs are already visited once per run regardless) and would do nothing to stop the run-over-run re-querying that is the actual, measured problem | Doesn't address the problem: the cost REQ-110's extension names is cross-run, not within-run |
| A new table (chosen) | Natural fit for the actual shape of the data (a pair, independent of whether any `Player` rows exist for it); no risk of corrupting `PlayerAttribute`/`PlayerData`'s existing meaning; composite PK gives an O(1) presence check with no join | One more table/migration to maintain, and its own invalidation surface (mitigated by piggybacking on the two existing purge/clean tools rather than inventing a third) | Best fit for the actual data shape, smallest blast radius on existing correctness-checking code |

## Consequences

- Positive: a cache-warming run no longer re-queries the ~1200 pairs already
  known to be genuinely below `MinValidAnswers` — directly closes the
  measured 1207/1214-live-queries waste from the 2026-07-27 run, saving both
  CI minutes and Wikidata/WDQS load.
- Positive: stays entirely within COMP-06's existing boundary — no new
  cross-component data-access path, no change to boundary rule 1
  (`architecture-document.md` §5); `Games.XGGrid` reaches this new state only
  through `IPlayerStoreRepository`, same as everything else it reads.
- Negative / trade-offs accepted: a third invalidation call site
  (`StaleClubAttributeCleaner`, `purge-player-pool`) must now remember to
  clear `ConfirmedLowMatchPair` alongside `PlayerAttribute`/`PlayerData`
  whenever either of those extends further — a future change to either
  cleaner that forgets this table would silently reintroduce a "trusts a
  stale marker" bug with no compiler or test signal unless the regression
  test REQ-110's own test-level note requires is kept in place.
- Negative / trade-offs accepted (inherited, not new): both existing
  invalidation tools are club-scoped only — there is no equivalent
  "stale-country-attribute cleaner" for a `CountryDefinition` correction
  (a wrong `WikidataQid`, a `UsesCountryForSportProperty` flag toggle, a
  rename). This gap already existed for `PlayerAttribute` itself before this
  ADR; `ConfirmedLowMatchPair` now shares it rather than introducing it.
  Worth a `NOTES.md`/follow-up entry if a country-side reference-data
  correction is ever needed in practice, not a blocker for this decision.
- Not eligible for `infra/scripts/lib/game-data-tables.sh`'s prod/dev sync
  allowlist (ADR-0009). Unlike `PlayerAttribute`/`Player` (objective,
  environment-independent Wikidata facts the allowlist already carries),
  this table is a derived-and-operational marker about *this codebase's own
  cache-warming process state* — tied to whichever `MinValidAnswers`/query
  shape was in effect, and only ever useful to short-circuit a warming run,
  never consulted by grid generation or guess-checking directly. Syncing it
  would risk one environment's warming-run history silently suppressing a
  re-check the other environment's own reference-data state actually needs,
  undermining this ADR's own "purge forces a real re-check" invariant across
  an environment boundary the purge tools were never designed to reason
  about. The allowlist's own stated default (a new table is excluded until
  someone consciously adds it) already produces this outcome; this paragraph
  exists so that omission reads as a decision, not an oversight.
- Follow-up: monitor whether the two-tool invalidation surface stays
  sufficient as more reference-data-correction paths are added (e.g. if a
  country-side cleaner is ever built); it should clear `ConfirmedLowMatchPair`
  the same way the club-side one now does.

## For AI agents

Do not add a third way to invalidate `ConfirmedLowMatchPair` outside
`StaleClubAttributeCleaner`/`purge-player-pool` without also updating this
ADR — the whole point of piggybacking on those two is that there is exactly
one place each correction path's "force a real re-check" logic lives. Do not
add `ConfirmedLowMatchPair` to `infra/scripts/lib/game-data-tables.sh`'s sync
allowlist without a new ADR revisiting the reasoning above — this was a
deliberate exclusion, not an oversight. Do not read or write this table
through anything other than `IPlayerStoreRepository`'s
`IsConfirmedLowAsync`/`RecordConfirmedLowAsync` — a direct `DbContext` query
from `Games.XGGrid` would violate boundary rule 1 the same way a direct
`PlayerAttribute` query would.
