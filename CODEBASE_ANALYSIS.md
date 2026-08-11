# xG Arcade — Codebase Analysis

**Scope:** `backend/`, `frontend/`, `infra/`, `docs/`
**Method:** static line/comment counting (custom Python scanner — `cloc`/`tokei` not available in this environment), `git log` churn analysis, targeted `grep`/manual review for security and duplication patterns, `npm audit` for frontend dependencies. `dotnet` CLI and frontend `node_modules` are not available in this environment, so no build, no `dotnet test`/`npm run test`, and no NuGet vulnerability audit were run directly — package-version and behavioral claims for already-merged work are taken from the merging PRs' own CI-gated results, not re-executed here. Everything reported as "verified in this pass" (SLOC, churn, `npm audit`, grep-based security/duplication checks, method-count/nesting inspections) **was** re-run live against current `main`.

**Revision history:**
- **2026-08-10** — original analysis, top 5 targets. Flagged `WikidataClient.cs` duplication, a high-severity `undici` transitive dependency, `Program.cs` composition-root sprawl, `AdminScreen.tsx`'s God Component, and `GridGameModule.cs`'s nesting → became Epic 7 (`docs/backlog.md`, S-099–S-105).
- **2026-08-11 (first update)** — re-verified 6 of 7 Epic 7 stories merged; flagged S-104 as not started.
- **2026-08-11 (second update)** — S-104 merged; Epic 7 fully complete (7/7). Fresh sweep found 5 next-batch targets.
- **2026-08-11 (this revision)** — extended the sweep from top 5 to **top 10**, now that security and the original hotspots are settled ground. Two candidates were investigated and explicitly **cleared** rather than reported — see the note at the end of §1 — to keep this list honest rather than padded.

---

## 0. Epic 7 Closeout (unchanged from the last revision)

All 7 stories (S-099–S-105) are merged and verified: `undici` fixed, `WikidataClient.cs` deduplicated (2,034→1,815 lines), `Program.cs` decomposed (1,245→29 lines), `AdminScreen.tsx` extracted (1,432→190 lines), `GridGameModule.cs` nesting flattened (25→3 deep-indent lines), CSS/TS comment dedup done. `npm audit`, CORS, SQL/SPARQL-injection guards, and secret scans remain clean, re-verified again in this pass. **No security or P1 findings anywhere in this revision.**

---

## 1. Executive Summary & Top 10 Priorities

**Overall health: Good.** With every original hotspot resolved and security clean, this revision's findings are second- and third-tier maintainability items by nature — real, but none of them P1. They fall into three groups: two genuine structural gaps (§#1–2), a docs-drift item unique to this revision (§#3), and a tail of real-but-lower-urgency items included because you asked for depth now that the bigger fish are handled.

| # | Target | Priority | One-line why |
| :-- | :--- | :--- | :--- |
| 1 | `PlayerStoreRepository.cs` / `IPlayerStoreRepository.cs` | **P2** | 44 methods spanning 9 unrelated sub-entity concerns in one 772/482-line file/interface — confirmed the outlier by comparing method counts across *every* repository in the codebase (next-highest is 16) |
| 2 | `AdminScreen.tsx`'s 9 extracted components have zero dedicated tests | **P2** | S-103's correctly-scoped "pure mechanical extraction" left all coverage in one unsplit `AdminScreen.test.tsx` |
| 3 | `architecture-document.md`/`implementation-document.md` still describe `Program.cs` as holding DI/auth/CLI-verb/endpoint-mapping logic | **P2** | That logic moved to `CompositionRoot/*.cs` in S-102 (2026-08-11) and the docs were never updated — a factually wrong statement in two governing docs, not a code issue |
| 4 | `frontend/src/lib/api.ts` (1,057 lines, 51 exports) | P3 | Flagged P3 in the *original* 2026-08-10 report, never one of Epic 7's 7 stories, still the largest unaddressed frontend file |
| 5 | `CliVerbDispatcher.cs` (649 lines, one single method) | P3 | S-102 moved the CLI-verb logic out of `Program.cs` but didn't restructure it — `TryHandleAsync` is 649 lines of sequential verb handling in one method |
| 6 | `CompositionRoot/*.cs` has no dedicated unit tests | P3 | Only indirect coverage via `XGArcade.Api.Tests`' `WebApplicationFactory` integration suite — may be a deliberate, correct choice (composition-root code is often better integration-tested), but it hasn't been *decided*, just defaulted into |
| 7 | Large test files continuing to grow (`WikidataClientTests.cs` now 3,463 lines, `GridGameModuleTests.cs` 2,474, `AuthEndpointTests.cs` 2,095) | P4 — watch | Growing for the right reasons (real regression coverage); navigability-only concern |
| 8 | `LeaderboardScreen.tsx` (1,130 lines, 6 `useEffect`, low churn) | P4 — watch | Large but stable; per this report's own doctrine, not an action item until something else touches it |
| 9 | `AuthController.cs` (773 lines, churn 1) | P4 — watch | Same reasoning as #8 |
| 10 | `SuggestionsScreen.tsx` (645 lines, 8 `useState`, 2 `useEffect`) | P4 — watch | Already has its own dedicated test file (unlike the `AdminScreen.tsx` components at #2) and is proportionate today; flagged only so it isn't the next surprise God Component |

**Cleared, not reported (checked and found to be fine):**
- **Grid vs. Path frontend "duplication"** (`GuessInput.tsx`/`PathGuessInput.tsx`, `ScoringExplainer.tsx`/`PathScoringExplainer.tsx`): these looked like copy-paste candidates by file-pair naming, but inspection found substantial real behavioral differences (different attempt/clue models, no shared "uniqueness" concept in xG Path) *and*, in `PathScoringExplainer.tsx`'s own comments, an explicit, reasoned decision already on record for why a shared abstraction was rejected — right down to citing this repo's own "three similar lines is better than a premature abstraction" convention. Re-litigating a decision that was already made deliberately and correctly isn't a finding.
- **Two separate `FakeWikidataClient.cs` test doubles** (`XGArcade.DataSync.Tests`, `XGArcade.Games.XGGrid.Tests`): looked like duplicated test infrastructure at a glance. On inspection, each is independently and narrowly scoped to what its own test project's system-under-test actually calls (339 lines with full intersection/photo-batch stubbing vs. 125 lines stubbing almost nothing, since `GridGameModule` only calls one method on the interface) — this is the repo's documented "don't over-mock" convention working correctly, not lazy duplication.
- **Design-token discipline**: grepped for hardcoded hex colors outside `index.css`'s own token definitions — zero found. Still clean.
- **Dead/unused API exports**: checked every `frontend/src/lib/api.ts` export for at least one call site elsewhere in the frontend — zero unused.
- A nesting false-positive on `AdminSuggestionEndpoints.cs` surfaced by this report's own line-indentation heuristic turned out to be multi-line method-call argument lists (record construction, multi-parameter logger calls), not real control-flow nesting — worth naming since it's a limitation of the heuristic used throughout this report, not a finding about that file.

---

## 2. Codebase Size & Comment Hygiene

Unchanged in shape from the last revision (S-104 added modest line count without moving these numbers materially): **72,172 SLOC, 21,570 comment lines, 29.9% overall ratio**, backend tests ~2.3× production code, frontend tests ~1.5×. No new under-documented or noise-comment finding in this pass. Full breakdown in the previous revision (see git history on this file) — omitted here to keep this revision focused on the new findings, per your steer that ground already covered doesn't need repeating at length.

One data point worth naming rather than flagging: `IWikidataClient.cs` is 563 lines but only 92 are code — 454 are comments (a per-method rationale paragraph for each of the 9 query methods, mirroring the style `WikidataClient.cs` itself used before its refactor). Consistent with this codebase's established, deliberate documentation style (see prior revisions' analysis of `types.ts`/`Grid.css`) — not a new instance of bloat, just noted for completeness since it's the single highest-ratio file found in this pass.

---

## 3. Security Findings

Re-verified fresh again in this pass: `npm audit` (full and `--omit=dev`) **0 vulnerabilities**; no hardcoded secrets; no `FromSqlRaw`/`ExecuteSqlRaw`; `WikidataQid.IsValid` guard still centralized and present; CORS still an explicit allow-list; no `eval()`/`innerHTML`/`dangerouslySetInnerHTML` in production code; NuGet packages unchanged, still current .NET 10. **No P1 security findings.** This section stays short because there is nothing new to say — see §0/§3 of the prior revision for the full original sweep.

---

## 4. Priority Detail — New Findings

### #1 (P2): `PlayerStoreRepository.cs` — repository spanning too many concerns

Confirmed as a genuine outlier, not just a "large file," by comparing method counts across **every** repository in `backend/src/XGArcade.Data/Repositories/`:

| Repository | Methods | Lines |
| :--- | ---: | ---: |
| **`PlayerStoreRepository.cs`** | **44** | **772** |
| `UserRepository.cs` | 16 | 161 |
| `GuessRepository.cs` | 11 | 146 |
| `PathInstanceRepository.cs` | 11 | 145 |
| `LeagueRepository.cs` | 9 | 97 |
| `RoundRepository.cs` | 8 | 80 |
| *(6 more, all ≤6 methods)* | | |

The 44 methods span at least 9 distinct sub-entity concerns: `Player` CRUD, `PlayerData` (unverified/approve/remove), `PlayerAttribute`, `PlayerAlias`, `PlayerOverride`, photo backfill, position/birth-year backfill, `PlayerCareerStint`, and confirmed-low/technical-failure tracking. Zero deep-nesting — this isn't a complexity problem, it's a scope problem: an unrelated change to career-stint merge logic and a change to override management both touch the same file and its 482-line interface.

### #2 (P2): `AdminScreen.tsx`'s 9 extracted components — test coverage didn't follow the code split

S-103 correctly scoped itself to a pure, behavior-preserving extraction and correctly left `AdminScreen.test.tsx` untouched as its regression net. Confirmed in this pass: `frontend/src/admin/*.test.tsx` contains exactly two files — `AdminScreen.test.tsx` and `SuggestionsScreen.test.tsx` — for what is now 10+ implementation files. `PlayerSuggestionsEntry.tsx`, `IncidentReportsEntry.tsx`, `AnnouncementBannerSection.tsx`, `UnverifiedDataSection.tsx`, `AccountMetricsSection.tsx`, `GuestClearSection.tsx`, `XGPathCycleSection.tsx`, `RoundControlSection.tsx`, `UserDeletionSection.tsx`, and `useAdminSectionFetch.ts` all have zero dedicated tests.

### #3 (P2, new this revision): governing docs still describe the pre-S-102 shape of `Program.cs`

`docs/implementation-document.md` §4's folder-structure block still reads `/XGArcade.Api -> Controllers, DTOs, Program.cs`, and both `docs/architecture-document.md` and `docs/implementation-document.md` describe auth wiring, admin authorization, and scoring-strategy registration as happening "in `Program.cs`" in several places. Since S-102 (2026-08-11), `Program.cs` is a 29-line entry point and that logic lives in `backend/src/XGArcade.Api/CompositionRoot/{AuthSetup,CliVerbDispatcher,EndpointMapping,ServiceRegistration,WikidataHttpClientConfiguration}.cs`. Neither doc mentions `CompositionRoot` at all (confirmed via grep — zero hits). This is the first doc-drift finding in any revision of this report — worth naming precisely because `CLAUDE.md`'s own workflow treats "behavior changed, doc didn't" as a signal to double back, and S-102 was scoped as "no behavior change" so it's an easy one for the normal per-story doc-sync check to have waved through.

### #4 (P3): `frontend/src/lib/api.ts` — carried over, unaddressed

Unchanged assessment from the original report: 1,057 lines, 51 exports, ~47 similarly-shaped fetch-wrapper functions (average ~20 lines each) — breadth-driven size, not complexity (each function does something genuinely different). Never one of Epic 7's 7 stories. Now the largest hand-written frontend file with no plan against it.

### #5 (P3, new this revision): `CliVerbDispatcher.cs` — one 649-line method

A direct, unrestructured byproduct of S-102: the CLI-verb dispatch logic moved out of `Program.cs` into its own file, but is still a single `public static async Task<bool> TryHandleAsync(string[] args)` handling every verb (`--all-clubs` and its siblings — there are 10+ CLI-triggered workflows in `.github/workflows/` alone) sequentially in one method body. Confirmed via direct inspection: only one method declaration in the whole 649-line file.

### #6 (P3, new this revision): `CompositionRoot/*.cs` has no dedicated unit tests

Confirmed via search: no `AuthSetupTests.cs`, `ServiceRegistrationTests.cs`, `EndpointMappingTests.cs`, or `CliVerbDispatcherTests.cs` exist anywhere in `backend/tests/`. Coverage is entirely indirect, through `XGArcade.Api.Tests`' `WebApplicationFactory`-based integration suite. This may well be the *right* call — composition-root code (DI wiring, endpoint registration) is often more naturally integration-tested than unit-tested — but it's worth flagging as a decision that should be made deliberately (and written down, e.g. in `coding-guidelines.md`) rather than one that happened by default because S-102 was scoped as a pure move with no new tests required.

---

## 5. Watch-Only Items (#7–10) — Explicitly Not Action Items

Per this report's own priority-matrix doctrine (low churn + not-yet-a-problem = leave alone until something else touches the file), these four are listed for visibility only:

- **#7 — Large test files**: `WikidataClientTests.cs` (3,463 lines, now the single largest file in the repo), `GridGameModuleTests.cs` (2,474), `AuthEndpointTests.cs` (2,095). Growing for legitimate reasons (S-100/S-101 added real regression assertions). A future split-by-scenario pass would help navigability but isn't urgent.
- **#8 — `LeaderboardScreen.tsx`**: 1,130 lines, 6 `useEffect`, only 1 `useState` (a single state-object pattern, not 16 independent `useState` calls — actually a *healthier* shape than `AdminScreen.tsx`'s pre-S-103 form). Low churn (3 commits). No action.
- **#9 — `AuthController.cs`**: 773 lines, 13 methods, zero deep-nesting, churn of 1 commit in the repo's entire history. No action.
- **#10 — `SuggestionsScreen.tsx`**: 645 lines, 8 `useState`, 2 `useEffect`, but — unlike the `AdminScreen.tsx` components at #2 — already has its own dedicated `SuggestionsScreen.test.tsx`. Proportionate today. Named here only so that if it keeps growing, this report will have been the first to say so.

---

## 6. Action Plan — Newly Actionable Items (#3, #5, #6)

(#1 and #2's action plans are unchanged from the prior revision — see git history on this file — and both are large enough to warrant their own backlog stories rather than repeating the plan here.)

### #3: Sync governing docs with the S-102 `CompositionRoot` split

**Fix:** Update `docs/implementation-document.md` §4's folder-structure block (`/XGArcade.Api -> Controllers, DTOs, Program.cs` → mention `CompositionRoot/`) and every place in `docs/architecture-document.md`/`docs/implementation-document.md` that says a specific behavior is wired "in `Program.cs`" where it's now actually wired in one of the `CompositionRoot/*.cs` files. This is exactly `doc-sync`'s job — run it directly against the S-102 diff (PR #172) rather than against current uncommitted work.

**Verification:** grep both docs for `Program.cs` afterward and confirm every remaining hit is either still accurate (e.g. `Program.cs` still calls the `CompositionRoot` extension methods, so a reference to "wired via `Program.cs`'s composition root" is fine) or has been corrected.

### #5: Restructure `CliVerbDispatcher.cs` from one method into a verb registry

**Fix:** Same shape as `WikidataClient.cs`'s spec-table fix (S-100/S-101) — replace the sequential if/else-per-verb body with a `Dictionary<string, Func<...>>` (or similar) mapping each `--verb` string to its own named private method, populated once. `TryHandleAsync` becomes a lookup-and-dispatch, not 649 lines of inline logic.

**Verification:** pure refactor, no behavior change — existing coverage for each CLI verb (wherever it lives today, likely `XGArcade.Api.Tests` or manual verification per the workflow files) must exercise the same verb the same way before and after. No new REQ IDs.

### #6: Decide (and document) `CompositionRoot/*.cs`'s testing strategy

**Fix:** Not necessarily code — a deliberate decision, written down. Either (a) confirm integration-only coverage via `WebApplicationFactory` is the intended strategy for composition-root code and say so explicitly in `docs/coding-guidelines.md`, or (b) add focused unit tests for the parts of `AuthSetup.cs`/`ServiceRegistration.cs`/`EndpointMapping.cs` that have real conditional logic worth isolating (e.g. `AuthSetup.cs`'s `useLocalE2EAuth` branch). Either outcome is fine; what's not fine is the current state of neither being a decision.

**Verification:** if (b), new focused tests pass; either way, `coding-guidelines.md` gains a stated convention so the next composition-root-adjacent file doesn't re-litigate this.

---

## Appendix: What Was Not Assessed

- **`dotnet build` / `dotnet test` / `npm run test`:** still not runnable in this environment. All churn, SLOC, `npm audit`, method-count, and grep-based checks in this revision **were** re-run live.
- **True cyclomatic/cognitive complexity numbers:** still no AST-based tool available. This revision surfaced and explicitly corrected one false positive from the line-indentation nesting heuristic (`AdminSuggestionEndpoints.cs`) — a reminder that heuristic findings in this report should be read as directional, not exact, and are cross-checked by hand before being reported wherever feasible.
- **Temporal coupling and code-age/decay over a 6–12 month window:** still not possible — the repository remains under 3 weeks old in total.
