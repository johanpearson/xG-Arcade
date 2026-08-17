# ADR-0045: xG Path puzzle generation — entity shape, Player FK, and REQ-1201's textual ambiguities

- **Status:** Accepted (superseded in part by ADR-0073 and ADR-0074 — see notes below)
- **Date:** 2026-07-27
- **Related requirements:** REQ-1201, REQ-1202
- **Related components:** COMP-11 (Games.XGPath), COMP-06 (Data.PlayerStore)

> **Superseded in part by ADR-0073 (2026-08-17):** ADR-0073 adds a
> `Player.BirthYear >= 1975` eligibility floor to `XGPathGameModule`'s
> target-player selection, additive to the three structural checks this
> ADR's Decision §§3-4 already establish. This ADR remains the
> authoritative record for everything else it covers (entity shape, the
> `Player` FK decision, the "3 stint rows not 3 clubs" reading, the
> "undeterminable order" reading) — see ADR-0073 for the birth-year point
> specifically.

> **Superseded in part by ADR-0074 (2026-08-17):** ADR-0074 drops this
> ADR's Decision §3 ("≥3 documented stint rows, not 3 distinct clubs")
> entirely — it's redundant once eligibility requires ≥2 distinct
> qualifying seeded clubs (ADR-0074), which is strictly more specific.
> Decision §4 (chronological order determinable) is untouched and remains
> governed by this ADR. See ADR-0074 for the current eligibility rule.

## Context

S-081 is xG Path's first real logic (`GenerateInstanceAsync`/`GetCellIdsAsync`),
built on top of S-079's `PlayerCareerStint` (ADR-0042) and S-080's module
scaffold. Three things needed a decision that could reasonably have gone
another way:

1. **Entity shape.** `IGameModule`'s only existing precedent
   (`GridTemplate`/`GridInstance`/`GridCell`, COMP-05) has no entity that
   targets one specific, fixed player — a `GridCell` is only ever two
   category constraints, checked against whichever players happen to match
   at guess time. An xG Path puzzle, by contrast, targets exactly one
   player, fixed at generation time (REQ-1201/1202).
2. **Whether that fixed target should be a real foreign key to `Player`.**
   `GridCell` deliberately has no such FK (it has no single fixed answer to
   reference). Every existing FK-to-`Player` in `XGArcadeDbContext`
   (`PlayerData`, `PlayerOverride`, `PlayerAttribute`, `PlayerAlias`,
   `PlayerCareerStint`) is COMP-06-internal — this would be the first
   FK-to-`Player` from a *different* component's own entity.
3. **Two of REQ-1201's acceptance-criteria phrases are genuinely
   ambiguous as written:**
   - "at least 3 distinct documented career club stints" — does "distinct"
     mean 3 different *clubs*, or 3 separately-recorded *rows* (which
     `PlayerCareerStint`'s own doc comment says can repeat the same club,
     e.g. a loan then a later permanent return)?
   - "chronological order determinable from start/end dates" — what
     specific data condition makes an order "undeterminable," given that
     `AddCareerStintsAsync` always assigns *some* `SequenceOrder` to every
     row regardless?

## Decision

1. **New entities `PathTemplate`/`PathInstance`/`PathPuzzle`**, deliberately
   mirroring `GridTemplate`/`GridInstance`/`GridCell`'s shape (surrogate
   `Guid` ids, `PathInstance.Puzzles` as an owned collection cascade-deleted
   with its parent) rather than inventing a new persistence pattern.
   `PathPuzzle.Id` is the opaque "cell id" `IGameModule.GetCellIdsAsync`
   returns, same contract `GridCell.Id` already fulfills.
2. **`PathPuzzle.TargetPlayerId` IS a real FK to `Player`, cascade delete.**
   Unlike `GridCell`, an xG Path puzzle has exactly one correct answer,
   fixed at generation time, so a referential-integrity constraint is
   meaningful here in a way it isn't for `GridCell`. This is a cross-
   component FK (COMP-11's own table pointing into COMP-06's `Player`
   table) but is **not** the boundary ADR-0003 protects — that ADR is
   specifically about `Round`/`XGArcade.Core` never holding a foreign key
   into a *game-specific* table. A game module referencing shared platform
   data (`Player`) is the same direction of reference `PlayerCareerStint`
   etc. already have, just from the opposite entity.
3. **"≥3 distinct documented career club stints" is read as ≥3 stint
   *rows*, not 3 distinct clubs.** `PlayerCareerStint`'s own doc comment
   explicitly treats two rows at the same club (loan, then a later
   permanent return) as two valid, separate stints; REQ-1201's text never
   says "3 different clubs." A player with 3 rows all at the same seeded
   club is eligible.
4. **"Chronological order determinable from start/end dates" is read as:
   reject a candidate if any two of their stints share an identical
   `(StartYear, EndYear)` pair**, including two simultaneously "ongoing"
   stints (`EndYear` both `null` — compared as equal, not as "never a
   duplicate"). At that point, `AddCareerStintsAsync`'s persisted
   `SequenceOrder` between those two rows reflects write order, not
   anything actually derivable from the dates themselves, so the order
   genuinely isn't "determinable from start/end dates" for that candidate.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| No FK from `PathPuzzle.TargetPlayerId` to `Player` (mirror `GridCell`'s FK-less precedent) | Keeps the "games don't FK into shared tables" pattern uniform | An xG Path puzzle *does* have exactly one real, permanent answer — leaving it unconstrained invites an orphaned target with no DB-level protection, for no boundary reason (this isn't ADR-0003's Core/game boundary) | The data genuinely has referential integrity to offer here; `GridCell` not having one is about its answer being computed dynamically, not a general "games never FK into Player" rule |
| "3 distinct clubs" reading of REQ-1201 | Arguably a more literal reading of "distinct" | Contradicts `PlayerCareerStint`'s own doc comment, which explicitly documents same-club repeat stints as valid; would silently reject real, well-documented careers (loan-then-return) with no textual requirement forcing that | `PlayerCareerStint`'s existing, more specific doc comment takes precedence over one ambiguous adjective in REQ-1201's prose |
| Reject only on exact duplicate rows (same club AND same dates) for the "undeterminable order" check | Simpler | Two stints at *different* clubs with identical `(StartYear, EndYear)` are just as ambiguous chronologically — the club identity doesn't resolve which one comes first | The ambiguity is about date ordering specifically, independent of which club is involved |

## Consequences

- Positive: xG Path's puzzle-generation data model has a real referential-
  integrity guarantee on its most important fact (which player a puzzle
  targets), and REQ-1201's two ambiguous phrases now have one settled,
  documented reading instead of being re-interpreted ad hoc by whoever
  implements REQ-1203/S-082 next
- Negative / trade-offs accepted: cascading a future `Player` deletion
  would also delete any `PathPuzzle` still targeting them — currently
  low-risk since no player-deletion pathway exists in the codebase, but
  worth re-examining if one is ever added
- Follow-up: if a future story adds player-row deletion/anonymization
  (parallel to REQ-710's `Guess.UserId = NULL` treatment), revisit whether
  `PathPuzzle`'s cascade delete is still the right behavior for an
  in-progress or historical puzzle, versus e.g. anonymizing/soft-deleting
  the puzzle instead

## For AI agents

Do not read REQ-1201's "3 distinct... stints" as requiring 3 different
clubs, and do not treat two same-club stints as a violation — see
`PlayerCareerStint`'s own doc comment. Do not narrow the "undeterminable
order" check to exact-duplicate rows only; it is specifically about a tied
`(StartYear, EndYear)` pair regardless of club. Do not add a redundant
REQ-112 (pool membership) runtime check anywhere in COMP-11 — `Player` has
no `BirthYear`/`Gender` field, and the restriction is enforced upstream at
Wikidata-query time (ADR-0025); COMP-05 (`GridGameModule`) already
establishes this same "verified by construction, not by a runtime branch"
precedent. If a task seems to need `PathPuzzle` to reference `Player`
loosely (no FK) "to match `GridCell`'s pattern," stop and re-read this
ADR's Decision §2 first — the two cases are not analogous.
