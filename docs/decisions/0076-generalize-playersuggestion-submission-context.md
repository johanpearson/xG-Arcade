# ADR-0076: Generalize `PlayerSuggestion`'s submission context off `CellId`/row-col category types

- **Status:** Accepted
- **Date:** 2026-08-18
- **Related requirements:** REQ-215, REQ-1201-1209 (xG Path, context only — no xG Path behavior changes here)
- **Related components:** COMP-05 (Games.XGGrid), COMP-06 (Data.PlayerStore — `PlayerSuggestion`/`PlayerSuggestionClub`), COMP-11 (Games.XGPath)

## Context

`PlayerSuggestion` (ADR-0053, REQ-215) and its submission route
(`SuggestionEndpoints.cs`, `POST /rounds/{roundId}/cells/{cellId}/suggestions`)
are structurally coupled to xG Grid:

- `PlayerSuggestion.CellId` is a required, non-nullable field — the
  originating `GridCell`, explicitly flagged in the entity's own doc
  comment as a v1 simplification coupling this table to a xG-Grid-specific
  concept (the same accepted shape `Guess.CellId` already carries).
- `RowCategoryType`/`ColCategoryType` are required, non-nullable strings,
  denormalized off the `GridCell` at submission time.
- The submission route resolves these two fields by calling
  `IGameModule.GetCellCategoryTypesAsync(instanceId, cellId)` — a method
  `XGPathGameModule` deliberately implements as a hard
  `NotSupportedException`, with its own doc comment stating plainly that
  "REQ-215's `PlayerSuggestion` flow is not supported for `xg-path`" and
  that there is no reachable production caller for xG Path today.

This is a documented, deliberate gap, not an oversight —
`requirements-document.md`'s REQ-215 entry already notes "if xG Path ever
grows a suggestion entry point, not fixed now." Epic 14
(`docs/backlog.md`) closes it: xG Path has no cell/category-pairing
concept to plug into the existing shape. It has a single target player per
puzzle, revealed via progressive clue turns (`PathClueTurn`), so "report a
correction" there means "this target player's asserted nationality/club is
wrong," not "this cell's category pairing is wrong."

This codebase already has a directly applicable precedent for exactly this
shape of problem: ADR-0003 established that `Round` references its game
instance through two game-agnostic fields — `GameKey` (string) and
`GameInstanceId` (opaque `Guid`) — rather than a game-specific foreign key,
specifically so a second game could be added without a schema change to
the game-agnostic side. `PlayerSuggestion` needs the same treatment for
the same reason.

**Two structural questions have to be settled together**, since S-144
(the follow-up implementation story) depends on both:

1. What shape does `PlayerSuggestion`'s per-game context take?
2. What does the submission route look like once it has to serve two
   games with genuinely different context shapes?

## Decision

### 1. Entity shape: `GameKey` + nullable, per-game opaque context fields

`PlayerSuggestion` gains a required `GameKey` field (string, same
vocabulary as `Round.GameKey`/`IGameModule.GameKey` — `"xg-grid"` /
`"xg-path"`), and its existing xG-Grid-specific fields become
**nullable, populated only when `GameKey == "xg-grid"`**:

- `CellId` (`Guid?`, was `Guid`)
- `RowCategoryType` (`string?`, was `string`)
- `ColCategoryType` (`string?`, was `string`)

A new field carries xG Path's equivalent, populated only when
`GameKey == "xg-path"`:

- `PathPuzzleId` (`Guid?`) — the specific `PathPuzzle` (target player) the
  report concerns.

This mirrors ADR-0003's opaque `GameKey` + per-game-instance shape rather
than inventing a new pattern, per S-143's own direction — but the new
field is named `PathPuzzleId`, not `PathInstanceId` as the backlog entry's
prose suggested. That's a deliberate correction, not a typo: the field
this ADR needs is the one that plays the *same structural role as
`CellId`* — identifying the specific unit-of-report *within* a game
instance, not the instance itself. `Round.RoundId` already resolves to
`Round.GameInstanceId` (== `PathInstance.Id`, per ADR-0003), exactly the
same way it already resolves to `GridInstance.Id` for xG Grid — storing
an instance-level id a second time on `PlayerSuggestion` would be pure
redundancy with `RoundId`, for either game. `CellId` earns its place on
this entity precisely because a `GridInstance` has *many* cells and
`RoundId` alone can't tell you which one; the equivalent gap for xG Path
is "which puzzle (target player), of the several in this round's
`PathInstance`," which only `PathPuzzleId` answers.

`AssertedClubs`/`AssertedNationality`/`PlayerName`/`SubmittingUserId`/
`RoundId`/`Status`/`CreatedAt`/`ResolvedByAdminId`/`ResolvedAt` are
**unchanged** — a suggestion is always "this player's true data is X,"
regardless of which game surfaced the report. This confirms S-143's own
accept criterion: nothing about the claim itself needed to change, only
the submission-context fields.

A migration backfills `GameKey = "xg-grid"` for every existing row
(the only game with a real submission path before this ADR), and adds
the new nullable columns — left to S-144 to write, not part of this
design-only story.

### 2. Route shape: widen the existing route, moving `cellId` into the body

The existing route path (`POST
/rounds/{roundId}/cells/{cellId}/suggestions`) is itself game-specific —
a URL segment literally named `cellId` cannot represent "this report
concerns `PathPuzzleId` X" without either lying about what the segment
means or forcing xG Path through a URL shape borrowed from a concept it
doesn't have.

The route becomes `POST /rounds/{roundId}/suggestions`, with the
context id moved into the request body as two optional fields
(`cellId`, `pathPuzzleId`), exactly one of which is required, chosen by
the same `round.GameKey` resolution the endpoint already performs via
`IGameModuleResolver` (ADR-0003) before doing anything else. This is the
"widen the existing route to branch on `GameKey`" option S-144 flagged as
needing S-143 to settle, not a new per-game route:

- A per-game route (e.g. a second `POST
  /path/rounds/{roundId}/suggestions`) would duplicate the entire
  authenticated/non-guest validation block, the `PlayerName`/clubs/
  nationality validation, and the persistence call this file already has
  — the same "duplicated plumbing" reasoning this ADR uses below to
  reject a separate `PathSuggestion` table applies just as directly to a
  separate route.
- One route, branching on `GameKey` right after resolving `round` (the
  same point `GetCellCategoryTypesAsync` is called today), keeps
  `SuggestionEndpoints.cs`'s existing "resolve `Round` → resolve
  `IGameModule` → validate → persist" shape intact for both games.

**Server-side context validation, resolved per game, never trusted from
the client:**

- `GameKey == "xg-grid"`: unchanged from today — call
  `IGameModule.GetCellCategoryTypesAsync(round.GameInstanceId, cellId)`
  to resolve the authoritative `RowCategoryType`/`ColCategoryType`, same
  `GameEntityNotFoundException` → `404` handling as today.
- `GameKey == "xg-path"`: **`GetCellCategoryTypesAsync` is never called**
  — it keeps its existing `NotSupportedException` untouched, since xG
  Path genuinely has no row/col category concept to return, and this
  route never needs one. Instead, `pathPuzzleId` is validated as a real
  puzzle in this round's instance via the already-implemented,
  game-agnostic `IGameModule.GetCellIdsAsync(instanceId)` (which
  `XGPathGameModule` already implements, returning `instance.Puzzles
  .Select(p => p.Id)` — the same list `Core.Scoring`'s unanswered-cell
  penalty already reads) — a membership check (`ids.Contains(pathPuzzleId)`),
  not a new interface method.

This directly settles the open question S-144 flagged: whether
`XGPathGameModule.GetCellCategoryTypesAsync`'s `NotSupportedException`
needs replacing. It does not — the new route bypasses it entirely for xG
Path, exactly as S-144's own text anticipated as the likely outcome.

### 3. Admin review stays untouched

`AdminSuggestionEndpoints.cs`'s list/review/commit/reject flow
(`ResolveAsync`, `CommitPlayerDataAsync` — ADR-0060's nationality→
`PlayerOverride` / clubs→additive-`PlayerAttribute` split) reads only
`PlayerName`/`AssertedNationality`/`AssertedClubs`/`Status` — confirmed by
inspection, no branch anywhere on `CellId`/category types. It needs no
game-specific change: a `GameKey`/`PathPuzzleId`-carrying row reviews,
commits, and rejects through the exact same admin flow a `CellId`-carrying
one does today. `GameKey`/`CellId`/`PathPuzzleId` become available as
additional, purely informational context on the admin list/detail view if
wanted later — not required for this story, and not part of this ADR's
decision.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| **`GameKey` + nullable per-game opaque context fields on `PlayerSuggestion` (chosen)** | Directly mirrors this codebase's own established ADR-0003 precedent; no duplicated `SubmittingUserId`/`Status`/`ResolvedAt`/admin-review plumbing; admin review, `GET /me/suggestions` (Epic 15, S-147) and any future cross-game suggestion query stay single-table, single-shape | `PlayerSuggestion` carries columns that are `NULL` for every row of the other game — the same trade-off ADR-0003 itself already accepted for `Round.GameKey`/`GameInstanceId` | Best fit: same problem shape ADR-0003 already solved, same trade-off already accepted platform-wide |
| Single polymorphic JSON/text context-blob column (e.g. `ContextJson`) | One column handles any future game's context shape without a schema change per game | Loses typed columns/queryability for the two shapes that exist today; admin tooling and any future query (e.g. "suggestions for cell X") would need to parse an opaque blob; no precedent for this shape anywhere else in the codebase (`PlayerSuggestion.AssertedClubs` itself deliberately avoided a delimited/JSON column for the same reason, per its own doc comment) | Rejected: trades away real type safety for a flexibility this codebase has never needed elsewhere, for a platform currently hosting exactly two games |
| Fully separate `PathSuggestion` table, mirroring `PlayerSuggestion`'s shape with xG-Path-specific fields | Each table stays free of the other game's unused nullable columns | Duplicates `SubmittingUserId`/`Status`/`CreatedAt`/`ResolvedByAdminId`/`ResolvedAt`/`AssertedClubs`/`AssertedNationality` and all admin-review plumbing (`AdminSuggestionEndpoints.cs`'s list/commit/reject flow) across two tables/endpoints; directly conflicts with Epic 15's `GET /me/suggestions` (S-147), which depends on one unified, per-user, all-status query surface across every game a suggestion could originate from | Rejected: same reasoning ADR-0053 already used to keep `PlayerSuggestion` separate from REQ-503's queue, applied in reverse — here the two rows share enough shape and reviewer workflow (submit → pending → admin commit/reject) that forcing them apart duplicates real plumbing for no benefit |
| New per-game submission route (`POST /path/rounds/{roundId}/suggestions`), existing xG-Grid route left untouched | No change to the existing route's URL shape or its current callers | Duplicates the entire authenticated/non-guest/validation/persistence block in `SuggestionEndpoints.cs` across two route handlers for what is otherwise identical logic; every future game needs its own copy | Rejected: same "duplicated plumbing" reasoning as the separate-table alternative above, applied to the route layer |

## Consequences

- Positive: xG Path can submit a suggestion (S-144) without a second
  table, a second admin review flow, or a change to `GET /me/suggestions`'s
  (Epic 15) single-query shape.
- Positive: `XGPathGameModule.GetCellCategoryTypesAsync` needs no new
  implementation — its existing `NotSupportedException` stays exactly as
  documented, since the widened route never calls it for `xg-path`.
- Positive: a third future game reuses the same pattern — add its own
  nullable opaque context field(s), branch the route on its `GameKey` —
  without another schema redesign, the same "adding a game touches no
  Core table" property ADR-0003 established for `Round`.
- Negative / trade-off accepted: `PlayerSuggestion` now has three
  columns (`CellId`, `RowCategoryType`, `ColCategoryType`) that are always
  `NULL` for an `xg-path` row, and one (`PathPuzzleId`) always `NULL` for
  an `xg-grid` row — the same nullable-sprawl trade-off ADR-0003 itself
  already accepted for `Round.GameInstanceId`'s cross-game genericity, now
  extended to a second entity.
- Negative / trade-off accepted: no database-level constraint enforces
  "exactly one of `CellId`/`PathPuzzleId` is set, matching `GameKey`" —
  enforced only at the application level, in `SuggestionEndpoints.cs`'s
  validation, the same "opaque cross-game reference, not enforced
  referential integrity" trade-off `CellId`'s own existing doc comment
  already accepts for its FK-less relationship to `GridCell`.
- Follow-up: if a third game's context shape doesn't fit "one nullable
  opaque id," revisit whether the nullable-column-per-game pattern still
  holds, or whether it's time for the polymorphic-context alternative
  rejected above — same "revisit if a second game module shows this
  pattern doesn't hold" follow-up ADR-0003 itself already carries.
- Follow-up: S-144 must add the backfill migration (`GameKey = "xg-grid"`
  for all pre-existing rows) and the route/body changes described above;
  S-146 updates REQ-215's status note and `architecture-document.md`'s
  COMP-06 row once S-144/S-145 land.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision
needs a new ADR that supersedes this one, or the approach needs to
change.

Specifically: do not add a third top-level submission route for a future
game before checking whether widening `POST /rounds/{roundId}/suggestions`
still fits — that duplication is exactly what this ADR rejected for xG
Path. Do not resolve `PathPuzzleId`/xG-Path context via
`IGameModule.GetCellCategoryTypesAsync` — that method's
`NotSupportedException` is deliberate and stays in place; use
`IGameModule.GetCellIdsAsync` for existence validation instead, per the
Decision above. Do not add a game-specific foreign key column to
`PlayerSuggestion` for any new game's context — follow this ADR's
nullable-opaque-id pattern (or supersede this ADR first if a concrete case
shows the pattern no longer fits, per the Follow-up note above).
