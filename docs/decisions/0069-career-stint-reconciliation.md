# ADR-0069: Career-stint writes reconcile a superseded ongoing stint instead of only ever adding rows

- **Status:** Accepted
- **Date:** 2026-08-16
- **Related requirements:** REQ-1210 (career-stint reconciliation, drafted alongside this ADR — REQ-1208 was already taken; see CHANGELOG's correction entry)
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-11 (Games.XGPath)

## Context

Issue #195 (user-reported): Kelechi Nwakali's xG Path clues show "Sociedad
Deportivo Huesca 2019-present" even though he left Huesca in 2022 and has
since played for SD Ponferradina, Chaves, and (currently) Barnsley. The data
itself isn't the problem — Wikidata almost certainly has the correct,
current statements. The problem is structural: every `PlayerCareerStint`
writer is **purely additive**.

Three call sites persist stints, all via `IPlayerCareerStintRepository
.AddCareerStintsBatchAsync`:

- `WikidataLookupService.PersistCareerStintsAsync` (ADR-0042's xG Grid
  lookup byproduct)
- `PlayerCareerStintRefreshService.RefreshCareerStintsAsync` (ADR-0054, xG
  Path per-target refresh)
- `PlayerCareerPrefetchService` (ADR-0055, proactive per-country prefetch),
  sharing `PlayerCareerStintRefreshService.BuildNewStintsByPlayerId`'s dedup
  logic with the refresh path

All three dedupe a freshly-fetched stint against what's already stored using
the same **full 4-tuple** — `(ClubName, StartYear, EndYear,
AppearanceCount)` — as the identity key: if the exact tuple isn't already
present, it's inserted as a new row. `AddCareerStintsBatchAsync` itself only
ever calls `dbContext.PlayerCareerStints.AddRange(...)`; there is no update
or delete path anywhere in this stack.

This means the *only* way for Nwakali's Huesca stint to ever stop reading
"present" is for some write path to fetch it again — S-127 (this same
investigation) already fixes that half by putting `prefetch-player
-careers.yml` on a weekly cron. But even a successful re-fetch would not fix
the display: the freshly-fetched entry `(Huesca, 2019, 2022, N)` doesn't
match the stored tuple `(Huesca, 2019, null, M)` under the current 4-tuple
key, so it would be inserted as a **second, new row** — and the codebase
now has "Huesca 2019-present" and "Huesca 2019-2022" simultaneously, two
xG Path clue nodes for one real stint. That's a different bug (duplicate
nodes, ADR-0059/ADR-0063's own subject), not a fix.

`PlayerCareerStint.EndYear`'s own doc comment already defines the
semantics we need to act on: "Null = an ongoing stint (no Wikidata P582
'end time' qualifier on this statement **yet**)" — "yet" implies the value
is expected to change over the player's career, but nothing before this ADR
ever revisits a row once written.

## Decision

Change the per-stint identity key used for matching a freshly-fetched
Wikidata entry against an existing stored row, in all three writer paths,
from the full 4-tuple to **`(ClubName, StartYear)`**. For a fetched entry
matched against an existing row under this narrower key:

1. **Exact match** (`EndYear`/`AppearanceCount` also equal) — no-op, same
   as today (idempotent re-fetch).
2. **An existing row's `EndYear` is `null`, fetched entry's `EndYear` is
   non-null** — the stint has concluded since it was last observed. Update
   the existing row in place: set `EndYear` and `AppearanceCount` to the
   freshly-fetched values. This is the exact shape of the reported bug and
   the case this ADR exists to fix. **The match key is not guaranteed
   unique before `DuplicateCareerStintCleaner` (ADR-0059/ADR-0063) has run**
   — a not-yet-cleaned cross-writer duplicate can leave two rows sharing
   `(ClubName, StartYear)`. This update applies to **every** existing row
   sharing the key with `EndYear: null`, not just the first one found — so
   an outstanding duplicate pair is closed identically on both rows rather
   than only one, keeping them mutually consistent (see Consequences for
   why this matters).
3. **Existing row's `EndYear` is already non-null and disagrees with the
   fetched value** — deliberately **not** auto-resolved. Neither update nor
   insert; log a warning (same observability pattern as
   `PlayerCareerStintRefreshService`'s existing `WikidataQueryException`
   handling) and leave the row untouched. This could be a genuine Wikidata
   correction, or two distinct real stints that happen to share a
   `(ClubName, StartYear)` (e.g. a loan cut short and a same-year return) —
   guessing wrong in either direction risks silently corrupting a real
   historical record, which is worse than a stale-but-accurate-as-of-last-fetch
   row. Left as a follow-up (see Consequences).
4. **No existing row shares `(ClubName, StartYear)`** — insert as a new row,
   unchanged from today.

Implementation shape: extract the three writers' independent (and, in
`WikidataLookupService.PersistCareerStintsAsync`'s case, inline-duplicated)
tuple-comparison logic into one shared reconciliation helper (extending
`PlayerCareerStintRefreshService.BuildNewStintsByPlayerId`, already shared
by the refresh and prefetch paths) that returns both new-stints-to-insert
and existing-stints-to-close, per player — the latter identified by **row
`Id`**, not just `(ClubName, StartYear)`, since the plan is built from an
`AsNoTracking` read (`GetCareerStintsByPlayerIdsAsync`) but must be applied
against `AddCareerStintsBatchAsync`'s separately, freshly-loaded **tracked**
query; the `Id` is what lets that tracked query re-locate the correct
entity to mutate; the plan's `(ClubName, StartYear)` alone would not.
Mutating a matched row's `EndYear`/`AppearanceCount` before the existing
`SaveChangesAsync()` call is sufficient for EF Core to persist the update;
no new repository method, migration, or explicit `ExecuteUpdateAsync` is
needed. The existing chronological `SequenceOrder` resequencing pass
(already re-runs across every row, existing + new, on every batch)
naturally picks up an updated row's new `EndYear` for re-sorting, since it
re-reads the property after the mutation and before the resequencing loop.

## Alternatives considered

| Option | Pros | Cons | Why (not) chosen |
|---|---|---|---|
| Do nothing beyond S-127's cron fix | No new logic, no new invariant to reason about | S-127 alone cannot fix this class of bug — a re-fetch under the existing 4-tuple key produces a duplicate row, not a correction (see Context) | Rejected — doesn't actually close the reported gap |
| Match key `ClubName` only (drop `StartYear` too) | Simplest possible key | Genuinely conflates two different real stints at the same club in different years (the documented "loan, then a later permanent return" case from `PlayerCareerStint`'s own doc comment) — would silently merge distinct history | Rejected — too coarse, contradicts an existing documented invariant |
| Auto-resolve case 3 (non-null vs. non-null conflict) by always trusting the newest fetch | Fully automated, no stuck/unresolved rows | No way to distinguish "Wikidata corrected a mistake" from "these are actually two different stints that share a start year" from inside this logic — silently overwriting a real row on a wrong guess is worse than leaving a rarer, narrower gap open | Rejected — the null→value case (this ADR's actual fix) is unambiguous; the value→different-value case is not, and guessing wrong is a correctness regression, not an improvement |
| Wipe-and-replace: on any refetch, delete all of a player's existing stints and reinsert fresh from the response | Never leaves a stale row uncorrected, simplest mental model | Contradicts the additive-only discipline ADR-0059/ADR-0063 both build on (`SequenceOrder`'s cross-writer resequencing, `DuplicateCareerStintCleaner`'s canonicalization assumptions); also destroys any stint added by a *different* writer path that this particular fetch doesn't happen to re-observe (e.g. a rarely-triggered xG Grid byproduct stint would vanish on the next prefetch run's wipe, even though nothing about it was actually wrong) | Rejected — the narrower null→value update targets exactly the reported failure mode without touching data no writer path actually contradicts |
| `(ClubName, StartYear)` key; update-in-place only for the unambiguous null→value transition (chosen) | Fixes exactly the reported bug; conservative on the genuinely ambiguous case; reuses existing tracked-entity/`SaveChangesAsync` machinery, no new repository surface | Case 3 (non-null conflict) stays unresolved, silently logged rather than fixed | Best fit: minimal, targeted change that doesn't extend the additive-only invariant further than the evidence (Nwakali's exact shape: null→value) actually supports |

## Consequences

- Positive: closes the reported gap class — a player whose stint data is
  re-fetched (via S-127's now-recurring cron, a future xG Path refresh, or
  a fresh xG Grid lookup) will have a concluded stint correctly closed
  instead of duplicated, the next time any writer path observes the change.
- Positive: consolidates three previously-independent (and, in
  `WikidataLookupService`'s case, inline-duplicated) tuple-comparison
  implementations into one shared helper — reduces the risk of the three
  writers silently drifting apart the way ADR-0059's canonicalization fix
  once had to be applied inconsistently across writers.
- Negative / trade-off accepted: this only takes effect when a write path
  actually re-observes the player — it is not a background sweep. A player
  nothing ever re-fetches stays exactly as stale as before this ADR. S-127's
  cron is what supplies the "re-observes" trigger for the general case;
  this ADR only fixes what happens once that trigger fires.
- Negative / trade-off accepted: case 3 (non-null EndYear conflict) is
  deliberately left unresolved and only logged — a genuinely stale-but-
  wrong row in that narrower shape will not self-correct. No incident of
  this shape has been observed yet; revisit if one is.
- Negative / trade-off accepted: no optimistic-concurrency guard
  (`RowVersion` or similar) is added — two writer paths racing on the same
  player's rows in overlapping transactions is a pre-existing risk this ADR
  doesn't change (last `SaveChangesAsync()` wins, same as every other
  `PlayerCareerStint` write today). Not worth solving here; the writers run
  at weekly/on-demand cadence, not concurrently against the same player in
  practice.
- Follow-up: if case 3 (non-null conflict) is ever observed for real, decide
  then whether it needs a resolution strategy or a manual-review surface —
  premature to design one without a real example the way this ADR's own
  null→value case had one (Nwakali).
- Negative / trade-off accepted, addressed by case 2's "update every
  matching row" rule (architecture-review finding, 2026-08-16): without
  that rule, closing only the first row found among an outstanding,
  not-yet-`DuplicateCareerStintCleaner`-cleaned duplicate pair (ADR-0059/
  ADR-0063) would permanently diverge that pair's `EndYear` — one row
  closed, its sibling still `null` — and both `DuplicateCareerStintCleaner`
  Step 1 and Step 2 require exact `EndYear` equality to merge a pair, so the
  divergence would make that specific duplicate unrecoverable going
  forward, re-opening exactly the bug ADR-0059/ADR-0063 closed. Updating
  every row sharing the match key (not just one) keeps an outstanding
  duplicate pair mutually consistent, so the cleaner can still merge it on
  its next run regardless of write order. This does not eliminate the
  underlying race — a duplicate pair that exists at the moment of a
  reconciling write is still two rows until the cleaner next runs — it only
  ensures reconciliation doesn't make that pair permanently unmergeable in
  the meantime. `AddCareerStintsBatchAsync`'s per-player batch already
  produces the necessary duplicate scenario coverage for existing rows
  loaded in the same call; the shared reconciliation helper's tests should
  include a fixture with two pre-existing same-key rows (an uncleaned
  duplicate) to confirm both get closed identically, not just one.

## For AI agents

This ADR's case-2 update applies to every existing row sharing the match
key, not just one — see the Decision section and the "addressed by case 2's
'update every matching row' rule" Consequences entry above for why: it
exists specifically so this reconciliation logic can't reopen the duplicate
-unmergeability bug ADR-0059/ADR-0063 already closed. Do not simplify case 2
back down to "close the first/only matching row" without re-reading that
interaction.

`AddCareerStintsBatchAsync`'s existing-row loading query must stay a
**tracked** query (no `.AsNoTracking()`) — the update-in-place mechanism
this ADR describes depends on EF Core's change tracking picking up a
mutated `EndYear`/`AppearanceCount` on `SaveChangesAsync()`. Do not widen
the `(ClubName, StartYear)` match key further (e.g. to `ClubName` alone) or
auto-resolve the non-null-conflict case (3) without a fresh ADR — both were
explicitly considered and rejected above, not merely deferred. All three
writer paths (`WikidataLookupService.PersistCareerStintsAsync`,
`PlayerCareerStintRefreshService.RefreshCareerStintsAsync`,
`PlayerCareerPrefetchService`) must go through the same shared
reconciliation helper — do not let a future change reintroduce a
second, independently-drifting copy of this logic.
