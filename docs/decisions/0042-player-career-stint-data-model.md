# ADR-0042: New PlayerCareerStint entity for ordered, dated career data

- **Status:** Accepted
- **Date:** 2026-07-26
- **Related requirements:** REQ-1201-REQ-1206 (xG Path)
- **Related components:** COMP-06 (Data.PlayerStore), COMP-11 (Games.XGPath)

## Context

`PlayerAttribute` (COMP-06) is deliberately a flat membership table — one
row per `(PlayerId, AttributeType, AttributeValue)`, e.g. one row per
career club, with no fields for order, dates, or appearance counts. That
shape is correct for what it's for: xG Grid's correctness check only ever
needs "did this player ever have attribute X," never "when," never "how
many times," and `PlayerAttribute`'s denormalized flatness is exactly what
makes REQ-101's candidate-matching query fast (see `PlayerAttribute`'s own
doc comment).

xG Path's clue-reveal mechanic (REQ-1201-REQ-1206) needs something
`PlayerAttribute` structurally cannot represent: the player's career
*ordered* by time (earliest club first), *each stint's* date range (the
bundled "years" clue), and *each stint's* appearance count (bundled into
that club's own clue). Wikidata already carries all of this — `P54`
("member of sports team") statements carry `P580`/`P582`
(start/end time) and, inconsistently, `P1350` (number of matches played)
as qualifiers on the *specific statement*, not as separate top-level facts
— but nothing in this codebase persists statement-level qualifiers today;
`WikidataLookupService` only ever extracts the flat "this player has this
attribute" fact `PlayerAttribute` expects.

Widening `PlayerAttribute` itself to carry order/dates/counts would give
every row of every attribute type (club, nationality, trophy) nullable
fields that only ever apply to "club," and would slow down or complicate
the exact fast-path query REQ-101 depends on. That's the wrong place for
this data.

## Decision

Add a new entity, `PlayerCareerStint` (COMP-06, alongside
`PlayerAttribute`/`PlayerAlias`/`PlayerOverride` in `XGArcade.Data`):
`PlayerId`, `ClubName`, `StartYear`, `EndYear` (nullable — an ongoing
stint), `SequenceOrder` (int, chronological position, resolved at write
time so no reader needs to re-sort by date), and `AppearanceCount`
(nullable int — null when Wikidata's `P1350` qualifier isn't present for
that stint, which REQ-1201-REQ-1206 already treats as an expected,
ungated gap, not an error). Populated by extending
`WikidataLookupService`'s existing `P54` query to also read the
`P580`/`P582`/`P1350` qualifiers already present in the statement it's
already fetching — no new SPARQL query shape, no new external call, just
capturing fields the existing query result already contains and currently
discards.

`PlayerAttribute`'s `"club"` rows are unaffected and keep being written
exactly as before — `PlayerCareerStint` is populated alongside it, not
instead of it, from the same underlying Wikidata response. xG Grid's
correctness path continues to read only `PlayerAttribute`/`PlayerOverride`
and must never be changed to read `PlayerCareerStint`; xG Path's puzzle
generation reads only `PlayerCareerStint` and must never read
`PlayerAttribute` for club data — this mirrors the same "never merge these
two paths" boundary rule ADR-0007 already established between
`PlayerNameIndex` and `PlayerAttribute`, applied to a third, structurally
different table for a third reason (order/dates/counts vs. membership,
not autocomplete-vs-correctness).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Add nullable `StartYear`/`EndYear`/`SequenceOrder`/`AppearanceCount` columns directly to `PlayerAttribute` | No new table | Every nationality/trophy row carries four always-null columns; REQ-101's fast membership query gains dead weight; conflates "is this true" with "when/how many," a genuinely different question | Wrong shape for a table whose whole value is flat, fast membership checks |
| Parse `P54` qualifiers on demand at puzzle-generation time, cache nothing | No new persisted data | Re-fetches/re-parses Wikidata on every puzzle generation for the same player; loses the "cache, don't refetch" principle every other correctness-side lookup in this codebase already follows (COMP-07, `PlayerAttribute`'s cache-first design) | Inconsistent with how every other piece of Wikidata-sourced data in this system is handled |
| New `PlayerCareerStint` entity (chosen) | Matches the data's actual shape (ordered, dated, countable); reuses the existing `P54` fetch, no new external call; keeps `PlayerAttribute` untouched and fast; mirrors the established "separate table for a separate access pattern" precedent (ADR-0007's `PlayerNameIndex`) | One more table, one more thing `WikidataLookupService` populates per player | Best fit: the data genuinely has a different shape and a different consumer than `PlayerAttribute`'s |

## Consequences

- Positive: xG Path's clue mechanic has exactly the data it needs, sourced
  from a query this codebase already runs, no new API surface against
  Wikidata; `PlayerAttribute`'s existing fast-path is untouched
- Negative / trade-offs accepted: two tables (`PlayerAttribute`,
  `PlayerCareerStint`) now both derive from the same `P54` statements —
  a future data-correction (e.g. REQ-501 override, or a QID fix per
  REQ-111) that should logically affect both must be applied to both
  explicitly; there is no automatic propagation between them, and this ADR
  does not build one
- Negative / trade-offs accepted: `AppearanceCount` will be `null` for a
  real, non-trivial fraction of stints (Wikidata's `P1350` coverage is
  inconsistent) — REQ-1201-REQ-1206 already specifies this must render as
  "count unknown," never a placeholder like `0`, which would misleadingly
  imply zero appearances
- Follow-up: if a manual override (REQ-501/`PlayerOverride`) is ever needed
  for career-stint data specifically (wrong dates, wrong appearance count),
  `PlayerOverride`'s existing "replaces the entire attribute type" semantics
  (ADR-0015) do not obviously extend to a multi-row, ordered table — that
  needs its own design pass if/when it's actually requested, not assumed to
  work by analogy

## For AI agents

Do not add `StartYear`/`EndYear`/`SequenceOrder`/`AppearanceCount` fields to
`PlayerAttribute`, and do not have xG Grid's correctness-checking path
(`HasEffectiveAttributeAsync` or anything upstream of it) read from
`PlayerCareerStint`. If a task seems to need club dates/order/counts inside
xG Grid's own logic, stop and flag it — that's a sign the task is
misunderstood, not a sign these tables should merge.
