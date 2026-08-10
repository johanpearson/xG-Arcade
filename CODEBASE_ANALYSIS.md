# xG Arcade — Codebase Analysis

**Scope:** `backend/`, `frontend/`, `infra/`, `docs/`
**Method:** static line/comment counting (custom Python scanner — `cloc`/`tokei` not available in this environment), `git log` churn analysis, targeted `grep`/manual review for security patterns, `npm audit` for frontend dependencies. `dotnet` CLI was not available in this environment, so no build, no `dotnet test`, and no NuGet vulnerability audit were run — package versions were reviewed by hand instead.
**Date:** 2026-08-10

> **Important caveat on Step 2 (behavioral/git history):** the task brief asks for churn over "the past 6–12 months." This repository's entire history is **58 commits spanning 2026-07-26 → 2026-08-10 (16 days)** — there is no 6–12 month window to analyze. All churn/hotspot/temporal-coupling findings below reflect the project's *entire* lifetime, not a mature-codebase trend, and should be read as "what's been touched a lot during initial build-out," not "what's decaying." No 12-month "code age & decay" analysis is possible or reported for the same reason.

---

## 1. Executive Summary & Top Refactoring/Security Targets

**Overall health: Good. Security posture: Low risk.** This is a young (16-day-old), disciplined, heavily-tested codebase built under an unusually strict documentation/ADR process (enforced by `CLAUDE.md` and a set of specialized review agents). No hardcoded secrets, no SQL/SPARQL injection, no wildcard CORS, no unsafe `eval`/`innerHTML`, and JWT validation is correctly configured (JWKS-based, issuer/audience/lifetime all validated). Test code (22.2k LOC backend, ~11k LOC frontend) outweighs production source code (9.7k LOC backend, 7.2k LOC frontend) by more than 2:1, which is a strong signal for a codebase this age.

The main risks are not security vulnerabilities but **maintainability hotspots concentrated in a handful of files that are both large and frequently changed**, plus one **real, if low-severity, dependency vulnerability**.

**Top 5 highest-ROI targets:**

1. **`WikidataClient.cs` (2,034 lines, 65 methods, 20 commits, 9-way duplicated query-builder pattern)** — the single strongest hotspot in the repo: large, high-churn, and structurally repetitive. P1.
2. **`undici` 7.28.0 (transitive, via `jsdom`→Vitest) — High-severity advisory (GHSA-8xcm-r25x-g524 and others)** — trivial one-line fix available. P1 (cheap, do immediately, even though blast radius is test-only).
3. **`backend/src/XGArcade.Api/Program.cs` (1,245 lines, 22 commits — the single most-changed source file in the repo)** — a monolithic composition-root file mixing DI wiring, JWT config, CLI-verb dispatch, and endpoint mapping. P2 (not unhealthy yet, but it's the single point every feature commit touches).
4. **`AdminScreen.tsx` (1,432 lines, 16 `useState`, 4 `useEffect`)** — God Component for the admin dashboard; already partially being addressed (see `#167`, which extracted `useAdminSectionFetch`). P2/P3 — keep extracting.
5. **Frontend `.ts`/`.css` files with very high comment ratios** (`types.ts` 116%, `Grid.css` 266%, `turnstile-stub.ts` 276%) — not noise, but a documentation-style outlier worth a conscious call: these are dense root-cause/rationale comments (many tied to REQ/ADR/S-### IDs) rather than restated code. P4 — a style observation, not a defect, but flagged since it's unusual enough to be worth a deliberate "yes, we do this on purpose" decision rather than an accident of habit.

No P1-severity security findings exist. The single P1 item that *is* security-flavored is the `undici` advisory, included at P1 only because it is a five-minute fix, not because it is exploitable in this application's actual deployment (it's a dev/test-only transitive dependency of `jsdom`, never shipped to production).

---

## 2. Codebase Size & Comment Hygiene

| Language / Ext | Total Files | Source Lines (SLOC) | Comment Lines | Comment Ratio (%) |
| :--- | ---: | ---: | ---: | ---: |
| C# (`.cs`) | 324 | 46,229 | 14,859 | 32.1% |
| TSX (`.tsx`) | 54 | 16,191 | 3,680 | 22.7% |
| JSON (`.json`) | 11 | 3,128 | 0 | 0.0% |
| CSS (`.css`) | 27 | 3,033 | 1,194 | 39.4% |
| TypeScript (`.ts`) | 26 | 2,357 | 1,677 | 71.1% |
| Markdown (`.md`, code dirs only) | 2 | 364 | 0 | — |
| Bicep (`.bicep`) | 4 | 287 | 29 | 10.1% |
| Shell (`.sh`) | 3 | 177 | 167 | 94.4% |
| HTML (`.html`) | 1 | 20 | 0 | 0.0% |
| **Overall Total** | **452** | **71,786** | **21,606** | **30.1%** |

(`docs/` itself — 66 ADRs plus the governing documents — is prose, not counted as source; it's a large, actively-maintained corpus in its own right and is the reason comment hygiene in code is so high: the project's convention is to cite REQ/ADR/S-### IDs directly in comments rather than duplicate rationale.)

**Test vs. production code (backend):** 9,665 SLOC production `.cs` vs. 22,161 SLOC test `.cs` — tests are **2.3× larger** than the code they cover.
**Test vs. production code (frontend):** 7,242 SLOC production `.ts`/`.tsx` vs. ~11,030 SLOC test code (`*.test.tsx` + `tests/e2e/*.ts`) — tests are **~1.5× larger**.

### Under-Documented Modules

Using the brief's threshold (>200 SLOC hand-written file, <5% comment ratio), the codebase is almost entirely clean. After excluding EF Core auto-generated migration `*.Designer.cs` files (which are correctly and expectedly comment-free — they're generated code, not hand-written) and test files, only two files clear even a relaxed >150 SLOC / <5% bar:

- `backend/tests/XGArcade.Api.Tests/IncidentEndpointTests.cs` (279 SLOC, 4.7%) — a test file, low concern.
- `frontend/src/admin/SuggestionsScreen.css` (182 SLOC, 3.3%) — plain CSS, low concern.

**No production business-logic file of meaningful size is under-documented.** This is a genuine strength, not a gap to remediate.

### Over-Documented / Bloated Modules

Several files exceed the brief's 35–40% threshold substantially:

| File | Comment Ratio | Character |
| :--- | ---: | :--- |
| `frontend/src/lib/suggestionCopy.ts` | 375% | Tiny file (4 SLOC), ratio is an artifact of file size, not real bloat |
| `frontend/tests/e2e/turnstile-stub.ts` | 276% | Small stub file, same artifact |
| `frontend/src/grid/Grid.css` | 266% | **Real** — see below |
| `frontend/src/grid/CellState.css` | 134% | **Real** — see below |
| `frontend/src/lib/turnstile.ts` | 157% | **Real** |
| `frontend/src/lib/types.ts` | 116% | **Real** |
| `frontend/tests/e2e/play-grid.spec.ts` | 115% | Real, test file |

Manual inspection of `types.ts` and `Grid.css` (the two largest genuine outliers) found **no noise comments and no dead commented-out code** — every comment is a substantive, REQ/ADR/S-###-tagged explanation of a non-obvious invariant or a previously-debugged root cause (e.g. `Grid.css` documents a specific CSS `table-layout: auto` sizing bug, with the exact symptom, root cause, and why the fix doesn't regress the ≤480px breakpoint). A targeted regex search for typical noise-comment patterns (`// sets/gets/returns/creates the X`) across all of `frontend/src` returned only 2 hits, both of which turned out to be substantive rather than restating the following line. **No commented-out dead code blocks and no stray `TODO`/`FIXME`/`HACK` markers were found anywhere in the tracked source tree.**

**Verdict:** the high ratios are a deliberate documentation style (deep, ID-linked rationale, consistent with `CLAUDE.md`'s "only comment on the non-obvious WHY" rule taken to a thorough extreme), not comment bloat in the SonarQube-noise sense. The one soft recommendation: some of the longest CSS/type comments (`Grid.css` lines 11–47, `types.ts`'s per-field paragraphs) function as inline ADR addenda — for the *longest* of these (multi-paragraph, spanning several fields), consider whether the rationale would be better centralized in the relevant ADR with just a pointer comment in code, purely to keep the source file scannable. This is a P4/quality-of-life suggestion, not a hygiene defect.

---

## 3. Security & Secrets Findings

**Hardcoded Secrets / Tokens:** **None found.** No `.env` files are tracked in git. A full-repository grep for API-key/secret/password/bearer-token/private-key patterns across `.cs`, `.ts`, `.tsx`, `.json`, `.yml`, `.bicep`, `.sh` turned up only:
- Correct `${{ secrets.* }}` GitHub Actions references (`.github/workflows/*.yml`) — this is the intended pattern, not a leak.
- `POSTGRES_PASSWORD: postgres` in `.github/workflows/ci.yml:81` — an ephemeral, localhost-only Postgres service-container password used solely inside the CI job's own Docker network for the test run. Not a credential for any real environment. No action needed.

**Vulnerable Dependencies:**

| Package | Current | Recommended | Severity | Notes |
| :--- | :--- | :--- | :--- | :--- |
| `undici` (transitive, via `jsdom` → Vitest devDependency chain) | 7.28.0 | ≥7.29.0 (or whatever `npm audit fix` resolves to) | High | 5 advisories: response desync via retry interceptor, cross-user cache info disclosure (×2), CRLF injection via blob body type, cookie attribute injection. **Test-only** — `jsdom`/Vitest never ship to production, so production blast radius is zero, but CI/dev-machine risk is nonzero if untrusted content is ever fetched during tests. Fix: `npm audit fix` in `frontend/`. |

`npm audit --omit=dev` (production dependency tree only) reports **0 vulnerabilities** — the only two direct runtime dependencies are `react`/`react-dom`.

Backend NuGet packages (`Microsoft.AspNetCore.*`, `Microsoft.EntityFrameworkCore.*` at 10.0.10, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `NUnit` 4.6.1) are all current .NET 10 releases; Dependabot (`.github/dependabot.yml`) is active and has already landed several bump PRs in the visible git history (`#100`, `#102`, `#150`). No stale/deprecated packages were identified. (No `dotnet list package --vulnerable` was run — `dotnet` CLI is not installed in this analysis environment — so this is a version-currency check, not a live advisory check.)

**Risky OWASP-Top-10 Patterns:**

- **SQL injection:** No `FromSqlRaw`/`ExecuteSqlRaw`/`FromSqlInterpolated` usage found anywhere — all data access goes through EF Core's parameterized LINQ query surface.
- **SPARQL injection (Wikidata client):** `WikidataClient.cs`'s nine `Build*IntersectionQuery` methods interpolate Wikidata QIDs directly into SPARQL query strings, which would be a textbook injection point — **but every call site validates the QID against `WikidataQid.IsValid(...)` and throws `ArgumentException` before interpolation** (confirmed at `WikidataClient.cs:175-230` and others). This is correctly defended; flagged here only so the pattern is visible in one place — any *new* `Build*Query` method added to this class must preserve the same `IsValid` guard before interpolating a new caller-supplied value.
- **CORS:** Configured via `builder.Configuration["Cors:AllowedOrigins"]` → `policy.WithOrigins(...)` (`Program.cs:688-697`) — an explicit allow-list from config, not `AllowAnyOrigin()`. No wildcard CORS found.
- **`eval()` / `innerHTML` / `dangerouslySetInnerHTML`:** None in production frontend code. The one `innerHTML` hit is `document.body.innerHTML = ''` in a test's cleanup step (`turnstile.test.ts`), not application code.
- **Auth/JWT:** Password hashing is correctly delegated to Supabase Auth (per ADR-0005/ADR-0017) rather than reimplemented in-app. JWT validation (`Program.cs:1063-1133`) validates issuer, audience, lifetime, and signing key via a JWKS `ConfigurationManager`, with `RequireHttps` enforced whenever the JWKS endpoint is non-localhost. The `local-e2e` auth bypass mode is gated by `builder.Environment.IsDevelopment()` in addition to a config flag (`Program.cs:1025`), so it cannot be accidentally left active in a deployed environment via config alone.

**No P1 security findings.** The codebase demonstrates defense-in-depth thinking in the two places (SPARQL construction, auth mode switching) where a shortcut would have been easy to take.

---

## 4. High-Risk Hotspots (Churn × Complexity Matrix)

Churn = commit count over the repo's full 16-day history (see caveat above — treat as "build-out attention," not "decay signal").

| File / Module Path | Churn (Commits) | Size / Complexity | Priority Level |
| :--- | ---: | :--- | :--- |
| `backend/src/XGArcade.DataSync/Wikidata/WikidataClient.cs` | 20 | 2,034 lines, 1 class, 65 methods; 9 near-identical `Query*IntersectionAsync` + 10 near-identical `Build*Query` methods (duplication cluster) | **P1** |
| `backend/src/XGArcade.Api/Program.cs` | 22 (highest in repo) | 1,245 lines; composition root mixing DI setup, JWT config, CLI-verb dispatch (`--all-clubs` etc.), and Minimal-API endpoint mapping (26 `app.Map*`/`app.Use*` calls) | P2 |
| `backend/tests/XGArcade.DataSync.Tests/Wikidata/WikidataClientTests.cs` | 19 | 3,107 lines — largest file in repo; churns in lock-step with `WikidataClient.cs` (see §5, genuine coupling, expected) | P2 (test-only, size tracks the class it tests) |
| `frontend/src/admin/AdminScreen.tsx` | 7 | 1,432 lines, 16 `useState`, 4 `useEffect` — God Component; partial extraction already underway (`#167`) | P2/P3 |
| `frontend/src/lib/types.ts` | 11 | 620 lines, very dense per-field rationale comments; churns with every new API field | P3 (healthy churn — this is the frontend/backend contract file, expected to move often) |
| `frontend/src/lib/api.ts` | 10 | 1,057 lines, single file for the entire API client surface | P3 |
| `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs` | 12 | 983 lines, 23 methods, deepest nesting of any hand-written file scanned (25 lines at ≥5 indent levels) | P3 |
| `backend/src/XGArcade.Data/Repositories/PlayerStoreRepository.cs` | 10 | 772 lines | P4 |
| `backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs` | 12 | (newer sibling module to Grid; mirrors its shape) | P4 — watch, don't act yet |

Notably **absent** from this table: nothing here is "large *and* untouched *and* buggy" (the classic P2 "leave alone" quadrant) — this codebase is too young to have accumulated that kind of debt yet. Every large file is also a recently- and actively-changed file, which is the expected shape for a 16-day-old project mid-build.

---

## 5. Structural & Architectural Anomalies

### Hidden Coupling

Co-change analysis (commits touching ≤8 files, to filter out large sweeping refactors, pairs co-changed ≥3 times) surfaced only two real clusters, both of which are **expected, not hidden**:

- `WikidataClient.cs` ↔ `WikidataClientTests.cs` (co-changed 4×) — implementation and its test file changing together is exactly what should happen.
- `docs/CHANGELOG.md` ↔ `docs/requirements-document.md`/`docs/backlog.md`/`docs/architecture-document.md` — mandated by `CLAUDE.md`'s "after finishing work" workflow, which requires a CHANGELOG entry whenever a doc changes. This is process working as designed, not leaky coupling.

A second pass specifically checked for **architecture-boundary-violating** coupling — i.e., `XGArcade.Core` changing together with `XGGrid`/`XGPath` game-module internals *without* going through `IGameModule`, which ADR-0003 explicitly forbids. Cross-module co-commits do exist (`Api`+`Data`+`XGGrid`/`XGPath` together, 5× each; `Api`+`Core`+`Data`+`XGGrid`+`XGPath` together, 3×), but these are consistent with the expected shape of a modular monolith where the API layer legitimately orchestrates Core, Data, and a game module in the same feature commit. Only 1–2 commits touched `Core` and a game module together without `Api` in between; at this volume it isn't distinguishable from noise, and no specific commit was found that added a game-specific type reference into a `Core` entity (the concrete thing ADR-0003 prohibits). **No boundary violation found**, but this is worth a spot-check again once churn volume is higher and the signal is statistically meaningful — `git log -p` on `Core`+game-module co-commits at that point, filtered to whether `Core` entities gained game-specific fields.

### Duplication Clusters

- **`WikidataClient.cs`'s query-builder family** (see §4) is the clearest duplication cluster in the codebase: 9 `Query*IntersectionAsync` methods with near-identical shape (validate QIDs → build query string → call `RunIntersectionQueryAsync`) and 10 `Build*Query` methods that differ only in which SPARQL properties/QIDs they interpolate. This is very likely deliberate — each method corresponds to a distinct category-pair combination (country×club, trophy×club, team-trophy×national-team, etc.) referenced by its own REQ/ADR, so a naive "collapse into one generic method" refactor would trade explicit, individually-testable, individually-commented query shapes for a harder-to-audit parameterized one. **Candidate abstraction, not a mandate:** a lookup-table/strategy-map keyed by `(CategoryType, CategoryType)` pair that stores just the differing SPARQL clause + which timeout tier applies, with a single shared `Query*IntersectionAsync` driver, would cut the ~1,500 lines this pattern occupies substantially while keeping each clause's rationale comment attached to its table entry. Worth doing the next time a 10th category pair is added (when the marginal cost of copy-pasting the 10th near-identical method pair exceeds the cost of the refactor).
- No other significant exact/near-duplicate logic clusters were found outside this one file.

---

## 6. Concrete Action Plan for P1 Targets

### P1-1: `WikidataClient.cs` — size + churn + duplication hotspot

**Root Cause / Risk:** The file grew by accretion — each new category-pair query (country×club, then national-team×club, then trophy×club, …) was added as a fresh copy-pasted method pair rather than parameterized, because each pair genuinely does need its own SPARQL clause and its own REQ/ADR-linked rationale comment. The risk isn't a live bug; it's that the file's size (2,034 lines) and duplication density make it the single most expensive file in the repo to review or extend correctly — a mistake in one `Build*Query` method (e.g. an unvalidated QID interpolation, or a missing `MINUS { ?x wikibase:rank wikibase:DeprecatedRank }` clause that another sibling method has) is easy to introduce and hard to catch by eye across 10 near-identical blocks.

**Proposed Architecture / Fix:**
1. Extract a `IntersectionQuerySpec` record (or similar) holding: the two `CategoryType`s it covers, a delegate/template for the SPARQL clause body, and which timeout tier (`WikidataQueryTimeoutTier`) applies.
2. Build a static, testable registry (`Dictionary<(CategoryType, CategoryType), IntersectionQuerySpec>` or a small ordered list) populated once, at the bottom of the file or in a sibling `IntersectionQuerySpecs.cs`, with each entry carrying the same REQ/ADR-tagged comment the current method already has.
3. Replace the 9 public `Query*IntersectionAsync` methods with a single `QueryIntersectionAsync(CategoryType a, CategoryType b, string qidA, string qidB, ...)` that looks up the spec, validates both QIDs via the existing `WikidataQid.IsValid` guard (keep this check centralized, not per-method — this is also a correctness improvement, since a spec-table version can't forget the guard for a new pair the way a copy-pasted method could), and calls the existing `RunIntersectionQueryAsync`.
4. Keep the public method names as thin call-through wrappers if any external caller depends on the specific method names (check `GridGameModule.cs`/`XGPathGameModule.cs` call sites first) — or update callers to the unified signature in the same change, whichever `architecture-reviewer` prefers given `IGameModule`'s existing call shape.
5. This is purely internal to `XGArcade.DataSync` — it doesn't cross the `IGameModule`/Core boundary, so no ADR is needed per ADR-0003's scope, but per `CLAUDE.md`'s own rule ("a choice that could reasonably have gone another way… needs an ADR"), a short ADR *is* warranted here since collapsing 9 explicit methods into 1 parameterized one is exactly this kind of structural, reversible-but-not-obviously-reversible choice.

**Verification Strategy:** `WikidataClientTests.cs` (3,107 lines) already has close to 1:1 coverage of every `Build*Query`/`Query*IntersectionAsync` pair — before refactoring, confirm each of the 9 existing query shapes has an assertion on its exact generated SPARQL string (not just "returns non-null"), since that's what will catch a spec-table transcription error. Refactor one query pair at a time (not all 9 at once), running the full `WikidataClientTests.cs` suite after each, and diff the generated SPARQL string byte-for-byte against the pre-refactor version for all 9 pairs before deleting the old methods — this guarantees zero behavioral change even though `dotnet test` itself could not be executed in this analysis environment.

### P1-2: `undici` high-severity transitive dependency

**Root Cause / Risk:** `jsdom` (a `devDependency` of Vitest's DOM environment) pins `undici` in the `^7.25.0` range, which resolved to 7.28.0 — a version with 5 known advisories (response desync, cache/cookie info disclosure, CRLF injection). Because this is strictly a `devDependency` chain (`jsdom` is never bundled into the production frontend build), the risk is scoped to the local dev/CI environment during test runs, not to deployed users. Low actual exploitability here, but it's a one-command fix with no reason to defer.

**Proposed Fix:** `cd frontend && npm audit fix`, then re-run `npm audit` to confirm 0 vulnerabilities, and re-run `npm run test` to confirm `jsdom`'s newer `undici` doesn't change any test behavior (unlikely, since it's an HTTP client library and tests don't make real network calls in the jsdom environment, but worth the 30 seconds).

**Verification Strategy:** `npm audit` output going from "1 high severity vulnerability" to "found 0 vulnerabilities" is itself the verification; follow with a full `npm run test` pass to catch any incidental breakage from the transitive version bump.

---

## Appendix: What Was Not Assessed

- **`dotnet build` / `dotnet test`:** the `dotnet` CLI is not installed in this analysis environment, so no build correctness check, no actual test-pass/fail status, and no `dotnet list package --vulnerable` NuGet advisory check were performed. Package currency was checked by hand against `.csproj` version strings only.
- **True cyclomatic/cognitive complexity numbers:** no AST-based complexity tool (e.g. a Roslyn analyzer, `eslint-plugin-complexity`) was available; complexity findings above are based on manual review plus heuristics (method count, nesting-depth line counts, file size) rather than computed McCabe/cognitive-complexity scores.
- **Temporal coupling and code-age/decay, per the brief's specified 6–12 month window:** not possible — the repository is 16 days old in total (see caveat at top of document).
