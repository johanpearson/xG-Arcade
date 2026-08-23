# xG Arcade — Codebase Analysis

**Scope:** `backend/`, `frontend/`, `infra/`, `docs/`
**Method:** line counting (`wc -l`; the original custom SLOC/comment-splitting Python scanner isn't checked into the repo and wasn't re-derived this pass — see §2/Appendix), `git log` churn analysis, targeted `grep`/manual review for security and duplication patterns, `npm audit` for frontend dependencies. `dotnet` CLI is still not available in this environment (confirmed again in the 2026-08-18 revision), so no backend build/`dotnet test` and no NuGet vulnerability audit were run directly — package-version and behavioral claims for already-merged backend work are taken from the merging PRs' own CI-gated results, not re-executed here. Frontend tooling (`npm run test`/`tsc -b`/`oxlint`) **was** run live in the 2026-08-18 revision after a fresh `npm install` (`node_modules/` isn't checked in) — a change from earlier revisions, which had neither installed. Everything reported as "verified in this pass"/"re-verified" **was** re-run or re-read live against current `main` in that revision, not assumed from prior revisions' findings.

**Revision history:**
- **2026-08-10** — original analysis, top 5 targets. Flagged `WikidataClient.cs` duplication, a high-severity `undici` transitive dependency, `Program.cs` composition-root sprawl, `AdminScreen.tsx`'s God Component, and `GridGameModule.cs`'s nesting → became Epic 7 (`docs/backlog.md`, S-099–S-105).
- **2026-08-11 (first update)** — re-verified 6 of 7 Epic 7 stories merged; flagged S-104 as not started.
- **2026-08-11 (second update)** — S-104 merged; Epic 7 fully complete (7/7). Fresh sweep found 5 next-batch targets.
- **2026-08-11 (third update)** — extended the sweep from top 5 to **top 10**, now that security and the original hotspots are settled ground. Two candidates were investigated and explicitly **cleared** rather than reported — see the note at the end of §1 — to keep this list honest rather than padded. Findings #1–#6 became `docs/backlog.md` Epic 9 (S-115–S-129, via `code-health-auditor`).
- **2026-08-18 (this revision, `code-health-auditor` periodic sweep)** — re-verified all 10 prior findings against current code/`git log`, not against `docs/backlog.md`'s notes alone. **#1, #3, #4, #5, #6 fully resolved** (`PlayerStoreRepository` split/ADR-0067, `Program.cs`/`CompositionRoot` docs synced, `frontend/src/lib/api.ts` split by domain, `CliVerbDispatcher.cs` restructured to a verb registry, `CompositionRoot` testing strategy decided and documented). **#2 half-resolved** (5 of 9 `AdminScreen.tsx` subcomponents now have dedicated tests; 4 remain indirect-only). #7–#9 (watch-only) unchanged in status; #8 was already marked superseded in the prior revision (S-121) and stays that way. #10 (`SuggestionsScreen.tsx`) re-inspected after growing to 697 lines (from 645) and still found proportionate — remains watch-only. One genuinely new finding from this sweep: `XGPathGameModule.cs` (backend/src/XGArcade.Games.XGPath), flagged as a **P2, rising** — see §1a below. Full numeric detail for every module/component is `CODE_HEALTH_ASSESSMENT.md`'s job, not repeated here; this document stays focused on the priority-ranked list. New work tracked as `docs/backlog.md` Epic 17.
- **2026-08-22 (this revision, `code-health-auditor` periodic sweep)** — verified all 5 Epic 17 stories (S-154–S-158) against `git log`/current code before scoring anything, per this report's own step-0 discipline: all 5 confirmed merged (`IPathEligibilityService`/`PathEligibilityService` split + ADR-0082, `SparqlQueryBuilders`/`SparqlResponseParsers` split, all 4 remaining `AdminScreen.tsx` subcomponents gained dedicated tests, `AdminScreen.tsx` migrated onto `useAuthedFetch`, `App.tsx`'s session lifecycle extracted into `useSession()`). Epic 17 is now fully closed — **#1a and #2 (this document's own last two open items) are both resolved.** One doc-drift found and fixed directly, not re-proposed as new work: S-154's own `docs/backlog.md` entry was missing its "Built as" note despite the extraction, ADR, and doc-sync all having genuinely shipped (confirmed via `git log`, ADR-0082, and the existing 2026-08-22 CHANGELOG entry) — backfilled from those sources rather than re-investigated from scratch. One genuinely new finding this sweep: `PlayerCareerPrefetchService.cs`'s country/club sweep loops (near-identical ~90-line shape, the same "duplicated block repeated per near-identical case" pattern this report flagged in `WikidataClient.cs` back in 2026-08-10) — see §1a below (replaces the now-resolved xG Path finding). `npm audit` re-run fresh: `nanoid@<3.3.18` advisory (dev-only, first seen 2026-08-18) is unchanged, still Dependabot's lane. No new security finding. New work tracked as `docs/backlog.md` Epic 21.
- **2026-08-23 (this revision, `code-health-auditor` periodic sweep, deliberately widened scope)** — the requester explicitly asked for a wider net than the single-finding-per-sweep cadence Epics 17/21 used: every module still below ~9.0 in `CODE_HEALTH_ASSESSMENT.md`'s 2026-08-22 revision was re-read, and backend/frontend/infra were each searched past the #1 hotspot for the same patterns this report has already caught before, not just re-verified at the top. S-165 (`PlayerCareerPrefetchService.cs`, #1a above) re-confirmed still accurate against current code/`git log` — unchanged, still open, not re-investigated. **Four genuinely new findings**, all the same "duplicated shape repeated per near-identical block" pattern at different sites/granularities — see #11-#14 below. `npm run test` (647/647, 44 files), `tsc -b`, `oxlint` all re-ran live and clean (existing `node_modules/`, no reinstall needed); `npm audit` unchanged (`nanoid@<3.3.18`, dev-only). No `dotnet` SDK, confirmed again — no backend code changed this pass (findings/planning only). New work tracked as `docs/backlog.md` Epic 22 (S-166–S-169).

---

## 0. Epic 7 Closeout (unchanged from the last revision)

All 7 stories (S-099–S-105) are merged and verified: `undici` fixed, `WikidataClient.cs` deduplicated (2,034→1,815 lines), `Program.cs` decomposed (1,245→29 lines), `AdminScreen.tsx` extracted (1,432→190 lines), `GridGameModule.cs` nesting flattened (25→3 deep-indent lines), CSS/TS comment dedup done. `npm audit`, CORS, SQL/SPARQL-injection guards, and secret scans remain clean, re-verified again in this pass. **No security or P1 findings anywhere in this revision.**

---

## 1. Executive Summary & Top 10 Priorities

**Overall health: Good, still improving; this pass traded depth for breadth.** Epic 17/Epic 21 are both settled ground (Epic 17 fully closed 2026-08-22; Epic 21's one finding, S-165, re-confirmed still open and unchanged, not re-investigated). **Items 1-6 below remain archival** (resolved 2026-08-18 or earlier); #7-#10 remain watch-only, unchanged. This pass's own brief was explicit: don't just re-verify the single top finding, look past it — every module still below ~9.0 in `CODE_HEALTH_ASSESSMENT.md` was re-read, and backend/frontend/infra were each searched for the same duplicated-block/god-file/weak-coverage patterns this lineage already knows to look for. **Four new findings surfaced — #11-#14 below** — all instances of the same "duplicated shape repeated per near-identical block" pattern this report has now caught six times across four sweeps, including, for the first time, two frontend instances at once.

| # | Target | Priority | Status | One-line why |
| :-- | :--- | :--- | :--- | :--- |
| 1 | `PlayerStoreRepository.cs` / `IPlayerStoreRepository.cs` | P2 | **✅ Resolved (S-106/S-107, ADR-0067)** | Split into 8 independently-registered narrow repositories; confirmed no facade reintroduced, no repository re-exceeds the original's method count |
| 2 | `AdminScreen.tsx`'s 9 extracted components have zero dedicated tests | P2 | **✅ Resolved (S-108/S-156)** | All 9 now have their own `*.test.tsx`; `AdminScreen.test.tsx` trimmed of the now-redundant per-subcomponent cases, keeps only composition/wiring coverage |
| 3 | `architecture-document.md`/`implementation-document.md` still describe `Program.cs` as holding DI/auth/CLI-verb/endpoint-mapping logic | P2 | **✅ Resolved (S-110)** | Both docs now reference `CompositionRoot/*.cs` directly; confirmed via grep |
| 4 | `frontend/src/lib/api.ts` (1,057 lines, 51 exports) | P3 | **✅ Resolved (S-111)** | Split by domain into `admin.ts`/`auth.ts`/`leaderboard.ts`/`rounds.ts`/`turnstile.ts`/`announcements.ts`/`useAuthedFetch.ts`/etc.; largest remaining `lib/` file is `types.ts` (646 lines, mostly type declarations, not a complexity concern) |
| 5 | `CliVerbDispatcher.cs` (649 lines, one single method) | P3 | **✅ Resolved (S-112)** | Restructured to a 14-verb registry (`Verbs` dictionary + one handler method each); unchanged since 2026-08-18 (confirmed via `git log --since`), still no duplication |
| 6 | `CompositionRoot/*.cs` has no dedicated unit tests | P3 | **✅ Resolved (S-113)** | Decision made and documented: `AuthSetupTests.cs` added for the one branch worth isolating; `docs/coding-guidelines.md` §"Composition-root testing" records the rest as deliberately integration-tested |
| 7 | Large test files continuing to grow (`WikidataClientTests.cs` still 3,973 lines, `GridGameModuleTests.cs` coverage across 4 files totaling 2,792, `AuthEndpointTests.cs` 2,095) | P4 — watch | No change in status | Growing for the right reasons (real regression coverage); navigability-only concern |
| 8 | `LeaderboardScreen.tsx` | P4 — watch | **Superseded 2026-08-16 (S-121)**, confirmed still split this pass | Split into 4 scope components + shared hook; see §5 |
| 9 | `AuthController.cs` (773 lines, churn 1) | P4 — watch | Unchanged | Same reasoning as before — still churn-1, still low complexity |
| 10 | `SuggestionsScreen.tsx` (697 lines, unchanged since 2026-08-18) | P4 — watch | Re-confirmed this pass, still watch-only | Still cohesive (one feature, one shared `PlayerReviewPanel`), own dedicated `SuggestionsScreen.test.tsx` at a healthy ~0.67x line ratio — see `CODE_HEALTH_ASSESSMENT.md` §"Watch-only" for the full inspection |

**New this revision:**

### #1a (P2, rising): `PlayerCareerPrefetchService.cs` — the country/club sweep loops have duplicated

`PlayerCareerPrefetchService.cs` (408 lines) is now the highest-churn file in `XGArcade.DataSync` (8 commits since 2026-08-11). S-127 added a club-sweep loop that deliberately mirrors the file's original country-sweep loop (ADR-0069); every subsequent xG-data-quality story since (REQ-110/ADR-0077/ADR-0078/S-159/S-160) touched both loops symmetrically, growing rather than shrinking the duplication each time. The two `foreach` loops in `PrefetchAsync` are ~90 lines each and near-identical in shape (skip-on-null-QID, fetch-with-try/catch-log-continue, mark-swept, skip-empty-pool, batch into `FetchAndPersistBatchAsync`, log running totals) — the same "duplicated block repeated per near-identical case" pattern this report flagged in `WikidataClient.cs` back in 2026-08-10 (Epic 7) and in `GridGameModule.cs`'s multi-concern methods (Epic 9). Not yet critical — each loop's own real behavioral nuance (the club loop's deliberate `club.Name`, not `clubNameByClubQid`, attribute-value sourcing) is already correctly documented inline, and the file is well-tested (563-line `PlayerCareerPrefetchServiceTests.cs`) — but it's the clearest next duplication-risk target by this report's own complexity × churn ranking. **Fix:** extract a shared `SweepPoolAsync`-shaped helper both loops call, preserving every nuance verbatim — `docs/backlog.md` Epic 21 S-165. **Unchanged this pass, still open.**

### #11 (P2): `PlayerCacheWarmingService.cs` — the Country×Club/Club×Club sweep loops have duplicated (same shape as #1a, one directory over)

`PlayerCacheWarmingService.cs` (`backend/src/XGArcade.Games.XGGrid/`, 388 lines, 4 commits) is almost entirely two nested loops in `WarmAsync` that duplicate the exact same 5-branch decision tree end to end: the Country×Club loop (~lines 169-269) and the Club×Club loop (~lines 271-349) each check already-valid, then confirmed-low-from-a-fully-swept-pool (ADR-0078/S-160), then previously-confirmed-low, then persistent-technical-failure, and only then run a live Wikidata lookup and persist the outcome — differing only in which two `AttributeType`/name pairs and which `IWikidataLookupService` method (`LookupAndPersistAsync` vs. `LookupAndPersistClubClubAsync`) are used. This is the *third* occurrence of this specific "country/club-pair sweep" duplication shape in the codebase (after `WikidataClient.cs`'s HTTP handling in 2026-08-10/Epic 7, and #1a/`PlayerCareerPrefetchService.cs` above) — not a coincidence, the same underlying "two categories, sweep every pair" domain shape keeps recurring at different call sites as the codebase grows. Well-tested (775-line `PlayerCacheWarmingServiceTests.cs`). **Fix:** extract a shared `SweepPairsAsync`-shaped helper both loops call, preserving every counter/log line verbatim — `docs/backlog.md` Epic 22 S-166.

### #12 (P2): `CliVerbDispatcher.cs` — 5 of 14 handlers duplicate their own Wikidata-client bootstrap

`CliVerbDispatcher.cs` (769 lines) is the single highest-churn file in the entire repository (13 commits) — both the 2026-08-18 and 2026-08-22 revisions confirmed its *verb-registry* shape (S-112's `Verbs` dictionary + one handler per verb, S-114's shared `BuildDbContext`/`BuildLoggerFactory`) is healthy and unchanged, and that verdict still holds. This is a different, narrower finding one level down, only visible on a full re-read rather than a `git log --since` diff check: 5 of the 14 handlers (`HandleWarmPlayerCacheAsync`, `HandleImportPlayerNameIndexAsync`, `HandleBackfillPlayerPhotosAsync`, `HandleBackfillPlayerPositionBirthYearAsync`, `HandlePrefetchPlayerCareersAsync`) each construct a bare `HttpClient`, call `WikidataHttpClientConfiguration.Configure` on it, then `new WikidataClient(...)` with a `logger:` from the handler's own `BuildLoggerFactory()` — the one piece of per-handler boilerplate S-114's own extraction didn't reach (it only pulled `BuildDbContext`/`BuildLoggerFactory` themselves out, not what callers do with them). **Fix:** extract a shared `BuildWikidataClient(ILoggerFactory, TimeSpan? queryTimeout)` helper mirroring `BuildDbContext`/`BuildLoggerFactory`'s own shape — `docs/backlog.md` Epic 22 S-167.

### #13 (P3): `frontend/src/lib/*.ts` — 47 call sites duplicate the same fetch+error-check+parse shape

Every domain file under `frontend/src/lib/` that talks to the backend (`admin.ts` 18 call sites, `auth.ts` 9, `announcements.ts` 5, `leaderboard.ts` 5, `rounds.ts` 4, `leagues.ts` 3, `incidents.ts` 2, `path.ts` 1 — 47 total across 8 files) hand-rolls the same shape at every call site: build a headers object, call `fetch(...)`, check `!response.ok` and call the already-shared `throwApiError` if so, then parse the JSON body. `apiClient.ts`'s own header comment frames itself as "the shared fetch foundation every domain file imports from," but today it only centralizes the *error*-handling half — the *request*-building half is still repeated 47 times by hand. Never flagged before this pass because no single file in `lib/` was ever large enough to be the outlier on its own (S-111, Epic 8, already split the original monolithic `api.ts` by domain) — the duplication is only visible aggregated across the whole directory, which this pass's wider net specifically looked for. Deliberately distinct from `useAuthedFetch.ts` (S-107/Epic 17), which is React-hook-scoped and owns loading/error *state* — these are plain async functions callable from anywhere, so the right fix is a small `apiRequest<T>`-shaped function, not a hook. **Fix:** add a shared request-building helper to `apiClient.ts` — `docs/backlog.md` Epic 22 S-168.

### #14 (P3): `GridScreen.tsx`/`PathScreen.tsx` duplicate their own round-fetch/load-state machinery

`GridScreen.tsx` (529 lines) and `PathScreen.tsx` (357 lines), 5 commits each, independently define an identically-shaped `LoadState` union, an identical round-fetch mount effect (fetch, guard on a `cancelled` flag, treat a 401 as `onAuthError()`, otherwise set an error state), an identical fire-and-forget `warmUpAutocomplete` mount effect, and a `handleViewCompletedRoundLeaderboard` that `PathScreen.tsx`'s own comment states outright "mirrors GridScreen.tsx's `handleViewCompletedRoundLeaderboard` exactly." Not previously flagged because Epic 17's frontend pass was `AdminScreen.tsx`/`App.tsx`-scoped, and these two screens' own `useAuthedFetch.ts` evaluation (correctly rejecting the hook because both screens need to mutate fetched state after a guess submission) asked a different question than "do these two screens duplicate each other" — they do, just not via `useAuthedFetch.ts`. **Fix:** extract a shared `useRoundFetch`-shaped hook, generic over the round/path response type, covering the `LoadState`/fetch-effect/warm-up piece at minimum — `docs/backlog.md` Epic 22 S-169.

**Cleared, not reported (checked and found to be fine):**
- **Grid vs. Path frontend "duplication"** (`GuessInput.tsx`/`PathGuessInput.tsx`, `ScoringExplainer.tsx`/`PathScoringExplainer.tsx`): these looked like copy-paste candidates by file-pair naming, but inspection found substantial real behavioral differences (different attempt/clue models, no shared "uniqueness" concept in xG Path) *and*, in `PathScoringExplainer.tsx`'s own comments, an explicit, reasoned decision already on record for why a shared abstraction was rejected — right down to citing this repo's own "three similar lines is better than a premature abstraction" convention. Re-litigating a decision that was already made deliberately and correctly isn't a finding.
- **Two separate `FakeWikidataClient.cs` test doubles** (`XGArcade.DataSync.Tests`, `XGArcade.Games.XGGrid.Tests`): looked like duplicated test infrastructure at a glance. On inspection, each is independently and narrowly scoped to what its own test project's system-under-test actually calls (339 lines with full intersection/photo-batch stubbing vs. 125 lines stubbing almost nothing, since `GridGameModule` only calls one method on the interface) — this is the repo's documented "don't over-mock" convention working correctly, not lazy duplication.
- **Design-token discipline**: grepped for hardcoded hex colors outside `index.css`'s own token definitions — zero found. Still clean.
- **Dead/unused API exports**: checked every `frontend/src/lib/api.ts` export for at least one call site elsewhere in the frontend — zero unused.
- A nesting false-positive on `AdminSuggestionEndpoints.cs` surfaced by this report's own line-indentation heuristic turned out to be multi-line method-call argument lists (record construction, multi-parameter logger calls), not real control-flow nesting — worth naming since it's a limitation of the heuristic used throughout this report, not a finding about that file.
- **`backend/src/XGArcade.DataSync/Wikidata/WikidataLookupService.cs`** (406 lines) — re-inspected this pass given its role as `PlayerCacheWarmingService.cs`'s (#11) own dependency. Already properly deduplicated via its own shared `PersistMatchesAsync`/`QueueAttribute`/`PersistCareerStintsAsync` helpers, explicitly documented as shared by all four of its `LookupAndPersist*Async` callers — no per-call-site repetition the way #11's file has. Not a finding.
- **`backend/src/XGArcade.Api/CompositionRoot/ServiceRegistration.cs`** (308 lines, 5 commits) — read in full this pass (not just measured). A growing but cohesive DI registration list — one line/block per service or option, each with its own doc-comment citing the REQ/ADR/COMP it backs, zero duplicated registration shape. Not a finding.
- **`backend/src/XGArcade.Api/Path/PathEndpoints.cs`** (324 lines, 6 commits — this module's highest-churn file) — read in full this pass. One endpoint, heavily and specifically documented (mirrors `RoundEndpoints.cs`'s established shape per ADR-0016/ADR-0048), no duplication. Not a finding.
- **`infra/scripts/sync-prod-to-dev.sh`/`promote-dev-to-prod.sh`** (83/85 lines) — read past structure for the first time this pass (both prior revisions noted "no deep review performed beyond structure"). Genuinely near-duplicate outside their shared `lib/game-data-tables.sh` allowlist (same dry-run/confirm/dump/restore sequence, differing only in source-vs-target direction and the confirmation phrase), but deliberately **not** written up as a finding: both scripts' own header comments frame the confirmation-phrase asymmetry as a deliberate safety property (a stronger phrase for the prod-writing direction), and unifying them would trade that per-script hard-coded safety for a single shared code path where a flag-typo mistake could point the wrong way — a genuine design call, not a mechanical win, and low churn (1 commit each). See `docs/backlog.md` Epic 22's watch-only list.

---

## 2. Codebase Size & Comment Hygiene

Backend + frontend source (excluding EF Core migrations, `bin`/`obj`, `node_modules`): **85,333 raw lines** across `.cs`/`.ts`/`.tsx` (production + tests), up from the prior revision's SLOC-only 72,172 figure — not directly comparable line-for-line since this pass used a plain `wc -l` scan rather than re-running the prior revision's custom SLOC/comment-splitting Python scanner (that tool itself isn't checked into the repo; re-deriving it was judged not worth the time this pass since no new bloat/hygiene finding emerged from the targeted deep-reads that *were* done — see §4). `WikidataClientTests.cs` (3,973 lines) is now the single largest file in the repo including tests, up from `WikidataClient.cs` itself at the last revision. No new under-documented or noise-comment finding surfaced by this pass's targeted reads (`XGPathGameModule.cs`, `CliVerbDispatcher.cs`, `SuggestionsScreen.tsx`, `architecture-document.md`).

`IWikidataClient.cs` (595 lines, comment-to-code ratio unchanged in shape from the last revision) remains this codebase's established, deliberate documentation style, not bloat — same conclusion as before, re-confirmed by spot-check rather than re-flagged.

---

## 3. Security Findings

Re-verified fresh this pass (fresh `npm install`, since `node_modules/` isn't checked in): `npm audit --omit=dev` **0 vulnerabilities** (production dependencies clean). Full `npm audit` (including dev dependencies) still surfaces the one high-severity finding first seen 2026-08-18: `nanoid@<3.3.18` (transitive, via `vite`) — GHSA-2v37-7h3g-55p8, "custom generators can loop indefinitely when size is zero." Unchanged this pass — still dev-tooling-only (never bundled into the shipped frontend), still no direct application call site, still Dependabot's routine-drift lane per `CLAUDE.md`, not fixed directly here. No hardcoded secrets (re-grepped); no `FromSqlRaw`/`ExecuteSqlRaw`; `WikidataQid.IsValid` guard still centralized; CORS still an explicit allow-list; no `eval()`/`innerHTML`/`dangerouslySetInnerHTML` in production code. NuGet package versions not re-verified this pass (no `dotnet` SDK in this sandbox, confirmed again — `dotnet: command not found`). **No P1 security findings.**

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

### #2: `AdminScreen.tsx`'s extracted components — test coverage, now fully resolved (S-108/S-156)

The remaining 4 components (`GuestClearSection.tsx`, `RoundControlSection.tsx`, `UnverifiedDataSection.tsx`, `UserDeletionSection.tsx`) gained dedicated test files this cycle, closing out the last of the original 9; `AdminScreen.test.tsx` was trimmed of the now-redundant per-subcomponent cases (kept only composition/wiring coverage — fetch-on-mount, real `onRefresh` round-tripping, activeRound-gated show/hide). Confirmed via direct file listing and a live `npm run test` pass this revision (647/647 passing).

### #1a (new this revision): `PlayerCareerPrefetchService.cs` — country/club sweep-loop duplication

Full write-up is in §1 above (under "New this revision") rather than repeated here, to avoid narrating the same finding twice in one document. **Fix:** `docs/backlog.md` Epic 21 S-165 (extract a shared `SweepPoolAsync`-shaped helper, preserving each loop's own real behavioral nuance).

The prior revision's #1a (`XGPathGameModule.cs`'s eligibility pipeline) is now fully resolved — see the 2026-08-22 revision-history entry above and ADR-0082.

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

## 6. Action Plan — Current Open Items (#1a, #11–#14)

(Every prior action plan — #1–#6 and Epic 17's #1a/#2 — is complete and archived in git history on this file, no longer repeated here now that it's resolved, per this report's own "don't repeat settled ground" convention.)

### #1a: Extract `PlayerCareerPrefetchService.cs`'s shared country/club sweep shape

**Fix:** `docs/backlog.md` Epic 21 S-165 — extract a shared `SweepPoolAsync`-shaped helper (delegate/record-parameterized) that both the country and club loops in `PrefetchAsync` call, preserving each loop's own real behavioral nuance verbatim (in particular the club loop's deliberate `club.Name`, not `clubNameByClubQid`, attribute-value sourcing).

**Verification:** pure refactor, `PlayerCareerPrefetchServiceTests.cs` passes unchanged including the nuance-specific cases; net line-count reduction reported in the PR description. No `dotnet` SDK in this sandbox — implement and verify in a session with real `dotnet test` access.

### #11: Extract `PlayerCacheWarmingService.cs`'s shared Country×Club/Club×Club sweep shape

**Fix:** `docs/backlog.md` Epic 22 S-166 — extract a shared `SweepPairsAsync`-shaped helper that both loops in `WarmAsync` call, preserving every counter (`pairsQueriedLive`/`pairsAlreadyValid`/etc.) and log line verbatim.

**Verification:** pure refactor, `PlayerCacheWarmingServiceTests.cs` (775 lines) passes unchanged. No `dotnet` SDK in this sandbox — implement and verify in a session with real `dotnet test` access.

### #12: Extract `CliVerbDispatcher.cs`'s shared Wikidata-client bootstrap

**Fix:** `docs/backlog.md` Epic 22 S-167 — extract a `BuildWikidataClient(ILoggerFactory, TimeSpan?)` helper (mirroring `BuildDbContext`/`BuildLoggerFactory`'s existing shape) that the 5 affected handlers call instead of constructing the `HttpClient`/`WikidataClient` pair by hand.

**Verification:** no dedicated `CliVerbDispatcherTests.cs` exists (deliberate, S-113) — verify via a hand-traced read confirming each of the 5 call sites' exact prior arguments (including the two `queryTimeout: 60s` overrides) are reproduced, plus the rest of the backend suite passing unchanged. No `dotnet` SDK in this sandbox.

### #13: Extract a shared authenticated-fetch helper for `frontend/src/lib/*.ts`

**Fix:** `docs/backlog.md` Epic 22 S-168 — add an `apiRequest<T>`-shaped helper to `apiClient.ts` that the 47 call sites across `admin.ts`/`auth.ts`/`announcements.ts`/`leaderboard.ts`/`rounds.ts`/`leagues.ts`/`incidents.ts`/`path.ts` call instead of hand-rolling `fetch`+headers+`ok`-check+`throwApiError`+`json()` each time — preserving each call site's own status-code special-casing (several treat a bare 404 as data, not an error).

**Verification:** `npm run test` (647/647), `tsc -b`, `oxlint` all pass unchanged — runnable and verifiable in this sandbox, unlike #1a/#11/#12 above.

### #14: Extract a shared round-fetch/load-state hook for `GridScreen.tsx`/`PathScreen.tsx`

**Fix:** `docs/backlog.md` Epic 22 S-169 — extract a `useRoundFetch`-shaped hook covering the duplicated `LoadState` union, fetch-mount-effect, and autocomplete warm-up effect; leave each screen's own guess-submission/mutation logic untouched.

**Verification:** `npm run test` (647/647, including `GridScreen.test.tsx`/`PathScreen.test.tsx` unchanged), `tsc -b`, `oxlint` all pass unchanged — runnable and verifiable in this sandbox.

---

## Appendix: What Was Not Assessed

- **`dotnet build` / `dotnet test`:** still not runnable in this environment — confirmed again this pass (`dotnet: command not found`). `npm run test`/`tsc -b`/`oxlint` **were** run live this pass (existing `node_modules/`, no reinstall needed — 647/647 tests passing across 44 files, `tsc -b` clean, zero `oxlint` findings).
- **A full re-run of the original custom SLOC/comment-splitting Python scanner:** not re-executed this pass (tool isn't checked into the repo); §2 uses a plain `wc -l` figure instead and says so explicitly rather than presenting it as directly comparable to the prior revision's SLOC/comment-ratio numbers.
- **True cyclomatic/cognitive complexity numbers:** still no AST-based tool available. Findings in this report remain directional, cross-checked by hand (direct file reads) wherever feasible, not exact.
- **Temporal coupling and code-age/decay over a 6–12 month window:** still not meaningfully possible — the repository is under 4 weeks old in total as of this revision.
