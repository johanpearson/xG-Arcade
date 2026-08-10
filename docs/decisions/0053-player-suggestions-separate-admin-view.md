# ADR-0053: Player suggestions (REQ-215) get their own admin view, separate from REQ-503's unverified-data queue

- **Status:** Accepted
- **Date:** 2026-08-01
- **Related requirements:** REQ-215, REQ-509, REQ-510, REQ-501, REQ-502, REQ-503
- **Related components:** COMP-01 (Core.Users — submitting user reference),
  COMP-06 (Data.PlayerStore — `PlayerAttribute`/`PlayerOverride`, the only
  write target for a committed suggestion), COMP-10 (Data.PlayerNameIndex —
  explicitly out of scope for any write in this pipeline)

## Context

REQ-215/509/510 (drafted 2026-07-28) introduce a new pipeline: a
non-guest player can suggest that a specific player genuinely satisfies a
cell after an incorrect or timed-out guess (REQ-215); an admin can review
that suggestion against a fresh, admin-triggered Wikidata lookup and
commit or reject it (REQ-509); and an admin can separately run the same
fetch/commit flow with no suggestion involved at all (REQ-510).

REQ-503 already has an admin review queue (`GET
/admin/player-data/unverified`, backed by `PlayerData.Confidence =
"unverified"`). ADR-0029's original follow-up note anticipated that "when
a real user-suggestion channel exists, it should feed the same
`Confidence = "unverified"` review queue" — at the time, ADR-0029 still
kept the guess-time-fallback path unverified, so REQ-503's queue was a
real, populated backlog reviewers actually worked through. ADR-0032 later
reversed that: every Wikidata-sourced write, including the guess-time
fallback, now persists `verified` immediately. As a result, REQ-503's
queue is empty by construction today, with no code path writing
`unverified` at all (see REQ-503's own 2026-07-20 status note) — the
"third source" ADR-0029 anticipated feeding that queue is the only thing
that would ever populate it again.

REQ-509's own drafted status note left open whether REQ-215's pending
suggestions should be surfaced as a new row type inside REQ-503's
existing queue (fulfilling ADR-0029's original anticipation literally) or
as a wholly separate admin view/table, and recommended a new ADR resolve
it. This ADR is that resolution, made by the product owner 2026-08-01.

A `PlayerData` row (REQ-503's queue) is an auto-fetched Wikidata
sync/lookup result: no submitter, no claim to check a fresh fetch
against — the row *is* the fetch. A `PlayerSuggestion` (REQ-215) is a
human assertion: a player name (already known from the guess that
prompted it), the submitter's claimed club(s) and nationality, the
submitting user's id, and the originating cell/category types — reviewed
by fetching Wikidata fresh and checking it *against* that claim, not
displaying the claim as if it were the fetch result. These are
structurally different review actions (compare a claim to a fetch vs.
review a fetch on its own) with different required fields, not two
instances of the same row shape.

## Decision

Player suggestions (REQ-215) get their own, dedicated admin view —
`PlayerSuggestion` is a new entity/table (COMP-01-adjacent by virtue of
its `SubmittingUserId` reference, persisted in COMP-06's data project
alongside the other player-correction data), with its own admin-facing
list/review/commit/reject endpoints and its own frontend screen/section.
It is never folded into REQ-503's `PlayerData`/`Confidence = "unverified"`
queue, and never given a shared row shape or a merged UI with it.

REQ-510's standalone manual-search-and-add path (no suggestion involved)
commits through the identical write path REQ-509's suggestion-review
commit uses — it is not a third view; it is a variant entry point into
the same admin fetch/review/commit flow REQ-509 already defines,
reachable without a `PlayerSuggestion` row existing before, during, or
after it.

**ADR-0007's boundary, reconfirmed without exception:** ADR-0007
established that autocomplete/name-matching queries only `PlayerNameIndex`
(COMP-10) and correctness-checking a submitted guess queries only
`PlayerAttribute`/`PlayerOverride` (COMP-06) — the two paths must never
merge, since doing so would leak answer validity through autocomplete.
ADR-0007 predates this suggestion/commit pipeline and doesn't mention it
by name; this ADR makes explicit what was already implied: committing an
approved suggestion (REQ-509) or a manually-added player (REQ-510) may
only ever write `PlayerAttribute`/`PlayerOverride`. Neither commit path
may write `PlayerNameIndex`, under any circumstance, including as a
convenience to "also make the newly-confirmed name autocomplete-able
sooner" — that is not this pipeline's job, and doing so would silently
reopen the exact leak ADR-0007 exists to prevent.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Add `PlayerSuggestion` as a new row type inside REQ-503's existing `PlayerData`/`Confidence = "unverified"` queue, fulfilling ADR-0029's original anticipation literally | One admin screen instead of two; reuses REQ-503's existing list/approve/reject UI shell | `PlayerData` has no submitter field, no claimed-club(s)/nationality-to-check-against fields, and no "claim vs. fresh fetch" comparison shape — accommodating a suggestion would mean adding a set of nullable fields that only ever populate for suggestion-origin rows, or overloading `Confidence` to mean two different things ("this Wikidata fetch hasn't been spot-checked" vs. "this human claim hasn't been verified against a fetch") | The shared-table cost (nullable-field sprawl or an overloaded `Confidence` semantic) outweighs the UI-reuse benefit; the two inputs are different enough in shape and reviewer workflow to justify a second view |
| One shared generic "review queue" UI component, parameterized by origin, backed by two separate tables | Some UI code reuse without forcing one data shape | Real complexity for a UI layer that doesn't yet need it — REQ-503's queue is empty by construction (ADR-0032) and REQ-509/510's view has a genuinely different set of reviewer actions (compare-to-claim vs. review-a-fetch) | Premature abstraction for two views that don't actually share enough behavior yet; revisit only if a third similar queue appears |
| Keep the question open, decide it during REQ-509/510 implementation rather than via an ADR | No ADR overhead now | REQ-509's own status note already flagged this as exactly the kind of structural, hard-to-reverse choice (shared table vs. new one) an ADR exists to record before code is written | Deferring risks the choice being made implicitly by whichever implementer starts first, rather than deliberately |

## Consequences

- Positive: `PlayerSuggestion`'s schema can carry exactly the fields a
  human-submitted claim needs (submitter, claimed club(s)/nationality,
  originating cell) without distorting `PlayerData`'s existing,
  fetch-result-shaped schema.
- Positive: REQ-503's queue and REQ-509/510's suggestion view can evolve
  independently — e.g. REQ-503's queue could later gain a genuinely
  different "third source" (per ADR-0029's own remaining anticipation)
  without colliding with this suggestion pipeline's schema.
- Negative / trade-off accepted: two separate admin screens/sections
  instead of one, with some inevitable UI similarity (both are "list →
  inspect → commit/reject" flows) built twice rather than shared. Accepted
  because the underlying data and reviewer actions are different enough
  that forcing a shared shape was judged the worse cost (see Alternatives).
- Follow-up: if a third similar "reviewable, admin-actioned item" type
  ever appears, revisit whether a shared generic queue UI component (not a
  shared table) is worth extracting at that point — not before.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.

Specifically: do not add a `PlayerSuggestion`-origin row (or any suggestion
fields) into `PlayerData`/REQ-503's existing queue or its endpoints — build
`PlayerSuggestion` as its own entity/table with its own admin endpoints and
screen. Do not write to `PlayerNameIndex` from either REQ-509's
suggestion-commit path or REQ-510's manual-add commit path, under any
circumstance — both write only through the existing
`PlayerAttribute`/`PlayerOverride` mechanism REQ-501's manual-override path
already uses, per ADR-0007's boundary, reconfirmed here.
