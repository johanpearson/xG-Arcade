---
doc_id: architecture-document
title: Architecture Document
version: "1.51"
status: draft
last_updated: 2026-09-05
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

Version 1.06 · 2026-08-18
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
| External data sources | External system | Wikidata (player attribute data), API-Football (still-dormant Tier 1 fallback for player attribute data, ADR-0011), football-data.org (xG Predict fixtures/results, ADR-0099) |
| Scheduler | External system | GitHub Actions cron — triggers round generation and sync jobs |
| Auth provider | External system | Supabase Auth — identity, session management, and account confirmation state |
| Email provider | External system | Resend — sends auth emails (via Supabase custom SMTP) and product notification emails (via direct API from Core.Notifications) — see ADR-0005 |

## 4. Containers (C4 Level 2)

| ID | Container | Responsibility | Tech |
|---|---|---|---|
| CONT-01 | Web Frontend | Renders grid, guess input, leaderboards, auth/account screens (login, signup, delete-account), admin review UI | TypeScript / React, hosted on Azure Static Web Apps |
| CONT-02 | Backend API | Business logic, request handling, scoring, orchestration | C# / ASP.NET Core, containerized, hosted on Azure Container Apps — as of ADR-0087 (S-180), also compiles in the new `XGArcade.Storage` project (the avatar-image storage client), referenced by `XGArcade.Api` alongside `XGArcade.Core`/`XGArcade.Data`; not a separate deployable container, still one image |
| CONT-03 | Database | Persists users, leagues, rounds, guesses, player data, overrides | PostgreSQL (Supabase); Supabase Auth also used for identity |
| CONT-04 | Sync Worker | Scheduled job that refreshes player data from external sources | C# console job, containerized, triggered by GitHub Actions |
| CONT-05 | Round Scheduler Job | Scheduled job that generates new Round + game-specific instance (e.g. a GridInstance for xG Grid) | C# console job / API endpoint, triggered by GitHub Actions |

Data flow between containers is always frontend → backend API → database; no
container other than the Backend API writes to the database directly, so
business rules (e.g. override precedence) are enforced in one place.

## 5. Components (C4 Level 3) — inside the Backend API

Each row describes the component's **current** responsibility and shape only.
Full evolution history (every incremental change, which story built it, which
ADR decided it) lives in the ADRs themselves, not here — see §5.3 for a
pointer from each component to its ADR trail. This section was rewritten
2026-08-11 (S-116) to stop accreting a dated changelog inline; if you're
about to add "**Extended (DATE, S-xxx):**" prose to a cell below, write an
ADR (if the change is structural) and/or a `docs/CHANGELOG.md` line instead,
and update the cell to describe the *new* current state in place of the old
one.

| ID | Component | Responsibility (current state) | Maps to (implementation doc) |
|---|---|---|---|
| COMP-01 | Core.Users | User accounts and Supabase Auth integration, including guest accounts (`IsGuest`/`ClaimedAt`/nullable `Email`, REQ-717). `IAccountDeletionService` (`XGArcade.Core.Auth`) is the single implementation behind self-service deletion (REQ-710), admin-triggered deletion (REQ-505/506), and scheduled guest cleanup (REQ-718, `/internal/purge-guest-accounts`) — it anonymizes `Guess` rows (COMP-04), removes `LeagueMembership` rows (COMP-02), and deletes the Supabase Auth identity via `ISupabaseAuthClient.DeleteUserAsync` (needs the privileged `Supabase:ServiceRoleKey`, ADR-0026). **Per-game data purge (S-201, ADR-0101, 2026-08-31):** `AccountDeletionService` also depends on `IEnumerable<IGameModule>` (COMP-03/`Core.Games`) and calls a new `IGameModule.PurgeUserDataAsync(userId)` once per registered module — never a direct dependency on any game-specific repository (ADR-0003). xG Grid/xG Path implement it as a no-op (their only per-user table, `Guess`, is Core's own and already handled above); xG Predict's implementation is COMP-15's own concern (see that row). `User.NormalizedDisplayName` enforces REQ-701 uniqueness via a DB unique index plus an app-level pre-check (ADR-0019). `AdminAccountsEndpoints` (REQ-507/508) exposes account metrics/guest counts/bulk-clear, registered unconditionally including Production since it acts on real account data, not seeded/test data. | `XGArcade.Core` |
| COMP-02 | Core.Leagues | `ILeaderboardService` (read) and `ILeagueService` (write) in `XGArcade.Core.Leagues`. Every user auto-joins one Global League at signup (REQ-401); custom leagues (REQ-402/403) can additionally be created/joined via `POST /leagues`/`POST /leagues/join`, with a collision-checked 6-character invite code. Four leaderboard scopes exist, all `GameKey`-scoped (REQ-410, defaulting to xG Grid when the caller omits `gameKey`): all-time (`GetGlobalLeaderboardAsync`, REQ-409 — ranks by median per-round `SUM(FinalPoints)` across qualifying closed rounds, ≥5 rounds to be ranked, never live), active-round (REQ-407, live via COMP-04's `ILiveRoundContributionService`), closed-rounds (REQ-408), and windowed (REQ-405, calendar-aligned round/week/month/year). REQ-404's full per-custom-league leaderboard remains deferred (Tier 1). `GetUserStatsAsync` (REQ-411, S-178) is a fifth, per-player read — rounds played/best/average `FinalPoints` plus current all-time rank, one `GameKey` and `UserId` at a time — reusing the same `GetPerRoundFinalPointsByUserIdsAsync` query and a ranked-members helper extracted from `GetGlobalLeaderboardAsync`, not a new aggregate path; exposed via `GET /users/{userId}/stats`. COMP-02 never references a game module directly — every `GameKey` it needs is passed in by the Api layer (ADR-0003 intact). **Per-`GameKey` sort direction (ADR-0095, 2026-08-30):** `GetActiveRoundLeaderboardAsync`/`GetClosedRoundLeaderboardAsync`/`GetWindowedLeaderboardAsync` no longer assume ADR-0021's ascending order platform-wide — each resolves ascending/descending via `IScoringStrategyResolver(gameKey).LowerIsBetter` (COMP-04), through a shared `RankByTotalPoints` helper. `GetGlobalLeaderboardAsync`/`GetRankedMembersAsync`'s separate median-based ranking (REQ-409/410) was initially not part of this migration and remained unconditionally ascending for every `GameKey`; this gap was closed same-day as a direct follow-up — `GetRankedMembersAsync` now also resolves `IScoringStrategyResolver(gameKey).LowerIsBetter`, its own `OrderBy`/`OrderByDescending` branch rather than reused via `RankByTotalPoints` (tuple/return-type shape mismatch). All four `LeaderboardService` ranking scopes now resolve sort direction per `GameKey`, matching REQ-1304's text with no remaining gap. **Per-`GameKey` round-total source (ADR-0100, 2026-08-31, S-199):** `LeaderboardService` no longer injects `IGuessRepository`/`ILiveRoundContributionService` directly — its four scopes (`GetRankedMembersAsync`/`GetUserStatsAsync`, `GetActiveRoundLeaderboardAsync`, `GetClosedRoundLeaderboardAsync`, `GetWindowedLeaderboardAsync`) each resolve a `Core.Scoring.IRoundScoreSource` via a new `IRoundScoreSourceResolver` (mirroring `IScoringStrategyResolver`'s per-`GameKey` shape) and call its matching method instead. This is what makes `"xg-predict"` totals correct for the first time — see COMP-04/COMP-15's own rows for the two implementations. `GetRankedMembersAsync`/`GetUserStatsAsync` also now fetch every closed round for the requested `GameKey` up front (`IRoundRepository.GetClosedByGameKeyAsync(gameKey, 0, take)`, a large `take` for MVP scale, no new repository method) and hand it to the resolved source, since `PredictRoundScoreSource` needs it — `GuessRoundScoreSource` ignores it. | `XGArcade.Core` |
| COMP-03 | Core.Rounds | `RoundGenerationService.GenerateNextRoundIfNeededAsync` (REQ-301) takes a `gameKey` and resolves per-game scheduling options via `IRoundSchedulingOptionsResolver` (ADR-0051, mirroring COMP-04's `IScoringStrategyResolver`) — three instances registered today (`"xg-grid"`, `"xg-path"`, and, as of the 2026-08-30 round-scheduling wiring story, `"xg-predict"`), each with its own `RoundDuration`. It creates a `Round` only once the owning game module's `GenerateInstanceAsync` succeeds, then closes the *previous* round (never the one just created) via `IRoundCloseService` (ADR-0022). `Round.ClosedAt` is set only after score-locking completes, never before or concurrently. Each created `Round` is also assigned `SequenceNumber` (REQ-304/ADR-0071): `MAX(SequenceNumber) + 1` scoped to its own `GameKey` (via `IRoundRepository.GetMaxSequenceNumberByGameKeyAsync`), guarded against concurrent duplication by a `(GameKey, SequenceNumber)` unique index rather than an explicit transaction — a display-only label surfaced on every round-shaped DTO, never a routing/FK identifier (`Round.Id` remains the only real one). `POST /internal/generate-round` (bearer-token-gated, registered in every environment) is the one production trigger, called once per `GameKey` — `generate-round.yml`'s single daily cron until S-136/ADR-0072, now each `GameKey`'s own independent daily cron (`generate-grid-round.yml`/`generate-path-round.yml`/`generate-predict-round.yml`, the third added by the 2026-08-30 round-scheduling wiring story). `IRoundCloseService.CloseRoundAsync` has three callers — `RoundGenerationService`, `AdminManagementEndpoints` (REQ-505), and the non-Production test-data force-close endpoint — never a second implementation. | `XGArcade.Core` (`IRoundRepository`/`RoundRepository` in `XGArcade.Data`) |
| COMP-04 | Core.Scoring | `GuessSubmissionService` (REQ-201/202/210) and `IScoreLockingService` (REQ-205) are COMP-04's two entry points. Scoring is pluggable per `Round.GameKey` via `IScoringStrategy`/`IScoringStrategyResolver` (ADR-0040, mirroring `IGameModuleResolver`'s shape): `UniquenessScoringStrategy` (xG Grid, REQ-204 — excludes the guesser's own guess from the ratio, lowest-wins, ADR-0020/ADR-0021) and `ClueEfficiencyScoringStrategy` (xG Path, REQ-1206, scores off `Guess.AttemptCount`, no uniqueness concept). `ScoreLockingService.MaterializeUnansweredCellsAsync` synthesizes an incorrect-guess row for every cell a round participant never attempted, before any strategy runs. The per-cell attempt cap is resolved through `IGameModule.GetMaxAttemptsForCellAsync` (ADR-0041; xG Grid: 2, xG Path: 7). `IGameModule` also exposes `GetCellCategoryTypesAsync` (REQ-215 — xG Grid implements it, xG Path throws `NotSupportedException`, no suggestion UI wired up for xG Path yet) and `ResolveWrongGuessPlayerAsync` (REQ-216/ADR-0057, xG Grid only — cache-first/Wikidata-fallback display name+photo for a locked, incorrectly-guessed cell, no correctness implication). `Guess.CellId` is a raw `Guid` typed only as "opaque submission reference" — a deliberate v1 simplification (confirmed no real FK exists in `XGArcadeDbContext`, so it already works for a second game with no schema change). **`IScoringStrategy.LowerIsBetter` (ADR-0095, 2026-08-30):** every strategy now declares its sort direction; `UniquenessScoringStrategy`/`ClueEfficiencyScoringStrategy` both return `true`, unchanged. `XGPredictScoringStrategy` (`GameKey="xg-predict"`) is the one exception — `LowerIsBetter = false` — and implements REQ-1304's three-component formula on a separate public method, `ScorePrediction`, not the interface's own `ScoreCorrectGuess` (which throws `NotSupportedException`: architecturally unreachable for this `GameKey`, since ADR-0096 established xG Predict never writes `Guess` rows — see ADR-0095's amendment for the full reasoning and the standing item it leaves for REQ-1305). **`IRoundScoreSource`/`IRoundScoreSourceResolver` (ADR-0100, 2026-08-31, S-199):** a new, small abstraction (also `Core.Scoring`) COMP-02's `LeaderboardService` resolves per `Round.GameKey` instead of calling `IGuessRepository`/`ILiveRoundContributionService` directly — mirrors `IScoringStrategy`/`IScoringStrategyResolver`'s shape, but the resolver is built from an explicit `GameKey -> IRoundScoreSource` dictionary at the composition root rather than a `FirstOrDefault` scan, since this interface carries no `GameKey` property of its own. `GuessRoundScoreSource` (this component) is a thin, zero-behavior-change pass-through to the existing `IGuessRepository`/`ILiveRoundContributionService` calls, registered twice (once per `GameKey`, `"xg-grid"`/`"xg-path"`) — see COMP-15's row for the `"xg-predict"` implementation, `PredictRoundScoreSource`, which lives in `Games.XGPredict` instead since it wraps `IPredictInstanceRepository`. | `XGArcade.Core` (`Scoring/`; `Guess`/`IGuessRepository` in `XGArcade.Data`) |
| COMP-05 | Games.XGGrid | As of ADR-0068 (2026-08-11), `GridGameModule` is a thin `IGameModule` adapter (~160 lines) composing three independently-registered classes, no facade: `IGridGenerationService`/`GridGenerationService` (grid generation — each row/column header independently picks its own Country/Club/Trophy category type per REQ-107/108, ADR-0089, pairing legality enforced via `CategoryPairingRules`), `IGridNameMatcher`/`GridNameMatcher` (three-stage name matching and disambiguation, REQ-207-209), and `IGridLiveLookupDispatcher`/`GridLiveLookupDispatcher` (the REQ-211 guess-time live-lookup fallback, also shared by `GridGenerationService`'s own generation-time cache-miss path). `GridGameModule` itself still owns the small set of trivial single-repository-call `IGameModule` methods that don't belong to any of the three (`GetCellIdsAsync`, `GetCellCategoryTypesAsync`, `GetMaxAttemptsForCellAsync`) plus the REQ-211 gate check — as of ADR-0070 (S-128), that gate check is preceded by a config-driven `GridLiveLookupOptions.Enabled` flag (default `true`) that can disable the whole guess-time fallback (never `GridGenerationService`'s own REQ-103 live lookup, a separate call path through the same shared `IGridLiveLookupDispatcher`) — and must keep implementing `IGameModule` directly since that contract has real external callers (`Core.Scoring`, `Core.Rounds`, `XGArcade.Api`). `PlayerCacheWarmingService` (REQ-110) proactively warms COMP-06's cache for every reference pairing except Trophy pairs — a known, flagged latency/reliability gap now that Trophy pairings are reachable in production (ADR-0061), not a correctness gap, since `GridGenerationService`'s own live-lookup fallback still covers a cold cell. Reaches COMP-06 only through its repository interfaces (boundary rule 1) — `IsConfirmedLowAsync`/`RecordConfirmedLowAsync` (ADR-0050) and the persistent-technical-failure markers (ADR-0052) both gate cache-warming's live queries. Run as its own CLI verb, not an HTTP endpoint (ADR-0024). As of ADR-0093 (S-189), COMP-07's `RecentTransferSweepService` also feeds this component's answer-key data path directly — a genuinely new club arrival it discovers is a valid `GridGenerationService`/guess-correctness answer immediately, not only after ADR-0090's rotation; `GridGenerationService`'s own live-correctness reads (`CountPlayersWithBothAttributesAsync`, `HasEffectiveAttributeAsync`) are unchanged by this, since both already read `PlayerAttribute`/`PlayerOverride` live and never consulted `ConfirmedLowMatchPair` either way. **Bug fix (2026-09-04, ADR-0106):** REQ-211's guess-time live lookup determines guess correctness from the intersection query's own match list, unaffected by this bug — but the `PlayerCareerStint` byproduct rows it persists alongside that correctness result (via COMP-07's `WikidataLookupService.PersistCareerStintsAsync`) could silently omit a real stint whenever Wikidata recorded no start-time qualifier for it, the same underlying parser gap ADR-0106 fixed for xG Path/xG Connect's own fetches — found by architecture review and fixed in the same change. This is exactly the kind of narrow, incomplete byproduct write ADR-0105 already had to work around (never trust "has any row" as "has a full career"); no code in this module itself changed. | `XGArcade.Games.XGGrid` |
| COMP-06 | Data.PlayerStore | `Player`/`PlayerData`/`PlayerAttribute`/`PlayerAlias`/`PlayerOverride`, with override-merge precedence in exactly one place (`HasEffectiveAttributeAsync`, ADR-0015). `PlayerCareerStint` (ADR-0042, xG Path/COMP-11 only) holds ordered career stints. `ConfirmedLowMatchPair`/`PairLookupFailure` (ADR-0050/ADR-0052) are cache-warming process-state markers, deliberately excluded from the prod/dev sync allowlist (ADR-0009) since they're derived, not objective Wikidata fact. `PlayerSuggestion`/`PlayerSuggestionClub` (ADR-0053, REQ-215/509/510) hold a pending player-submitted claim until an admin commit writes through the normal `PlayerAttribute`/`PlayerOverride` path — never read by correctness-checking or written to by COMP-10. As of ADR-0067 (2026-08-11), the original 772-line/43-method `PlayerStoreRepository` no longer exists — COMP-06 is eight independently-registered repositories (`IPlayerRepository`, `IPlayerDataRepository`, `IPlayerAttributeRepository`, `IPlayerAliasRepository`, `IPlayerOverrideRepository`, `IPlayerBackfillRepository`, `IPlayerCareerStintRepository`, `IPlayerDataQualityRepository`), together still the only path to this data (boundary rule 1), no facade — a caller needing multiple concerns injects multiple narrow repositories. `Player.FullName`/`Position`/`BirthYear`/`PhotoUrl` are otherwise set once at creation and never re-synced by any automatic path; `IPlayerRepository.GetPlayerForRefreshAsync`/`UpdatePlayerAsync` (ADR-0086, REQ-513) is the one deliberate, narrow, admin-triggered-only exception, called solely from `AdminEndpoints.cs`'s `POST /admin/players/{id}/refresh-from-wikidata`. | `XGArcade.Data` |
| COMP-07 | DataSync.Clients | `IWikidataClient`/`WikidataLookupService` — Wikidata SPARQL queries and the live-lookup fallback. Dispatches Country×Club-shaped queries through one of two query-property paths, `P27` (citizenship) or `P1532` ("country for sport"), chosen from `CountryDefinition.UsesCountryForSportProperty` (ADR-0035); the same flag shape (`IsTeamTrophy`) selects between individual-award (`P166`) and team-competition (`P1344`/`P3450`/`P1346` join) trophy query shapes (ADR-0061). Per-call timeout is selected by a `WikidataQueryTimeoutTier` (`Default`/`CacheWarming`, ADR-0050). By-QID full-career-history, by-nationality-pool, and by-club-pool query shapes (ADR-0054/ADR-0055/ADR-0069) feed `PlayerCareerStintRefreshService`/`PlayerCareerPrefetchService` — `PlayerCareerPrefetchService`'s candidate pool sweeps every seeded `CountryDefinition` row AND (as of ADR-0069) every seeded `ClubDefinition` row, not countries alone; a sitelink-count query backs `PlayerFamiliarityService` (ADR-0056, COMP-11's target-familiarity filter). All query methods — the 9 `CategoryType`-intersection queries and the by-QID/by-nationality/by-club/familiarity ones above — now share one HTTP/timeout/retry path: the intersection queries via `RunIntersectionQueryAsync`, the rest via `RunThrowingQueryAsync`, both driven by `SparqlQueryBuilders.cs`/`SparqlResponseParsers.cs` (S-100/S-101, then S-118/S-124/S-155). A third freshness mechanism alongside ADR-0088's skip-forever default and ADR-0090's rotating bounded resweep (both on `PlayerCareerPrefetchService`'s full pool sweeps): `IRecentTransferSweepService`/`RecentTransferSweepService` runs two new targeted, date-filtered queries per seeded `ClubDefinition` (`BuildRecentClubArrivalsQuery`/`BuildRecentClubDeparturesQuery`, `pq:P580`/`pq:P582` qualifier `FILTER`s, WDQS-server-filtered) for an operator-triggered, faster-than-the-rotation check around a transfer-window deadline — it writes `PlayerCareerStint` (via ADR-0091's shared `CareerStintReconciler`) and, as of ADR-0093 (S-189), also writes `PlayerAttribute`/`PlayerData` for a genuinely new arrival, plus a targeted `IPlayerDataQualityRepository.ClearMatchPairAsync` invalidation of any now-stale `ConfirmedLowMatchPair`/`PairLookupFailure` row for the pairs that arrival affects; it still never touches `PlayerPoolSweptAt` (ADR-0092's boundary, unchanged by ADR-0093). As of 2026-08-31 (ADR-0099, superseding ADR-0094), COMP-07 also hosts `IFootballDataClient`/`FootballDataClient` (`XGArcade.DataSync.FootballData`) — a genuinely separate REST client from every `IWikidataClient` query above (a different protocol, provider, and data domain: live match fixtures/results, not Wikidata career/bio data), following `Core.IncidentReporting`'s `GitHubIssueClient` shape (typed `HttpClient`, a dedicated nullable `FootballDataApiKey` record, per-request `X-Auth-Token` header, `FootballDataClientException` on any technical failure) rather than `WikidataClient`'s SPARQL shape. Replaces the original `IApiFootballClient`/`ApiFootballClient` (ADR-0094) — API-Football's free tier turned out to exclude the current season entirely, making it structurally unusable for this game. `GetUpcomingGameweekFixturesAsync` (REQ-1301) returns a gameweek's whole fixture list unfiltered — the 5-match tightest-kickoff-clustering selection is `Games.XGPredict`'s job, not this client's. `GetFixtureResultAsync` (REQ-1305) returns a `FootballDataFixtureOutcome` (`Finished`/`PostponedOrAbandoned`/`NotYetConfirmed`) rather than caching or polling itself — point-in-time data that `PredictGradingService` re-checks until confirmed, unlike `WikidataClient`'s fetch-once-cache-permanently query methods. **Bug fix (2026-09-04, ADR-0106):** `SparqlResponseParsers.ParseCareerStintBindings` (feeding `PlayerCareerStintRefreshService`'s full-career fetch, ADR-0054/ADR-0105) used to require a usable P580 start-time qualifier to construct any stint row at all, silently dropping a real P54 statement whenever Wikidata had no start date recorded for it — a real, reported incident (a Wigan Athletic loan spell) confirmed this made ADR-0105's own "always refresh" fix ineffective for that exact stint, since the refetch kept re-discarding the same row. Now falls back to the P582 end-time qualifier as the start year when start time is missing/unparseable but end time is usable; a row with neither is still skipped (nothing to anchor a year on). Architecture review of this fix found the identical unconditional-`startTime` gap in the sibling `ParseBindings` parser (the `CategoryType`-intersection queries feeding `WikidataLookupService.PersistCareerStintsAsync`, xG Grid's REQ-211 guess-time live lookup) — the same fallback was applied there too in the same change, since it is the same defect, not a separate one. No component boundary change — purely a parsing-completeness fix inside COMP-07's two existing response parsers, applying uniformly across every caller of either (xG Path's ADR-0054 fetch, xG Connect's ADR-0105 fetch, and xG Grid's REQ-211 guess-time lookup). | `XGArcade.DataSync` |
| COMP-08 | Core.Notifications | Sends product notification emails (round results) via Resend's API; owns notification preferences. Does not handle auth emails — those are Supabase Auth's responsibility, configured with custom SMTP. See ADR-0005. | `XGArcade.Core` |
| COMP-09 | Testing.SeedManager | Test-data creation/reset/scenario API. Registered only when the environment is not Production — see ADR-0006. | `XGArcade.Api` (conditionally registered), reaches other components' normal write paths, never a separate data path |
| COMP-10 | Data.PlayerNameIndex | Broad, bulk-imported name/alias index used only for autocomplete and as the candidate pool for name matching (REQ-207/208/209) — deliberately separate from COMP-06's narrow, incrementally-built validation cache and from COMP-06's own `PlayerAlias` (ADR-0007, boundary rule 5). `PlayerNameIndex` entity + `IPlayerNameIndexRepository` live in `XGArcade.Data`; the bulk Wikidata importer (`PlayerNameIndexImporter`) lives in `XGArcade.DataSync` instead. **Bug fix (2026-09-05, ADR-0107):** `PlayerNameIndex` gains a `WikidataQid` column — a deliberate, additive reconciliation between this component's own id space and `Player.Id` (COMP-06), via each row's underlying Wikidata identity rather than by changing what `PlayerNameIndex.PlayerId` means (that field's own doc comment already called this out as something that "must be built deliberately" if ever needed). Populated by `PlayerNameIndexImporter` (already had the QID in scope, just never persisted it) and backfilled on any future re-import via `PlayerNameIndexRepository.UpsertManyAsync`'s existing update-in-place branch. Exists so `Games.XGConnect` (COMP-17) can resolve a specific autocomplete suggestion to an unambiguous real person — closing a real, reported same-name-collision bug (two different real footballers both named "Jonas Olsson"). Not a correctness-leak risk for xG Grid the way `PrimaryNationality` is (ADR-0007's own boundary rule 5 concern) — an opaque identifier reveals no category-match information. See ADR-0107. | `XGArcade.Data` |
| COMP-11 | Games.XGPath | As of ADR-0082 (2026-08-22), `XGPathGameModule` is a thin `IGameModule` adapter composing `IPathEligibilityService`/`PathEligibilityService` (registered independently, `AddScoped`, in `ServiceRegistration.cs`, no facade) — mirroring ADR-0068's `GridGameModule`/COMP-05 split exactly. `PathEligibilityService` owns REQ-1201's whole target-player eligibility pipeline (candidate narrowing, stint sanitization, the three structural checks, the BirthYear/Position floors, and ADR-0056's familiarity filter); `XGPathGameModule` itself still owns target-player selection with no-repeat cycling over the eligible pool it's handed (REQ-1201/1202/1208/1209, `PathTargetCycle`/`PathCycleTargetUsage`, ADR-0058), progressive clue reveal (`PathClueSequenceBuilder` — every club stint across 3 turns, then years, then position/nationality/age, REQ-1203), and guess scoring via exact-match name resolution with no fuzzy matching or disambiguation prompt — a deliberate difference from xG Grid, since only one target player can ever be correct. Fixed 7-clue attempt cap (ADR-0041). Reads career data from COMP-06's `PlayerCareerStint` (ADR-0042, refreshed per-round via COMP-07) and nationality from `PlayerAttribute` (display-only read). National-team caps (both youth and senior) are excluded from career-stint clues at the SPARQL level (COMP-07) and, for pre-existing rows the SPARQL fix can't retroactively clean, at read-time (`PathCareerStintFilter`). B-team/reserve-team stints (e.g. "Real Madrid Castilla") are excluded the same read-time way, chained alongside the national-team filter — there is no SPARQL-level exclusion for this category, since no B-team concept exists in the schema at all (ADR-0075). `GET /path/current` reads `PathInstance`/`PathPuzzle` directly (ADR-0016's direct-read pattern, confirmed for a second game module by ADR-0048), including a per-request `IScoringStrategyResolver` (COMP-04) call for the live `Points` field. | `XGArcade.Games.XGPath` |
| COMP-12 | Core.IncidentReporting | `IncidentReportService`/`IGitHubIssueClient` (`XGArcade.Core.IncidentReporting`) implement REQ-903: a logged-in, non-guest player can file `POST /incidents`, which creates a fixed-template GitHub issue (title/description/screen/environment plus non-PII triage context) via a fine-grained PAT (`GitHubIncidentReportToken`, set per-request, never on the shared `HttpClient`'s default headers). Rate-limited per-user (default 3/10min, a dedicated `PartitionedRateLimiter<Guid>`, distinct from the IP-partitioned auth rate-limit policies). `ICachedIncidentIssueSummaryProvider` (REQ-904/ADR-0066) gives admins a polled, cached (60s TTL, serves-stale-through-an-outage) count of open `user-reported`-labeled issues via `GET /admin/incident-reports`, reusing the same PAT's read scope — no PAT scope widening, no in-app moderation queue (a deliberate ADR-0064 boundary). | `XGArcade.Core` (`IncidentReporting/`), `XGArcade.Api` (`Incidents/`, `Admin/`) |
| COMP-13 | Core.Announcements | `AnnouncementBanner` (`XGArcade.Data.Entities`) is a true singleton — at most one row, ever (REQ-511/ADR-0065): a site-wide, admin-managed banner (maintenance notices, announcements) visible to every visitor including a fully logged-out one. `GET /announcement-banner` is one of only two unauthenticated endpoints in the whole API, alongside `GET /health`. Admin create/activate/deactivate live under the standard `"Admin"` policy, no new authorization policy introduced. Deliberately no scheduling, no per-user dismissal, no multiple-concurrent-banner support — see REQ-511's own "Out of scope" list. | `XGArcade.Core` conceptually; entity/repository in `XGArcade.Data`, endpoints in `XGArcade.Api` (`Announcements/`, `Admin/`) |
| COMP-14 | Core.AvatarSubmissions | `AvatarSubmission` (`XGArcade.Data.Entities`, REQ-722/ADR-0087) mirrors `PlayerSuggestion`'s submit/review/decide shape: `Pending`/`Approved`/`Rejected` status, `SubmittingUserId` deliberately unconstrained by an FK (same "no FK" reasoning as `PlayerSuggestion.SubmittingUserId`/`Guess.UserId`, since REQ-710 account deletion hard-deletes `User` rows and this story defines no anonymize-on-delete semantics for this entity yet), `ImageStorageKey` holding only the storage object key, never a URL or the raw bytes. `IAvatarStorage` (`XGArcade.Core.Storage`) is the narrow upload/best-effort-delete contract; its concrete implementation, `SupabaseAvatarStorage`, lives in a new project, `XGArcade.Storage`, referencing only `XGArcade.Core` — the first hosting-specific Supabase client built strictly to ADR-0004's boundary (deliberately not copying `Core.Auth.SupabaseAuthClient`'s pre-existing in-`Core` placement; see ADR-0087). `POST /users/me/avatar` (`XGArcade.Api.Avatars.AvatarEndpoints`) enforces the size/type limit REQ-722 leaves to implementation (5 MB; `image/jpeg`/`image/png`/`image/webp` only, no SVG/GIF — recorded in `implementation-document.md` §5) and replaces rather than duplicates an existing `Pending` submission, best-effort deleting the superseded image via `IAvatarStorage.DeleteAsync`. `IAvatarStorage.GetPreviewUrlAsync` (REQ-517/S-181, ADR-0087's own anticipated Follow-up — not a new structural decision) resolves a storage key into a short-lived (5 min) signed URL, generated server-side per request, never cached or persisted. `GET /admin/avatar-submissions`/`POST .../approve`/`POST .../reject` (`XGArcade.Api.Admin.AdminAvatarEndpoints`, its own file, mirroring `AdminSuggestionEndpoints`'s list/act-on-one-by-id/terminal-state-409 shape) list the `Pending` queue oldest-first with a resolved preview URL and submitter `DisplayName` (batched, no N+1), and race-safely (`IAvatarSubmissionRepository.ApproveAsync`/`RejectAsync` re-check `Status==Pending` inside the same tracked load, mirroring `PlayerSuggestionRepository.ResolveAsync`) approve or reject a submission. Approving supersedes ("a player has at most one visible avatar at a time") any prior `Approved` row for the same player by deleting it in the same `SaveChangesAsync` — `AvatarSubmissionStatus` gained no `Superseded` member, following `CreateOrReplacePendingAsync`'s existing "replace, don't invent a new status" precedent — and best-effort deletes that row's now-orphaned image. Rejecting never touches a prior `Approved` row. `IAvatarStorage.DownloadAsync` (REQ-722/S-182, built in parallel with S-181 and merged afterward) is a second, deliberately narrower mediation shape on the same interface: it streams the raw bytes + content type back through the backend rather than resolving a signed URL, for `GET /users/me/avatar` (three independent Pending/Rejected/Approved summaries for the caller, never one mutually-exclusive status) and `GET /users/me/avatar/{id}/image` (owner-only stream, 404 for a missing/not-owned/underlying-storage-missing row — never distinguished from "unknown id" to avoid leaking existence) in `XGArcade.Api.Avatars.AvatarEndpoints`. Two "resolve a stored key into something viewable" shapes now coexist on `IAvatarStorage` deliberately, not redundantly: `GetPreviewUrlAsync`'s signed URL is acceptable exposure for an admin reviewer's browser (a different trust boundary), while a general player-facing surface viewing their own image goes through the backend-streamed `DownloadAsync` instead, per ADR-0013's "backend mediates, frontend never talks to the provider directly" convention — see ADR-0087's Consequences section (S-182 follow-up paragraph) for the fuller reasoning and which shape is canonical for any future avatar-viewing surface (e.g. REQ-411 eventually showing another player's `Approved` avatar). That anticipated case is now built (REQ-722/S-184): `GET /users/{userId}/avatar/image`, a fourth `AvatarEndpoints` handler, calls the same `DownloadAsync` shape but for an arbitrary target `userId` rather than the caller's own — the caller is verified as logged-in only, never compared against `{userId}`, the deliberate opposite of `GET /users/me/avatar/{id}/image`'s ownership check. No new repository or storage method was needed; `GetApprovedAsync`/`DownloadAsync` were already generic on `submittingUserId`/storage key. Consumed by a new shared frontend component, `PlayerAvatar.tsx` (`frontend/src/components/`), rendered on `UserStatsScreen.tsx` (COMP-02/REQ-411's stats view). All four `AvatarEndpoints` handlers (`POST /users/me/avatar`, the three `GET`s above) share one caller-identity-resolution helper, originally a local `ResolveCurrentUserAsync` extracted during S-182's quality gate; as of S-209's rule-of-three cleanup (ADR-0084) that helper, along with three other near-identical copies in `LeaderboardEndpoints.cs`/`LeagueEndpoints.cs`/`FriendEndpoints.cs`, was consolidated into one shared `XGArcade.Api.Auth.RequestingUserResolver` used by all four files (plus `UserEndpoints.cs`) rather than left as separate per-file copies. | `XGArcade.Core` (`Storage/IAvatarStorage.cs`) conceptually; entity/repository in `XGArcade.Data`, concrete storage client in `XGArcade.Storage`, endpoints in `XGArcade.Api` (`Avatars/`, `Admin/`) |
| COMP-15 | Games.XGPredict | The third game on the platform, alongside Games.XGGrid (COMP-05) and Games.XGPath (COMP-11) — a match-outcome prediction game (REQ-1301-1306, `docs/requirements-document.md` §4.14). As of 2026-08-30 (ADR-0096), `XGPredictGameModule.GenerateInstanceAsync` (REQ-1301), `ScoreSubmissionAsync` (REQ-1302/1303), and `GetCellIdsAsync` are real, tested implementations against a persisted `PredictTemplate`/`PredictInstance`/`PredictMatch`/`PredictMatchPrediction` schema (`XGArcade.Data`, via `IPredictInstanceRepository`) — see ADR-0096 for the full entity-shape and submission-contract reasoning. `GetMaxAttemptsForCellAsync` still throws `NotImplementedException` (xG Predict's attempt-cap model is explicitly out of this ADR's scope — REQ-1302 rules out a bounded-guess cap the way REQ-210 imposes one on xG Grid/xG Path, and no decision has been made on what, if anything, this method should return instead). `GetCellCategoryTypesAsync` throws `NotSupportedException` (REQ-215's row/col category concept genuinely does not apply to this game, mirroring COMP-11's own precedent), and `ResolveWrongGuessPlayerAsync` returns `null` unconditionally (REQ-216 does not apply — no player-name-guess concept exists here either, same precedent). Registered in `ServiceRegistration.cs` so `IGameModuleResolver.Resolve("xg-predict")` returns a real module, same as xG Grid/xG Path. **Built 2026-08-30 (round-scheduling wiring story, S-196):** `RoundSchedulingOptions` for `"xg-predict"` is now registered too (`RoundScheduling:XGPredict:RoundDurationHours`, default 48h), alongside `IScoringStrategy` (already registered, S-193/ADR-0095) — both gaps this row used to describe as deliberately deferred are closed. `InternalRoundEndpoints`'s `gameKey` switch now routes `"xg-predict"` (a third arm, mirroring the xG Grid/xG Path arms, resolving a `PredictTemplate` via the new `PredictTemplateResolver`), and `LeaderboardEndpoints.ValidateGameKey`'s allow-list now includes `"xg-predict"` too — round generation for this `GameKey` is reachable in production via `POST /internal/generate-round?gameKey=xg-predict`, scheduled by the new `.github/workflows/generate-predict-round.yml` (daily cron, its own independent file per ADR-0072's 2026-08-30 amendment, not a loop extension of a shared workflow). **Built 2026-08-31 (ADR-0098, S-197):** `GuessSubmissionService`/`GuessEndpoints` is still **not**, and will never be, the write path for this game (ADR-0096) — instead, this component now has its own real HTTP surface, `XGArcade.Api.Predict.PredictEndpoints` (`GET /predict/current`, `POST /predict/matches/{matchId}/predictions`, `POST /predict/confirm`), which calls `IGameModuleResolver.Resolve("xg-predict").ScoreSubmissionAsync` directly. REQ-1302 (submission)/REQ-1303 (round-wide lock) are unchanged at the `ScoreSubmissionAsync` level — the new endpoint only adds a real caller and maps its exceptions (`PredictInvalidSubmissionException`→400, `PredictRoundLockedException`→409, `PredictScoringException`→404). REQ-1306 (per-player confirm-and-lock), previously entirely unbuilt, is now implemented: a new `PredictPlayerLock` entity (`XGArcade.Data`, composite-keyed on `(PredictInstanceId, UserId)`, migration `20260831090000_AddPredictPlayerLock`) backs two new `IPredictInstanceRepository` methods, `IsPlayerLockedAsync`/`LockPlayerPredictionsAsync`. Per ADR-0098, the lock check lives in `PredictEndpoints` (checked before `ScoreSubmissionAsync` is ever called), not inside `XGPredictGameModule` — that module's own `ScoreSubmissionAsync`/test suite are unmodified by this story. `PredictInstance` also gained a `[NotMapped]` computed `LockInstant` property (`Matches.Min(m => m.KickoffUtc)`, no schema/migration impact), extracted after this exact formula was independently re-derived at three call sites (`ScoreSubmissionAsync` and the two new `PredictEndpoints` reads) — a quality-gate fix, not a new decision. **Risk flagged (ADR-0098, unresolved):** `GuessEndpoints`' `POST /rounds/{roundId}/cells/{cellId}/guesses` still has no `GameKey` allow-list; it is safe today only because `XGPredictGameModule.GetMaxAttemptsForCellAsync` still throws `NotImplementedException`, so `GuessSubmissionService` never reaches `ScoreSubmissionAsync` through that path. Whoever implements `GetMaxAttemptsForCellAsync` for this game must add an explicit `GameKey` guard there (or move REQ-1306's lock check somewhere both paths pass through) — tracked as a `docs/backlog.md` follow-up. On the frontend, xG Predict is now the third game wired into `GameSelectScreen`/`HeaderNav` (a third tile/nav entry, `frontend/src/predict/PredictScreen.tsx` + `PredictMatchInput.tsx`/`PredictConfirmDialog.tsx`, SCREEN-14) — it does not wire in `RoundCompletionBanner`/REQ-1210, confirmed inapplicable to this game per §4.14's own note. **Built 2026-08-31 (ADR-0100, S-199):** the `ILeaderboardService`/leaderboard wiring gap this row used to flag is closed — `PredictRoundScoreSource` (this component, `Core.Scoring.IRoundScoreSource`'s `"xg-predict"` implementation) wraps `IPredictInstanceRepository` only, registered once against `XGPredictGameModule.XGPredictGameKey`; never `IRoundRepository`/`IUserRepository` (every `Round`/`User` it needs is handed in by `LeaderboardService`, which resolves it via the new `IRoundScoreSourceResolver`). `IPredictInstanceRepository` also gained `GetParticipantUserIdsByInstanceIdAsync` (participation, not points — distinguishes "predicted, ungraded" from "never predicted" for REQ-409's qualifying-round test). See COMP-02/COMP-04's own rows for the resolver shape. **Built 2026-08-31 (ADR-0101, S-201):** the REQ-710 account-deletion gap this row used to flag (`PredictPlayerLock`/`PredictMatchPrediction` untouched by `AccountDeletionService`) is closed — `XGPredictGameModule` implements the new `IGameModule.PurgeUserDataAsync(userId)` (see COMP-01's row) by anonymizing `PredictMatchPrediction.UserId` (nullable, same reasoning as `Guess`) and hard-deleting `PredictPlayerLock` rows (its `UserId` is non-nullable, half the composite primary key, so anonymize-in-place isn't possible) via its own already-injected `IPredictInstanceRepository` — `AccountDeletionService`/Core.Auth never references that repository directly (ADR-0101). Uses football-data.org's fixtures/results endpoint as its data source (ADR-0099, superseding ADR-0094's original API-Football choice) — the first live match-schedule/result data this codebase has ever needed, distinct from every other game's Wikidata career/bio data — and is the platform's first named exception to ADR-0021's golf-style scoring convention (ADR-0095: conventional higher-is-better scoring, confirmed by the product owner). **Built 2026-08-30:** `XGPredictScoringStrategy` (`Core.Scoring`, COMP-04) implements this — `LowerIsBetter = false`, registered against `"xg-predict"` in `ServiceRegistration.cs` — and its `ScorePrediction` method implements REQ-1304's three-component formula, unit-tested directly. `LeaderboardService`'s three plain-total ranking scopes (COMP-02) also now resolve `"xg-predict"` descending as a result; its separate median-based all-time ranking (REQ-409/410) now does too, closed same-day as a direct follow-up — see COMP-02's row and REQ-1304's status note. **Built 2026-08-30 (ADR-0097, S-195):** `ScorePrediction` now has a real production caller — `IPredictGradingService`/`PredictGradingService` (this component's own new service) fetches every ready match's real result via COMP-07's `IFootballDataClient.GetFixtureResultAsync`, grades every stored prediction through `XGPredictScoringStrategy` (the concrete class, not `IScoringStrategy`, per ADR-0097's own reasoning), and persists via two new `IPredictInstanceRepository` methods (`GradeMatchAsync`/`VoidMatchAsync`), gated on a new `PredictMatch.GradingStatus` (`PredictMatchGradingStatus`: `Pending`/`Graded`/`Voided`) discriminator that is the sole idempotency mechanism — a `Graded`/`Voided` match is never re-fetched. `PredictMatchPrediction.FinalPoints` (nullable int, same shape as `Guess.FinalPoints`) holds each graded prediction's points; a prediction belonging to a `Voided` match keeps it `null` permanently, by design. A new `IPredictInstanceRepository.GetTotalPointsByInstanceIdAsync` sums `FinalPoints` per user over `Graded` matches only, giving a round a partial, always-growing total with no placeholder for an ungraded match — now called by `ILeaderboardService`/`LeaderboardEndpoints` too, via `PredictRoundScoreSource` (ADR-0100/S-199, see this row's own later note). The trigger is a new bearer-token-gated endpoint, `POST /internal/grade-predict-matches` (`XGArcade.Api.Predict.InternalPredictGradingEndpoints`, registered unconditionally like `/internal/generate-round`), polled hourly plus `workflow_dispatch` by a new `.github/workflows/grade-predict-matches.yml` — a deliberately separate workflow from `generate-grid-round.yml`/`generate-path-round.yml`/`generate-predict-round.yml`, since grading is not round generation (ADR-0072's boundary, extended). REQ-302's `Closed` status and `RoundCloseService` remain completely untouched by grading completeness — a round can close with matches still `Pending`, by design (ADR-0097 Decision §4). Note: as of ADR-0098 (S-197), REQ-1302 prediction submission is wired end to end, so once a round is generated (S-196) and matches are predicted through it, this grading path now has real predictions to grade — the round-generation/submission/grading legs are no longer three independently-gapped pieces, though production data still depends on a live football-data.org key per `MVP-SCOPE.md`. The round/match/prediction entity shape (a fixed set of 5 real-world matches with a whole-round lock at the first kickoff, not a dynamically-matched grid cell or a single fixed target player) is now decided by ADR-0096 (amended same-day for the exception-hierarchy/field-naming detail): `PredictScoringException` derives from `Core.Games.GameEntityNotFoundException` (the "not found" case, matching `PathScoringException`/`GuessScoringException` precedent); a separate `PredictInvalidSubmissionException`/`PredictRoundLockedException` cover invalid-goal-count and post-lock rejection respectively. Depends on `XGArcade.Data` (entities/repository) and `XGArcade.DataSync` (football-data.org client) for real now, not deferred. | `XGArcade.Games.XGPredict` |
| COMP-16 | Core.Social | **Decided (ADR-0103, 2026-09-02).** **Data model scaffolding built (S-208, 2026-09-02):** `FriendRequest`/`Friendship`/`Challenge`/`MatchmakingOptIn` entities, `IFriendRepository`/`IChallengeRepository`/`IMatchmakingOptInRepository` + implementations, and a migration exist in `XGArcade.Data`, registered in `ServiceRegistration.cs`. **Friend request send/accept/decline built (S-209, 2026-09-02):** `IFriendService`/`FriendService` (`XGArcade.Core.Social`) implements REQ-1401's send/accept/decline logic (self-request, recipient-not-found, already-friends, and both-directions duplicate-pending rejection; accept creates the symmetric `Friendship` row in the same call, decline does not and does not block a resend), depending on `IUserRepository` (COMP-01) the same way `LeaderboardService` (COMP-02) already does for its own read/validation needs — a Core-to-Core dependency, not a boundary violation of ADR-0003 (which governs game modules, not two Core components). `XGArcade.Api.Social.FriendEndpoints` exposes it as `POST /friends/requests`, `POST /friends/requests/{id}/accept`, `POST /friends/requests/{id}/decline`, `GET /friends/requests/pending`, and `GET /friends`. **Direct challenge send/accept/decline + matchmaking opt-in built (S-210, 2026-09-02):** `IChallengeService`/`ChallengeService` implements REQ-1402's send/accept/decline (friendship precondition via `IFriendRepository.AreFriendsAsync`, both-directions duplicate-pending rejection); `IMatchmakingService`/`MatchmakingService` implements REQ-1403's opt-in creation only (`OptInAsync`, always succeeds — opting in is itself the consent). Neither service ever writes a `ConnectMatch` row itself, per ADR-0103 — `ChallengeService.AcceptChallengeAsync` takes a caller-supplied `resultingMatchId` and persists Accepted+that id in the same call, and the pairing sweep (`MatchmakingSweepService`) lives entirely in `XGArcade.Api.Social`, not here, because it needs `IConnectMatchRepository` (COMP-17). `XGArcade.Api.Social.ChallengeEndpoints` exposes `POST /challenges`, `POST /challenges/{id}/accept` (pre-generates the match id, then creates the `ConnectMatch` row itself via `IConnectMatchRepository` once `ChallengeService` confirms `Resolved`), `POST /challenges/{id}/decline`, and `GET /challenges/pending`; `MatchmakingEndpoints` exposes `POST /matchmaking/opt-in`; `InternalMatchmakingSweepEndpoints` exposes the bearer-token-gated `POST /internal/sweep-matchmaking-pairings` (hourly cron, `sweep-matchmaking-pairings.yml`), whose `MatchmakingSweepService.RunSweepAsync` expires past-window `Waiting` rows first, then greedily pairs the remainder oldest-first while tracking every `UserId` already paired that run so a user's own second `Waiting` row (or any other row) can never make them a participant in more than one resulting `ConnectMatch` from a single sweep. An arcade-level component, alongside `Core.Users`/`Core.Leagues`, not behind `IGameModule` — genuinely separate from COMP-17, not folded into it, since friends are conceptually reusable by any future game, not xG-Connect-specific. A resolved challenge/pairing creates a `ConnectMatch` (COMP-17) directly — never via `Core.Rounds`/`RoundGenerationService` (ADR-0103's second decision: xG Connect's pairwise, on-demand match doesn't fit the `Round`/`League` model). **Notification indicator built (S-216, 2026-09-03):** REQ-1411's cross-cutting `GET /notifications/summary` (new `XGArcade.Api.Notifications.NotificationEndpoints`) reads from both COMP-16 and COMP-17 through their normal read paths rather than being owned by either — `IFriendService.GetPendingFriendRequestsAsync`, `IChallengeService.GetPendingChallengesAsync` (both COMP-16), and a new `IConnectMatchLifecycleService.GetMatchesAwaitingActionAsync` (COMP-17, see its own entry below). Per ADR-0103's "belongs to neither" resolution, no third component was created; the aggregation itself lives in `XGArcade.Api`. | `XGArcade.Core` (`Social/`), `XGArcade.Api` (`Social/` — `ChallengeEndpoints`, `MatchmakingEndpoints`, `InternalMatchmakingSweepEndpoints`, `MatchmakingSweepService`), entities/repositories in `XGArcade.Data` |
| COMP-17 | Games.XGConnect | **Decided (ADR-0103, 2026-09-02).** **Data model scaffolding built (S-208, 2026-09-02):** `ConnectMatch`/`ConnectTargetPick`/`ConnectChainStep`/`ConnectChatMessage` entities, `IConnectMatchRepository`/`IConnectChatMessageRepository` + implementations, and a migration exist in `XGArcade.Data`, registered in `ServiceRegistration.cs` — schema/repository CRUD only. As of S-210, `IConnectMatchRepository.AddMatchAsync` has its first two real callers — `XGArcade.Api.Social.ChallengeEndpoints`' accept handler (REQ-1402) and `XGArcade.Api.Social.MatchmakingSweepService` (REQ-1403) — both in `XGArcade.Api`, not `Core.Social` (ADR-0103); every `ConnectMatch` row either creates is left at its default `AwaitingTargetPicks` status with no target picks, chain steps, or resolution, since that logic doesn't exist yet. **`XGArcade.Games.XGConnect` project scaffolded (S-211 scaffold step, 2026-09-02):** `XGConnectGameModule` is now a real, registered `IGameModule` implementation (`GameKey = "xg-connect"`, `IGameModuleResolver.Resolve("xg-connect")` returns it) — but a fourth game module behind `IGameModule` (ADR-0003) owning only what this scaffold step wires up: `PurgeUserDataAsync` (REQ-710, ADR-0101's per-module purge hook), implemented for real via a new `IConnectMatchRepository.AnonymizeUserDataAsync` method that anonymizes `ConnectMatch.PlayerAUserId`/`PlayerBUserId`, `ConnectTargetPick.UserId`, and `ConnectChainStep.UserId` in place (not `ConnectChatMessage.SenderUserId` — REQ-1410 chat isn't built yet, flagged as a gap for whichever story builds it). Every round-generation-shaped `IGameModule` method — `GenerateInstanceAsync`, `ScoreSubmissionAsync`, `GetCellIdsAsync`, `GetMaxAttemptsForCellAsync`, `GetCellCategoryTypesAsync` — throws `NotSupportedException`, following COMP-11/COMP-15's existing "permanently inapplicable, not a TODO" precedent (ADR-0103's own "narrower reading of `IGameModule`" paragraph); `ResolveWrongGuessPlayerAsync` returns `null` unconditionally, same precedent. **Target-pick selection built (S-211, 2026-09-02):** `IConnectTargetPickService`/`ConnectTargetPickService` implements REQ-1404 in full — independent, mutually-invisible per-player selection; free resubmission via `IConnectMatchRepository.AddOrUpdateTargetPickAsync` for as long as the caller's own `ConnectTargetPick.IsLocked` is false; and, once the second (completing) selection arrives, a check-before-persist ordering (the trivial-pair overlap check runs before anything is written or locked, so a rejected completing pick never touches either player's row). `ConnectTargetPick.IsLocked` is this story's own self-contained "puzzle fixed" signal — `ConnectMatch.Status`/`StartedAt`/`DeadlineUtc` are untouched by this story, reserved for S-212/REQ-1405. Layered as an independent service on top of `IConnectMatchRepository`, not built directly into `XGConnectGameModule`, mirroring `GridGameModule`/`XGPathGameModule`/`XGPredictGameModule`'s own thin-adapter-composing-independent-services shape. The direct-connection check itself is delegated to a new, deliberately generic (player-ID-pair, not `ConnectTargetPick`-shaped) service, `IPlayerCareerOverlapService`/`PlayerCareerOverlapService` — same component (`XGArcade.Games.XGConnect`), same "a game-module-owned business-rule service may depend directly on a shared `XGArcade.DataSync` service" shape `Games.XGPath`'s `PathEligibilityService` already established with `IPlayerFamiliarityService`/`IPlayerCareerStintRefreshService`. It trusts cached `PlayerCareerStint` rows once at least one exists per player, and otherwise triggers a live Wikidata refresh through the shared `IPlayerCareerStintRefreshService` (`XGArcade.DataSync`, ADR-0054 — which gained a `throwOnFailure` opt-in in this same story specifically so `PlayerCareerOverlapService` could reuse its fetch/persist/club-canonicalization logic rather than fork it), following ADR-0010/0011's fetch-once-cache-forever discipline; a technical Wikidata failure surfaces as `LiveLookupUnavailableException`, never silently treated as connected or not connected. Reviewed explicitly and judged not to need its own ADR — a straightforward application of the `Games.XGPath`/`DataSync` precedent, not a new structural decision. Built generic on two bare player IDs precisely so S-213's chain-step validation (REQ-1406) can reuse it unchanged. Exposed as `POST /matches/{matchId}/target-pick` (`XGArcade.Api.Connect.ConnectMatchEndpoints`). **Match-start transition and forfeit-timeout sweep built (S-212, 2026-09-03):** `IConnectMatchLifecycleService`/`ConnectMatchLifecycleService` (same component) implements REQ-1405. `StartMatchIfBothPicksLockedAsync` is called from `ConnectTargetPickService.SubmitTargetPickAsync`'s completing-pick branch, immediately after `LockTargetPicksForMatchAsync`; it independently re-confirms via `IConnectMatchRepository.GetTargetPicksForMatchAsync` that both `ConnectTargetPick` rows are locked before transitioning `ConnectMatch.Status` to `Active` with `StartedAt = now` and `DeadlineUtc = StartedAt + 6h` — so the "both target picks locked" detection is never trusted blindly from the caller. `RunForfeitSweepAsync`, triggered by the new bearer-token-gated `POST /internal/sweep-connect-forfeits` (`XGArcade.Api.Connect.InternalConnectForfeitSweepEndpoints`, called hourly by `sweep-connect-forfeits.yml`, same shape as COMP-16's `InternalMatchmakingSweepEndpoints`/`sweep-matchmaking-pairings.yml`), finds `Active` matches past `DeadlineUtc` (`IConnectMatchRepository.GetActiveMatchesPastDeadlineAsync`) and marks each not-yet-terminal player slot as timed out independently and idempotently via two new nullable columns, `ConnectMatch.PlayerATimedOutAt`/`PlayerBTimedOutAt` — slot-based rather than `UserId`-keyed, since `PlayerAUserId`/`PlayerBUserId` go null after REQ-710 anonymization. If both slots are terminal after that same sweep pass, the match resolves immediately to `ConnectMatchOutcome.Draw` in the same call, per REQ-1409's "both forfeit -> draw" rule. This only resolves the both-timed-out case — REQ-1409's mixed-outcome resolution (one player times out while the other legitimately busts or completes their chain) needs REQ-1406-1408's chain-step logic first and remains out of scope here. Reviewed explicitly and judged not to need its own ADR: both the slot-based tracking and the both-timeout-resolves-to-Draw behavior are direct, requirement-mandated implementations of already-accepted REQ-1405/REQ-1409 text, not new structural decisions. **Chain-step submission and live per-step validation built (S-213, 2026-09-03):** `IConnectChainStepService`/`ConnectChainStepService` (same component) implements REQ-1406. `IPlayerCareerOverlapService` gained a second method, `HaveOverlapAtClubAsync(playerAId, playerBId, clubName)`, sharing its fetch-once/live-refresh plumbing with the existing `HaveSharedClubOverlapAsync` via a new private `LoadBothPlayersStintsAsync` helper (no change to that existing method's own behavior). `ConnectChainStepService` resolves each submitted candidate name against `IPlayerRepository.GetPlayersByNormalizedFullNameAsync` (COMP-06) — deliberately never `PlayerNameIndex`/COMP-10, mirroring `GridNameMatcher`'s own autocomplete/correctness separation (ADR-0007) — then runs the claimed-club check via `HaveOverlapAtClubAsync` against the immediately preceding chain player (the caller's own fixed target pick, for the first step; the most recently accepted valid step's candidate thereafter). Only once that check passes does it run the chain-closing check, reusing the existing, unmodified `HaveSharedClubOverlapAsync` against the OTHER participant's target pick (never the one the chain started from) — a `LiveLookupUnavailableException` from either check discards the whole step, including an already-passed main check, so nothing is ever partially persisted. `ConnectChainStep` gained a `ClosesChain` column (migration `20260903130000_AddConnectChainStepClosesChain`), true only once alongside `IsValid`. Exposed as `POST /matches/{matchId}/chain-steps` (`XGArcade.Api.Connect.ConnectChainStepEndpoints`), mirroring `GuessEndpoints`'s "a wrong answer is a normal 200 body, not an error" shape — a step failing live validation is `200 OK` with `IsValid: false`; only match-not-found/not-a-participant/not-active/chain-already-complete (404/403/409/409) and a genuine live-lookup failure (503) are non-200. Candidate search needed no new endpoint — the existing `/players/autocomplete` (REQ-207, COMP-10) already satisfies REQ-1406's "not restricted to the curated reference tables" clause. This story does not enforce any cap on invalid attempts per position — REQ-1407/S-214's job. Reviewed explicitly and judged not to need its own ADR, same reasoning as S-211/S-212's own entries above. **Two-strikes penalty/bust rule, scoring, and win/draw/forfeit resolution built (S-214, 2026-09-03):** `ConnectChainStepService.SubmitChainStepAsync` (same component) enforces REQ-1407 inline with its existing per-step validation — a second, consecutive failure at the same chain position marks that player's slot busted via a new, idempotent `IConnectMatchRepository.MarkPlayerBustedAsync` (mirroring `MarkPlayerTimedOutAsync`'s own `??=` semantics, new nullable `ConnectMatch.PlayerABustedAt`/`PlayerBBustedAt` columns) and returns a new `SubmitChainStepOutcome.Busted`, distinct from an ordinary `InvalidStep`; a new `AlreadyForfeited` precondition (409) closes a real pre-existing gap — a player whose own slot already busted or timed out could previously keep submitting steps for as long as `ConnectMatch.Status` stayed `Active` (true whenever the opponent hadn't yet reached terminal, since `Status` only flips to `Resolved` once BOTH players are terminal). New `IConnectScoringService`/`ConnectScoringService` (same component, pure/stateless) implements REQ-1408: `score = Math.Max(1, validStepCount + firstAttemptFailureCount)`, mirroring `Core.Scoring`'s `IScoringStrategy` shape without xG Connect depending on `Core.Scoring` itself (ADR-0103). `ConnectMatchLifecycleService` gained `TryResolveMatchIfBothTerminalAsync`, implementing REQ-1409: a shared private `ResolveIfBothTerminalAsync` helper (used by both this new method and `RunForfeitSweepAsync`) is the single place all three terminal-reaching paths (timeout/REQ-1405, bust/REQ-1407, chain completion via a `ClosesChain` step/REQ-1408, detected through a new shared `ConnectChainStepExtensions.HasClosedChain()` extension) converge into a resolution decision — both-completed compares `IConnectScoringService.CalculateScore` (lower wins, equal draws), one-completed-one-forfeited is an outright win for the completer with no minimum score, both-forfeited is always a draw; called from `ConnectChainStepService` right after a bust or a chain-close. `RunForfeitSweepAsync`'s own sweep logic was corrected in the same story: it previously marked BOTH slots timed-out unconditionally once the shared 6h deadline passed, which was wrong once bust/completion existed as terminal paths (a player who already busted or already completed before the deadline must not also be marked timed-out) — it now checks each slot's already-terminal state before marking a timeout, then delegates to the same shared resolution helper, which is what makes the mixed-outcome case (one player times out while the other already busted/completed) resolve correctly. `ConnectMatch.PlayerAScore`/`PlayerBScore` are persisted in the same `ResolveMatchAsync` write as `Outcome`/`ResolvedAt`, null for a forfeiting player. New EF Core migration `20260903140000_AddConnectMatchBustAndScoreTracking` adds the four new columns. Reviewed explicitly and judged not to need its own ADR — same "straightforward, requirement-mandated implementation of already-accepted REQ text" reasoning S-211/S-212/S-213's own entries above already used for this component; the one duplication finding from this review (a redundant chain-completion check re-derived at multiple call sites) was fixed by extracting `ConnectChainStepExtensions.HasClosedChain()`, not by an ADR. Full `REQ1407_...`/`REQ1408_...`/`REQ1409_...`-named test coverage across `ConnectChainStepServiceTests.cs`, `ConnectChainStepEndpointTests.cs`, `ConnectMatchLifecycleServiceTests.cs`, `ConnectMatchRepositoryTests.cs`, and a new `ConnectScoringServiceTests.cs`. **In-match text chat built (S-215, 2026-09-03):** new `IConnectChatService`/`ConnectChatService` (same component) implements REQ-1410 on top of the existing S-208 `IConnectChatMessageRepository` (send/read persistence) and `IConnectMatchRepository` (participant check only, via `GetMatchByIdAsync`) — `MatchNotFound`/`NotAParticipant` outcomes, same shape/ordering as `ConnectChainStepService`. Deliberately does not gate on `ConnectMatch.Status`, unlike `ConnectChainStepService`/`ConnectTargetPickService` — none of REQ-1410's three Given/When/Then blocks make match status a precondition for sending or reading, and one explicitly requires chat to stay readable once a match has resolved for both players. Exposed as `POST`/`GET /matches/{matchId}/chat-messages` (`XGArcade.Api.Connect.ConnectChatEndpoints`). This story also closed a gap `IConnectMatchRepository.AnonymizeUserDataAsync`'s own doc comment had flagged since S-208: new `IConnectChatMessageRepository.AnonymizeSenderAsync` (load-then-save, mirrors `ConnectMatchRepository.AnonymizeUserDataAsync`'s per-entity-type shape) is now injected into `XGConnectGameModule` and called from `PurgeUserDataAsync` alongside the existing `IConnectMatchRepository.AnonymizeUserDataAsync` call, so `ConnectChatMessage.SenderUserId` is anonymized on account deletion (REQ-710) too. No new ADR — same reasoning S-211 through S-214's own entries above already used for this component. Full `REQ1410_...`-named test coverage across `ConnectChatServiceTests.cs` and `ConnectChatEndpointTests.cs`, plus `REQ710_...`-named coverage of the new `AnonymizeSenderAsync` method in an extended `ConnectChatMessageRepositoryTests.cs` and `XGConnectGameModuleTests.cs`. Two same-story quality-gate follow-ups (2026-09-03): the "load match, then confirm caller is PlayerA/PlayerB" shape had grown to four call sites across `ConnectTargetPickService`, `ConnectChainStepService`, and both `ConnectChatService` methods, so it was extracted into `ConnectMatchAccessExtensions.ResolveParticipantMatchAsync` (new file, same rule-of-three-driven extraction pattern as `ConnectChainStepExtensions.HasClosedChain()` above; no outcome enum, result record, or public signature changed on any of the three services); separately, `ConnectChatEndpoints` now rejects a null/empty/whitespace-only `MessageText` and anything over `MaxMessageLength = 1000` trimmed characters with a `400` Problem response and trims the message before it reaches `ConnectChatService`, matching the blank/max-length validation convention already applied to every other free-text endpoint (`GuessEndpoints`, `AdminAnnouncementBannerEndpoints`, `LeagueEndpoints`) — not mandated by REQ-1410's own Given/When/Then text, but not a new structural decision either. No new ADR for either follow-up, extended `REQ1410_...`-named coverage in `ConnectChatEndpointTests.cs` for the validation cases. Only the frontend match/gameplay screen (S-218) remains **not yet implemented** for this component. Not wired into `GuessSubmissionAllowedGameKeys`, `RoundSchedulingOptions`, or any `IScoringStrategy` registration — deliberately, since xG Connect never uses `Core.Rounds`/`Core.Scoring`'s `Guess`-based submission path (ADR-0103). `ConnectMatch` persists scoped to exactly two participating `UserId`s with a native win/draw/forfeit outcome (REQ-1409) — never a `Round`/`GameKey`+`GameInstanceId` pair, and never scored via `Core.Scoring`'s `FinalPoints`/`IScoringStrategy`. Whether `ConnectMatch` results ever feed a `Core.Leagues` leaderboard remains an open product decision, explicitly out of ADR-0103's scope. **REQ-1411's match-side notification source built (S-216, 2026-09-03):** `IConnectMatchLifecycleService` gained `GetMatchesAwaitingActionAsync(userId)`, layered on a new `IConnectMatchRepository.GetOpenMatchesForUserAsync(userId)` (every match the user participates in, either slot, with `Status != Resolved`) — the service then filters that candidate set down to matches where the caller's OWN slot has not reached a terminal state, reusing the same per-slot bust/timeout check plus `ConnectChainStepExtensions.HasClosedChain()` that `RunForfeitSweepAsync`/`TryResolveMatchIfBothTerminalAsync` already use, evaluated one-sided (only the caller's slot, not both) since this read only cares whether the match is still awaiting the caller's own next move. Consumed by `XGArcade.Api.Notifications.NotificationEndpoints` (see COMP-16's row above). **S-218's read-side gap closed (2026-09-03):** every xG Connect endpoint before this story (`ConnectMatchEndpoints`/`ConnectChainStepEndpoints`/`ConnectChatEndpoints`) was write-only, leaving no way to read a match's current state or discover which `matchId`s belong to the caller — new `IConnectMatchQueryService`/`ConnectMatchQueryService` (same component) closes that gap, reusing rather than re-deriving `ConnectMatchAccessExtensions.ResolveParticipantMatchAsync`, `IConnectMatchLifecycleService.GetMatchesAwaitingActionAsync`, and `ConnectChainStepExtensions.HasClosedChain()`; exposed as `GET /matches`/`GET /matches/{matchId}` (`XGArcade.Api.Connect.ConnectMatchQueryEndpoints`), unblocking the S-218 frontend gameplay screen. **Chain-step club design changed (2026-09-04, ADR-0104):** `HaveOverlapAtClubAsync` (above) is removed — replaced by `IPlayerCareerOverlapService.GetSharedClubOverlapsAsync(playerAId, playerBId)`, returning every shared, overlapping-time club (not one the caller must already know and pass in); `HaveSharedClubOverlapAsync` is now a thin wrapper over it rather than a separate implementation. `ConnectChainStepService` no longer takes a `claimedClubName` — only a candidate name — and picks one representative overlap deterministically (latest `OverlapStartYear`) when a pair shares more than one club. `ConnectChainStep.ClaimedClubName` (required) is replaced by nullable `MatchedClubName`/`MatchedOverlapStartYear`/`MatchedOverlapEndYear` (migration `20260904090000_ReplaceConnectChainStepClaimedClubWithMatchedOverlap`). No boundary/component change — same component, same data-flow shape, a corrected internal contract. **Bug fix, same day (ADR-0105):** `PlayerCareerOverlapService.LoadBothPlayersStintsAsync`'s "trust cached rows once at least one exists per player" behavior (described in this row's own S-211 entry above) was a real bug — `PlayerCareerStint` is a shared table other features can write narrow, single-club byproduct rows into, so "has any row" was never the same as "has a full career fetched"; a live report (Reece James's real Wigan Athletic loan, hidden because he already had a Chelsea-only row from an earlier chain step) confirmed it. Fixed by following the same precedent ADR-0054 already established for the identical bug in xG Path: always refresh both players unconditionally on every call, never gated on existing rows — matching `XGPathGameModule.GenerateInstanceAsync`'s own unconditional refresh. No boundary/component change; the `HasStints` gate and its private helper are removed. **Bug fix (2026-09-05, ADR-0107):** `ConnectChainStepService`/`ConnectTargetPickService`'s candidate-name resolution — both services' own comments already flagged their "deterministically pick the lowest `Id` on a same-name collision" behavior as "a known, deliberate simplification, not a new REQ" — turned out to be a real, twice-reported bug: two different real footballers both named "Jonas Olsson" (different `WikidataQid`s, both plausibly indexed via this codebase's own routine Wikidata sweeps) meant name-only resolution had no way to pick the right one. Fixed by a new shared `ConnectCandidateResolver` (this component): when the client supplies a `WikidataQid` (now carried on `/players/autocomplete` suggestions, COMP-10's own row), it resolves the exact real person via `IPlayerRepository.GetOrCreatePlayersByWikidataQidAsync` (COMP-06) — get-or-create, so a player indexed but never before referenced by any game module still resolves cleanly; the old name-only, lowest-`Id` fallback is kept only for a suggestion that predates the `WikidataQid` backfill. `ChainBuilder.tsx`'s candidate field now requires a real suggestion click before submitting (previously it allowed typed-and-unselected text), matching `TargetPickPanel.tsx`'s pre-existing requirement. See ADR-0107. | `XGArcade.Games.XGConnect`, entities/repositories in `XGArcade.Data` |

**"Maps to" column note (ADR-0014):** for COMP-01, COMP-03, COMP-04, and
COMP-05 specifically, this column names where each component's
*business/orchestration logic* lives — it does not mean every entity or
repository that component owns is physically defined in that project.
`User` (COMP-01), `Round` (COMP-03), `Guess` (COMP-04), and
`GridTemplate`/`GridInstance`/`GridCell` (COMP-05) are EF Core entities
defined in `XGArcade.Data` alongside their repositories, in the single
shared `XGArcadeDbContext`, same as every other component's persistence
code — see ADR-0014 for why. The component boundary itself (e.g. boundary
rule 1) is enforced by which repository interfaces a component is allowed
to call, not by which `.csproj` the entity class sits in.

### 5.1 Boundary rules

**Boundary rule 1 (data access):** COMP-05 (and any future game module) may
only reach player data through COMP-06's public interface. It must never
query `PlayerData`/`PlayerOverride` directly — this keeps the
override-precedence rule (REQ-501) enforced in exactly one place (ADR-0015).
If a new game module needs a different kind of data store, that's a signal
for an ADR, not a workaround.

**Boundary rule 2 (Round genericity):** `Core.Rounds` (COMP-03) must never
hold a foreign key to a game-specific entity such as `GridInstance`. A
`Round` references a game instance only via an opaque pair — `GameKey`
(e.g. `"xg-grid"`) and `GameInstanceId` (a `Guid` with no type Core
understands). Resolving that ID into an actual `GridInstance` is the
responsibility of the owning game module (COMP-05), reached through
`IGameModule` — see ADR-0003. **Narrow, documented exception (ADR-0016):**
`GET /rounds/current` reads `GridInstance`/`GridCell` directly via
`IGridInstanceRepository`, bypassing `IGameModule`, for display purposes
only — never for generation or scoring, which must still always go through
`IGameModule`.

**Boundary rule 3 (email separation):** Auth-lifecycle emails (signup
confirmation, password reset) are never sent by `XGArcade.Core` code — they
are Supabase Auth's responsibility, configured with custom SMTP.
Conversely, product notification emails (round results) are never routed
through Supabase Auth or an auth hook — they are sent directly by
Core.Notifications (COMP-08) via Resend's API. See ADR-0005.

**Boundary rule 4 (test-data isolation):** Testing.SeedManager (COMP-09)
must create and reset data only by calling other components' normal public
interfaces — never by writing directly to tables through a separate path.
See ADR-0006.

**Boundary rule 5 (autocomplete/correctness separation):** Autocomplete
(typeahead suggestions shown before submission) queries only
`Data.PlayerNameIndex` (COMP-10) — never COMP-06, at all, for any reason.
Correctness-checking a submitted guess (REQ-203) queries only
`Data.PlayerStore` (COMP-06, which includes `PlayerAlias`), never COMP-10.
These two paths must never be merged — doing so would leak answer validity
through autocomplete. See ADR-0007.

This is a stricter rule than "name matching only ever touches one of the
two" — REQ-208's post-submission candidate-resolution step deliberately
reads *both* `PlayerNameIndex` (COMP-10, the candidate pool) and
`PlayerAlias` (COMP-06, alongside `PlayerAttribute`) together to resolve a
submitted name to a candidate player. That's the documented design, not a
violation: `PlayerAlias` is never read for autocomplete, and
`PlayerNameIndex` is never used to *determine* correctness (candidates it
returns still have to satisfy the cell's categories via COMP-06 before a
guess is accepted). The boundary this rule protects is "nothing
autocomplete shows implies correctness" — not "COMP-06 and COMP-10 may
never be read in the same request."

### 5.2 Cross-component method inventory (`IGameModule`, resolvers)

- `IGameModule` (COMP-03/04 call into COMP-05/COMP-11/COMP-15 via
  `IGameModuleResolver`, keyed by `Round.GameKey`, ADR-0003): `GenerateInstanceAsync`
  (ADR-0102: returns `Task<GameInstance?>` — `null` means "no new round due
  for this `GameKey` right now," read via the new `RoundConfig.
  LatestGameInstanceId`/`GameInstance.SuggestedStartTime`/`SuggestedEndTime`
  extension points; xg-grid/xg-path never use these, xg-predict does),
  `ScoreSubmissionAsync`, `GetCellIdsAsync`, `GetMaxAttemptsForCellAsync`
  (ADR-0041), `GetCellCategoryTypesAsync` (REQ-215), `ResolveWrongGuessPlayerAsync`
  (REQ-216/ADR-0057).
- `IScoringStrategyResolver` (COMP-04, ADR-0040) and
  `IRoundSchedulingOptionsResolver` (COMP-03, ADR-0051) both mirror
  `IGameModuleResolver`'s per-`GameKey` resolution shape.

### 5.3 Component evolution reference

Full narrative history (dated, story-by-story) previously lived inline in
each component's row above; it has moved to the ADRs themselves, which
already contain the complete reasoning for every change below. This is
purely a lookup table — read the cited ADR(s)/REQ(s) for the actual
rationale, don't expect this list to explain anything on its own.

| Component | ADRs / REQs that shaped its current shape |
|---|---|
| COMP-01 Core.Users | ADR-0019 (display-name uniqueness), ADR-0026 (service-role deletion), ADR-0036 (guest play), ADR-0038 (guest cleanup), REQ-505/506/507/508 (admin account management), ADR-0101 (S-201: per-game account-deletion data purge via `IGameModule.PurgeUserDataAsync`, not a direct game-repository dependency) |
| COMP-02 Core.Leagues | ADR-0031 (live leaderboard coupling), REQ-405 (windowed scope), REQ-409 (median all-time ranking), REQ-402/403 (custom leagues), ADR-0043 (per-`GameKey` leaderboards), REQ-411 (per-player stats/rank view, S-178), ADR-0095 (per-`GameKey` sort direction for all four ranking scopes, including the median all-time ranking, closed same-day as a direct follow-up), ADR-0100 (per-`GameKey` `IRoundScoreSource` round-total sourcing, closing the `"xg-predict"` leaderboard gap ADR-0097 deferred) |
| COMP-03 Core.Rounds | ADR-0022 (close-previous-round-on-generate), REQ-408 (`Round.ClosedAt`), ADR-0051 (per-`GameKey` round scheduling) |
| COMP-04 Core.Scoring | ADR-0020 (exclude own guess from ratio), ADR-0021 (lowest-wins scoring), ADR-0040/ADR-0041 (pluggable `IScoringStrategy`/attempt cap), ADR-0049 (clue-efficiency strategy signature), ADR-0057 (wrong-guess player resolution), ADR-0095 (`IScoringStrategy.LowerIsBetter` + `XGPredictScoringStrategy`, the one `false` exception), ADR-0100 (`IRoundScoreSource`/`IRoundScoreSourceResolver`, `GuessRoundScoreSource`) |
| COMP-05 Games.XGGrid | ADR-0018 (guess-time live-lookup fallback), ADR-0023, ADR-0032, ADR-0035 (home-nation query path), ADR-0050/ADR-0052 (cache-warming failure tracking), ADR-0061 (trophy pool growth), ADR-0068 (responsibility split), ADR-0070 (guess-time fallback config flag), ADR-0078 (confirm a fully-swept, below-threshold pair low without a live query), ADR-0093 (recent-transfer sweep's new arrivals are now a valid Grid guess answer immediately, correcting ADR-0092's Grid-vs-Path asymmetry — COMP-05's own live-correctness reads are unchanged) |
| COMP-06 Data.PlayerStore | ADR-0015 (override precedence), ADR-0042 (career stints), ADR-0050/ADR-0052 (data-quality markers), ADR-0053 (player suggestions), ADR-0067 (repository split), ADR-0077 (`PlayerAttribute` populated from prefetch's bulk pool sweeps, not only pairwise lookups), ADR-0078 (`CountryDefinition`/`ClubDefinition.PlayerPoolSweptAt`, invalidated by REQ-111's cleaner and `purge-player-pool`) |
| COMP-07 DataSync.Clients | ADR-0035 (query-path dispatch), ADR-0050/ADR-0052 (timeout tiers, query-shape fix), ADR-0054/ADR-0055/ADR-0069 (career-stint refresh/prefetch, prefetch pool widened to clubs), ADR-0056 (familiarity filter), ADR-0059 (club canonicalization), ADR-0061 (trophy query shape), ADR-0077 (prefetch pool sweeps double as `PlayerAttribute` source, eliminating live pairwise queries for fully-swept pairs), ADR-0078 (prefetch also stamps `PlayerPoolSweptAt` on its own success path), ADR-0088 (prefetch now also *reads* `PlayerPoolSweptAt` to skip an already-fully-swept country/club on re-run — no live Wikidata call, no Supabase dedup read-back), ADR-0090 (rotating, bounded weekly re-sweep of a small number of already-swept countries/clubs, oldest `PlayerPoolSweptAt` first, so a transfer into an already-swept pool is eventually noticed without reintroducing ADR-0088's unbounded-re-sweep cost), ADR-0092 (a third, orthogonal freshness mechanism — a targeted, date-filtered per-club sweep, `workflow_dispatch`-only, for faster-than-the-rotation checks around a transfer-window deadline; still never touches `PlayerPoolSweptAt` — see ADR-0093 immediately below for the correction to what this ADR originally said about `PlayerAttribute`), ADR-0093 (S-189: a precise trace found ADR-0092's stated caution against writing `PlayerAttribute` here overstated — `ConfirmedLowMatchPair` is never consulted on any live-correctness path, and `PairLookupFailure`, while consulted at guess time by `GridLiveLookupDispatcher`, only ever costs latency, never correctness, when stale — so `RecentTransferSweepService` now also writes `PlayerAttribute`/`PlayerData` for new arrivals plus a targeted `ClearMatchPairAsync` invalidation, making a transfer this mechanism picks up a valid xG Grid answer immediately, closing the Grid-vs-Path freshness asymmetry ADR-0092 left open), ADR-0094 (S-191's client-only story: `IApiFootballClient`/`ApiFootballClient`, the fixtures/results REST client for xG Predict — no round generation/grading yet) |
| COMP-11 Games.XGPath | ADR-0045 (puzzle generation), ADR-0041 (attempt cap), ADR-0049 (clue-efficiency scoring), ADR-0051 (round scheduling), ADR-0054/ADR-0055/ADR-0056/ADR-0069 (career data + familiarity + club-scoped prefetch), ADR-0058 (no-repeat cycling), ADR-0059 (club canonicalization), ADR-0075 (B-team/reserve-team exclusion), ADR-0082 (`XGPathGameModule`/`PathEligibilityService` responsibility split), ADR-0091 (career-stint completion on end-date fill-in, closing a duplicate-club-reveal-clue bug in REQ-1203's clue timeline — a narrow, scoped exception to ADR-0054's "additive only" trade-off) |
| COMP-12 Core.IncidentReporting | ADR-0064 (incident reporting design), ADR-0066 (cached admin issue summary) |
| COMP-13 Core.Announcements | ADR-0065 (singleton banner design) |
| COMP-14 Core.AvatarSubmissions | ADR-0087 (avatar storage: Supabase Storage, client kept out of Core/Api) |
| COMP-15 Games.XGPredict | ADR-0094 (API-Football fixtures/results as data source), ADR-0095 (higher-is-better scoring exception to ADR-0021 — `LowerIsBetter`/`XGPredictScoringStrategy`/the leaderboard migration built 2026-08-30, see COMP-04/COMP-02 rows), ADR-0096 (round/match/prediction entity shape and `ScoreSubmissionAsync` return/exception contract for REQ-1301/1302/1303, amended same-day for the exception-hierarchy/field-naming detail), ADR-0097 (REQ-1305's grading trigger — a new hourly scheduled job/endpoint, mirroring ADR-0072's per-`GameKey` workflow shape — plus the `PredictMatch`/`PredictMatchPrediction` grading-state/read-path shape, and the decision that a locked-but-ungraded round's `Closed` status/leaderboard participation are fully decoupled from grading completeness), ADR-0098 (S-197: the first real HTTP surface for xG Predict gameplay, `PredictEndpoints` — decides REQ-1306's per-player confirm-lock check lives in the API endpoint, not `XGPredictGameModule.ScoreSubmissionAsync`, and is persisted in its own `PredictPlayerLock` table rather than a column on `PredictMatchPrediction`, explicitly superseding ADR-0096's own breadcrumb suggesting that column), ADR-0100 (S-199: `PredictRoundScoreSource`, the `"xg-predict"` implementation of `Core.Scoring.IRoundScoreSource`, closing the leaderboard-wiring gap ADR-0097 deferred; adds `IPredictInstanceRepository.GetParticipantUserIdsByInstanceIdAsync`), ADR-0101 (S-201: `XGPredictGameModule.PurgeUserDataAsync` anonymizes `PredictMatchPrediction`/hard-deletes `PredictPlayerLock` on account deletion, closing the REQ-710 gap ADR-0098 flagged) |

## 6. Key data flows

**6.1 Grid generation flow** (realizes REQ-101, REQ-102, REQ-103, REQ-109)

`Core.Rounds` (COMP-03) drives grid generation end to end. Each `GameKey`'s
own daily cron (`generate-grid-round.yml`/`generate-path-round.yml`/
`generate-predict-round.yml`, split from a single `generate-round.yml` as
of S-136/ADR-0072, extended to a third independent file for `"xg-predict"`
by the 2026-08-30 round-scheduling wiring story per that ADR's amendment)
is the flow's only production trigger: it calls `POST /internal/generate-round`
(`XGArcade.Api.Rounds.InternalRoundEndpoints`, bearer-token-protected,
registered in every environment — this endpoint is CONT-05's actual
realization, not a separate console job) for its own `GameKey`, with its
own independent retry loop. The endpoint
takes an optional `gameKey` query parameter (defaulting to `"xg-grid"` for
callers that omit it); its own `gameKey switch` — the *only* place in the
handler that branches on `GameKey` — dispatches to
`GridTemplateResolver`, `PathTemplateResolver` (`XGArcade.Api.Path`), or
(as of the same wiring story) `PredictTemplateResolver`
(`XGArcade.Api.Predict`) to resolve a `TemplateId`, while auth, the
`roundDurationHours` floor validation, and the response/error shape (an
unrecognized `gameKey` returns 400) all stay generic.

The endpoint then calls `RoundGenerationService.GenerateNextRoundIfNeededAsync`
(REQ-301's one-round-ahead rule), which takes a leading `gameKey` parameter
and resolves that game's `RoundSchedulingOptions` via
`IRoundSchedulingOptionsResolver` (ADR-0051, mirroring
`IScoringStrategyResolver`'s per-`GameKey` resolution shape, ADR-0040)
rather than a directly-injected singleton — three instances are registered
today (`"xg-grid"`, `"xg-path"`, and, as of the 2026-08-30 round-scheduling
wiring story, `"xg-predict"`), each with its own independently-configured
`RoundDuration`. See ADR-0051 (and its 2026-08-30 amendment, re-deriving
the pattern for this third `GameKey`) for the full decision, alternatives
considered, and why `GridSize`/`PuzzleCount` live on each game's own
generation-options class (`GridGenerationOptions`/`PathGenerationOptions`)
rather than on `RoundSchedulingOptions` itself. The service creates the
`Round` only once `GridGameModule.GenerateInstanceAsync` (`IGameModule`,
COMP-05) succeeds, then closes the *previous* round — never the one it
just created — via `IRoundCloseService` (ADR-0022), found through
`IRoundRepository.GetPreviousByGameKeyAsync` since "the latest round" is
the wrong one to check. This same cron is Tier 0's only
production-scheduled trigger point, so REQ-205's score-locking (§6.2) runs
in the deployed environment, not only via the non-Production test-data
endpoint. `Round` carries a nullable `ClosedAt` column (REQ-408):
`RoundCloseService.CloseRoundAsync` sets it once, first-close-wins, only
*after* `IScoreLockingService.LockRoundScoresAsync` completes successfully
— never before or concurrently, so a reader can never observe `ClosedAt`
set while some guesses in that round still have `FinalPoints == null`.
COMP-02's `GetClosedRoundsAsync`/`GetClosedRoundLeaderboardAsync` gate
purely on this column.

`IRoundCloseService.CloseRoundAsync` has three callers: `RoundGenerationService`
above, `XGArcade.Api.Admin.AdminManagementEndpoints` (REQ-505, which finds
the caller's own active round and, for its "adjust end_time" action, loads
and saves it), and the non-Production-only
`/internal/test-data/force-close-round/{roundId}` (REQ-806) — all reached
only through that one interface plus `IRoundRepository`, never a second
data-access path. The admin path is also the first non-test-only,
admin-facing use of ADR-0006's fail-closed "endpoint group not registered
at all outside non-Production" pattern — that pattern otherwise only gates
`Testing.SeedManager` (COMP-09); see §7's Authorization row.

`POST /internal/grid/generate` is a second, deliberately-kept endpoint: it
exercises grid generation in isolation from round scheduling for manual
testing (covered by `GridEndpointTests.cs`) and, unlike
`/internal/generate-round`, stays non-Production-only (ADR-0006-style
gating). Neither endpoint's `GridTemplate` resolution goes through
`IGameModule`: both call `IGridInstanceRepository` directly (via a shared
`GridTemplateResolver` helper) to find-or-create a `GridTemplate` by a
configured size. This is not a boundary violation — `GridTemplate` isn't
player data, and no boundary rule forbids the API layer from reaching it
directly — but it does mean there is still no admin-driven `GridTemplate`
management (REQ-102's full scope) for either endpoint to route through
instead.

Candidate selection reads reference tables only —
`Data.PlayerStore`/COMP-06's `CountryDefinition`/`ClubDefinition`/
`TrophyDefinition` (ADR-0012), never derived ad hoc from whatever's
already cached in `PlayerAttribute`. Each combination is resolved
cache-first, falling back to a live lookup on a cache miss (REQ-103;
Wikidata-only today, no API-Football leg), with the resulting
`GridInstance`/`GridCell`s and the chaining `Round` persisted once every
cell clears its threshold. Scoped to Tier 0 (`MVP-SCOPE.md`), every grid is built from Country, Club,
and Trophy category values only — never a Country × Country cell
(REQ-107). As of ADR-0089 (2026-08-29), there is no longer one pairing
type fixed for the whole instance: `GridGenerationService
.GenerateInstanceAsync` draws each row and column header independently
from one shuffled, combined pool of every seeded Country, Club, and Trophy
candidate concatenated together (each candidate carries its own
`CategoryType`), so a header's odds of being a given type are naturally
proportional to that type's actual reference-data pool size (today: 45
countries, 21 clubs, 3 trophies) rather than a fixed feasibility table
keyed off grid `Size`. A single grid can therefore mix category types
freely across both axes (e.g. a Country row next to a Trophy column).
REQ-107's Country×Country ban is enforced by
`CategoryPairingRules.IsAllowedPairing`, now checked per individual (row
header, column candidate) pair inside `PickHeadersAsync`'s per-row loop,
before that row's match-count query — replacing the once-per-call check
against a globally-fixed pairing that only worked because every candidate
in a call used to share the same two category types. `SelectPairing`/
`PoolFor` and their per-pairing feasibility thresholds
(`trophyCount >= Size`, `trophyCount >= Size × 2`, etc.) are removed
entirely; the replacement feasibility check is a simple combined-pool-size
check (`>= Size` candidates available across all three types), applied to
both the row pool and, after removing already-used row values, the column
candidate pool. `ReferenceDataSeeder` seeds three trophies (Ballon d'Or,
FIFA World Cup, UEFA Champions League) — Country×Trophy, Club×Trophy, and
Trophy×Trophy headers are all reachable and selectable in production now
(ADR-0061 added the second and third trophy; ADR-0089 removed the
`Size × 2` ceiling that had kept Trophy×Trophy infeasible; REQ-108 has the
full detail).

Every live lookup a round's cells will ever need to reach `MinValidAnswers`
happens *during generation*, before the `Round` (the thing players can
actually see/play) is created at all — a `Round` only exists once every
cell has enough cached matches to clear REQ-101's threshold. Clearing that
threshold proves only that *some* valid answers exist, not that every one
does, so a real player can still be missing from the cache for a cell
that's otherwise valid; REQ-211's guess-time fallback exists precisely to
cover that remaining gap (ADR-0010's anticipated gap, ADR-0018) — see
§6.2.

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
- **This whole fallback is now config-flagged (2026-08-17, S-128, ADR-0070) —
  no longer unconditional once its own gate above passes.** New
  `GridLiveLookupOptions.Enabled` (default `true`, `GridLiveLookup:Enabled`
  config key) is checked by `GridGameModule.ScoreSubmissionAsync`
  immediately before the `PlayerNameIndex` gate; when `false`, neither
  `IPlayerNameIndexRepository.ExistsByNormalizedNameAsync` nor
  `IGridLiveLookupDispatcher.TryRefreshCellAsync` is ever called, and an
  unresolved guess fails closed exactly as it would have before REQ-211
  existed — same `ScoreResult` shape, no new outcome. A deliberate,
  reversible operational toggle (an env var flip, not a redeploy) so the
  product owner can validate whether S-127's proactively-built cache is
  complete enough on its own, with REQ-509/510's admin suggestion
  approve/commit flow as the remediation path for any gap surfaced that
  way. REQ-103's grid-generation-time live lookup
  (`GridGenerationService.GetMatchCountAsync` →
  `IGridLiveLookupDispatcher.LookupMatchesAsync`) is a separate call path
  through the same shared dispatcher and is completely unaffected — the
  flag lives only at this one call site, never inside
  `GridLiveLookupDispatcher` itself. See ADR-0070 for the full decision.
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
  code each `GameKey`'s round-generation cron actually invokes —
  `generate-round.yml` at the time, `generate-grid-round.yml`/
  `generate-path-round.yml` as of S-136/ADR-0072) now closes a round's
  predecessor before deciding whether to generate a successor, so this leg
  runs for real, on the same schedule REQ-301's generation already runs on
  — not a separate scheduled job. REQ-806's non-Production-only
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
  this leg only wrote to `Database`); see §5's COMP-04 row for the current
  shape of `MaterializeUnansweredCellsAsync` this depends on.

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

[triggered by each GameKey's own round-generation cron (generate-round.yml
 until S-136/ADR-0072; generate-grid-round.yml/generate-path-round.yml
 since), ADR-0022 — the same schedule REQ-301's round generation already
 runs on, not a separate job]
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
REQ-404, REQ-405, REQ-407, REQ-408, REQ-409, REQ-411 — REQ-406 was retired
by REQ-409, see below; Tier 0 slice only, added S-011, extended through
2026-08-24/S-178)

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
  IRoundRepository, gated on Round.ClosedAt — see §5's COMP-03 row) for the
  browsable round list, then Core.Scoring
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

Player → Web Frontend → Backend API: GET /users/{userId}/stats?gameKey=
  (REQ-411, S-178) → Api layer (UserEndpoints): resolve the requesting
  user (same ResolveRequestingUserAsync/ValidateGameKey helpers
  LeaderboardEndpoints already exposes internally, reused rather than
  duplicated) → 401 with no valid session, 404 for a nonexistent target
  userId → Core.Leagues (COMP-02): LeaderboardService.GetUserStatsAsync —
  no new aggregate path: rounds played/best/average `FinalPoints` reuse the
  exact same IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync query
  REQ-408/409 already call, and the current all-time rank (when the player
  meets REQ-409's 5-round minimum) reuses GetRankedMembersAsync, a private
  helper extracted from GetGlobalLeaderboardAsync above so this route's
  rank is never a second, independently-drifting formula. One deliberate
  difference from every other call into that per-round-totals query:
  applyGuestEligibilityRules: false — REQ-411's own "Out of scope" text
  carves out rounds-played/best/average from REQ-409/717's guest-exclusion
  rule (a guest's or a not-yet-claimed account's pre-claim rounds count
  toward these three figures the same as a claimed account's), while Rank
  still goes through the unchanged, guest-excluding GetRankedMembersAsync
  path. No privacy branching either: the same call and response shape are
  returned whether userId is the requester's own id or another player's.
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
REQ-1204, REQ-1205, REQ-1207 — added S-082, 2026-07-27; REQ-207 — added
S-091, 2026-08-01; REQ-1206's `Points` field — added 2026-08-08)

```
Player → Web Frontend: types a guess
  → Data.PlayerNameIndex (COMP-10): autocomplete suggestions — the SAME
    generic, game-agnostic query §6.2's diagram documents for xG Grid
    (REQ-207); no second autocomplete endpoint or read path exists for
    xG Path (S-091)
Player → Web Frontend → Backend API: GET /path/current
  (XGArcade.Api.Path.PathEndpoints) → Core.Rounds (IRoundRepository):
  resolve the active "xg-path" round, 404 if none
  → Games.XGPath (COMP-11, via IPathInstanceRepository): read
    PathInstance/PathPuzzle directly, bypassing IGameModule — ADR-0016's
    direct-repository-read pattern, confirmed for a second game module by
    ADR-0048, mirroring RoundEndpoints' GET /rounds/current (§6.2) exactly
  → Games.XGPath (via IGameModule.GetMaxAttemptsForCellAsync, ADR-0041):
    resolve each puzzle's attempt cap (fixed 7) to compute its locked state
  → Core.Scoring (COMP-04, via IScoringStrategyResolver, ADR-0049): resolve
    ClueEfficiencyScoringStrategy for round.GameKey ("xg-path") once per
    request — 2026-08-08 addition, REQ-1206's Points field. Computed per
    puzzle, only once that puzzle's guess is Locked: ScoreCorrectGuess(guess,
    [], maxAttemptsForCell) on a correct guess (the same real formula
    ScoreLockingService will separately persist as FinalPoints once the
    round closes, never reimplemented here), or ScoringRules
    .MaxPointsPerCell directly on a locked-but-unsolved puzzle (the
    strategy is only ever invoked for a correct guess) — see §5's COMP-04
    and COMP-11 rows for the current shape of IScoringStrategy this relies on
  → Data.PlayerStore (COMP-06): bulk-read PlayerCareerStint (ADR-0042),
    Player.Position/BirthYear (REQ-1207), and PlayerAttribute's
    "nationality" rows (display-only, never PlayerOverride/
    HasEffectiveAttributeAsync) for every puzzle's target player, once for
    the whole instance
  → Games.XGPath: PathCareerStintFilter excludes any leftover pre-2026-08-02
    national-team row, senior or youth (2026-08-08 bug fix scoped to
    youth/age-grade only, broadened to any national team 2026-08-10 — see
    COMP-11's own table entry above for both dates' full reasoning) before
    PathClueSequenceBuilder assembles the full 7-turn sequence per puzzle,
    then the response includes only the turns the requesting player's own
    attempt count has unlocked so far — the target player's identity is
    never included unless that player's own guess already resolved it
    correctly (REQ-1204)

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
2026-08-01; submission half only — REQ-509/510's separate admin
review/commit half, S-090, has no dedicated flow diagram in this section)

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
`PlayerOverride`) — boundary rule 5 and ADR-0053 both apply: a suggestion
is a pending human claim, not a data write, until an admin commit
(REQ-509/510, S-090, `AdminSuggestionEndpoints`) resolves it through the
normal `PlayerOverride`/`PlayerAttribute` write path REQ-501 already uses,
per ADR-0060's field-cardinality split. The
row/col category type lookup originally read `GridCell` directly via
`IGridInstanceRepository` from the Api layer, bypassing `IGameModule` — a
boundary rule 2 violation caught by `architecture-reviewer` before merge
and corrected to the `IGameModule.GetCellCategoryTypesAsync` path shown
above.

**6.3 Data sync flow** (realizes REQ-501, REQ-502, REQ-503)

```
Sync Worker (CONT-04) → DataSync.Clients (COMP-07): fetch updates
  → Data.PlayerStore (COMP-06): write to PlayerData (never PlayerOverride)
  → [merge on read] effective value = PlayerOverride if present, else PlayerData
Admin → Web Frontend (admin view) → Backend API: approve/correct unverified data
  → Data.PlayerStore: create PlayerOverride or mark PlayerData verified
```

Routine syncs (`DataSync.Clients`/COMP-07 → `Data.PlayerStore`/COMP-06) and
`PlayerCacheWarmingService` both persist `Confidence = "verified"` directly
(a `WikidataLookupOrigin` parameter on `IWikidataLookupService`,
ADR-0029/ADR-0032); REQ-211's guess-time fallback (§6.2) also persists
`Confidence = "verified"` (ADR-0032, which supersedes ADR-0029's earlier
fallback-specific `"unverified"` carve-out) — no code path writes
`"unverified"` today, pending a real player-suggestion/correction channel
(both ADRs' shared follow-up note). Merge-on-read prefers `PlayerOverride`
when present, else `PlayerData`, in exactly one place (ADR-0015).
`PlayerCareerPrefetchService`'s country/club pool sweeps (ADR-0055/
ADR-0069/ADR-0077) write the same paired `PlayerData`(`Source =
"wikidata"`, `Confidence = "verified"`)/`PlayerAttribute` shape directly,
not through `IWikidataLookupService` — a fourth, bulk-sweep-scoped writer
of the same pattern, deduped per-country/per-club rather than per
pairwise lookup (ADR-0077's own "why not the same shape as
`QueueAttribute`" note covers the one deliberate divergence: no repeat
`PlayerData` row on a sweep that re-confirms an already-known fact).

`XGArcade.Api.Admin.AdminEndpoints`, behind the "Admin" authorization
policy (§7), is the admin-facing surface, reached only through
`IPlayerDataRepository`/`IPlayerOverrideRepository` (COMP-06, ADR-0067) —
no separate data-access path. `POST/GET/PUT/DELETE
/admin/player-overrides[/{id}]` creates and manages `PlayerOverride` rows,
REQ-501's correction path. `GET /admin/player-data/unverified` lists any
remaining unverified backlog. `POST /admin/player-data/approve` flips one
or more `PlayerData` ids to `verified`
(`PlayerDataRepository.ApprovePlayerDataAsync`); audit fields
(`PlayerData.ApprovedByAdminId`/`ApprovedAt`) mirror `PlayerOverride`'s
`LockedByAdminId`/`LockedAt` shape rather than a separate audit-log table.
`POST /admin/player-data/remove` hard-deletes one or more `PlayerData` ids
(`PlayerDataRepository.RemovePlayerDataAsync`) regardless of `Confidence`;
since the row is gone rather than mutated, REQ-503's "the action is logged
with admin_id and a timestamp" requirement is satisfied by a structured
`ILogger` line per removed row instead of an audit column. Approve,
correct (`PlayerOverride`), and remove together implement REQ-503's full
scope. `AdminScreen.tsx` (SCREEN-04, REQ-504) is this admin surface's
frontend; the account-management and round-close admin actions REQ-504
also covers are described in COMP-01's and COMP-03's rows in §5, and in
§6.8 for account deletion specifically.

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
  → Core.Users: for every registered IGameModule, PurgeUserDataAsync(userId)
    (xG Grid/xG Path: no-op; xG Predict: anonymize PredictMatchPrediction,
    hard-delete PredictPlayerLock — see COMP-15's own row, ADR-0101)
  → Core.Users: delete NotificationPreference, LeagueMembership, User record
  → Auth provider (Supabase Auth): delete the credential/identity
  → Email becomes available for a new registration
```

`IAccountDeletionService` (COMP-01) is reached from several entry points,
all converging on the same `DeleteAccountAsync` call — never a second
implementation. The self-service path above (`DELETE /auth/account`,
REQ-710) is slightly compressed in the diagram: the "Core.Users" step
anonymizes every `Guess` row belonging to the user (severs the `UserId`
link rather than deleting the rows, since other players' uniqueness scores
and leaderboard totals depend on the total guess count), then (S-201,
ADR-0101) loops every registered `IGameModule` calling
`PurgeUserDataAsync(userId)` so each game can anonymize/hard-delete
whatever per-user data it owns beyond `Guess` — never a direct dependency
on a game-specific repository (ADR-0003) — also removes the user's
`LeagueMembership` rows (`ILeagueRepository`, COMP-02), then deletes the
`User` record and the Supabase Auth identity/credential — needing the
privileged `Supabase:ServiceRoleKey` secret, since the anon key the rest of
this flow's Supabase Auth calls use can't reach the Admin API (ADR-0026) —
freeing the email for a new registration. `NotificationPreference`
deletion is currently a no-op — that table doesn't exist yet in Tier 0
(Resend/notification preferences are Tier 1, `MVP-SCOPE.md`).

The same call is also reached by: admin-triggered deletion, `DELETE
/admin/users?email=` (`XGArcade.Api.Admin.AdminManagementEndpoints`,
non-Production-only, ADR-0006, REQ-506), which resolves the admin-supplied
email to a `User.Id` via `IUserRepository.GetByEmailAsync`;
`AuthController.Logout`, for the best-effort unclaimed-guest cleanup
described in §6.10's Rule 1; `InternalGuestCleanupEndpoints`'s scheduled
job (REQ-718/ADR-0038, §6.10's Rules 2/3); and `POST
/admin/accounts/guests/clear` (`XGArcade.Api.Admin.AdminAccountsEndpoints`,
registered unconditionally including Production, REQ-508), which selects
every currently-matching guest id via `IUserRepository.GetAllGuestIdsAsync`
— deliberately unfiltered, unlike REQ-718's age-filtered cleanup queries,
per REQ-508's own scope note. Every entry point converges on
`IAccountDeletionService.DeleteAccountAsync` once it has resolved the
target `User.Id`, so everything past that point is identical across all of
them.

**6.9 Backup flow** (realizes REQ-901 — Supabase's free tier has no built-in backups)

No backup automation currently runs. The `backup-database.yml` workflow
shown in earlier revisions of this diagram was deleted (S-130, 2026-08-17)
after failing all 40/40 of its scheduled runs — it targeted a
`PROD_DATABASE_CONNECTION_STRING` secret that has never been set, because
no prod environment exists yet. There is nothing at stake yet either:
Tier 0's one environment (dev) holds no real user data. See REQ-901's
status note in `docs/requirements-document.md` and the "Backups" section
of `infra/README.md` for the plan to rebuild this flow once Tier 1
provisions a real prod environment. The intended shape, once rebuilt:

```
[scheduled, daily — backup-database.yml (not currently present)]
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
points. Data-model side: `User.LastActiveAt` (non-nullable `DateTime`,
updated on login/guest-provisioning/claim/guess-submission) plus two new
`IUserRepository` queries, `GetUnclaimedGuestsOlderThanAsync`/
`GetInactiveGuestsOlderThanAsync`. Both call sites reuse `IAccountDeletionService.DeleteAccountAsync`
unmodified (§6.8) — no new deletion logic was written for this flow.
`/internal/purge-guest-accounts` runs in every environment, following
`/internal/generate-round`'s own precedent (§6.1) for why a
bearer-token-gated `/internal/*` endpoint whose only caller is a scheduled
job isn't restricted to non-Production the way `Testing.SeedManager`/COMP-09
is (ADR-0006).

**6.11 xG Predict round generation, prediction submission, and asynchronous
grading** (realizes REQ-1301–REQ-1306, ADR-0094, ADR-0095, ADR-0096,
ADR-0097, ADR-0098 — scaffolded 2026-08-30, REQ-1301/1302/1303's generation
and submission legs implemented for real the same day per ADR-0096, and
REQ-1305's grading leg implemented for real the same day per ADR-0097 (see
[Asynchronous grading — REQ-1304/1305] below, describing real, tested
`PredictGradingService`/endpoint/workflow behavior). All four legs (round
generation, prediction submission, asynchronous grading, and now the
confirm-and-lock leg) describe real, tested, HTTP-reachable behavior as of
2026-08-31 (ADR-0098, S-197 — see [Prediction submission and confirm-lock
— REQ-1302/1303/1306] below, replacing the previous "still not built"
sketch). Grading's own leaderboard-total wiring
(`GetTotalPointsByInstanceIdAsync`) remains a `docs/backlog.md` follow-up,
noted inline below.)

Unlike §6.1/§6.2's xG Grid flow and §6.2b's xG Path flow — both of which
resolve a submission's correctness synchronously, inside the same request
that submits it — xG Predict's shape has a genuine third leg neither
existing game has: grading happens well after submission closes, once a
real-world match result becomes available. The sketch below is in three
parts for that reason, not because the "player submits something, gets
scored" shape doesn't apply — it does, split across time.

```
[Round generation — REQ-1301, built 2026-08-30 (ADR-0096); wired end to
  end 2026-08-30 (round-scheduling wiring story, ADR-0051/ADR-0072
  amendments)]
Round Scheduler Job (COMP-03): .github/workflows/generate-predict-round.yml
  — a third, fully independent workflow file (daily cron, own
  workflow_dispatch.round_duration_hours input), calling the existing
  .github/actions/trigger-round-generation composite action with
  game-key: xg-predict, exactly generate-grid-round.yml's/
  generate-path-round.yml's shape (ADR-0072's 2026-08-30 amendment) — a
  daily check for "is it time to generate the next round" independent of
  how often Premier League gameweeks actually occur, same as xG Grid/xG
  Path's own crons
  → Api.Rounds (COMP-03): POST /internal/generate-round?gameKey=xg-predict
    — InternalRoundEndpoints' gameKey switch now has a third arm,
    resolving a PredictTemplate via the new PredictTemplateResolver
    (Api.Predict, find-or-create by PredictGenerationOptions.MatchCount,
    mirroring GridTemplateResolver/PathTemplateResolver)
    → RoundGenerationService resolves xg-predict's own RoundSchedulingOptions
      (RoundScheduling:XGPredict:RoundDurationHours, default 48h) via
      IRoundSchedulingOptionsResolver (ADR-0051) — now a third registered
      instance, alongside xg-grid/xg-path. ADR-0102 (S-204): this
      RoundDuration value is a DEAD FALLBACK for xg-predict specifically —
      still resolved (the resolver call is unconditional per GameKey) but
      never actually read, since the module below always supplies its own
      SuggestedStartTime/SuggestedEndTime once it returns a non-null
      instance
    → RoundGenerationService populates RoundConfig.LatestGameInstanceId
      from the GameKey's existing latest Round.GameInstanceId, if any
      (ADR-0102, new)
  → Games.XGPredict (COMP-15): XGPredictGameModule.GenerateInstanceAsync
    → DataSync.Clients (COMP-07, ADR-0099, superseding ADR-0094's original
      API-Football choice): fetch the gameweek's full fixture list from
      football-data.org's fixtures endpoint — the first live match-schedule
      data source in this codebase, distinct from every Wikidata career/bio
      query every other flow in this document uses
    → Games.XGPredict: select the tightest-kickoff-clustered MatchCount-match
      subset via a sort + linear sliding window (REQ-1301); throw
      PredictGenerationException (abort, caught by InternalRoundEndpoints'
      existing GridGenerationException/PathGenerationException filter,
      returned as a problem-details response and logged) if fewer than
      MatchCount fixtures exist
    → Games.XGPredict (ADR-0102, new): if RoundConfig.LatestGameInstanceId
      was supplied, compare the selected fixture-ID set against that
      instance's own fixture-ID set — if identical, return null ("no new
      round due"; RoundGenerationService treats this exactly like its
      existing "one round ahead already satisfied" no-op, returning the
      existing latest Round unchanged and persisting nothing new). This is
      what actually prevents both a duplicated matchday (generation firing
      again before the real next matchday changes) and a silently-skipped
      one (chain-math StartTime/EndTime landing after the matches' own
      kickoffs) — see ADR-0102 for the full root-cause trace
    → Games.XGPredict: persist the selected matches as a PredictInstance's
      PredictMatch rows via IPredictInstanceRepository — entity shape
      decided by ADR-0096 (PredictTemplate/PredictInstance/PredictMatch/
      PredictMatchPrediction, XGArcade.Data)
    → Games.XGPredict (ADR-0102, new): return GameInstance with
      SuggestedStartTime=now and SuggestedEndTime=<last selected match's
      kickoff> + PredictGradingOptions.TypicalMatchDuration — real-fixture-
      timing hints RoundGenerationService prefers over chain math whenever
      supplied
  → Core.Rounds (COMP-03): create Round with GameKey="xg-predict",
    GameInstanceId=<the returned ID>, StartTime/EndTime taken from the
    module's SuggestedStartTime/SuggestedEndTime (ADR-0102) rather than
    chain math — Core never sees the match/prediction shape itself, same
    opaque-reference discipline ADR-0003 already establishes for xG
    Grid/xG Path. This leg is now reachable in production; the API-level
    tests in RoundEndpointTests.cs exercise it end to end. Only round
    *generation* is wired by this story — see the submission leg below,
    still not HTTP-reachable

[Prediction submission and confirm-lock — REQ-1302/1303/1306, built
  2026-08-30 (ADR-0096) at the IGameModule level, HTTP-reachable
  2026-08-31 (ADR-0098, S-197)]
Player → Web Frontend (frontend/src/predict/PredictScreen.tsx +
  PredictMatchInput.tsx/PredictConfirmDialog.tsx, SCREEN-14) → Backend API:
  POST /predict/matches/{matchId}/predictions (XGArcade.Api.Predict.
  PredictEndpoints) — deliberately NOT
  POST /rounds/{roundId}/cells/{cellId}/guesses (GuessEndpoints):
  REQ-1302's two-integer (homeGoals, awayGoals) shape doesn't fit
  GuessSubmission(CellId, SubmittedName, ChosenPlayerId), and ADR-0096
  already ruled out routing predictions through
  Guess/IGuessSubmissionService (structurally incompatible: no attempt
  cap, no synchronous correctness). ADR-0098 decided this permanently —
  PredictEndpoints is xG Predict's own, permanent write surface, not an
  interim stand-in.
  → Api.Predict (PredictEndpoints): checks
    IPredictInstanceRepository.IsPlayerLockedAsync(instance, user) first
    (REQ-1306/ADR-0098 — a 409 if the player has already confirmed and
    locked, checked before ScoreSubmissionAsync is ever called) —
    this per-player check lives here, deliberately not inside
    XGPredictGameModule, per ADR-0098 Decision §1
  → Games.XGPredict (COMP-15): XGPredictGameModule.ScoreSubmissionAsync —
    resolves the PredictInstance/PredictMatch (throws PredictScoringException,
    derives from Core.Games.GameEntityNotFoundException, if either id doesn't
    resolve — mapped to 404 by PredictEndpoints), rejects a negative goal
    count with PredictInvalidSubmissionException (REQ-1302, mapped to 400),
    then checks the round lock (PredictInstance.LockInstant, a shared
    [NotMapped] computed property wrapping Matches.Min(m => m.KickoffUtc),
    against an injectable TimeProvider, mirroring XGPathGameModule's own
    precedent) and throws PredictRoundLockedException if it has passed
    (REQ-1303, mapped to 409) — otherwise persists/replaces the stored
    prediction via IPredictInstanceRepository (unique on
    (PredictMatchId, UserId), REQ-1302's "resubmission replaces") and
    returns ScoreResult { IsCorrect = false, PlayerAnswerId = null },
    a documented, deliberate misfit (ADR-0096 §4): false here means "accepted,
    not yet gradable," never "wrong" — no per-match attempt cap, unlike
    REQ-210's cap for xG Grid/xG Path
  → Api.Predict: 200 with PredictionSubmissionResponse, echoing back what
    was persisted

Player → Web Frontend (PredictConfirmDialog.tsx, after its own required
  second affirmation) → Backend API: POST /predict/confirm (Api.Predict.
  PredictEndpoints) — REQ-1306's entire implementation
  → Api.Predict: checks IsPlayerLockedAsync (409 if already confirmed),
    PredictInstance.LockInstant against now (409 if the round has already
    locked automatically, REQ-1303), and that a stored prediction exists
    for all 5 of the instance's matches (409, naming the missing count, if
    not) — then calls IPredictInstanceRepository.LockPlayerPredictionsAsync,
    which inserts a PredictPlayerLock row (XGArcade.Data, composite-keyed
    on (PredictInstanceId, UserId), migration
    20260831090000_AddPredictPlayerLock) — the row's existence is the lock,
    with no boolean to flip back off (ADR-0098 Decision §2: not a column on
    PredictMatchPrediction, superseding ADR-0096's own breadcrumb)
  → Api.Predict: 200 with ConfirmPredictionsResponse; GET /predict/current's
    ConfirmedLocked field reflects this independently of the round-wide
    Locked field REQ-1303 above computes

**Risk flagged, unresolved (ADR-0098):** REQ-1306's lock can only be
bypassed if GuessEndpoints/GuessSubmissionService ever becomes a second,
unguarded path into XGPredictGameModule.ScoreSubmissionAsync. Today that
path is safe only incidentally — GuessSubmissionService.GetMaxAttemptsForCellAsync
still throws NotImplementedException for "xg-predict", so it never reaches
ScoreSubmissionAsync — not because GuessEndpoints has a GameKey allow-list
(it doesn't). Whoever implements GetMaxAttemptsForCellAsync for this game
must add that guard, or move REQ-1306's check somewhere both paths pass
through; tracked as a docs/backlog.md follow-up, not fixed by this story.

[Asynchronous grading — REQ-1305, built 2026-08-30 per ADR-0097, after the
  round has locked. REQ-1304's own scoring formula and the
  leaderboard-direction mechanism it depends on are also built — see the
  note below the arrow they were previously sketched against]
[triggered hourly (0 * * * *) plus workflow_dispatch,
  .github/workflows/grade-predict-matches.yml — a new, independent
  workflow, deliberately not folded into generate-grid-round.yml/
  generate-path-round.yml (ADR-0072's boundary, extended by ADR-0097
  Decision §1: grading is a wholly separate concern from round
  generation), calling POST /internal/grade-predict-matches
  (XGArcade.Api.Predict.InternalPredictGradingEndpoints, bearer-token-gated,
  registered unconditionally like /internal/generate-round)]
Grading Job (grade-predict-matches.yml) → XGArcade.Api:
  POST /internal/grade-predict-matches
  → Games.XGPredict (COMP-15): PredictGradingService.GradeReadyMatchesAsync
    queries IPredictInstanceRepository.GetMatchesReadyForGradingAsync —
    every PredictMatch still GradingStatus == Pending whose KickoffUtc +
    PredictGradingOptions.TypicalMatchDuration has passed now (no
    Round/IRoundRepository dependency — ADR-0097's kickoff-implies-lock
    proof: a match's own kickoff having passed already implies its round's
    lock instant, the minimum of its 5 kickoffs, has passed too)
    → DataSync.Clients (COMP-07, ADR-0099, superseding ADR-0094):
      IFootballDataClient.GetFixtureResultAsync polls this match's
      live/final status
      → [NotYetConfirmed] leave the match Pending, retried on next
        hourly run — never a permanent failure, no write at all
      → [PostponedOrAbandoned] Games.XGPredict:
        IPredictInstanceRepository.VoidMatchAsync sets
        GradingStatus = Voided only — no ActualHomeGoals/ActualAwayGoals,
        no PredictMatchPrediction.FinalPoints ever written for this match
        (product-owner-confirmed voiding rule)
      → [Finished] Games.XGPredict: XGPredictScoringStrategy.ScorePrediction
        (the concrete class, not IScoringStrategy — ADR-0097's own
        "don't widen a shared interface for one caller" reasoning) computes
        the 3 independent components (outcome/home-goals/away-goals,
        REQ-1304) for every stored prediction against this match,
        higher-is-better (ADR-0095, a named exception to ADR-0021) →
        IPredictInstanceRepository.GradeMatchAsync persists the match's
        GradingStatus = Graded/ActualHomeGoals/ActualAwayGoals and every
        prediction's FinalPoints atomically (one SaveChangesAsync), so a
        mid-write crash can never leave the two out of sync —
        GradingStatus == Pending is the query's whole idempotency gate
        (Decision §3): a Graded/Voided match is never re-fetched or
        re-scored on a later run
  → Core.Leagues (COMP-02, wired 2026-08-31, ADR-0100/S-199): a round's
    growing total is readable via the new
    IPredictInstanceRepository.GetTotalPointsByInstanceIdAsync (sums
    FinalPoints per user over Graded matches only — Pending/Voided matches
    contribute nothing, never a placeholder), and
    ILeaderboardService/LeaderboardEndpoints now call it — every
    LeaderboardService scope resolves a per-GameKey Core.Scoring.IRoundScoreSource
    (IRoundScoreSourceResolver); PredictRoundScoreSource (Games.XGPredict)
    is the "xg-predict" implementation, wrapping IPredictInstanceRepository
    only (see COMP-02/COMP-04/COMP-15's own rows for the full shape). This
    closes the ADR-0097 Decision §2 follow-up the previous version of this
    paragraph flagged. REQ-302's Closed status and RoundCloseService remain
    completely unaffected by grading completeness either way — a round can
    close with matches still Pending, by design (ADR-0097 Decision §4), not
    a gap to fix.
  → Core.Leagues (COMP-02): leaderboard for GameKey="xg-predict" sorts
    descending (highest total first) — built 2026-08-30 (ADR-0095 Decision
    §3): IScoringStrategy.LowerIsBetter and LeaderboardService's three
    named OrderBy(TotalPoints) call sites (GetActiveRoundLeaderboardAsync/
    GetClosedRoundLeaderboardAsync/GetWindowedLeaderboardAsync) resolve
    direction per GameKey. Also built (same-day follow-up):
    GetRankedMembersAsync's separate median-based OrderBy (REQ-409/410, the
    all-time Global League ranking) now resolves direction per GameKey too,
    its own OrderBy/OrderByDescending branch rather than reused via
    RankByTotalPoints — closing the gap flagged against REQ-1304's own
    acceptance text; see ADR-0095's amendment and REQ-1304's status note
```

**Open questions this section deliberately does not resolve** (see
`docs/requirements-document.md` §7 for the authoritative list): REQ-1210's
round-completion-animation trigger condition assumes synchronous
resolution and does not straightforwardly extend to xG Predict's
sometime-later grading (product-owner-confirmed: xG Predict gets no
completion celebration at all, closing this question for this game
specifically — see REQ-1306's own status note); whether `ScoreResult`
needs widening (or xG Predict needs a parallel,
non-`IGameModule.ScoreSubmissionAsync` entry point) to properly represent
"accepted, pending" rather than reusing `IsCorrect = false`. The
prediction submission endpoint/DTO shape is no longer open — decided by
ADR-0096 (`PredictionSubmission`, `Core.Games`) and, as of ADR-0098, has a
real HTTP caller (`PredictEndpoints`). The round/match/prediction entity
shape itself is no longer open — decided by ADR-0096 — and neither is the
grading-job trigger mechanism, or how a locked-but-ungraded round's
`Closed` status/leaderboard participation interact — both decided by
ADR-0097. REQ-1306 (confirm-and-lock) is no longer unbuilt — decided and
implemented by ADR-0098. None of the remaining open questions block
`GenerateInstanceAsync`/`ScoreSubmissionAsync`/`GetCellIdsAsync`/
`PredictGradingService`/`PredictEndpoints` being real and tested as of
2026-08-31 — they block only the still-open `ScoreResult`-widening
question above, plus the pre-existing, separately tracked follow-ups:
wiring `GetTotalPointsByInstanceIdAsync` into `ILeaderboardService`, adding
a `GameKey` allow-list to `GuessEndpoints`/`GuessSubmissionService`
(ADR-0098's flagged risk), and REQ-710 account-deletion wiring for
`PredictPlayerLock`/`PredictMatchPrediction` — all in `docs/backlog.md`.

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
| ADR-0057 | REQ-216's wrong-but-real guess photo lookup reuses ADR-0011's `WikidataClient` as its own distinct, lower-priority trigger — Wikidata-only, no API-Football fallback, fires once at cell-lock time, fails silently (no photo) rather than fail-closed-as-incorrect | Accepted |
| ADR-0058 | xG Path target cycle tracking (REQ-1208/1209): cycle state is xG Path's own data (never a field on shared `Player`), scored against the live ADR-0056 familiarity-filtered pool with a tolerant "remaining < N" completion rule, not the larger structural pool or an exact-zero rule | Accepted |
| ADR-0060 | REQ-509/510's admin-suggestion-commit action splits its write path by field cardinality — single-valued nationality via `PlayerOverride` (ADR-0015's existing full-type-replacement semantics), multi-valued club(s) via additive `PlayerAttribute` rows instead, so confirming one club can never mask another | Accepted |
| ADR-0061 | Team-competition trophies (FIFA World Cup, UEFA Champions League) query via tournament-edition participation + winner join (`P1344`/`P3450`/`P1346`), not a direct player property — the individual-award shape (`P166`) doesn't exist for a team competition | Accepted |
| ADR-0062 | REQ-509/510's admin by-name Wikidata lookup resolves its candidate player via a federated `wikibase:mwapi` `EntitySearch` call instead of a raw, unindexed label/alias scan — the scan was cheap-looking but expensive enough in production to trigger an HTTP 502 from a gateway in front of WDQS, not just a client-side timeout | Accepted |
| ADR-0065 | Site-wide announcement banner (REQ-511): `GET /announcement-banner` is unauthenticated (second such endpoint after `GET /health`), and `AnnouncementBanner` is a true singleton table (at most one row, ever), not a settings table or a list/queue of banners | Accepted |
| ADR-0066 | Admin GitHub-issue polling cache (REQ-904): `IGitHubIssueClient.ListOpenIssuesByLabelAsync` reuses the existing REQ-903 PAT with no scope widening, fronted by a single shared in-process `IMemoryCache` entry (not per-admin/per-request) with stale-serve-on-failure semantics, rather than a new persistence table or a distributed cache | Accepted |
| ADR-0070 | REQ-211's guess-time live-lookup fallback (`GridGameModule.ScoreSubmissionAsync`) is now gated by a config-driven `GridLiveLookupOptions.Enabled` flag (default `true`), so it can be operationally disabled to validate S-127's proactively-built cache without a code change — REQ-103's grid-generation-time live lookup is a separate call path, deliberately untouched | Accepted |
| ADR-0071 | `Round.SequenceNumber` (REQ-304) is a plain `int`, `MAX+1` per `GameKey`, guarded by a `(GameKey, SequenceNumber)` unique index rather than an explicit transaction — a display-only label, never a routing/FK identifier | Accepted |
| ADR-0076 | REQ-215's `PlayerSuggestion` submission context is generalized off xG Grid: adds `GameKey` + nullable per-game opaque context (`CellId`/`RowCategoryType`/`ColCategoryType` for `xg-grid`, `PathPuzzleId` for `xg-path`), mirroring ADR-0003's `Round.GameKey`/`GameInstanceId` pattern; also widens the submission route to `POST /rounds/{roundId}/suggestions`, branching on `GameKey`, and confirms `XGPathGameModule.GetCellCategoryTypesAsync`'s `NotSupportedException` stays in place, unused by the new route | Accepted |
| ADR-0082 | `XGPathGameModule`'s eligibility pipeline (`GetEligiblePlayerIdsAsync`/`IsEligible`) is extracted into `IPathEligibilityService`/`PathEligibilityService`, mirroring ADR-0068's `GridGameModule` split exactly — no facade, `IGameModule` stays implemented directly on `XGPathGameModule` | Accepted |
| ADR-0086 | REQ-513's admin `POST /admin/players/{id}/refresh-from-wikidata` is a narrow, admin-triggered-only exception to `Player.FullName`/`Position`/`BirthYear`/`PhotoUrl`'s "set once at creation, never re-synced" rule — re-fetches by the player's own already-stored `WikidataQid`, per-field diff (a missing fetched value never overwrites), no confirmation step or review queue, re-applying ADR-0032's existing Wikidata-trust model rather than reopening it | Accepted |
| ADR-0089 | Grid row/column headers each pick their own category type (Country/Club/Trophy) independently, drawn from one combined reference-data pool, instead of `SelectPairing` fixing one homogeneous pairing type for the whole instance — REQ-107's Country×Country ban is checked per cell, not once globally; fixes the recurring "Ran out of candidates" generation failure without touching `MinValidAnswers` | Accepted |
| ADR-0103 | xG Connect: Core.Social (COMP-16) is a separate arcade-level component from Games.XGConnect (COMP-17), not folded together; `ConnectMatch` is a new first-class concept owned by COMP-17, never a `Round` — xG Connect's pairwise, on-demand match doesn't fit `Core.Rounds`'/`Core.Scoring`'s shared-round, `FinalPoints`-total shape | Accepted |

## 11. Glossary

See `requirements-document.md` §2 for domain terms (Grid, Cell, Round, Guess,
Uniqueness score, Override, Unverified data). This document additionally
uses:

| Term | Meaning |
|---|---|
| Container | A separately deployable/runnable unit (C4 terminology) |
| Component | A cohesive module within a container, with a defined responsibility |
| Effective data | The result of merging PlayerData with any PlayerOverride, override wins |
