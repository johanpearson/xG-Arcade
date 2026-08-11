# xG Arcade — Codebase Analysis

**Scope:** `backend/`, `frontend/`, `infra/`, `docs/`
**Method:** static line/comment counting (custom Python scanner — `cloc`/`tokei` not available in this environment), `git log` churn analysis, targeted `grep`/manual review for security and duplication patterns, `npm audit` for frontend dependencies. `dotnet` CLI and frontend `node_modules` are not available in this environment, so no build, no `dotnet test`/`npm run test`, and no NuGet vulnerability audit were run directly — package-version and behavioral claims for already-merged work are taken from the merging PRs' own CI-gated results, not re-executed here. Everything reported as "verified in this pass" (SLOC, churn, `npm audit`, grep-based security/duplication checks) **was** re-run live against current `main`.

**Revision history:**
- **2026-08-10** — original analysis. Flagged `WikidataClient.cs` duplication, a high-severity `undici` transitive dependency, `Program.cs` composition-root sprawl, `AdminScreen.tsx`'s God Component, and `GridGameModule.cs`'s nesting as the top targets → became Epic 7 (`docs/backlog.md`, S-099–S-105).
- **2026-08-11 (first update)** — re-verified 6 of 7 Epic 7 stories merged; flagged S-104 (`GridGameModule.cs`) as not started.
- **2026-08-11 (this revision)** — S-104 has since merged (#175). Epic 7 is now **fully complete (7/7)**. This revision re-scans the whole codebase from scratch, past the original finding list, to surface the *next* batch of priorities.

---

## 0. Epic 7 Closeout

| Story | Target | Result |
| :--- | :--- | :--- |
| S-099 | `undici` high-severity dependency | ✅ `npm audit` (full and `--omit=dev`): **0 vulnerabilities**, re-confirmed live in this pass |
| S-100/S-101 | `WikidataClient.cs` query-builder duplication | ✅ 2,034 → **1,815 lines**; SPARQL-injection guard now centralized instead of duplicated 9× |
| S-102 | `Program.cs` composition root | ✅ 1,245 → **29 lines**, logic moved to `CompositionRoot/*.cs` |
| S-103 | `AdminScreen.tsx` God Component | ✅ 1,432 → **190 lines**, 9 sections extracted |
| S-104 | `GridGameModule.cs` nesting | ✅ Deep-nesting heuristic (lines at ≥5 indent levels): **25 → 3** |
| S-105 | Comment dedup vs. ADRs | ✅ `Grid.css` 266%→156%, `CellState.css` 134%→118% |

No open items remain from the original report. `npm audit`, CORS config, SQL/SPARQL-injection guards, and secret-pattern scans were all re-run directly against current `main` in this pass and remain clean — **no security findings, P1 or otherwise.** The rest of this report is a fresh sweep for the next batch of priorities, not a re-check of resolved items.

---

## 1. Executive Summary & Next Priorities

**Overall health: Good, and meaningfully better structured than either prior report.** With every original hotspot resolved, the next batch of findings is lower-severity by nature — there is **no P1 material in this pass**. That's a genuine result, not a gap in the scan: the codebase's largest, highest-churn files are now all either fixed-size compositions (`Program.cs`, `AdminScreen.tsx`) or already-addressed duplication (`WikidataClient.cs`). What's left is normal, second-tier maintainability debt.

**Top priorities for the next batch:**

1. **`PlayerStoreRepository.cs` / `IPlayerStoreRepository.cs` (772 / 482 lines, 44 methods, 10 commits) — a single repository spanning at least 9 distinct sub-entity concerns**: `Player` CRUD, `PlayerData` (unverified/approve/remove), `PlayerAttribute`, `PlayerAlias`, `PlayerOverride`, photo backfill, position/birth-year backfill, `PlayerCareerStint`, and confirmed-low/technical-failure tracking. This is the strongest genuine Single-Responsibility-Principle violation left in the codebase — not a nesting or churn problem (0 deep-nesting lines, moderate churn), but a scope problem: an unrelated change to career-stint sync logic and a change to override management both touch the same 772-line file and its 482-line interface. **P2.**
2. **Test-architecture drift following S-103** — `AdminScreen.tsx`'s extraction (correctly, per its own "pure mechanical extraction" scope) split the implementation into 10 files but left all test coverage in the single, unsplit `AdminScreen.test.tsx`. The 9 newly extracted components (`PlayerSuggestionsEntry.tsx`, `IncidentReportsEntry.tsx`, `AnnouncementBannerSection.tsx`, `UnverifiedDataSection.tsx`, `AccountMetricsSection.tsx`, `GuestClearSection.tsx`, `XGPathCycleSection.tsx`, `RoundControlSection.tsx`, `UserDeletionSection.tsx`) have **zero dedicated test files** — every one is still only exercised indirectly through `AdminScreen.test.tsx`'s full-tree rendering. This was the right call *during* S-103 (don't touch tests when behavior doesn't change), but it's now a natural, direct follow-on: as these 9 components evolve independently, only a large, slow, full-tree test file will catch a regression in any one of them. **P2.**
3. **`frontend/src/lib/api.ts` (1,057 lines, 51 exports, ~47 near-identically-shaped fetch-wrapper functions)** — flagged as P3 in the original report and never addressed by Epic 7 (it wasn't one of the 7 stories). Now the largest unaddressed hand-written frontend file. Candidate for the same domain-split treatment `Program.cs` got: `auth.ts`, `rounds.ts`, `leaderboard.ts`, `admin.ts`, `path.ts`. **P3** — real, but breadth-driven size, not complexity; lower urgency than #1/#2.
4. **Large test files continuing to grow**: `WikidataClientTests.cs` is now **3,463 lines** (was 3,107 at the original report — S-100/S-101 added byte-for-byte SPARQL regression assertions, which is exactly the right kind of growth, but the file is now the single largest file in the repo by a wide margin), `GridGameModuleTests.cs` (2,474), `AuthEndpointTests.cs` (2,095). Not urgent — large test files aren't unhealthy the way large production files are — but worth a watch note since navigability/run-time will keep degrading as they grow further. **P4.**
5. **`LeaderboardScreen.tsx` (1,130 lines, 6 `useEffect`, low churn) and `AuthController.cs` (773 lines, churn 1)** — both large but low-churn. Per this report's own priority-matrix doctrine (P2 = "low churn + potentially low health — leave alone until active development touches it"), these are explicitly **not** action items now; noted so they aren't rediscovered as a surprise the next time either file gets touched. **P4/watch.**

One thing checked and *cleared*, not a finding: several of S-103's extracted components (`GuestClearSection.tsx`, `RoundControlSection.tsx`, `UnverifiedDataSection.tsx`, `UserDeletionSection.tsx`) don't use the shared `useAdminSectionFetch` hook that `#167`/S-103 established. On inspection this is intentional, not inconsistent — `AdminScreen.tsx` retained exactly 3 `useState` calls (`pageState`, `unverifiedRows`, `activeRound`) per its own PR description, and these sections receive that data as props from the parent rather than fetching it themselves (action-only sections like guest-clear/user-deletion don't fetch at all). No action needed.

---

## 2. Codebase Size & Comment Hygiene

| Language / Ext | Total Files | Source Lines (SLOC) | Comment Lines | Comment Ratio (%) |
| :--- | ---: | ---: | ---: | ---: |
| C# (`.cs`) | 332 | 46,595 | 15,038 | 32.3% |
| TSX (`.tsx`) | 63 | 16,161 | 3,663 | 22.7% |
| JSON (`.json`) | 11 | 3,128 | 0 | 0.0% |
| CSS (`.css`) | 27 | 3,033 | 987 | 32.5% |
| TypeScript (`.ts`) | 27 | 2,407 | 1,686 | 70.0% |
| Markdown (`.md`, code dirs only) | 2 | 364 | 0 | — |
| Bicep (`.bicep`) | 4 | 287 | 29 | 10.1% |
| Shell (`.sh`) | 3 | 177 | 167 | 94.4% |
| HTML (`.html`) | 1 | 20 | 0 | 0.0% |
| **Overall Total** | **470** | **72,172** | **21,570** | **29.9%** |

Essentially flat since the last revision (S-104's nesting fix added modest line count without materially changing comment density). Comment hygiene remains a strength, not a gap — re-scanning after S-104, no new under-documented complex file and no new noise/dead-code comment was introduced. `GridGameModule.cs`'s refactor kept its REQ/ADR-tagged rationale comments attached to the newly-extracted private methods rather than dropping them.

Test-vs-production LOC ratios are unchanged from prior revisions (backend ~2.3×, frontend ~1.5×) — still a strong signal for this codebase's size.

---

## 3. Security & Secrets Findings

Re-verified fresh in this pass, not carried over from a prior report:

- **Hardcoded secrets:** none, repository-wide.
- **`npm audit` (full and `--omit=dev`):** **0 vulnerabilities.**
- **NuGet package versions:** unchanged, still current .NET 10 releases (`Microsoft.AspNetCore.*`/`Microsoft.EntityFrameworkCore.*` 10.0.10, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3).
- **SQL injection:** no `FromSqlRaw`/`ExecuteSqlRaw` anywhere in the tree.
- **SPARQL injection:** `WikidataQid.IsValid(...)` guard confirmed still present and centralized post-refactor.
- **CORS:** still an explicit `WithOrigins(...)` allow-list (now in `CompositionRoot/AuthSetup.cs`), no `AllowAnyOrigin()`.
- **`eval()`/`innerHTML`/`dangerouslySetInnerHTML`:** none in production code, repository-wide.

**No P1 security findings.** This section is included in full despite being unchanged, specifically because it was re-run rather than assumed — see the Method note at the top of this document.

---

## 4. High-Risk Hotspots (Churn × Complexity Matrix) — This Revision

Churn = commit count over the repo's full history (66 commits, 2026-07-26 → 2026-08-11 — still too short a window for the "6–12 months" framing this report format assumes; read churn as "build-out attention," not "decay signal").

| File / Module Path | Churn | Size / Complexity | Priority |
| :--- | ---: | :--- | :--- |
| `backend/src/XGArcade.Data/Repositories/PlayerStoreRepository.cs` + `IPlayerStoreRepository.cs` | 10 + 10 | 772 + 482 lines, 44 methods spanning 9 distinct sub-entity concerns; 0 deep-nesting (not a complexity problem, a scope problem) | **P2 — new top target** |
| `frontend/src/admin/AdminScreen.test.tsx` (+ 9 untested extracted components) | 8 | 1,328 lines covering what is now 10 separate implementation files; 0 dedicated test files for the 9 newly extracted components | **P2 — new** |
| `frontend/src/lib/api.ts` | 10 | 1,057 lines, 51 exports, ~47 near-identical fetch-wrapper functions in one file | P3 — carried over, unaddressed |
| `backend/tests/XGArcade.DataSync.Tests/Wikidata/WikidataClientTests.cs` | 21 | 3,463 lines — largest file in the repo; growth from S-100/S-101's regression assertions is expected/correct, but the file is now very large | P4 — watch (test-only) |
| `frontend/src/leaderboard/LeaderboardScreen.tsx` | 3 | 1,130 lines, 6 `useEffect`, 1 `useState` | P4 — watch, low churn |
| `backend/src/XGArcade.Api/Auth/AuthController.cs` | 1 | 773 lines, 13 methods, 0 deep-nesting | P4 — watch, low churn |
| `backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs` | 12 | 417 lines, 0 deep-nesting — **confirmed it did not inherit `GridGameModule.cs`'s pre-fix nesting pattern**, so S-104 doesn't need a follow-on here | Cleared, no action |
| `backend/src/XGArcade.Api/CompositionRoot/CliVerbDispatcher.cs` | new (from S-102) | 649 lines — the largest of the five files `Program.cs` was split into | Watch — if this grows the way `Program.cs`'s CLI-verb section once did, it's the next composition-root file to revisit |

**Resolved and no longer listed** (full detail in prior revisions, summarized in §0): `WikidataClient.cs`, `Program.cs`, `AdminScreen.tsx`'s own size, `GridGameModule.cs`'s nesting, `Grid.css`/`CellState.css`'s comment ratios, the `undici` dependency.

---

## 5. Structural & Architectural Anomalies — Re-checked

- **Architecture-boundary check (ADR-0003):** re-ran the `Core`/game-module co-change check against the fuller history; still no violation — no commit adds a game-specific reference to a `Core` entity.
- **Duplication:** the one major cluster (`WikidataClient.cs`) is resolved and stayed resolved (no regression). `frontend/src/lib/api.ts`'s ~47 similarly-shaped fetch wrappers (see §1/§4) is the next most significant repetition in the codebase, but it's breadth (many independent small endpoints), not logic duplication — each function is doing a genuinely different thing, just with a similar shape. Lower-severity than `WikidataClient.cs`'s pattern was.
- **New structural finding — repository scope:** `PlayerStoreRepository.cs` (see §1/§4) is the clearest SRP violation now in the codebase. Worth noting it wasn't visible in either prior report because those reports were scoped to the original P1 items and their direct remediation — this is the first pass to sweep the rest of the codebase for new candidates since Epic 7 started.
- **New structural finding — test/implementation shape mismatch:** `AdminScreen.test.tsx` vs. its 10 post-extraction implementation files (see §1/§4) is a specific, nameable instance of "test structure didn't follow code structure" — worth flagging precisely because it's a *direct* consequence of otherwise-good refactoring work, not a pre-existing problem.

---

## 6. Concrete Action Plan for the Top Two New Targets

### P2-1: `PlayerStoreRepository.cs` — repository spanning too many concerns

**Root Cause / Risk:** The repository grew the same way `WikidataClient.cs` originally did — by accretion, one new sub-entity's data-access methods at a time (`PlayerAttribute`, then `PlayerAlias`, then `PlayerOverride`, then career stints, then confirmed-low/technical-failure tracking), each addition reasonable in isolation. The risk: `IPlayerStoreRepository`'s 482-line interface means every consumer (real implementation, test fakes, any future implementation) must implement all 44 methods regardless of which sub-entity it actually needs, and any change to one concern (e.g. career-stint merge logic) requires touching a file that also contains unrelated override-management and photo-backfill logic — raising review cost and merge-conflict risk as more of these concerns get touched concurrently.

**Proposed Fix:** Split by sub-entity into focused repositories/interfaces — e.g. `IPlayerRepository` (core CRUD), `IPlayerAttributeRepository`, `IPlayerAliasRepository`, `IPlayerOverrideRepository`, `IPlayerCareerStintRepository`, `IPlayerDataQualityRepository` (confirmed-low/technical-failure tracking, photo/position backfill). Each becomes its own file, registered separately in DI (now straightforward to wire up via `CompositionRoot/ServiceRegistration.cs`, itself a product of S-102). Existing callers that need multiple concerns take multiple injected repositories rather than one wide one — check call sites first (`GridGameModule.cs`, `XGPathGameModule.cs`, `DataSync` services, admin endpoints) to see how many actually need cross-concern access in a single method, since that determines whether a thin facade over the split repositories is worth keeping for convenience.

**Verification Strategy:** This is a larger refactor than any single Epic 7 story — consider splitting it into 2–3 stories the same way `WikidataClient.cs` was (S-100/S-101), e.g. one story per 2–3 sub-entities. For each split-out repository, the existing `PlayerStoreRepositoryTests.cs` (1,401 lines) should already have close to 1:1 method coverage — move/rename its tests to match the new repository boundaries rather than rewriting assertions, which keeps this a structural-only change. No behavior change, no new REQ IDs, same as Epic 7's other stories; add an ADR since "split one repository into N" is exactly the kind of structural, reversible-but-not-obviously-reversible choice `CLAUDE.md` asks for one on.

### P2-2: Backfill test coverage for `AdminScreen.tsx`'s 9 extracted components

**Root Cause / Risk:** S-103 correctly scoped itself to a pure, mechanical, behavior-preserving extraction and correctly left `AdminScreen.test.tsx` untouched as its regression net. The gap this leaves: going forward, a change scoped to just one extracted component (say, `IncidentReportsEntry.tsx`) has no fast, focused test to run — only the full `AdminScreen.test.tsx` suite, which renders the entire composed admin screen tree.

**Proposed Fix:** For each of the 9 extracted components, add a dedicated `<ComponentName>.test.tsx` covering its own props/state/rendering in isolation, then either trim the now-redundant coverage from `AdminScreen.test.tsx` (keeping only composition-level tests — "does `AdminScreen` render all its sections," "does prop-passing work") or leave `AdminScreen.test.tsx` as a thinner integration-level check on top of the new focused unit tests. `useAdminSectionFetch.ts` (the shared hook from `#167`) should get its own test file too if it doesn't have one already — it's shared infrastructure now used by 5 of the 9 components.

**Verification Strategy:** This is naturally splittable into one story per component (or per 2–3 related components), each independently mergeable, no dependencies between them. Acceptance per story: new dedicated test file passes, existing `AdminScreen.test.tsx` still passes unchanged (or is deliberately trimmed with an explicit note on what moved where), no behavior change.

---

## Appendix: What Was Not Assessed

- **`dotnet build` / `dotnet test` / `npm run test`:** not runnable in this environment (no `dotnet` CLI, no `node_modules` installed). All churn, SLOC, `npm audit`, and grep-based security/duplication findings in this revision **were** re-run live; test-pass claims for already-merged Epic 7 work are taken from those PRs' own CI-gated results.
- **True cyclomatic/cognitive complexity numbers:** still no AST-based complexity tool available; `PlayerStoreRepository.cs`'s "too many concerns" finding is a manual read of its method list grouped by entity, not a computed coupling/cohesion metric.
- **Temporal coupling and code-age/decay over a 6–12 month window:** still not possible — the repository is under 3 weeks old in total.
