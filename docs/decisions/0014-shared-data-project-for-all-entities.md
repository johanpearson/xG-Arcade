# ADR-0014: All EF Core entities and repositories live in XGArcade.Data, regardless of which component owns them

- **Status:** Accepted
- **Date:** 2026-07-09
- **Related requirements:** REQ-101, REQ-102, REQ-107, REQ-109, REQ-701
- **Related components:** COMP-01 (Core.Users), COMP-05 (Games.XGGrid)

## Context

`architecture-document.md`'s §5 component table maps each component to the
project its logic lives in — e.g. COMP-01 (Core.Users) → `XGArcade.Core`,
COMP-05 (Games.XGGrid) → `XGArcade.Games.XGGrid`. Twice now, that mapping
has been read literally for persistence and then not followed: S-004 put
`User` and `IUserRepository`/`UserRepository` in `XGArcade.Data`, not
`XGArcade.Core`; S-007 did the same for `GridTemplate`/`GridInstance`/
`GridCell` and `IGridInstanceRepository`, which `implementation-document.md`
§5 explicitly labels as "xG Grid game entities (XGArcade.Games.XGGrid)" —
in direct tension with where the code actually puts them. Both times this
happened without a documented rationale, which an architecture-reviewer
pass on S-007 flagged as worth resolving before a third component repeats
the pattern unexamined.

The underlying reason both stories made this choice: the whole app shares
one Postgres database, one `XGArcadeDbContext`, and one EF Core migration
history, all living in `XGArcade.Data`. Splitting entity classes and
repositories across each owning project (e.g. `XGArcade.Games.XGGrid`
defining its own `GridInstance` class and its own `DbContext`) would mean
either multiple `DbContext`s against the same physical database, or
`XGArcade.Data` referencing every other project to assemble one `DbContext`
from pieces defined elsewhere — both real alternatives, not obviously
wrong, but neither was ever chosen deliberately; the single-`DbContext`
shape just kept getting reused because it already existed.

## Decision

Every EF Core entity class, `DbSet`, and repository interface+implementation
lives in `XGArcade.Data`, in one shared `XGArcadeDbContext` with one
migration history — regardless of which component in
`architecture-document.md`'s §5 table conceptually owns that data. That
table's "maps to" column describes where a component's business/
orchestration logic lives (e.g. grid generation's retry/abort algorithm is
in `XGArcade.Games.XGGrid`), not where its EF entity classes or persistence
code live. A component's boundary is enforced by which repository
interfaces other components are allowed to call (see boundary rule 1 and
each repository's own doc comment), not by which `.csproj` the entity class
physically sits in.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Shared `XGArcade.Data` project for all entities/repositories (chosen) | One `DbContext`, one migration history, one connection string to manage; matches what S-004 and S-007 already built | The component table's "maps to" column is misleading at face value for persistence; `implementation-document.md` §5's per-component entity headers overstate project boundaries that don't actually exist in code | Already the working pattern in two components; changing it now means moving `User` and `GridTemplate`/`GridInstance`/`GridCell` out of `XGArcade.Data`, a real migration-history disruption for no behavior change |
| Each component owns its own entities + its own `DbContext`, all against the same physical database | Entity classes physically live where the docs say they "map to"; a component's persistence code is genuinely self-contained | Multiple `DbContext`s against one database is real added complexity (separate migration histories to keep in sync, cross-`DbContext` queries impossible without raw SQL) for a single-database app with no current need to deploy components independently | More structure than Tier 0 (or the foreseeable system) needs; nothing about the actual requirements calls for independently-migratable components |
| Each component defines its entities in its own project; `XGArcade.Data` only holds the assembled `DbContext`, referencing every other project | Entity classes live with their owning component's logic | `XGArcade.Data` would need a project reference to every component project (`Core`, `Games.XGGrid`, and any future game module), which a shared, low-level persistence project having a reference *up* to feature projects it's supposed to be beneath — resembles the ADR-0003 problem (bidirectional coupling) applied to persistence instead of `Round` | Structurally awkward for the same reason ADR-0003 rejected direct FKs: it would make `XGArcade.Data` implicitly aware of every game module that ever gets added |

## Consequences

- Positive: no migration-history disruption from what's already built; one
  connection string, one place to run `dotnet ef migrations add`; a future
  game module's entities and repository follow the same, now-documented
  pattern instead of re-deriving it from precedent
- Negative / trade-offs accepted: `architecture-document.md`'s component
  table can look inconsistent with actual project layout to someone reading
  it literally for the first time — mitigated by this ADR and the table
  footnote added alongside it; a second game module will still put its
  `GridInstance`-equivalent entities in the same shared `XGArcade.Data`
  project as xG Grid's, which is a real (if currently harmless) coupling
  point worth re-examining if a second game module is ever actually built
- Follow-up: when a second game module is built, verify this still holds —
  if two game modules' entities end up needing genuinely different
  lifecycle/migration cadences, that's a signal this ADR needs revisiting,
  the same kind of check ADR-0003 already asks for on its own boundary

## For AI agents

A new component's EF Core entity classes and repository interface+
implementation belong in `XGArcade.Data`, in `XGArcadeDbContext`, following
`ICategoryValueRepository`/`IPlayerStoreRepository`/`IGridInstanceRepository`'s
existing shape — even if `architecture-document.md`'s component table says
that component "maps to" a different project. That column describes
business-logic ownership only. The component boundary itself (e.g. "COMP-05
may only reach player data through COMP-06's public interface") is what
matters and must still be enforced — just not by moving the entity class
itself into a different project. If a future change seems to require a
second `DbContext` or per-component migration history, stop and flag it
rather than adding one silently — that would supersede this ADR, not extend
it.
