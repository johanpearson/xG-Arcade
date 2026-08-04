# ADR-0058: Canonicalize PlayerCareerStint.ClubName by Wikidata QID, not by label

- **Status:** Accepted
- **Date:** 2026-08-04
- **Related requirements:** REQ-1203
- **Related components:** COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients), COMP-11 (Games.XGPath)

## Context

xG Path could show the same real career stint as two separate club-reveal
nodes. The 2026-08-03 REQ-1203 fix (`WikidataClient.NormalizeClubName`)
only strips a small, hand-picked set of legal-suffix tokens
(`FC`/`F.C.`/`AFC`/`A.F.C.`) from a raw Wikidata `?clubLabel`, which
collapses a variant like "Liverpool" vs. "Liverpool F.C." but does nothing
for a genuine alternate-name variant — e.g. "Lyon" (this codebase's
hand-seeded `ClubDefinition.Name`) vs. "Olympique Lyonnais" (a real,
equally valid Wikidata label for the same club, same QID `Q704`).

Two independent writers persist `PlayerCareerStint.ClubName` with different
conventions:

1. `WikidataLookupService.PersistCareerStintsAsync` (xG Grid's country x
   club intersection-lookup byproduct, ADR-0042) writes `ClubName =
   club.Name` — the canonical, hand-seeded `ClubDefinition.Name` it already
   has on hand, since it's scoped to one caller-supplied club per call.
2. `PlayerCareerStintRefreshService`/`PlayerCareerPrefetchService`
   (ADR-0054/ADR-0055, the full-career-fetch jobs that populate the
   majority of the ~608K-row `PlayerCareerStint` table) write Wikidata's
   own raw `?clubLabel`, run only through `NormalizeClubName`'s suffix
   stripper — because `BuildPlayerCareerStintsByQidsQuery` never selected
   the underlying `?club` QID, there was no way for this path to recognize
   "this label and that seeded name are the same real club."

Both writers dedup new stints against what's already stored, keyed on the
bare string tuple `(ClubName, StartYear, EndYear, AppearanceCount)` — never
cross-checked against `ClubDefinition`. So the same real stint can end up
as two `PlayerCareerStint` rows with two different `ClubName` strings,
surfacing as two xG Path club-reveal nodes for what a player experiences as
one real transfer.

A second, related bug shares this root cause:
`GetCareerStintCandidatePlayerIdsAsync` (xG Path's per-round target
eligibility hot path) does an exact, case-sensitive match against seeded
`ClubDefinition.Name`. A stint persisted under a non-canonical Wikidata
label never counts toward eligibility, even when the player genuinely
played for a seeded club — a correctness gap in candidate selection, not
just a display bug.

`ClubGapAuditService`/`GetUnseededClubCandidatesAsync` already exists
specifically because "a Wikidata-sourced label and a hand-seeded
`ClubDefinition.Name` come from two different paths and could plausibly
differ" (its own doc comment) — this is that exact mismatch, now shown to
also produce duplicate/missed rows, not just audit noise.

## Decision

Make `ClubDefinition` the single source of truth for club naming across
both writers, canonicalizing by **Wikidata QID**, not by string label:

- `BuildPlayerCareerStintsByQidsQuery`'s `SELECT` now projects `?club`
  (already bound in the query body via `?clubStatement ps:P54 ?club`, just
  not previously selected).
- `WikidataCareerStintEntry` gains a `ClubQid` field (trailing optional
  parameter, default `null` — purely additive, every existing
  caller/test untouched).
- `PlayerCareerStintRefreshService`/`PlayerCareerPrefetchService` (the
  layer that already talks to `IPlayerStoreRepository`/
  `ICategoryValueRepository`, not `WikidataClient` itself — `WikidataClient`
  stays free of a `ClubDefinition` dependency, per COMP-07's existing
  boundary) resolve each fetched stint's `ClubName` to the matching
  `ClubDefinition.Name` when `ClubQid` matches a seeded club's
  `WikidataQid`, falling back to the existing suffix-normalized label when
  it doesn't (a genuinely unseeded club — still useful for xG Path's own
  display and for `ClubGapAuditService`'s gap detection, which this change
  does not touch).
- `WikidataLookupService.PersistCareerStintsAsync` needs no change — it
  was already canonical (`club.Name` from `ClubDefinition`, not a Wikidata
  label at all). Verified by a new cross-writer test that both paths now
  converge on the identical `ClubName` for the same real stint.
- `GetCareerStintCandidatePlayerIdsAsync`'s exact-match eligibility check
  is fixed for free — a canonicalized `ClubName` now satisfies it whenever
  the underlying stint really is at a seeded club.

**Backfill:** a narrow, provable-only cleanup (`DuplicateCareerStintCleaner`,
`dotnet run -- clean-duplicate-career-stints`), not a full purge-and-reseed
of the ~608K-row table. See Alternatives considered for why a blind purge
(the `StaleClubAttributeCleaner` precedent) was rejected here. The cleaner
removes a `PlayerCareerStint` row only when another row for the exact same
`(PlayerId, StartYear, EndYear, AppearanceCount)` tuple already exists whose
`ClubName` **is** a seeded `ClubDefinition.Name` — i.e., only when the
canonical row for that exact real stint is already present, so the
non-canonical row is provably redundant without needing to re-derive which
QID it came from. A row at a genuinely unseeded club, or one with no
matching canonical counterpart, is never touched. Idempotent, safe to
re-run, run manually via `workflow_dispatch` (same friction level as
`clean-stale-club-attributes`/`clear-pair-lookup-failures`) — not wired
into `migrate-and-seed`.

## Alternatives considered

| Option | Pros | Cons | Why (not) chosen |
|---|---|---|---|
| QID-based canonicalization at write time (chosen) | Fixes the root cause going forward for both writers; also fixes `GetCareerStintCandidatePlayerIdsAsync`'s eligibility gap for free; no new external data source | `WikidataCareerStintEntry`/the SPARQL `SELECT` shape both change; needs a canonicalization lookup layer | Best fit — the QID is exactly the stable identity `ClubDefinition.WikidataQid` already exists to canonicalize against |
| Widen `NormalizeClubName` into a general fuzzy/alias matcher | No query-shape change | Explicitly rejected by the 2026-08-03 fix's own reasoning: a generic matcher risks merging two genuinely different clubs that happen to share a name fragment — a correctness risk, not just a display one | Rejected — repeats a mistake this codebase already deliberately avoided once |
| Hand-maintain a label-alias table per seeded club (`"Olympique Lyonnais" -> "Lyon"`) | No SPARQL change | A second, drifting source of truth alongside `ClubDefinition`; every seeded club needs every Wikidata label variant hand-discovered and kept in sync; doesn't fix `GetCareerStintCandidatePlayerIdsAsync`'s eligibility gap unless duplicated there too | Rejected — QID canonicalization is strictly more reliable and is already the mechanism `ClubDefinition.WikidataQid` exists for |
| Full purge-and-reseed of `PlayerCareerStint` (`StaleClubAttributeCleaner`-style) for the backfill | Guaranteed-correct end state; simplest cleanup logic | No QID stored on existing rows to re-canonicalize against, so this would require a full live re-run of `prefetch-player-careers` against every seeded country's pool (hours-long, WDQS-bound); temporarily collapses `GetCareerStintCandidatePlayerIdsAsync`'s candidate pool to whatever ADR-0054's per-round byproduct writes have accumulated since the purge — a real xG Path availability regression, disproportionate for what is presently a cosmetic-only bug (xG Grid never reads this table; scoring is unaffected) | Rejected as disproportionate — narrow provable-duplicate cleanup fixes the same observable symptom at far lower cost/risk |
| Defer backfill entirely, ship the write-time fix only | Zero extra work | Already-materialized duplicate pairs for previously-double-written players stay duplicated indefinitely, with no code path that will ever revisit them | Rejected — the targeted cleanup is cheap enough (no Wikidata calls, bounded to provable duplicates) that deferring it has no real offsetting benefit |

## Consequences

- Positive: xG Path's "every documented club stint revealed, none ever
  omitted/duplicated" acceptance criterion (REQ-1203) now holds across
  both writer paths, not just within a single full-career-fetch response
- Positive: `GetCareerStintCandidatePlayerIdsAsync`'s eligibility check
  now recognizes a stint at a seeded club regardless of which writer
  persisted it — a correctness fix, not just cosmetic
- Positive: the backfill cleanup needs no live Wikidata access and is safe
  to re-run, matching this codebase's existing one-off-maintenance-tool
  pattern (`StaleClubAttributeCleaner`/`PairLookupFailureCleaner`)
- Negative / trade-off accepted: the backfill is deliberately incomplete —
  a non-canonical row with **no** matching canonical counterpart already
  persisted (e.g. a player only ever touched by the full-career-fetch
  path, never by xG Grid's byproduct path for that same club) is left
  under its best-effort label, not corrected to the seeded name, until
  that player is naturally re-touched by a future xG Path target selection
  (`PlayerCareerStintRefreshService.RefreshCareerStintsAsync`, which now
  canonicalizes on every future write) or a future `prefetch-player-careers`
  re-run
- Negative / trade-off accepted: `WikidataCareerStintEntry`'s `HashSet`-based
  dedup inside `WikidataClient.ParseCareerStintBindings` now includes
  `ClubQid` in record equality — two rows with an identical normalized
  label and dates but genuinely different underlying `?club` QIDs no
  longer silently collapse into one entry the way they would have before
  this change. Judged correct (they are, in fact, two different Wikidata
  items), not flagged as a regression
- Follow-up: none planned — this is judged fully proportionate as shipped;
  revisit only if a further duplicate-node report surfaces a shape this
  fix's tuple-based matching still misses (same "own test, not a silent
  loosening" discipline the 2026-08-03 fix's own AppearanceCount
  limitation already established)

## For AI agents

`WikidataClient` must stay free of any `ClubDefinition`/`ICategoryValueRepository`
dependency — canonicalization lookups belong in the `PlayerCareerStintRefreshService`/
`PlayerCareerPrefetchService` layer, per this class's own layering convention
within COMP-07 (an internal convention, not one of `architecture-document.md`'s
numbered cross-component boundary rules). If a
future change needs club canonicalization somewhere else (e.g. a new
writer path), reuse `PlayerCareerStintRefreshService.BuildClubNameByClubQidAsync`/
`BuildNewStintsByPlayerId` rather than re-deriving the lookup inline.
`DuplicateCareerStintCleaner`'s provable-only matching (exact tuple match
against an already-canonical row) must not be widened into a fuzzy/alias
match without a fresh ADR — that would repeat exactly the correctness risk
`NormalizeClubName`'s own doc comment already rejected once.
