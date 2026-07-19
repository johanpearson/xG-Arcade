# ADR-0028: Single-valued Wikidata properties (e.g. a player's photo) live on `Player`, not `PlayerAttribute`

- **Status:** Accepted
- **Date:** 2026-07-18
- **Related requirements:** REQ-214
- **Related components:** COMP-06

## Context

REQ-214 (photo reveal on a locked, correct cell) needed somewhere to cache
Wikidata's `P18` (image) property once it's fetched alongside the existing
`P27`/`P54` intersection queries in `WikidataClient`. COMP-06 already has
two player-shaped tables: `Player` (one row per person — `FullName`,
`WikidataQid`) and `PlayerAttribute` (one row per *distinct attribute* —
composite key `(PlayerId, AttributeType, AttributeValue)`, e.g. one row per
career club, one per nationality, one per trophy). The original brief for
this story assumed the new field would go on `PlayerAttribute`, matching
where every other Wikidata-sourced fact about a player already lives.

That assumption doesn't actually fit: `PlayerAttribute` has no row that a
single scalar like a photo URL could naturally belong to — it would have to
be duplicated onto every attribute row for that player (denormalized
n-ways for no reason) or bolted onto an arbitrary one of them (which row,
and what happens when that row is deleted/re-synced?). `Player` is already
the correct shape for exactly this kind of fact: one value, one row, keyed
by the person.

## Decision

A Wikidata property that is single-valued per player (at most one value
worth keeping, like a photo — as opposed to `P54`/`P27`-style properties
that are genuinely multi-valued across a career) is stored as a nullable
column directly on `Player`, set once when the player row is first created
(`WikidataLookupService.GetOrCreatePlayerAsync`) and never re-synced on a
later lookup — the same lifecycle `FullName` already has. It is not stored
as a `PlayerAttribute` row, and it does not go through `PlayerOverride`/
ADR-0015's per-attribute-type override-and-replace machinery.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Add a `PlayerAttribute` row (e.g. `AttributeType: "photo"`) | Reuses the existing multi-valued cache table; no schema change to `Player` | No natural single row to own a scalar; either duplicates the value across every attribute row for that player or arbitrarily privileges one; `AttributeType`/`AttributeValue` are typed as category-matching data (used by grid-generation candidate queries), not display metadata, so overloading them mixes two different concerns in one table | `PlayerAttribute`'s composite key genuinely has no "this row is the photo" answer — it would need an ad-hoc convention invented on the spot |
| Store the photo on `Player` (chosen) | One row, one owner, matches `FullName`'s existing single-value/set-once lifecycle exactly; no new table; stays entirely inside COMP-06 | Bypasses `PlayerOverride`'s correction path — there is currently no admin-facing way to fix a wrong or missing photo the way `PlayerOverride` can fix a wrong club/nationality | Accepted deliberately (see Consequences) — REQ-214 is explicitly display-only and carries no correctness stake, so the lack of an override path is a real but low-cost gap, not a blocker |
| A new dedicated table (e.g. `PlayerPhoto`) | Most "correct" normalization if more single-valued display fields are expected later | Pure overhead for exactly one nullable column today; no second single-valued field exists yet to justify the abstraction | Speculative — no second use case exists to design against right now |

## Consequences

- Positive: no schema/table proliferation for a one-column need; the field
  follows the same, already-understood lifecycle as `FullName`
  (set-once-at-creation, never re-synced); stays entirely inside COMP-06,
  so ADR-0007's autocomplete/correctness boundary is untouched.
- Negative / trade-offs accepted: a wrong or stale `Player.PhotoUrl` has
  **no admin-correction path** today — `PlayerOverride` only covers
  `AttributeType`/`AttributeValue` pairs on `PlayerAttribute`, not columns
  on `Player`. This is acceptable specifically because REQ-214's photo is
  explicitly non-correctness, display-only data (a wrong photo doesn't
  affect scoring or a cell's validity) — it would not be acceptable for a
  single-valued field that *did* carry correctness weight.
- Follow-up: if a second single-valued Wikidata property is ever needed
  (something beyond a photo), default to the same pattern (a column on
  `Player`) rather than re-litigating this per field — but if an
  admin-correction path for `Player`-level fields becomes necessary (e.g.
  photos turn out to need moderation), that's a new decision, not an
  extension of this one.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
