# Code Quality & Health Assessment

**Repository:** `johanpearson/xg-arcade` (single monorepo: `backend/` — C# / ASP.NET Core, `frontend/` — TypeScript / React, `infra/` — Bicep/Azure, `docs/` — governing documentation)
**Assessment date:** 2026-08-11
**Method:** CodeScene/SonarQube-style manual review — structural discovery, git-churn analysis, and targeted deep reads of the largest/most-changed files per component, cross-referenced against this repo's own `docs/architecture-document.md` component boundaries and `docs/coding-guidelines.md`.
**Relationship to `CODEBASE_ANALYSIS.md`:** that document already tracks priority-ranked findings (P1-P4) with its own revision history and has driven two completed remediation epics (`docs/backlog.md` Epics 7-8). This report is a complementary lens — a numeric 1.0-10.0 score per module/component — not a replacement; findings here were cross-checked against Epics 7-8 before being turned into new stories (`docs/backlog.md` Epic 9), so genuinely-already-fixed items aren't re-flagged. Future sweeps of either kind are owned by the `code-health-auditor` agent (`docs/ai/agent-migration-plan.md` §8).

---

## 1. Executive Summary

- **Overall System Code Health Score: 6.4 / 10 — Fair, with a small number of severe, well-understood hotspots**
- **Key Takeaway:** This is a disciplined codebase for its stage — clean module boundaries (`IGameModule`, repository-per-concern data access), a real ADR/REQ paper trail, and a CI pipeline that actually gates merges. The debt is concentrated, not diffuse: one 1,815-line file (`WikidataClient.cs`) and one 1,039-line god-class (`GridGameModule.cs`) account for a disproportionate share of the risk, and the frontend has no shared data-fetching abstraction despite seven screens reinventing the same fetch/loading/error pattern. Two systemic risks compound the file-level debt: (1) the governing docs, especially `architecture-document.md`, have themselves begun accreting unbounded narrative history into single table cells (one cell is 14,718 characters), undermining the "read this before working" premise CLAUDE.md relies on; (2) this development sandbox has no `dotnet` SDK, so a running count of **34 separate instances** across `docs/`/`MVP-SCOPE.md` admit recent backend changes were "hand-traced, not compiled or run" before merge — real verification happens only in CI, after the fact, not during development.

---

## 2. Score Breakdown by Repository / Module

*(Single monorepo — rows are the natural sub-project boundaries this codebase already uses: `backend/src/*`, `frontend/`, `infra/`, `docs/`.)*

| Module | Code Health Score (1-10) | Risk Level | Primary Health Drivers / Issues |
|---|---|---|---|
| `XGArcade.Core` | 8.3 / 10 | Low | Small, single-purpose files (median well under 100 lines); `UniquenessCalculator`, `GuessSubmissionService`, `ScoreLockingService` each do one thing. Lives up to "thin platform core." ADR-0003 boundary (no game-specific leakage) verified intact. |
| `XGArcade.Data` | 8.3 / 10 | Low | 25,184 raw lines, but ~18,900 are EF Core-generated migration/designer code; substantive hand-written surface is ~5,200 lines and is well-factored. ADR-0067's repository split (772-line/43-method god-repo → 8 narrow repositories) is genuinely complete, not partial. |
| `XGArcade.Api` | 7.8 / 10 | Low | Consistent thin-endpoint pattern, uniform ProblemDetails error shape. `Program.cs` decomposition (S-102/112/113/114) succeeded — down to 29 lines of pure sequencing. `CliVerbDispatcher.cs` (621 lines) still has copy-pasted DI bootstrap per CLI verb. |
| `XGArcade.DataSync` | 5.5 / 10 (project) — **2.5 / 10 for `WikidataClient.cs`** | **High** | One file, `WikidataClient.cs` (1,815 lines, 39 methods), is 39% of the project's code and the single highest-churn file in the repo (15 commits) — SPARQL query-building, HTTP transport, retry/timeout policy, and JSON parsing all fused into one class with 9 near-duplicated HTTP-handling blocks. |
| `XGArcade.Games.XGGrid` | 4.5 / 10 | **High** | `GridGameModule.cs` (1,039 lines, 26 methods, 13 injected dependencies) is a textbook god-class: grid generation, 3-stage name matching, disambiguation, live-lookup dispatch, and DTO mapping all in one file, accreted through ~10 incremental REQ/ADR fixes with no split. |
| `XGArcade.Games.XGPath` | 6.8 / 10 | Medium | Smaller and better-isolated than XGGrid, but `GenerateInstanceAsync` already mixes eligibility + cycle-rollover + selection + persistence (~100 lines) — the same accretion pattern XGGrid took, caught earlier but not yet corrected. |
| `frontend/` | 5.5 / 10 | Medium-High | `LeaderboardScreen.tsx` (1,129 lines) is a genuine god component (4 state machines, 4 near-identical fetch effects). No shared data-fetching hook exists anywhere in the codebase — 7 screens hand-roll the same `loading/error/ready` + cancellation + auth-error pattern independently. The `lib/` API-client split (S-111) succeeded cleanly. |
| `infra/` (Bicep + scripts) | 8.0 / 10 | Low | Small, single-purpose modules (33–187 lines each), explicit trade-off documentation inline (e.g. region-choice rationale), sync/promote scripts centralize their table allowlist per ADR-0006/0009. No deep review performed beyond structure — scope this module for a dedicated infra review before real production reliance. |
| `docs/` (governing documents) | 4.5 / 10 | Medium | 32,221 lines across `docs/`, 67 ADRs, 97 REQs — thorough and traceable, but `architecture-document.md`'s per-component status cells have grown unbounded (one table cell alone is 14,718 characters of accumulated narrative history). This is the *same* accretion failure mode as `WikidataClient.cs`, in prose form, and it's the file every session is told to read first. |

---

## 3. Score Breakdown by Component / Layer

*(Mapped to this repo's own C4/COMP-xx architecture components, per `docs/architecture-document.md` §5.)*

| Component / Layer | Location | Score (1-10) | Key Metrics / Smells Observed |
|---|---|---|---|
| Core.Scoring / Core.Rounds / Core.Users (COMP-01/03/04) | `backend/src/XGArcade.Core` | 8.3 / 10 | `UniquenessCalculator.cs` 57 lines/1 method; `ScoreLockingService.cs` 166 lines, one clear orchestration path. Largest files (`LeaderboardService.cs` 285 lines, `SupabaseAuthClient.cs` 264 lines) still well within healthy range. |
| Data.PlayerStore (COMP-06) | `backend/src/XGArcade.Data/Repositories` | 8.3 / 10 | Post-ADR-0067: 8 independently-registered repositories, 6-8 methods each, no facade reintroduced. One deliberate, documented duplication (`GroupByPlayerIdAsync` copied 3x) traded off against inter-repo coupling. |
| DataSync.Clients (COMP-07) | `backend/src/XGArcade.DataSync/Wikidata/WikidataClient.cs` | **2.5 / 10** | 1,815 lines; `RunIntersectionQueryAsync` ~120 lines with a 4-branch timeout-tier switch; `ParseBindings` ~86 lines of hand-replicated nullable-field logic; 17-method interface surface signaling the class does too much. Companion test file is 3,463 lines / 46 cases (~75 lines/test) — itself a symptom of a hard-to-test class. |
| Games.XGGrid (COMP-05) | `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs` | 4.5 / 10 | 1,039 lines, 26 methods, 13 constructor dependencies, ~45% comment density (evidence of years of incremental patching without restructuring). `LookupLiveMatchesAsync` is an 80-line, 4-branch dispatcher with duplicated DTO construction per branch. |
| Games.XGPath (COMP-11) | `backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs` | 6.8 / 10 | 423 lines, mostly well-separated, but already shows the same multi-concern-method pattern XGGrid took at this size — a clear pre-emptive refactor candidate. |
| API / Routing (CONT-02) | `backend/src/XGArcade.Api` | 7.8 / 10 | `AuthController.cs` 773 lines but low per-method complexity (size driven by inline rationale comments, not tangled logic); consistent `Results.Problem`/`Problem()` error contract across 90+ call sites; `CliVerbDispatcher.cs` 621 lines with duplicated per-verb DI bootstrap. |
| Web Frontend (CONT-01) | `frontend/src` | 5.5 / 10 | `LeaderboardScreen.tsx` 1,129 lines / 1,600 test lines; `App.tsx` 603 lines carries 6+ concerns (routing, auth-session lifecycle, dialog state) that don't belong together; no shared `useAuthedFetch`/`usePaginatedFetch` hook anywhere despite 7 duplicate implementations. |
| Infrastructure as Code | `infra/bicep`, `infra/scripts` | 8.0 / 10 | Small, single-responsibility Bicep modules; documented Azure-capacity trade-offs inline; sync/promote scripts route through one shared table allowlist (ADR-0006/0009) rather than each hand-rolling its own. |
| Documentation / Process | `docs/`, `MVP-SCOPE.md`, `CLAUDE.md` | 4.5 / 10 | 67 ADRs and 97 REQs give this project unusually strong traceability for its size — but `architecture-document.md`'s COMP-05/COMP-11 status cells have grown to 14,718 and 11,583 characters respectively of un-pruned incremental history, and 34 separate places in the docs admit a backend change was verified only by hand-tracing, not a real compile, due to no `dotnet` SDK in this sandbox. |

---

## 4. Priority Refactoring Targets

### 🔴 Critical Hotspots (High Complexity / High Impact)

1. **`backend/src/XGArcade.DataSync/Wikidata/WikidataClient.cs`** (1,815 lines) — the single strongest hotspot in the repo by CodeScene's own complexity×churn methodology: highest line count *and* highest commit churn (15 commits) of any source file, with every commit adding another incremental bug fix or timeout tweak on top of an already-fused class. It mixes SPARQL query building, HTTP transport, 4 different timeout policies, retry logic, and JSON parsing in one place, with 9 near-duplicated HTTP-handling blocks. **Fix:** split into `SparqlQueryBuilder` (pure, static, trivially testable), `SparqlQueryRunner` (the one shared HTTP/timeout/retry policy `RunIntersectionQueryAsync` already models correctly — route every query through it instead of hand-duplicating), and `SparqlResponseParsers`. `WikidataClient` becomes a thin facade.

2. **`backend/src/XGArcade.Games.XGGrid/GridGameModule.cs`** (1,039 lines, 26 methods, 13 injected dependencies) — a god-class implementing grid generation, three-stage name matching, disambiguation, live-lookup orchestration, and DTO construction all in one `IGameModule` implementation. Every new REQ (207/208/209/211/216) has been layered directly onto this class rather than extracted. **Fix:** split into `GridGenerationService`, `GridNameMatcher`, and a `GridLiveLookupDispatcher`, composed behind the existing thin `IGameModule` adapter — the same shape ADR-0067 already proved works for `PlayerStoreRepository`.

3. **`frontend/src/leaderboard/LeaderboardScreen.tsx`** (1,129 lines, 1,600 test lines) — 4 independent state machines, 4 nearly-identical fetch/poll/cancel effects, and 4 copy-pasted `handleLoadMore*` functions in one component. The 1,600-line test file is a direct symptom, not just thorough coverage. **Fix:** one component per leaderboard scope (`AllTimeLeaderboard`, `LiveLeaderboard`, `PastRoundsLeaderboard`, `WindowedLeaderboard`) plus a shared `usePaginatedLeaderboard` hook.

4. **Absence of a shared frontend data-fetching hook.** Confirmed across 7 screens: each hand-rolls its own `phase: 'loading'|'error'|'ready'` union, its own cancellation-flag pattern, and its own `handleAuthError` catch block, with no shared abstraction. This is the single biggest structural driver behind both `LeaderboardScreen.tsx`'s size and its outsized test-line ratio (and a likely contributor to `AdminScreen.tsx`'s 191-line component / 1,328-line test file, a 7x ratio). **Fix:** extract `useAuthedFetch`/`usePaginatedFetch` once, migrate screens incrementally.

5. **Documentation accretion in `docs/architecture-document.md`.** The COMP-05 (Games.XGGrid) status cell is 14,718 characters — a running, unpruned narrative of every incremental change since the project began, in a document CLAUDE.md tells every session to read before touching a component boundary. This mirrors the exact code-hotspot pattern above (complexity growing unchecked through incremental additions with no periodic consolidation) but in the document meant to *prevent* that pattern in code. **Fix:** periodically collapse each COMP-xx cell to current-state-only, moving the historical narrative into `docs/CHANGELOG.md` (which already exists for this purpose) or a per-component decision log.

### 🟢 Quick Wins (Low Effort / High ROI)

1. Extract `LeaderboardRowsList`/`formatPoints` out of `LeaderboardScreen.tsx` into their own module — scope-agnostic already, just misplaced.
2. Deduplicate `CliVerbDispatcher.cs`'s near-identical backfill-handler DI bootstrap (`HandleBackfillPlayerPhotosAsync`/`HandleBackfillPlayerPositionBirthYearAsync`) into one shared "build Wikidata backfill dependencies" helper.
3. Consolidate `WikidataClient`'s triplicated `GroupByPlayerIdAsync`-style timeout/retry blocks even before the larger split lands — the shared path `RunIntersectionQueryAsync` already uses correctly should be the *only* path.
4. Add direct repository-level tests for `PlayerDataQualityRepository`'s five confirmed-low/technical-failure-tracking methods — currently covered only indirectly via `GridGameModuleTests`/`PlayerCacheWarmingServiceTests` (an acknowledged gap per ADR-0067).
5. Split `App.tsx`'s auth-session lifecycle (`handleAuthenticated`/`handleLogout`/`attemptSilentRefresh`/the `fetchMe` effect, ~150 lines) into a `useSession()` hook — self-contained already, just not extracted.

### 🏗️ Architectural & Structural Debt

- **No boundary violations found.** ADR-0003 (games reference Core only via opaque `GameKey`/`GameInstanceId`) and ADR-0007 (autocomplete/correctness-checking separation) both verified intact by direct grep of cross-namespace references — a genuine strength, not just a documented aspiration.
- **Verification debt, not architectural debt, but systemic:** 34 places across `docs/` and `MVP-SCOPE.md` explicitly note a backend change was "hand-traced... not compiled or run" because this development sandbox lacks a `dotnet` SDK. CI (`ci.yml`) does run `dotnet build`/`dotnet test` on every PR, so nothing merges unverified — but a real compile only happens *after* a change is written, not during. This is a sandbox-tooling gap, not a codebase defect, but it's worth fixing (e.g. provisioning `dotnet` in the dev container) since it's the most-repeated caveat in the entire documentation set.
- **Same accretion pattern, three places at once.** `WikidataClient.cs` (code), `GridGameModule.cs` (code), and `architecture-document.md`'s status cells (docs) all show the identical failure mode: new capability bolted onto an existing large unit rather than triggering a split, repeated dozens of times over the project's history. This suggests a process gap more than a skill gap — worth adding an explicit "does this file/section need to be split before this change lands" checkpoint to the `/quality-gate` or `doc-sync` workflow, since the existing agents (`quality-architect`, `doc-sync`) already have the right mandate but the pattern is recurring anyway.
- **Frontend lacks a shared data-fetching layer entirely**, unlike the backend's consistent repository/service pattern — this is the one place the two halves of the stack diverge in architectural discipline, and it's worth a deliberate ADR-level decision (a hook, a small client-state library, or a fetch-wrapper convention) rather than continuing to let each screen invent its own.
