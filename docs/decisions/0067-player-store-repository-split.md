# ADR-0067: Split `IPlayerStoreRepository` by entity concern (Player/PlayerData/PlayerAttribute/PlayerAlias)

- **Status:** Accepted
- **Date:** 2026-08-11
- **Related requirements:** none (pure refactor, no behavior change — same
  "foundational plumbing, no REQ-xxx" status the original
  `PlayerStoreRepositoryTests.cs` class comment already carried)
- **Related components:** COMP-06 (Data.PlayerStore)

## Context

`PlayerStoreRepository.cs`/`IPlayerStoreRepository.cs` had grown to
772/482 lines and 43 methods — confirmed the clearest Single Responsibility
Principle outlier in `backend/src/XGArcade.Data/Repositories/` by comparing
method counts across every repository in that folder (the next-highest was
16). The interface covered at least six genuinely distinct concerns: core
`Player` CRUD, `PlayerData` (raw sync log + admin review/approve/remove),
`PlayerAttribute`, `PlayerAlias`, `PlayerOverride`, and a grab-bag of
photo/position/birth-year backfill cursors, `PlayerCareerStint`, and
data-quality tracking (`ConfirmedLowMatchPair`/`PairLookupFailure`,
`GetUnseededClubCandidatesAsync`). Every caller across `Games.XGGrid`,
`Games.XGPath`, `DataSync.Wikidata`, and the admin API endpoints depended
on the same single wide interface regardless of which one or two of those
six concerns it actually used, making the boundary between "COMP-06 is the
only path to player data" (a real, load-bearing architecture rule,
`architecture-document.md` boundary rule 1) and "one repository class
happens to implement that boundary" impossible to see from the code
itself.

This ADR covers only the first half of the split (S-106,
`docs/backlog.md` Epic 8) — `IPlayerRepository`, `IPlayerDataRepository`,
`IPlayerAttributeRepository`, `IPlayerAliasRepository`. The remaining five
concerns (`PlayerOverride`, the photo/position/birth-year backfill
cursors, `PlayerCareerStint`, and the confirmed-low/technical-failure
data-quality tracking) are deliberately left on the original
`IPlayerStoreRepository`/`PlayerStoreRepository` for now — S-107 (backlog,
independent of this story, no shared new infrastructure with it) splits
those out next. Both stories must land before the original
`PlayerStoreRepository.cs`/`IPlayerStoreRepository.cs` files are deleted;
this ADR's own "For AI agents" section repeats that constraint.

## Decision

Split `IPlayerStoreRepository`'s Player/PlayerData/PlayerAttribute/
PlayerAlias methods into four new, narrower repository interface+
implementation pairs, following this folder's existing one-interface-
per-file convention (e.g. `IPlayerNameIndexRepository`):

- **`IPlayerRepository`/`PlayerRepository`** — core `Player` CRUD:
  `GetPlayerByWikidataQidAsync`, `GetPlayerByIdAsync`,
  `GetPlayersByIdsAsync`, `AddPlayerAsync`,
  `GetOrCreatePlayersByWikidataQidAsync` (and its `PlayerCreationRequest`
  record), `GetPlayersByNormalizedFullNameAsync`.
- **`IPlayerDataRepository`/`PlayerDataRepository`** — the `PlayerData`
  raw append-log and its admin review/approve/remove lifecycle:
  `AddPlayerDataAsync`, `AddPlayerDataBatchAsync`,
  `GetUnverifiedPlayerDataAsync`, `ApprovePlayerDataAsync` (and its
  outcome/failure-reason types), `RemovePlayerDataAsync` (and its
  outcome/failure-reason types).
- **`IPlayerAttributeRepository`/`PlayerAttributeRepository`** —
  `PlayerAttribute`, including the two attribute-driven queries that
  happen to return `Player` rows
  (`GetPlayersWithEitherAttributeAsync`, `CountPlayersWithBothAttributesAsync`):
  those stay with the attribute concern rather than moving to
  `IPlayerRepository`, since they query `PlayerAttribute` first and are
  fundamentally attribute-driven, not `Player`-driven.
- **`IPlayerAliasRepository`/`PlayerAliasRepository`** — `PlayerAlias`,
  with the same "query-driven-by-this-entity, not by which type it
  returns" reasoning applied to `GetPlayersByNormalizedAliasAsync`.

Every new interface is registered independently in
`CompositionRoot/ServiceRegistration.cs` (`AddScoped`, same lifetime as
the original). **No facade or aggregate repository was added** — each
call site takes exactly the narrower interface(s) it actually calls,
multi-injecting when it genuinely needs more than one (e.g.
`WikidataLookupService.PersistMatchesAsync` needs all four new interfaces
plus the still-undivided `IPlayerStoreRepository` for the
`PlayerCareerStint` methods S-107 hasn't moved yet). A caller using only
already-moved methods drops `IPlayerStoreRepository` entirely; a caller
mixing moved and not-yet-moved methods keeps both.

The `GroupByPlayerIdAsync<TEntity>` private helper (shared, pre-split, by
`GetPlayerAliasesByPlayerIdsAsync`/`GetPlayerAttributesByPlayerIdsAsync`/
`GetCareerStintsByPlayerIdsAsync`) is **duplicated**, not shared across the
new repository classes — one private copy in `PlayerAliasRepository`, one
in `PlayerAttributeRepository`, and the original stays in
`PlayerStoreRepository` for `GetCareerStintsByPlayerIdsAsync` (S-107
territory). Repositories do not depend on each other.

Existing `PlayerStoreRepositoryTests.cs` coverage for the four moved
concerns was moved/renamed into `PlayerRepositoryTests.cs`/
`PlayerDataRepositoryTests.cs`/`PlayerAttributeRepositoryTests.cs`/
`PlayerAliasRepositoryTests.cs`, each instantiating its own repository
class against the same shared EF Core InMemory `XGArcadeDbContext` fixture
pattern this project already uses — test bodies/assertions are unchanged,
this is a structural move only, no new REQ IDs.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Leave `IPlayerStoreRepository` as one wide interface | Zero migration risk; no call-site churn | The 43-method/772-line SRP violation persists indefinitely, and every caller keeps depending on (and mocking/faking against) a surface far wider than what it uses | Rejected — the outlier was severe enough (2.7x the next-highest repository's method count) that the maintenance cost of leaving it was judged higher than the one-time migration cost |
| Split into exactly one repository per concern in a single story (Player/PlayerData/PlayerAttribute/PlayerAlias/Override/backfill/CareerStint/data-quality — all nine at once) | One migration pass, one PR, no interim "half-split" state | A single story touching every call site across `Games.XGGrid`, `Games.XGPath`, `DataSync.Wikidata`, and every admin endpoint simultaneously is a much larger, harder-to-review diff, and the two halves (S-106's four concerns, S-107's five) have no shared new infrastructure forcing them together | Rejected — split into two independent stories (S-106, this ADR's scope, and S-107) that can land in either order; smaller, independently reviewable diffs at the cost of a temporary state where `IPlayerStoreRepository` still exists alongside its four new siblings |
| Introduce a facade/aggregate repository (e.g. `IPlayerDataAccess` composing all five/nine narrower repositories) for callers that need several concerns at once | Callers needing multiple concerns take one injected parameter instead of several | Reintroduces exactly the "one wide surface every caller depends on regardless of what it uses" problem this split exists to fix, just one level removed; the backlog story's own explicit instruction ("don't build a facade unless call sites show a real need for one") ruled this out before it was tried | Rejected — call-site analysis showed every "needs multiple concerns" case was 2-4 narrow interfaces, not enough to justify a facade, and a facade would make the boundary between concerns opaque again |

## Consequences

- **Positive:** each new interface's own doc comment now states exactly
  which entity/table it owns and why (e.g. why
  `GetPlayersWithEitherAttributeAsync` lives on `IPlayerAttributeRepository`
  rather than `IPlayerRepository` despite returning `Player` rows); a
  caller's constructor signature now documents, at a glance, which of the
  four (soon five, post-S-107) player-data concerns it actually touches,
  rather than blanket-depending on all of them; the next repository this
  codebase adds has a smaller, more obviously-scoped precedent to follow
  than the pre-split `IPlayerStoreRepository` ever was.
- **Negative / trade-offs accepted:** several call sites now inject 2-4
  repository parameters where they previously injected one (e.g.
  `GridGameModule` takes `IPlayerStoreRepository` +
  `IPlayerRepository` + `IPlayerAliasRepository` +
  `IPlayerAttributeRepository`) — a small, deliberate increase in
  constructor-parameter count in exchange for each parameter's type now
  saying exactly what it's for. `IPlayerStoreRepository` and its four new
  siblings coexist until S-107 lands and the original file's remaining
  five concerns are split out too — a caller that only touches
  not-yet-moved methods is untouched by this story, and the original
  `PlayerStoreRepository.cs`/`IPlayerStoreRepository.cs` files are not
  deleted yet (S-107's job, once both halves have landed).
- **Follow-up:** S-107 splits the remaining five concerns
  (`IPlayerOverrideRepository`, a photo/position/birth-year backfill
  repository, `IPlayerCareerStintRepository`, and a data-quality-tracking
  repository for `IsConfirmedLowAsync`/`RecordConfirmedLowAsync`/
  `IsPersistentTechnicalFailureAsync`/etc.) out of the same original file;
  only once both stories have landed does `PlayerStoreRepository.cs`/
  `IPlayerStoreRepository.cs` get deleted.

## For AI agents

Do not merge any of `IPlayerRepository`/`IPlayerDataRepository`/
`IPlayerAttributeRepository`/`IPlayerAliasRepository`/
`IPlayerStoreRepository` back into a single interface, and do not add a
facade/aggregate repository composing them — a caller needing multiple
concerns injects multiple narrow repositories, per this ADR's own
"no facade unless a real need is shown" decision. Do not delete
`PlayerStoreRepository.cs`/`IPlayerStoreRepository.cs` until S-107 (the
independent second half of this split) has also landed and moved its own
five remaining concerns out. When adding a new method to any of these five
repositories, put it on the interface that owns the entity/table it
primarily queries or writes — a method that queries entity A to return
entity B (like `GetPlayersWithEitherAttributeAsync` querying
`PlayerAttribute` to return `Player`) belongs with A, the entity actually
driving the query, not with B, the entity in the return type — matching
the reasoning already applied to `GetPlayersWithEitherAttributeAsync`/
`CountPlayersWithBothAttributesAsync` (→ `IPlayerAttributeRepository`) and
`GetPlayersByNormalizedAliasAsync` (→ `IPlayerAliasRepository`) above.
