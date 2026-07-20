---
doc_id: architecture-document
title: Architecture Document
version: "0.43"
status: draft
last_updated: 2026-07-20
owner: Johan
related_docs:
  - requirements-document.md
  - implementation-document.md
id_prefix: ARCH
read_before: ["requirements-document.md"]
read_after: []
update_when:
  - "A container or component is added, removed, or its responsibility changes"
  - "A data flow between components changes"
  - "A cross-cutting concern (auth, logging, config) changes approach"
  - "An architecture decision is made — also add an ADR under docs/decisions/"
---

# Architecture Document – xG Arcade (working title)

Version 0.43 · 2026-07-20
References: `requirements-document.md`, `implementation-document.md`

> **Naming note:** "xG Arcade" is a placeholder for the overall product name.
> xG Grid is the first game hosted on it, not the platform itself — see
> `requirements-document.md` §0 for the full distinction. Every reference to
> a game-specific concept in this document (e.g. `GridInstance`) is scoped to
> xG Grid and must not leak into `Core.*` components.

> **For AI agents:** this document defines WHY the system is structured the
> way it is — component boundaries, responsibilities, and data flow. Read
> this before `implementation-document.md` when the task involves adding a
> new component, changing a boundary, or understanding how pieces fit
> together. If your change affects a boundary described here, update this
> document and add an ADR under `docs/decisions/`. Component IDs (`COMP-xxx`)
> are stable identifiers — reference them in code comments and commit
> messages where relevant.
>
> **This document describes the full system, not what's being built right
> now.** See `MVP-SCOPE.md` (repo root) for the actual build order — several
> components below (COMP-07's API-Football fallback client specifically —
> its Wikidata client is Tier 0, built in S-006 — COMP-10, the dev/prod
> split) are Tier 1, not needed to get a first playable version working.

## 1. Purpose and audience

This document describes the structural design of the platform: its major
components, their responsibilities, how they communicate, and the
architectural decisions behind them. It is the reference for anyone (human
or AI agent) who needs to understand where a piece of logic belongs before
writing code, not just how to write the code itself (that's
`implementation-document.md`).

## 2. Architectural style

- **Modular monolith**, not microservices. One deployable backend, internally
  divided into modules with clear boundaries (`Core`, game modules, `Data`,
  `DataSync`). Rationale: team size is one developer, operational overhead of
  microservices is not justified at this stage, and module boundaries can be
  extracted into services later if ever needed without a rewrite.
- **Pluggable game modules**: the platform's core (users, leagues, rounds,
  scoring) is game-agnostic. Each game (starting with the grid game)
  implements a shared `IGameModule` interface. This is the mechanism that
  satisfies the "platform for multiple games" requirement without
  over-engineering the first game.
- **Cache-first data strategy**: no bulk upfront data import; the player
  attribute store grows on demand (see `implementation-document.md` §2 and
  ADR-0001).

## 3. System context (C4 Level 1)

```
                     ┌───────────────┐
                     │     Player     │
                     └───────┬────────┘
                             │ plays, guesses, views leaderboards
                             ▼
                  ┌────────────────────┐
   ┌─────────────▶│    The xG Arcade     │◀─────────────┐
   │  reviews/     │  (hosts xG Grid  │   configures  │
   │  corrects data│   and future games) │   templates/  │
   │               └──┬───────┬───────┬──┘   schedules   │
   │                  │       │       │                   │
┌──┴───┐  live lookups│       │       │ auth + confirmation/  scheduled sync
│ Admin │             ▼       │       │ notification emails    ▼
└───────┘   ┌─────────────────┐ │     ▼                ┌───────────────┐
            │ External data   │ │  ┌─────────────┐     │  Scheduler    │
            │ sources (Wiki-  │ │  │ Email        │     │  (GitHub      │
            │ data, API-Foot- │ │  │ provider     │     │  Actions cron)│
            │ ball)           │ │  │ (Resend)     │     └───────────────┘
            └─────────────────┘ │  └─────────────┘
                                 ▼
                        ┌─────────────────┐
                        │ Auth provider    │
                        │ (Supabase Auth)  │
                        └─────────────────┘
```

**Actors and external systems:**

| Name | Type | Role |
|---|---|---|
| Player | Person | Plays rounds, submits guesses, views leaderboards |
| Admin | Person | Reviews unverified data, configures templates and round schedules |
| External data sources | External system | Wikidata, API-Football — source of player attribute data |
| Scheduler | External system | GitHub Actions cron — triggers round generation and sync jobs |
| Auth provider | External system | Supabase Auth — identity, session management, and account confirmation state |
| Email provider | External system | Resend — sends auth emails (via Supabase custom SMTP) and product notification emails (via direct API from Core.Notifications) — see ADR-0005 |

## 4. Containers (C4 Level 2)

| ID | Container | Responsibility | Tech |
|---|---|---|---|
| CONT-01 | Web Frontend | Renders grid, guess input, leaderboards, auth/account screens (login, signup, delete-account), admin review UI | TypeScript / React, hosted on Azure Static Web Apps |
| CONT-02 | Backend API | Business logic, request handling, scoring, orchestration | C# / ASP.NET Core, containerized, hosted on Azure Container Apps |
| CONT-03 | Database | Persists users, leagues, rounds, guesses, player data, overrides | PostgreSQL (Supabase); Supabase Auth also used for identity |
| CONT-04 | Sync Worker | Scheduled job that refreshes player data from external sources | C# console job, containerized, triggered by GitHub Actions |
| CONT-05 | Round Scheduler Job | Scheduled job that generates new Round + game-specific instance (e.g. a GridInstance for xG Grid) | C# console job / API endpoint, triggered by GitHub Actions |

Data flow between containers is always frontend → backend API → database; no
container other than the Backend API writes to the database directly, so
business rules (e.g. override precedence) are enforced in one place.

## 5. Components (C4 Level 3) — inside the Backend API

| ID | Component | Responsibility | Maps to (implementation doc) |
|---|---|---|---|
| COMP-01 | Core.Users | User accounts, auth integration | `XGArcade.Core` |
| COMP-02 | Core.Leagues | Global + custom leagues, membership | `XGArcade.Core` |
| COMP-03 | Core.Rounds | Round lifecycle, scheduling config | `XGArcade.Core` |
| COMP-04 | Core.Scoring | Uniqueness calculation, score locking | `XGArcade.Core` (`Scoring/` — `GuessSubmissionService`, added S-009) |
| COMP-05 | Games.XGGrid | Grid generation, category logic, `IGameModule` implementation for the xG Grid game. Also owns `PlayerCacheWarmingService` (REQ-110, S-036) — proactively warms COMP-06's cache for every reference Country×Club/Club×Club pair, run as its own CLI verb rather than an HTTP endpoint (ADR-0024) | `XGArcade.Games.XGGrid` |
| COMP-06 | Data.PlayerStore | PlayerData, PlayerOverride, PlayerAttribute, PlayerAlias; override-merge logic — see ADR-0015 for the exact precedence semantics (`HasEffectiveAttributeAsync`: an override replaces its entire attribute type for correctness-checking, not one value within it). `PlayerAlias` (known nicknames/stage names) is populated incrementally alongside `PlayerAttribute` — e.g. from Wikidata's `skos:altLabel`, fetched in the same intersection query as REQ-103's live lookup (S-006) — not bulk-imported like COMP-10's index; not yet queried for guess-time name matching either (REQ-208's Tier 0 status note). As of S-012, `XGArcade.Api.Admin.AdminEndpoints` is a second caller alongside the guess-submission path, reaching PlayerData/PlayerOverride only through `IPlayerStoreRepository`, same as any other caller — no new data-access path | `XGArcade.Data` |
| COMP-07 | DataSync.Clients | Wikidata/API-Football clients, live-lookup fallback | `XGArcade.DataSync` |
| COMP-08 | Core.Notifications | Sends product notification emails (round results) via Resend's API; owns notification preferences. Does not handle auth emails — those are Supabase Auth's responsibility, configured with custom SMTP. See ADR-0005 | `XGArcade.Core` |
| COMP-09 | Testing.SeedManager | Test-data creation/reset/scenario API. Registered only when the environment is not Production — see ADR-0006 | `XGArcade.Api` (conditionally registered), reaches other components' normal write paths, never a separate data path |
| COMP-10 | Data.PlayerNameIndex | Broad, bulk-imported name/alias index used only for autocomplete and as the candidate pool for name matching (REQ-207/208/209). Deliberately separate from COMP-06's narrow, incrementally-built validation cache, and from COMP-06's own `PlayerAlias` above — see ADR-0007 and boundary rule 5. **Built S-032:** `PlayerNameIndex` entity + `IPlayerNameIndexRepository`/`PlayerNameIndexRepository` live in `XGArcade.Data`; the bulk Wikidata importer (`PlayerNameIndexImporter`) lives in `XGArcade.DataSync` instead, alongside `WikidataLookupService` — `XGArcade.Data` has no project reference to `XGArcade.DataSync`, so a class needing both `IWikidataClient` and `IPlayerNameIndexRepository` can't live in `XGArcade.Data` | `XGArcade.Data` |

**"Maps to" column note (ADR-0014):** for COMP-01, COMP-03, COMP-04, and
COMP-05 specifically, this column names where each component's
*business/orchestration logic* lives — it does not mean every entity or
repository that component owns is physically defined in that project.
`User` (COMP-01), `Round` (COMP-03, `IRoundRepository`/`RoundRepository`,
added in S-008), `Guess` (COMP-04, `IGuessRepository`/`GuessRepository`,
added in S-009), and `GridTemplate`/`GridInstance`/`GridCell` (COMP-05) are
EF Core entities defined in `XGArcade.Data` alongside their repositories, in
the single shared `XGArcadeDbContext`, same as every other component's
persistence code — see ADR-0014 for why. The component boundary itself
(e.g. boundary rule 1) is enforced by which repository interfaces a
component is allowed to call, not by which `.csproj` the entity class sits
in.

**COMP-04 status (S-009/S-011):** `GuessSubmissionService`
(`XGArcade.Core.Scoring`) was COMP-04's first real code (S-009) —
REQ-201/202/210's guess-acceptance, guess-change-policy, and
attempt-cap/lock rules. As of S-011, COMP-04's namesake responsibility
("uniqueness calculation, score locking") is also built:
`UniquenessCalculator.Calculate` (REQ-204) is the one place the formula is
written, shared by the live read path (`GET /rounds/current`,
`XGArcade.Api.Rounds.RoundEndpoints`) and `IScoreLockingService`
/`ScoreLockingService` (REQ-205), which `Core.Rounds`' `RoundCloseService`
calls at round close to persist `FinalUniquenessScore`/`FinalPoints` for
every `Guess` in the round. As of S-018, the uniqueScore→points formula
itself was likewise extracted to a single shared method,
`ScoringRules.PointsFromUniqueScore` — `ScoreLockingService` calls it to
compute `FinalPoints` (REQ-205) and `RoundEndpoints` calls the same method
to compute a new live `LivePoints` field on `GET /rounds/current` (REQ-204
extension), so COMP-04 has exactly one place this formula is written, same
principle as `UniquenessCalculator.Calculate` above. `LivePoints` is
read-only and re-derived per request, never persisted, so it does not
change COMP-04's data-flow boundary — only the API response shape (§6).
**S-022 correction (ADR-0020):** `UniquenessCalculator.Calculate`'s formula
now excludes the guesser's own guess from both sides of the ratio — no
boundary or data-flow change, a pure formula-correctness fix within COMP-04
(see REQ-204's status note and ADR-0020 for the rationale).
**S-028 correction (ADR-0021 — lowest-wins scoring):** two changes, both
within COMP-04's existing boundary. First, `ScoringRules.PointsFromUniqueScore`
is now `round((1 - uniqueScore) * MaxPointsPerCell)` (was `round(uniqueScore
* MaxPointsPerCell)`) and an incorrect guess now locks at `FinalPoints =
MaxPointsPerCell` (was `0`) — both pure formula changes, no new data flow.
Second, `ScoreLockingService` gained a new step,
`MaterializeUnansweredCellsAsync`, run before locking: for each round
participant (a user with ≥1 `Guess` row in that round), any cell they never
attempted gets a synthesized `Guess` row scored the same as an incorrect
guess. This *does* touch COMP-04's boundary with COMP-05/`IGameModule` —
resolving "every cell id for this instance" requires a new
`IGameModule.GetCellIdsAsync(instanceId)` method, reached the same way
`GenerateInstanceAsync`/`ScoreSubmissionAsync` already are (via
`IGameModuleResolver`, keyed by the `Round`'s `GameKey`), never by COMP-04
reaching into `GridInstance`/`GridCell` directly (ADR-0003 unaffected).
**Frontend name-display fix (2026-07-12):** `GuessSubmissionService` now also
calls `IPlayerStoreRepository.GetPlayerByIdAsync` directly for a correct
guess, to return its canonical `Player.FullName` (`GuessSubmissionResult
.ResolvedPlayerName`) alongside the existing correctness/attempt-count
result — the frontend now shows this instead of the raw as-typed guess for
a correct answer, and no name at all for an incorrect one. This is a plain
by-ID lookup, not a new matching/correctness path, so it doesn't touch
boundary rule 5's autocomplete/correctness separation; `GET /rounds/current`
(`RoundEndpoints`, §6.2) gained the same field via a new bulk
`IPlayerStoreRepository.GetPlayersByIdsAsync`, for the same reason.
`ADR-0022`: `RoundGenerationService` (COMP-03) now also depends on
`IRoundCloseService` (COMP-03/COMP-04's existing extension point) —
see COMP-03's own status note below for what changed and why.
`ScoreCalculator.CalculateTotalPoints`
(REQ-206) sums `FinalPoints` for a given set of guesses; the leaderboard's
all-time total (COMP-02, below) recomputes the same formula database-side
rather than calling this directly (see `ScoreCalculator`'s own doc comment
for why). An architecture-reviewer pass during S-011 caught this logic
initially living in the wrong components (inline in `Core.Rounds`/the API
layer) and it was extracted into `Core.Scoring`/`Core.Leagues` before
merge — see COMP-02's status note below. `Guess.CellId` being a raw `Guid`
typed only as "opaque submission reference" in practice resolves to a real
`GridCell` — an accepted v1 simplification, same one
`implementation-document.md` §5 already documents on the `Guess` entity
itself.

**COMP-02 status (S-011):** `ILeaderboardService`/`LeaderboardService`
(`XGArcade.Core.Leagues`) is COMP-02's first real code — REQ-401's
auto-enrollment (`ILeagueRepository`/`LeagueRepository`, called from
`AuthController.Signup` right after the local `User` row is created) and
REQ-404's Tier 0 slice (`GET /leagues/global/leaderboard` →
`GetGlobalLeaderboardAsync`, the global league only — custom leagues,
REQ-402/403, are deferred per `MVP-SCOPE.md`). Same thin-endpoint/
owning-Core-service shape `GuessEndpoints` → `GuessSubmissionService`
already establishes.

**COMP-02 status (S-053/S-054, REQ-406/407/408, ADR-0031):** `LeaderboardService`
now depends on `IRoundRepository` (COMP-03) and a new
`ILiveRoundContributionService` (COMP-04, `XGArcade.Core.Scoring`) —
ADR-0031 already documented this coupling growth as an accepted
consequence of the "always recompute live, never cache" decision; it is
now real, not hypothetical. `GetGlobalLeaderboardAsync` takes a nullable
`Round? activeRound` (resolved by the Api layer, `LeaderboardEndpoints`,
via the same `IRoundRepository.GetActiveByGameKeyAsync` pattern
`RoundEndpoints` already uses — COMP-02 itself still never references
`GridGameModule`/`XGGridGameKey` directly, ADR-0003 intact, confirmed by
`architecture-reviewer`'s quality-gate pass) and folds
`ILiveRoundContributionService`'s live per-cell contribution on top of the
existing locked `SUM(FinalPoints ?? 0)` (REQ-406). Two new methods expose
the same underlying computation as standalone scopes:
`GetActiveRoundLeaderboardAsync` (REQ-407, participant-only) and
`GetClosedRoundsAsync`/`GetClosedRoundLeaderboardAsync` (REQ-408, locked,
gated on the new `Round.ClosedAt` column — see COMP-03's status note
below).

**COMP-01 status (S-017):** `User.NormalizedDisplayName` is COMP-01's first
uniqueness-enforcement logic (REQ-701) — a case-insensitive unique index
(`XGArcadeDbContext`) backing `IUserRepository.DisplayNameExistsAsync`'s
pre-check in `AuthController.Signup`, with `UserRepository.AddAsync`
catching the DB's own constraint violation
(`DisplayNameAlreadyInUseException`) as the race-safety net behind that
pre-check — the same "pre-check plus DB-level backstop" shape as the
existing `AuthProviderUserId` unique index, now applied to a field users
choose themselves. The migration adding the index also had to resolve any
pre-existing collision in already-seeded data before creating it; see
ADR-0019 for that one-time silent-rename strategy and its explicit revisit
trigger once real users exist.

**COMP-01 status (S-025):** `IAccountDeletionService`/`AccountDeletionService`
(`XGArcade.Core.Auth`) implements REQ-710 as reusable service logic, built
deliberately so `docs/backlog.md` S-026's admin-triggered deletion can call
it too, rather than growing a second implementation — it identifies its
target by local `User.Id`, never a JWT or password, so both a self-service
caller (resolves its own id first) and an admin caller (already has the
target id) can use it identically; any caller-specific confirmation step
stays in the calling endpoint. It reaches across component boundaries the
same way `AuthController.Signup` already does (`ILeagueRepository` directly,
per COMP-02) to remove `LeagueMembership` rows, plus `IGuessRepository`
(COMP-04) to anonymize `Guess` rows and `ISupabaseAuthClient` to delete the
Supabase Auth identity. `ISupabaseAuthClient.DeleteUserAsync` needed a new,
genuinely privileged secret (`Supabase:ServiceRoleKey`) that the existing
anon-keyed signup/login calls don't use — see ADR-0026 for why this didn't
need a second `HttpClient`/component boundary change, just one new
per-request header override and one new DI-supplied value.

**COMP-01 status (S-026):** the prediction in the note directly above came
true — `XGArcade.Api.Admin.AdminManagementEndpoints` (REQ-506) is now a
second caller of `IAccountDeletionService.DeleteAccountAsync`, alongside
`AuthController.DeleteAccount` (REQ-710), identifying its target the same
way that note anticipated (by local `User.Id`, resolved from an
admin-supplied email via new `IUserRepository.GetByEmailAsync`) — no second
deletion implementation was written. Separately, `AdminAuthorizationHandler`
(previously private `GetAdminUserIds`) gained a public static
`IsAdminUserId` helper, now also called by `AuthController.Me` so `GET
/auth/me`'s `MeResponse.IsAdmin` (REQ-504) reads `Admin:UserIds` the exact
same way the "Admin" authorization policy itself does — one check, two
callers, never two independently-maintained ones.

**Boundary rule 1 (data access):** COMP-05 (and any future game module) may
only reach player data through COMP-06's public interface. It must never
query `PlayerData`/`PlayerOverride` directly — this keeps the
override-precedence rule (REQ-501) enforced in exactly one place (see
ADR-0015 for the exact precedence semantics that single place enforces).
If a new game module needs a different kind of data store, that's a signal
for an ADR, not a workaround. `GridGameModule.ScoreSubmissionAsync` (S-009,
extended S-011/ADR-0018) respects this rule: it reaches player data only
through `IPlayerStoreRepository.GetPlayersByNormalizedFullNameAsync`/
`HasEffectiveAttributeAsync`, never a direct `PlayerAttribute`/
`PlayerOverride` query — and its REQ-211 live-lookup fallback reaches
`IPlayerStoreRepository` only indirectly, through COMP-07's
`IWikidataLookupService.LookupAndPersistAsync` (the same call
`GenerateInstanceAsync` already makes), never bypassing it.

**Boundary rule 2 (Round genericity):** `Core.Rounds` (COMP-03) must never
hold a foreign key to a game-specific entity such as `GridInstance`. A
`Round` references a game instance only via an opaque pair —
`GameKey` (e.g. `"xg-grid"`) and `GameInstanceId` (a `Guid` with no
type Core understands). Resolving that ID into an actual `GridInstance` is
the responsibility of the owning game module (COMP-05), reached through
`IGameModule`. This is what makes it possible to add a second game later
without changing `Core.Rounds` at all — see ADR-0003. **Narrow, documented
exception (ADR-0016, S-010):** `GET /rounds/current`
(`XGArcade.Api.Rounds.RoundEndpoints`, REQ-303) reads `GridInstance`/
`GridCell` directly via `IGridInstanceRepository`, bypassing `IGameModule`,
for display purposes only — never for generation or scoring, which must
still always go through `IGameModule`. See ADR-0016 for why (no second game
module exists yet to design a real generic read method against) and its
explicit trigger for revisiting this.

**Boundary rule 3 (email separation):** Auth-lifecycle emails (signup
confirmation, password reset) are never sent by `XGArcade.Core` code — they
are Supabase Auth's responsibility, configured with custom SMTP. Conversely,
product notification emails (round results) are never routed through
Supabase Auth or an auth hook — they are sent directly by Core.Notifications
(COMP-08) via Resend's API. See ADR-0005 for why these stay separate.

**Boundary rule 4 (test-data isolation):** Testing.SeedManager (COMP-09)
must create and reset data only by calling other components' normal
public interfaces (e.g. Core.Rounds' round-creation logic, Core.Leagues'
league-creation logic) — never by writing directly to tables through a
separate path. This guarantees test data is always structurally valid
exactly like real data, and that a business-rule change only needs to be
implemented once. See ADR-0006.

**Boundary rule 5 (autocomplete/correctness separation):** Autocomplete
specifically (typeahead suggestions shown before submission) queries only
`Data.PlayerNameIndex` (COMP-10) — never COMP-06, at all, for any reason.
Correctness-checking a submitted guess (REQ-203) queries only
`Data.PlayerStore` (COMP-06, which includes `PlayerAlias`), never COMP-10.
These two paths must never be merged — doing so would leak answer validity
through autocomplete. See ADR-0007.

This is a stricter rule than "name matching only ever touches one of the
two" — REQ-208's post-submission candidate-resolution step (implementation-
document.md §6's `normalize()` pseudocode) deliberately reads *both*
`PlayerNameIndex` (COMP-10, the candidate pool) and `PlayerAlias` (COMP-06,
alongside `PlayerAttribute`) together to resolve a submitted name to a
candidate player. That's the documented design, not a violation of this
rule: `PlayerAlias` is never read for autocomplete (upholding the rule
above), and `PlayerNameIndex` is never used to *determine* correctness
(candidates it returns still have to satisfy the cell's categories via
COMP-06 before a guess is accepted, same as any other candidate). The
boundary this rule protects is "nothing autocomplete shows implies
correctness" — not "COMP-06 and COMP-10 may never be read in the same
request."

## 6. Key data flows

**6.1 Grid generation flow** (realizes REQ-101, REQ-102, REQ-103, REQ-109)

**Tier 0 status (S-008):** `Core.Rounds`/COMP-03 now exists and the diagram
below is real end to end: `generate-round.yml`'s cron calls
`POST /internal/generate-round` (`XGArcade.Api.Rounds.InternalRoundEndpoints`,
bearer-token-protected, registered in every environment — CONT-05's actual
realization is "API endpoint," not a separate console job), which resolves
a `GridTemplate`, calls `RoundGenerationService.GenerateNextRoundIfNeededAsync`
(REQ-301's one-round-ahead rule, via the new `IGameModuleResolver`), and
that service creates the `Round` itself once `GridGameModule
.GenerateInstanceAsync` (`IGameModule`, COMP-05) succeeds — matching the
diagram's last line exactly.

**ADR-0022 addition (2026-07-12):** `RoundGenerationService` now also closes
a round via `IRoundCloseService` (a new constructor dependency) — this same
`generate-round.yml` cron is Tier 0's only production-scheduled trigger
point, so REQ-205's score-locking (§6.2) now actually runs in the deployed
environment, not only via the non-Production test-data endpoint. The round
closed is never `latest` itself but its predecessor (`IRoundRepository
.GetPreviousByGameKeyAsync`, new) — see ADR-0022 for why "latest" is the
wrong round to check, and REQ-205's status note for the leaderboard-facing
effect.

**COMP-03 status (S-054, REQ-408, executing ADR-0022's own anticipated
follow-up):** `Round` gained a nullable `ClosedAt` column (`AddRoundClosedAt`
migration) — the explicit revisit ADR-0022's "Follow-up" section already
named ("if a past-round-detail screen is ever built... revisit adding an
explicit `Round.ClosedAt` column then"). No new ADR needed; this is that
decision executing, not a new one. `RoundCloseService.CloseRoundAsync` sets
it once, first-close-wins, only *after* `IScoreLockingService
.LockRoundScoresAsync` completes successfully — never before or
concurrently, so a reader can never observe `ClosedAt` set while some
guesses in that round still have `FinalPoints == null` (a real ordering bug
caught by `quality-architect`'s S-054 quality-gate pass and fixed before
merge). COMP-02's `GetClosedRoundsAsync`/`GetClosedRoundLeaderboardAsync`
gate purely on this column.

**COMP-03 status (S-026):** `XGArcade.Api.Admin.AdminManagementEndpoints`
(REQ-505) is now a third caller of `IRoundCloseService.CloseRoundAsync`,
alongside `RoundGenerationService` above and REQ-806's non-Production-only
`/internal/test-data/force-close-round/{roundId}` — reached only through
that existing interface plus `IRoundRepository` (to find the caller's own
active round and, for the new "adjust end_time" action, to load and save
it), never a new data-access path. This is also the first non-test-only,
admin-facing use of ADR-0006's fail-closed "endpoint group not registered
at all outside non-Production" pattern — until now that pattern only
gated `XGArcade.Testing`/`InternalRoundEndpoints` (COMP-09); its scope of
use has grown, not its shape (see §7's Authorization row).

Two things from the S-007-era version of this note did **not** resolve the
way that note predicted:

- `POST /internal/grid/generate` (S-007's temporary endpoint) was
  **deliberately kept, not retired.** It still exercises grid generation in
  isolation from round scheduling for manual testing, and its existing test
  coverage (`GridEndpointTests.cs`) was no reason to discard. It remains
  non-Production-only (ADR-0006-style gating), unlike the new
  `/internal/generate-round`.
- The new, production-intended `/internal/generate-round` endpoint's own
  template resolution still bypasses `IGameModule`: it calls
  `IGridInstanceRepository` directly (via a shared `GridTemplateResolver`
  helper, factored out of S-007's endpoint so both share one
  find-or-create-by-size implementation) to find-or-create a `GridTemplate`
  by a configured size, the same shortcut S-007's endpoint already took.
  This is not a new boundary violation — `GridTemplate` isn't player data,
  and no boundary rule forbids the API layer from reaching it directly —
  but it means the gap this note originally framed as "temporary until
  S-008" has actually carried forward into the production-intended
  endpoint rather than closing. There is still no admin-driven
  `GridTemplate` management (REQ-102's full scope) for either endpoint to
  route through instead.

What is built and matches the diagram: reference-table-only candidate
selection (`Data.PlayerStore`/COMP-06 → `CountryDefinition`/`ClubDefinition`,
ADR-0012), cache-first-then-live-lookup per combination (S-006's
Wikidata-only half — no API-Football leg, see REQ-103's status note), and
persistence of the resulting `GridInstance`/`GridCell`s and the chaining
`Round`. Scoped further to Tier 0 (`MVP-SCOPE.md`): every grid is either
Country × Club or, as of `docs/backlog.md` S-030, Club × Club — never
Country×Country (REQ-107), never Trophy (REQ-108, deferred). Which of the
two allowed pairings a given instance uses is chosen once per
`GenerateInstanceAsync` call (`GridGameModule.SelectPairing`) — randomly
whenever the seeded reference data can support either, deterministically
falling back to whichever single pairing is feasible otherwise. REQ-107's
Country×Country ban is enforced by `CategoryPairingRules.IsAllowedPairing`,
checked once per `PickHeadersAsync` call (invariant for that call, since
every candidate in one call shares the same two category types) — not by a
fixed-axis assumption baked into the code.

**Explicit rule, not just implied by the diagram below:** every live
lookup this round's cells will ever need to reach `MinValidAnswers` happens
*during generation*, before `Round` (the thing players can actually
see/play) is created at all — a `Round` only exists once every cell has
enough cached matches to clear REQ-101's threshold. This was originally
read as making a "local DB only, no guess-time Wikidata fallback"
answer-checking strategy defensible, on the theory that "enough cached
matches" meant "every true match already cached." **That theory was wrong
in practice (ADR-0010's predicted gap, confirmed 2026-07-10, ADR-0018):**
clearing the threshold only proves *some* valid answers exist, not that
every one does, so a real player can still be missing from the cache for a
cell that's otherwise valid. REQ-211's guess-time fallback (below, S-011
follow-up) exists precisely to cover that remaining gap — see 6.2.

```
Round Scheduler Job (COMP-03)
  → Games.XGGrid (COMP-05): "generate instance for template X"
    → Data.PlayerStore (COMP-06): pick candidate row/column values from
      CountryDefinition/ClubDefinition/TrophyDefinition (ADR-0012) —
      never derived ad hoc from whatever's already in PlayerAttribute
    → Data.PlayerStore: query candidate combinations
      → [miss] DataSync.Clients (COMP-07): live lookup — Wikidata first
        (timeout-bounded, using the category values' resolved WikidataQid;
        skipped entirely if either value's QID is still null), API-Football
        only as fallback if Wikidata doesn't resolve it (ADR-0011);
        API-Football calls count against the shared daily counter (ExternalApiUsage)
        → Data.PlayerStore: persist as verified (ADR-0029, a routine
          generation-time sync, WikidataLookupOrigin.Sync; as of
          ADR-0032/2026-07-20, REQ-211's guess-time fallback in §6.2 below
          also persists as verified — the two origins are no longer
          distinguished by `Confidence`, see ADR-0032)
    → Games.XGGrid: assemble GridInstance once all cells valid, return its ID
  → Core.Rounds (COMP-03): create Round with GameKey="xg-grid",
    GameInstanceId=<the returned ID> — Core never sees the GridInstance shape
```

**6.1a Club addition and external ID resolution** (realizes REQ-109, ADR-0012)

```
[admin-triggered, one time per new club — not per grid, not per guess]
Admin → Web Frontend (admin view) → Backend API: add new ClubDefinition
  → DataSync.Clients (COMP-07): resolve Wikidata QID (entity search) and
    API-Football team ID (team search), best-effort
  → Data.PlayerStore: persist ClubDefinition with whatever was resolved —
    a still-null QID or team ID is a valid state, not an error (REQ-109);
    the live-lookup waterfall degrades gracefully around it
```

**6.2 Guess submission and scoring flow** (realizes REQ-201–REQ-206, REQ-207–REQ-211)

**Tier 0 status (S-009/S-011):** the diagram below is the full/long-term
shape. What's actually built and real end to end: `POST
/rounds/{roundId}/cells/{cellId}/guesses` (`XGArcade.Api.Guesses
.GuessEndpoints`) → `GuessSubmissionService` (`Core.Scoring`, COMP-04) →
`GridGameModule.ScoreSubmissionAsync` (`Games.XGGrid`, COMP-05) →
`Guess` persisted (`XGArcade.Data`), with correctness shown immediately
and an immediate lock on a correct answer or on the 2nd attempt. As of
S-011, the live-uniqueness and round-close-lock legs (below) are also real
end to end: `GET /rounds/current` computes `UniquePercent` live via
`UniquenessCalculator` (and, as of S-018, a `LivePoints` estimate alongside
it via `ScoringRules.PointsFromUniqueScore`), and `RoundCloseService`
(`Core.Rounds`) calls
`IScoreLockingService` (`Core.Scoring`) at round close to persist
`FinalUniquenessScore`/`FinalPoints` for every `Guess` in the round.

Several lines below do not match Tier 0's actual implementation, all
deliberate per `MVP-SCOPE.md`, not bugs:

- **The `Data.PlayerNameIndex`/autocomplete leg is now built (S-032,
  ADR-0007, pulled forward from Tier 1 by deliberate choice).** COMP-10
  exists: `PlayerNameIndex` (keyed on `PlayerId`, `HasIndex(NormalizedName)`),
  `IPlayerNameIndexRepository`/`PlayerNameIndexRepository` (a repository
  deliberately separate from COMP-06's `IPlayerStoreRepository` — never
  merged, per boundary rule 5), and `GET /players/autocomplete?query=&limit=`
  (`XGArcade.Api.Players.PlayerAutocompleteEndpoints`, bearer-token
  authenticated). Populated by `PlayerNameIndexImporter`
  (`XGArcade.DataSync.Wikidata` — not `XGArcade.Data/Seeding`, despite
  living alongside `ReferenceDataSeeder`/`StaleClubAttributeCleaner`
  conceptually: `XGArcade.Data` has no project reference to
  `XGArcade.DataSync`, only the reverse, so a class needing both
  `IWikidataClient` and `IPlayerNameIndexRepository` must live in
  `XGArcade.DataSync`, same as the existing `WikidataLookupService`), run via
  the `import-player-name-index` CLI verb (ADR-0024), workflow_dispatch-only,
  no schedule yet. This is REQ-207's suggestion-list data path only — REQ-208's
  alias/fuzzy-matching for guess *scoring* and REQ-209's disambiguation UI
  remain not built, so REQ-211's live-lookup trigger (below) still does not
  consult `PlayerNameIndex`.
- **"Core.Rounds: validate round is active, guess-change policy" is
  attributed to the wrong component in this diagram** even in what Tier 0
  built: it is `GuessSubmissionService` (COMP-04, not COMP-03) that reads
  the `Round` row (via `IRoundRepository`) and performs both checks itself,
  before resolving the owning `IGameModule`. `Core.Rounds` exposes no
  guess-validation method of its own — COMP-04 reaches `Round` data
  directly, the same way it's always been described as allowed to (Round
  is a Core-owned table, not a game-specific one; no boundary rule
  restricts this the way boundary rule 1 restricts player-data access).
  This line should read `Core.Scoring` once this diagram is next revised.
- **"Games.XGGrid: reject immediately if this cell is already correct, or
  if 2 attempts are already used (REQ-210)" is also mis-attributed.** This
  check happens entirely in `GuessSubmissionService` (COMP-04) *before*
  `Games.XGGrid` is ever called at all — `Games.XGGrid` is only reached
  once REQ-210's checks have already passed. Matches the acceptance
  criteria's substance ("checked before any name resolution work"), just
  not this diagram's component attribution.
- Name resolution is real but much narrower than described: `normalize +
  alias + fuzzy match against Data.PlayerNameIndex (REQ-208)` should read
  "normalize (lowercase/diacritics/punctuation only) and look up exact
  matches against `Player.NormalizedFullName` via `Data.PlayerStore`
  (COMP-06) directly" — no alias matching, no fuzzy tolerance, and no
  `PlayerNameIndex`/COMP-10 involved in matching at all (REQ-208's own
  status note).
- The disambiguation branch ("more than one → return a disambiguation
  prompt") is not built — Tier 0 auto-accepts the lowest-`Id` fitting
  candidate and logs a warning instead (REQ-209's status note); there is
  no disambiguation prompt or extra round-trip.
- **REQ-211's live-lookup branch is now partially built (S-011 follow-up,
  ADR-0018), pulled forward from Tier 1 once its documented MVP-SCOPE.md
  trigger fired.** `GridGameModule.ScoreSubmissionAsync` falls back to
  re-running the cell's own Wikidata intersection query
  (`DataSync.Wikidata.WikidataLookupService`, the same call
  `GenerateInstanceAsync` uses) whenever cached data doesn't already answer
  the guess, then re-checks. As of `docs/backlog.md` S-030, this covers
  both pairings a grid can actually be generated with — a Country×Club cell
  (`LookupAndPersistAsync`) and a Club×Club cell
  (`LookupAndPersistClubClubAsync`) — dispatched from one shared
  `LookupLiveMatchesAsync` helper also used by generation-time matching
  (`GetMatchCountAsync`), so the two call sites can't drift on which
  pairings are handled; any other pairing (e.g. a future Trophy cell) fails
  closed rather than throwing. This differs from the diagram's full shape in
  one deliberate way: the trigger is "cached data didn't resolve this
  guess," not "guess matched a `Data.PlayerNameIndex` candidate" — even
  though `PlayerNameIndex`/COMP-10 (REQ-207) is now built (S-032), REQ-211's
  guess-time live-lookup trigger does not consult it yet (that wiring is
  REQ-208/209's still-deferred candidate-resolution step, not this story's
  scope), and ADR-0018 explains why Tier 0 doesn't need it as a prerequisite
  here (Wikidata has no scarce daily budget to protect, unlike
  API-Football). There is still no API-Football fallback leg or
  `ExternalApiUsage` budget-gating for this call site, same as REQ-103's
  status.
- "Core.Scoring: compute live uniqueness on read, not on write" **is now
  built (S-011, extended S-018)** — `GET /rounds/current` computes
  `UniquePercent` on every request via `UniquenessCalculator.Calculate`,
  for any cell the requesting player has correctly guessed, plus (as of
  S-018) a `LivePoints` estimate derived from that same `UniquePercent` via
  `ScoringRules.PointsFromUniqueScore`. One attribution correction versus
  the diagram: this line is drawn as part of the guess-submission response
  path, but the actual read happens on `GET /rounds/current`
  (`XGArcade.Api.Rounds.RoundEndpoints`), a separate request — the guess
  submission response itself (`POST .../guesses`) does not include
  `UniquePercent` or `LivePoints`, only the next `GET /rounds/current` does.
- The final `[scheduled, at Round.EndTime]` block (locking
  `FinalUniquenessScore`/`FinalPoints`) **is now built (S-011), and its
  scheduled trigger is now real too (ADR-0022).** `RoundCloseService`
  (`Core.Rounds`) calls `IScoreLockingService`
  (`Core.Scoring`), which persists `FinalUniquenessScore`/`FinalPoints` for
  every `Guess` in the round. **Correction (ADR-0022):** this diagram
  previously said no automated job called round-close in production —
  `RoundGenerationService.GenerateNextRoundIfNeededAsync` (the one piece of
  code `generate-round.yml`'s cron actually invokes) now closes a round's
  predecessor before deciding whether to generate a successor, so this leg
  runs for real, on the same schedule REQ-301's generation already runs on
  — not a second scheduled job. REQ-806's non-Production-only
  `POST /internal/test-data/force-close-round/{roundId}` still exists too,
  for manual/E2E use (REQ-205's status note has the full picture, including
  the trade-off accepted for a pre-existing backlog of never-closed rounds
  from before this fix). **S-028 addition (ADR-0021):** this block now has a step before
  locking, not shown in the pre-S-028 diagram below — `ScoreLockingService`
  first calls `MaterializeUnansweredCellsAsync`, which reads the `Round`
  via the newly-added `IRoundRepository` dependency to resolve its
  `GameKey`/`GameInstanceId`, resolves the owning game module via the also
  newly-added `IGameModuleResolver` dependency, and calls
  `IGameModule.GetCellIdsAsync(instanceId)` (COMP-05) to find, for each
  round participant, any cell they never attempted — inserting a
  synthetic, worst-case-scored `Guess` row for each one before the existing
  lock-every-`Guess`-in-the-round step runs. This is `Core.Scoring`'s first
  dependency on `Core.Rounds`/COMP-05 data at round-close time (previously
  this leg only wrote to `Database`); see COMP-04's status note in §5 for
  the full rationale.

```
Player → Web Frontend: types a guess
  → Data.PlayerNameIndex (COMP-10): autocomplete suggestions — a broad
    pool, never sourced from COMP-06 (REQ-207)
Player → Web Frontend → Backend API: POST guess (selected/typed name)
  → Core.Rounds: validate round is active, guess-change policy;
    resolve GameKey to find the owning game module (ADR-0003)
  → Games.XGGrid (COMP-05): reject immediately if this cell is already
    correct, or if 2 attempts are already used (REQ-210) — checked before
    any name resolution work, not after
  → Games.XGGrid: resolve the name to a candidate player
    → normalize + alias + fuzzy match against Data.PlayerNameIndex (REQ-208)
    → if multiple candidates match the name, check each against the cell's
      categories via Data.PlayerStore (COMP-06): one match → accept it;
      more than one → return a disambiguation prompt (REQ-209, doesn't
      consume an attempt until resolved); none → incorrect
    → single-candidate case: check against Data.PlayerStore (effective
      data, override-aware)
      → if Data.PlayerStore has NO record at all for this player against
        these category types: DataSync.Clients (COMP-07) performs a live
        lookup — Wikidata first (timeout-bounded), API-Football only as a
        fallback if Wikidata doesn't resolve it (ADR-0011) — checking the
        shared API-Football daily counter only on that fallback path
        → resolved (either source): result persisted immediately as
          verified PlayerData (REQ-211; ADR-0032 as of 2026-07-20 — this
          leg persisted as unverified until then, see ADR-0029/ADR-0032)
          — same request, not deferred
        → unresolved (Wikidata failed AND API-Football budget exhausted or
          also unresolved): fail closed, evaluate as incorrect using only
          existing cached data
  → correctness shown to the player immediately (REQ-203) — if correct,
    the cell locks now, regardless of the round's remaining time
  → Core.Scoring: compute live uniqueness on read, not on write
  → Database: persist Guess (AttemptCount incremented, IsCorrect set)

[triggered by generate-round.yml's cron, ADR-0022 — the same schedule
 REQ-301's round generation already runs on, not a separate job]
Round Scheduler Job (Core.Rounds, via RoundGenerationService) → Core.Rounds
  (via IRoundRepository.GetPreviousByGameKeyAsync): find the round this
  invocation is about to supersede
  → Core.Scoring: lock final scores for all guesses in that round
  → Core.Rounds (via IRoundRepository): resolve the round's GameKey/GameInstanceId
  → Games.XGGrid (COMP-05, via IGameModule.GetCellIdsAsync, ADR-0003): resolve
    every cell id for the instance
  → for each round participant, synthesize a worst-case-scored Guess row
    (ADR-0021) for any cell they never attempted
  → Database: persist FinalUniquenessScore / FinalPoints
```

**6.2a Global leaderboard flow** (realizes REQ-401, REQ-404 — Tier 0 slice
only, added S-011)

```
Person → Web Frontend → Backend API: POST /auth/signup (new account)
  → Core.Users (COMP-01): create User row (includes DisplayName)
  → Core.Leagues (COMP-02): GetOrCreateGlobalLeagueAsync (idempotent
    singleton, filtered unique index on League.Type="global"), then
    AddMembershipAsync — "requires no action from the user" (REQ-401) is
    enforced by this happening automatically inside signup, not a
    separate step

Player → Web Frontend → Backend API: GET /leagues/global/leaderboard
  → Backend API (LeaderboardEndpoints): resolve the currently active round,
    if any (IRoundRepository.GetActiveByGameKeyAsync, same REQ-303 pattern
    RoundEndpoints uses — the Api layer is the one place allowed to
    hardcode GridGameModule.XGGridGameKey, ADR-0003; Core.Leagues below
    never does)
  → Core.Leagues (COMP-02): GetGlobalLeaderboardAsync(activeRound)
    → Core.Leagues' own persistence: member user ids for the global league
    → Core.Users (COMP-01): resolve each member's DisplayName
    → Core.Scoring (COMP-04): each member's all-time SUM(FinalPoints ?? 0)
      — computed database-side (GroupBy), not by re-summing REQ-206's
      per-round ScoreCalculator in memory (see ScoreCalculator's own doc
      comment on why these two call sites intentionally reimplement the
      same formula at different scopes)
    → Core.Scoring (COMP-04, ILiveRoundContributionService, S-053/REQ-406/
      ADR-0031, if activeRound is not null): the active round's per-cell
      live contribution, folded on top of the locked SUM above — recomputed
      fully in memory on every single request, no caching/snapshot anywhere
      in this path (ADR-0031)
  → sorted ascending by combined total (ADR-0021: lowest wins), then ranked
    and sliced into a `cursor`/`pageSize`-bounded page in memory (REQ-607,
    S-034) — the locked SUM is still database-side, but the live
    contribution, ranking, and pagination are not; see
    implementation-document.md §6 and ADR-0031 for why this is an accepted
    tradeoff, not a boundary change

Player → Web Frontend → Backend API: GET /leagues/global/leaderboard/active-round
  (S-053, REQ-407) → Core.Leagues (COMP-02): GetActiveRoundLeaderboardAsync
  → same ILiveRoundContributionService call as above, exposed as its own
    standalone, participant-only, always-live scope instead of folded onto
    the locked total — 404 ("No active round") when none exists

Player → Web Frontend → Backend API: GET /leagues/global/leaderboard/closed-rounds[/{roundId}]
  (S-054, REQ-408) → Core.Leagues (COMP-02): GetClosedRoundsAsync /
  GetClosedRoundLeaderboardAsync → Core.Rounds (COMP-03, via
  IRoundRepository, gated on the new Round.ClosedAt column — see COMP-03's
  status note above) for the browsable round list, then Core.Scoring
  (COMP-04) for that one round's locked, never-recomputed
  SUM(final_points) — REQ-206's own formula, filtered to a single round;
  404/409 distinguish "round not found" from "round not closed yet"
```

Custom leagues (REQ-402/403 — create/join via invite code) are not built;
this flow only ever has the one global league to read. All three routes
above share SCREEN-03 as a single leaderboard surface with a scope selector
("All-time" / "Current Round" / "Previous Rounds" — renamed 2026-07-20,
S-056, from "This round (live)"/"Past rounds"; purely cosmetic, no REQ
specifies exact tab wording), not three separate screens (REQ-407/408's
own resolved UX placement decision).

**6.3 Data sync flow** (realizes REQ-501, REQ-502, REQ-503)

```
Sync Worker (CONT-04) → DataSync.Clients (COMP-07): fetch updates
  → Data.PlayerStore (COMP-06): write to PlayerData (never PlayerOverride)
  → [merge on read] effective value = PlayerOverride if present, else PlayerData
Admin → Web Frontend (admin view) → Backend API: approve/correct unverified data
  → Data.PlayerStore: create PlayerOverride or mark PlayerData verified
```

**Tier 0 status (S-012):** the top half (sync writes PlayerData, merge-on-read
prefers PlayerOverride) predates this story. This story built the bottom
half's backend leg only, and only part of it: `XGArcade.Api.Admin
.AdminEndpoints`, behind the new "Admin" authorization policy (§7 below),
reaches `Data.PlayerStore` (COMP-06) exclusively through its existing
`IPlayerStoreRepository` interface — no new data-access path, consistent
with the COMP-06 boundary rule. `GET /admin/player-data/unverified` lists
candidates; `POST/GET/PUT/DELETE /admin/player-overrides[/{id}]` covers
"create PlayerOverride". "Mark PlayerData verified" and "remove the data
point" are not built — there is no way to flip a `PlayerData` row's
`Confidence` or delete it via any endpoint yet (see REQ-503's status note
at the time — since superseded, see the 2026-07-20/S-057 note below).
No "Web Frontend (admin view)" exists — the Admin actor above reaches the
Backend API directly (e.g. via a REST client), not through a UI.

**S-026/ADR-0029 status:** "Web Frontend (admin view)" now exists
(`AdminScreen.tsx`, SCREEN-04 — REQ-504, also covered by this story's own
COMP-01/COMP-03 status notes above and §6.8's account-deletion status note
below). Once it had a real caller, `GET /admin/player-data/unverified`
turned out to return 52,782 rows — every sync since S-006, since the top
diagram line's "write to PlayerData" had persisted every row `Confidence =
"unverified"` unconditionally, not merely "unverified until an admin
acts." ADR-0029 narrows that: only REQ-211's guess-time fallback (§6.2)
still writes `Confidence = "unverified"`; a routine sync (this section's
top line, and `PlayerCacheWarmingService`) now writes `"verified"`
directly, via a new `WikidataLookupOrigin` parameter on
`IWikidataLookupService`. A one-time CLI verb
(`verify-wikidata-player-data`) bulk-flipped the pre-existing backlog to
match, since no persisted row records which of these two paths originally
created it. At this point, "Mark PlayerData verified via an endpoint" and
"remove the data point" still remained unbuilt, per the note above — this
change addressed the queue's *size*, not that missing action.

**2026-07-20/ADR-0032/S-057 status:** ADR-0032 supersedes ADR-0029's
fallback-specific carve-out — REQ-211's guess-time fallback (§6.2) now
also persists `Confidence = "verified"` directly, so no code path writes
`"unverified"` anymore (until a real player-suggestion/correction channel
exists, per both ADRs' shared follow-up note). Separately, the same
2026-07-20 batch of work finally builds "Mark PlayerData verified via an
endpoint," closing half of the gap the two status notes above flagged:
`POST /admin/player-data/approve` (`XGArcade.Api.Admin.AdminEndpoints`,
Admin policy, REQ-503's 2026-07-20 extension) takes one or more
`PlayerData` ids and flips each independently to `verified`, reached
through the same `IPlayerStoreRepository` (COMP-06) interface as every
other caller — `PlayerStoreRepository.ApprovePlayerDataAsync`, no new
data-access path. Audit fields (`PlayerData.ApprovedByAdminId`/
`ApprovedAt`) mirror `PlayerOverride`'s existing `LockedByAdminId`/
`LockedAt` shape rather than a separate audit-log table. "Remove the data
point" is still not built and remains out of scope. Because the review
queue is now empty by construction going forward (ADR-0032), this new
endpoint's practical caseload is limited to whatever
`unverified`-at-write-time backlog exists from before that ADR shipped,
plus any future player-suggestion channel once one exists.

**6.4 Signup and email confirmation flow** (realizes REQ-701–REQ-705)

**Tier 0 status (S-004, ADR-0013):** only the flow's first leg is built —
backend-mediated signup/login, not the full confirmation loop described
below. `POST /auth/signup`/`POST /auth/login` on `XGArcade.Api`'s
`AuthController` proxy Supabase Auth's REST API directly (the frontend
never calls Supabase itself), and `GET /auth/me` is protected by JWT
bearer middleware validated against Supabase's JWKS endpoint (ADR-0017;
originally assumed a static shared secret, corrected after a real
deployment's tokens — signed with Supabase's rotating asymmetric JWT
Signing Keys — failed that assumption). Supabase's
confirm-email requirement is turned off for Tier 0 (per `MVP-SCOPE.md`), so
`Core.Users`' `User.EmailConfirmed` is hardcoded `true` at creation time —
nothing yet sets it to `false` or checks it. The diagram below is the
full/long-term design; the "Player clicks link OR enters code" leg, the
Resend confirmation email itself, and the REQ-702 unconfirmed-account
block are **not yet built** (REQ-702–705 remain deferred). See ADR-0013
for the backend-mediation decision and its `Auth:Mode=local-e2e` test-only
branch (gated to `ASPNETCORE_ENVIRONMENT=Development`, never active
otherwise).

```
Player → Web Frontend → Backend API: POST create account
  → Auth provider (Supabase Auth): create unconfirmed identity
  → Auth provider → Email provider (Resend, via custom SMTP): send
    confirmation email containing both a link and a numeric code
  → Core.Users (COMP-01): create/link local profile record, unconfirmed

Player clicks link OR enters code → Auth provider: verify → mark confirmed
  → Core.Users: reflect confirmed state
[REQ-702] Core.Rounds/Core.Leagues reject actions from unconfirmed accounts
  by checking this state before accepting a guess or league action
```

**6.5 Round-result notification flow** (realizes REQ-706 — Deferred/Phase 2)

```
[scheduled, at Round.EndTime, after Core.Scoring locks final scores]
Round Scheduler Job → Core.Notifications (COMP-08): "notify participants of round X"
  → Core.Notifications: filter to opted-in participants
  → Email provider (Resend, direct API call — not via Supabase Auth):
    send per-player round-result summary email
```

**6.6 Test-data reset flow** (realizes REQ-801, REQ-802, REQ-803 — dev only)

```
[dev environment only; endpoint doesn't exist in prod — ADR-0006]
Test runner / developer → Backend API: POST /internal/test-data/reset
  → Testing.SeedManager (COMP-09): tear down test-created rounds/guesses/
    leagues/synthetic users
  → Testing.SeedManager: recreate baseline via Core.Rounds/Core.Leagues/etc.
    normal creation paths (boundary rule 4) — never a direct table write
```

**6.7 Game-data sync flows, bidirectional** (realizes REQ-804/REQ-805 — ADR-0009)

Both directions share one allowlist (`infra/scripts/lib/game-data-tables.sh`):
`Player`, `PlayerData`, `PlayerOverride`, `PlayerAttribute`,
`PlayerNameIndex`, `PlayerAlias`, `TrophyDefinition`, `ClubCrest`,
`GridTemplate` — game/reference content only. `GridInstance`/`GridCell`
were never included (an earlier version of this doc incorrectly implied
they were — corrected here): they're specific to actual generated rounds,
which are inherently per-environment and never meaningful to sync.

```
Recommended direction — promote-dev-to-prod.sh (REQ-805):
[manual only, never scheduled]
Promotion job → Dev database: read the game-data allowlist
  → Production database: write/merge the same tables
  → User, NotificationPreference, League, LeagueMembership, Guess, Round,
    GridInstance/GridCell, and all Supabase Auth tables are excluded by
    construction — the shared allowlist never includes them

Fallback direction — sync-prod-to-dev.sh (REQ-804):
[manual only, never scheduled]
Sync job → Production database: read the same game-data allowlist
  → Dev database: write/merge the same tables
  → Same exclusions as above, same shared allowlist file
```

**6.8 Account deletion flow** (realizes REQ-710)

```
User → Web Frontend → Backend API: DELETE /auth/account (with confirmation)
  → Core.Users (COMP-01): anonymize all Guess rows belonging to this user
    (sever the UserId link — do not delete the rows, since other players'
    uniqueness scores and leaderboard history depend on the total guess count)
  → Core.Users: delete NotificationPreference, User record
  → Auth provider (Supabase Auth): delete the credential/identity
  → Email becomes available for a new registration
```

**Built as (S-025):** the "Core.Users" step above is slightly compressed —
`IAccountDeletionService` (COMP-01) also makes an explicit call into
`ILeagueRepository` (COMP-02) to remove the user's `LeagueMembership` rows,
between the `Guess` anonymize step and the `User` row delete (§5's COMP-01
status note has the precise sequence and reasoning). Attributed to
"Core.Users" here only for brevity, same as this diagram already
compresses several other cross-component calls elsewhere in this document.
`NotificationPreference` deletion is a no-op — that table doesn't exist yet
in Tier 0 (Resend/notification preferences are Tier 1, `MVP-SCOPE.md`).
Deleting the Supabase Auth identity needed a new `Supabase:ServiceRoleKey`
secret, since the anon key the rest of this flow's Supabase Auth calls use
can't call the Admin API — see ADR-0026.

**S-026 addition (REQ-506):** this flow now has a second entry point —
`Admin → Web Frontend (admin view) → Backend API: DELETE /admin/users?email=`
(`XGArcade.Api.Admin.AdminManagementEndpoints`, non-Production-only,
ADR-0006) — which resolves the admin-supplied email to a `User.Id` (new
`IUserRepository.GetByEmailAsync`) and then joins the diagram above at
exactly the same `IAccountDeletionService` call the self-service path uses;
everything below that point is identical, unchanged, and not duplicated.

**6.9 Backup flow** (realizes REQ-901 — Supabase's free tier has no built-in backups)

```
[scheduled, daily — backup-database.yml]
GitHub Actions → Production database: pg_dump (full export)
  → Store as a workflow artifact (or equivalent off-platform storage),
    with a bounded retention window, separate from the primary database
    and from Supabase entirely — see infra/README.md for the retention
    policy and restore procedure
```

## 7. Cross-cutting concerns

| Concern | Approach |
|---|---|
| Authentication | Delegated to Supabase Auth; backend validates JWTs on every request, does not manage passwords (see ADR-0004). Signup/login are backend-mediated — `XGArcade.Api` proxies Supabase Auth's REST API rather than the frontend calling it directly, so REQ-701's checkbox clause is enforced server-side before any identity is created (see ADR-0013) |
| CORS | Restricted to the known frontend origin(s) only, configured via environment variable, never a wildcard — enforced first in the middleware pipeline (before authorization), so an unrecognized origin is rejected regardless of any other check. No configured origin means the policy allows nothing rather than falling back to permissive (REQ-606; see `implementation-document.md` §3 for the full pipeline ordering) |
| Authorization | Two roles at this stage: Player, Admin. Enforced at the API controller level via a policy/attribute, verified by an automated test per admin endpoint (REQ-606) |
| Input validation | All user-supplied input is validated server-side (model validation / explicit checks), regardless of any client-side validation in the frontend (REQ-606) |
| Rate limiting | Applied to sign-up, login, and confirmation-resend endpoints specifically, since these are the abuse-prone surface (REQ-606, REQ-704's resend cooldown) |
| Transport security | HTTPS/TLS everywhere — frontend↔backend, backend↔database, backend↔external providers; no plaintext transport (REQ-606) |
| Secrets management | Environment variables / platform secret stores (GitHub Actions secrets, Container App secrets) for connection strings, API keys; never committed to source control |
| Configuration | Non-secret configuration (cron expressions, feature flags, environment tag) via environment variables, distinct from the secrets store above |
| Dependency security | Automated vulnerability scanning in CI for both backend (NuGet) and frontend (npm) dependencies; a high/critical finding blocks merge (REQ-606) |
| Query performance | Indexed lookups for hot paths — especially `Guess` queries used by uniqueness calculation (REQ-203/204) — and pagination on any list endpoint that can grow unbounded, e.g. leaderboards (REQ-607) |
| Logging | Structured logging in the Backend API; generation failures (REQ-101 abort case) must log with enough context to reproduce |
| Error handling | API returns problem-details style errors; frontend distinguishes user-facing validation errors from system errors |
| Observability | Minimal at MVP stage: logs + free-tier hosting metrics. Revisit if usage grows |
| Test data isolation | A test-data API exists only outside Production, and creates/resets data only through normal component write paths (ADR-0006, boundary rule 4). **S-026:** ADR-0006's fail-closed "not registered at all outside non-Production" pattern is no longer test-only — `AdminManagementEndpoints` (REQ-505/506, admin-facing, not test-only) reuses the identical discipline. This is a reuse of the existing decision (a growth in scope of use), not a new one — no new ADR was written for it |
| Backups | Independent daily backup of production, since the hosting free tier includes none — see ADR/REQ-901 and `infra/README.md` |
| Failure alerting | Scheduled jobs (round generation, sync, backups) must surface failures to the operator, not fail silently — REQ-902 |
| Data provider compliance | Terms of service for each external data source are read before relying on it, not assumed — see ADR-0008 |
| Shared external API budget | Live lookups try Wikidata first (not meaningfully capped for this system's volume), falling back to API-Football only when Wikidata can't resolve it. Grid generation (REQ-103) and guess-time verification (REQ-211) share a tracked daily counter for the API-Football fallback specifically — see ADR-0011, which corrected an earlier design (ADR-0010) that mistakenly treated API-Football as the only source |
| Account data rights | Deletion anonymizes rather than hard-deletes `Guess` rows, preserving other users' historical scores while removing the personal link — REQ-710/711 |

## 8. Quality attribute drivers

| Attribute | Driver | Architectural response |
|---|---|---|
| Testability | REQ-601 | Modular monolith with clear component boundaries; business logic has no direct DB/network dependency (see implementation doc test strategy) |
| Cost | REQ-602 | Modular monolith avoids per-service hosting cost; cache-first data strategy avoids storage/API-call cost growth |
| Extensibility | xG Arcade vision (multiple games) | `IGameModule` boundary isolates game-specific logic from Core |
| Data integrity | REQ-501 | Single write path to PlayerData/PlayerOverride via COMP-06; override precedence enforced in one place |
| Consistency of correctness | REQ-203 | **Revised 2026-07-10 (ADR-0018):** answer-checking still tries locally cached effective data first, but a guess that doesn't resolve from cache now falls through to a live Wikidata call (REQ-211) before being scored incorrect. This trades away the original "no live external call, so mid-round changes to external sources can't shift correctness" guarantee — two guesses on the same cell within one round could theoretically see different live Wikidata state — in exchange for not wrongly rejecting genuinely correct guesses (the reported bug this ADR fixes). Judged acceptable: a live-fetched result is upserted immediately, so once fetched it becomes the new stable cached state for the rest of the round |

## 9. Deployment view

| Environment | Frontend | Backend | Database/Auth | Notes |
|---|---|---|---|---|
| Local | Vite dev server | `dotnet run` or local container | Points at the dev Supabase project | Docker Compose optional for a fully local Postgres instead |
| Dev | Azure Static Web Apps (Free tier), separate app | Azure Container Apps (Consumption plan), separate app, `ASPNETCORE_ENVIRONMENT != Production` | Supabase project #2 (of the free plan's 2) | Used by CI's automated tests, manual QA, and local dev; hosts the test-data API (COMP-09) — see ADR-0006 |
| Production | Azure Static Web Apps (Free tier) | Azure Container Apps (Consumption plan), image from GHCR | Supabase project #1 (Postgres + Auth) | GitHub Actions builds/pushes the image and applies Bicep; see ADR-0004 |

Dev exists specifically to satisfy REQ-801–804 (testability) at
zero additional cost — it uses the second of Supabase's two free projects
and a second, equally free, Container Apps/Static Web Apps deployment
(Consumption/Free tiers are billed by usage, not by environment count).
This replaces the earlier "no separate staging environment" position now
that a concrete testability need justifies it — see ADR-0006.

Dev redeploys automatically on every PR and push to `main` (via `ci.yml`'s
`deploy-dev` job, which E2E tests depend on), so it never drifts from
current code the way a manually-updated environment would. Prod deploys
on every push to `main` via `deploy.yml` — effectively a promotion step
once a commit has already passed CI on dev.

IaC lives under `/infra/bicep` as composed modules (Container Apps
environment, Container App, Static Web App), not one flat template — see
`implementation-document.md` §8 and ADR-0004 for the full rationale. The
same modules are reused for both environments via a per-environment
parameters file (`main.parameters.json` vs `main.parameters.dev.json`).

## 10. Architecture Decision Records

Significant decisions are recorded as individual ADRs under
`docs/decisions/`, using the template in `docs/decisions/0000-template.md`.
Do not edit historical ADRs to reflect new decisions — supersede them with a
new ADR that references the old one.

| ADR | Title | Status |
|---|---|---|
| ADR-0001 | Incremental data cache instead of upfront database import | Accepted |
| ADR-0002 | Modular monolith instead of microservices | Accepted |
| ADR-0003 | Round references game instances generically, never a game-specific FK | Accepted |
| ADR-0004 | Hosting on Azure Container Apps + Static Web Apps, Bicep for IaC, Supabase for data/auth | Accepted |
| ADR-0005 | Custom SMTP via Resend for auth emails; separate Notifications component for product emails | Accepted |
| ADR-0006 | Two-project environment split, gated test-data API, one-way non-PII sync | Accepted |
| ADR-0007 | A broad player name index for autocomplete, separate from the narrow validated attribute cache | Accepted |
| ADR-0008 | Data provider terms-of-service compliance approach | Accepted (one pre-launch action item) |
| ADR-0009 | Bidirectional game-data sync (dev↔prod), never results or customer data | Accepted (supersedes ADR-0006's one-way clause) |
| ADR-0010 | Live verification at guess time, sharing the API budget with grid generation | Accepted (budget model superseded by ADR-0011) |
| ADR-0011 | Wikidata-first waterfall for live lookups; API-Football as fallback only | Accepted |
| ADR-0012 | Category value reference tables, each with resolved external IDs (Wikidata QID / API-Football team ID) | Accepted |
| ADR-0013 | Backend-mediated signup/login (proxying Supabase Auth's REST API), not frontend-direct | Accepted |
| ADR-0014 | All EF Core entities and repositories live in `XGArcade.Data`, regardless of which component owns them | Accepted |
| ADR-0015 | A `PlayerOverride` replaces an entire attribute type, not one value within it | Accepted |
| ADR-0016 | Read-only display queries against an already-generated instance may bypass `IGameModule` | Accepted |
| ADR-0017 | Validate Supabase JWTs against its JWKS endpoint, not a static shared secret | Accepted |
| ADR-0018 | REQ-211 (guess-time live verification) implemented in Tier 0, without its `PlayerNameIndex` gate | Accepted (further revises ADR-0010's trigger condition) |
| ADR-0019 | Silent auto-rename to resolve pre-existing DisplayName collisions during the S-017 uniqueness migration | Accepted |
| ADR-0020 | Uniqueness formula excludes the guesser's own guess from the comparison | Accepted |
| ADR-0021 | xG Arcade is scored like golf — lower points is better, lowest total wins | Accepted (builds on ADR-0020, does not supersede it) |
| ADR-0022 | Round closing runs inside the round-generation scheduled job, not a second cron | Accepted |
| ADR-0023 | Grid generation gets its own wall-clock deadline (`MaxDuration`), separate from `MaxAttempts` | Accepted |
| ADR-0024 | Player cache warming runs as a CLI verb, never an HTTP endpoint or background task | Accepted |
| ADR-0025 | Player pool restricted to male footballers born in 1939 or later | Accepted |
| ADR-0026 | A dedicated `service_role` secret for Supabase Auth account deletion | Accepted |
| ADR-0027 | Configuration-bound `RoundDuration` + daily safety-poll cron (replaces the Tue+Fri cadence's hand-matched coupling) | Accepted |
| ADR-0028 | Single-valued Wikidata properties (e.g. a player's photo) live on `Player`, not `PlayerAttribute` | Accepted |
| ADR-0029 | Wikidata sync data is auto-verified; only the guess-time fallback stays reviewable | Superseded by ADR-0032 |
| ADR-0030 | Mobile hamburger nav toggle, and a consolidated Settings screen replacing standalone header links | Accepted |
| ADR-0031 | Live leaderboard contributions (REQ-406/407) are recomputed on every read, never cached or snapshotted — reverses §6.2a's DB-side-aggregate/bounded-read-cost pattern for the live component | Accepted |
| ADR-0032 | Wikidata guess-time fallback data is now auto-verified too, reversing ADR-0029's fallback-specific carve-out | Accepted |
| ADR-0033 | Refresh token (REQ-715) stored in `localStorage`, same mechanism as the existing access token — no new cookie/CSRF infrastructure | Accepted |

## 11. Glossary

See `requirements-document.md` §2 for domain terms (Grid, Cell, Round, Guess,
Uniqueness score, Override, Unverified data). This document additionally
uses:

| Term | Meaning |
|---|---|
| Container | A separately deployable/runnable unit (C4 terminology) |
| Component | A cohesive module within a container, with a defined responsibility |
| Effective data | The result of merging PlayerData with any PlayerOverride, override wins |
