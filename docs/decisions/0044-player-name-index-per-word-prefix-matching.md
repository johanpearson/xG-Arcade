# ADR-0044: Per-word decomposition, not pg_trgm, for PlayerNameIndex surname-prefix matching

- **Status:** Accepted
- **Date:** 2026-07-26
- **Related requirements:** REQ-207, REQ-208
- **Related components:** COMP-10 (Data.PlayerNameIndex)

## Context

REQ-208's 2026-07-26 correction identified a gap in
`PlayerNameIndexRepository.SearchByPrefixAsync` (COMP-10, autocomplete's only
read path per ADR-0007): it matched a query only as a prefix of a player's
*entire* normalized name (e.g. `"zlatan ibrahimovic"`), so a surname-only
query (e.g. "ibrahimovic") never matched. The fix needs to also match the
query as a prefix of any individual word within the normalized name, while
keeping the existing whole-name-prefix behavior working.

`PlayerNameIndex` (COMP-10) is bulk-imported from Wikidata and can hold a
large number of rows (a full birth-year-sliced import across many years of
professional footballers). It backs `/players/autocomplete`, queried on every
keystroke past `MinQueryLength = 2`. Whatever query shape answers the new
per-word condition has to stay index-backed at that scale — a naive
`Contains()` or leading-wildcard `LIKE '%query%'` against the existing
`NormalizedName` column can't use a standard B-tree index and would become a
sequential scan as the table grows.

## Decision

Add a new child table, `PlayerNameIndexWord` (`PlayerId`, `Word`), one row
per space-separated word in `PlayerNameIndex.NormalizedName`, with a plain
`HasIndex(Word)` — the same indexing style already used for
`NormalizedName` itself. `SearchByPrefixAsync` now identifies candidate
`PlayerId`s via two independently index-backed `StartsWith` scans (one
against `NormalizedName`, one against `PlayerNameIndexWord.Word`), unions the
scalar ids, and fetches the matching rows in a single primary-key lookup.
`PlayerNameIndexRepository.UpsertManyAsync` reconciles each player's word
rows in place on every upsert (add new words, remove stale ones), the same
"correct in place, don't blindly re-insert" discipline the rest of that
method already follows.

This keeps every query in the hot path a genuine, index-friendly prefix
comparison — never a leading-wildcard or substring match — at the cost of
one extra child table populated by the same bulk import.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| `pg_trgm` GIN index + trigram-accelerated `LIKE`/`ILIKE`/similarity on `NormalizedName` directly | No schema change to the row shape; one query, one table | Needs a new Postgres extension (`CREATE EXTENSION pg_trgm`) with no existing precedent in this codebase; trigram indexes are built for arbitrary substring/similarity search, which is more machinery than a genuine prefix match needs; still requires care to keep the query shape one a GIN index can actually accelerate (naive `ILIKE '%x%'` isn't automatically fast without matching operator classes) | The per-word table answers the *actual* requirement (prefix of a word, not arbitrary substring) with the same B-tree indexing approach already shipped and working for `NormalizedName`, no new extension or operator class to introduce and verify |
| Naive `Contains()` / leading-wildcard `LIKE '%query%'` against `NormalizedName` | Simplest possible code change | Cannot use a standard B-tree index; becomes a sequential scan at scale, on the exact endpoint queried every keystroke | Explicitly ruled out by the requirement itself |
| Client-side (application-layer) word matching: load broader candidate set, filter in C# | No schema/migration needed | Either fetches far too many rows to filter cheaply, or requires the same "match a prefix without an index" problem just moved into the app layer | Doesn't solve the underlying indexing problem, just relocates it |

## Consequences

- Positive: both directions (whole-name-prefix and per-word-prefix) stay
  genuine, index-backed `StartsWith` queries; no new Postgres extension to
  provision/verify; reuses this codebase's existing indexing idiom rather
  than introducing a new one.
- Negative / trade-offs accepted: one more child table to keep in sync on
  every `UpsertManyAsync` call (bounded, small, reconciled in place); autocomplete's
  `SearchByPrefixAsync` is now two database round trips (candidate id
  union, then a keyed fetch) instead of one, chosen deliberately over
  unioning the two `IQueryable<PlayerNameIndex>` branches directly, since an
  entity-level `Union`'s deduplication semantics differ between a real
  relational provider (translates to a genuine SQL `UNION`, which dedupes by
  column value) and the InMemory test provider (no such guarantee for
  `AsNoTracking`-materialized entities) — unioning on the scalar `PlayerId`
  instead removes that ambiguity entirely.
- Follow-up: if profiling ever shows the per-word table meaningfully
  increasing bulk-import time, reconsider batching the word reconciliation;
  not expected to matter at today's scale.

## For AI agents

`PlayerNameIndexWord` is COMP-10's own internal decomposition of
`PlayerNameIndex.NormalizedName` — never read directly by anything on the
correctness-checking side (COMP-06, ADR-0007), and never given a foreign key
into `Player`'s id space, same rule `PlayerNameIndex` itself follows. If a
future change needs fuzzy/substring matching (not just prefix) for
autocomplete, that's a different requirement and likely does need
`pg_trgm` — don't retrofit it onto this table without a new ADR.
