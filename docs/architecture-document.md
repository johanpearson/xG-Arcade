---
doc_id: architecture-document
title: Architecture Document
version: "0.71"
status: draft
last_updated: 2026-08-01
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

# Architecture Document – xG Arcade

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
| COMP-11 | Games.XGPath | **Status: Puzzle generation (S-081, ADR-0045), clue reveal/guess submission/attempt cap (2026-07-27, S-082), clue-efficiency scoring (2026-07-28, S-083), and round-scheduling wiring (2026-07-28, S-084, ADR-0051) all built.** `XGPathGameModule.GenerateInstanceAsync`/`GetCellIdsAsync` implement REQ-1201 (target-player eligibility)/REQ-1202 (N distinct-target puzzles per round) — see those REQs' own status notes for the exact eligibility reading and ADR-0045 for why. New entities `PathTemplate`/`PathInstance`/`PathPuzzle` (`XGArcade.Data`) mirror `GridTemplate`/`GridInstance`/`GridCell`'s shape; unlike `GridCell`, `PathPuzzle.TargetPlayerId` is a real FK into `Player` (COMP-06) — see ADR-0045 for why this doesn't cross ADR-0003's boundary. New repository `IPathInstanceRepository`/`PathInstanceRepository` is COMP-11's own persistence, same one-repo-per-component precedent as `IGridInstanceRepository`; a new `IPlayerStoreRepository.GetAllCareerStintsByPlayerAsync` bulk read (COMP-06) is the only new cross-component call from S-081. **S-082 addition:** `ScoreSubmissionAsync` (REQ-1204) resolves a guess via the same `Player.NormalizedFullName`/`PlayerAlias.NormalizedAlias` matching order `GridGameModule.FindMatchAsync` uses, correct iff the resolved candidate's `PlayerId` equals the puzzle's one target — **deliberately no fuzzy-matching stage and no REQ-209-style disambiguation prompt**, reviewed and confirmed "fine as-is" by `architecture-reviewer` during S-082's quality gate (a structural difference from xG Grid, not a gap to "fix" later: xG Path has no category concept to bound a fuzzy search by, and disambiguation is moot when only one target player can ever be correct — see `XGPathGameModule.ScoreSubmissionAsync`'s own doc comment for the full reasoning). `GetMaxAttemptsForCellAsync` (REQ-1205) returns the fixed constant 7 unconditionally, mirroring `GridGameModule`'s own unconditional-return shape (ADR-0041). New `GET /path/current` (`XGArcade.Api.Path.PathEndpoints`, REQ-1203) is COMP-11's own read-only display endpoint, reading `PathInstance`/`PathPuzzle` directly via `IPathInstanceRepository` — ADR-0016's direct-repository-read pattern applied to a second game module, confirmed (not superseded) by the new ADR-0048 (§6.2b). Guess submission itself adds **no new write endpoint**: xG Path guesses go through the existing, already-game-agnostic `POST /rounds/{roundId}/cells/{cellId}/guesses` (`XGArcade.Api.Guesses.GuessEndpoints`), routed to `XGPathGameModule.ScoreSubmissionAsync` purely via `IGameModuleResolver`/`Round.GameKey`, same as xG Grid. `PathScoringException` now derives from a new shared `XGArcade.Core.Games.GameEntityNotFoundException` (alongside xG Grid's `GuessScoringException`), so `GuessEndpoints` — game-agnostic by design — no longer needs compile-time knowledge of either game's own concrete exception type to catch this failure mode; mirrors `LiveLookupUnavailableException`'s existing cross-boundary precedent for living in `Core.Games` rather than a game module's own assembly. `Player.Position`/`Player.BirthYear` (REQ-1207, new nullable scalar columns on COMP-06's `Player`, set once at creation from Wikidata P413/P569 riding the existing intersection queries) feed the clue sequence's position/age clues. **S-084 addition (ADR-0051):** round-scheduling wiring is now built — a second `RoundSchedulingOptions` instance (`GameKey = "xg-path"`, its own configured `RoundDuration`) is registered and resolved via the new `IRoundSchedulingOptionsResolver` (mirroring `IScoringStrategyResolver`'s per-`GameKey` shape), and `POST /internal/generate-round?gameKey=xg-path` (the same `generate-round.yml` daily cron `"xg-grid"` uses, not a second scheduled job) now generates real xG Path rounds end to end, dispatching to the new `PathTemplateResolver` (`XGArcade.Api.Path`) to find-or-create a `PathTemplate` by `PathGenerationOptions.PuzzleCount` (default 4). `GET /path/current` now reads instances created this way in every environment, not only via the non-Production test-data endpoints. Owns the progressive clue-reveal sequence (`PathClueSequenceBuilder`/`PathClueTurn`: every documented club stint, split across exactly 3 reveal turns per REQ-1203's 2026-07-27 revision — never capped, each club bundled with its appearance count when known — then one bundled "years" clue, then position/nationality/age) and the fixed 7-clue-per-puzzle attempt cap this requires (ADR-0041). **S-083 addition (REQ-1206):** `ClueEfficiencyScoringStrategy` (`XGArcade.Core.Scoring`) is now registered against `GameKey = XGPathGameModule.XGPathGameKey` in `Program.cs`, mirroring `UniquenessScoringStrategy`'s `"xg-grid"` registration — this game has no uniqueness concept at all, since every solver of a given puzzle necessarily names the same target player, so `FinalUniquenessScore` is always null. `cluesUsed` is read directly off the winning `Guess.AttemptCount` (no new column); `maxCluesForThisPuzzle` is `maxAttemptsForCell`, resolved once per cell by COMP-04's `ScoreLockingService` via the existing `GetMaxAttemptsForCellAsync` (ADR-0041) and passed in — see COMP-04's own S-083 status note below and the new ADR-0049 for the resolved `IScoringStrategy` parameter shape (ADR-0040's own deferred follow-up). Reads career data only from COMP-06's `PlayerCareerStint` table (ADR-0042), never `PlayerAttribute`, except for the nationality clue, which reads `PlayerAttribute`'s existing "nationality" rows as a display-only read (never `PlayerOverride`/`HasEffectiveAttributeAsync`, which remains xG Grid correctness-checking's own precedence logic); reuses COMP-10's autocomplete/name-matching pipeline unchanged. See `docs/requirements-document.md` §4.12 (REQ-1201 onward) | `XGArcade.Games.XGPath` (scaffolded S-080; puzzle generation S-081; clue reveal/guess submission/attempt cap S-082; scoring strategy S-083; round-scheduling wiring S-084) |
| COMP-05 | Games.XGGrid | Grid generation, category logic (Country/Club/Trophy, REQ-107/REQ-108), `IGameModule` implementation for the xG Grid game. Also owns `PlayerCacheWarmingService` (REQ-110, S-036) — proactively warms COMP-06's cache for every reference Country×Club/Club×Club pair; not yet extended to Trophy pairs (a known, harmless gap — S-031's Trophy pairings are structurally unselectable in production anyway, see REQ-108's status note), run as its own CLI verb rather than an HTTP endpoint (ADR-0024). **Extended 2026-07-28 (REQ-110, ADR-0050):** `WarmAsync` now also skips a pair COMP-06 reports as `IsConfirmedLowAsync` (a prior run's persisted "checked, genuinely below `MinValidAnswers`" signal — see COMP-06's own row below), requests COMP-07's new cache-warming-only query-timeout tier on every live lookup, and retries a technical failure once within the same run (`LookupWithSameRunRetryAsync`) before counting it in the run summary's `PairsWithTechnicalFailure`/`FailingPairs`. **Extended 2026-08-01 (REQ-110, ADR-0052) — supersedes the same-run retry above:** the retry doubled every technical failure's cost and did nothing for a structural (rather than transient) failure — see NOTES.md's 2026-08-01 entry for the incident. `LookupWithSameRunRetryAsync` is removed; each pair is attempted exactly once per run. A pair also now skips (without any live query) once COMP-06 reports `IsPersistentTechnicalFailureAsync` true (2+ consecutive RUN-level failures, `PersistentFailureThreshold`), and a pair's failure marker is cleared (`ClearTechnicalFailureAsync`) the moment it gets a real answer. Still reaches COMP-06 only through `IPlayerStoreRepository` (`IsConfirmedLowAsync`/`RecordConfirmedLowAsync`/`IsPersistentTechnicalFailureAsync`/`RecordTechnicalFailureAsync`/`ClearTechnicalFailureAsync`) — boundary rule 1 unaffected | `XGArcade.Games.XGGrid` |
| COMP-06 | Data.PlayerStore | PlayerData, PlayerOverride, PlayerAttribute, PlayerAlias; override-merge logic — see ADR-0015 for the exact precedence semantics (`HasEffectiveAttributeAsync`: an override replaces its entire attribute type for correctness-checking, not one value within it). `PlayerAlias` (known nicknames/stage names) is populated incrementally alongside `PlayerAttribute` — e.g. from Wikidata's `skos:altLabel`, fetched in the same intersection query as REQ-103's live lookup (S-006) — not bulk-imported like COMP-10's index; not yet queried for guess-time name matching either (REQ-208's Tier 0 status note). As of S-012, `XGArcade.Api.Admin.AdminEndpoints` is a second caller alongside the guess-submission path, reaching PlayerData/PlayerOverride only through `IPlayerStoreRepository`, same as any other caller — no new data-access path. **Built S-079 (ADR-0042):** `PlayerCareerStint` (ordered, dated career stints with an optional appearance count) is a new entity alongside the three above, populated from the same `P54` fetch as `PlayerAttribute`'s "club" rows — specifically only by `WikidataLookupService.LookupAndPersistAsync` (the country/nationality × club path); the other three `Lookup*Async` callers (club-club, trophy-country, trophy-club) deliberately do not populate it yet, a scoped decision made in S-079, not an oversight, and one a future story may extend. `SequenceOrder` is resolved at write time across a player's full stint set (existing rows plus any newly discovered), so a stint found later that chronologically precedes existing ones re-numbers the whole sequence; `AppearanceCount` is null (never `0`) when Wikidata's P1350 qualifier isn't present. **S-081 addition:** `IPlayerStoreRepository.GetAllCareerStintsByPlayerAsync` is a new bulk read (every player's full stint set, grouped by `PlayerId`, in one query) — COMP-11's puzzle-generation eligibility check (REQ-1201) is its only caller, same "tolerate a full-table-scale read at Tier 0's player-pool size" precedent `GetPlayersMissingPhotoAsync` already establishes. Read only by COMP-11 (xG Path) — never by xG Grid's correctness path, and never merged with `PlayerAttribute` itself. **S-082 addition (REQ-1207):** `Player.Position`/`Player.BirthYear` are two new nullable scalar columns on `Player` itself (not `PlayerAttribute` rows — neither value has club-style multiplicity), sourced from Wikidata's P413/P569 riding the same five intersection queries that already resolve `FullName`/`WikidataQid`/`PhotoUrl`, set once at player creation and never re-synced, same rule as `PhotoUrl`. Read only by COMP-11's clue-reveal sequence (REQ-1203) — no other caller. **Extended 2026-07-28 (REQ-110, ADR-0050):** `ConfirmedLowMatchPair` is a new entity/table, a composite-key (`FirstAttributeType`/`FirstAttributeValue`/`SecondAttributeType`/`SecondAttributeValue`) marker recording that COMP-05's cache warming already confirmed a pair genuinely below `MinValidAnswers`, deliberately with no FK into `Player` (the zero-match case it mainly exists for has none). Reachable only via two new `IPlayerStoreRepository` methods, `IsConfirmedLowAsync`/`RecordConfirmedLowAsync` — never a direct `DbContext` query from `Games.XGGrid`, same boundary-rule-1 discipline as every other COMP-06 read. Invalidated (deleted) by `StaleClubAttributeCleaner` (REQ-111) and the `purge-player-pool` CLI verb (REQ-112/S-038) alongside the `PlayerAttribute`/`PlayerData` rows they already clear — see ADR-0050 for the full "why a new table, not a column" reasoning and the invalidation invariant. Deliberately **not** added to `infra/scripts/lib/game-data-tables.sh`'s prod/dev sync allowlist (ADR-0009) — it is derived, environment-specific cache-warming process state, not an objective Wikidata fact. **Extended 2026-08-01 (REQ-110, ADR-0052):** `PairLookupFailure` is a second new entity/table, same composite-key shape and invalidation surface as `ConfirmedLowMatchPair` above but a different kind of fact — an operational record of this codebase's own query reliability against a pair (resettable, not an objective Wikidata fact), tracking `ConsecutiveFailureCount` across separate cache-warming runs. Reachable only via three new `IPlayerStoreRepository` methods, `IsPersistentTechnicalFailureAsync`/`RecordTechnicalFailureAsync`/`ClearTechnicalFailureAsync`. Same "not eligible for the prod/dev sync allowlist" reasoning as `ConfirmedLowMatchPair` — see ADR-0052. **Extended 2026-08-01 (live-incident follow-up to ADR-0052):** a second, narrower invalidation path — `PairLookupFailureCleaner` (`XGArcade.Data.Seeding`) and its `clear-pair-lookup-failures` CLI verb — clears only `PairLookupFailure` rows already at/above `PersistentFailureThreshold`, touching no other table; added after a real run left 125 Club×Club pairs stuck across all 32 seeded clubs, where `StaleClubAttributeCleaner`'s club-name scope would have wiped ~850 other pairs' good cached data along with them. `StaleClubAttributeCleaner`/`purge-player-pool` remain the tools for a QID/query-shape correction (their own broader scope is intentional there); this one is for clearing the failure marker alone. **Built S-089 (REQ-215, ADR-0053):** `PlayerSuggestion` (a submitting user's claim that a named player satisfies a specific cell — asserted club(s)/nationality, `SubmittingUserId`, originating `CellId`/`RoundId`/`RowCategoryType`/`ColCategoryType`, `Status` pending/committed/rejected) and its child table `PlayerSuggestionClub` (one row per asserted club) are new entities, persisted in `XGArcade.Data` alongside `PlayerData`/`PlayerOverride`/`PlayerAttribute` per ADR-0053's "COMP-01-adjacent, filed under COMP-06's data project" placement — reached only via the new `IPlayerSuggestionRepository`/`PlayerSuggestionRepository` (`AddAsync`, submission-only as of S-089; REQ-509/510's list/commit/reject reads are S-090, not yet built). Deliberately never read by `HasEffectiveAttributeAsync` or any other correctness-checking path, and never written to by `PlayerNameIndex`/COMP-10 — a `PlayerSuggestion` is a pending human claim, not player data, until a future admin commit (REQ-509, S-090) writes through the existing `PlayerAttribute`/`PlayerOverride` mechanism, per ADR-0053/ADR-0007 | `XGArcade.Data` |
| COMP-07 | DataSync.Clients | Wikidata/API-Football clients, live-lookup fallback. As of REQ-114/ADR-0035 (S-066), `IWikidataClient`/`WikidataLookupService` dispatch a Country×Club query through one of two query-property paths — `P27` (citizenship, every ordinary country) or `P1532` ("country for sport", the four home nations) — chosen from a flag on the `CountryDefinition` row passed in, never a second category type; see COMP-05/COMP-06's own status note below and ADR-0035 for the full design. **Extended 2026-07-28 (REQ-110, ADR-0050):** `WikidataClient.RunIntersectionQueryAsync`'s per-call timeout is now selected by a `WikidataQueryTimeoutTier` enum (`Default`/`CacheWarming`), not `throwOnTimeout` alone — `Default` preserves the existing two-way split exactly (`throwOnTimeout: false` → round generation's 15s, REQ-103; `throwOnTimeout: true` → the guess-time fallback's 28s, ADR-0046); `CacheWarming` (always paired with `throwOnTimeout: false`) is a third, 45s budget requested only by COMP-05's `PlayerCacheWarmingService`, justified by the same ADR-0011 9-27s WDQS evidence `_guessTimeFallbackQueryTimeout` already leans on, just with a wider margin since nothing is synchronously waiting on a cache-warming run. `IWikidataClient`/`IWikidataLookupService` also gained an optional `onTechnicalFailure` callback so a caller can distinguish a genuine timeout/HTTP/parse-error swallow from a successful-but-empty response, without changing the existing fail-open/swallow-to-`[]` contract for any caller. **Extended 2026-08-01 (REQ-110, ADR-0052):** `BuildClubClubIntersectionQuery` now wraps each club's P54 statement-path match in its own `FILTER EXISTS { }` block instead of a plain join — a plain join's two independently-bound statement variables could multiply result rows by (statements at club A) x (statements at club B) per player, producing a real 250,000+ row WDQS response for two clubs with a large, overlapping squad (see NOTES.md's 2026-08-01 entry). Scoped to this one builder only — `BuildCountryClubIntersectionQuery`/`BuildNationalTeamClubIntersectionQuery`/`BuildTrophyClubIntersectionQuery` still need their club statement variable bound in the outer pattern for the shared query footer's career-stint qualifier fetch (ADR-0042/S-079) and cannot use the same trick. Also: `RunIntersectionQueryAsync`'s two per-pair failure logs (timeout, HTTP/parse error) are now `Debug`, not `Warning` — see ADR-0052's log-cleanup note | `XGArcade.DataSync` |
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

**COMP-04 status (design only, 2026-07-26, ADR-0040/ADR-0041):** planning
xG Path (COMP-11) surfaced two hidden xG-Grid-only assumptions inside
`Core.Scoring`, both scoped as ADRs rather than being folded silently into
xG Path's own build. First (ADR-0040): `ScoreLockingService` currently
calls `UniquenessCalculator`/`ScoringRules.PointsFromUniqueScore` directly
for every game — it will instead resolve an `IScoringStrategy` by
`Round.GameKey` through a new `IScoringStrategyResolver`, the same
resolution shape `IGameModuleResolver` already establishes; xG Grid's
existing formula becomes `UniquenessScoringStrategy` (an extraction, not a
behavior change), and xG Path gets `ClueEfficiencyScoringStrategy`. Second
(ADR-0041): `GuessRules.MaxAttemptsPerCell`'s hardcoded `2` becomes a
per-cell value read through a new `IGameModule` method (mirroring
`GetCellIdsAsync`'s existing shape) — xG Grid returns `2` unconditionally
(no behavior change), xG Path returns a fixed `7` for every puzzle
(REQ-1203, revised 2026-07-27: all of a target's club stints are now
shown, spread across 3 reveal turns, rather than capping at 5 one-per-clue
— see `docs/CHANGELOG.md`). Separately, this planning pass also resolved
the open question the paragraph directly above raises about `Guess.CellId`
("generalize this when a second game is built"): `XGArcadeDbContext` was
checked and there is no actual EF Core foreign-key relationship configured
between `Guess`/`GridCell` today, only the doc comment's conceptual
coupling — so `Guess.CellId` already works as an opaque per-game cell
reference for COMP-11 with no schema change needed; the doc-comment
caveat can be removed once COMP-11 is actually built and confirms this in
practice.

**COMP-04 status (S-076, ADR-0040 — built, not just designed):** the first
of the two ADR-0040/ADR-0041 refactors above is now real code, ahead of xG
Path itself (S-079+). `ScoreLockingService.LockRoundScoresAsync` no longer
calls `UniquenessCalculator`/`ScoringRules.PointsFromUniqueScore` directly;
it resolves an `IScoringStrategy` via the new `IScoringStrategyResolver`
(`Core.Scoring`), keyed by `Round.GameKey`, mirroring
`IGameModuleResolver`'s resolution shape exactly (interface + a concrete
resolver taking `IEnumerable<IScoringStrategy>`, throwing
`InvalidOperationException` for an unregistered `GameKey`). xG Grid's
existing formula is now `UniquenessScoringStrategy`, a pure wrap of
`UniquenessCalculator.Calculate` + `ScoringRules.PointsFromUniqueScore` —
same math, same order of operations, registered in `Program.cs` with
`GameKey = GridGameModule.XGGridGameKey` supplied at the composition root
(never hardcoded inside `XGArcade.Core`, same pattern
`RoundSchedulingOptions.GameKey` already established — ADR-0003).
`MaterializeUnansweredCellsAsync`'s unanswered-cell penalty is untouched:
it still runs before any strategy is consulted and stays
`FinalPoints = MaxPointsPerCell`/`FinalUniquenessScore = null`,
strategy-agnostic. This is a pure extraction — every existing REQ-204/205
acceptance criterion still holds for xG Grid unchanged.

**COMP-04 status (S-077, ADR-0041 — built, not just designed):** the second
of the two ADR-0040/ADR-0041 refactors above is now real code too, ahead of
xG Path itself (S-079+). `IGameModule` gained
`Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken)`,
resolved through `IGameModuleResolver` the same way `GetCellIdsAsync`
already is. `GridGameModule`'s implementation returns `2` unconditionally
for every cell — no repository lookup, no branching on `instanceId` or
`cellId` — deliberately identical to the behavior it replaces. The old
`GuessRules.MaxAttemptsPerCell` global constant no longer exists.
`GuessSubmissionService` (REQ-210's lock/cap check),
`LiveRoundContributionService` (the locked-incorrect live-contribution
branch), and `RoundEndpoints` (`GET /rounds/current`'s `Locked` field on
each cell's guess) all now read the cap through the module instead of the
deleted constant. This is a pure extraction — every existing REQ-210 acceptance
criterion still holds for xG Grid unchanged; new tests cover
`GridGameModule.GetMaxAttemptsForCellAsync` directly plus call-count
assertions on `GuessSubmissionService`/`LiveRoundContributionService`'s
resolution of it.

**COMP-05/COMP-11 status (S-089, REQ-215 — new `IGameModule` method,
architecture-review fix applied same session):** `IGameModule` gained a
fourth method, `Task<CellCategoryTypes> GetCellCategoryTypesAsync(Guid
instanceId, Guid cellId, CancellationToken)`, mirroring
`GetCellIdsAsync`/`GetMaxAttemptsForCellAsync`'s existing shape —
resolved through `IGameModuleResolver` by `Round.GameKey`, never called
directly. Its only caller is the new `XGArcade.Api.Suggestions
.SuggestionEndpoints` (`POST /rounds/{roundId}/cells/{cellId}/suggestions`,
REQ-215), which needs a cell's authoritative row/col category types to
persist on a submitted `PlayerSuggestion` row without trusting the
client for them. `GridGameModule` (COMP-05) implements it as a plain
`IGridInstanceRepository.GetCellByIdAsync` read, throwing
`GuessScoringException` (a `GameEntityNotFoundException`) for an unknown
cell — no new repository method. `XGPathGameModule` (COMP-11) implements
it by throwing `NotSupportedException` unconditionally: xG Path's
`PathPuzzle` has a single fixed `TargetPlayerId`, not two independent
category axes, so there is genuinely nothing to return — the same
"flag it, don't fabricate a value" discipline this interface's other
per-game judgment calls already follow. REQ-215's frontend does not wire
a suggestion entry point up for `GameKey = "xg-path"` at all, so this path
is unreachable today; a known, accepted, non-blocking gap (flagged by
architecture-reviewer, not fixed) is that reaching it would currently
surface ASP.NET's bare default `500` rather than a deliberate
`ProblemDetails` response, since `SuggestionEndpoints` has no catch clause
for `NotSupportedException`. **Architecture-review fix applied
same-session:** the original S-089 commit resolved a cell's category
types via a direct `IGridInstanceRepository`/`GridCell` read from
`SuggestionEndpoints.cs` itself — a boundary rule 2 violation (an
Api-layer file reaching into COMP-05's game-specific entity directly
instead of going through `IGameModule`, ADR-0003). `architecture-reviewer`
caught this before merge; the fix (routing through the new
`GetCellCategoryTypesAsync` method instead) is what's described above and
is what actually shipped. No architecture-doc pass happened when REQ-215
was first built, which is why this note — and the `PlayerSuggestion`/
`PlayerSuggestionClub` note on COMP-06's row below — are both being added
only now, as part of doc-sync, not at the time of the original commit.

**COMP-04 status (S-083, ADR-0040/ADR-0049 — xG Path's own strategy now
built):** the `ClueEfficiencyScoringStrategy` xG Path's own COMP-11 status
note above named as still owed is now real code, resolving ADR-0040's own
deferred follow-up ("the exact parameter shape is an implementation detail
... not fixed by this ADR") via the new ADR-0049. `IScoringStrategy
.ScoreCorrectGuess`'s signature changed from
`(IReadOnlyCollection<Guess> correctGuessesForCell, Guid myAnswerPlayerId)`
to `(Guess guess, IReadOnlyCollection<Guess> correctGuessesForCell, int
maxAttemptsForCell)` — `guess` (the correct `Guess` row being scored)
replaces the bare `myAnswerPlayerId`, and `maxAttemptsForCell` is new,
resolved once per cell (not once per guess) by `ScoreLockingService` itself
via the existing `IGameModule.GetMaxAttemptsForCellAsync` (ADR-0041) before
either strategy is invoked. `UniquenessScoringStrategy` was adapted to the
new signature with no formula/behavior change (it still reads
`guess.PlayerAnswerId` where it used to read the bare parameter, and still
ignores `maxAttemptsForCell` entirely — xG Grid's attempt cap has no
bearing on REQ-204/205's formula). `ClueEfficiencyScoringStrategy`
(`XGArcade.Core.Scoring`) reads `cluesUsed` directly off `guess
.AttemptCount` (no new `Guess` column — `XGPathGameModule`/
`GuessSubmissionService`'s one-row-per-cell, increment-per-submission
behavior already makes a winning guess's `AttemptCount` equal its
clue-reveal count) and ignores `correctGuessesForCell` entirely (no
uniqueness concept). Registered against `GameKey =
XGPathGameModule.XGPathGameKey` in `Program.cs`, mirroring
`UniquenessScoringStrategy`'s own `"xg-grid"` registration — `Core.Scoring`
gains no new dependency on `Core.Games`/`IGameModule` from either strategy
itself; `ScoreLockingService` is the only caller of
`GetMaxAttemptsForCellAsync` in this flow. See ADR-0049 for the full
alternatives considered (notably: why `IScoringStrategy` itself was not
given a direct `IGameModule` dependency instead).

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

**COMP-02 status correction (2026-07-20/S-060, REQ-409) — supersedes part of
the S-053/S-054 note above:** the S-053/S-054 note above describes
`GetGlobalLeaderboardAsync` as taking a nullable `Round? activeRound` and
folding `ILiveRoundContributionService`'s live per-cell contribution onto
the locked `SUM(FinalPoints ?? 0)`. That is no longer how this method
works. REQ-409 replaced the all-time ranking formula outright: it now
ranks by each qualifying member's **median** per-round `SUM(FinalPoints)`
(a new `IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync`, closed
rounds only), and a member needs at least 5 qualifying rounds (a closed
round with >=1 `Guess` in it) to be ranked at all — a member with fewer is
absent from the list entirely, the same "absent, not defaulted" shape the
old zero-guess exclusion already used. `GetGlobalLeaderboardAsync` no
longer takes an `activeRound` parameter and no longer calls
`ILiveRoundContributionService` at all — folding a still-changing round
into a median has no resolved meaning (median-of-what, for a round that
hasn't finished contributing yet), so REQ-409 dropped that fold rather than
adapting it. This *only* affects the original `GET
/leagues/global/leaderboard` (all-time) route — `GetActiveRoundLeaderboardAsync`
(REQ-407, `GET /leagues/global/leaderboard/active-round`) is untouched and
still recomputes fully live via `ILiveRoundContributionService`, same as
`GetClosedRoundsAsync`/`GetClosedRoundLeaderboardAsync` (REQ-408) are
untouched. The two now-dead repository methods this replaced
(`GetTotalFinalPointsByUserIdsAsync`, the old all-time SUM query, and
`GetUserIdsWithAnyGuessAsync`, the old "ever played at all" exclusion) were
removed outright, not left dormant — see REQ-409's own text.

**COMP-02 status (2026-07-20/S-063, REQ-402/403):** `ILeagueService`/
`LeagueService` (`XGArcade.Core.Leagues`) is COMP-02's first *write* path
for leagues, alongside `ILeaderboardService`'s pre-existing read-only
aggregation — kept as a separate interface/service rather than folded into
`ILeaderboardService`, since create/join mutates `League`/`LeagueMembership`,
a genuinely different responsibility. `POST /leagues` (create, REQ-402) and
`POST /leagues/join` (join by invite code, REQ-403) are reached via a new
`XGArcade.Api.Leagues.LeagueEndpoints`, the same thin-endpoint/
owning-Core-service shape `LeaderboardEndpoints` already establishes.
`League` gained `Type="custom"`/`InviteCode`/`CreatedByUserId` columns
(`Type="global"` already existed, REQ-401); a 6-character invite code is
generated by a new `IInviteCodeGenerator`, checked for collision via an
in-app pre-check plus a DB-level unique index as the race-safety net — the
same "pre-check plus DB-level backstop" shape `DisplayNameExistsAsync`
(COMP-01, S-017) already established. `GET /leagues/mine` lists a caller's
own custom-league memberships. REQ-404's full per-custom-league leaderboard
(tab switching, per-league reads) is deliberately **not** part of this —
every leaderboard route in §6.2a below (all-time, active-round, closed-
rounds, windowed) still only ever reads the one global league; a custom
league's own leaderboard remains tracked follow-up work, not built yet.

**COMP-02 status (2026-07-20/S-027, REQ-405):** `GetWindowedLeaderboardAsync`
adds a fourth ranking scope alongside all-time/active-round/closed-rounds —
`GET /leagues/global/leaderboard/window/{resolution}`, `resolution` one of
`round`/`week`/`month`/`year`. Like REQ-408's closed-round scope, this is
locked-rounds-only (no live component, REQ-406's fold never applied here
either) — it sums `FinalPoints` over whichever closed rounds fall inside
the calendar-aligned window ending "now" for the requested resolution, not
a rolling N-day lookback. Explicitly unaffected by REQ-409's median change
above: REQ-405 keeps its own plain-sum-within-the-window ranking, since
"total scored within this window" and "typical per-round performance
across all history" are different questions with different natural
formulas.

**COMP-02 status (2026-07-27, S-078, ADR-0043):** planning xG Path's
platform integration (not just its own game logic, COMP-11) found that
three of `ILeaderboardService`'s four scopes were already `GameKey`-scoped
(`GetActiveRoundLeaderboardAsync` via the specific `Round` passed in;
`GetClosedRoundsAsync`/`GetClosedRoundLeaderboardAsync`/
`GetWindowedLeaderboardAsync` via an explicit `gameKey` parameter,
S-054/S-027 above) — only `GetGlobalLeaderboardAsync` (REQ-409's all-time
median) was not, silently blending every game's rounds into one ranking.
ADR-0043 closed that one remaining gap: `GetGlobalLeaderboardAsync` and
`IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync` both gained a
required `gameKey` parameter (the latter's existing `Guess`-`Round` join
just gained a `round.GameKey == gameKey` filter, no schema change). `League`
membership itself is untouched — one Global League, auto-joined at signup
(REQ-401) — only which game's rounds count toward the *ranking* is now an
explicit parameter, consistent with the other three scopes. See
`docs/requirements-document.md` §4.4 for the corresponding REQ (REQ-410).
`LeaderboardEndpoints` (the Api/outer-composition layer) passes
`GridGameModule.XGGridGameKey` explicitly, same convention as the other
three scopes' routes — xG Grid is still the only shipped game, so behavior
is unchanged in practice; the frontend game-switcher UI this eventually
needs (SCREEN-03) is a separate, not-yet-built follow-up (S-087).

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

**COMP-01 status (S-069, 2026-07-21, REQ-717/ADR-0036):** guest play added
two `User` columns (`IsGuest bool`, `ClaimedAt DateTime?`) and made `Email`
nullable — a guest is a real `User` row with no email/password, created via
`POST /auth/guest` (mirrors `Signup`'s Supabase-mediation shape, ADR-0013)
and later converted in place via `POST /auth/claim`
(`IUserRepository.ClaimGuestAsync`), never a second table or a re-link of
existing `Guess`/`LeagueMembership` rows. `ISupabaseAuthClient` gained
`SignInAnonymouslyAsync`/`LinkEmailPasswordAsync` alongside the existing
Signup/Login/Refresh/DeleteUser methods — same interface, same "never
throws, Success/ErrorMessage shape" contract, no new component. `IsGuest`
is consulted in exactly one place outside `AuthController` itself:
`GuessRepository.GetPerRoundFinalPointsByUserIdsAsync` (REQ-409's
qualifying-rounds query), which now joins `Users` to exclude guest rows and
a claimed account's pre-claim rounds — every other query/service
(REQ-201-210, REQ-204, REQ-406/407/408) is unmodified, per ADR-0036's own
"For AI agents" instruction that a guest must never gain a second,
guest-aware code path anywhere else.

**COMP-01 status (S-072, 2026-07-25, REQ-718/ADR-0038):** guest account
lifecycle cleanup added a third `User` column, `LastActiveAt` (non-nullable
`DateTime`, migration `20260725120000_AddUserLastActiveAt`) — updated on
exactly four events (login, guest provisioning, claim, a submitted guess)
with no `IsGuest` branch in any of those write paths, the same discipline
the S-069 status note above already established for `IsGuest` itself. Two
new `IUserRepository` queries
(`GetUnclaimedGuestsOlderThanAsync`/`GetInactiveGuestsOlderThanAsync`) are
the *only* other place `IsGuest`/`LastActiveAt` are consulted for this
feature — inside the new `/internal/purge-guest-accounts` endpoint
(`XGArcade.Api.Auth.InternalGuestCleanupEndpoints`, see §6.10), never inside
REQ-201-210/204/406/407/408. A new `POST /auth/logout`
(`AuthController.Logout`) is this system's first backend logout call at
all — REQ-715's logout was, until now, entirely client-side. Both the new
endpoint and the scheduled job call the exact same
`IAccountDeletionService.DeleteAccountAsync` (COMP-01, S-025) REQ-710's
self-service deletion and S-026's admin deletion already use — a fourth and
fifth caller, never a second implementation. The existing
`/internal/generate-round` bearer-token check
(`InternalRoundEndpoints.IsAuthorized`) was extracted into a shared
`XGArcade.Api.Internal.InternalJobAuthorization` helper so this second
bearer-token-gated `/internal/*` endpoint doesn't hand-duplicate it.

**COMP-01 status (S-073, 2026-07-25, REQ-507/508):** a new
`XGArcade.Api.Admin.AdminAccountsEndpoints` (`GET
/admin/accounts/metrics`, `GET /admin/accounts/guests/count`, `POST
/admin/accounts/guests/clear`) adds four new read-only `IUserRepository`
methods — `CountUsersAsync`, `CountGuestsAsync`, `CountClaimedGuestsAsync`,
`GetAllGuestIdsAsync` — all reached the same way every other
`IUserRepository` caller is, no new data-access path. Unlike
`AdminManagementEndpoints` (REQ-505/506, S-026), this file is registered
unconditionally, including Production — see the file's own doc comment
and each REQ's "Scope note"/environment acceptance criterion for why: both
REQs act on real account data as their stated purpose, not on seeded/test
data. `GetAllGuestIdsAsync` is a deliberately separate, unfiltered query
from S-072's `GetUnclaimedGuestsOlderThanAsync`/
`GetInactiveGuestsOlderThanAsync` above — REQ-508's own scope note
requires no age/inactivity filter, so it is not built by relaxing those
two queries. The bulk "clear" action is a further caller of
`IAccountDeletionService.DeleteAccountAsync` (§6.8) — the same
anonymize-and-keep mechanism REQ-710/REQ-506/REQ-718 already use, no new
deletion implementation. `AccountDeletionService` gained a public
`UserNotFoundErrorMessage` const (no behavior change) so this new caller
can distinguish a "no longer exists" outcome from any other failure
without a second existence check.

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

**COMP-05/COMP-06/COMP-07 status (2026-07-21/S-066, REQ-114/ADR-0035):**
national teams (England, Scotland, Wales, Northern Ireland) are seeded as
four additional `CountryDefinition` rows, never a new category type or
reference table — `CountryDefinition` gained one field,
`UsesCountryForSportProperty` (`bool`, default `false`), read only at the
point COMP-07's `WikidataLookupService.LookupAndPersistAsync` decides
between its `P27` (citizenship) and `P1532` ("country for sport") query
paths. No boundary rule above is affected: `GridGameModule` (COMP-05) still
never queries player data directly (boundary rule 1), `CategoryPairingRules`/
`SelectPairing` need no change (a home nation is picked, paired, and
validated exactly like any other `CountryDefinition` row), and matched
players still persist under the same `PlayerAttribute.AttributeType =
"nationality"` vocabulary via COMP-06. The one piece of plumbing this added
is `GridGameModule`'s internal `CategoryCandidate` struct carrying the flag
from generation through to COMP-07's dispatch call, so the decision is made
in exactly one place rather than re-derived per candidate — see ADR-0035
for the full rationale, alternatives considered, and the one known follow-up
gap (Country×Trophy doesn't yet honor the flag, currently unreachable in
production).

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

**COMP-03 status (S-084, ADR-0051):** the shape described above (one
`RoundSchedulingOptions`, one `GameKey`, resolved directly into
`RoundGenerationService`) was single-`GameKey` only until this story.
`RoundGenerationService.GenerateNextRoundIfNeededAsync` now takes a leading
`gameKey` parameter and resolves the right `RoundSchedulingOptions` via a
new `IRoundSchedulingOptionsResolver` (mirroring `IScoringStrategyResolver`'s
per-`GameKey` resolution shape, ADR-0040) rather than a directly-injected
singleton; two instances are now registered (`"xg-grid"`, `"xg-path"`),
each with its own independently-configured `RoundDuration`.
`/internal/generate-round` stays **one** endpoint, gaining an optional
`gameKey` query parameter (defaulting to `"xg-grid"` for back-compat with
any caller that omits it) — its own `gameKey switch`, dispatching narrowly
to either `GridTemplateResolver` or the new `PathTemplateResolver`
(`XGArcade.Api.Path`) to produce the round's opaque `TemplateId`, is the
*only* place in the handler that branches on `GameKey`; auth, the
`roundDurationHours` floor validation, and the response/error shape (an
unrecognized `gameKey` now returns 400, a quality-gate follow-up correcting
an initial 500) all stay generic. `generate-round.yml`'s single daily cron
now triggers this endpoint once per `GameKey` (each with its own
independent retry loop) rather than a second scheduled job. See ADR-0051
for the full decision, alternatives considered, and why `GridSize`/the new
`PuzzleCount` moved onto each game's own generation-options class
(`GridGenerationOptions`/`PathGenerationOptions`) instead of staying on
`RoundSchedulingOptions`.

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
`Round`. Scoped further to Tier 0 (`MVP-SCOPE.md`): every grid is Country ×
Club, Club × Club (`docs/backlog.md` S-030), or, as of S-031, a
Trophy-involving pairing (Country × Trophy, Club × Trophy, or Trophy ×
Trophy) — never Country×Country (REQ-107). Which of the (up to five)
allowed pairings a given instance uses is chosen once per
`GenerateInstanceAsync` call (`GridGameModule.SelectPairing`) — uniformly at
random among whichever the seeded reference data can support,
deterministically falling back to whichever subset is feasible otherwise.
REQ-107's Country×Country ban is enforced by
`CategoryPairingRules.IsAllowedPairing`, checked once per `PickHeadersAsync`
call (invariant for that call, since every candidate in one call shares the
same two category types) — not by a fixed-axis assumption baked into the
code. **Load-bearing caveat (REQ-108's status note has the full detail):**
with only one trophy seeded in production, every Trophy pairing is
structurally infeasible for any realistic grid size, so Trophy is
mechanically wired up but not yet actually selectable — this becomes live
only once more trophies are added as reference data.

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
  plus (as of REQ-208's 2026-07-26 correction, ADR-0044) a child table
  `PlayerNameIndexWord` (`PlayerId`, `Word` — one row per space-separated
  word in `NormalizedName`, `HasIndex(Word)`) so `SearchByPrefixAsync` can
  match a surname-only query, not just a prefix of the whole stored name —
  both still plain, index-backed `StartsWith` scans, never a
  leading-wildcard/`Contains()` match at this table's bulk-imported scale.
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
  not this diagram's component attribution. **S-077/ADR-0041 addendum
  (2026-07-26):** since S-077, `GuessSubmissionService` *does* call into
  `Games.XGGrid` before that rejection decision — `IGameModule
  .GetMaxAttemptsForCellAsync(instanceId, cellId)`, resolved through
  `IGameModuleResolver` to read this cell's own attempt cap. This is not
  the exception the paragraph above is about (`ScoreSubmissionAsync`, the
  name-resolution call, still only runs after every REQ-210 check passes)
  — `GetMaxAttemptsForCellAsync` is a narrow, side-effect-free read of a
  per-cell configuration value, not name-resolution work. The rejection
  *decision* itself is still made entirely in `GuessSubmissionService`;
  `Games.XGGrid` only answers "what's this cell's cap," never "should this
  guess be rejected."
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
  the guess, then re-checks. As of `docs/backlog.md` S-030/S-031, this
  covers every pairing a grid can actually be generated with except
  Trophy×Trophy — Country×Club (`LookupAndPersistAsync`), Club×Club
  (`LookupAndPersistClubClubAsync`), Country×Trophy
  (`LookupAndPersistTrophyCountryAsync`), and Club×Trophy
  (`LookupAndPersistTrophyClubAsync`) — dispatched from one shared
  `LookupLiveMatchesAsync` helper also used by generation-time matching
  (`GetMatchCountAsync`), so the call sites can't drift on which pairings
  are handled; Trophy×Trophy has no dedicated persist method (unreachable
  in production anyway, see REQ-108's status note) and, like any other
  unhandled pairing, fails closed rather than throwing. **Correction
  (2026-07-27, bug-fix bundle, ADR-0046; supersedes the paragraph this
  replaces):** this diagram's full shape ("guess matched a
  `Data.PlayerNameIndex` candidate") is now accurate for the trigger
  condition, not a deliberate simplification anymore —
  `GridGameModule.ScoreSubmissionAsync` now checks
  `IPlayerNameIndexRepository.ExistsByNormalizedNameAsync` before running
  the live lookup, closing the gap the previous version of this paragraph
  described. This was not a new pull-forward of REQ-208/209's
  candidate-resolution work: `PlayerNameIndex`/COMP-10 (REQ-207) has existed
  since S-032, and the un-gated trigger had simply never been updated to
  use it once it existed — a stale simplification note, not a deliberate
  scope boundary, and the dominant cost behind a reported "guessing is slow"
  bug (an un-gated live Wikidata round-trip on every unresolved guess,
  including ones matching no real player at all). ADR-0018's own
  Wikidata-has-no-scarce-budget reasoning for *not requiring* this gate is
  unaffected — the gate is now applied anyway, purely as the latency
  optimization ADR-0018's "For AI agents" section already anticipated.
  There is still no API-Football fallback leg or `ExternalApiUsage`
  budget-gating for this call site, same as REQ-103's status.
- **New exception-based signal crossing the Games.XGGrid → Core.Scoring
  boundary (2026-07-27, ADR-0046):** a timeout on this live-lookup call
  (`DataSync.Clients`, COMP-07) previously swallowed to an empty result,
  indistinguishable from "Wikidata found no match" — wrong for this call
  site specifically, since it let a timeout during a genuinely correct
  guess get persisted as a confirmed incorrect answer. `IWikidataClient`'s
  intersection-query methods now accept an opt-in `throwOnTimeout`
  parameter, set only here (REQ-103's own use of the same client is
  unaffected — default unchanged); on timeout, `DataSync.Clients` throws
  `WikidataQueryException`, which `Games.XGGrid` catches and translates
  into a new `XGArcade.Core.Games.LiveLookupUnavailableException` — defined
  in `Core` itself, never in `Games.XGGrid` or `DataSync`, so `Core.Scoring`
  never references a `DataSync`-specific type (ADR-0003's boundary).
  `Core.Scoring` (`GuessSubmissionService`) catches that exception and
  returns a new `GuessSubmissionOutcome.LiveLookupUnavailable` — before
  writing any `Guess` row, the same shape REQ-209's disambiguation branch
  already uses — which `XGArcade.Api` maps to HTTP 503. See ADR-0046 for
  the full decision and alternatives considered.
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

**6.2a Global leaderboard flow** (realizes REQ-401, REQ-402, REQ-403,
REQ-404, REQ-405, REQ-407, REQ-408, REQ-409 — REQ-406 was retired by
REQ-409, see below; Tier 0 slice only, added S-011, extended through
2026-07-21)

```
Person → Web Frontend → Backend API: POST /auth/signup (new account)
  → Core.Users (COMP-01): create User row (includes DisplayName)
  → Core.Leagues (COMP-02): GetOrCreateGlobalLeagueAsync (idempotent
    singleton, filtered unique index on League.Type="global"), then
    AddMembershipAsync — "requires no action from the user" (REQ-401) is
    enforced by this happening automatically inside signup, not a
    separate step

Player → Web Frontend → Backend API: GET /leagues/global/leaderboard
  → Core.Leagues (COMP-02): GetGlobalLeaderboardAsync — **REQ-409
    (2026-07-20/S-060), superseding this route's original REQ-401/404
    SUM-based ranking and REQ-406's live fold onto it (both retired, not
    left dormant):**
    → Core.Leagues' own persistence: member user ids for the global league
    → Core.Scoring (COMP-04, via
      IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync): each member's
      per-round SUM(FinalPoints), one figure per *closed* round they have
      >=1 Guess in — computed database-side, grouped by round
    → Core.Leagues: members with fewer than 5 qualifying rounds are
      dropped entirely (absent, not ranked with a default); the rest are
      ranked ascending (ADR-0021: lowest wins) by the **median** of their
      per-round totals — ties broken by display name
  → sliced into a `cursor`/`pageSize`-bounded page in memory (REQ-607,
    S-034) — the per-round totals are database-side, but the median
    computation, ranking, and pagination are not; see
    implementation-document.md §6 for why this is an accepted tradeoff at
    Tier 0 scale, not a boundary change. No IRoundRepository/active-round
    resolution happens on this route any more — a still-changing round has
    no resolved meaning as one data point in a median (REQ-409's own
    reasoning), so this route no longer touches Core.Rounds or
    ILiveRoundContributionService at all

Player → Web Frontend → Backend API: GET /leagues/global/leaderboard/active-round
  (S-053, REQ-407) → Backend API (LeaderboardEndpoints): resolve the
  currently active round, if any (IRoundRepository.GetActiveByGameKeyAsync,
  same REQ-303 pattern RoundEndpoints uses — the Api layer is the one place
  allowed to hardcode GridGameModule.XGGridGameKey, ADR-0003; Core.Leagues
  below never does) → 404 ("No active round") when none exists
  → Core.Leagues (COMP-02): GetActiveRoundLeaderboardAsync
    → Core.Scoring (COMP-04, ILiveRoundContributionService, ADR-0031): the
      active round's per-cell live contribution, participant-only,
      recomputed fully in memory on every single request, no
      caching/snapshot anywhere in this path — this is the *only* leg of
      this whole flow that still calls ILiveRoundContributionService, now
      that REQ-409 removed the fold onto the all-time route above

Player → Web Frontend → Backend API: GET /leagues/global/leaderboard/closed-rounds[/{roundId}]
  (S-054, REQ-408) → Core.Leagues (COMP-02): GetClosedRoundsAsync /
  GetClosedRoundLeaderboardAsync → Core.Rounds (COMP-03, via
  IRoundRepository, gated on the new Round.ClosedAt column — see COMP-03's
  status note above) for the browsable round list, then Core.Scoring
  (COMP-04) for that one round's locked, never-recomputed
  SUM(final_points) — REQ-206's own formula, filtered to a single round;
  404/409 distinguish "round not found" from "round not closed yet"

Player → Web Frontend → Backend API: GET /leagues/global/leaderboard/window/{resolution}
  (S-027, REQ-405) → Core.Leagues (COMP-02): GetWindowedLeaderboardAsync —
  a fourth, independent scope (round/week/month/year), locked-rounds-only,
  ranked by a plain SUM(FinalPoints) within the calendar-aligned window
  ending "now" for the requested resolution — REQ-409's median change above
  applies only to the all-time route, not this one

Person → Web Frontend → Backend API: POST /leagues (REQ-402, S-063)
  → Core.Leagues (COMP-02): LeagueService.CreateCustomLeagueAsync — creates
    a League(Type="custom") with a freshly generated, collision-checked
    InviteCode (in-app pre-check plus a DB-level unique index as the
    race-safety net) and adds the creator as its first member, in the same
    call — never a separate step a caller could skip

Person → Web Frontend → Backend API: POST /leagues/join (REQ-403, S-063)
  → Core.Leagues (COMP-02): LeagueService.JoinByInviteCodeAsync — resolves
    the invite code to its League and adds the caller as a member;
    re-joining a league already belonged to is an idempotent success, an
    unrecognized code is a clear 404, no membership ever created on that
    path

Player → Web Frontend → Backend API: GET /leagues/mine (REQ-402/403, S-063)
  → Core.Leagues (COMP-02): LeagueService.GetMemberLeaguesAsync — lists the
    caller's own custom-league memberships (name + invite code only, no
    per-league leaderboard data — see below)
```

Custom leagues (REQ-402/403) are now built — create, join, and "list my
own" — but a custom league's own leaderboard (REQ-404's full per-league
picker/read) is deliberately not: every leaderboard route above (all-time,
active-round, closed-rounds, windowed) still only ever reads the one global
league, regardless of which custom leagues a player belongs to. This
remains tracked follow-up work, not a boundary gap. All-time/active-round/
closed-rounds/windowed together share SCREEN-03 as a single leaderboard
surface with a scope selector ("All-time" / "Current Round" / "Previous
Rounds" / "Time Windows" — renamed 2026-07-20, S-056, from "This round
(live)"/"Past rounds"; purely cosmetic, no REQ specifies exact tab
wording), not separate screens (REQ-407/408's own resolved UX placement
decision); custom leagues (REQ-402/403) have their own separate
`LeaguesScreen.tsx` (create/join/list), not a SCREEN-03 tab.

**6.2b xG Path clue reveal and guess submission flow** (realizes REQ-1203,
REQ-1204, REQ-1205, REQ-1207 — added S-082, 2026-07-27)

```
Player → Web Frontend → Backend API: GET /path/current
  (XGArcade.Api.Path.PathEndpoints) → Core.Rounds (IRoundRepository):
  resolve the active "xg-path" round, 404 if none
  → Games.XGPath (COMP-11, via IPathInstanceRepository): read
    PathInstance/PathPuzzle directly, bypassing IGameModule — ADR-0016's
    direct-repository-read pattern, confirmed for a second game module by
    ADR-0048, mirroring RoundEndpoints' GET /rounds/current (§6.2) exactly
  → Games.XGPath (via IGameModule.GetMaxAttemptsForCellAsync, ADR-0041):
    resolve each puzzle's attempt cap (fixed 7) to compute its locked state
  → Data.PlayerStore (COMP-06): bulk-read PlayerCareerStint (ADR-0042),
    Player.Position/BirthYear (REQ-1207), and PlayerAttribute's
    "nationality" rows (display-only, never PlayerOverride/
    HasEffectiveAttributeAsync) for every puzzle's target player, once for
    the whole instance
  → Games.XGPath: PathClueSequenceBuilder assembles the full 7-turn
    sequence per puzzle, then the response includes only the turns the
    requesting player's own attempt count has unlocked so far — the target
    player's identity is never included unless that player's own guess
    already resolved it correctly (REQ-1204)

Player → Web Frontend → Backend API: POST /rounds/{roundId}/cells/{cellId}/guesses
  (XGArcade.Api.Guesses.GuessEndpoints — the SAME generic, game-agnostic
  endpoint §6.2 documents for xG Grid; no second write endpoint exists for
  xG Path)
  → Core.Scoring (COMP-04, GuessSubmissionService): resolve Round.GameKey
    ("xg-path") → IGameModuleResolver
  → Games.XGPath (COMP-11): XGPathGameModule.ScoreSubmissionAsync —
    resolves the guess via the same Player.NormalizedFullName/
    PlayerAlias.NormalizedAlias matching order GridGameModule.FindMatchAsync
    uses (ADR-0007's shared pipeline), correct iff the resolved candidate's
    PlayerId equals the puzzle's one target — deliberately no fuzzy-matching
    stage and no REQ-209-style disambiguation prompt (see COMP-11's own
    table entry above for why this is a confirmed scope decision, not a gap)
  → Data (XGArcade.Data): Guess persisted, correctness shown immediately,
    locks on a correct guess or once the 7-attempt cap (REQ-1205) is reached
```

A submitted cellId/instanceId that doesn't resolve to a real puzzle throws
`PathScoringException`, which — like xG Grid's `GuessScoringException` —
now derives from the shared `XGArcade.Core.Games.GameEntityNotFoundException`
(added S-082), so `GuessEndpoints`'s single catch clause handles both games
without a per-game `using`; this is the same "define the shared signal in
`Core`, never in a game module's own assembly" precedent §6.2 above already
documents for `LiveLookupUnavailableException`.

**6.2c Player suggestion submission flow** (realizes REQ-215 — added S-089,
2026-08-01; submission half only, REQ-509/510's admin review/commit half
is S-090, not yet built)

```
Player → Web Frontend (SuggestionEntry.tsx, mounted by GuessInput.tsx only
  after a guess is scored incorrect or a REQ-211 live lookup times out)
  → Backend API: POST /rounds/{roundId}/cells/{cellId}/suggestions
    (XGArcade.Api.Suggestions.SuggestionEndpoints)
  → Core.Users (IUserRepository): resolve the caller from the bearer
    token; 401 if no match; 403 if the resolved user IsGuest (server-side
    enforcement, regardless of what the client UI shows)
  → Core.Rounds (IRoundRepository): resolve Round.GameKey
  → owning game module (COMP-05/COMP-11, via IGameModuleResolver,
    ADR-0003): IGameModule.GetCellCategoryTypesAsync(instanceId, cellId) —
    the authoritative row/col category types, never trusted from the
    request; 404 if the cell doesn't resolve (GameEntityNotFoundException)
  → Data.PlayerStore (COMP-06): PlayerSuggestion + PlayerSuggestionClub
    persisted, Status = Pending — no write to PlayerAttribute,
    PlayerOverride, PlayerNameIndex, or the triggering Guess row
```

This flow deliberately never reaches `Data.PlayerNameIndex`/COMP-10 or
`Data.PlayerStore`'s correctness-checking tables (`PlayerAttribute`/
`PlayerOverride`) — boundary rule 5 and ADR-0052 both apply: a suggestion
is a pending human claim, not a data write, until a future admin commit
(REQ-509, S-090, not yet built) resolves it through the normal
`PlayerOverride`/`PlayerAttribute` write path REQ-501 already uses. The
row/col category type lookup is the one part of this flow with its own
history: the original S-089 commit read `GridCell` directly via
`IGridInstanceRepository` from the Api layer, bypassing `IGameModule` —
a boundary rule 2 violation caught by `architecture-reviewer` before
merge and corrected to the `IGameModule.GetCellCategoryTypesAsync` path
shown above; see COMP-05/COMP-11's own S-089 status note for the full
account.

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
`LockedAt` shape rather than a separate audit-log table. Because the review
queue is now empty by construction going forward (ADR-0032), this new
endpoint's practical caseload is limited to whatever
`unverified`-at-write-time backlog exists from before that ADR shipped,
plus any future player-suggestion channel once one exists.

**2026-07-20/S-061 status:** "Remove the data point" — the other half of
the gap the two status notes above flagged — is now built too:
`POST /admin/player-data/remove` (`XGArcade.Api.Admin.AdminEndpoints`,
Admin policy, REQ-503's second 2026-07-20 extension) takes one or more
`PlayerData` ids and hard-deletes each independently, again through
`IPlayerStoreRepository` (COMP-06) only — `PlayerStoreRepository
.RemovePlayerDataAsync`, no new data-access path. Unlike approve, a row
does not need to still be `"unverified"` to be removed. Because the row is
gone rather than mutated, there is no `ApprovedByAdminId`-shaped pair of
audit columns to set; "the action is logged with admin_id and a timestamp"
(REQ-503) is satisfied by a structured `ILogger` line per successfully
removed row instead, not a new audit-log table. Approve, correct
(`PlayerOverride`), and remove are now all built — REQ-503's full scope.

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

**S-072 addition (REQ-718/ADR-0038):** two more entry points join the same
`IAccountDeletionService` call — `AuthController.Logout` and
`InternalGuestCleanupEndpoints`'s scheduled job (see §6.10) — a fourth and
fifth *caller*, not a second/third/fourth *implementation*.

**S-073 addition (REQ-508):** a sixth entry point joins the same call —
`Admin → Web Frontend (admin view) → Backend API: POST
/admin/accounts/guests/clear` (`XGArcade.Api.Admin.AdminAccountsEndpoints`,
registered unconditionally including Production, unlike
`AdminManagementEndpoints` above) — which selects every currently-matching
guest id via a new, unfiltered `IUserRepository.GetAllGuestIdsAsync` (not
REQ-718's age-filtered queries — see the COMP-01 status note above for why)
and then joins this diagram at the same `IAccountDeletionService` call
every other entry point uses; everything below that point is identical,
unchanged, and not duplicated.

**6.9 Backup flow** (realizes REQ-901 — Supabase's free tier has no built-in backups)

```
[scheduled, daily — backup-database.yml]
GitHub Actions → Production database: pg_dump (full export)
  → Store as a workflow artifact (or equivalent off-platform storage),
    with a bounded retention window, separate from the primary database
    and from Supabase entirely — see infra/README.md for the retention
    policy and restore procedure
```

**6.10 Guest account cleanup flow** (realizes REQ-718, ADR-0038)

```
Rule 1 (logout, best-effort):
User → Web Frontend: logs out
  → Backend API: POST /auth/logout ([Authorize])
    → Core.Users (COMP-01): if IsGuest && ClaimedAt is null,
      IAccountDeletionService.DeleteAccountAsync(user.Id) — same mechanism
      as §6.8
    → Always responds 204, regardless of outcome — Web Frontend clears
      localStorage immediately, never blocked or delayed by this call

Rules 2 and 3 (scheduled purge, safety net):
[scheduled, daily, 07:00 UTC — purge-guest-accounts.yml]
GitHub Actions → Backend API: POST /internal/purge-guest-accounts
  (bearer-token-protected, same InternalJobAuthorization helper §6.1 uses)
  → Core.Users: IUserRepository.GetUnclaimedGuestsOlderThanAsync(30 days)
    — rule 2 (IsGuest && ClaimedAt IS NULL && CreatedAt < cutoff)
  → Core.Users: IUserRepository.GetInactiveGuestsOlderThanAsync(7 days)
    — rule 3 (IsGuest && LastActiveAt < cutoff, no ClaimedAt condition)
  → For every matching row (deduped — a row can satisfy both):
    IAccountDeletionService.DeleteAccountAsync(user.Id) — same mechanism
    as §6.8
  → Returns a count of each rule's matches and the total accounts removed
```

**Built as (S-072):** `AuthController.Logout` and
`XGArcade.Api.Auth.InternalGuestCleanupEndpoints` are the two new entry
points — see the COMP-01 status note above for exactly what's new on the
data-model side (`User.LastActiveAt`, the two new `IUserRepository`
queries). Both call sites reuse `IAccountDeletionService.DeleteAccountAsync`
unmodified (§6.8) — no new deletion logic was written for this flow.
`/internal/purge-guest-accounts` runs in every environment, following
`/internal/generate-round`'s own precedent (§6.1) for why a
bearer-token-gated `/internal/*` endpoint whose only caller is a scheduled
job isn't restricted to non-Production the way `XGArcade.Testing`/COMP-09
is (ADR-0006).

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
| ADR-0034 | Dark mode is an explicit System/Light/Dark toggle, stored in `localStorage` (not `prefers-color-scheme`-only, not a `User`-level column) | Accepted |
| ADR-0035 | National teams (P1532) are a per-row flag on `CountryDefinition`, not a separate category type | Accepted |
| ADR-0036 | Guest play is a real `User` row via backend-mediated Supabase Anonymous Sign-ins (`User.IsGuest` flag), not a client-local scheme | Accepted |
| ADR-0037 | Cloudflare Turnstile, passed through unmodified to Supabase's native captcha verification, hardens guest creation, signup, login, and account-deletion password re-confirmation against scripted abuse (widened twice from guest-only on 2026-07-25, after Supabase's "Enable Captcha Protection" toggle proved project-wide rather than per-endpoint) | Accepted |
| ADR-0038 | Guest account cleanup reuses `IAccountDeletionService`; activity tracked via a new `User.LastActiveAt`, updated only on genuine engagement | Accepted |
| ADR-0039 | Hash-based, hand-rolled client-side routing for URL-reflected navigation (REQ-721) — no `react-router`, no server-side SPA-fallback dependency | Accepted |
| ADR-0040 | `Core.Scoring` resolves an `IScoringStrategy` per `GameKey`, extracting xG Grid's existing formula as the first implementation with no formula change | Accepted |
| ADR-0041 | Guess attempt cap becomes a per-cell value the owning game module reports (`IGameModule`), not a shared `GuessRules.MaxAttemptsPerCell` constant | Accepted |
| ADR-0042 | New `PlayerCareerStint` entity (COMP-06) for ordered, dated career stint data, populated from the same Wikidata `P54` fetch as `PlayerAttribute` | Accepted |
| ADR-0043 | Global League's all-time leaderboard ranking is scoped per `GameKey`, not merged across games | Accepted |
| ADR-0044 | Per-word decomposition (`PlayerNameIndexWord`), not `pg_trgm`, for `PlayerNameIndex` surname-prefix matching | Accepted |
| ADR-0045 | xG Path puzzle generation: `PathTemplate`/`PathInstance`/`PathPuzzle` entity shape, `PathPuzzle.TargetPlayerId` as a real FK to `Player`, and the settled reading of REQ-1201's two ambiguous eligibility phrases | Accepted |
| ADR-0046 | A timeout during REQ-211's guess-time live lookup is a distinct, non-scoring exception signal (`LiveLookupUnavailableException`/`GuessSubmissionOutcome.LiveLookupUnavailable`/HTTP 503), not a swallowed empty result | Accepted |
| ADR-0047 | REQ-1201's seeded-club eligibility stint must also clear a 20-appearance floor (or have an unknown count) — closes the "one token appearance at a big club" loophole | Accepted |
| ADR-0048 | ADR-0016's direct-repository-read pattern for read-only display endpoints (`GET /rounds/current`, `GET /path/current`) is confirmed as the platform's permanent shape, not superseded by a generic `IGameModule` read method | Accepted |
| ADR-0049 | `IScoringStrategy.ScoreCorrectGuess` takes the whole `Guess` plus a plain `int maxAttemptsForCell` (resolved once per cell by `ScoreLockingService` via ADR-0041's mechanism), never a direct `IGameModule` dependency — closes ADR-0040's own deferred parameter-shape follow-up | Accepted |
| ADR-0050 | A new `ConfirmedLowMatchPair` table (COMP-06), not a column on `PlayerAttribute`/`PlayerData` or an in-memory-only signal, persists "checked, genuinely below `MinValidAnswers`" per Country×Club/Club×Club pair so `PlayerCacheWarmingService` stops re-querying it every run; invalidated by `StaleClubAttributeCleaner`/`purge-player-pool`, excluded from the prod/dev sync allowlist | Accepted |
| ADR-0051 | Per-`GameKey` round scheduling: `IRoundSchedulingOptionsResolver` mirrors `IScoringStrategyResolver`'s pattern, `/internal/generate-round` stays one endpoint dispatching narrowly by `gameKey`, `generate-round.yml`'s existing cron is extended rather than duplicated, and `GridSize`/`PuzzleCount` move onto each game's own options class | Accepted |

## 11. Glossary

See `requirements-document.md` §2 for domain terms (Grid, Cell, Round, Guess,
Uniqueness score, Override, Unverified data). This document additionally
uses:

| Term | Meaning |
|---|---|
| Container | A separately deployable/runnable unit (C4 terminology) |
| Component | A cohesive module within a container, with a defined responsibility |
| Effective data | The result of merging PlayerData with any PlayerOverride, override wins |
