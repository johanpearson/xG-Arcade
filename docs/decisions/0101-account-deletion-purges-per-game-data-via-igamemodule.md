# ADR-0101: Account deletion purges per-game data through IGameModule, not a direct game repository dependency

- **Status:** Accepted
- **Date:** 2026-08-31
- **Related requirements:** REQ-710
- **Related components:** COMP-01 (Core.Users), COMP-15 (Games.XGPredict), COMP-05 (Games.XGGrid), COMP-11 (Games.XGPath)

## Context

S-201 (Epic 13) closed a gap flagged in S-197: `AccountDeletionService`
(Core.Auth, COMP-01) anonymized `Guess` rows on account deletion (REQ-710)
but never touched xG Predict's own `PredictMatchPrediction`/
`PredictPlayerLock` tables, which have no per-user handling at all today.

The first implementation gave `AccountDeletionService` a direct constructor
dependency on `IPredictInstanceRepository` — Games.XGPredict's (COMP-15) own
persistence — and called its two new anonymize/hard-delete methods
directly. A same-day quality-gate review (`architecture-reviewer`) flagged
this as a violation of ADR-0003's boundary rule ("Core references games
only via opaque `GameKey`/`GameInstanceId` pairs... Games reference Core
through `IGameModule`"): `Guess`/`IGuessRepository` is fine as an
`AccountDeletionService` dependency because `Guess` is Core.Scoring's own
entity (COMP-04, just physically hosted in `XGArcade.Data` per ADR-0014),
but `PredictMatchPrediction`/`PredictPlayerLock`/`IPredictInstanceRepository`
belong to Games.XGPredict, not Core. This is structurally the same
anti-pattern ADR-0100 was built to eliminate for the leaderboard's
round-total reads (`IRoundScoreSource`/`IRoundScoreSourceResolver`) — that
interface's own doc comment already states Core must never reference
`IPredictInstanceRepository` directly, and a future change that seems to
need that reference should stop and flag it rather than adding it.

Unlike ADR-0100's leaderboard case, account deletion doesn't need a new,
purpose-built abstraction: `IGameModule` (ADR-0003) already exists
specifically so Core can reach into a specific game's data without knowing
what that game is, and `IGameModuleResolver`'s constructor already takes
`IEnumerable<IGameModule>` — every registered game module is already
available via DI as a collection, no new resolver needed.

## Decision

Add `Task PurgeUserDataAsync(Guid userId, CancellationToken cancellationToken = default)`
to `IGameModule`. `AccountDeletionService` depends on
`IEnumerable<IGameModule>` instead of any specific repository, and calls
`PurgeUserDataAsync` once per registered module inside `DeleteAccountAsync`,
in the same position the direct `IPredictInstanceRepository` calls occupied
(right after `Guess` anonymization, before `LeagueMembership`/`User`
cleanup). Each module decides what, if anything, it owns to purge:
`GridGameModule`/`XGPathGameModule` are genuine no-ops (their only per-user
table, `Guess`, is Core's own and already handled directly);
`XGPredictGameModule` anonymizes `PredictMatchPrediction.UserId` and
hard-deletes `PredictPlayerLock` rows via its own, already-injected
`IPredictInstanceRepository` — the one place in the codebase allowed to
reference that repository directly, since `XGPredictGameModule` IS
Games.XGPredict.

Test coverage follows the same split: `AccountDeletionServiceTests`
(`XGArcade.Core.Tests`) uses `FakeGameModule` for every registered-module
slot and only proves the generic "every module's `PurgeUserDataAsync` gets
called" loop — it does not reference any game-specific project, matching
this test project's pre-existing state and the same "Core must never
reference a game module directly" discipline `FakeRoundScoreSource.cs`
(ADR-0100's own test-side precedent) already documents. The actual
anonymize/hard-delete behavior is proven in `XGPredictGameModuleTests`
(`XGArcade.Games.XGPredict.Tests`), which already legitimately depends on
`IPredictInstanceRepository`.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Direct `IPredictInstanceRepository` dependency on `AccountDeletionService` (the original S-201 implementation) | Simplest code, fewest files touched | Violates ADR-0003; Core.Auth gains compile-time knowledge of a specific game's schema; the exact anti-pattern ADR-0100 already named and forbade | Rejected by the same-day architecture review; not viable long-term as more games are added |
| A new `IUserDataPurger`-style abstraction, mirroring ADR-0100's `IRoundScoreSource`/`IRoundScoreSourceResolver` (resolved per `GameKey`) | Consistent with the ADR-0100 precedent for a purpose-built read abstraction | Account deletion isn't scoped to one `GameKey`/`Round` the way a leaderboard read is — it needs *every* registered game to get a chance to purge, not one resolved by key; a whole new resolver interface duplicates what `IEnumerable<IGameModule>` (already used internally by `GameModuleResolver`) does for free | `IGameModule` already fits this shape once given one more method — no new interface earns its keep here |
| Give `AccountDeletionService` its own hardcoded list of per-game cleanup actions (mirroring `GameHistoryPurger`'s accepted exception in ADR-0003's 2026-08-18 addendum) | No interface change | `GameHistoryPurger`'s exception is explicitly scoped to operational/maintenance CLI tooling outside the request-serving path; `AccountDeletionService` is exactly the request-serving code that exception says the original rule still applies to | Would misuse a narrowly-scoped exception for genuinely request-serving code |

## Consequences

- Positive: a third, fourth, etc. game that owns per-user data it must
  anonymize/delete on account deletion needs only to implement
  `PurgeUserDataAsync` — `AccountDeletionService`/Core.Auth needs no change,
  the same "adding a game touches no Core code" property ADR-0003 already
  established for round generation/scoring.
- Positive: no new resolver/interface introduced — `IGameModule` and the
  already-DI-registered `IEnumerable<IGameModule>` collection cover this
  need directly.
- Negative / trade-offs accepted: every `IGameModule` implementation now
  must implement `PurgeUserDataAsync`, even when it's a permanent no-op
  (xG Grid, xG Path today) — a small, explicit tax on adding a new game
  module, consistent with this interface's existing per-method judgment
  calls (e.g. `GetCellCategoryTypesAsync` throwing `NotSupportedException`
  for xG Path).
- Follow-up: `NotificationPreference` (REQ-710's other named per-user table)
  has no Tier 0 table yet (MVP-SCOPE.md) — when it's built, it's a
  Core-owned table (like `Guess`), so it's anonymized directly by
  `AccountDeletionService`, not through this mechanism.

## For AI agents

If code you are about to write would give `Core.*` a direct reference to
`IPredictInstanceRepository`, `PredictInstance`, `PredictMatch`,
`PredictMatchPrediction`, `PredictPlayerLock`, or any other
Games.XGPredict-owned type (or the equivalent for any other game module),
stop and flag it — the answer is almost always "add or reuse a method on
`IGameModule` and resolve through `IEnumerable<IGameModule>`/
`IGameModuleResolver`", not a new direct reference. This applies to test
projects too: `XGArcade.Core.Tests` must never take a project reference on
a specific game's assembly (`XGArcade.Games.XGGrid`, `XGArcade.Games.XGPath`,
`XGArcade.Games.XGPredict`) — use `FakeGameModule` instead, and put any
test that needs to prove a specific game module's real behavior in that
module's own test project.
