# ADR-0089: Grid headers pick their category type independently, not once per instance

- **Status:** Accepted
- **Date:** 2026-08-29
- **Related requirements:** REQ-101, REQ-102, REQ-107, REQ-108
- **Related components:** COMP-05 (Games.XGGrid)

## Context

`GridGenerationService.SelectPairing` picks **one** pairing type — Country×Club,
Club×Club, Country×Trophy, Club×Trophy, or Trophy×Trophy — for an entire grid
instance, uniformly at random among whichever of those five the seeded
reference-data pool sizes can support. Every row header then shares one
category type and every column header shares the other; row headers are
picked randomly and fixed for good, never retried, while `PickHeadersAsync`
retries only the column side, accepting a column candidate only once it
clears `MinValidAnswers` against **every** already-fixed row simultaneously.

This causes real, recurring production failures. `docs/backlog.md` S-036
already diagnosed this exact failure mode once (2026-07-13 incident,
`GridGenerationException: "Ran out of candidates before completing the
grid."`) and mitigated it by widening the reference pool (15→21 clubs,
20→45 countries) and adding proactive cache warming — but the structural
fragility itself was never addressed: an unlucky set of 3 randomly-chosen
row headers of one homogeneous type (e.g. three small-market countries with
genuinely sparse shared-player data) can still exhaust the entire column
candidate pool, even though plenty of valid grids exist using a different
mix of category types for those same headers. The user reported this is
still happening ("this tends to happen.. and it can't happen").

REQ-107's actual text never required per-instance pairing homogeneity — it
only bans a Country×Country **cell**. The "one pairing for the whole
instance" design was `SelectPairing`'s own implementation choice (S-030/
S-031), not something REQ-107 mandates. The code's own comment in
`PickHeadersAsync` already anticipated this: "a hypothetical future grid
whose row/column category types vary within one call would need to check
this per candidate instead."

An `architecture-reviewer` pre-check confirmed no component-boundary
violation, confirmed an ADR is warranted (reverting this decision later
would require understanding a genuine trade-off, not just an
implementation detail), and flagged three specific things this ADR needs
to settle explicitly: the per-header type-selection distribution, how
REQ-102's row/column value-collision check generalizes once axes are no
longer homogeneously typed, and what replaces the removed upfront
"not enough reference data" feasibility check.

Explicitly out of scope for this decision: `MinValidAnswers` stays at its
current value of 5. It was deliberately raised from 3 in S-014 after live
playtesting found 3-answer cells "too thin" — trading that quality bar away
for generation reliability was considered and rejected this session in
favor of fixing the actual structural bug instead.

## Decision

Each row and column header now gets its own independently-chosen category
type (Country, Club, or Trophy) instead of the whole instance sharing one
pairing. `CategoryPairingRules.IsAllowedPairing`'s Country×Country ban is
checked per individual cell (one specific row header's type against one
specific column candidate's type), inside `PickHeadersAsync`'s existing
per-row loop, in the same position REQ-107 already requires (before the
match-count query, not folded into the `MinValidAnswers` retry) — replacing
the single check that used to run once outside the loop against a
globally-fixed pairing.

Concretely:

1. `CategoryCandidate` gains a `CategoryType` field (`Country`/`Club`/
   `Trophy`), the same precedent as its existing `UsesCountryForSportProperty`/
   `IsTeamTrophy` fields.
2. Row headers are drawn from one combined pool — every seeded Country,
   Club, and Trophy candidate concatenated together, each tagged with its
   own `CategoryType` — shuffled and taken, rather than `PoolFor`'s
   single-type pool selected by `SelectPairing`. Column candidates are
   drawn from the same combined-pool shape. **Selection is a uniform draw
   over the concatenated pool**, not a uniform choice among the 3 types
   first — this makes a header's odds of being a given type naturally
   proportional to how much reference data that type actually has (today:
   45 countries, 21 clubs, 3 trophies), rather than an artificial
   even-across-types split that would make Trophy headers wildly
   over-represented relative to how well-supported they are.
3. REQ-102's "no row category may be identical to a column category" is
   now a per-`(CategoryType, Name)` equality check across all headers,
   replacing the old axis-level "only filter by name if both axes share
   one type" branch — a Club-typed row header and a Club-typed column
   header must never collide even though the two axes are no longer
   uniformly typed; candidates of a different `CategoryType` are never
   compared against each other, same as before.
4. `SelectPairing`'s all-or-nothing "none of the 5 fixed combinations is
   feasible" upfront throw is removed. Its replacement is a simple
   combined-pool-size check per axis (`>= GridSize` candidates available in
   total across all three types) before picking begins — a genuine
   empty/near-empty reference-data database is the only realistic way to
   trip it. `GridGenerationOptions.MaxAttempts`/`MaxDuration` (ADR-0023)
   remain the real backstop for the picking loop itself, unaffected by this
   change.
5. `BuildCells`/`CreateCell` pass each header's own `CategoryType` instead
   of one constant type per axis — no `GridCell` schema change, since it
   already stores `RowCategoryType`/`ColCategoryType` per cell, not per
   instance.

`MinValidAnswers` is unchanged (stays 5).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Keep per-instance pairing, add more pairing types or more retry attempts on the row side | Smaller diff | Doesn't fix the root fragility — still an all-rows-homogeneous, all-or-nothing model; a bad row-header set is still fatal regardless of how many pairing *types* exist to choose the axis-wide type from | Not chosen: treats the symptom (too few pairing shapes), not the actual cause (rows are fixed once, never reconsidered, and forced into one shared type) |
| Lower `MinValidAnswers` (e.g. to 3) instead of changing the selection algorithm | Simple one-line change, directly reduces rejection rate | Reverts S-014's deliberate, playtested quality bar ("3-answer cells felt too thin") — trades game quality for reliability rather than fixing the structural bug | Rejected by the user this session; the structural fix addresses the actual failure without touching gameplay quality |
| Uniform choice among the 3 category types per header, then uniform within that type's pool | Guarantees even type variety regardless of pool-size imbalance | Would make Trophy headers (3 seeded values) appear as often as Country headers (45 seeded values) per header slot, worsening `MinValidAnswers` rejection odds for Trophy-heavy cells rather than improving overall reliability — fights the actual reference-data distribution instead of working with it | Not chosen: reference-data-proportional selection (drawing from one concatenated pool) naturally favors the types most likely to have enough real matching players, which is the whole point of this change |
| Retry/reselect row headers too, not just columns, on a stuck generation | Most thorough fix — removes the "rows are permanently fixed" fragility entirely | Materially larger change to `PickHeadersAsync`'s control flow (would need to backtrack and re-pick rows, not just discard-and-retry one column candidate at a time); per-header type mixing already removes most of the practical failure cases this session is trying to fix | Not chosen for this pass — flagged as a follow-up if per-header mixing alone doesn't sufficiently reduce failures in practice |

## Consequences

- Positive: removes the single point of failure where one unlucky,
  homogeneously-typed row-header set could exhaust the entire column pool
  and abort generation — a grid can now freely mix Country/Club/Trophy
  headers on both axes, so a data-sparse header of one type no longer dooms
  the whole attempt when a different type would have worked just as well.
  Directly addresses the recurring "Ran out of candidates" failure without
  touching `MinValidAnswers`'s playtested value.
- Positive: selection probability by type is self-adjusting as reference
  data grows (e.g. when the trophy pool widens past 3, Trophy headers
  naturally appear more often, with no code change needed) — the removed
  `SelectPairing` feasibility table (`clubCount >= size * 2`, etc.) needed
  hand-updating every time a pool grew; the combined-pool approach doesn't.
- Negative / trade-offs accepted: a single grid can now look more visually
  "mixed" (e.g. two country rows and one club row) rather than the
  cleaner, more thematically consistent single-pairing-type grids players
  have seen so far — judged acceptable, since REQ-107 never required
  thematic consistency, only the Country×Country ban.
- Negative / trade-offs accepted: still does not retry row headers once
  fixed — an adversarial or extremely sparse reference-data state could
  still exhaust the column pool in principle, just far less often now that
  headers aren't forced into one homogeneous type. `MaxAttempts`/
  `MaxDuration` (ADR-0023) remain the clean abort path if it does.
- Follow-up: revisit row-header retry (see rejected alternative above) if
  "Ran out of candidates" failures still recur after this change ships.
- Follow-up: REQ-107's status note and ADR-0061's feasibility-math
  references to `SelectPairing` need updating to describe the new
  per-header selection mechanism (doc-sync, same change).

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change. In
particular: do not reintroduce a single axis-wide `CategoryType` string for
all row (or all column) headers — each header carries its own type on
`CategoryCandidate`, and the REQ-107 pairing check must stay per-cell, not
hoisted back out to a once-per-call check. Do not change `MinValidAnswers`
as part of implementing this decision — that was a separate, explicitly
rejected option, not an oversight to "helpfully" fix while in this code.
