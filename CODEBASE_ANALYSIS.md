# xG Arcade — Codebase Analysis

**Scope:** `backend/`, `frontend/`, `infra/`, `docs/`
**Method:** static line/comment counting (custom Python scanner — `cloc`/`tokei` not available in this environment), `git log` churn analysis, targeted `grep`/manual review for security patterns, `npm audit` for frontend dependencies. `dotnet` CLI was not available in this environment, so no build, no `dotnet test`, and no NuGet vulnerability audit were run — package versions were reviewed by hand instead, and test-suite results are taken from the merged PRs' own CI runs (each PR in Epic 7 was gated on green CI before merge) rather than re-executed locally.
**Original analysis date:** 2026-08-10
**This update:** 2026-08-11, after Epic 7 (`docs/backlog.md`) landed

> **Important caveat on git-history analysis:** the task brief this report follows asks for churn over "the past 6–12 months." This repository's entire history is **65 commits spanning 2026-07-26 → 2026-08-11 (17 days)** — there is no 6–12 month window to analyze. All churn/hotspot findings reflect the project's *entire* lifetime, not a mature-codebase trend.

---

## 0. Remediation Status Since the 2026-08-10 Baseline

Following the original analysis, `docs/backlog.md` Epic 7 (S-099 through S-105) was created to work through the findings in chunks. **6 of 7 stories merged; 1 was never started.**

| Story | Target | Status | Result |
| :--- | :--- | :--- | :--- |
| S-099 | `undici` high-severity transitive dependency | ✅ Merged (#169) | `npm audit` now reports 0 vulnerabilities (was 1 High) |
| S-100 | `WikidataClient.cs` spec-table infra + 3 non-trophy pairs | ✅ Merged (#170) | Spec table introduced (`IntersectionQuerySpec.cs`, `IntersectionQuerySpecs.cs`) |
| S-101 | `WikidataClient.cs` remaining 6 trophy pairs | ✅ Merged (#171) | All 9 query pairs now driven by the spec table |
| S-102 | Decompose `Program.cs` composition root | ✅ Merged (#172) | `Program.cs`: 1,245 → **29 lines** |
| S-103 | Finish `AdminScreen.tsx` God-Component extraction | ✅ Merged (#173) | `AdminScreen.tsx`: 1,432 → **190 lines** |
| S-104 | Reduce `GridGameModule.cs` nesting | ❌ **Not started — no PR exists** | File unchanged (983 lines, same nesting depth) |
| S-105 | Trim inline comments duplicating ADR rationale | ✅ Merged (#174) | `Grid.css` comment ratio: 266% → 156%; `CellState.css`: 134% → 118% |

All six merged PRs report full test suites green in their PR descriptions (backend `dotnet test`, frontend `vitest`/`tsc`/`oxlint`) and were merged only after CI passed, per this repo's workflow. This analysis environment still has no `dotnet` CLI and no `node_modules` installed, so these results are taken from the PRs' own CI/test output, not re-executed here — `npm audit` and all `git`/static-scan checks below **were** re-run directly in this pass.

**Net effect:** every P1 item from the original report is resolved. The one P3 item that was also proposed (`GridGameModule.cs`, S-104) remains open — see §4 and §6 below, now the top actionable target in this codebase.

---

## 1. Executive Summary & Top Refactoring/Security Targets

**Overall health: Good, and improved since 2026-08-10. Security posture: Low risk.** The codebase remains disciplined and heavily tested (backend tests now 22.4k LOC vs. 9.7k LOC production, frontend tests ~11.0k LOC vs. 7.3k LOC production — both ratios essentially unchanged from the baseline). No hardcoded secrets, no SQL/SPARQL injection, no wildcard CORS, no unsafe `eval`/`innerHTML`, JWT validation still correctly configured. `npm audit` (both full and `--omit=dev`) now reports **0 vulnerabilities**, versus 1 High before.

The two largest structural hotspots from the original report — `WikidataClient.cs`'s duplication and `Program.cs`'s composition-root sprawl — are resolved. The frontend's `AdminScreen.tsx` God Component is resolved. The only originally-flagged item still outstanding is `GridGameModule.cs`'s nesting (S-104, never started).

**Top targets now:**

1. **`backend/src/XGArcade.Games.XGGrid/GridGameModule.cs` (983 lines, 23 methods, deepest nesting of any hand-written file in the repo)** — the one item from the original P1–P3 list that was never remediated. Promoted to this report's top target by elimination: everything larger or higher-churn has already been addressed. P2.
2. **`backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs`** — the newer sibling module to `GridGameModule.cs`, mirrors its shape (12 commits, similar size). Still correctly assessed as "watch, don't act yet" (P4) — flagged again only because if `GridGameModule.cs`'s nesting gets fixed without checking whether `XGPathGameModule.cs` copied the same pattern, the fix is incomplete.
3. **No security P1s.** `npm audit` is clean; the SPARQL-injection guard (`WikidataQid.IsValid`) survived the `WikidataClient.cs` refactor intact and is now centralized in one place instead of duplicated across 9 call sites — a genuine improvement, not just a wash.
4. **Backend C# comment volume grew slightly** (14,859 → 15,022 comment lines) even as CSS comment volume shrank (S-105's target). This is expected — the `Program.cs` decomposition (S-102) and `WikidataClient.cs` spec-table extraction (S-100/S-101) both created new files, each carrying forward the REQ/ADR-tagged rationale comments from the code they extracted, rather than the refactor being a net comment reduction. Not a concern; noted for completeness.
5. **Process observation, not a code finding:** S-104 sitting unstarted while 6 of 7 sibling stories merged suggests it's worth explicitly re-queuing rather than assuming it will get picked up — it has no dependents blocking it and no PR was ever opened, so it likely just wasn't scheduled. See §6.

---

## 2. Codebase Size & Comment Hygiene

| Language / Ext | Total Files | Source Lines (SLOC) | Comment Lines | Comment Ratio (%) | Δ vs. 2026-08-10 |
| :--- | ---: | ---: | ---: | ---: | :--- |
| C# (`.cs`) | 332 | 46,568 | 15,022 | 32.3% | +339 SLOC, +163 comment (new `CompositionRoot/`, `IntersectionQuerySpecs.cs` files) |
| TSX (`.tsx`) | 63 | 16,161 | 3,663 | 22.7% | +9 files (AdminScreen extraction), SLOC flat (moved, not added) |
| JSON (`.json`) | 11 | 3,128 | 0 | 0.0% | unchanged |
| CSS (`.css`) | 27 | 3,033 | 987 | 32.5% | **−207 comment lines (S-105)** |
| TypeScript (`.ts`) | 27 | 2,407 | 1,686 | 70.0% | +1 file, +50 SLOC |
| Markdown (`.md`, code dirs only) | 2 | 364 | 0 | — | unchanged |
| Bicep (`.bicep`) | 4 | 287 | 29 | 10.1% | unchanged |
| Shell (`.sh`) | 3 | 177 | 167 | 94.4% | unchanged |
| HTML (`.html`) | 1 | 20 | 0 | 0.0% | unchanged |
| **Overall Total** | **470** | **72,145** | **21,554** | **29.9%** | roughly flat (30.1% → 29.9%) |

**Test vs. production code (backend):** 9,722 SLOC production `.cs` vs. 22,443 SLOC test `.cs` — tests remain **~2.3× larger**, unchanged in shape from baseline.
**Test vs. production code (frontend):** 7,262 SLOC production vs. ~11,030 SLOC test code — unchanged.

### Under-Documented Modules

Unchanged from the original finding: no production business-logic file of meaningful size is under-documented. The refactor PRs did not introduce any new large, comment-sparse file — `CompositionRoot/CliVerbDispatcher.cs` (649 lines) and `IntersectionQuerySpecs.cs` (280 lines), the two largest new files, both carry forward the original per-clause/per-verb rationale comments rather than dropping them.

### Over-Documented / Bloated Modules — S-105 Result

S-105 targeted the four genuinely high-ratio files identified in the original report:

| File | Before | After | Change |
| :--- | ---: | ---: | :--- |
| `frontend/src/grid/Grid.css` | 266% | **156%** | −110pp, largest single reduction |
| `frontend/src/grid/CellState.css` | 134% | **118%** | −16pp |
| `frontend/src/lib/turnstile.ts` | 157% | **142%** | −15pp |
| `frontend/src/lib/types.ts` | 116% | **114%** | −2pp (deliberately left mostly untouched) |

`types.ts` barely moved, and that's the correct outcome, not an incomplete job: the story's own rule was "skip any comment that is the only place its rationale is recorded," and `types.ts`'s per-field comments were, on inspection, largely *not* duplicated in any ADR — they're the field-level API contract rationale, which has nowhere else to live. `Grid.css` and `CellState.css` had the most genuine duplication with existing ADRs/design-doc sections and shrank accordingly. This is exactly the selective outcome the story intended, and the PR's `quality-architect` review (per its description) confirmed no rationale was lost, only de-duplicated.

Overall comment ratio (30.1% → 29.9%) is essentially flat rather than dropping further, because the C# side grew (new extracted files carrying their comments forward) while CSS shrank — a wash, not a regression.

---

## 3. Security & Secrets Findings

**Hardcoded Secrets / Tokens:** Still none found, including in every new file introduced by Epic 7 (`CompositionRoot/*.cs`, `IntersectionQuerySpec*.cs`, the new `frontend/src/admin/*.tsx` section files). Re-ran the same secret-pattern grep across these specifically — only expected, correctly-handled hits (`AuthSetup.cs`'s `client.DefaultRequestHeaders.Add("apikey", supabaseAnonKey)`, which reads the key from configuration, not a literal).

**Vulnerable Dependencies:**

| Package | Was | Now | Status |
| :--- | :--- | :--- | :--- |
| `undici` (transitive, via `jsdom`) | 7.28.0, High severity (5 advisories) | **7.29.0+, resolved (S-099, PR #169)** | `npm audit` (full) and `npm audit --omit=dev`: **0 vulnerabilities**, both re-confirmed in this pass |

Backend NuGet package versions unchanged since the original review (still current .NET 10 releases); Dependabot remains active.

**Risky OWASP-Top-10 Patterns — re-verified after the refactor:**

- **SQL injection:** still none — no raw/interpolated SQL anywhere.
- **SPARQL injection (Wikidata client):** the `WikidataQid.IsValid(...)` guard survived the spec-table refactor and, per S-100's explicit design goal, is now **centralized in the shared driver** (`WikidataClient.cs` around the `RunIntersectionQueryAsync`/spec-lookup path) instead of duplicated across 9 separate methods — confirmed by grep (`IsValid` now appears at the shared call sites, not once per removed `Build*Query` method). This is a structural improvement: a future 10th category pair added to `IntersectionQuerySpecs.cs` cannot forget the guard the way a copy-pasted method could have.
- **CORS:** still an explicit allow-list (`policy.WithOrigins(corsAllowedOrigins)`), now living in `CompositionRoot/AuthSetup.cs` rather than `Program.cs` — same policy, same behavior, moved file.
- **`eval()` / `innerHTML` / `dangerouslySetInnerHTML`:** none in any of the new `frontend/src/admin/*.tsx` section files created by the `AdminScreen.tsx` extraction.
- **Auth/JWT:** unchanged and still correctly configured, now in `CompositionRoot/AuthSetup.cs` (284 lines) instead of inline in `Program.cs`.

**No P1 security findings**, same conclusion as the original report, with the SPARQL-guard centralization as a genuine (if minor) net improvement.

---

## 4. High-Risk Hotspots (Churn × Complexity Matrix) — Updated

Churn = commit count over the repo's full 17-day history.

| File / Module Path | Churn (Commits) | Size / Complexity | Priority Level | Status |
| :--- | ---: | :--- | :--- | :--- |
| `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs` | 7 | 983 lines, 23 methods, same deep nesting as original report (no change) | **P2** | **Still open — top target** |
| `backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs` | 12 | Mirrors `GridGameModule.cs`'s shape | P4 | Watch — check for the same nesting pattern before/alongside a `GridGameModule.cs` fix |
| `backend/src/XGArcade.Data/Repositories/PlayerStoreRepository.cs` | 10 | 772 lines | P4 | Unchanged, still low concern |
| `frontend/src/lib/types.ts` | 12 | 620 lines, dense contract-rationale comments (largely intentional, see §2) | P3 | Unchanged — healthy churn, this is the frontend/backend contract file |
| `frontend/src/lib/api.ts` | 10 | 1,057 lines | P3 | Unchanged |
| ~~`backend/src/XGArcade.DataSync/Wikidata/WikidataClient.cs`~~ | 22 | 1,815 lines (was 2,034); 9 wrapper methods now call a shared spec-driven path instead of 9 independent duplicated implementations | ~~P1~~ | **Resolved (S-100/S-101)** |
| ~~`backend/src/XGArcade.Api/Program.cs`~~ | 23 (historical — see note) | **29 lines** (was 1,245); logic moved to `CompositionRoot/{AuthSetup,CliVerbDispatcher,EndpointMapping,ServiceRegistration,WikidataHttpClientConfiguration}.cs` | ~~P2~~ | **Resolved (S-102)** |
| ~~`frontend/src/admin/AdminScreen.tsx`~~ | 8 | **190 lines**, 3 `useState` (was 1,432 lines / 16 `useState`); 9 sections extracted to their own files | ~~P2/P3~~ | **Resolved (S-103)** |

Note on `Program.cs`'s churn count (23, still numerically the highest in the repo): this is a lagging historical artifact — it counts every commit across the file's *entire* lifetime, including all the pre-refactor commits that built up to 1,245 lines and the S-102 commit that emptied it. Going forward, changes to auth config, CLI verbs, or endpoint mapping will accumulate on the new `CompositionRoot/*.cs` files instead, so `Program.cs`'s churn count will now flatline — it's a historical total, not a live signal, and should be read that way in any future re-run of this report.

New composition-root files, for future churn tracking: `CompositionRoot/CliVerbDispatcher.cs` (649 lines — itself worth a future glance if it starts growing the way `Program.cs`'s CLI-verb section once did), `CompositionRoot/AuthSetup.cs` (284), `CompositionRoot/ServiceRegistration.cs` (264), `CompositionRoot/EndpointMapping.cs` (95), `CompositionRoot/WikidataHttpClientConfiguration.cs` (18).

---

## 5. Structural & Architectural Anomalies — Re-checked

- **Hidden coupling:** the same two expected clusters as before (`WikidataClient.cs` ↔ its test file; `CHANGELOG.md` ↔ the other governing docs, per the mandated workflow). No new coupling introduced by Epic 7 — each refactor PR touched exactly the files described in its own story, plus `docs/coding-guidelines.md`/`docs/CHANGELOG.md` as required.
- **Architecture-boundary check (ADR-0003):** re-ran the same `Core`/game-module co-change check; no new violations. The `Program.cs` decomposition stayed entirely within `XGArcade.Api`; the `WikidataClient.cs` refactor stayed entirely within `XGArcade.DataSync`; neither crossed into `XGArcade.Core` or added a game-specific reference to a Core entity.
- **Duplication clusters:** the one cluster flagged originally (`WikidataClient.cs`'s 9-way query-builder duplication) is resolved. No new duplication cluster was introduced by any of the 6 merged refactor PRs — each PR's own description states it was verified as a pure/mechanical extraction with no logic drift, and spot-checking the new `frontend/src/admin/*.tsx` section files and `CompositionRoot/*.cs` files found no copy-paste repetition among them.

---

## 6. Concrete Action Plan for Remaining Targets

### P2-1 (was P3 in the original report, promoted by elimination): `GridGameModule.cs` — S-104, never started

**Root Cause / Risk:** Unchanged from the original report — `GridGameModule.cs` (983 lines, 23 methods) has the deepest control-flow nesting of any hand-written file in the repo (25 lines at ≥5 indentation levels, per the original scan; unchanged since no commit has touched this file since before Epic 7 started). The risk is the same as originally assessed: not a live bug, but the file is the most expensive one left in the repo to review or safely extend, now that the two larger hotspots are gone.

**Why flag it again instead of assuming it's covered:** `docs/backlog.md`'s S-104 entry describes this exact work, was never picked up (no PR, open or closed, references it), and has no blocking dependency — S-100 through S-103 and S-105 all merged independently around it. It's the one item most likely to be quietly skipped if this report isn't explicit that it's still outstanding.

**Proposed Fix:** unchanged from `docs/backlog.md`'s S-104 story text — flatten the deepest-nested branches into named private methods / early-returns, without changing generation or scoring behavior. Before starting, do a quick check of whether `XGPathGameModule.cs` (the newer sibling game module) copied the same nested shape from `GridGameModule.cs` during its own development — if so, consider whether the fix should cover both in the same pass rather than leaving `XGPathGameModule.cs` as a second copy of the pattern.

**Verification Strategy:** unchanged — full `GridGameModuleTests.cs` suite must pass with no behavior change; report the nesting-depth reduction (lines at ≥5 indent levels) against this report's and the original report's baseline of 25.

### Everything else from the original P1 list

No further action plan needed — see §0's status table. `WikidataClient.cs` and `Program.cs` (both former P1/P2) and `AdminScreen.tsx` (former P2/P3) are resolved and verified in this pass via direct file inspection, `npm audit`, and re-run static/security scans. The `undici` fix (former P1) is confirmed via a freshly re-run `npm audit` in this session, not just taken on faith from the PR description.

---

## Appendix: What Was Not Assessed

- **`dotnet build` / `dotnet test`:** still not runnable in this environment (`dotnet` CLI not installed). This pass leans on each merged PR's own description of its local test results and the fact that every PR was merged under this repo's "merge on green CI" gate, rather than re-executing the suite here.
- **Frontend test suite:** `node_modules` is not installed in this environment (`vitest: not found` on attempted re-run), so `npm run test` was not re-executed here either; `npm audit` (which only needs the lockfile) was re-run directly and is the one dependency-health check genuinely re-verified in this pass, not just cited from a PR.
- **True cyclomatic/cognitive complexity numbers:** same limitation as the original report — no AST-based complexity tool available; `GridGameModule.cs`'s nesting assessment is still the same line-based heuristic as the original scan, not a computed score.
- **Temporal coupling and code-age/decay, per the brief's specified 6–12 month window:** still not possible — the repository is 17 days old in total.
