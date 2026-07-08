# ADR-0003: Round references game instances generically, never a game-specific FK

- **Status:** Accepted
- **Date:** 2026-07-04
- **Related requirements:** REQ-301, REQ-302
- **Related components:** COMP-03 (Core.Rounds), COMP-05 (Games.XGGrid, and any future game module)

## Context

The xG Arcade is meant to sit one level above any individual game: it owns
users, leagues, the round schedule, and scoring, while games (starting with
xG Grid) plug in underneath it. Early drafts of the data model had
`Round.GridInstanceId` as a direct foreign key to a xG Grid-specific
table. That's fine for a single game, but it means `Core.Rounds` — supposedly
game-agnostic — actually depends on a xG Grid concept. Adding a second
game later would require either a second nullable FK column on `Round` for
every new game, or a migration to fix the original design.

## Decision

`Round` references its game instance through two game-agnostic fields:
`GameKey` (string, e.g. `"xg-grid"`) and `GameInstanceId` (`Guid`, opaque
to Core). `Core.Rounds` never resolves `GameInstanceId` into an actual
entity — it only uses `GameKey` to look up which `IGameModule` implementation
owns that round, and delegates instance resolution, generation, and
answer-validation to that module.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Direct FK per game (`GridInstanceId`, later `OtherGameInstanceId`, ...) | Simple, type-safe within EF Core | `Round` grows a new nullable column per game; `Core.Rounds` has compile-time knowledge of every game | Directly contradicts the platform-above-games structure the user asked for |
| Polymorphic/inheritance-based instance table | Type-safe, single table | EF Core table-per-hierarchy adds complexity disproportionate to two entities; still couples Core to game schemas | More complexity than the opaque-ID approach for no real benefit at this scale |
| Opaque GameKey + GameInstanceId (chosen) | Core has zero compile-time knowledge of any specific game; adding a game touches no Core table | Loses a DB-level foreign-key constraint on `GameInstanceId` — referential integrity for the instance itself is the owning game module's responsibility | Best fit for the stated goal: xG Arcade must not know about any single game's internals |

## Consequences

- Positive: a second game can be added without any migration or code change
  to `Core.Rounds`; the boundary the user asked for ("platform is one level
  above the game") is enforced structurally, not just by convention
- Negative / trade-offs accepted: no database-level FK constraint from
  `Round` to the game-specific instance table, so an orphaned `GameInstanceId`
  is only caught at the application level (the owning `IGameModule` should
  validate this and surface an error, not fail silently)
- Follow-up: when a second game module is actually built, use it to verify
  this pattern holds — if `Core.Rounds` ends up needing game-specific
  branching logic anyway, that's a signal this ADR needs revisiting

## For AI agents

Never add a game-specific foreign key column to the `Round` entity or to any
other `Core.*` entity. If a task seems to require Core to know about a
specific game's internals, stop and flag it — the answer is almost always
"resolve it through `IGameModule` using `GameKey`", not a new direct
reference.
