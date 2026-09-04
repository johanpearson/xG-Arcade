# ADR-0105: xG Connect's chain-step overlap check always refreshes both players

- **Status:** Accepted
- **Date:** 2026-09-04
- **Related requirements:** REQ-1404, REQ-1406
- **Related components:** COMP-17 (Games.XGConnect)

## Context

`PlayerCareerOverlapService.LoadBothPlayersStintsAsync` (S-211) decided
whether to trigger a live Wikidata refresh for a player using a single
heuristic: does that player already have **any** cached `PlayerCareerStint`
row? If so, their data was treated as trustworthy and complete, and no
refresh ever ran for them again.

That heuristic is wrong, because `PlayerCareerStint` is a shared table other
features can — and routinely do — write narrow, single-club byproduct rows
into. `WikidataLookupService.PersistCareerStintsAsync` (xG Grid's own
guess-checking path) persists career-stint qualifiers only for the ONE club
a grid cell happened to query, whenever that guess is checked. "Has any
row" was never equivalent to "has a full career fetched."

A real, reported incident (2026-09-04) confirmed this directly: playing xG
Connect's chain-builder (built the same day this ADR's related design
change, ADR-0104, shipped), a player built Eden Hazard → César
Azpilicueta → Reece James, matching correctly at Chelsea each step. The
NEXT step — Reece James → Jonas Olsson, connecting via Reece James's real,
confirmed 2019 loan spell at Wigan Athletic (Jonas Olsson played 6 games
for Wigan that same season) — was wrongly rejected. Root cause: James
already had a Chelsea-only `PlayerCareerStint` row from the earlier,
successful Azpilicueta step, so `LoadBothPlayersStintsAsync`'s "any rows
already exist" check skipped refreshing him again — his Wigan Athletic loan
had simply never been fetched, and the existing narrow cache permanently
hid it.

This is the exact same bug shape ADR-0054 already found and fixed once
before, in a different game module: a live xG Path puzzle for Timothy Weah
was missing real, documented Juventus and Marseille stints, for the
identical structural reason ("a player's `PlayerCareerStint` set is never
more complete than whatever clubs xG Grid happened to ask about so far").
ADR-0054's fix was for `XGPathGameModule.GenerateInstanceAsync` to call
`IPlayerCareerStintRefreshService.RefreshCareerStintsAsync` **unconditionally**
for every selected target, never gated on whether they already had some
rows. `PlayerCareerOverlapService` (built after ADR-0054, in the same S-211
story that gave it its own `IPlayerCareerStintRefreshService` dependency)
did not follow that precedent — it added its own, narrower "only if zero
rows" gate on top of the same shared service, reintroducing the bug
ADR-0054 already closed elsewhere.

## Decision

`PlayerCareerOverlapService.LoadBothPlayersStintsAsync` now calls
`RefreshCareerStintsAsync([playerAId, playerBId], throwOnFailure: true, ...)`
unconditionally, every time either `HaveSharedClubOverlapAsync` or
`GetSharedClubOverlapsAsync` is called — exactly matching
`XGPathGameModule.GenerateInstanceAsync`'s own unconditional call to the
same shared service. The "only refresh players lacking cached rows" branch
and its `HasStints` helper are removed entirely.

This is safe and cheap to call on every invocation:
`RefreshCareerStintsAsync`'s own reconciliation (`XGArcade.DataSync`,
`BuildNewStintsByPlayerId`) already dedupes the freshly-fetched career
against whatever rows exist and persists only genuinely new stints — a
player whose data really is already complete costs one Wikidata round trip
that adds nothing, not a full re-derivation or a duplicate-row risk.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Always refresh unconditionally (chosen) | Directly matches this codebase's own already-accepted precedent (ADR-0054) for the identical bug shape; simplest code (removes a whole conditional branch); `RefreshCareerStintsAsync`'s reconciliation makes repeat calls cheap and safe | One additional live Wikidata call per chain-step submission, even when both players' data is already genuinely complete | REQ-1406 already commits to "each step is validated live, at submission time" — an extra round trip on an already-live-validated action is a matter of degree, not a new category of cost, and it directly closes a real, reported correctness bug |
| Add a new "fully fetched" tracking column/flag on `Player`, checked instead of "has any row" | Avoids a redundant Wikidata call once a player is genuinely known-complete; more precise than either extreme | New schema, a new migration, and a new invariant to keep correct (when exactly does a "narrow" write get to set this flag, vs. a full one?) for a problem the existing reconciliation logic already handles cheaply enough | Real complexity for a marginal perf gain; not what ADR-0054 already established as this codebase's answer to the same problem, and no evidence yet that the extra Wikidata call is actually a performance problem in practice |
| Keep the "any rows" gate, but track provenance (narrow vs. full fetch) per stint or per batch | Same idea as the flag option, finer-grained | Same added complexity, plus PlayerCareerStint rows from different origins (xG Grid byproduct, xG Path full refresh, xG Connect full refresh) would need to be distinguishable after the fact — a bigger schema change than this bug warrants | Same reasoning as the flag option — solves a problem not yet shown to exist |

## Consequences

- Positive: closes a real, reported false-rejection bug directly, using
  this codebase's own already-established, already-precedented fix shape
  for the identical bug — no new design, no new ADR-worthy trade-off beyond
  "apply the existing precedent here too."
- Positive: removes a subtle, easy-to-reintroduce correctness trap (a
  shared table's "has any row" is not "has complete data") from this
  specific call site — the same trap a future caller of
  `PlayerCareerOverlapService` could otherwise reintroduce by copying its
  old pattern.
- Negative / trade-off accepted: one additional live Wikidata call per
  chain-step submission (both players, every time), even when nothing new
  would be found. Not measured against a specific latency/rate-limit budget
  in this ADR — revisit only if this proves to be an actual, observed
  problem (the Follow-up below), not preemptively.
- Follow-up: if per-step latency or Wikidata request volume becomes a real,
  observed problem (not assumed), the "fully fetched" flag/provenance
  alternatives above become worth revisiting — with real numbers behind the
  decision this time, not a guess made before any evidence existed.
- Follow-up: `SparqlResponseParsers.ParseCareerStintBindings` still silently
  drops any P54 statement whose `startTime` qualifier is missing or
  unparseable (loan spells are the class of stint most likely to have an
  imprecise Wikidata date). This ADR does not address that — a full refresh
  can still miss a real stint if Wikidata's own record of it has no usable
  date. Separate, pre-existing gap; not fixed here.

## For AI agents

Do not reintroduce a "skip refreshing a player who already has some
`PlayerCareerStint` rows" optimization anywhere in `Games.XGConnect` or
`Games.XGPath` without re-reading this ADR and ADR-0054 first — both exist
specifically because that heuristic has already produced two real,
independently-reported correctness bugs in two different game modules. If
Wikidata call volume genuinely needs reducing, that needs its own ADR
proposing a real completeness signal (see the Alternatives table above),
not a quiet reintroduction of "has any row."
