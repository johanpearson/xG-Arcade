# xG Arcade — Codebase Analysis

**Scope:** `backend/`, `frontend/`, `infra/`, `docs/`
**Method:** line counting (`wc -l`; the original custom SLOC/comment-splitting Python scanner isn't checked into the repo and wasn't re-derived this pass — see §2/Appendix), `git log` churn analysis, targeted `grep`/manual review for security and duplication patterns, `npm audit` for frontend dependencies. `dotnet` CLI is still not available in this environment (confirmed again in the 2026-08-18 revision), so no backend build/`dotnet test` and no NuGet vulnerability audit were run directly — package-version and behavioral claims for already-merged backend work are taken from the merging PRs' own CI-gated results, not re-executed here. Frontend tooling (`npm run test`/`tsc -b`/`oxlint`) **was** run live in the 2026-08-18 revision after a fresh `npm install` (`node_modules/` isn't checked in) — a change from earlier revisions, which had neither installed. Everything reported as "verified in this pass"/"re-verified" **was** re-run or re-read live against current `main` in that revision, not assumed from prior revisions' findings.

**Revision history:**
- **2026-08-10** — original analysis, top 5 targets. Flagged `WikidataClient.cs` duplication, a high-severity `undici` transitive dependency, `Program.cs` composition-root sprawl, `AdminScreen.tsx`'s God Component, and `GridGameModule.cs`'s nesting → became Epic 7 (`docs/backlog.md`, S-099–S-105).
- **2026-08-11 (first update)** — re-verified 6 of 7 Epic 7 stories merged; flagged S-104 as not started.
- **2026-08-11 (second update)** — S-104 merged; Epic 7 fully complete (7/7). Fresh sweep found 5 next-batch targets.
- **2026-08-11 (third update)** — extended the sweep from top 5 to **top 10**, now that security and the original hotspots are settled ground. Two candidates were investigated and explicitly **cleared** rather than reported — see the note at the end of §1 — to keep this list honest rather than padded. Findings #1–#6 became `docs/backlog.md` Epic 9 (S-115–S-129, via `code-health-auditor`).
- **2026-08-18 (this revision, `code-health-auditor` periodic sweep)** — re-verified all 10 prior findings against current code/`git log`, not against `docs/backlog.md`'s notes alone. **#1, #3, #4, #5, #6 fully resolved** (`PlayerStoreRepository` split/ADR-0067, `Program.cs`/`CompositionRoot` docs synced, `frontend/src/lib/api.ts` split by domain, `CliVerbDispatcher.cs` restructured to a verb registry, `CompositionRoot` testing strategy decided and documented). **#2 half-resolved** (5 of 9 `AdminScreen.tsx` subcomponents now have dedicated tests; 4 remain indirect-only). #7–#9 (watch-only) unchanged in status; #8 was already marked superseded in the prior revision (S-121) and stays that way. #10 (`SuggestionsScreen.tsx`) re-inspected after growing to 697 lines (from 645) and still found proportionate — remains watch-only. One genuinely new finding from this sweep: `XGPathGameModule.cs` (backend/src/XGArcade.Games.XGPath), flagged as a **P2, rising** — see §1a below. Full numeric detail for every module/component is `CODE_HEALTH_ASSESSMENT.md`'s job, not repeated here; this document stays focused on the priority-ranked list. New work tracked as `docs/backlog.md` Epic 17.

---

## 0. Epic 7 Closeout (unchanged from the last revision)

All 7 stories (S-099–S-105) are merged and verified: `undici` fixed, `WikidataClient.cs` deduplicated (2,034→1,815 lines), `Program.cs` decomposed (1,245→29 lines), `AdminScreen.tsx` extracted (1,432→190 lines), `GridGameModule.cs` nesting flattened (25→3 deep-indent lines), CSS/TS comment dedup done. `npm audit`, CORS, SQL/SPARQL-injection guards, and secret scans remain clean, re-verified again in this pass. **No security or P1 findings anywhere in this revision.**

---

## 1. Executive Summary & Top 10 Priorities

**Overall health: Good, improving.** Of the 6 actionable findings from the 2026-08-11 revision, 5 are fully resolved and 1 (#2) is half-resolved — verified this pass via direct file/git inspection, not by trusting `docs/backlog.md`'s own "Built as" notes (several of which were missing entirely despite the work having shipped; added in this pass). One genuinely new finding surfaced: `XGPathGameModule.cs`, the file the 2026-08-11 revision's own watch-list logic would have predicted next, now showing the identical accretion pattern `GridGameModule.cs` showed before its split — caught earlier in its lifecycle this time (557 lines / 2 concerns worth of mixing vs. `GridGameModule.cs`'s 1,039 lines / 5 concerns before ADR-0068).

| # | Target | Priority | Status | One-line why |
| :-- | :--- | :--- | :--- | :--- |
| 1 | `PlayerStoreRepository.cs` / `IPlayerStoreRepository.cs` | P2 | **✅ Resolved (S-106/S-107, ADR-0067)** | Split into 8 independently-registered narrow repositories; confirmed no facade reintroduced, no repository re-exceeds the original's method count |
| 2 | `AdminScreen.tsx`'s 9 extracted components have zero dedicated tests | P2 | **◐ Half-resolved** | 5 of 9 now have their own `*.test.tsx` (`AccountMetricsSection`, `AnnouncementBannerSection`, `IncidentReportsEntry`, `PlayerSuggestionsEntry`, `XGPathCycleSection`); `GuestClearSection`/`RoundControlSection`/`UnverifiedDataSection`/`UserDeletionSection` remain indirect-only via `AdminScreen.test.tsx` (1,332 lines) → `docs/backlog.md` Epic 17 S-156 |
| 3 | `architecture-document.md`/`implementation-document.md` still describe `Program.cs` as holding DI/auth/CLI-verb/endpoint-mapping logic | P2 | **✅ Resolved (S-110)** | Both docs now reference `CompositionRoot/*.cs` directly; confirmed via grep |
| 4 | `frontend/src/lib/api.ts` (1,057 lines, 51 exports) | P3 | **✅ Resolved (S-111)** | Split by domain into `admin.ts`/`auth.ts`/`leaderboard.ts`/`rounds.ts`/`turnstile.ts`/`announcements.ts`/`useAuthedFetch.ts`/etc.; largest remaining `lib/` file is `types.ts` (637 lines, mostly type declarations, not a complexity concern) |
| 5 | `CliVerbDispatcher.cs` (649 lines, one single method) | P3 | **✅ Resolved (S-112)** | Restructured to a 14-verb registry (`Verbs` dictionary + one handler method each); now 736 lines but confirmed no duplication despite 3 verbs added since |
| 6 | `CompositionRoot/*.cs` has no dedicated unit tests | P3 | **✅ Resolved (S-113)** | Decision made and documented: `AuthSetupTests.cs` added for the one branch worth isolating; `docs/coding-guidelines.md` §"Composition-root testing" records the rest as deliberately integration-tested |
| 7 | Large test files continuing to grow (`WikidataClientTests.cs` now 3,973 lines, `GridGameModuleTests.cs` coverage moved to 4 files totaling 2,792, `AuthEndpointTests.cs` 2,095) | P4 — watch | No change in status | Growing for the right reasons (real regression coverage); navigability-only concern |
| 8 | `LeaderboardScreen.tsx` | P4 — watch | **Superseded 2026-08-16 (S-121)**, confirmed still split this pass | Split into 4 scope components + shared hook; see §5 |
| 9 | `AuthController.cs` (773 lines, churn 1) | P4 — watch | Unchanged | Same reasoning as before — still churn-1, still low complexity |
| 10 | `SuggestionsScreen.tsx` (now 697 lines, was 645) | P4 — watch | Re-inspected this pass, still watch-only | Grew via S-129's confirmation-message feature; still cohesive (one feature, one shared `PlayerReviewPanel`), own dedicated `SuggestionsScreen.test.tsx` at a healthy ~0.67x line ratio — see `CODE_HEALTH_ASSESSMENT.md` §"Watch-only" for the full inspection |

**New this revision:**

### #1a (P2, rising): `XGPathGameModule.cs` — the eligibility pipeline is now a separable concern

Flagged watch-only in spirit by the 2026-08-11 `CODE_HEALTH_ASSESSMENT.md` revision ("already shows the same multi-concern-method pattern XGGrid took at this size — a clear pre-emptive refactor candidate"), not carried into that revision's own top-10 (different report, different scope at the time). Confirmed materializing this pass: 423→557 lines (+32%), 8 commits since 2026-08-11 (REQ-1201/1203 eligibility-rule tightening, S-137/138/139/141 — behavior changes, not restructuring). `GetEligiblePlayerIdsAsync`/`IsEligible` (~150 lines together) form a genuinely separable eligibility-computation pipeline — candidate narrowing, stint sanitization, 3 structural checks, familiarity filtering — distinct from `GenerateInstanceAsync`'s orchestration and `ScoreSubmissionAsync`'s scoring, the same shape `GridGameModule.cs` was in before ADR-0068. Not yet critical (well-tested, exceptionally well-documented, 7 required dependencies vs. `GridGameModule`'s pre-split 13) but the clearest next target. **Fix:** extract `IPathEligibilityService`/`PathEligibilityService`, same shape as ADR-0068 — `docs/backlog.md` Epic 17 S-154.

**Cleared, not reported (checked and found to be fine):**
- **Grid vs. Path frontend "duplication"** (`GuessInput.tsx`/`PathGuessInput.tsx`, `ScoringExplainer.tsx`/`PathScoringExplainer.tsx`): these looked like copy-paste candidates by file-pair naming, but inspection found substantial real behavioral differences (different attempt/clue models, no shared "uniqueness" concept in xG Path) *and*, in `PathScoringExplainer.tsx`'s own comments, an explicit, reasoned decision already on record for why a shared abstraction was rejected — right down to citing this repo's own "three similar lines is better than a premature abstraction" convention. Re-litigating a decision that was already made deliberately and correctly isn't a finding.
- **Two separate `FakeWikidataClient.cs` test doubles** (`XGArcade.DataSync.Tests`, `XGArcade.Games.XGGrid.Tests`): looked like duplicated test infrastructure at a glance. On inspection, each is independently and narrowly scoped to what its own test project's system-under-test actually calls (339 lines with full intersection/photo-batch stubbing vs. 125 lines stubbing almost nothing, since `GridGameModule` only calls one method on the interface) — this is the repo's documented "don't over-mock" convention working correctly, not lazy duplication.
- **Design-token discipline**: grepped for hardcoded hex colors outside `index.css`'s own token definitions — zero found. Still clean.
- **Dead/unused API exports**: checked every `frontend/src/lib/api.ts` export for at least one call site elsewhere in the frontend — zero unused.
- A nesting false-positive on `AdminSuggestionEndpoints.cs` surfaced by this report's own line-indentation heuristic turned out to be multi-line method-call argument lists (record construction, multi-parameter logger calls), not real control-flow nesting — worth naming since it's a limitation of the heuristic used throughout this report, not a finding about that file.

---

## 2. Codebase Size & Comment Hygiene

Backend + frontend source (excluding EF Core migrations, `bin`/`obj`, `node_modules`): **85,333 raw lines** across `.cs`/`.ts`/`.tsx` (production + tests), up from the prior revision's SLOC-only 72,172 figure — not directly comparable line-for-line since this pass used a plain `wc -l` scan rather than re-running the prior revision's custom SLOC/comment-splitting Python scanner (that tool itself isn't checked into the repo; re-deriving it was judged not worth the time this pass since no new bloat/hygiene finding emerged from the targeted deep-reads that *were* done — see §4). `WikidataClientTests.cs` (3,973 lines) is now the single largest file in the repo including tests, up from `WikidataClient.cs` itself at the last revision. No new under-documented or noise-comment finding surfaced by this pass's targeted reads (`XGPathGameModule.cs`, `CliVerbDispatcher.cs`, `SuggestionsScreen.tsx`, `architecture-document.md`).

`IWikidataClient.cs` (595 lines, comment-to-code ratio unchanged in shape from the last revision) remains this codebase's established, deliberate documentation style, not bloat — same conclusion as before, re-confirmed by spot-check rather than re-flagged.

---

## 3. Security Findings

Re-verified fresh this pass (fresh `npm install`, since `node_modules/` isn't checked in): `npm audit --omit=dev` **0 vulnerabilities** (production dependencies clean). Full `npm audit` (including dev dependencies) surfaces **1 new high-severity finding since 2026-08-11**: `nanoid@3.3.17` (transitive, via `vite@8.2.0` → `postcss@8.5.25`) — GHSA-2v37-7h3g-55p8, "custom generators can loop indefinitely when size is zero." Dev-tooling-only (never bundled into the shipped frontend), and the app itself never calls `nanoid` directly with attacker-controlled input, so real-world exploitability here is effectively none — but it's a genuine advisory that didn't exist at the last revision, so it's reported rather than silently cleared. Per `CLAUDE.md`'s own convention, Dependabot (`.github/dependabot.yml`) owns routine minor/patch drift like this — not fixed directly in this sweep, left for Dependabot's normal PR. No hardcoded secrets (re-grepped); no `FromSqlRaw`/`ExecuteSqlRaw`; `WikidataQid.IsValid` guard still centralized; CORS still an explicit allow-list; no `eval()`/`innerHTML`/`dangerouslySetInnerHTML` in production code. NuGet package versions not re-verified this pass (no `dotnet` SDK in this sandbox, confirmed again — `dotnet: command not found`). **No P1 security findings.**

---

## 4. Priority Detail — New Findings

### #1 (P2): `PlayerStoreRepository.cs` — repository spanning too many concerns

Confirmed as a genuine outlier, not just a "large file," by comparing method counts across **every** repository in `backend/src/XGArcade.Data/Repositories/`:

| Repository | Methods | Lines |
| :--- | ---: | ---: |
| `PlayerNameIndexRepository.cs` | — | 191 |
| `PlayerDataQualityRepository.cs` | — | 162 |
| `UserRepository.cs` | 16 | 161 |
| `PlayerRepository.cs` | — | 152 |
| `PlayerCareerStintRepository.cs` | — | 151 |
| `GuessRepository.cs` | 11 | 146 |
| `PathInstanceRepository.cs` | 11 | 145 |
| *(the rest, all smaller)* | | |

**✅ Resolved (S-106/S-107, ADR-0067).** `PlayerStoreRepository.cs`/`IPlayerStoreRepository.cs` (44 methods/772 lines) no longer exist — split into 8 independently-registered narrow repositories (`IPlayerRepository`, `IPlayerDataRepository`, `IPlayerAttributeRepository`, `IPlayerAliasRepository`, `IPlayerOverrideRepository`, `IPlayerBackfillRepository`, `IPlayerCareerStintRepository`, `IPlayerDataQualityRepository`). Re-ran this pass's own method-count comparison across every current repository in `backend/src/XGArcade.Data/Repositories/`: no repository comes close to the original 44-method outlier, and no facade was reintroduced (confirmed — no `PlayerStoreRepository`-shaped class exists anywhere in the codebase, grepped).

### #2 (P2, half-resolved): `AdminScreen.tsx`'s extracted components — test coverage still incomplete

S-103's original extraction correctly left `AdminScreen.test.tsx` as the interim regression net; since then, 5 of the 9 originally-flagged components gained their own dedicated test file (`AccountMetricsSection.test.tsx`, `AnnouncementBannerSection.test.tsx`, `IncidentReportsEntry.test.tsx`, `PlayerSuggestionsEntry.test.tsx`, `XGPathCycleSection.test.tsx`), confirmed via direct file listing this pass. `useAdminSectionFetch.ts` was itself promoted and renamed to `useAuthedFetch.ts` (S-120), so it's no longer a separate untested concern — it's the shared hook. Four components remain indirect-only: `GuestClearSection.tsx`, `RoundControlSection.tsx`, `UnverifiedDataSection.tsx`, `UserDeletionSection.tsx`. Tracked as `docs/backlog.md` Epic 17 S-156, not re-planned here.

### #1a (P2, new this revision): `XGPathGameModule.cs` — the eligibility pipeline is now a separable concern

Full write-up is in §1 above (under "New this revision") rather than repeated here, to avoid narrating the same finding twice in one document. **Fix:** `docs/backlog.md` Epic 17 S-154 (extract `IPathEligibilityService`, same shape as ADR-0068's `GridGameModule` split).

### #3–#6: all four fully resolved (S-110/S-111/S-112/S-113)

- **#3** (docs described the pre-S-102 shape of `Program.cs`): resolved by S-110 — both `architecture-document.md` and `implementation-document.md` now reference `CompositionRoot/*.cs` directly; re-grepped this pass, zero stale `Program.cs`-holds-this-logic claims found.
- **#4** (`frontend/src/lib/api.ts`, 1,057 lines/51 exports): resolved by S-111 — split by domain (`admin.ts`/`auth.ts`/`leaderboard.ts`/`rounds.ts`/`turnstile.ts`/`announcements.ts`/`useAuthedFetch.ts`/`roundTime.ts`/`incidentReportCopy.ts`, largest is `types.ts` at 637 lines of mostly type declarations); confirmed every export still has a call site (spot-checked, not re-run exhaustively).
- **#5** (`CliVerbDispatcher.cs`, one 649-line method): resolved by S-112 — now a `Verbs` dictionary + 14 named handler methods (736 lines total, but proportional to verb count, not one sequential blob); confirmed on inspection that the 3 verbs added since (`prefetch-player-careers`, `reset-path-target-cycle`, `purge-game-history`) each follow the same registry shape with zero duplicated DI-bootstrap.
- **#6** (`CompositionRoot/*.cs` testing strategy undecided): resolved by S-113 — `AuthSetupTests.cs` now exists for the one branch worth isolating (`useLocalE2EAuth`), and `docs/coding-guidelines.md` explicitly documents the rest as a deliberate integration-only choice, not a default.

---

## 5. Watch-Only Items (#7–10) — Explicitly Not Action Items

Per this report's own priority-matrix doctrine (low churn + not-yet-a-problem = leave alone until something else touches the file), these are listed for visibility only:

- **#7 — Large test files**: `WikidataClientTests.cs` (3,973 lines, still the single largest file in the repo including tests), `GridGameModuleTests.cs`'s coverage (now split across 4 files post-S-119: `GridGameModuleTests.cs`/`GridGenerationServiceTests.cs`/`GridNameMatcherTests.cs`/`GridLiveLookupDispatcherTests.cs`, 2,792 lines total), `AuthEndpointTests.cs` (2,095, unchanged). Growing for legitimate reasons (real regression assertions — S-118/S-124/S-127 each added byte-for-byte assertions to `WikidataClientTests.cs` specifically). No action.
- **#8 — `LeaderboardScreen.tsx`**: **Superseded (2026-08-16, S-121)**, status re-confirmed this pass unchanged from the last revision — split into a 261-line thin orchestrator plus `AllTimeLeaderboard.tsx`/`LiveLeaderboard.tsx`/`PastRoundsLeaderboard.tsx`/`WindowedLeaderboard.tsx`/`LeaderboardRowsList.tsx`.
- **#9 — `AuthController.cs`**: 773 lines, still churn-1 in the repo's entire history (re-checked this pass). No action.
- **#10 — `SuggestionsScreen.tsx`**: grew to 697 lines (from 645) via S-129's confirmation-message feature. Re-inspected directly this pass (not just re-measured): still one cohesive feature (REQ-509/510) with one deliberately-shared `PlayerReviewPanel` component across its two entry points, not four unrelated concerns the way pre-split `LeaderboardScreen.tsx` was. Own dedicated `SuggestionsScreen.test.tsx` (466 lines, ~0.67x ratio — healthy, unlike `AdminScreen.test.tsx`'s ~7x). Still watch-only; re-check again if it keeps growing.

---

## 6. Action Plan — Current Open Items (#1a, #2 remainder)

(#3–#6's action plans are complete and archived in git history on this file — no longer repeated here now that they're resolved, per this report's own "don't repeat settled ground" convention.)

### #1a: Extract `XGPathGameModule.cs`'s eligibility pipeline

**Fix:** `docs/backlog.md` Epic 17 S-154 — extract `IPathEligibilityService`/`PathEligibilityService` (candidate narrowing, stint sanitization, structural checks, familiarity filtering), composed behind a thinner `XGPathGameModule`, same shape ADR-0068 already proved for `GridGameModule`.

**Verification:** pure refactor, `IGameModule` contract unchanged, REQ-1203's fetch→sanitize→eligibility-check ordering invariant preserved with its own load-bearing comment block intact. New ADR (same "could reasonably have gone another way" bar as ADR-0068).

### #2 (remainder): finish `AdminScreen.tsx` subcomponent test coverage

**Fix:** `docs/backlog.md` Epic 17 S-156 — add dedicated `*.test.tsx` files for `GuestClearSection.tsx`/`RoundControlSection.tsx`/`UnverifiedDataSection.tsx`/`UserDeletionSection.tsx`, trimming now-redundant cases out of `AdminScreen.test.tsx`.

**Verification:** full `npm run test` suite passes with equal or greater total assertion count; no behavior change.

---

## Appendix: What Was Not Assessed

- **`dotnet build` / `dotnet test`:** still not runnable in this environment — confirmed again this pass (`dotnet: command not found`). `npm run test`/`tsc -b`/`oxlint` **were** run live this pass (fresh `npm install`, 584/584 tests passing, zero lint findings) — a change from prior revisions, which noted `npm run test` as also not runnable; `node_modules/` simply wasn't installed yet at session start.
- **A full re-run of the original custom SLOC/comment-splitting Python scanner:** not re-executed this pass (tool isn't checked into the repo); §2 uses a plain `wc -l` figure instead and says so explicitly rather than presenting it as directly comparable to the prior revision's SLOC/comment-ratio numbers.
- **True cyclomatic/cognitive complexity numbers:** still no AST-based tool available. Findings in this report remain directional, cross-checked by hand (direct file reads) wherever feasible, not exact.
- **Temporal coupling and code-age/decay over a 6–12 month window:** still not meaningfully possible — the repository is under 4 weeks old in total as of this revision.
