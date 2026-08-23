# Changelog

This log tracks changes that affect the requirements, architecture, or
implementation documents — not every commit. If a change updates one of the
docs under `docs/`, add an entry here in the same iteration.

**Archiving policy:** entries older than 6 months move to
`docs/CHANGELOG-archive.md` (not yet created — create it when the first
archiving pass happens). Keep this file scannable rather than letting it
grow indefinitely.

Format: `YYYY-MM-DD — [docs touched] — one-line summary — REQ/ADR refs`

## Unreleased

- 2026-08-23 — `docs/requirements-document.md` — REQ-513 marked
  `Status: Implemented` after the quality gate ran: `architecture-reviewer`
  confirmed the boundary/ADR question (ADR-0086); `quality-architect`
  found one real code-health-budget issue (a third duplicate
  `CapturingLoggerProvider` test double, past the rule-of-three
  threshold — extracted to a shared
  `XGArcade.Api.Tests/CapturingLoggerProvider.cs`) and two test-coverage
  gaps (missing `NormalizedFullName` re-derivation assertion after a
  `FullName` refresh — the column REQ-208 guess-matching actually
  queries, directly relevant to issue #239's own scenario; and an
  overclaiming "no-op" test assertion, fixed with a call-counting spy
  proving `UpdatePlayerAsync` is genuinely skipped when nothing
  changed) — all fixed. Test suite not compiler-verified in this
  sandbox (no `dotnet` SDK available); must be confirmed in CI before
  merge.
- 2026-08-23 — `docs/requirements-document.md`, `docs/architecture-document.md`,
  `docs/decisions/0086-admin-player-wikidata-refresh-narrow-exception.md`
  (new) — fixed GitHub issue #239 (a garbled player name, frozen in at
  creation from a bad Wikidata snapshot, was shown to a player as the
  "correct answer" on a locked xG Path puzzle, with no way to correct it).
  Added REQ-513: an admin-only `POST /admin/players/{id}/refresh-from-wikidata`
  that re-queries Wikidata by a `Player`'s existing `WikidataQid` and
  updates `FullName`/`Position`/`BirthYear`/`PhotoUrl` per-field where the
  fresh value differs (a missing/null fetched value never overwrites).
  This is the first-ever exception to REQ-1207's "these four fields are
  set once at creation, never re-synced" rule, so it's deliberately narrow
  (admin-triggered only, one player per call, no admin-supplied
  name/QID) and recorded in ADR-0086 — re-applies ADR-0032's existing
  "Wikidata trusted by default" model rather than adding a review step,
  since the goal is closing the "no correction path" gap, not reopening
  that trust decision. `architecture-document.md` §5/§10 updated (COMP-06
  row, new ADR-0086 table row).
- 2026-08-23 — `docs/CHANGELOG.md` only (no REQ/ADR/architecture-document
  change — this is CI-only and doesn't touch a component boundary) — Added
  path filtering to `ci.yml`/`deploy.yml` so doc-only changes
  (`docs/**`, root `*.md`, `.claude/**`, `mockups/**`) skip the full
  backend/frontend/E2E test run and the build-push-deploy pipeline.
  `deploy.yml` (push-to-`main`, not a required check) got a plain
  `paths-ignore` on its `push` trigger. `ci.yml`'s three jobs
  (`backend-tests`/`frontend-unit-tests`/`e2e-tests`) are confirmed
  required status checks for `main`'s branch protection, so a
  workflow-level `paths-ignore` there would leave those checks stuck
  pending forever on a doc-only PR and block auto-merge — instead, a new
  leading `changes` job (`dorny/paths-filter`) makes the three jobs
  conditional (`if: needs.changes.outputs.code == 'true'`) so the
  workflow still triggers and each required job still posts a (skipped,
  green) result.
- 2026-08-23 — `docs/decisions/0072-split-generate-round-workflow-per-gamekey.md`,
  `docs/backlog.md` — S-176: extracted the byte-identical `generate_round()`
  retry-with-backoff bash function (3 attempts, 30s/60s backoff,
  `::warning`/`::error` annotations), previously duplicated in
  `generate-grid-round.yml` and `generate-path-round.yml`, into a
  composite action (`.github/actions/trigger-round-generation/action.yml`).
  `architecture-reviewer` confirmed this is compatible with ADR-0072's
  decoupling intent before implementation: a composite action has no
  `on:`/trigger surface of its own, so each workflow's cron and
  `workflow_dispatch.round_duration_hours` input stay fully independent —
  only the retry-loop bash body is shared, mirroring ADR-0085's
  composite-action-vs-reusable-workflow reasoning for S-175's sibling
  problem. Updated ADR-0072's "Consequences"/"For AI agents" sections to
  record the resolved duplication trade-off and to carve composite actions
  out of its "no shared/reusable workflow or matrix" prohibition. Not
  verified against real GitHub Actions in this sandbox — see S-176's
  "Built as" note.
- 2026-08-23 — `docs/backlog.md` — S-174 follow-up: the Azure AD
  federated-credential gap flagged earlier the same day is resolved. Root
  cause of the persistent `AADSTS700213` failure (it didn't clear after
  the user first added a credential, ruling out propagation delay) was
  that Azure's "Add credential" wizard auto-generates an ID-qualified
  subject (embedding the org/repo numeric IDs) rather than the plain
  name-based subject GitHub's OIDC token actually presents for this repo
  — fixed by manually overriding the Subject identifier to the plain
  form. Verified via a second scratch PR (#258, closed without merging):
  `az bicep build`, `Azure login (OIDC)`, and `az deployment group
  validate` all green. S-174 is now fully verified end-to-end against
  real Azure. Updated S-174's "Built as" note with the resolution and a
  general note for future federated-credential setups.
- 2026-08-23 — `docs/decisions/0085-run-cli-verb-composite-action.md` (new),
  `docs/backlog.md` — S-175: extracted
  `.github/actions/run-cli-verb/action.yml`, a composite action (not a
  `workflow_call` reusable workflow — ADR-0085) sharing the checkout/
  setup-dotnet/run-a-CLI-verb/dev-DB-connection-string shape duplicated
  across `backfill-player-photos.yml`, `import-player-name-index.yml`,
  `prefetch-player-careers.yml`, `purge-game-history.yml`,
  `purge-player-pool.yml`, `warm-grid-cache.yml`, and `deploy.yml`'s
  `migrate-and-seed-database` job (7 real sites; `ci.yml`'s similarly-named
  step was examined and deliberately left unconverted — see ADR-0085 and
  S-175's "Built as" note for why). Each caller keeps its own
  `actions/checkout@v7` step (a GitHub Actions constraint, not leftover
  duplication) and its own `on:`/cron/`timeout-minutes`.
- 2026-08-23 — `.github/workflows/validate-bicep.yml` (new), `docs/backlog.md`
  — S-174: added a CI-only Bicep validation gate on every PR touching
  `infra/bicep/**` — `az bicep build` (catches broken module paths/syntax
  errors with no Azure login needed) followed by `az deployment group
  validate` against the real dev resource group (dev's real secrets, no
  mutation). `deploy.yml`'s actual `az deployment group create` deploy step
  is unchanged. Verified via real GitHub Actions runs on a scratch PR
  against `main` after merge (this sandbox has no `az` CLI): the `az bicep
  build` layer is fully verified both ways (red on a deliberately-typo'd
  module path, green once fixed). The `az deployment group validate` layer
  is implemented correctly but currently blocked by an Azure AD federated-
  credential gap — the `AZURE_CLIENT_ID` app registration doesn't yet trust
  the `pull_request` OIDC subject, only `deploy.yml`'s push-to-`main`
  subject — a one-time Azure Portal fix, not a code change; see S-174's
  "Built as" note for the exact steps. Also updated PR #253's description
  with both run links once verified.
- 2026-08-23 — `infra/bicep/main.parameters.json`, `docs/backlog.md` —
  S-173: deleted the unreferenced, generic-template
  `infra/bicep/main.parameters.json` (`environmentTag: "prod"`), matching
  Epic 10/S-130's "delete now, cheap to re-add at Tier 1" precedent for the
  same leftover-Tier-1-scaffold shape. `infra/README.md`'s and `SETUP.md`'s
  "does not exist yet" wording is now accurate and needed no change; added
  S-173's "Built as" note recording the `architecture-reviewer` decision
  and reasoning.
- 2026-08-23 — `docs/architecture-document.md`, `docs/backlog.md` — S-172:
  fixed the stale COMP-07 row claim that the by-QID/by-nationality/by-club/
  familiarity Wikidata query methods "still hand-roll their own HTTP
  handling" with an open pointer to Epic 9. Verified against current
  `backend/src/XGArcade.DataSync/Wikidata/WikidataClient.cs` that every one
  of those methods is a thin wrapper over the shared `RunThrowingQueryAsync`
  driver (S-118/S-124/S-155), same as the 9 intersection queries' own
  `RunIntersectionQueryAsync` path, both built on
  `SparqlQueryBuilders.cs`/`SparqlResponseParsers.cs`; Epic 9 is fully
  closed. Rewrote the sentence to describe the current fully-centralized
  state and dropped the stale Epic 9 pointer; added S-172's "Built as" note.
  Doc-only change, no REQ/ADR/code touched.
- 2026-08-23 — `docs/backlog.md` — S-171: backfilled the missing "Built as"
  notes for S-168 (`frontend/src/lib/apiClient.ts`'s shared `apiRequest<T>`
  helper) and S-169 (`frontend/src/lib/useRoundFetch.ts`'s shared
  `useRoundFetch`/`useAutocompleteWarmup` hook), both confirmed shipped and
  already fully documented in this file's own 2026-08-23 entries but never
  given a "Built as" note in `docs/backlog.md` itself. Sourced from those
  existing CHANGELOG entries and current code (file paths, line counts
  re-verified: `apiClient.ts` 102 lines, `useRoundFetch.ts` 138 lines), not
  re-investigated from scratch. Doc-only change, no code touched, no tests
  to run.
- 2026-08-23 — `docs/backlog.md` — S-170: removed the two unused
  `ILogger<T>` constructor parameters left over from S-119's split
  (`GridGameModule.cs`, `GridLiveLookupDispatcher.cs`), fixing both
  `CS9113` warnings; added S-170's "Built as" note. Pure structural
  removal, no behavior/requirement change, so no REQ/ADR/architecture doc
  update needed.
- 2026-08-23 — `docs/backlog.md` — filed Epic 24 (S-172–S-176), a
  deliberately deeper `code-health-auditor` sweep beyond the usual
  duplicated-block/god-file/churn cadence, in two parts. Part 1 dug into
  the four modules `CODE_HEALTH_ASSESSMENT.md`'s 2026-08-23 revision tied
  at 8.0/10 (`XGArcade.DataSync`, `XGArcade.Games.XGGrid`, `infra/`,
  `docs/`) along new dimensions (test-depth spot-checks, error-handling
  review, doc accuracy, infra fragility) rather than re-running the same
  heuristics; Part 2 was a first-ever dead/unused-code hunt across
  backend, frontend, and infra. Concrete findings: (1) S-172 —
  `docs/architecture-document.md`'s COMP-07 row still claims the
  by-QID/nationality/club/familiarity Wikidata query methods "hand-roll
  their own HTTP handling," which stopped being true across S-118/S-124/
  S-155 (verified directly against current `WikidataClient.cs` — every one
  is now a thin wrapper over the shared `RunThrowingQueryAsync` driver);
  (2) S-173 — `infra/README.md`/`SETUP.md` state
  `infra/bicep/main.parameters.json` ("prod") "does not exist yet," but it
  is present on disk, referenced by nothing (`deploy.yml` only ever uses
  `main.parameters.dev.json`), and matches the exact leftover-Tier-1-
  scaffold shape Epic 10/S-130 already decided to delete-not-patch for five
  sibling workflow files; (3) S-174 — no Bicep validation step
  (`az deployment group validate`/`what-if`) exists anywhere in CI, so a
  broken template is only ever caught at the real `deploy.yml` deploy
  against live dev infra; (4) S-175/S-176 — two genuinely new instances of
  this lineage's recurring "duplicated shape repeated per near-identical
  block" pattern, never previously looked for in `infra/`: the
  checkout+setup-dotnet+dotnet-run-verb+env boilerplate repeated across 8
  CLI-verb-triggering workflow/job sites, and the byte-identical
  `generate_round` retry-with-backoff bash function duplicated verbatim in
  `generate-grid-round.yml`/`generate-path-round.yml`. Everything else
  investigated (DataSync/XGGrid error-handling and test depth,
  `frontend/src/lib/*.ts`'s post-S-168 exports, orphaned CLI verbs/
  workflows, ADR-0029→ADR-0032 supersession completeness, REQ-211's
  `PlayerNameIndex` gate, Tier 1 API-Football scaffolding, `docs/`'s own
  accretion) came back clean and is recorded in Epic 24's own "Findings
  that turned out clean" / "Watch-only" sections rather than turned into
  busywork stories. Epic 22's four merged stories (S-166–S-169) and Epic
  23's status (S-170/S-171 both confirmed still open/unimplemented at
  investigation time) were re-verified first, neither epic touched by
  this pass. Read-only investigation session — no code changed, no commit
  made. (Both S-170 and S-171 merged separately the same day, PRs
  #247/#248 — Epic 23 is now fully closed.)

- 2026-08-23 — `docs/coding-guidelines.md`, `docs/ai/agent-migration-plan.md`,
  `docs/decisions/0084-per-diff-code-health-budget.md`,
  `.claude/agents/quality-architect.md`,
  `.claude/agents/code-health-auditor.md`, `.claude/commands/quality-gate.md`
  — added ADR-0084 and a new "Code health budget" section to
  `coding-guidelines.md` (duplicated-shape rule-of-three,
  sibling-relative god-file/god-class sizing, and a per-touched-file
  churn check), wired into `quality-architect`'s Mode 1 review checklist
  and `/quality-gate`'s step 2, so the patterns `code-health-auditor`'s
  periodic sweeps have repeatedly caught only after the fact (six
  instances of the same duplicated-shape pattern alone, per
  `CODE_HEALTH_ASSESSMENT.md`'s revision history) get a chance to be
  flagged at the diff that introduces them. `code-health-auditor`'s own
  periodic whole-tree scoring/epic-planning is unchanged; this is a
  diff-scoped subset of its heuristics, not a merge of the two agents.
  Requested explicitly to make code health a standing part of per-diff
  development rather than only a periodic sweep concern.

- 2026-08-23 — `docs/backlog.md` — filed Epic 23 (S-170/S-171), a same-day
  follow-up to Epic 22's sweep: S-170 removes two unused `ILogger<T>`
  constructor parameters left over from S-119's `GridGameModule` split
  (`GridGameModule.cs`, `GridLiveLookupDispatcher.cs`, both flagged by the
  compiler as `CS9113`); S-171 backfills the "Built as" notes S-168/S-169
  are missing despite both being confirmed shipped and already fully
  documented in this file's own 2026-08-23 entries. Re-investigated
  `CliVerbDispatcher.cs`'s dispatch-logic test coverage, `AuthController.cs`'s
  churn, and `WikidataClientTests.cs`'s size — all three re-confirmed
  already-settled/still-low-risk and explicitly declined as stories, not
  written up. No code changed this pass (findings/planning only).

- 2026-08-23 — no docs changed beyond this entry — S-169 (`docs/backlog.md`
  Epic 22): extracted `useRoundFetch<TRound extends { roundId: string;
  endTime: string }>(accessToken, fetchFn, onAuthError): { state, setState,
  checkRoundStillLive }` into new `frontend/src/lib/useRoundFetch.ts`,
  covering the `LoadState` union and mount-fetch effect
  `GridScreen.tsx`/`PathScreen.tsx` previously hand-rolled identically
  (loading/empty/error/ready, `roundEndTime` computed once at fetch-success
  time, 401 escalated to `onAuthError`, other errors via `describeError`).
  Folded in `checkRoundStillLive(roundId)` (the shared core of
  `handleViewCompletedRoundLeaderboard`'s live-vs-past leaderboard-scope
  check, REQ-1210/ADR-0083) as a read-only re-fetch-and-compare that
  deliberately never calls `setState` — the pre-extraction code never did
  either, and `GridScreen.test.tsx`'s "reports the 'past' scope..." test
  (a 404 re-check) would otherwise flip the screen to `'empty'` and blank
  out the just-completed round mid-click. Left `warmUpAutocomplete` out as
  a separate `useAutocompleteWarmup(accessToken)` hook in the same file —
  an unrelated effect (no `TRound`/`state` involvement) that would have
  conflated two concerns under one hook name. Each screen keeps its own
  thin `handleViewCompletedRoundLeaderboard` wrapper (owns
  `checkingLeaderboardTarget` and its own `gameKey`). `GridScreen.tsx`'s
  `applyScoredGuess`/`handleSubmitGuess`/`handleResolveDisambiguation` and
  `PathScreen.tsx`'s `puzzleIndex`/`refetchWarning`/`handleSubmitGuess` were
  untouched beyond reading `state`/`setState` from the hook. Two new
  `react-hooks/exhaustive-deps` warnings appeared once `setState` came from
  a custom hook instead of a literal `useState` call in each file
  (oxlint's static check no longer recognized it as stable) — fixed by
  adding `setState` to the affected `useCallback` dependency arrays, since
  React guarantees a `useState` setter's identity is stable. Pure
  structural refactor, no behavior change: `npm run test` 647/647 across 44
  files (including `GridScreen.test.tsx`/`PathScreen.test.tsx` unchanged),
  `npx tsc -b` clean, `npm run lint` (oxlint) clean; no test files added or
  changed. No sibling `useRoundFetch.test.ts` added, matching
  `useAuthedFetch.ts`'s own precedent of no dedicated lib-hook test file —
  its behavior is exercised only via the two screens' own tests.
  `requirements-document.md`/`architecture-document.md`/
  `implementation-document.md` checked against their own `update_when`
  triggers and left unedited — no REQ behavior or COMP boundary changed.
- 2026-08-23 — no docs changed beyond this entry — S-168 (`docs/backlog.md`
  Epic 22): added `apiRequest<T>(accessToken: string | null, path: string,
  init?: RequestInit): Promise<T>` to `frontend/src/lib/apiClient.ts` and
  refactored the 47 existing hand-rolled fetch call sites across
  `frontend/src/lib/admin.ts`, `auth.ts`, `announcements.ts`,
  `leaderboard.ts`, `rounds.ts`, `leagues.ts`, `incidents.ts`, `path.ts` to
  use it instead of each repeating headers-build/fetch/ok-check+
  throwApiError/json()-cast by hand. All 47 call sites' status-code
  special-casing — including the 404-as-data idioms in
  `fetchActiveAdminRound`/`deleteUserByEmail`/`fetchCurrentRound`/
  `fetchAdminAnnouncementBanner`/`fetchCurrentPath` — is preserved verbatim
  via a `try { apiRequest(...) } catch (error) { if (error instanceof
  ApiError && error.status === 404) return sentinel; throw error; }`
  wrapper. `useAuthedFetch.ts` (React-hook-scoped, a deliberately different
  abstraction) and `rounds.ts`'s `warmUpAutocomplete` (deliberately stays on
  raw `fetch`, never wants the throw-on-non-ok behavior) were left
  untouched per the story's own scoping. `quality-architect` review caught
  one fix-now issue during this pass: an earlier draft wrapped
  `response.json()` in a blanket try/catch that would have swallowed parse
  failures indiscriminately across all ~40 typed call sites, not just the 4
  genuinely-void/204 ones — corrected to an explicit `if (response.status
  === 204) return undefined as T` check before parsing, so a real parse
  failure on an otherwise-ok response still throws instead of silently
  resolving to `undefined`. `architecture-reviewer` found no module-boundary
  violation (change confined to `frontend/src/lib/`) and no ADR warranted —
  judged equivalent in kind to S-111's original `apiClient.ts` split, which
  also had no ADR. Pure internal refactor, no behavior change: `npm run
  test -- --run` 647/647 across 44 files, `npx tsc -b` clean, `npm run lint`
  (oxlint) clean; no test files added or changed, matching the story's own
  stated acceptance criteria (existing tests exercise these functions only
  via mocked `fetch` at each component's boundary). `requirements-document.md`,
  `architecture-document.md`, and `implementation-document.md` checked
  against their own `update_when` triggers and left unedited — none of the
  three mention `apiClient.ts`/`frontend/src/lib/` today, no REQ behavior or
  COMP boundary changed.
- 2026-08-23 — `docs/backlog.md` (S-166 "Built as" note) — implemented
  Epic 22's S-166: extracted the shared "check cache -> confirmed-low-
  from-sweep -> confirmed-low -> persistent-failure -> live lookup"
  decision tree out of `PlayerCacheWarmingService.WarmAsync`'s two
  ~100-line near-identical Country×Club/Club×Club loops, into a shared
  generic `SweepPairsAsync<TLeft, TRight>` core plus thin
  `SweepCountryClubPairsAsync`/`SweepClubClubPairsAsync` wrappers supplying
  each sweep's own delegates (attribute type/name selectors, which
  `IWikidataLookupService` method to call, each log line's exact wording,
  the failing-pair label) — matching S-165's own generic-delegate
  parameterization (checked S-165's landed code in
  `PlayerCareerPrefetchService.cs` first, per this story's own flag; note
  that file lives in a different project, `XGArcade.DataSync`, not "one
  directory over" as the story text put it — a `quality-architect` review
  correction that doesn't affect the fix, both are private in-class
  helpers with no boundary crossed). Running totals (`SweepPairsOutcome`)
  are threaded through both sweep calls the same "starting totals continue"
  shape S-165 established, so `LogProgressCheckpoint` and the final summary
  still see cumulative counts across both loops, not two separate ones.
  `backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs` went
  388 → 367 lines (`git diff --stat`: 169 insertions/190 deletions) — an
  initial pass came in net +10 lines from duplicating the original
  per-branch REQ-110/ADR-0078 rationale comments into the new shared
  helper; trimmed those to one-liners since the class-level doc comment
  already covers the full rationale, which fixed both the line-count
  regression and got under this story's own accept criterion. Pure
  structural refactor, no behavior change — `PlayerCacheWarmingServiceTests.cs`
  (775 lines/28 tests) unchanged and passing, full backend suite (1,616
  tests across 6 projects) green. `architecture-reviewer` and
  `quality-architect` both reviewed and passed with zero blocking findings;
  two non-blocking nits from `quality-architect` (a stale "each loop below"
  comment reference now pointing at the single shared `SweepPairsAsync`,
  and `new SweepPairsOutcome()` instead of a bare `default` for the first
  sweep's starting totals) were applied. No ADR — same "private-method-shape
  territory below ADR granularity" reasoning `architecture-reviewer` gave
  for S-165. `docs/requirements-document.md`/`docs/architecture-document.md`
  checked against the diff and confirmed unaffected — no REQ acceptance
  criteria or COMP-xxx boundary changed, neither doc names
  `SweepPairsAsync`/the two-loop shape at the method-signature level.
  Verified with a real `dotnet` SDK in this session (10.0.111, installed
  via apt — the repo targets `net10.0`). S-166.
- 2026-08-23 — no docs changed beyond this entry — S-167 (`docs/backlog.md`
  Epic 22, direct follow-up to S-114's own `BuildDbContext()`/
  `BuildLoggerFactory()` extraction): extracted the Wikidata-client bootstrap
  five of `CliVerbDispatcher.cs`'s handlers repeated inline — `new
  HttpClient()` → `WikidataHttpClientConfiguration.Configure(...)` → `new
  WikidataClient(...)` — into a single private static
  `BuildWikidataClient(ILoggerFactory loggerFactory, TimeSpan? queryTimeout =
  null)` helper, called from `HandleWarmPlayerCacheAsync`,
  `HandleImportPlayerNameIndexAsync`, `HandleBackfillPlayerPhotosAsync`,
  `HandleBackfillPlayerPositionBirthYearAsync`, and
  `HandlePrefetchPlayerCareersAsync`. The two explicit `queryTimeout:
  TimeSpan.FromSeconds(60)` overrides (import-player-name-index,
  prefetch-player-careers) and their original multi-paragraph justification
  comments are preserved verbatim. `HandleAuditClubGapsAsync`/
  `HandleVerifyWikidataPlayerDataAsync` and the rest, which never construct a
  `WikidataClient`, are untouched. Pure structural refactor — no behavior
  change, no ADR (same "no new boundary crossed" reasoning as S-114,
  confirmed by `architecture-reviewer`). Added one doc-comment sentence on
  the new helper explaining the deliberately-undisposed `HttpClient` is safe
  because every caller is a one-shot CLI verb process (`quality-architect`
  review finding). Verified with a real `dotnet` SDK in this session (10.0.111,
  installed via apt — the repo targets `net10.0`): full backend suite green,
  1616/1616 across all 6 test projects (`XGArcade.Core.Tests` 205,
  `XGArcade.Data.Tests` 302, `XGArcade.DataSync.Tests` 370,
  `XGArcade.Games.XGGrid.Tests` 129, `XGArcade.Games.XGPath.Tests` 223,
  `XGArcade.Api.Tests` 387), plus a hand-traced check (independently
  cross-checked by both `architecture-reviewer` and `quality-architect`)
  confirming each of the 5 call sites reproduces its original
  `loggerFactory`/`queryTimeout` arguments exactly. No dedicated
  `CliVerbDispatcherTests.cs` — deliberate per S-113, not a coverage gap.
- 2026-08-23 — `docs/backlog.md` (S-165 "Built as" note) — implemented
  Epic 21's S-165: extracted the shared "fetch pool -> mark swept ->
  skip-empty -> dedup+chunk" shape out of `PlayerCareerPrefetchService
  .PrefetchAsync`'s two ~90-line near-identical country/club sweep loops,
  into a shared generic `SweepAsync<TRow>`/`SweepPoolAsync` core plus thin
  `SweepCountriesAsync`/`SweepClubsAsync` wrappers supplying each sweep's
  own delegates (fetch call, mark-swept write, log wording). The club
  sweep's deliberate `club.Name` (never `clubNameByClubQid`) attribute-value
  sourcing — the 2026-08-18 quality-gate-fix nuance — was preserved
  verbatim via `SweepClubsAsync`'s own `getName` selector, unchanged.
  `backend/src/XGArcade.DataSync/Wikidata/PlayerCareerPrefetchService.cs`
  went 408 → 404 lines (`git diff --stat`: 173 insertions/177 deletions).
  Pure structural refactor, no behavior change — `PlayerCareerPrefetchServiceTests.cs`
  byte-unchanged, full backend suite (1,616 tests across 6 projects) green.
  `architecture-reviewer` and `quality-architect` both reviewed and passed
  with zero findings; per `architecture-reviewer`, the wrapper-methods-over-
  shared-core shape is private-method-shape territory below ADR granularity
  (this story's own flagged "could reasonably go another way" call), so no
  ADR was written for it. `docs/requirements-document.md` and
  `docs/architecture-document.md` checked against the diff and found
  unaffected — no REQ acceptance criteria and no COMP-xxx boundary changed;
  neither doc names `SweepAsync`/`SweepPoolAsync`/the two-loop shape at the
  method-signature level, so nothing there needed updating either. S-165.
- 2026-08-23 — `CODE_HEALTH_ASSESSMENT.md`, `CODEBASE_ANALYSIS.md`,
  `docs/backlog.md` (new Epic 22, S-166–S-169) — periodic whole-codebase
  health sweep (`code-health-auditor`), deliberately widened past the
  single-finding-per-sweep cadence Epics 17/21 used: every module still
  below ~9.0 in the 2026-08-22 revision was re-read, and backend/frontend/
  infra were each searched past the #1 hotspot for the same duplicated-
  block/god-file/weak-coverage/boundary-smell patterns this lineage has
  already caught before. Epic 21's S-165 (`PlayerCareerPrefetchService.cs`)
  re-confirmed still accurate against current code/`git log` — unchanged,
  still open, not re-investigated. Four new findings, all instances of the
  same "duplicated shape repeated per near-identical block" pattern at
  different sites: `PlayerCacheWarmingService.cs`'s Country×Club/Club×Club
  sweep loops (`backend/src/XGArcade.Games.XGGrid`, S-166 — the third
  occurrence of this specific shape in the codebase); `CliVerbDispatcher.cs`'s
  per-handler Wikidata-`HttpClient` bootstrap, duplicated across 5 of its 14
  handlers (`backend/src/XGArcade.Api`, S-167 — a narrower finding one level
  below the verb-registry shape Epics 17/21 already confirmed healthy);
  `frontend/src/lib/*.ts`'s 47 `fetch`+`throwApiError`+`json()` call sites
  duplicated across 8 domain files, only visible aggregated (S-168); and
  `GridScreen.tsx`/`PathScreen.tsx`'s duplicated `LoadState`/round-fetch/
  autocomplete-warm-up/`handleViewCompletedRoundLeaderboard` machinery
  (S-169). No code changed this pass (findings/planning only) — overall
  system score moved 8.1→7.9 in `CODE_HEALTH_ASSESSMENT.md`, reflecting
  newly-counted pre-existing complexity, not a regression. `npm run test`
  (647/647, 44 files), `tsc -b`, `oxlint` all re-ran live and clean
  (existing `node_modules/`); `npm audit` unchanged (`nanoid@<3.3.18`,
  dev-only). No `dotnet` SDK in this sandbox, confirmed again — every
  backend-touching story (S-166/S-167, plus the still-open S-165) needs a
  session with real `dotnet test` access.
- 2026-08-22 — `CODE_HEALTH_ASSESSMENT.md`, `CODEBASE_ANALYSIS.md`,
  `docs/backlog.md` (S-154 "Built as" note backfilled; new Epic 21, S-165)
  — periodic whole-codebase health sweep (`code-health-auditor`). Verified
  Epic 17's actual completion state against `git log`/direct file
  inspection before scoring anything: all 5 stories (S-154–S-158) confirmed
  shipped. Found and fixed one doc-drift directly: S-154's own
  `docs/backlog.md` entry was missing its "Built as" note despite the
  `IPathEligibilityService`/`PathEligibilityService` extraction, ADR-0082,
  and the architecture/requirements doc-sync having all genuinely landed
  (2026-08-22, earlier in this file) — backfilled from those existing
  sources rather than re-investigated from scratch. One new finding this
  pass: `backend/src/XGArcade.DataSync/Wikidata/PlayerCareerPrefetchService.cs`'s
  country-sweep and club-sweep loops (highest churn in `XGArcade.DataSync`,
  8 commits since 2026-08-11) have grown into a near-identical ~90-line
  duplicated shape since S-127 added the club loop as a deliberate mirror
  of the original (ADR-0069) — the same "duplicated block repeated per
  near-identical case" pattern this sweep lineage already fixed in
  `WikidataClient.cs` (Epic 7) and `GridGameModule.cs` (Epic 9). Filed as
  `docs/backlog.md` Epic 21 S-165 (extract a shared sweep helper,
  preserving each loop's real behavioral nuance verbatim) rather than fixed
  directly — nontrivial, and this sandbox has no `dotnet` SDK to verify a
  backend refactor against. No mechanical fixes applied to application code
  this pass (only documentation). Checked `docs/architecture-document.md`/
  `docs/requirements-document.md` for the dated-narrative accretion pattern
  again (`awk '{print length, NR}'`) and found both still clean — longest
  `architecture-document.md` cell is now COMP-01 at 2,002 characters,
  proportional to that component's genuine scope, not runaway narrative.
  Overall system health score: 7.6/10 → 8.1/10 (see
  `CODE_HEALTH_ASSESSMENT.md`'s own revision history for the full
  per-module/component breakdown). All frontend tooling ran live and clean
  after a fresh `npm install`: `npm run test` 647/647 (44 files, up from
  584/584), `tsc -b` clean, `oxlint` clean. `npm audit` re-run: the
  `nanoid@<3.3.18` dev-only advisory first seen 2026-08-18 is unchanged,
  still Dependabot's routine-drift lane. No `dotnet` SDK available in this
  sandbox, confirmed again — no backend build/test run. No boundary
  violations found; no new ADR needed by this sweep itself.
- 2026-08-22 — `docs/backlog.md` (S-158 "Built as" note added) — extracted
  `frontend/src/App.tsx`'s self-contained auth-session lifecycle
  (`accessToken`/`currentUser` state, `isGuest`, `handleAuthenticated`,
  `handleLogout`, `attemptSilentRefresh`, the `fetchMe` effect) into a new
  `useSession()` hook (`frontend/src/lib/useSession.ts`), mirroring
  `useThemePreference`'s (`frontend/src/lib/theme.ts`) hook-module style.
  Pure extraction, no behavior change: `App.tsx` shrank from 649 to 529
  lines and keeps only routing/dialog state, calling `useSession()` once at
  the top and passing it an `onLoggedOut` callback for the three
  routing-specific side effects `handleLogout` used to do inline (reset
  `screen`, hide `AuthScreen`, clear the URL hash). Full frontend suite
  (647/647) passes unchanged; `oxlint`/`tsc -b` both clean. No REQ/ADR
  changed — this is an internal refactor of already-documented behavior
  (REQ-504/715/718/719/721).
- 2026-08-22 — `docs/backlog.md` (S-157 "Built as" note added) — migrated
  `frontend/src/admin/AdminScreen.tsx` off its hand-rolled
  `Promise.allSettled` fetch-on-mount effect onto two independent instances
  of the shared `useAuthedFetch` hook (`frontend/src/lib/useAuthedFetch.ts`,
  S-120), one per endpoint (unverified player data; the active-round
  probe), mirroring the pattern already used by sibling admin
  subcomponents (`AccountMetricsSection.tsx`, `XGPathCycleSection.tsx`,
  etc.). Refetch granularity was preserved (each endpoint still refetches
  independently via its own `refetch`), and the pre-existing page-wide
  403 → "You don't have access to this page." behavior (REQ-504/505,
  SCREEN-04) and the active-round probe's swallow-non-401/403/404-to-null
  behavior (REQ-505/506) were both carried over unchanged. One new test
  was added to `frontend/src/admin/AdminScreen.test.tsx` closing a
  coverage gap found in review: the active-round probe's "swallow any
  non-401/403/404 failure (e.g. a 500) to null rather than escalating to a
  page-wide error" boundary had no direct test before this. `GridScreen.tsx`
  and `PathScreen.tsx` — the other two candidates S-157 named — were
  evaluated and deliberately left out of scope: both need to mutate their
  fetched state after a guess submission, which `useAuthedFetch` doesn't
  support (it exposes no setter), so migrating them isn't a drop-in change
  like `AdminScreen.tsx` was. No behavior changed: all 38 pre-existing
  `AdminScreen.test.tsx` tests pass unchanged plus the 1 new test (39/39);
  full frontend suite 647/647 passing; `oxlint`/`tsc -b` clean.
  `docs/requirements-document.md`, `docs/architecture-document.md`, and
  `docs/implementation-document.md` were all checked against their
  `update_when` triggers and need no change (internal refactor of
  already-documented behavior, REQ-504/505/506, SCREEN-04, not new/changed
  behavior) — no ADR needed either (sixth consumer of the already-established
  S-120 pattern, not a new structural decision); `architecture-reviewer` and
  `quality-architect` both reviewed and passed the diff.
- 2026-08-22 — `docs/backlog.md` (S-156 marked SHIPPED) — implemented
  S-156 (Epic 17): backfilled dedicated test files for the 4 remaining
  `AdminScreen.tsx` subcomponents S-108/S-109 left covered only
  indirectly (`GuestClearSection.test.tsx`, `RoundControlSection.test.tsx`,
  `UnverifiedDataSection.test.tsx`, `UserDeletionSection.test.tsx`), each
  rendering its component directly and stubbing only the routes that
  component itself calls, mirroring the S-108 batch-1 shape.
  `AdminScreen.test.tsx` was trimmed of the now-redundant per-subcomponent
  cases (unlike S-108's "left unchanged" choice) but keeps its own
  composition/wiring coverage — fetch-on-mount, a real (not mocked)
  `onRefresh` round-tripping through `UnverifiedDataSection` and
  `RoundControlSection`, and the activeRound-gated show/hide of
  `RoundControlSection`+`UserDeletionSection` — with one composition case
  (`RoundControlSection`'s real-`onRefresh` coverage) restored in a
  follow-up commit after a `quality-architect` review found it had been
  dropped rather than migrated. Pure test-coverage addition — no
  production code, REQ, component boundary, or data-model change; full
  frontend suite went 613 → 646 tests (44 files), all passing, `tsc -b`/
  `oxlint` clean. `docs/requirements-document.md`,
  `docs/architecture-document.md`, and `docs/implementation-document.md`
  were all checked against their `update_when` triggers and need no
  change — no ADR needed either (test-infrastructure addition, not a
  structural decision).
- 2026-08-22 — `docs/requirements-document.md` (v1.94 → v1.95, new §4.13
  "Cross-game player experience" with REQ-1210 + an unresolved §7 product
  question on replay-on-revisit), `docs/design-document.md` (v0.72 →
  v0.73, new `SCREEN-12: Round-completion banner` section + a settle-in
  named-animation paragraph in §2), `docs/decisions/0083-round-completion-client-side-signal-and-navigation.md`
  (new — numbered 0083, not 0082, after rebasing onto `main`'s own
  independently-created ADR-0082 for the xG Path eligibility-service
  split below), `docs/backlog.md` (S-164 added, SHIPPED) — implements
  REQ-1210: a completion animation, generic across every game (xG Grid,
  xG Path today), shown once a player's own guessing activity locks every
  cell available to them in a round, showing their current points for
  that round and a link straight to that round's live-or-closed
  leaderboard for that specific game. Frontend-only — new
  `frontend/src/lib/roundCompletion.ts` (game-agnostic
  `computeRoundCompletion`/`useCompletionTransition`) and
  `frontend/src/components/RoundCompletionBanner.tsx`, wired into
  `GridScreen.tsx`/`PathScreen.tsx` and threaded through `App.tsx`'s
  existing hash-based screen-switch mechanism (no `react-router`, no new
  route) into `LeaderboardScreen`/`PastRoundsLeaderboard`/`LiveLeaderboard`
  via new optional `initial*` props, per ADR-0083, which records the two
  structural decisions: completion/current-points computed entirely
  client-side from data both games' existing current-round responses
  already return (no backend/`IGameModule` change, so it never crosses
  the ADR-0003 boundary), and the leaderboard link is in-memory
  navigation state rather than a URL route (explicitly reasoned against
  ADR-0039's own "add react-router" follow-up trigger — not superseding
  it). `docs/architecture-document.md` was checked (architecture-reviewer
  review, independently sanity-checked here) and needs no change: nothing
  crosses a Core/game/component boundary — this is new frontend
  state/component composition entirely inside the already-described
  CONT-01 "Web Frontend" container, against two endpoints
  (`GET /rounds/current`, `GET /path/current`) whose response shapes are
  unchanged.
- 2026-08-22 — `docs/architecture-document.md` (v1.10, COMP-11 row + ADR
  mapping table), `docs/requirements-document.md` (v1.95, REQ-1201/1203
  prose and test-level references) — doc-sync for S-154 (Epic 17):
  `XGPathGameModule.GetEligiblePlayerIdsAsync`/`IsEligible` and their
  supporting constants were extracted into a new
  `IPathEligibilityService`/`PathEligibilityService`, mirroring ADR-0068's
  `GridGameModule` split (pure structural refactor, no behavior/requirement
  change — `XGPathGameModule` remains the `IGameModule` adapter, no
  facade). `PathEligibilityService` is registered independently
  (`AddScoped`) in `ServiceRegistration.cs`. Updated COMP-11's architecture
  row to describe the split (mirroring COMP-05's own ADR-0068 note) and
  fixed every stale `XGPathGameModule.GetEligiblePlayerIdsAsync`/
  `XGPathGameModule.IsEligible`/`XGPathGameModuleTests` reference under
  REQ-1201/REQ-1203 to point at `PathEligibilityService`/
  `PathEligibilityServiceTests` instead, including renamed test method
  names where coverage moved 1:1. See ADR-0082 (new, scaffolded
  separately) for the split decision itself.
- 2026-08-22 — `docs/backlog.md` (S-155 marked SHIPPED) — `WikidataClient.cs`
  (backend/src/XGArcade.DataSync/Wikidata/) split from 1,775 to 782 lines
  (a 993-line/56% reduction), a pure refactor with zero behavior change.
  Every `Build*Query` static helper moved to new file
  `SparqlQueryBuilders.cs` (456 lines, plus the three builder-only
  constants `MaleWikidataQid`/`DateOfBirthCutoff`/
  `NationalTeamClassWikidataQid` as `internal const`); every
  `Parse*Bindings`/`ParseBindings` static helper moved to new file
  `SparqlResponseParsers.cs` (592 lines, including the
  `SparqlResponse`/`SparqlResults`/`SparqlValue` JSON-shape records).
  `WikidataClient.cs` now holds only its constructor/fields, the two
  `Run*` drivers (`RunIntersectionQueryAsync`/`RunThrowingQueryAsync`),
  the private `QueryIntersectionAsync` dispatcher, and its public
  `IWikidataClient` methods as thin wrappers delegating to the moved
  helpers. No `WikidataClientTests.cs` changes needed — every case passes
  through the same unchanged public surface. Both new files land flat in
  the existing `XGArcade.DataSync/Wikidata/` folder (not a new
  `Wikidata/Sparql/` subfolder) — `architecture-reviewer` resolved the
  story's own flagged judgment call in favor of flat, matching the
  `IntersectionQuerySpecs.cs` precedent already in that folder from
  S-100/S-101, with no other DataSync subfolder convention to justify a
  new one; this is file organization, not a structural/boundary decision,
  so no new ADR was scaffolded. `docs/requirements-document.md` and
  `docs/architecture-document.md` were checked and need no change: no
  REQ's behavior or acceptance criteria changed, and no COMP's
  responsibility/boundary/data-flow changed — this is a pure internal
  file-organization refactor entirely within already-documented COMP-07
  (`XGArcade.DataSync`), confirmed not to touch ADR-0003's Core/game
  boundary or any other architectural boundary. No `dotnet` SDK available
  in this development sandbox, so `WikidataClientTests.cs` and the full
  solution could not be run locally — must be verified green in CI before
  merge, per this repo's normal constraint.
- 2026-08-19 — `docs/requirements-document.md` (v1.92, REQ-1201 dated status
  note + new acceptance-criteria bullet + updated test-level paragraph),
  `docs/decisions/0079-xg-path-position-eligibility-floor.md` (new),
  `docs/backlog.md` (Epic 19 added, S-161 marked SHIPPED, S-162/S-163 filed
  as queued follow-ups) — closes a 2026-08-18 user-QA-reported bug: xG Path
  puzzles rendering "Position: not available" because nothing previously
  excluded a null/empty-`Player.Position` candidate from being selected as
  a target in the first place. `XGPathGameModule.GetEligiblePlayerIdsAsync`
  gains a second, independent `Player`-level eligibility floor
  (`Position != null`/non-empty, fail-closed via `IsNullOrWhiteSpace`),
  additive to and mirroring ADR-0073's existing `BirthYear >= 1975` floor
  field-for-field — same `playersById` bulk-fetch reused, no new repository
  call (ADR-0079, REQ-1201, S-161). `docs/architecture-document.md` and
  `docs/implementation-document.md` were checked and need no change: this
  is a pure runtime-filter addition inside the already-documented COMP-11
  (Games.XGPath), with no new component, boundary, data flow, or data-model
  field.
- 2026-08-19 — `docs/requirements-document.md` (v1.93, REQ-1203 dated
  status note for the new inferred-loan label), `docs/decisions/0080-xg-path-inferred-loan-label.md`
  (new), `docs/backlog.md` (S-163 marked SHIPPED) — closes the Beckham/
  Preston loan-shown-as-a-peer-club concern from the same 2026-08-18 QA
  report S-161 (Position eligibility floor) already fixed: an inferred,
  heuristic, presentation-only "(loan)" qualifier now appears on xG Path
  club-reveal clues when a stint's date range is fully contained within a
  different club's concurrent stint. New `PathCareerStintFilter.IsInferredLoan`
  flows through `PathClubClue.IsLoan` → `PathClubClueResponse.IsLoan` (API)
  → frontend `PathClubClue.isLoan` → the qualifier in `PathTimeline.tsx`;
  no eligibility/scoring impact. A quality-gate review caught and fixed a
  short-circuit bug in the original containment formula before it reached
  production — the shipped version gates the whole check on
  `stint.EndYear is not null` rather than only one branch of the inner
  `||` (ADR-0080). `docs/architecture-document.md` and
  `docs/implementation-document.md` were checked and need no change: this
  is a runtime-filter/annotation addition inside the already-documented
  COMP-11 (Games.XGPath), with no new component, boundary, data flow, or
  data-model field — `PathClubClue`/`PathClubClueResponse` are existing
  DTOs gaining one field.
- 2026-08-19 — `docs/decisions/0081-xg-path-collapse-adjacent-same-club-stints.md`
  (new) — closes a real reported bug: a puzzle matching Divock Origi's real
  career rendered three consecutive "Lille" club-reveal entries back to
  back (three `PlayerCareerStint` rows for the same club, adjacent in
  chronological sequence, with different `AppearanceCount` values).
  `PathCareerStintFilter.CollapseAdjacentSameClub` is a new, read-time,
  DISPLAY-ONLY collapse of strictly-adjacent same-`ClubName` stint rows
  (earliest `StartYear`, latest `EndYear`, summed `AppearanceCount` ONLY if
  every row in the run is known, `null` otherwise) — deliberately NOT
  `DuplicateCareerStintCleaner`/ADR-0063's job, since that class proves two
  rows are the same real stint before a permanent DB delete, which isn't
  provable here. Chained identically, in the identical position, at both
  `XGPathGameModule.GetEligiblePlayerIdsAsync` (so `IsEligible`'s
  `MinDocumentedStintCount >= 3` check sees the post-collapse row count)
  and `PathEndpoints.cs`'s `GET /path/current` handler — see ADR-0081's
  invariant-risk reasoning for why both must move together. Also documents
  an intentional, positive side effect: a player whose true seeded-club
  appearance total was split across two adjacent sub-threshold rows now
  correctly qualifies post-collapse. `docs/requirements-document.md`
  (v1.94) gains a matching REQ-1203 dated status note, a new
  acceptance-criteria bullet, and an updated test-level paragraph;
  `docs/backlog.md`'s S-162 entry is now marked SHIPPED.
  `docs/architecture-document.md` and
  `docs/implementation-document.md` were checked and need no change: this
  is a runtime-filter addition inside the already-documented COMP-11
  (Games.XGPath), no new component/boundary/data flow/data-model field.
- 2026-08-18 — `.github/workflows/backfill-player-photos.yml` (re-added),
  `docs/implementation-document.md` (v1.04) — re-created the
  `backfill-player-photos` workflow wrapper S-132 deleted (2026-08-17) as a
  served-its-purpose one-off incident tool, exactly the "re-add later if
  ever needed" scenario S-132 planned for: S-159's
  `PlayerCareerPrefetchService` pool sweeps create players via Wikidata
  queries that deliberately never fetch P18 (photo), so every one of the
  ~198K players that job creates needs this backfill to ever get a photo —
  nothing else will provide one. The underlying `PlayerPhotoBackfillService`/
  CLI verb was never removed (S-132 kept it on purpose), so this is a pure
  workflow-wrapper re-add, no application code change. Updated
  `implementation-document.md`'s stale "deleted in S-132" note to reflect
  the re-add.
- 2026-08-18 — `docs/implementation-document.md` (v1.03), `docs/architecture-document.md` (v1.09, §5.3
  COMP-05/COMP-06/COMP-07 evolution table rows), `docs/backlog.md` (S-160 marked SHIPPED),
  `backend/src/XGArcade.Data/Entities/CountryDefinition.cs`,
  `ClubDefinition.cs`, `backend/src/XGArcade.Data/Migrations/20260818120000_AddPlayerPoolSweptAt.*`,
  `XGArcadeDbContextModelSnapshot.cs` — implemented ADR-0078/S-160 (REQ-110's
  "Extended (2026-08-18) — confirmed-low without a live query for a
  fully-swept pair" criterion, already drafted in requirements-document.md
  v1.91): `CountryDefinition`/`ClubDefinition` gain a nullable
  `PlayerPoolSweptAt`, set by `PlayerCareerPrefetchService` only inside its
  existing `countriesProcessed++`/`clubsProcessed++` success path (never on
  a null-QID skip or a caught `WikidataQueryException`) via two new
  `ICategoryValueRepository` methods (`UpdateCountrySweptAtAsync`/
  `UpdateClubSweptAtAsync`, load-then-`SaveChangesAsync`, since
  `GetCountriesAsync`/`GetClubsAsync` return `AsNoTracking` rows).
  `PlayerCacheWarmingService.WarmAsync` checks both sides of a
  below-threshold pair for a non-null `PlayerPoolSweptAt` before
  `IsConfirmedLowAsync`/`IsPersistentTechnicalFailureAsync`/the live-query
  chain and, when both are set, calls `RecordConfirmedLowAsync` directly
  from the local count with zero Wikidata round-trips — a new
  `PairsConfirmedLowFromSweep` counter on `CacheWarmingResult` makes this
  visible in the run summary. Both documented invalidation sites are wired:
  `StaleClubAttributeCleaner.CleanAsync`/`CleanAllSeededClubsAsync` (REQ-111)
  now also null a cleaned club's `PlayerPoolSweptAt`; `CliVerbDispatcher
  .HandlePurgePlayerPoolAsync` (REQ-112/S-038, `purge-player-pool`) now also
  resets it to `null` on every `CountryDefinition`/`ClubDefinition` row via
  `ExecuteUpdateAsync`, matching that verb's existing `ExecuteDeleteAsync`
  style (a standalone operational CLI verb not exercised by the
  InMemory-provider test suite). Tests extended in
  `PlayerCareerPrefetchServiceTests.cs`, `PlayerCacheWarmingServiceTests.cs`,
  `StaleClubAttributeCleanerTests.cs`, and `CategoryValueRepositoryTests.cs`
  (the two new repository methods themselves); no dedicated test file exists yet
  for `CliVerbDispatcher.HandlePurgePlayerPoolAsync` itself (same
  ExecuteDeleteAsync/ExecuteUpdateAsync InMemory-provider limitation as its
  existing deletes), so its `PlayerPoolSweptAt` reset is covered the same
  way its existing `ConfirmedLowMatchPair`/`PairLookupFailure` resets
  already are — a same-end-state proxy assertion in
  `PlayerCacheWarmingServiceTests.cs`, not a direct unit test of the verb.
  `dotnet` was unavailable in this session; the migration and Designer/
  snapshot updates were hand-written by mirroring
  `20260817120000_AddRoundSequenceNumber`'s exact shape rather than
  generated, and none of this has been run.
- 2026-08-18 — `docs/requirements-document.md` (v1.90), `docs/decisions/0077-prefetch-populates-playerattribute.md`
  (new), `docs/architecture-document.md` (v1.07, §5.3 evolution table:
  COMP-06/COMP-07 rows) — `PlayerCareerPrefetchService`'s
  country/club sweeps now also persist `PlayerAttribute` rows (nationality/
  club), not just `Player`/`PlayerCareerStint` — every pooled player
  satisfies that attribute by construction of the pool query, so this needs
  no extra Wikidata call. Lets `PlayerCacheWarmingService`'s existing local
  `CountPlayersWithBothAttributesAsync` pre-check become the complete
  answer once both sides of a pair have been swept, avoiding the live
  pairwise SPARQL intersection queries that were timing out at a 100%
  failure rate on large club combinations. `PlayerCareerPrefetchResult`
  gains `AttributesAdded`. REQ-110, ADR-0077 (deliberate narrow reversal of
  ADR-0001's incremental-only `PlayerAttribute` principle, scoped to the
  seeded-reference subset). `PlayerCacheWarmingService.cs` itself and
  `PlayerNameIndex` are untouched.
- 2026-08-18 — `docs/decisions/0077-prefetch-populates-playerattribute.md`,
  `docs/architecture-document.md` (v1.08, §6.3) — two quality-gate follow-up
  fixes to the above, caught by `architecture-reviewer`/`quality-architect`
  before merge: (1) the new `PlayerAttribute` writes now also write a paired
  `PlayerData` row (`Source = "wikidata"`, `Confidence = "verified"`),
  matching every other automated Wikidata-derived attribute write and
  satisfying REQ-502's source/confidence traceability — gated on the same
  per-country/per-club dedup, so a repeat sweep re-confirming an
  already-known fact does not re-append a `PlayerData` row (a deliberate,
  documented divergence from `WikidataLookupService.QueueAttribute`'s
  always-append shape); (2) a first attempt to fix a club-name-sourcing
  concern (route the club attribute value through `clubNameByClubQid`) was
  itself wrong and reverted — the value must come from `club.Name` directly
  to match `PlayerCacheWarmingService`'s own join key, not from the QID→name
  map used for the unrelated `PlayerCareerStint.ClubName` write. ADR-0077
  updated with a correction note explaining why the two writes need
  different sources; §6.3 now lists this as a fourth writer of the
  `PlayerData`/`PlayerAttribute` pairing pattern.
- 2026-08-18 — `CODE_HEALTH_ASSESSMENT.md`, `CODEBASE_ANALYSIS.md`, `docs/backlog.md`
  (new Epic 17, S-154–S-158), `docs/architecture-document.md` (v1.06,
  §6.2c/§6.10 fixes) — periodic whole-codebase health sweep
  (`code-health-auditor`). Re-verified Epic 9's actual completion state
  against `git log`/direct file inspection rather than trusting
  `docs/backlog.md`'s own notes: 11 of 15 Epic 9 stories (S-115–S-123,
  S-127–S-129) had already shipped since 2026-08-11 but several were
  missing their "Built as" note — added in this pass, not re-implemented.
  Closed the three items Epic 9 had explicitly deferred: **S-124**
  (`backend/src/XGArcade.DataSync/Wikidata/WikidataClient.cs`) — migrated
  `QueryPlayerPhotosByQidsAsync`/`QueryPlayerPhotoByNameAsync` onto the
  shared `RunThrowingQueryAsync` driver S-118 built, closing the file's
  last two hand-rolled HTTP/timeout/retry blocks (1,820→1,775 lines); added
  4 new byte-for-byte/exact-message regression tests to
  `WikidataClientTests.cs` following S-118's own precedent. **S-125/S-126**
  (`docs/architecture-document.md`) — fixed §6.2c's stale ADR-0052 citation
  (should be ADR-0053) and §6.10's stale `XGArcade.Testing` naming (should
  be `Testing.SeedManager`); found and fixed one adjacent stale claim in
  the same §6.2c paragraphs (REQ-509/510's admin commit was described as
  "not yet built" — it shipped 2026-08-08, well before this note was
  written). All changes hand-traced, not compiled — no `dotnet` SDK in
  this sandbox, confirmed unchanged from the 2026-08-11 report; `npm run
  test` (584/584), `tsc -b`, and `oxlint` all ran live and clean after a
  fresh `npm install`. Overall system health score: 6.4/10 → 7.6/10 (see
  `CODE_HEALTH_ASSESSMENT.md`'s own revision history for the full
  per-module/component breakdown). New Epic 17 (`docs/backlog.md`)
  tracks the remaining gaps found this pass — `XGPathGameModule.cs`'s
  eligibility pipeline (S-154, the clearest new hotspot-in-progress,
  growing the same way `GridGameModule.cs` did before ADR-0068),
  `WikidataClient.cs`'s residual query-builder/parser breadth (S-155,
  lower urgency now that its duplication risk is resolved), `AdminScreen.tsx`'s
  4 still-untested subcomponents (S-156), continued `useAuthedFetch`
  migration (S-157), and `App.tsx`'s carried-over `useSession()` extraction
  (S-158). No boundary violations, no new ADR-worthy decision made by this
  sweep itself (S-154/S-119's own ADR-0068 precedent already sets the bar
  for that story's eventual implementer). `docs/architecture-document.md`'s
  in-body version line (stale at "1.00 · 2026-08-17" against a frontmatter
  already at 1.05) was also found drifted again and re-synced to 1.06 —
  same drift class S-116 fixed once before.

- 2026-08-18 — `docs/backlog.md`, `docs/decisions/0003-generic-round-game-reference.md`,
  `TODO.md` — S-152 (Epic 16, build-only): implemented the `purge-game-history` CLI
  verb (`GameHistoryPurger`, `CliVerbDispatcher`), its
  `.github/workflows/purge-game-history.yml` confirmation-gated workflow,
  and `GameHistoryPurgerTests.cs`. Wipes every `Round`/`Guess`/
  `PlayerSuggestion`(+`PlayerSuggestionClub`)/`GridInstance`(+`GridCell`)/
  `PathInstance`(+`PathPuzzle`)/`PathTargetCycle`/`PathCycleTargetUsage`
  row, leaving `User`/`League`/`LeagueMembership` and every `Player`/
  reference table untouched — not run for real; `TODO.md`'s pre-launch
  checklist is the actual trigger, per the story's own sequencing gate
  (after Epics 10-15 settle, satisfied as of the prior 2026-08-18 backlog
  entry cancelling Epics 14/15). No new REQ (self-contained operational
  tool, matching `purge-player-pool.yml`'s own precedent). `architecture-reviewer`
  and `quality-architect` both reviewed the diff (`/quality-gate`): no
  blocking findings; two trivial test/comment nits from `quality-architect`
  applied directly (a missing pair of assertions in the "every table
  empty" test, an inaccurate verb-count comment). `architecture-reviewer`
  flagged one architecturally-significant point worth recording rather
  than leaving implicit — `GameHistoryPurger` is the first tool to
  hardcode both Core and two separate game modules' table names in one
  class instead of routing per-game deletion through `IGameModule` — so
  ADR-0003 gets a 2026-08-18 follow-up addendum accepting this as a scoped
  exception for one-off, human-triggered maintenance tooling outside the
  request-serving path, explicitly not extending to `Core.Rounds` or any
  other request-serving code.
- 2026-08-18 — `docs/decisions/0076-generalize-playersuggestion-submission-context.md`,
  `docs/architecture-document.md` — S-143 (Epic 14, design-only, no code):
  new ADR-0076 generalizes `PlayerSuggestion`'s submission context off xG
  Grid's `CellId`/row-col category-type coupling, mirroring ADR-0003's
  `Round.GameKey`/`GameInstanceId` opaque-reference pattern. Adds
  `GameKey` (required) and nullable per-game context fields —
  `CellId`/`RowCategoryType`/`ColCategoryType` populated only for
  `xg-grid`, a new `PathPuzzleId` populated only for `xg-path` (a
  deliberate correction of the backlog text's "`PathInstanceId`" —
  `RoundId` already resolves to `PathInstance.Id` via
  `Round.GameInstanceId`, so the field this entity actually needs is the
  per-instance child id, the same structural role `CellId` already plays
  for `GridInstance`, confirmed against `XGPathGameModule
  .GetCellIdsAsync`/`PathPuzzle`). Also settles the two open questions
  S-144 flagged as needing this ADR to decide: the submission route
  widens from `POST /rounds/{roundId}/cells/{cellId}/suggestions` to
  `POST /rounds/{roundId}/suggestions` (branching on `round.GameKey`,
  `cellId`/`pathPuzzleId` moved into the request body) rather than a
  second per-game route, and `XGPathGameModule
  .GetCellCategoryTypesAsync`'s existing `NotSupportedException` stays
  untouched — the new route validates `pathPuzzleId` via the already-
  implemented, game-agnostic `IGameModule.GetCellIdsAsync` instead.
  Reviewed by `architecture-reviewer` against `architecture-document.md`
  and ADR-0003/0007/0053/0060 before finalizing; one accuracy gap it
  found was fixed in the ADR itself (§3): `GET /admin/suggestions`'s
  `PendingSuggestionResponse` DTO already surfaces
  `RowCategoryType`/`ColCategoryType` as non-nullable fields today, so
  S-144 must widen that DTO (and the matching frontend type) to nullable
  and add `GameKey` to the response, not leave the admin list endpoint
  untouched as the ADR's first draft claimed. `architecture-document.md`
  §10 gets ADR-0076's row; its COMP-06 row is deliberately left unchanged
  here — S-146 (deps: S-143/144/145) updates that plus REQ-215's status
  note once the xG Path submission path actually lands, per this ADR's
  own Follow-up note. No REQ change in this iteration: no behavior
  changed yet (design-only story, no code), so REQ-215 stays as-is until
  S-146.
- 2026-08-18 — `backend/src/XGArcade.Api/Players/PlayerAutocompleteEndpoints.cs`,
  `backend/tests/XGArcade.Api.Tests/PlayerAutocompleteEndpointTests.cs`,
  `frontend/src/lib/rounds.ts`, `frontend/src/grid/GridScreen.tsx`,
  `frontend/src/grid/GridScreen.test.tsx`, `frontend/src/path/PathScreen.tsx`,
  `frontend/src/path/PathScreen.test.tsx`, `docs/requirements-document.md`,
  `NOTES.md` — S-151 (Epic 13): added `GET /players/autocomplete/warmup`
  (bearer-token authenticated), a DB-touching warm-up that runs the real
  `IPlayerNameIndexRepository.SearchByPrefixAsync` path against a trivial
  server-side-only 1-character query and returns `204`, distinct from
  `App.tsx`'s existing app-load `/health` ping, which only wakes the
  Container App process and never opens a Postgres connection or compiles
  the EF Core query shape this route needs. `warmUpAutocomplete`
  (`frontend/src/lib/rounds.ts`) fires it fire-and-forget, try/catch-guarded
  so it never surfaces an error or blocks render, from a dedicated mount
  `useEffect` in both `GridScreen.tsx` and `PathScreen.tsx`, independent of
  each screen's existing round-fetch effect. 2 new NUnit tests
  (`PlayerAutocompleteEndpointTests.cs`,
  `REQ207_AutocompleteWarmup_Get_ReturnsNoContent_AndExercisesRealPlayerNameIndexRepository`,
  `REQ207_AutocompleteWarmup_Get_ReturnsUnauthorized_WithoutBearerToken`) and
  2 new Vitest tests (one each in `GridScreen.test.tsx`/`PathScreen.test.tsx`).
  Full frontend suite (584/584), `tsc -b`, and `oxlint` all pass; `dotnet
  test` could not be run in this sandbox (no .NET SDK available — same
  limitation S-141's CHANGELOG entry below documents), so the two new
  backend tests were hand-traced against existing helpers/signatures but not
  executed — needs confirming in CI. `docs/requirements-document.md` gets a
  new 2026-08-18 addendum to REQ-207 documenting the warm-up call, following
  the same dated-addendum pattern as S-142's entry directly below.
  `docs/architecture-document.md` was checked (COMP-10, ADR-0007) and left
  unchanged — the warm-up reuses the exact same `PlayerNameIndex` read path
  already documented there, no new component/boundary/data-flow, and it
  doesn't touch the `minReplicas: 0` infra trade-off already documented in
  `infra/README.md` (works within it, doesn't change it).
  `docs/implementation-document.md` was checked and left unchanged — no
  project-structure, data-model, or tech-stack change, only one more
  endpoint in an already-documented file, matching the precedent of prior
  same-file endpoint additions not being individually cataloged. No ADR —
  reviewed and explicitly declined by both `architecture-reviewer` and
  `quality-architect`: the change stays entirely within the already-documented
  COMP-10 boundary, doesn't cross into COMP-06/`IPlayerStoreRepository`,
  and reverting it is a clean one-file-per-layer removal with no cascading
  effect, so it fails CLAUDE.md's own "would reverting require understanding
  why the original choice was made" ADR test. Two non-blocking optional notes
  from review, not acted on: the mount-`useEffect` warm-up block is
  duplicated verbatim between `GridScreen.tsx`/`PathScreen.tsx` (worth a
  shared hook only if a third such need appears); an additional API-layer
  test for the empty-`PlayerNameIndex`-table case (low priority). S-151's
  other acceptance criterion — manual before/after latency verification
  against the deployed dev environment, since Container Apps' real
  scale-to-zero can't be reproduced in this sandbox — could not be completed
  here; `NOTES.md` gets a new 2026-08-18 entry documenting the limitation and
  handoff steps, same category as S-141's gap noted there. REQ-207, S-151.
- 2026-08-18 — `docs/requirements-document.md` — S-142 (Epic 13): added an
  explicit acceptance-criterion line to REQ-207 stating the 2-character
  minimum-query-length threshold before autocomplete suggestions are
  fetched/shown, citing the three places that already enforce it
  identically: `frontend/src/grid/GuessInput.tsx` (`MIN_QUERY_LENGTH = 2`),
  `frontend/src/path/PathGuessInput.tsx` (same constant), and
  `backend/src/XGArcade.Api/Players/PlayerAutocompleteEndpoints.cs`
  (`MinQueryLength = 2`, enforced server-side independent of either
  frontend). Doc-only — verified as already-correct in code, not newly
  built; no code, test, or ADR change. `docs/architecture-document.md` and
  `docs/implementation-document.md` were checked and left unchanged — no
  component/boundary/data-flow or build-detail change, only an
  acceptance-criterion addendum to an already-`Implemented` REQ. REQ-207,
  S-142.
- 2026-08-18 — `backend/src/XGArcade.Data/Seeding/PathTargetCycleResetter.cs`,
  `backend/src/XGArcade.Api/CompositionRoot/CliVerbDispatcher.cs`,
  `backend/tests/XGArcade.Data.Tests/PathTargetCycleResetterTests.cs`,
  `docs/requirements-document.md`, `NOTES.md` — S-141 (Epic 12): added a
  new `reset-path-target-cycle` `dotnet run --` CLI verb
  (`PathTargetCycleResetter`, mirroring `PairLookupFailureCleaner`'s
  load-then-`SaveChangesAsync` shape) that wipes the `PathTargetCycle`
  singleton row and every `PathCycleTargetUsage` row, so the next xG Path
  generation starts a clean `CycleNumber` 1 baseline scored against the
  eligible pool as narrowed by S-137–S-140 — the pre-existing
  `UsedInCycleCount`/usage bookkeeping was accumulated against the OLD,
  larger pre-S-137–S-140 pool and does not self-correct the way
  `ObservedPoolSize` does. 4 new NUnit tests
  (`PathTargetCycleResetterTests.cs`, `REQ1208_*`); 1548/1548 backend tests
  pass. `docs/requirements-document.md` gets a new dated status note under
  REQ-1208 documenting the tool and cross-referencing REQ-1201's own
  S-137/S-138 notes that already flagged S-141 as a planned follow-up.
  `docs/architecture-document.md` was checked (COMP-11) and left unchanged
  — the tool works entirely within xG Path's own already-declared tables,
  no component/boundary/data-flow change. `docs/implementation-document.md`
  was checked and left unchanged — matching the precedent of its two
  structurally-identical predecessors (`clear-pair-lookup-failures`,
  `clean-duplicate-career-stints`), neither of which was added to its CLI
  verb narrative either. No ADR — same category as those two precedent
  reset tools, confirmed by both `architecture-reviewer` and
  `quality-architect`. S-141's other half — an actual before/after
  eligible-pool count against real (dev) data — could not be produced in
  this sandbox (no live Wikidata access, no real dev Postgres access);
  `NOTES.md` gets a new 2026-08-18 entry documenting the limitation and the
  handoff steps for whoever next has real dev access. REQ-1208, S-141.
- 2026-08-18 — `backend/src/XGArcade.Games.XGPath/PathCareerStintFilter.cs`,
  `backend/tests/XGArcade.Games.XGPath.Tests/PathCareerStintFilterTests.cs`,
  `docs/requirements-document.md`,
  `docs/decisions/0075-xg-path-b-team-reserve-team-exclusion.md` — S-140
  (Epic 12): bug fix broadening `PathCareerStintFilter.NationalTeamPattern`
  to also exclude a label pairing the word-bounded token "regional" with a
  trailing "team"/"representative" (e.g. "Basque Country regional football
  team"), not just "national" + "team" labels — closes the inconsistency
  where "Catalonia national football team" was excluded as a clue-reveal
  club but "Basque Country regional football team" wasn't, even though
  both are non-club representative sides with no real FIFA-affiliation
  signal this filter can check. Test renamed/flipped
  (`REQ1203_IsNationalTeam_NonFifaRegionalTeam_ReturnsFalse` →
  `REQ1203_IsNationalTeam_NonFifaRegionalRepresentativeTeam_ReturnsTrue`),
  plus new regression cases (substring false-positive guard, bare "Regional"
  club-name guard, full seeded-club false-positive sweep).
  requirements-document.md gets a new dated status note under REQ-1203
  marking the prior notes' "Basque Country stays a valid clue" claim as
  superseded, without editing that historical prose (REQ-1203's own
  acceptance criteria wording is unchanged — it already had no
  FIFA-affiliation qualifier, so this is an internal regex refinement, not
  a reinterpretation). ADR-0075's own follow-up note (which had tracked
  this as "not yet fixed as of this ADR") is corrected to reflect S-140
  landing. No new ADR — confirmed by architecture-reviewer as a bug fix to
  an already-ADR'd filter's implementation, and ADR-0075 already scoped
  this fix out of itself as a separately tracked item. REQ-1203.
- 2026-08-18 — `backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs`,
  `docs/requirements-document.md` — S-139 fast-follow: investigated a
  product concern that a player could see an empty club-reveal turn, and
  confirmed "always `PuzzleCount` puzzles per round, never an empty
  turn" already holds structurally for every puzzle the current
  generation pipeline selects — `GetEligiblePlayerIdsAsync` only ever
  judges eligibility against the sanitized (B-team/national-team
  excluded) stint list, so `MinDocumentedStintCount >= 3` always holds
  before `PathClueSequenceBuilder.SplitIntoTurns` ever runs. Locked that
  fetch-sanitize-eligible ordering down as an explicit "must never
  change" code comment rather than leaving it an emergent property. A
  read-time defensive assertion (log-and-continue) was drafted and
  reverted — it logged the violation but still rendered the degraded
  turn, so it didn't satisfy "never show an empty clue," and the only
  way to fully guarantee that (omitting the puzzle from the response)
  was rejected for breaking "always `PuzzleCount` puzzles." No test or
  runtime-behavior changes; REQ-1203 status note records the full
  reasoning. REQ-1203.
- 2026-08-18 — `backend/src/XGArcade.Games.XGPath/PathCareerStintFilter.cs`,
  `backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs`,
  `backend/src/XGArcade.Api/Path/PathEndpoints.cs`,
  `backend/tests/XGArcade.Games.XGPath.Tests/PathCareerStintFilterTests.cs`,
  `backend/tests/XGArcade.Games.XGPath.Tests/XGPathGameModuleTests.cs`,
  `backend/tests/XGArcade.Api.Tests/PathEndpointTests.cs`,
  `docs/decisions/0075-xg-path-b-team-reserve-team-exclusion.md`,
  `docs/requirements-document.md`, `docs/architecture-document.md`,
  `docs/implementation-document.md` — S-139 (Epic 12): added a
  B-team/reserve-team exclusion (`PathCareerStintFilter.IsBTeam`/
  `ExcludeBTeams`), parallel to and chained alongside the existing
  national-team filter at both call sites (`XGPathGameModule.
  GetEligiblePlayerIdsAsync`'s REQ-1201 eligibility check and `GET
  /path/current`'s clue-reveal path), closing the same class of REQ-1203
  clue-leak violation for reserve/development-side rows (e.g. "Real Madrid
  Castilla," "Barcelona B"). New ADR-0075 records the pattern, its
  alternatives, and its explicitly acknowledged false-positive risk;
  requirements-document.md gets a new dated status note under REQ-1203
  (framed as closing the same class of violation via the same mechanism,
  not as a reinterpretation of the REQ's own national-team-specific
  wording); architecture-document.md's COMP-11 row and ADR-trail table get
  a one-line completeness update; implementation-document.md's project-
  structure tree gets the matching file-history note.
- 2026-08-17 — `.github/workflows/warm-grid-cache.yml` — follow-up
  correction to S-134 (PR #209, merged): the rename swept every
  *reference* to `warm-player-cache.yml` but missed the workflow file's
  own top-level `name:` property and job id, which were left as
  `warm-player-cache` — the previous CHANGELOG entry's "content
  unchanged" framing over-extended to these two fields, which are
  genuinely part of "the workflow's name" (the `name:` key is what
  actually displays in the GitHub Actions UI's run list, arguably more
  visibly than the filename) rather than the CLI-verb-invocation
  boundary that framing was meant to protect. Both now read
  `warm-grid-cache`, matching the filename. The `dotnet run --
  warm-player-cache` CLI invocation, its log/echo strings, and
  `CliVerbDispatcher.cs`'s dictionary key are still deliberately
  `warm-player-cache` — that scoping decision stands; only the two
  fields that are unambiguously "the workflow's name" in the GitHub
  Actions UI were corrected. S-134.
- 2026-08-17 — `backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs`,
  `backend/src/XGArcade.Data/Repositories/PlayerCareerStintRepository.cs`,
  `backend/src/XGArcade.Data/Repositories/IPlayerCareerStintRepository.cs`,
  `backend/tests/XGArcade.Games.XGPath.Tests/XGPathGameModuleTests.cs`,
  `backend/tests/XGArcade.Data.Tests/PlayerCareerStintRepositoryTests.cs`,
  `backend/tests/XGArcade.DataSync.Tests/Wikidata/PlayerCareerStintRefreshServiceTests.cs`,
  `docs/decisions/0074-xg-path-two-seeded-club-eligibility.md`,
  `docs/decisions/0045-xg-path-puzzle-generation-model-and-eligibility.md`,
  `docs/requirements-document.md` — S-138 quality-gate follow-up:
  architecture and quality review of the S-138 diff below found that
  dropping `MinStintCount` (the ≥3-total-documented-stint-row floor)
  entirely, as the original backlog story proposed, was incorrect — ≥2
  distinct qualifying seeded clubs only implies ≥2 total rows, not ≥3, so a
  candidate with exactly 2 documented stints (both qualifying seeded clubs)
  could pass eligibility and break REQ-1203's `PathClueSequenceBuilder`,
  which divides a target's stint count across exactly 3 fixed club-reveal
  turns and assumes ≥3 (`SplitIntoTurns(2)` → turn sizes `[0, 1, 1]`, an
  empty first clue turn — production-reachable via `PathEndpoints.cs`, not
  theoretical). Fixed by RETAINING the row-count floor, renamed
  `MinDocumentedStintCount` (value unchanged, 3) and re-justified as a
  REQ-1203 structural requirement, independent of and in addition to
  `MinQualifyingSeededClubs` (2) below — `IsEligible` and the narrowing
  pre-filter (`GetCareerStintCandidatePlayerIdsAsync`, which gained a
  `minTotalStintCount` parameter alongside `minSeededClubCount`) both check
  both conditions. New test coverage:
  `REQ1203_GenerateInstanceAsync_CandidateWithTwoQualifyingSeededClubsButOnlyTwoTotalStints_NeverSelected`
  (`XGPathGameModuleTests.cs`) and
  `GetCareerStintCandidatePlayerIdsAsync_ExcludesPlayersWithFewerThanMinTotalStintCount`
  (`PlayerCareerStintRepositoryTests.cs`) pin down the exact scenario the
  review found. ADR-0074 was rewritten (not just amended) to reflect the
  corrected decision — the total-row floor is retained, not dropped, with
  its justification changed from ADR-0045's original textual reading of
  REQ-1201 (now moot) to a REQ-1203-specific need; ADR-0045's own pointer
  note was corrected to match. `docs/requirements-document.md`'s REQ-1201
  status note was corrected accordingly, and two stale test-name references
  (`...CandidateWithThreeRealStints_StillEligible...`, renamed by the
  original S-138 implementation to
  `...CandidateWithTwoQualifyingSeededClubStints_StillEligible...`) were
  fixed in the same pass. Full backend suite re-run after this fix — see
  below for the original S-138 entry this corrects. **Closes the "open
  item" the original entry below flagged**: REQ-1203's "`N >= 3`,
  guaranteed by REQ-1201's eligibility check" and REQ-1208's "REQ-1201's
  three structural checks" are both accurate again now that the floor is
  restored — no further action needed on that point.
- 2026-08-17 — `backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs`,
  `backend/src/XGArcade.Data/Repositories/PlayerCareerStintRepository.cs`,
  `backend/src/XGArcade.Data/Repositories/IPlayerCareerStintRepository.cs`,
  `backend/tests/XGArcade.Games.XGPath.Tests/XGPathGameModuleTests.cs`,
  `backend/tests/XGArcade.Data.Tests/PlayerCareerStintRepositoryTests.cs`,
  `backend/tests/XGArcade.DataSync.Tests/Wikidata/PlayerCareerStintRefreshServiceTests.cs`,
  `backend/tests/XGArcade.Api.Tests/RoundEndpointTests.cs`,
  `docs/requirements-document.md`,
  `docs/decisions/0074-xg-path-two-seeded-club-eligibility.md`,
  `docs/decisions/0045-xg-path-puzzle-generation-model-and-eligibility.md`,
  `docs/decisions/0047-xg-path-seeded-club-appearance-threshold.md` —
  implemented S-138 (Epic 12) per REQ-1201: `XGPathGameModule.IsEligible`'s
  old "≥3 documented `PlayerCareerStint` rows, any clubs" floor
  (`MinStintCount`) is removed entirely as redundant, replaced by "≥2
  DISTINCT clubs from the seeded `ClubDefinition` list, each individually
  meeting the existing ≥20-appearance-or-unknown bar" (`MinQualifyingSeededClubs`,
  ADR-0047's per-club bar carried forward unchanged, now applied to 2 clubs
  instead of 1); the count is over distinct qualifying club NAMES, not stint
  rows, so two stints at the same seeded club (a loan, then a later return)
  count once. `IPlayerCareerStintRepository.GetCareerStintCandidatePlayerIdsAsync`'s
  narrowing pre-filter parameter was renamed `minStintCount` →
  `minSeededClubCount` and its over-inclusive superset condition updated to
  match ("≥N distinct seeded club names," not "≥N rows AND ≥1 seeded"),
  remaining a true superset of `IsEligible`'s real candidates. The
  chronological-order-determinable check and the `BirthYear >= 1975` floor
  (S-137/ADR-0073) are both unchanged and orthogonal. New ADR-0074 records
  the decision, supersedes ADR-0045's Decision §3 (the dropped ≥3-stint-row
  point only) and ADR-0047 in full (1-club threshold raised to 2, its
  appearance bar carried forward), with pointer notes added to both ADRs'
  status lines. `docs/requirements-document.md`: REQ-1201 gained a new
  2026-08-17/S-138 dated status note describing the current rule and
  pointing readers away from the now-superseded "≥3 stints"/"≥1 seeded-club
  stint" language in the original 2026-07-27 bullet and the acceptance
  criteria below (neither rewritten in place, per this REQ's append-only
  convention); the original bullet also got two short inline pointers so it
  isn't mistaken for still-current on its own. `docs/architecture-document.md`
  and `docs/implementation-document.md` were checked (same terms S-137's own
  entry below checked: `MinStintCount`, `IsEligible`,
  `GetCareerStintCandidatePlayerIdsAsync`, "seeded club") and left
  unchanged — neither doc names REQ-1201's structural checks by name,
  matching S-137's own precedent immediately below. **Open item flagged, not
  fixed in this pass:** REQ-1203's own text ("`N >= 3`, guaranteed by
  REQ-1201's eligibility check") and REQ-1208's design note ("REQ-1201's
  three structural checks") both describe the now-superseded rule and were
  left untouched — out of this story's scope; needs a human call on whether
  REQ-1203's turn-split acceptance criteria still hold for `N` as low as 2.
- 2026-08-17 — `backend/src/XGArcade.Games.XGPath/XGPathGameModule.cs`,
  `backend/tests/XGArcade.Games.XGPath.Tests/XGPathGameModuleTests.cs`,
  `backend/tests/XGArcade.Games.XGPath.Tests/PathCareerStintFilterTests.cs`,
  `docs/requirements-document.md`, `docs/decisions/0073-xg-path-birth-year-floor.md`,
  `docs/decisions/0045-xg-path-puzzle-generation-model-and-eligibility.md` —
  implemented S-137 (Epic 12) per REQ-1201: added an additive, xG-Path-only
  `Player.BirthYear >= 1975` eligibility floor to `XGPathGameModule
  .GetEligiblePlayerIdsAsync`, layered on top of (not replacing) REQ-112's
  own shared 1939 pool floor, fail-closed excluding candidates with a
  `BirthYear` of `null`; new `IPlayerRepository.GetPlayersByIdsAsync` bulk
  fetch scoped to the structurally-eligible candidate set, applied before
  the familiarity filter (ADR-0056 ordering). New ADR-0073 records the
  decision (xG-Path-only vs. raising the shared REQ-112 floor, and the
  fail-closed-on-null choice per ADR-0070's precedent) and supersedes
  ADR-0045 on this one point only, with a pointer note added to ADR-0045's
  status line. Test coverage added in `XGPathGameModuleTests.cs` for the
  1975/1974/null boundary and a familiarity-filter-ordering regression;
  `PathCareerStintFilterTests.cs` gained an explanatory comment only (no
  stint-level surface for this rule). `docs/architecture-document.md` and
  `docs/implementation-document.md` were checked and left unchanged: COMP-11's
  row and the `GetEligiblePlayerIdsAsync` narrative in the implementation
  doc don't enumerate REQ-1201's existing structural checks (`MinStintCount`,
  `MinAppearancesAtSeededClub`) by name either, so adding the new floor
  there alone would be an inconsistent level of detail.
- 2026-08-17 — `docs/architecture-document.md`, `docs/requirements-document.md`,
  `docs/implementation-document.md`, `docs/decisions/0072-split-generate-round-workflow-per-gamekey.md`,
  `infra/README.md`, `NOTES.md`, `TODO.md` — implemented S-136 (Epic 11) per
  REQ-301/REQ-1202: split the single `generate-round.yml` daily-cron workflow
  (one job calling `/internal/generate-round` twice via a shared bash retry
  function, once per `GameKey`) into two fully independent workflow files,
  `generate-grid-round.yml` (`GameKey = "xg-grid"`) and
  `generate-path-round.yml` (`GameKey = "xg-path"`), each with its own
  `on.schedule` cron (unchanged `0 6 * * *` cadence, independently
  re-verified against ADR-0027's `RoundDuration >= cron's max gap`
  invariant) and its own `workflow_dispatch.round_duration_hours` input
  scoped to only its own `GameKey` — fixing a latent bug where the old
  shared input silently applied to both games on a manual dispatch. No
  backend/C# behavior changed (`RoundSchedulingOptions`,
  `IRoundSchedulingOptionsResolver`, `RoundGenerationService`,
  `/internal/generate-round` are all untouched); only comments referencing
  the old filename were updated across the backend and its tests. See
  ADR-0072 (extends, does not supersede, ADR-0027/ADR-0051) for the full
  reasoning on why the split is safe now.
- 2026-08-17 — `docs/implementation-document.md`, `docs/architecture-document.md`,
  `docs/decisions/0071-round-sequence-number.md`, `docs/requirements-document.md`,
  `docs/design-document.md` — implemented S-135 (Epic 11) per REQ-304: added `Round.SequenceNumber` (`int`,
  `required`), computed as `MAX(SequenceNumber) + 1` scoped to the new
  row's own `GameKey` inside `RoundGenerationService`, guarded against a
  concurrent-generation race by a new `(GameKey, SequenceNumber)` unique
  index (`XGArcadeDbContext`/migration `20260817120000_AddRoundSequenceNumber`)
  rather than an explicit transaction — see ADR-0071 for why. The migration
  backfills every existing row per `GameKey`, ordered by `StartTime`
  ascending, via a `ROW_NUMBER() OVER (PARTITION BY "GameKey" ORDER BY
  "StartTime")` window function. `sequenceNumber` added alongside the
  unchanged `roundId` on every round-shaped DTO: `CurrentRoundResponse`
  (`RoundEndpoints.cs`), `CurrentPathResponse` (`PathEndpoints.cs`),
  `ClosedRoundSummary`/`ClosedRoundSummaryResponse`
  (`LeaderboardService.cs`/`LeaderboardEndpoints.cs`),
  `GenerateRoundResponse` and both `/internal/test-data/seed-guessable-*`
  endpoints (`InternalRoundEndpoints.cs`, which also compute their own
  `MAX+1` since they bypass `RoundGenerationService`), and
  `AdminRoundResponse` (`AdminManagementEndpoints.cs`). `Round.Id`
  unchanged as the sole real routing/FK identifier everywhere — never
  replaced or supplemented as a lookup key. Frontend
  (`RoundControlSection.tsx`) was also updated in this same pass to
  display `"Grid Round #{sequenceNumber}"` instead of the raw `roundId`
  GUID, with `sequenceNumber` added to the relevant DTOs in
  `frontend/src/lib/types.ts` and `AdminScreen.test.tsx` updated to assert
  the new label and that no GUID substring is rendered. REQ-304 itself was
  already added to `docs/requirements-document.md` ahead of this
  implementation, and its acceptance criteria are corrected here to match
  what was actually built: `SequenceNumber` assignment is not computed
  inside the creation transaction (the requirement's original wording) but
  read immediately before it, with the `(GameKey, SequenceNumber)` unique
  index as the real race guard (matching ADR-0071), and the
  `RoundControlSection.test.tsx` reference is corrected to
  `AdminScreen.test.tsx`, the file that actually carries this coverage.
  Existing `new Round { ... }` test-fixture call sites
  across `XGArcade.Api.Tests`/`XGArcade.Core.Tests` updated to set the
  now-`required` `SequenceNumber` (a placeholder value in each —
  `InMemory`'s provider does not enforce unique indexes, per
  `UserRepositoryTests.cs`'s own existing note, so this doesn't risk a
  spurious test failure). REQ304-named unit tests were added afterward to
  `RoundGenerationServiceTests.cs` (MAX+1 starts at 1, doesn't collide
  across two rounds of the same `GameKey`, stays independent across two
  different `GameKey`s), plus API-level assertions extending the existing
  `RoundEndpointTests.cs`/`CurrentRoundEndpointTests.cs`/
  `PathEndpointTests.cs`/`LeaderboardEndpointTests.cs`/
  `AdminManagementEndpointTests.cs` to confirm each round-shaped DTO
  surfaces `sequenceNumber` alongside `roundId`. Two further
  `RoundEndpointTests.cs` cases prove that same coverage through the real
  `/internal/generate-round` HTTP endpoint, not just `RoundGenerationService`
  directly:
  `REQ304_GenerateRound_Post_CalledTwiceForSameGameKey_AssignsDistinctIncrementingSequenceNumbers`
  (same-`GameKey` distinctness/incrementing) and
  `REQ304_GenerateRound_Post_TwoDifferentGameKeys_EachIndependentlyAssignsSequenceNumberOne`
  (two different `GameKey`s each independently land on `SequenceNumber == 1`
  against the same shared database, proving the counter is scoped per
  `GameKey` rather than global). The migration's backfill logic (raw SQL,
  `ROW_NUMBER() OVER (PARTITION BY "GameKey" ORDER BY "StartTime")`) is
  **not** covered by an automated test and is not expected to be: this
  repo's test suite runs against the EF Core InMemory provider, which
  cannot execute raw-SQL migrations, and there is no real-Postgres-backed
  test infrastructure here yet, so `requirements-document.md`'s REQ-304
  Test-level line now states plainly that this logic is verified by manual/
  code review only, rather than describing it as outstanding work for a
  future test-writer pass. `docs/design-document.md`'s SCREEN-04 mock
  (which showed the pre-REQ-304 raw-GUID-style round label) and its
  SCREEN-01 status note (which stated no field on `GET /rounds/current`
  carried a round number, no longer true now that `sequenceNumber` exists)
  are both corrected in place with dated status notes, without removing the
  original notes' history — SCREEN-01's player-facing grid header still
  does not render `sequenceNumber`; only the admin round-control section
  does, per REQ-304's own scope. REQ-304's "Path Round #{sequenceNumber}"
  acceptance criterion also gets a clarifying note: `RoundControlSection.tsx`
  is `"xg-grid"`-only today and no equivalent `"xg-path"` admin
  round-control element exists yet (`XGPathCycleSection.tsx` shows
  cycle/pool metrics, not a round GUID or number), so that half of the
  criterion is a forward-looking naming convention for whenever such a UI
  element is added, not an unimplemented gap in this story.

  Test coverage now backing REQ-304 in full: Unit
  (`RoundGenerationServiceTests.cs` — `MAX + 1` starts at 1, doesn't
  collide across two rounds of the same `GameKey`, stays independent
  across two different `GameKey`s), API/Integration
  (`RoundEndpointTests.cs`'s same-`GameKey` distinctness test and
  cross-`GameKey` independence test above, plus DTO-field-presence
  assertions across `RoundEndpointTests.cs`/`CurrentRoundEndpointTests.cs`/
  `PathEndpointTests.cs`/`LeaderboardEndpointTests.cs`/
  `AdminManagementEndpointTests.cs`), Component (`AdminScreen.test.tsx`
  asserting the `"Grid Round #N"` label and that no GUID substring is
  rendered), and manual/code review only (the migration's raw-SQL backfill,
  per the InMemory-provider limitation above).
- 2026-08-17 — `.github/workflows/`, `docs/implementation-document.md`,
  `docs/requirements-document.md`, `NOTES.md`,
  `docs/decisions/0024-cache-warming-runs-as-a-cli-verb.md`,
  `docs/decisions/0025-player-pool-restricted-to-male-born-1939-or-later.md`,
  `docs/decisions/0052-pair-lookup-failure-persistence-and-club-club-query-fix.md`,
  `docs/decisions/0055-proactive-player-data-buildout.md`,
  `docs/decisions/0069-club-scoped-player-career-prefetch.md` — implemented
  S-134 (Epic 10, deps S-130/S-132 confirmed merged first): renamed
  `.github/workflows/warm-player-cache.yml` → `warm-grid-cache.yml`, since
  the old name didn't say which cache/game it fills (it only ever warms
  `PlayerAttribute`, xG Grid's category-pairing answer cache — never
  `PlayerCareerStint`, xG Path's `prefetch-player-careers.yml`), unlike
  its Path counterpart. Content unchanged — pure `git mv` plus a
  repo-wide sweep of every reference to the old filename. Scoping
  decision, stated explicitly since the story's own accept criteria reads
  more broadly at first glance: this is a **workflow-filename** rename
  only, not a CLI-verb rename. `PlayerCacheWarmingService`'s underlying
  `dotnet run -- warm-player-cache` CLI verb (`CliVerbDispatcher.cs`'s
  dictionary key, its log/echo strings, and the renamed workflow's own
  internal `name:`/job-id/invocation lines) is deliberately left as
  `warm-player-cache` throughout — renaming a still-working CLI verb that
  external scripts/muscle memory may call by name is a behavior-relevant
  change, not a rename, and would have contradicted the story's own
  "content unchanged" clause for the `.yml` file. Every reference to the
  *workflow* (`warm-player-cache.yml`, and bare `warm-player-cache` prose
  that names the job/workflow rather than the CLI verb string) was swept.
  Where a sentence used the bare name loosely to describe the job as a
  whole (e.g. "the way `warm-player-cache`'s skip-shortcut works"), it was
  normalized to `warm-grid-cache` too, as shorthand for "the job" — this
  is a documentation-precision choice, not a claim that anything besides
  the `.yml` filename actually changed; the CLI verb string, job id, and
  log/echo text remain `warm-player-cache` everywhere they execute. Swept:
  `PlayerCacheWarmingService.cs`'s and `CliVerbDispatcher.cs`'s doc
  comments, `XGArcade.Data.csproj`'s CI-hygiene comment,
  `PlayerNameIndexImporter.cs`'s doc comment, sibling workflows'
  own comments that name it (`import-player-name-index.yml`,
  `purge-player-pool.yml`, `prefetch-player-careers.yml`), all of
  `NOTES.md` (its own preamble says it doesn't preserve history, unlike
  this log, so its 2026-08-01 incident entry's header was updated too),
  and `docs/requirements-document.md`/`docs/implementation-document.md`.
  Five still-**Accepted** ADRs that named the old filename as a current
  operational detail (0024, 0025, 0052, 0055, 0069) each got a short
  non-rewriting follow-up addendum recording the rename, matching
  ADR-0059's S-132 precedent, rather than editing their original
  Decision/Context/Consequences prose. ADR-0029 is **Superseded** — left
  untouched, same precedent as S-132's own ADR-0029 treatment (frozen
  historical text, not a claim about current system state). `NOTES.md`'s
  one literal CLI-invocation line (`` `dotnet run -- warm-player-cache` ``)
  was deliberately left as-is for the same CLI-verb-not-renamed reason.
  `infra/README.md`, `docs/architecture-document.md`, and `MVP-SCOPE.md`
  confirmed via grep to have zero references — no changes needed there.
  `docs/backlog.md` deliberately left unedited, per this repo's own
  established convention (see the S-132 entry below) of not sweeping
  backlog prose during these rename/cleanup passes — any backlog entry
  written from here on that names this job should use `warm-grid-cache`.
  No dotnet toolchain in this sandbox to verify a build; all backend
  changes are comment-only (no executable code touched), so risk is
  minimal, but this should still be confirmed once CI runs. No ADR of its
  own — this is a rename, not a new structural decision (the five
  addenda above record the rename's effect on existing decisions, not a
  new one). S-134.
- 2026-08-17 — `docs/implementation-document.md`, `docs/requirements-document.md`,
  `docs/decisions/0059-career-stint-club-name-canonicalization.md` — doc-sync
  for S-132: deleted `.yml` workflow wrappers for 7 one-off
  incident-recovery/backfill/cleanup tools that already served their
  purpose and had no runs in weeks — `audit-club-gaps.yml`,
  `backfill-player-photos.yml`, `backfill-player-position-birthyear.yml`,
  `clean-duplicate-career-stints.yml`, `clean-stale-club-attributes.yml`,
  `clear-pair-lookup-failures.yml`, and `verify-wikidata-player-data.yml`
  (`purge-player-pool.yml` explicitly kept — actively reused, not a
  one-time artifact). Only the thin `workflow_dispatch` Actions wrappers
  were removed; the underlying CLI verbs/services
  (`PlayerPhotoBackfillService`, `PlayerPositionBirthYearBackfillService`,
  `DuplicateCareerStintCleaner`, `StaleClubAttributeCleaner`,
  `PairLookupFailureCleaner`, and the `verify-wikidata-player-data`/
  `audit-club-gaps` verbs) are unchanged and still runnable via
  `dotnet run -- <verb>` for any future incident of the same shape.
  `implementation-document.md` §6's `StaleClubAttributeCleaner`/
  `backfill-player-photos`/`backfill-player-position-birthyear` passages
  now describe the verbs as run locally via `dotnet run --`, with the
  deleted `.yml` wrappers noted as historical (S-132, 2026-08-17), rather
  than present-tense "run via `<name>.yml`". `requirements-document.md`'s
  REQ-1207 status note dropped its explicit
  `.github/workflows/backfill-player-position-birthyear.yml` reference the
  same way. ADR-0059's Decision-section "run manually via
  `workflow_dispatch`" line for `clean-duplicate-career-stints` is now
  stale in the same way ADR-0009's `promote-dev-to-prod-dry-run.yml`
  reference was after S-130, so it gets the same small, non-rewriting
  follow-up addendum recording the wrapper's deletion; the decision itself
  is untouched. **Correction (same day, caught independently by both
  `architecture-reviewer` and `quality-architect` during the S-132 quality
  gate):** this entry originally claimed ADR-0029 was also grep-confirmed
  clean, but it still names `verify-wikidata-player-data.yml` in present
  tense in its "Existing backlog" section. Unlike ADR-0059/ADR-0009,
  ADR-0029 is **Status: Superseded by ADR-0032, kept for history, not
  deleted** — per this repo's "ADRs are not rewritten" rule, a superseded
  ADR's already-frozen historical text is left as-is rather than getting a
  follow-up addendum; the addendum treatment is only for still-Accepted
  ADRs (ADR-0059, and ADR-0009's S-130 precedent). So ADR-0029 is
  deliberately left unedited — its `verify-wikidata-player-data.yml`
  mention describes a one-time bulk-verify action from a now-superseded
  decision, not a claim about current system behavior. Confirmed via grep
  that `infra/README.md`, `MVP-SCOPE.md`, `docs/architecture-document.md`,
  and ADRs 0025/0032/0052 have no stale references to any of the 7 removed
  workflow filenames — no changes needed there. `docs/backlog.md` is
  unchanged per this repo's convention of recording story closure only in
  this CHANGELOG, not inline in the backlog text. S-132; ADR-0059
  addendum.
- 2026-08-17 — `docs/backlog.md`, `NOTES.md` — closed S-131: verified
  `prefetch-player-careers.yml`'s post-#203 re-run (run #6, triggered on
  commit `1e7cb99` itself) against real GitHub Actions run history and job
  logs. Confirmed #203's timeout headroom fix worked (the run finished in
  43 of its 240-minute cap, no timeout failure) but the job still exited
  nonzero on a different, already-documented cause — transient Wikidata
  `502 Bad Gateway` responses across 8 countries, 1 club, and 26
  career-fetch batches, the same flakiness class as this job's two prior
  incidents, not a regression. Per S-131's own accept criteria, filed
  S-153 as the real follow-up (give `prefetch-player-careers` a persisted
  failure-tracking skip-shortcut mirroring `warm-player-cache`'s
  `PairLookupFailure`/ADR-0052, so re-runs retry only what failed instead
  of repeating the full sweep) instead of reopening S-131. No code
  changed, no REQ/ADR affected — diagnostic/backlog-triage only.
- 2026-08-17 — `docs/implementation-document.md`, `docs/architecture-document.md`,
  `infra/bicep/modules/backend-container-app.bicep`,
  `docs/decisions/0009-bidirectional-game-data-sync.md` — doc-sync follow-up
  closing a quality-gate finding on S-130's diff: two "HOW it's built" docs
  still described the 5 deleted workflows
  (`promote-dev-to-prod.yml`/`sync-prod-to-dev.yml`/
  `promote-dev-to-prod-dry-run.yml`/`sync-players.yml`/`backup-database.yml`)
  as present-tense active/scheduled. `implementation-document.md` §8's
  CI/CD subsection and game-data-sync subsection now say these wrappers
  were deleted (S-130, 2026-08-17) while the underlying scripts
  (`infra/scripts/promote-dev-to-prod.sh`, `infra/scripts/sync-prod-to-dev.sh`)
  and the `/internal/sync-players` endpoint remain runnable by hand, pointing
  at `infra/README.md` for current state; its §10 open-questions bullet
  dropped the stale `sync-players.yml` reference too.
  `architecture-document.md` §6.9's backup data-flow diagram no longer shows
  `backup-database.yml` as an active scheduled flow — it now states no
  backup automation currently runs (REQ-901), pointing at
  `docs/requirements-document.md`'s REQ-901 status note and
  `infra/README.md`'s Backups section for the Tier 1 rebuild plan; the old
  diagram shape is kept below as the intended-once-rebuilt shape, clearly
  marked "not currently present". `backend-container-app.bicep`'s
  `internalJobToken` parameter description dropped its stale reference to
  `sync-players.yml` as a consumer (the param/logic are unchanged — still
  used by `generate-round.yml` and the `/internal/sync-players` endpoint
  itself). ADR-0009 gained a brief follow-up note under its existing
  2026-08-08 addendum recording that `promote-dev-to-prod-dry-run.yml` was
  later deleted in S-130 once its target workflow was also gone — the
  addendum's prose about the decision itself is untouched, per ADRs not
  being rewritten. No code, `infra/README.md`, `MVP-SCOPE.md`, or
  `requirements-document.md` content touched — those were already updated
  in the S-130 commit. REQ-901; ADR-0009.

- 2026-08-17 — `.github/workflows/`, `infra/README.md`, `MVP-SCOPE.md`,
  `docs/requirements-document.md` — implemented S-130: deleted all 5 Tier 1
  dev/prod-split/backup workflows that had never once succeeded —
  `backup-database.yml` (40/40 scheduled runs failed), `promote-dev-to-prod.yml`
  (0 runs ever), `sync-players.yml` (0 runs ever), `sync-prod-to-dev.yml`
  (0 runs ever), and `promote-dev-to-prod-dry-run.yml` (orphaned once its
  target, `promote-dev-to-prod.yml`, was gone). Clean-slate deletion, not a
  patch-and-keep — none of Tier 1's real prod environment exists yet for
  any of these to act on. The underlying scripts
  (`infra/scripts/promote-dev-to-prod.sh`, `infra/scripts/sync-prod-to-dev.sh`)
  and the `/internal/sync-players` endpoint are unchanged and still fully
  runnable by hand — no capability was lost, only the always-red/never-run
  Actions-tab entries. `infra/README.md` updated throughout (Environments,
  Backups, secrets table, Supabase-pause keep-alive note) to drop
  descriptions of the deleted workflows as active and point to the kept
  scripts for manual use instead. `MVP-SCOPE.md`'s Tier 1 section gains the
  same pointer on its "Creating a real prod environment" and "Backups +
  alerting" bullets, so a future Tier-1 session isn't surprised these are
  gone. `docs/requirements-document.md`'s REQ-901 gains a status note that
  its automation was removed pending Tier 1 (the requirement itself is
  unchanged). No ADR — this is a deletion of dead automation, not a
  structural decision that could reasonably have gone another way.
- 2026-08-17 — `docs/backlog.md`, `TODO.md` — added Epic 16/S-152, a
  `purge-game-history` CLI verb + confirmation-gated workflow to wipe all
  historical rounds/guesses/grids/paths for a pre-launch clean slate.
  Confirmed against `XGArcadeDbContext.cs`'s actual FK configuration (not
  assumed) that deleting `Round` cascades to `PlayerSuggestion` as well as
  `Guess` — recorded as a deliberate inclusion, not a silent side effect —
  and that `PathTargetCycle`/`PathCycleTargetUsage` have no cascade path at
  all and must be deleted explicitly, or xG Path's cycle state would
  reference rounds that no longer exist. Explicitly excludes `Player`/all
  reference-table data (that's `purge-player-pool.yml`'s separate concern).
  Scoped to run last, after Epics 10-15 land, since Epic 11's
  `SequenceNumber` backfill and Epic 14/15's `PlayerSuggestion` schema
  changes would otherwise need re-deriving against a moving target.
  `TODO.md`'s pre-launch checklist gets the actual "run it once" step. No
  code changed yet.
- 2026-08-17 — `docs/backlog.md` — folded a rename into S-134:
  `warm-player-cache.yml` → `warm-grid-cache.yml`, since it only ever
  fills xG Grid's `PlayerAttribute` cache (not `PlayerCareerStint`, which
  is xG Path's `prefetch-player-careers.yml`) and the un-scoped name broke
  parity with the Epic 11 `generate-grid-round.yml`/`generate-path-round.yml`
  split. Also added a standing "token efficiency" directive to the
  backlog's "For AI agents" preamble — governs how every story (not just
  today's) should be turned into a session prompt: hand the implementing
  agent the story's already-recorded file/line specifics directly instead
  of re-deriving them via broad exploration, and keep session scope to
  exactly what the story names. No code changed yet.
- 2026-08-17 — `docs/backlog.md` — product decision: rewrote S-130 from
  "patch `backup-database.yml` with an early-exit guard" to "delete every
  Tier 1 dev/prod-split workflow that has never succeeded" (clean slate,
  re-add when Tier 1 actually needs it) — now covers `backup-database.yml`
  (40/40 failed), `promote-dev-to-prod.yml` (0 runs), `sync-players.yml`
  (0 runs), `sync-prod-to-dev.yml` (0 runs), and `promote-dev-to-prod-dry-run.yml`
  (orphaned once its target is gone). S-133, which had left keep-vs-remove
  as an open product decision, is marked superseded rather than deleted
  (kept numbered, per the S-092 precedent). S-134's naming-audit list and
  deps updated to match the smaller post-cleanup workflow set. No code
  changed yet.
- 2026-08-17 — `docs/backlog.md` — added S-151 (Epic 13) scoping a fix for
  autocomplete's reported first-keystroke slowness: confirmed the backend
  Container App scales to zero (`minReplicas: 0`) and the existing
  `/health` warm-up ping on app load never touches the database, so the
  cold Postgres connection/EF Core query-compile cost still lands on the
  player's first autocomplete keystroke. Story adds a real DB-touching
  warm-up call on game-screen mount. No code changed yet.
- 2026-08-17 — `docs/backlog.md` — added Epics 10-15 (S-130 through S-150)
  scoping the repo-wide overhaul: CI/CD workflow cleanup (grounded in a
  live GitHub Actions run-history audit — found `backup-database.yml`
  failing 40/40 recent scheduled runs and `prefetch-player-careers.yml`
  failing 4/6, plus 3 never-triggered and 7 one-off-incident-served
  workflows), a per-`GameKey` human-readable round number to replace the
  raw GUID `RoundControlSection.tsx` currently renders, splitting
  `generate-round.yml` into per-game workflows, xG Path eligibility
  changes (born-1975-or-later floor, ≥2 eligible-club requirement, B-team/
  broadened regional-team exclusion), confirmation that REQ-207's
  2-character autocomplete threshold already ships in both games (doc-only
  gap, no code change needed), generalizing `PlayerSuggestion` submission
  off of xG Grid's `CellId`/category-type coupling so xG Path can report
  corrections too, and a new per-user suggestion-history view with a
  clear/dismiss action in `SettingsScreen.tsx` (the codebase's first
  soft-delete/dismiss pattern). No code changed yet — this is the scoping
  pass; several stories flag an explicit product decision needed before
  implementation (S-133's keep/remove call on the three never-triggered
  Tier-1-pending workflows; S-138's appearance-threshold-on-both-clubs
  design) rather than presuming an answer.
- 2026-08-17 — `NOTES.md`, `.github/workflows/warm-player-cache.yml`,
  `.github/workflows/prefetch-player-careers.yml` — the first real
  post-purge cold rebuild needed more than 90 minutes: `warm-player-cache`
  got killed by its own job timeout (no `ConfirmedLowMatchPair`/
  `PairLookupFailure` rows left to skip after a purge), and
  `prefetch-player-careers` completed a full pass (193,382 players/527,252
  stints) but exited nonzero on 37 scattered transient WDQS 502s + 2 failed
  country pool fetches — ordinary load flakiness under ADR-0069's now-doubled
  sweep, not a bug. Both workflows' `timeout-minutes` raised 90 → 240.
- 2026-08-17 — `NOTES.md` — first real `purge-player-pool` run since
  S-038/ADR-0025 timed out on Npgsql's 30s default command timeout against
  a pool grown large enough (600k+ `PlayerCareerStint` rows) that the
  cascading bulk delete couldn't finish in time. Fixed with a verb-scoped
  10-minute `Database.SetCommandTimeout`, same class of fix as ADR-0055's
  own timeout incident — no ADR needed, this is an operational fix, not a
  new structural decision.
- 2026-08-17 — `docs/decisions/0070-grid-live-lookup-flag.md`,
  `infra/bicep/main.bicep` — product owner's explicit direction: dev's
  `gridLiveLookupEnabled` default flipped to `false` — REQ-211's guess-time
  live-lookup fallback is off as of the next deploy, testing whether
  S-127's proactively-built cache is complete enough on its own. Reverts
  by flipping the default back to `true` if wrongly-rejected correct
  guesses start appearing.
- 2026-08-17 — `docs/backlog.md`, `docs/decisions/0070-grid-live-lookup-flag.md`,
  `infra/bicep/main.bicep`, `infra/bicep/modules/backend-container-app.bicep`
  — S-128 follow-up: `GridLiveLookup__Enabled` was never actually wired into
  the deployed dev Container App's environment variables, so the flag's
  `true` default always applied regardless of intent — closed by adding a
  `gridLiveLookupEnabled` bicep param (default `true`, no behavior change),
  mirroring `roundDurationHours`'s existing wiring exactly — ADR-0070.
- 2026-08-17 — `docs/backlog.md` — S-129 (frontend half): `SuggestionsScreen.tsx`
  now shows a real confirmation summary on commit instead of nothing
  (`PendingSuggestionRow`'s approval flow) or a generic "Player data
  committed." string (`ManualSearchSection`'s flow) — both now build their
  message from the actual `CommitPlayerDataResult` response
  (`playerCreated`/`nationalityWritten`/`clubsAdded`/`clubsAlreadyEffective`),
  with a genuine no-op called out plainly. `frontend/src/lib/types.ts`'s
  `CommitPlayerDataResult` updated to match the backend's redesigned
  response shape. No `docs/design-document.md` change — `SuggestionsScreen`
  is an admin-only utility screen with no `SCREEN-xxx` entry. REQ-509/510.
- 2026-08-17 — `docs/backlog.md`, `docs/decisions/0060-suggestion-commit-write-path-split-by-cardinality.md`,
  `docs/requirements-document.md` (v1.75) — S-129 (backend half):
  `CommitPlayerDataResponse` (`AdminSuggestionEndpoints.cs`) redesigned to
  report what actually changed rather than echoing back the admin's
  confirmed input — `PlayerCreated`, `NationalityWritten`, and
  `ClubsAdded`/`ClubsAlreadyEffective` replace the old `Nationality`/`Clubs`-only
  shape, so a genuine no-op commit (e.g. every asserted club already
  effective) is no longer indistinguishable from a real write. No
  write-path/validation behavior changed (ADR-0060's decision stands, new
  status note added). `AdminSuggestionEndpointTests.cs` updated for the new
  shape plus new no-op/already-effective/update-branch cases. Frontend
  consumption is an explicit follow-up story. REQ-509/REQ-510.
  **Quality-gate correction, same day:** `PlayerCreated` was initially
  computed via a separate, non-atomic pre-read
  (`IPlayerRepository.GetPlayerByWikidataQidAsync`) before
  `GetOrCreatePlayersByWikidataQidAsync`'s own upsert — racy against
  concurrent callers of that shared batched method (REQ-211's guess-time
  fallback, `PlayerCareerPrefetchService`), and that method itself had no
  `DbUpdateException`/unique-violation handling at all, unlike this
  codebase's other get-or-create paths. `PlayerRepository
  .GetOrCreatePlayersByWikidataQidAsync` now catches the unique-violation
  on `IX_Players_WikidataQid` and detaches/re-fetches the winner (same
  precedent as `LeagueRepository.GetOrCreateGlobalLeagueAsync`/
  `PathInstanceRepository.GetOrCreateCycleStateAsync`), and its return type
  changed to `IReadOnlyDictionary<string, PlayerCreationResult>`
  (`PlayerCreationResult(Player Player, bool WasCreated)`, new in
  `IPlayerRepository.cs`) so `WasCreated` is computed atomically at the
  insert point. `WikidataLookupService`/`PlayerCareerPrefetchService`
  updated to unwrap `.Player`; `CommitPlayerDataAsync` reads
  `PlayerCreated` off the new signal directly, pre-read removed.
  `PlayerRepositoryTests.cs` updated accordingly.
- 2026-08-17 — `docs/backlog.md`, `docs/decisions/0070-grid-live-lookup-flag.md`
  (new), `docs/requirements-document.md` (v1.74), `docs/architecture-document.md`
  (v1.01) — S-128: feature-flagged REQ-211's guess-time live-lookup fallback
  (`GridGameModule.ScoreSubmissionAsync`) behind new `GridLiveLookupOptions
  .Enabled` (default `true`, config key `GridLiveLookup:Enabled`/env var
  `GridLiveLookup__Enabled`, same override convention as
  `RoundScheduling:RoundDurationHours`) — an operational toggle, not a
  removal, so the product owner can test whether S-127's proactively-built
  cache is complete enough on its own, with an instant way back if correct
  guesses start getting wrongly rejected again. Checked immediately before
  the existing `PlayerNameIndex` gate; when disabled, neither
  `IPlayerNameIndexRepository.ExistsByNormalizedNameAsync` nor
  `IGridLiveLookupDispatcher.TryRefreshCellAsync` is ever called, and the
  guess fails closed exactly as it would have before REQ-211 existed.
  REQ-103's grid-generation-time live lookup is a separate call path through
  the same shared dispatcher and is deliberately unaffected. ADR-0070
  records the "flag, not outright removal" reasoning; REQ-211 and
  ADR-0018/ADR-0046 got status notes, not supersessions — the fallback
  still exists in full. `GridGameModuleTests` gained
  `CallCountingPlayerNameIndexRepository`/`CallCountingGridLiveLookupDispatcher`
  spies (same pattern as `GridNameMatcherTests`'s existing call-counting
  repositories) to assert neither dependency is reached when the flag is
  off.
- 2026-08-17 — `docs/backlog.md`, `docs/decisions/0069-club-scoped-player-career-prefetch.md`
  (new), `docs/requirements-document.md` (v1.73), `docs/architecture-document.md`
  (v1.00) — S-127: widened `PlayerCareerPrefetchService` to also sweep
  every seeded `ClubDefinition` row's full eligible player pool (new
  `IWikidataClient.QueryPlayerPoolByClubAsync`, P54's full statement path —
  never the truthy `wdt:P54` shortcut), in addition to its existing
  countries-only sweep — ADR-0069, extending (not superseding) ADR-0055,
  which had deliberately deferred this widening pending a fresh product
  decision. `PlayerCareerPrefetchResult` gained `ClubsProcessed`/`ClubsFailed`;
  `CliVerbDispatcher.cs`'s console summary and `prefetch-player-careers.yml`'s
  header comment updated to mention clubs. Added a REQ-110 status note (no
  prior status note had documented `PlayerCareerPrefetchService` itself, so
  this one covers both ADR-0055's original nationality sweep and ADR-0069's
  club-sweep addition) and updated architecture-document.md's COMP-07 row/
  ADR cross-reference table.
- 2026-08-16 — `docs/architecture-document.md` (v0.99), `docs/requirements-document.md`
  (v1.72) — S-123 (`docs/backlog.md` Epic 9): applied S-116's same
  current-state-only treatment to the remaining "COMP-XX status (DATE,
  S-xxx):"/"S-xxx addition:" accretion pockets in §6 (Key data flows) that
  S-116 deliberately left for this follow-up. §6.1 (Grid generation flow)'s
  opening pocket — 137 lines/8,737 characters of dated, stacked status
  prose between the heading and the flow diagram — is now 102 lines/6,439
  characters of current-state prose (26% reduction). While in §6, checked
  the rest of the section for the same pattern at a similarly bloated scale
  (per S-116's own conservatism principle — not rewriting prose that was
  already current-state) and found two more genuine pockets, both fixed the
  same way: §6.3 (Data sync flow)'s four stacked "Tier 0 status (S-012)"/
  "S-026/ADR-0029 status"/"2026-07-20/ADR-0032/S-057 status"/
  "2026-07-20/S-061 status" paragraphs (77 lines/4,892 characters → 41
  lines/2,495 characters, 49% reduction), and §6.8 (Account deletion
  flow)'s "Built as (S-025)"/"S-026 addition"/"S-072 addition"/"S-073
  addition" paragraphs (50 lines/2,917 characters → 43 lines/2,549
  characters). §6.2, §6.4, §6.6, §6.9, and §6.10's remaining single "Tier 0
  status"/"Built as" paragraphs were checked and left alone — they read as
  current, load-bearing diagram-correction content, not accreted dated
  narration, matching the bar S-116 already applied. Whole document: 1,254
  lines/98,350 characters → 1,174 lines/93,196 characters (6.4%/5.2%
  reduction). No ADR/REQ/COMP pointer was lost — every ADR-xxxx/REQ-xxx/
  COMP-xxx reference present in each rewritten pocket before the edit was
  grepped and confirmed still present after (§6.3's rewrite additionally
  gained one correct pointer, ADR-0015, for the override-precedence rule it
  already described). Fixed three dangling cross-references discovered by
  grepping the whole `docs/` tree for "status note"/"status notes" phrases
  pointing at the removed prose: two within `architecture-document.md`
  itself (§6.2's "see COMP-04's status note in §5" → "see §5's COMP-04 row";
  §6.2c's "see COMP-05/COMP-11's own S-089 status note" → removed, since the
  same paragraph already states the full account inline) and one in
  `docs/requirements-document.md` (REQ-215's "see architecture-document.md's
  COMP-05/COMP-11 status note" → repointed to §5.2's cross-component method
  inventory, which already lists `GetCellCategoryTypesAsync`/REQ-215
  explicitly). Frontmatter `version`/`last_updated` bumped on both files;
  `architecture-document.md`'s in-body "Version 0.98 · 2026-08-11" line
  (already back in sync with frontmatter since S-116 fixed a prior drift)
  bumped to match, 0.99 · 2026-08-16. Quality-gate review (`architecture-reviewer`
  and `quality-architect`, independently) caught two pre-existing stale
  identifiers carried forward unexamined into the rewritten §6.1/§6.3 prose
  — `GridGameModule.SelectPairing` (actually `GridGenerationService
  .SelectPairing`, per §5's own COMP-05 row) and `IPlayerStoreRepository`/
  `PlayerStoreRepository.Approve/RemovePlayerDataAsync` (that repository no
  longer exists post-ADR-0067; the correct names are
  `IPlayerDataRepository`/`PlayerDataRepository.Approve/RemovePlayerDataAsync`,
  per §5's own COMP-06 row) — both verified against the actual backend code
  and corrected in the same commit. Two further pre-existing issues found
  during review are explicitly out of this story's scope and left for a
  follow-up: §6.2c cites ADR-0052 where it should cite ADR-0053 for
  player-suggestion admin views, and §6.10 still names the pre-split
  `XGArcade.Testing`/COMP-09 rather than `Testing.SeedManager`; neither line
  was touched by this diff.

- 2026-08-16 — no docs changed beyond this entry — S-122 (`docs/backlog.md`
  Epic 9): added direct repository-level tests to
  `PlayerDataQualityRepositoryTests.cs` for the five
  `IPlayerDataQualityRepository` methods that previously had only indirect
  coverage via `GridGameModuleTests`/`PlayerCacheWarmingServiceTests`
  (`IsConfirmedLowAsync`/`RecordConfirmedLowAsync`/
  `IsPersistentTechnicalFailureAsync`/`RecordTechnicalFailureAsync`/
  `ClearTechnicalFailureAsync`, REQ-110) — closing the gap flagged in
  S-107's own note (Epic 8). Also renamed one new test to the
  `REQ###_...` convention. Pure test addition, no production code touched,
  no behavior change; `requirements-document.md`/`architecture-document.md`/
  `implementation-document.md` all checked against their `update_when`
  triggers and confirmed unchanged — REQ-110's documented behavior,
  `PlayerDataQualityRepository`'s structure (`implementation-document.md`
  §5's `ConfirmedLowMatchPair`/`PairLookupFailure` entries), and the
  COMP-06/Games.XGGrid boundary are all exactly as already documented; no
  ADR needed (confirmed during the quality gate).

- 2026-08-16 — `docs/implementation-document.md` (v0.98) + `CODEBASE_ANALYSIS.md`
  — S-121 (`docs/backlog.md` Epic 9, branch
  `claude/leaderboard-screen-split-syb081`): split `LeaderboardScreen.tsx`
  (1,129 lines, 4 independent state machines for the all-time/live/
  past-rounds/window scopes) into `AllTimeLeaderboard.tsx`/
  `LiveLeaderboard.tsx`/`PastRoundsLeaderboard.tsx`/`WindowedLeaderboard.tsx`
  plus shared `LeaderboardRowsList.tsx` (row/footer rendering), all in
  `frontend/src/leaderboard/`. None of the four scopes' fetch shapes
  actually fit S-120's `useAuthedFetch` hook (each has its own poll/
  pagination/deferred-fetch lifecycle the hook doesn't cover), so each keeps
  a small local `handleAuthError` helper instead — confirmed consistent
  with `coding-guidelines.md`'s existing scoped description of that hook,
  no change needed there. `LeaderboardScreen.tsx` (now 261 lines) is a thin
  orchestrator that always mounts all four scope components, gating visible
  output via an `active` prop rather than conditional mount/unmount, to
  preserve the all-time poll running continuously and each scope's loaded
  state surviving a tab switch away/back. Test coverage relocated (not
  rewritten) from `LeaderboardScreen.test.tsx` into matching per-component
  test files, with shared test helpers extracted into
  `leaderboardTestHelpers.ts`. Pure structural refactor, no REQ/behavior
  change; `architecture-reviewer` and `quality-architect` both passed clean
  and `architecture-reviewer` explicitly concluded no ADR is needed (same
  reasoning as S-120: the always-mount pattern is dictated by the
  pre-existing no-visual/behavior-change acceptance criterion, not a fresh
  architectural fork, and is scoped to one screen with a single caller).
  `docs/requirements-document.md` and `docs/architecture-document.md`
  checked and left unchanged — their existing `LeaderboardScreen.tsx`
  references describe screen-level behavior (still true) or high-level
  `CONT-01` responsibilities, not internal file structure, so nothing there
  went stale. `docs/implementation-document.md` §4's `/leaderboard`
  project-structure entry was updated to name the four new scope
  components and the shared row list directly, same pattern S-119 used for
  `XGArcade.Games.XGGrid`'s split. `CODEBASE_ANALYSIS.md` §1/§5's prior
  "watch-only, no action" call on this file (2026-08-11 revision) is marked
  superseded in place, per the reasoning already on record in
  `docs/backlog.md`'s S-121 entry — a full re-verified revision of that
  report is still `code-health-auditor`'s job, not done here.
- 2026-08-16 — `docs/coding-guidelines.md` (v0.8) — S-120 (`useAuthedFetch`
  hook promotion, branch `claude/shared-auth-fetch-hook-m31oms`): updated
  the fetch-on-mount hook guideline's name/path reference from
  `useAdminSectionFetch` (`frontend/src/admin/useAdminSectionFetch.ts`) to
  `useAuthedFetch` (`frontend/src/lib/useAuthedFetch.ts`), and rewrote the
  closing sentence from forward-looking anticipation to present tense now
  that the promotion outside `AdminScreen.tsx` (into `LeaguesScreen.tsx`)
  has actually happened.
- 2026-08-11 — `docs/architecture-document.md` (v0.98) +
  `docs/requirements-document.md` (v1.71) + `docs/implementation-document.md`
  (v0.97) + new `docs/decisions/0068-grid-game-module-responsibility-split.md`
  — S-119 (`docs/backlog.md` Epic 9): split `GridGameModule.cs` (1,039 lines,
  26 methods, 13 constructor dependencies — confirmed COMP-05's clearest
  SRP outlier, second-worst code-health hotspot platform-wide) into three
  new, independently-registered classes behind their own narrow interfaces
  — `IGridGenerationService`/`GridGenerationService` (grid generation:
  pairing selection, header picking, cell construction, REQ-101/102/107/108),
  `IGridNameMatcher`/`GridNameMatcher` (three-stage name matching and
  disambiguation, REQ-207/208/209, plus REQ-216's wrong-guess name/photo
  resolution), and `IGridLiveLookupDispatcher`/`GridLiveLookupDispatcher`
  (the shared Country/Club/Trophy → `IWikidataLookupService` dispatch used
  by both generation-time cache-miss fallback and REQ-211's guess-time
  fallback) — composed behind `GridGameModule`, now a thin ~160-line
  `IGameModule` adapter that keeps implementing that interface directly
  (unlike ADR-0067's repository split, `IGameModule` has real external
  callers — `Core.Scoring`, `Core.Rounds`, `XGArcade.Api`,
  `IGameModuleResolver` — so there is no "delete the original file" step;
  `GridGameModule.cs` stays, shrunk, retaining only the small set of
  trivial single-repository-call `IGameModule` methods with no other
  owner). No facade added — `GridGenerationService` injects
  `IGridLiveLookupDispatcher` directly, the one cross-dependency between
  the new classes; a caller needing more than one narrowly injects more
  than one. `CategoryCandidate` moved from a private nested type to its
  own file (namespace-`internal`, shared by two of the new classes);
  `CategoryPairingRules` gained one new public static method,
  `MapAttributeType`, moved from a private method on the old god-class
  (deliberately not tripled across the three new classes — a single,
  stateless, dependency-free lookup table with exactly one correct
  implementation). Pure structural refactor, no REQ change, no behavior
  change; existing `GridGameModuleTests.cs` coverage (2,345 lines,
  90 test methods) moved/renamed 1:1 into `GridGenerationServiceTests.cs`/
  `GridNameMatcherTests.cs`/`GridLiveLookupDispatcherTests.cs` plus a
  slimmed `GridGameModuleTests.cs` for the adapter's own orchestration
  tests, confirmed by a mechanical method-name diff against the original
  file (zero drops, zero duplicates). `architecture-reviewer` and
  `quality-architect` review passes both passed clean; two stale
  doc-comment references caught by `quality-architect` (a
  `MapAttributeType` comment pointing at a since-moved method, two
  `LookupLiveMatchesAsync` references in `CategoryCandidate.cs` naming the
  pre-refactor method name) were fixed in the same PR, not deferred.
  Updated `docs/architecture-document.md` §5's COMP-05 row and §5.3's
  COMP-05 ADR list to attribute generation/matching/live-lookup to the
  right class(es) — no REQ references in that row changed, since none of
  the underlying behavior did. `docs/requirements-document.md` and
  `docs/implementation-document.md` each gained a blanket
  "`GridGameModule`-split note" (same pattern as ADR-0067's existing
  repository-split note in both docs) mapping any pre-2026-08-11
  `GridGameModule.<Method>`/`GridGameModuleTests.cs` reference to its new
  owner instead of individually rewriting the many scattered historical
  references — no REQ acceptance criteria changed. `implementation-document.md`
  §4's project-structure entry for `XGArcade.Games.XGGrid` was also updated
  to name the three new classes directly. See ADR-0068 for the full
  decision record.
- 2026-08-11 — no docs changed beyond this entry — S-118 (`docs/backlog.md`
  Epic 9): extends S-100/S-101's shared HTTP/timeout driver concept to the
  six `WikidataClient.cs` query methods added by later ADRs
  (`QueryPlayerPoolBirthYearAsync`/`QueryPlayerPoolByNationalityAsync`
  (ADR-0054/ADR-0055), `QueryPlayerPositionsAndBirthYearsByQidsAsync`/
  `QuerySitelinkCountsByQidsAsync` (ADR-0056),
  `QueryPlayerCareerStintsByQidsAsync` (ADR-0054), and
  `QueryPlayerCareerAndNationalityByNameAsync` (ADR-0053)) that had never
  been migrated and still hand-rolled their own HTTP send/timeout-CTS/
  catch-throw logic. New private generic `RunThrowingQueryAsync<T>` is a
  second, "always throws `WikidataQueryException`" sibling to S-100/S-101's
  `RunIntersectionQueryAsync` (which swallows to `[]` unless
  `throwOnTimeout`) — kept as a separate method rather than one shared,
  flag-parameterized method, since the two error contracts are genuinely
  different per-method, not a single axis to switch on. Deliberately does
  NOT route through `WikidataQueryTimeoutTier` (unlike the 9 intersection
  queries) — that enum resolves a timeout from two independent axes
  (`throwOnTimeout` + `timeoutTier`) none of these six methods have; each
  always uses one of `WikidataClient`'s four fixed timeout fields directly
  (`_queryTimeout` for five of the six, `_adminLookupQueryTimeout` for
  `QueryPlayerCareerAndNationalityByNameAsync`), preserving the existing
  decoupling between `_adminLookupQueryTimeout` and
  `_cacheWarmingQueryTimeout` (independently-reasoned budgets that happen
  to share a 45s value today) rather than force-fitting both into one
  shared tier. All 6 methods are now thin wrappers; `Build*Query`/`Parse*`
  methods and every exception message/timeout value are byte-for-byte
  unchanged. Pure refactor, no new REQ IDs, no component boundary crossed
  (still entirely inside `XGArcade.DataSync`) — no ADR, same reasoning as
  S-100/S-101. Regression proof: 12 new tests in `WikidataClientTests.cs`
  (`REQ118_*_SentQuery_IsByteForByteIdenticalToPreRefactorOutput` and
  `REQ118_*_Timeout_Reports*Budget_NotAnotherBudget` per method, renamed
  from an initial `S118_*` prefix to match the `REQ###_*` convention
  `docs/coding-guidelines.md` and S-100/S-101's own tests already use),
  full existing suite otherwise unchanged. `WikidataClient.cs` line count:
  1,815 → 1,778 (-37 lines; the new shared driver's own doc comment offsets
  most of the six per-method savings it enables).
  Not run against `dotnet test` from this sandbox (no SDK available) —
  hand-traced against each method's pre-refactor source instead; CI must
  confirm. `quality-architect` review pass: refactor correctness, the
  two-driver design, and the timeout-tier decision all confirmed sound;
  found the new driver's doc comment overclaimed "every throwing query
  method in this file" when `QueryPlayerPhotosByQidsAsync`/
  `QueryPlayerPhotoByNameAsync` also match the shape but were outside
  S-118's scoped method list — comment corrected in place to name both as
  not-yet-migrated, and a follow-up story (S-124) added to `docs/backlog.md`
  to close that gap rather than pulling it in opportunistically here.
- 2026-08-11 — architecture-document.md (v0.97), CODE_HEALTH_ASSESSMENT.md
  (new) — S-115/S-116 (`docs/backlog.md` Epic 9): added a CodeScene/SonarQube-style
  numeric (1-10) code health assessment covering every backend/frontend/infra
  module, complementing `CODEBASE_ANALYSIS.md`'s existing priority-list
  format. Separately, slimmed `docs/architecture-document.md` §5 (Components)
  from 629 lines/88K characters down to 125 lines/~13K characters — the
  per-component table cells and ~600 lines of trailing "COMP-XX status
  (DATE, story)" prose had accreted an unbounded dated changelog inline
  (one cell alone was 14,718 characters) since every ADR already records
  the same history in full. Cells now describe current state only; a new
  §5.3 "Component evolution reference" points each component to its ADR
  trail in one line instead of re-narrating it. §6 (data flows, 862 lines)
  was checked and found not similarly accreted — left untouched — except a
  smaller ~135-line pocket at the start of §6.1, tracked as a follow-up
  (see Epic 9, S-123). No boundary rules, current-state facts, or ADR
  references were removed — see `CODE_HEALTH_ASSESSMENT.md` for the
  reasoning and `docs/ai/agent-migration-plan.md` §8 for the new
  `code-health-auditor` agent (S-117) this pattern is now owned by.
- 2026-08-11 — coding-guidelines.md (v0.7) — S-113 (`docs/backlog.md` Epic
  8): decided and documented `backend/src/XGArcade.Api/CompositionRoot/*.cs`'s
  testing strategy, which had happened by default (integration-only, via
  `XGArcade.Api.Tests`'s `WebApplicationFactory` suite) when S-102 moved
  this code out of `Program.cs` as a pure reorganization, not as a
  deliberate choice. New "Composition-root testing" convention: these files
  stay integration-tested by default (they're wiring, not logic), with
  unit tests reserved for genuine conditional logic worth isolating on its
  own. Applied that call today: `AuthSetup.cs`'s `IsLocalE2EAuth` (ADR-0006's
  "never guarded only by config alone" principle, applied to auth) and
  `GetClientIpPartitionKey` (REQ-606/REQ-717's rate-limit partition key) are
  real, pure, security/correctness-relevant branches — marked `internal`
  (new `AssemblyInfo.cs` + `InternalsVisibleTo`) and covered by new
  `AuthSetupTests.cs`. `CliVerbDispatcher.cs`, `EndpointMapping.cs`, and
  `ServiceRegistration.cs` have no comparable logic today, so their
  integration-only coverage is confirmed intentional, not a gap. No REQ/ADR
  changes — a testing-convention decision, not a structural one.
- 2026-08-11 — no docs changed beyond this entry — S-112 (`docs/backlog.md`
  Epic 8): restructured `backend/src/XGArcade.Api/CompositionRoot/CliVerbDispatcher.cs`'s
  single 667-line sequential `TryHandleAsync` into a `Verbs` lookup table
  (`Dictionary<string, Func<string[], Task<bool>>>`) mapping each literal
  verb string to its own named `Handle<Verb>Async` method — same
  spec-table-plus-shared-driver shape as `WikidataClient.cs`'s S-100/S-101
  refactor. `TryHandleAsync` itself is now a thin `Verbs.TryGetValue`
  lookup. Preserves both pre-existing match shapes exactly: the 8
  exact-match verbs (`migrate-and-seed`, `warm-player-cache`,
  `import-player-name-index`, `backfill-player-photos`,
  `backfill-player-position-birthyear`, `prefetch-player-careers`,
  `verify-wikidata-player-data`, `audit-club-gaps`) each start with an
  explicit `if (args.Length != 1) return false;` to reproduce the old
  `args is ["verb"]` silent-fallthrough-to-server-start behavior on extra
  arguments; the 4 prefix-match verbs (`clean-stale-club-attributes`,
  `clear-pair-lookup-failures`, `clean-duplicate-career-stints`,
  `purge-player-pool`) keep their own internal argument validation and
  `throw new InvalidOperationException(...)` on a malformed shape,
  unchanged. Every verb's body, doc comments, `Console.WriteLine` text, and
  exception messages were moved verbatim, not rewritten (confirmed via a
  whitespace-normalized diff against the pre-refactor file — the only
  differences are the expected structural ones: method signatures, the
  `Verbs` dictionary, and the new `if (args.Length != 1) return false;`
  guards). Pure refactor, no behavior change, no new REQ IDs, no component
  boundary crossed — no ADR, same reasoning as S-100/S-101. No dedicated
  unit tests exist for this file (S-113 tracks that gap separately);
  `dotnet` SDK unavailable in this sandbox, so `dotnet build`/`dotnet test`
  could not be run here — verification was a manual line-by-line re-read of
  each handler against the original plus the normalized-diff check above;
  CI's `dotnet test` run is the actual regression net.
- 2026-08-11 — no docs changed beyond this entry — S-114 (`docs/backlog.md`
  Epic 8, direct follow-up to S-112's own `quality-architect` review
  finding): extracted the boilerplate all 12 `CliVerbDispatcher.cs` verb
  handlers repeated — `ConfigurationBuilder().AddEnvironmentVariables().Build()`
  → `GetConnectionString("Database")` (throwing
  `InvalidOperationException("ConnectionStrings:Database is not configured.")`
  when missing) → `DbContextOptionsBuilder<XGArcadeDbContext>().UseNpgsql(...)`
  → `new XGArcadeDbContext(...)` — into a single private static
  `BuildDbContext()` helper, and the `LoggerFactory.Create(b => b.AddConsole()
  .SetMinimumLevel(LogLevel.Information))` block 6 of those handlers
  (`HandleWarmPlayerCacheAsync`, `HandleImportPlayerNameIndexAsync`,
  `HandleBackfillPlayerPhotosAsync`,
  `HandleBackfillPlayerPositionBirthYearAsync`,
  `HandlePrefetchPlayerCareersAsync`, `HandleAuditClubGapsAsync`) also
  repeated into a `BuildLoggerFactory()` helper. Every handler now calls
  these instead, keeping its own existing local variable name (e.g.
  `warmingDbContext`, `auditLoggerFactory`) and its position relative to
  other statements — including the two handlers
  (`HandleCleanStaleClubAttributesAsync`, `HandlePurgePlayerPoolAsync`)
  whose own confirmation-argument validation still runs, unchanged, before
  the (now one-line) DbContext construction. `CliVerbDispatcher.cs` shrinks
  from 735 to 621 lines. Confirmed via a whitespace-normalized diff against
  the pre-refactor (post-S-112) file that the only differences are the two
  new helper methods plus each handler's boilerplate collapsing to 1-2
  lines — no `Console.WriteLine`/exception text, control flow, or ordering
  changed anywhere; `Program.cs`'s call site is untouched. Pure refactor,
  no behavior change, no new REQ IDs, no component boundary crossed — no
  ADR, same reasoning as S-112. `dotnet` SDK unavailable in this sandbox,
  so `dotnet build`/`dotnet test` could not be run here — verification was
  the normalized-diff check plus a manual top-to-bottom re-read of all 12
  handlers; CI's `dotnet test` run is the actual regression net.
- 2026-08-11 — `docs/backlog.md` — implemented S-111 (`docs/backlog.md`
  Epic 8): split `frontend/src/lib/api.ts` (1,057 lines, 51 exports) into a
  shared `apiClient.ts` (`ApiError`/`throwApiError`/`describeError`/
  `API_BASE_URL`) plus eight domain files — `auth.ts`, `rounds.ts`,
  `path.ts`, `leaderboard.ts`, `admin.ts`, and three the original story
  text didn't name but whose functions fit none of the other five:
  `leagues.ts`, `announcements.ts`, `incidents.ts`. Original `api.ts`
  deleted; all 28 call sites' import paths updated. Pure refactor — no
  behavior change, no new REQ IDs; `tsc -b`/`oxlint`/`vitest run` (581
  tests) all pass with test bodies untouched.
- 2026-08-11 — `docs/architecture-document.md` + `docs/implementation-document.md`
  + `docs/backlog.md` — implemented S-110 (`docs/backlog.md` Epic 8):
  synced both docs' `Program.cs` references with S-102's
  `CompositionRoot/{AuthSetup,CliVerbDispatcher,EndpointMapping,
  ServiceRegistration,WikidataHttpClientConfiguration}.cs` split — the
  folder-structure block in `implementation-document.md` §4 now shows
  `CompositionRoot/` alongside the now-thin `Program.cs`, and every
  `Program.cs`-attributed statement about pipeline order, auth wiring,
  admin/test-data-endpoint gating, CLI verbs, and scoring-strategy
  registration now names the specific `CompositionRoot/*.cs` file that
  actually contains it, with "(called from `Program.cs`)" where that's
  still accurate context. Docs-only, no code changes — S-102's own PR
  #172 was pure reorganization and made none of these docs stale on
  purpose; this closes the gap.
- 2026-08-11 — `docs/backlog.md` — implemented S-108 (`docs/backlog.md`
  Epic 8, batch 1): backfilled dedicated test files for 5 of the 9
  components S-103 extracted from `AdminScreen.tsx`
  (`PlayerSuggestionsEntry.test.tsx`, `IncidentReportsEntry.test.tsx`,
  `AnnouncementBannerSection.test.tsx`, `AccountMetricsSection.test.tsx`,
  `XGPathCycleSection.test.tsx`), each rendering its component directly and
  stubbing only the routes that component itself calls, rather than the
  full `AdminScreen` tree. Pure test-coverage backfill — no behavior
  change, no new REQ IDs; `AdminScreen.test.tsx` left unchanged (its
  existing full-tree coverage of these 5 components is now redundant with,
  not replaced by, the new files — same "left unchanged" option the story
  itself allows). Remaining 4 components + `useAdminSectionFetch.ts` are
  S-109 (batch 2, not yet landed).
- 2026-08-11 — `docs/architecture-document.md` + `docs/requirements-document.md`
  + `docs/implementation-document.md` + `docs/decisions/0067-player-store-repository-split.md`
  + `docs/backlog.md` — implemented S-107 (`docs/backlog.md` Epic 8, the
  independent second half of S-106's split): split the remaining five
  `IPlayerStoreRepository` concerns (21 of its original 43 methods) into
  four more new, independently-registered repositories
  (`IPlayerOverrideRepository`, `IPlayerBackfillRepository`,
  `IPlayerCareerStintRepository`, `IPlayerDataQualityRepository`), rewiring
  every call site (`GridGameModule`, `PlayerCacheWarmingService`,
  `XGPathGameModule`, `WikidataLookupService`,
  `PlayerCareerStintRefreshService`, `PlayerCareerPrefetchService`,
  `ClubGapAuditService`, `PlayerPhotoBackfillService`,
  `PlayerPositionBirthYearBackfillService`, `CliVerbDispatcher`'s
  hand-built CLI verbs, and the admin/round/path API endpoints) to depend
  only on the narrower interface(s) each actually calls, with no facade.
  Pure refactor — no behavior change, no new REQ IDs; existing
  `PlayerStoreRepositoryTests.cs` coverage for these five concerns
  moved/renamed into four new test files rather than being rewritten. Both
  halves of the split (S-106, S-107) have now landed, so the original
  `PlayerStoreRepository.cs`/`IPlayerStoreRepository.cs` files are deleted
  — COMP-06 is now eight independently-registered repositories. No new
  structural question came up, so this extended ADR-0067 (S-106's own ADR)
  with an "S-107 update" section rather than adding a second ADR.
- 2026-08-11 — `docs/architecture-document.md` + `docs/requirements-document.md`
  + `docs/implementation-document.md` + `docs/decisions/0067-player-store-repository-split.md`
  — implemented S-106 (`docs/backlog.md` Epic 8): split
  `IPlayerStoreRepository`'s Player/PlayerData/PlayerAttribute/PlayerAlias
  concerns (22 of its original 43 methods) into four new, independently-
  registered repositories (`IPlayerRepository`, `IPlayerDataRepository`,
  `IPlayerAttributeRepository`, `IPlayerAliasRepository`), rewiring every
  call site (`GridGameModule`, `XGPathGameModule`, `WikidataLookupService`,
  `PlayerCacheWarmingService`, `PlayerFamiliarityService`,
  `PlayerCareerPrefetchService`, `PlayerCareerStintRefreshService`,
  `GuessSubmissionService`, and the admin/round/path API endpoints) to
  depend only on the narrower interface(s) each actually calls, with no
  facade. Pure refactor — no behavior change, no new REQ IDs; existing
  `PlayerStoreRepositoryTests.cs` coverage for the four moved concerns
  moved/renamed into four new test files rather than being rewritten.
  `IPlayerStoreRepository`/`PlayerStoreRepository` remain, scoped to the
  five concerns S-107 (independent, not yet landed) will split out next —
  see ADR-0067 for the full decision record.
- 2026-08-11 — `CODEBASE_ANALYSIS.md` (root) + `docs/backlog.md` (new
  Epic 8: S-106 through S-113) — extended the codebase analysis from a
  top-5 to a top-10 list now that Epic 7's items and security are settled
  ground. New actionable findings: `PlayerStoreRepository.cs`/
  `IPlayerStoreRepository.cs` spans 9 unrelated concerns across 44 methods
  (confirmed the outlier via a method-count comparison across every
  repository in the codebase — split into S-106/S-107); `AdminScreen.tsx`'s
  9 S-103-extracted components have zero dedicated tests (S-108/S-109);
  `architecture-document.md`/`implementation-document.md` still describe
  the pre-S-102 shape of `Program.cs` (S-110, docs-only); `api.ts`'s size,
  carried over unaddressed since the original report (S-111);
  `CliVerbDispatcher.cs` is one 649-line method, a direct byproduct of
  S-102 (S-112); `CompositionRoot/*.cs`'s testing strategy was defaulted
  into rather than decided (S-113). Four watch-only items (large test
  files, `LeaderboardScreen.tsx`, `AuthController.cs`,
  `SuggestionsScreen.tsx`) were deliberately left without stories per this
  epic's own low-churn doctrine. Two suspected findings (Grid/Path frontend
  "duplication," two separate `FakeWikidataClient.cs` fakes) were
  investigated and explicitly cleared rather than reported. No
  requirements/architecture changes accompany this entry beyond what S-110
  itself will make when it runs — this entry only plans the work.
- 2026-08-11 — `CODEBASE_ANALYSIS.md` (root, not `docs/` — noted here for
  traceability) — full re-scan of `main` now that Epic 7 (S-099–S-105) is
  fully merged, including S-104. Closed out Epic 7 in the report (all 7
  items resolved, no open security or P1 findings) and identified the next
  batch of priorities from a fresh sweep: `PlayerStoreRepository.cs`/
  `IPlayerStoreRepository.cs` spans ~9 unrelated sub-entity concerns in one
  772/482-line file (new P2), and `AdminScreen.tsx`'s S-103 extraction left
  its 9 newly split-out components with zero dedicated test files, still
  only covered indirectly via the unsplit `AdminScreen.test.tsx` (new P2).
  `frontend/src/lib/api.ts`'s size (carried over, unaddressed) and a few
  large-but-low-churn files were noted as lower-priority watch items. No
  requirements/architecture changes accompany this entry — the scan
  re-assessed existing code, it didn't change any.
- 2026-08-10 — no docs changed beyond this entry — S-100 (`docs/backlog.md`
  Epic 7): `backend/src/XGArcade.DataSync/Wikidata/WikidataClient.cs`'s three
  non-trophy intersection pairs (`QueryCountryClubIntersectionAsync`,
  `QueryNationalTeamClubIntersectionAsync`, `QueryClubClubIntersectionAsync`)
  now go through a `(CategoryType, CategoryType)`-keyed spec table
  (`CategoryType.cs`, `IntersectionQuerySpec.cs`, `IntersectionQuerySpecs.cs`,
  new files) and a shared `QueryIntersectionAsync` driver that centralizes
  the `WikidataQid.IsValid` guard previously duplicated per method. Public
  method signatures/behavior unchanged (thin wrappers); the remaining six
  trophy-involving pairs are untouched pending S-101. Pure refactor, no new
  REQ IDs, no component boundary crossed (still entirely inside
  `XGArcade.DataSync`) — no ADR, following the same reasoning as the
  `useAdminSectionFetch` entry below. Regression proof: three new
  byte-for-byte SPARQL-string assertions in `WikidataClientTests.cs`
  (`REQ100_Query*IntersectionAsync_SentQuery_IsByteForByteIdenticalToPreRefactorOutput`),
  full existing suite otherwise unchanged.
- 2026-08-10 — no docs changed beyond this entry — S-101 (`docs/backlog.md`
  Epic 7): extends S-100's spec table to the remaining six trophy-involving
  intersection pairs (`QueryTrophyCountryIntersectionAsync`,
  `QueryTrophyClubIntersectionAsync`, `QueryTeamTrophyCountryIntersectionAsync`,
  `QueryTeamTrophyNationalTeamIntersectionAsync`,
  `QueryTeamTrophyClubIntersectionAsync`,
  `QueryTrophyNationalTeamIntersectionAsync`) — `CategoryType.cs` gains
  `Trophy`/`TeamTrophy`, `IntersectionQuerySpecs.cs` gains the six
  corresponding spec entries, and all 9 `Query*IntersectionAsync` methods on
  `WikidataClient.cs` are now thin wrappers over the shared
  `QueryIntersectionAsync` driver. The now-dead standalone `Build*Query`
  methods for these six pairs are deleted (moved, unchanged, to
  `IntersectionQuerySpecs.cs`). Public method signatures/behavior unchanged;
  pure refactor, no new REQ IDs, no component boundary crossed — no ADR,
  same reasoning as S-100. Regression proof: six new byte-for-byte
  SPARQL-string assertions in `WikidataClientTests.cs`
  (`REQ100_Query*IntersectionAsync_SentQuery_IsByteForByteIdenticalToPreRefactorOutput`),
  full existing suite otherwise unchanged. `WikidataClient.cs` line count:
  1,977 → 1,815 (-162 lines).
- 2026-08-11 — `CODEBASE_ANALYSIS.md` (§4 hotspot row updated, no version
  header) — S-104 (`docs/backlog.md` Epic 7): flattened
  `backend/src/XGArcade.Games.XGGrid/GridGameModule.cs`'s deepest-nested
  branches into named private methods/early-returns. Four extractions: the
  `ResolveWrongGuessPlayerAsync` try/catch around the optional live-photo
  lookup → `TryLookupLivePhotoAsync`; `BuildDisambiguationCandidatesAsync`'s
  ternary/LINQ block → `GetDistinguishingAttributeValues`;
  `PickHeadersAsync`'s three abort-condition checks →
  `EnsurePickingCanContinue`/`ThrowDeadlineExceeded`, and its inner
  match-count for-loop → `TryComputeMatchCountsAsync`; `BuildCells`'s nested
  for-loop object initializer → `CreateCell`. Public method
  signatures/behavior unchanged — same exception types/messages, same log
  templates, same generation/scoring outcomes; pure refactor, no new REQ
  IDs, no component boundary crossed (still entirely inside
  `XGArcade.Games.XGGrid`) — no ADR, same reasoning as S-100/S-101/S-103.
  Regression proof: full `GridGameModuleTests.cs` suite (119 tests)
  unchanged and passing, full backend suite (1,384 tests across all 6 test
  projects) unchanged and passing. Nesting (lines at ≥5 indent levels, same
  heuristic `CODEBASE_ANALYSIS.md` §4 used): 25 → 3. `GridGameModule.cs`:
  983 → 1,032 lines, 23 → 30 methods (net +49 lines from the extracted
  methods' own doc comments, offset by removed duplication).
- 2026-08-11 — `docs/coding-guidelines.md` (path correction only) — S-103
  (`docs/backlog.md` Epic 7): finished the `AdminScreen.tsx` God-Component
  extraction that `#167` started (`useAdminSectionFetch`).
  `PlayerSuggestionsEntry.tsx`, `IncidentReportsEntry.tsx`,
  `AnnouncementBannerSection.tsx`, `UnverifiedDataSection.tsx`,
  `AccountMetricsSection.tsx`, `GuestClearSection.tsx`,
  `XGPathCycleSection.tsx`, `RoundControlSection.tsx`, and
  `UserDeletionSection.tsx` are now each their own file under
  `frontend/src/admin/`, and `useAdminSectionFetch` moved out of
  `AdminScreen.tsx` into its own module,
  `frontend/src/admin/useAdminSectionFetch.ts` — `docs/coding-guidelines.md`'s
  reference to the hook's location is updated to match (no convention
  change, so no version bump). `AdminScreen.tsx`: 1,432 → 190 lines, 16 → 3
  `useState` (`pageState`, `unverifiedRows`, `activeRound` remain — the rest
  now live in their own components). Pure refactor, no new REQ IDs, no
  props/copy/class-name changes, `AdminScreen.css` untouched (zero diff), no
  component boundary crossed (still entirely inside `frontend/src/admin/`)
  — no ADR, same reasoning as S-100/S-101. Regression proof: existing
  `AdminScreen.test.tsx` unchanged and passing 56/56, full frontend suite
  543/543 unchanged, tsc and lint clean — this was pure code motion, not new
  testable behavior, so no new REQ-named tests were added.
- 2026-08-10 — `docs/backlog.md` (new Epic 7: S-099 through S-105) — added
  a technical-debt-remediation epic from `CODEBASE_ANALYSIS.md`'s findings
  (WikidataClient.cs duplication/size, Program.cs composition-root sprawl,
  AdminScreen.tsx God Component, GridGameModule.cs nesting, a high-severity
  transitive `undici` dev-dependency, and an optional comment-dedup pass).
  Pure-refactor stories, no new REQ IDs — no requirements/architecture doc
  changes accompany this entry since no behavior or boundary has changed
  yet, only the plan to touch that code.
- 2026-08-10 — `docs/coding-guidelines.md` (v0.5 → v0.6) — documented the
  new `useAdminSectionFetch` convention (`frontend/src/admin/AdminScreen.tsx`):
  a shared hook extracted from five previously hand-rolled fetch-on-mount +
  401-escalate/403-hide/other-error-inline + unmount-cancellation-guard
  implementations. Pure refactor of REQ-512/REQ-904/REQ-511/REQ-507/REQ-1209's
  internal implementation only — no acceptance criteria, component boundary,
  or data flow changed for any of them, so requirements-document.md and
  architecture-document.md are unchanged; no ADR (no boundary crossed, stays
  a local hook inside one existing component).
- 2026-08-10 — `docs/requirements-document.md` (REQ-904, new, v1.67 →
  v1.68 — already drafted and bumped earlier this session),
  `docs/decisions/0066-admin-github-issue-polling-cache.md` (new, already
  written and Accepted earlier this session), `docs/architecture-document.md`
  (COMP-12 extended with a REQ-904/ADR-0066 status-note addition, §10 ADR
  table gained the missing ADR-0066 row, v0.92 → v0.93), `docs/backlog.md`
  (S-098 updated from "queued, not started" to built/tested) — admin
  notification for open in-app incident reports (REQ-904), the second of
  the two follow-on stories queued alongside REQ-511's banner (S-096) and
  REQ-512's suggestion badge (S-097). Unlike S-097, REQ-903/ADR-0064
  deliberately keeps no in-app record of a created incident, so there was
  no existing data source to badge against — `IGitHubIssueClient` gained
  `ListOpenIssuesByLabelAsync` (same PAT as `CreateIssueAsync`, no scope
  widening), fronted by a new `ICachedIncidentIssueSummaryProvider`/
  `CachedIncidentIssueSummaryProvider` (`XGArcade.Core.IncidentReporting`)
  wrapping the GitHub read in a single shared `IMemoryCache` entry
  (default 60s TTL, `GitHub:IncidentReportCacheTtlSeconds`) with
  stale-serve-on-failure semantics — this codebase's first use of
  `Microsoft.Extensions.Caching.Memory`, added as a direct
  `XGArcade.Core.csproj` package reference. `GET /admin/incident-reports`
  (new file, `XGArcade.Api.Admin.AdminIncidentReportEndpoints`), same
  `"Admin"` policy every other admin endpoint uses, no new authorization
  policy introduced. Frontend: a new `IncidentReportsEntry` section in
  `AdminScreen.tsx` (placed after S-097's `PlayerSuggestionsEntry`),
  fetching once on load, rendering the count's absence rather than `(0)`
  at zero and a distinct inline message for the "no successful poll yet"
  failure state; a new `.admin-screen__link` class styles the "view on
  GitHub" link-out using only existing tokens (`--color-text-primary`,
  `--touch-target-min`) — `docs/design-document.md` confirmed unchanged
  (no new color/typeface/animation introduced), and
  `docs/implementation-document.md` confirmed unchanged (its tech-stack
  table tracks layer-level choices, not individual component-level
  packages — `Microsoft.Extensions.Caching.Memory` is covered by
  ADR-0066/COMP-12 in the same way `PartitionedRateLimiter` was for
  REQ-903's rate limiting, never added to that table). Full quality-gate
  run (`architecture-reviewer` + `quality-architect`): no boundary
  violations (the cache confirmed as the only caller `GET
  /admin/incident-reports` uses, `IGitHubIssueClient` remains the only
  class calling GitHub's REST API); this doc-sync pass closes the doc
  gaps that same review found (COMP-12/CHANGELOG/backlog were not yet
  updated when code/tests/ADR-0066 landed). Backend 1375/1375, frontend
  543/543 passing, both confirmed in this sandbox. See `docs/backlog.md`'s
  S-098 entry for the full implementation shape.

- 2026-08-10 — `docs/requirements-document.md` (REQ-511, new, v1.65 →
  v1.66), `docs/architecture-document.md` (COMP-13, new; §10 ADR table,
  v0.91 → v0.92), `docs/design-document.md` (§7, new REQ-511
  open-question entry, v0.70 → v0.71), `MVP-SCOPE.md` (Tier 1
  pulled-forward entries for REQ-511 and the two queued follow-on
  stories), `docs/backlog.md` (S-096 built, S-097/S-098 queued),
  `docs/decisions/0065-site-wide-announcement-banner-shape.md` (new) —
  admin-managed, site-wide announcement banner (REQ-511), requested
  directly by the product owner alongside a separate ask for admin
  notifications when a new player suggestion or in-app incident report
  is posted. Run through `/orchestrate`: decomposed into three candidate
  stories, the product owner picked the banner to build this session
  (`AskUserQuestion`), the other two queued as S-097/S-098 rather than
  bundled, per this file's one-story-per-PR rule. `AnnouncementBanner`
  is a true singleton table (ADR-0065) behind an unauthenticated public
  `GET /announcement-banner` (ADR-0065's other half — only the second
  no-auth endpoint in the API, after `GET /health`) and an
  `"Admin"`-policy-gated `PUT`/`activate`/`deactivate`/admin-`GET` quartet
  — no new authorization policy introduced. Frontend banner mounts
  outside every auth-gated branch in `App.tsx` so a logged-out visitor,
  a guest, and a signed-in user all see it identically; admin management
  is an inline `AnnouncementBannerSection` in `AdminScreen.tsx`. Full
  `/quality-gate` run (architecture + quality review in parallel): no
  boundary violations found; two blocking test-coverage findings (missing
  cross-render-path coverage in `App.test.tsx`, a weak max-length
  assertion) were routed back to `test-writer` and fixed; both reviewers
  independently flagged the same doc gaps (this entry closes them).
  Verified: 529/529 Vitest tests pass, `tsc -b`/`oxlint` clean, all
  confirmed in this sandbox; backend suite hand-traced only, `dotnet` SDK
  unavailable here — deferred to CI, same recurring constraint as every
  other recent backend story in this repo. See `docs/backlog.md`'s S-096
  entry for the full implementation shape.

- 2026-08-10 — `docs/requirements-document.md` (REQ-512, new, v1.66 →
  v1.67), `docs/backlog.md` (S-097 built) — admin notification badge for
  pending player suggestions (REQ-215/509/512), the first of the two
  follow-on stories queued alongside REQ-511's banner above.
  `requirements-writer` drafted REQ-512 first per this repo's "no REQ, no
  code" workflow; `ui-implementer` then built it as a frontend-only
  change — a new `PlayerSuggestionsEntry` component in `AdminScreen.tsx`
  reusing REQ-509's existing `GET /admin/suggestions` endpoint and
  existing `fetchPendingSuggestions()` client function, no new backend
  endpoint or data source. Rendered as plain text (`Player suggestions
  (N)`), the same convention `Unverified data (N)` already uses in the
  same file, deliberately not a new pill/badge token since
  `design-document.md` §2 has none — `docs/design-document.md` is
  unchanged as a result. A quality-gate finding (401/403/other-error
  states were not distinguished) was fixed before merge, matching
  `AccountMetricsSection`/`XGPathCycleSection`'s existing resilience
  pattern. `architecture-reviewer` gave an explicit no-change verdict for
  `docs/architecture-document.md` — no new component, boundary, or data
  flow; REQ-509's existing endpoint and `"Admin"` policy are reused
  as-is, so no ADR was opened. 7 new tests (`AdminScreen.test.tsx`,
  `App.test.tsx`); full frontend suite 536/536 passing, `tsc -b`/oxlint
  clean, confirmed in this sandbox; backend untouched by this story.
  `docs/implementation-document.md` confirmed unchanged (no new
  library/service, no data model or folder-layout change, no test
  tooling change). See `docs/backlog.md`'s S-097 entry for the full
  implementation shape.

- 2026-08-10 — `docs/design-document.md` (v0.69 → v0.70, SCREEN-11 updated),
  `docs/requirements-document.md` (REQ-903, v1.64 → v1.65 — also corrects a
  pre-existing acceptance-criteria error, see below),
  `docs/architecture-document.md` (COMP-12, v0.90 → v0.91) — a third,
  same-day pass on REQ-903, requested directly: mandatory, structured
  Title/Screen fields (previously folded into free-text Description) plus
  an auto-captured, read-only Environment field, so every issue this
  feature creates follows one consistent template instead of however a
  player happened to phrase a single free-text box. Backend:
  `SubmitIncidentReportRequest` gained `Title`/`Screen` (both mandatory,
  server-re-validated regardless of the client's `<select>`/`<input>`
  shape — `IncidentEndpoints.TitleMaxLength`/`ScreenMaxLength` at
  120/50) and `Environment` (optional on the wire, `EnvironmentMaxLength`
  200); `IncidentReportService.SubmitAsync` now uses the submitted Title
  verbatim as the created GitHub issue's own title (previously
  derived/truncated from Description) and builds the body as one fixed
  markdown template (`## Description` / `## Details`, each of
  Screen/Environment/internal-UserId/timestamp under its own bolded
  label, same order every time). Frontend: `IncidentReportDialog.tsx`
  gained a Title text input and a Screen `<select>` (a fixed option list,
  `lib/incidentReportCopy.ts`'s new `INCIDENT_REPORT_SCREEN_OPTIONS`,
  mirroring `App.tsx`'s own `Screen` union as parallel plain strings to
  avoid a circular import — pre-selected from wherever the dialog was
  opened, changeable), plus a read-only "Environment: {origin}" line
  computed from `window.location.origin` — REQ-903's "found in
  environment... can be set in the background since we know from what
  url" request, answered literally. Description's placeholder wording
  changed to prompt reproduction steps and expected-vs-actual, now that
  Title/Screen carry the summary/location. **Also fixes a pre-existing
  REQ-903 documentation error found while updating this**: the original
  acceptance criteria said a guest sees no incident-report entry point in
  the UI at all — that was never actually built that way (both the
  original Settings section and the footer relocation built "advertised,
  disabled" from the start, correctly following REQ-215's own precedent)
  and directly contradicted REQ-903's own text elsewhere citing that same
  REQ-215 precedent; the requirements doc's wording was simply wrong and
  is corrected in place, not a behavior change. `SettingsScreen`/
  `App.test.tsx`/`IncidentReportDialog.test.tsx` and the backend's
  `IncidentEndpointTests.cs`/`IncidentReportServiceTests.cs` all updated
  for the new fields (513 frontend tests passing locally, `tsc -b` and
  `oxlint` clean; backend hand-traced only, same `dotnet`-unavailable
  sandbox caveat as every other change in this story — confirm in CI).

- 2026-08-10 — `docs/design-document.md` (v0.68 → v0.69, new SCREEN-11),
  `docs/requirements-document.md` (REQ-903, v1.63 → v1.64),
  `docs/architecture-document.md` (COMP-12, v0.89 → v0.90) — moved
  REQ-903's incident-report entry point, same day as its original build,
  from a section inside `SettingsScreen.tsx` to an app-wide footer button
  (`App.tsx`'s `.app__footer-report-link`) opening a new
  `frontend/src/incidents/IncidentReportDialog.tsx` modal — requested
  directly, so a player can report a problem from whatever screen they're
  actually looking at rather than navigating to Settings first. Structural/
  accessibility pattern taken from `GuestLogoutConfirm.tsx`/
  `ScoringExplainer.tsx` (`role="dialog"`, Escape/backdrop-click-to-close,
  focus-in/focus-return). The footer button itself only renders while
  `accessToken` is set (matches REQ-903's own 401 rule — no entry point at
  all while signed out); a guest still sees it, disabled, per REQ-215's
  "advertised, not hidden" precedent, unchanged from the original build.
  The dialog is opened with `App.tsx`'s current `screen` state passed
  straight through as REQ-903's `route` field, so triage context now
  reflects wherever the report was actually filed from instead of always
  saying "/settings". Added a second, explicitly requested change:
  `lib/incidentReportCopy.ts` gained `INCIDENT_REPORT_DESCRIPTION_PLACEHOLDER`,
  concrete example wording shown as the textarea's placeholder, addressing
  reports that tended to be too vague to act on. **Screenshot/image
  attachment was requested and explicitly deferred, not built**: GitHub's
  issue-creation API has no attach-a-file endpoint, so the only two real
  paths are widening the PAT past ADR-0064's locked-in `Issues: write`
  scope (to also write repo contents) or adding a new third-party image
  host (its own ToS check, secret, and privacy-policy disclosure per
  CLAUDE.md's external-data-source rule) — both are genuine architectural
  decisions flagged rather than silently picked; the product owner chose
  to ship the placement/example-copy change now and revisit screenshots as
  its own story. `SettingsScreen.tsx`/`.test.tsx`/`.css` had the section,
  its six tests, and its now-unused styles removed entirely (no
  duplication with the new footer/dialog location); `App.test.tsx` and the
  new `IncidentReportDialog.test.tsx` cover the relocation (28 + 13 + 4 net
  new/moved tests). `npm run test` (508/508), `tsc -b`, and `oxlint` all
  pass locally.

- 2026-08-10 — `docs/decisions/0064-backend-mediated-github-incident-reporting.md`
  (Status: Proposed → Accepted), `docs/requirements-document.md` (REQ-903,
  v1.62 → v1.63), `docs/architecture-document.md` (COMP-12, v0.88 → v0.89),
  `MVP-SCOPE.md` (Tier 1 pull-forward entry marked built),
  `docs/legal/privacy-policy-draft.md` (v0.9 → v0.10, new "Who we share it
  with" bullet for GitHub — a report's description and internal account id
  become a GitHub issue, potentially public, the first feature that posts
  player-written text to a third party) — implemented
  REQ-903/ADR-0064: a logged-in, non-guest player can file a bug report
  from Settings ("Report a problem") that the backend turns into a real
  GitHub issue in this repo. Backend: `POST /incidents`
  (`XGArcade.Api.Incidents.IncidentEndpoints`, mirrors REQ-215's
  `SuggestionEndpoints` resolve-caller/reject-guest shape exactly),
  `Core.IncidentReporting` (new, `XGArcade.Core` — `IGitHubIssueClient`/
  `GitHubIssueClient` calling GitHub's REST API with a fine-grained PAT set
  per-request via `HttpRequestMessage.Headers.Authorization`, never on the
  shared `HttpClient`'s defaults, same pattern `SupabaseAuthClient
  .DeleteUserAsync` already established for its own service_role key;
  `IIncidentReportService`/`IncidentReportService` builds the non-PII
  triage body). Guests rejected `403` server-side; per-user rate limit
  (default 3/10min, `RateLimiting:IncidentReportPermitLimit`/
  `WindowMinutes`) via a plain `PartitionedRateLimiter<Guid>` keyed on the
  resolved caller's `User.Id`, checked directly in the endpoint rather than
  as a global named `RateLimiter` policy — the existing `auth-signup`/
  `auth-login`/`auth-guest` policies are IP-partitioned and evaluated by
  `UseRateLimiter()` before `UseAuthentication()` runs, the wrong shape for
  a per-user key that only exists once this endpoint's own caller-lookup
  has run; see COMP-12's own architecture-document.md entry for the full
  reasoning. Target repo/label are non-secret `GitHubIncidentReportOptions`
  resolved from config in `Program.cs`, never accepted from the client.
  Threaded through `infra/bicep/modules/backend-container-app.bicep` →
  `infra/bicep/main.bicep` → `.github/workflows/deploy.yml` as
  `GitHub__IncidentReportToken`, sourced from a new, optional
  (default-empty) `INCIDENT_REPORT_PAT` shared repo secret
  (`infra/README.md`, `SETUP.md` step 6) — not yet created in any real
  environment, so `POST /incidents` currently fails closed (503) rather
  than reaching GitHub. Frontend: `SettingsScreen.tsx` gained a "Report a
  problem" section (always rendered, disabled — not hidden — for a guest,
  mirroring REQ-215's advertised-but-disabled rule), `lib/api.ts`'s
  `reportIncident`, `lib/incidentReportCopy.ts` for the guest-locked/
  submitted copy strings. Tests (`GitHubIssueClientTests.cs`,
  `IncidentReportServiceTests.cs`, `IncidentEndpointTests.cs`) never call
  the real GitHub API — all against a fake `IGitHubIssueClient`. **Backend
  caveat: `dotnet` SDK unavailable in this sandbox** — the new backend code
  and tests were hand-traced against this codebase's existing
  `SuggestionEndpoints`/`SuggestionEndpointTests` and `SupabaseAuthClient`/
  `SupabaseAuthClientCaptchaTests` patterns, not actually built or run;
  confirm in CI. Frontend: `npm run test` (497/497) and `tsc -b` both pass
  locally. **Naming correction (same day):** originally named
  `GITHUB_INCIDENT_REPORT_PAT` throughout this session's docs/workflow —
  GitHub rejects any repo secret name starting with the reserved `GITHUB_`
  prefix, so it's `INCIDENT_REPORT_PAT` everywhere instead
  (`.github/workflows/deploy.yml`, `infra/README.md`, `SETUP.md`,
  `MVP-SCOPE.md`, `TODO.md`, `docs/requirements-document.md`, and
  `GitHubIssueClient.cs`'s own comments) — the Bicep parameter/env var
  names (`githubIncidentReportToken`/`GitHub__IncidentReportToken`) are
  unaffected, since those aren't GitHub secret names. **The secret has now
  been created** (confirmed by the product owner, 2026-08-10) — REQ-903's
  required one-time manual end-to-end check against a throwaway/test repo
  is still outstanding before relying on this in production; see REQ-903's
  own "Verification status" note.

- 2026-08-10 — new `docs/decisions/0064-backend-mediated-github-incident-reporting.md`,
  `docs/requirements-document.md` (REQ-903), `docs/architecture-document.md`
  (new COMP-12 Core.IncidentReporting), `MVP-SCOPE.md` (Tier 1 pull-forward
  entry) — design-only exploration of letting a logged-in, non-guest player
  file a bug report from inside the app that lands as a real GitHub issue,
  without them needing a GitHub account. Backend holds a fine-grained PAT
  scoped to `Issues: write` on this repo only, never exposed to the client;
  guests rejected server-side `403`, same boundary REQ-215 already
  established. No code written yet — REQ-903/ADR-0064 only.

- 2026-08-10 — `docs/requirements-document.md` (v1.60 → v1.61),
  `docs/decisions/0060-suggestion-commit-write-path-split-by-cardinality.md`
  (new status note) — admin reported the mandatory "reason" field on
  `/admin/suggestions/{id}/commit` as unwanted friction; investigation found
  it was a real bug, not just friction: `Reason` is only ever persisted
  (`PlayerOverride.Reason`) when a nationality is committed — `PlayerAttribute`
  has no audit columns, so a clubs-only commit validated the field as
  required then discarded it, satisfying no audit trail. Fixed by making
  `Reason` conditionally required: still mandatory and persisted whenever a
  commit includes a nationality, optional for clubs-only commits.
  `AdminSuggestionEndpoints.ValidateCommitRequest` and
  `SuggestionsScreen.tsx`'s `PlayerReviewPanel` (`canCommit`, the `Reason`
  field's `required` attribute) updated together; new backend test
  (`REQ509_Commit_SucceedsWithoutReason_WhenClubsOnly_NoNationality`) and
  frontend test (`SuggestionsScreen.test.tsx`) added. REQ-509, ADR-0060.
  **Backend caveat: `dotnet` SDK unavailable in this sandbox** — the new
  backend test was hand-traced against the existing, already-verified
  `REQ509_Commit_ReturnsBadRequest_WhenReasonMissing` pattern, not actually
  built or run; confirm in CI.

- 2026-08-10 — `docs/requirements-document.md` (v1.61 → v1.62),
  `docs/decisions/0052-pair-lookup-failure-persistence-and-club-club-query-fix.md`
  (new status note) — player reported REQ-211's guess-time live-lookup
  fallback timing out "quite often" on guesses they expected to be
  incorrect. Root cause: `GridGameModule.RefreshCellFromLiveLookupAsync`
  never consulted `PairLookupFailure` (ADR-0052) — a Country×Club/Club×Club
  pair `PlayerCacheWarmingService` had already confirmed, independently, as
  a persistent technical failure still paid the full ~28s guess-time
  timeout on every guess against it. Fixed by checking
  `IPlayerStoreRepository.IsPersistentTechnicalFailureAsync` before
  attempting the live call — a known-doomed pair now fails fast
  (`LiveLookupUnavailableException`) instead of re-waiting out a timeout
  already known to happen. Correctness-neutral: still reports the pair as
  genuinely unknown, not incorrect, and still never consumes a REQ-210
  attempt — purely removes a redundant wait. `PlayerCacheWarmingService
  .PersistentFailureThreshold` changed `private` → `internal` so
  `GridGameModule` can reference the same value instead of duplicating it
  (both live in `Games.XGGrid`, no project-boundary issue). New tests in
  `GridGameModuleTests.cs` (`REQ211_ScoreSubmissionAsync_
  PairAlreadyKnownPersistentFailure_ThrowsLiveLookupUnavailableException_
  WithoutCallingWikidata`, `REQ211_ScoreSubmissionAsync_
  PairBelowPersistentFailureThreshold_StillAttemptsLiveLookup`). Only
  benefits Country×Club/Club×Club — `PlayerCacheWarmingService` doesn't
  track Trophy pairings. REQ-211, ADR-0052. **Backend caveat: `dotnet` SDK
  unavailable in this sandbox** — hand-traced against the existing,
  already-verified `REQ211_ScoreSubmissionAsync_LiveLookupTimesOut_
  ThrowsLiveLookupUnavailableException` test pattern, not built or run;
  confirm in CI.

- 2026-08-10 — `docs/design-document.md` (v0.67 → v0.68) — player-name
  autocomplete's debounce lowered from 275ms to 150ms (`GuessInput.tsx`,
  `PathGuessInput.tsx`), now that a superseded in-flight request is actually
  aborted (`AbortController`, new REQ-207 test coverage) rather than merely
  ignored client-side — the shorter debounce no longer risks piling up
  redundant concurrent requests the way it would have before that fix.
  REQ-207.

- 2026-08-10 — new `docs/decisions/0063-duplicate-career-stint-cleaner-appearance-count-merge-widening.md`,
  `docs/decisions/0059-career-stint-club-name-canonicalization.md`
  ("For AI agents" section) — quality-gate follow-up on commit `237439c`
  (REQ-1203/REQ-1207 xG Path clue-reveal bug fixes). `DuplicateCareerStintCleaner`'s
  widened matching (null-tolerant `AppearanceCount` merge, same-`ClubName`
  Step 2 pass, in-place survivor mutation) needed the fresh ADR that
  ADR-0059's own "For AI agents" guardrail required before any widening —
  ADR-0063 records it and ADR-0059 now points at it. Same round also fixed
  two real bugs the ADR gap was standing in front of: Step 2 silently
  failing to collapse two rows sharing an identical, already-populated
  `AppearanceCount` (only null rows were ever removed); and Step 1's
  in-place mutation being order-dependent for 3+-row groups sharing a
  `(PlayerId, StartYear, EndYear)` key (now conservative — an ambiguous
  3+-row group is left entirely untouched rather than picking a winner via
  enumeration order). Also corrected `PathCareerStintFilter`'s doc comment,
  which overclaimed the national-team regex "leaves non-FIFA regional
  representative sides alone" — it has no FIFA-affiliation signal at all
  and matches on label wording only; a non-FIFA side labeled as a
  "national team" is excluded same as any other, pinned down with a new
  test (`Catalonia national football team`, NOT verified against a live
  Wikidata query from this sandbox — flagged for manual confirmation). No
  `dotnet` SDK available in this sandbox; new/changed tests hand-traced,
  not run. REQ-1203, ADR-0059, ADR-0063.

- 2026-08-10 — `docs/requirements-document.md` (v1.58 → v1.59),
  `docs/architecture-document.md` (v0.86 → v0.87) — doc-sync pass over
  commits `237439c`/`44771a6`/`ccbfbfe` (the same xG Path clue-reveal
  bug-bundle round as the entry directly above). REQ-1203: renamed stale
  `PathCareerStintFilter.ExcludeYouthNationalTeams`/`IsYouthNationalTeam`
  references to `ExcludeNationalTeams`/`IsNationalTeam`; added a new
  2026-08-10 status note (superseding, not deleting, the 2026-08-08 note)
  recording that the youth-only scoping was reopened by a new bug report
  showing a senior national team leaking through, and that the filter now
  matches any national team per this REQ's own unqualified acceptance
  criterion — the non-FIFA-regional-side behavior is unchanged but is now
  documented as incidental (label-wording-based), not a deliberate policy
  exemption; added a status note on the 2026-08-03 "known, accepted
  limitation" (`AppearanceCount` mismatch) recording that ADR-0063's
  null-vs-populated merge now partially closes it (both-populated-and-
  different still doesn't merge). REQ-1207: added a status note on the
  raw-Wikidata-URI backfill-candidate widening fix. `docs/architecture-
  document.md`: updated COMP-11's status note and §6.2b's data-flow wording
  to match the broadened national-team filter (was described as
  "youth/age-grade only," now reads as senior-or-youth with both dates'
  reasoning). REQ-1203, REQ-1207, ADR-0063. Note:
  `docs/implementation-document.md` (project-structure section, ~line 245)
  still describes the filter as "youth/age-grade national-team" only — left
  unedited, out of this pass's explicit scope; flagged for a follow-up pass.

- 2026-08-10 — `docs/implementation-document.md` (v0.92 → v0.93) — closed
  the follow-up flagged in the entry directly above. Project-structure
  section's `PathCareerStintFilter` description updated from "youth/age-
  grade national-team" rows to noting the 2026-08-10 widening to any
  national team (`IsYouthNationalTeam` → `IsNationalTeam`); the position/
  birth-year backfill section's `GetPlayersMissingPositionOrBirthYearAsync`
  description updated to record the raw-Wikidata-URI candidate widening and
  `UpdatePlayerPositionsAndBirthYearsAsync`'s corresponding one-time
  exception to its "never clobber an already-set field" rule. Same round
  as commits `237439c`/`44771a6`/`ccbfbfe`. REQ-1203, REQ-1207.

- 2026-08-09 — `docs/architecture-document.md` (v0.85 → v0.86), new
  `docs/decisions/0062-admin-lookup-wikibase-mwapi-search.md` — a
  production log showed REQ-509/510's admin by-name Wikidata lookup
  running ~39s and then failing with HTTP 502 Bad Gateway (not a
  timeout — the 45s budget added earlier the same day wasn't the
  bottleneck; something in front of WDQS rejects the query under its
  own cost/duration). Root cause: `QueryPlayerCareerAndNationalityByNameAsync`
  resolves a name to a candidate player via an unindexed, population-wide
  `rdfs:label`/`skos:altLabel` scan. ADR-0062 records the decision to
  replace that scan with a federated `wikibase:mwapi` `EntitySearch` call
  (Wikidata's own indexed search) instead, and the two alternatives
  rejected (backfilling `PlayerNameIndex` with a real `WikidataQid`;
  calling Wikidata's REST `wbsearchentities` API as a new external
  dependency). Flagged in the ADR's own Consequences: unverified against
  the real Wikidata endpoint from this sandbox (no live network access),
  needs a human check before being trusted in production. `docs/architecture-document.md`
  §10's ADR table gained the new row. REQ-509, REQ-510, ADR-0062.

- 2026-08-09 — `docs/requirements-document.md` (v1.57 → v1.58),
  `docs/architecture-document.md` (v0.84 → v0.85),
  `docs/implementation-document.md` (v0.91 → v0.92), `docs/backlog.md`
  (new S-095 entry), `MVP-SCOPE.md` — synced all five docs to REQ-108's
  now-completed follow-up story: team-competition trophies (FIFA World Cup,
  UEFA Champions League) for xG Grid's Trophy category, per ADR-0061's
  `P1344`/`P3450`/`P1346` edition-participation/winner query shape. REQ-107
  and REQ-108's status notes updated to record the trophy pool growing from
  one to three and, critically, that Country×Trophy/Club×Trophy are now
  REACHABLE and selectable in production (previously "structurally
  dormant") — Trophy×Trophy remains infeasible (`trophyCount >= size * 2`
  not yet cleared). `docs/architecture-document.md`'s boundary-rule-1
  discussion (COMP-05/06/07 status note) updated to record ADR-0035's own
  outstanding follow-up note as resolved in the same story; its §6.1 grid-
  generation flow caveat and COMP-05's `PlayerCacheWarmingService` note
  updated to match (the latter flagged as a newly-live, not fixed, gap —
  Trophy pairs still aren't proactively cache-warmed even though they're
  now reachable). `docs/implementation-document.md`'s `TrophyDefinition`
  data-model snippet and grid-generation pairing narrative updated to match
  the actual entity/behavior. `docs/backlog.md` gained a new S-095 entry
  mirroring S-031's "Built as" format; `MVP-SCOPE.md`'s struck-through
  REQ-108 Tier 1 entry updated to record the deferred remainder as shipped.
  REQ-108, ADR-0061, ADR-0035 (follow-up note only, not re-litigated).

- 2026-08-09 — `docs/requirements-document.md` (v1.56 → v1.57),
  `docs/backlog.md` — synced both docs to S-090's actual shipped state
  (`docs/architecture-document.md`'s COMP-06 status note and new
  ADR-0060 were already updated directly by the orchestrator, not touched
  here). REQ-509 and REQ-510 moved from "drafted only" to "Implemented
  (2026-08-08, S-090)," documenting the four suggestion-scoped admin
  endpoints and two standalone search-and-add endpoints
  (`AdminSuggestionEndpoints.cs`), the new `SuggestionsScreen.tsx` (linked
  from, never merged into, `AdminScreen.tsx` per ADR-0053), the
  nationality-via-`PlayerOverride`/club(s)-via-additive-`PlayerAttribute`
  write-path split (ADR-0060), and a bug found and fixed mid-implementation
  (the Wikidata career lookup silently dropping clubs with no P580
  start-date qualifier). REQ-215's own status note and its two stale
  "admin half still queued" cross-references were updated to match.
  `docs/backlog.md`'s S-090 entry replaced its "not yet started" placeholder
  with a "Built as" paragraph covering the same ground, plus the deviation
  from the original story text (the write-path split wasn't specified up
  front) and the bug fix. REQ-509, REQ-510, ADR-0060, ADR-0053.

- 2026-08-08 — `infra/scripts/lib/game-data-tables.sh`,
  `infra/scripts/promote-dev-to-prod.sh`, `infra/scripts/sync-prod-to-dev.sh`,
  `.github/workflows/promote-dev-to-prod-dry-run.yml` (new),
  `docs/decisions/0009-bidirectional-game-data-sync.md`,
  `infra/README.md`, `docs/implementation-document.md` — fixed a real
  data-loss bug found during an architecture review: both sync scripts ran
  `TRUNCATE TABLE $t CASCADE;` per allowlisted table before restoring, and
  Postgres's `TRUNCATE ... CASCADE` truncates every OTHER table with a
  foreign key into the truncated table too, not just rows — truncating
  `Player` was silently wiping xG Path's `PathPuzzle`/`PathCycleTargetUsage`
  tables (verified against `XGArcadeDbContext.cs`'s FK graph), neither of
  which is or should be on the sync allowlist per this ADR. Fixed by
  finding and temporarily dropping only the specific external FK
  constraints at runtime (`pg_constraint`), truncating the whole allowlist
  together with no `CASCADE` keyword at all, then re-adding the
  constraints after restore — verified end-to-end against a real local
  Postgres 16 instance (including reproducing the original bug for
  contrast), not just reasoned about; `SET session_replication_role =
  replica` was tried first and confirmed NOT to solve this specific
  problem. Also extended both scripts' `--dry-run` output to show both
  sides' row counts per table (previously only the source side's), and
  added `promote-dev-to-prod-dry-run.yml`, a weekly-scheduled workflow that
  surfaces the diff on the job summary without ever writing to prod or
  adding a non-interactive flag to the real promote path; it exits cleanly
  when prod isn't configured yet (Tier 1). No new ADR — addendum to
  ADR-0009. REQ-804/REQ-805, ADR-0009.

- 2026-08-08 — `infra/scripts/lib/game-data-tables.sh`,
  `docs/decisions/0009-bidirectional-game-data-sync.md` — fixed a real gap
  found during an architecture review of a proposed shared dev/prod
  database: `PlayerCareerStint` (ADR-0042) was never added to the
  prod↔dev sync allowlist, so the two environments have had no sync path
  for it since it was introduced. Added `"public.\"PlayerCareerStints\""`
  to the allowlist both `sync-prod-to-dev.sh`/`promote-dev-to-prod.sh`
  share; ADR-0009 gained a dated addendum recording the gap and fix. No
  behavior change beyond making this table syncable going forward — no
  sync actually run as part of this fix. ADR-0009.

- 2026-08-08 — `docs/backlog.md` — doc-hygiene fix: the "Tier 1 backlog
  (unordered)" quick-reference list still showed `T-104 disambiguation UI
  (REQ-209)` without a strikethrough, even though S-067 fully built it
  (backend/API + frontend, same day) — the list just wasn't updated when
  S-067 shipped. Struck through and cross-referenced to S-067, matching
  the convention every other completed T-10x entry in that list already
  uses. No behavior/requirement change. REQ-209.

- 2026-08-08 — `docs/requirements-document.md` (v1.55 → v1.56) — fixed the
  gap flagged (not fixed) in the same-day xG Path `PathScoringExplainer`
  entry: `LeaderboardScreen.tsx`'s `(ⓘ)` "How scoring works" button always
  opened xG Grid's `ScoringExplainer`, even when the leaderboard's xG Path
  tab was active, showing Grid-specific content (uniqueness, live/locked
  points, median ranking) that doesn't describe xG Path's rules — reported
  directly by a player after the gap was flagged. Made the entry point's
  modal `gameKey`-aware: `gameKey === XG_GRID_GAME_KEY` renders
  `ScoringExplainer`, `gameKey === XG_PATH_GAME_KEY` renders
  `PathScoringExplainer` (imported from `../path/PathScoringExplainer`,
  the same cross-feature-folder import pattern this file already used for
  `../grid/ScoringExplainer` — no component relocation needed). Judgement
  call: switching the game tab while the explainer is open now closes it
  rather than swapping its content live or leaving the old game's
  mismatched content on screen — follows the same "back out on a game
  switch" precedent this file's `selectedRound`/`pastDetailState` reset
  effect already established (REQ-410/S-087), rather than inventing a new
  behavior. 4 new Vitest tests in `LeaderboardScreen.test.tsx`
  (`describe('game-aware scoring explainer', ...)`): Grid tab opens the
  Grid explainer, Path tab opens the Path explainer, switching games while
  open closes it (and a re-open shows the new game's content), switching
  games while closed has no effect. Full frontend suite run (476 tests
  passing, up from 472), `npx tsc -b` clean, `npm run lint` clean. No
  backend changes, no ADR (UI composition only — same reasoning as the
  original PathScoringExplainer change this follows up on). REQ-213.
- 2026-08-08 — `docs/requirements-document.md` (v1.54 → v1.55),
  `docs/design-document.md` (v0.66 → v0.67) — closed a real player-reported
  gap on xG Path (SCREEN-10): "no scoring information in the game" turned
  out to mean the `(ⓘ)` "How scoring works" explainer specifically (not the
  same-day REQ-1206 per-puzzle points value, which stays untouched).
  `PathScreen.tsx` had no explainer entry point at all before this. Added a
  new `(ⓘ)` button (`.path-screen__info-toggle`) in the header, next to the
  round end-time indicator, opening a new sibling component
  (`frontend/src/path/PathScoringExplainer.tsx`) rather than reusing or
  `gameKey`-branching xG Grid's `ScoringExplainer.tsx` — xG Path's rules
  share almost nothing with xG Grid's (no uniqueness, no live/locked point
  distinction, a different 7-clue/7-attempt model), so reuse would have
  misdescribed the game; the modal/accessibility shell (focus management,
  Escape-to-close) is duplicated from `ScoringExplainer.tsx`, not
  extracted, per this repo's two-call-sites duplication preference.
  Content verified against `XGPathGameModule.cs`,
  `PathClueSequenceBuilder.cs`, `ClueEfficiencyScoringStrategy.cs`, and
  `PathGenerationOptions.cs` rather than assumed. `docs/requirements-
  document.md` REQ-213 gained a 2026-08-08 second-consumer status note
  (mirroring REQ-303's earlier second-consumer precedent) plus new
  acceptance-criteria/Test-level coverage for SCREEN-10's distinct entry
  point; `docs/design-document.md`'s SCREEN-10 section gained a matching
  status note. Both docs flag the same known, pre-existing,
  out-of-scope gap: `LeaderboardScreen.tsx`'s own `(ⓘ)` entry point still
  shows xG Grid's `ScoringExplainer` content even when the leaderboard's
  xG Path tab is active — not fixed here, filed as a follow-up candidate.
  3 new Vitest tests added to `PathScreen.test.tsx`
  (`describe('REQ-213: scoring explainer', ...)`); full frontend suite run
  (472 tests passing, up from 469), `npx tsc -b` clean, `npm run lint`
  clean. No backend changes, no ADR (UI content/composition, not a
  structural/boundary decision). REQ-213.
- 2026-08-08 — `docs/architecture-document.md` (v0.82 → v0.83),
  `docs/implementation-document.md` (v0.90 → v0.91) — doc-sync closing an
  `architecture-reviewer` gate finding on today's two xG Path bug fixes
  (no boundary violation, no new ADR needed either time; purely a
  documentation gap). Architecture doc: COMP-11's status note gained a
  2026-08-08 continuation of its own 2026-08-02 national-team-exclusion
  note, documenting the new `PathCareerStintFilter` read-time filter (same
  read-time-filter-over-destructive-cleanup reasoning as ADR-0059) at both
  its call sites; a new COMP-04/COMP-11 status note documents `GET
  /path/current` (`PathEndpoints.cs`) now also resolving
  `IScoringStrategyResolver` to compute REQ-1206's `Points` field, the
  first Api-layer caller of that resolver besides `ScoreLockingService`,
  via the already-established `IGameModuleResolver`-from-Api-layer shape;
  §6.2b's data-flow diagram extended with both the new `Core.Scoring`
  step and the `PathCareerStintFilter` step, which it previously omitted
  entirely. Implementation doc: one additional line noting
  `PathEndpoints.cs`'s new `IScoringStrategyResolver` dependency and how
  `Points` is computed, alongside the existing `PathCareerStintFilter`
  note from earlier today. `docs/requirements-document.md` and
  `docs/design-document.md` untouched — already correctly updated earlier
  this session. REQ-1203, REQ-1206.
- 2026-08-08 — no doc changes — same-day quality-gate fix-up (not a new
  requirement) to `XGArcade.Games.XGPath.PathCareerStintFilter`'s
  `YouthNationalTeamPattern` regex: added a missing leading `\b` before
  `national` so the pattern anchors to a real word, not a bare substring
  match inside a longer word (e.g. "Inter"+"national",
  "Multi"+"national") — was wrongly flagging club/team names like
  "International Under-20 Select XI" and "Multinational Development
  Squad Under-19" as youth national teams. New negative test cases added
  to `PathCareerStintFilterTests`
  (`REQ1203_IsYouthNationalTeam_ClubNamesContainingNationalAsSubstring_ReturnsFalse`).
  Also corrected an inaccurate precedent claim in `PathEndpoints.cs`'s
  comment introducing `scoringStrategyResolver.Resolve(round.GameKey)` —
  it mirrors the `IGameModuleResolver` Api-layer-resolver pattern, not an
  existing `RoundEndpoints`/`ScoreLockingService` call to this specific
  resolver (that resolver's only prior caller was `ScoreLockingService`
  inside `XGArcade.Core.Scoring`). `dotnet` unavailable in this sandbox;
  regex fix hand-traced against all positive/negative cases in both test
  files, not run. REQ-1203.

- 2026-08-08 — `docs/requirements-document.md` (v1.52 → v1.53) — backend
  half of REQ-1206's 2026-08-08 "score is never shown" gap: `GET
  /path/current`'s `CurrentPathGuessResponse` (`XGArcade.Api.Path.
  PathEndpoints`) gains a `Points` field (`int?`), non-null only when
  `Locked` is true. Computed by resolving `IScoringStrategyResolver`
  (already DI-registered for `ScoreLockingService`) and calling
  `ClueEfficiencyScoringStrategy.ScoreCorrectGuess` directly for a solved
  puzzle — never a reimplemented copy of its rounding formula — or the
  same `ScoringRules.MaxPointsPerCell` worst case `ScoreLockingService`
  assigns for a puzzle locked via exhausted attempts (unsolved), since
  that strategy is only ever invoked for a correct guess. Named `Points`,
  not `LivePoints`/anything implying "estimated," matching REQ-1206's
  explicit "this is never provisional, unlike xG Grid's `LivePoints`"
  distinction. No change to `ClueEfficiencyScoringStrategy`,
  `IScoringStrategyResolver`, or `ScoreLockingService` themselves — this
  is a new call site for an existing formula, not a formula change, so no
  new ADR. New coverage: `PathEndpointTests`
  (`REQ1206_PathCurrent_Get_LockedViaCorrectGuess_ReturnsPointsMatchingClueEfficiencyFormula`,
  `REQ1206_PathCurrent_Get_LockedViaExhaustedAttempts_ReturnsWorstCasePoints`,
  `REQ1206_PathCurrent_Get_UnlockedPuzzleWithAnExistingGuess_ReturnsNoPoints`,
  `XGArcade.Api.Tests`). `dotnet` is unavailable in this sandbox — tests
  were hand-traced against the existing `PathEndpointTests` patterns but
  not run; will only run in CI. Frontend (`PathScreen.tsx`, SCREEN-10)
  deliberately untouched — left to a follow-up `ui-implementer` task, per
  REQ-1206's still-open UI "Not yet covered" note. REQ-1206.

- 2026-08-08 — `docs/requirements-document.md` (v1.53 → v1.54),
  `docs/design-document.md` (v0.65 → v0.66) — frontend half of REQ-1206's
  "score is never shown" gap, closing it: `lib/types.ts`'s
  `CurrentPathGuess` gains a `points: number | null` field mirroring
  `CurrentPathGuessResponse.Points` exactly, and `PathTimeline.tsx`'s
  `SolvedNode`/`FailedRevealNode` (wired from `PathScreen.tsx`, gated on
  the same `locked` boolean already used for the resolved player name/
  photo) render it as plain `"N pts"` text (`mono-figure`, colored to
  match the reveal node's own outcome accent —
  `accent-gold-text`/`accent-red` — mirroring `CellState.css`'s existing
  points-color convention) — deliberately never `"~N pts estimated"` or
  any other provisional wording, per REQ-1206's explicit "not the same as
  xG Grid's `LivePoints`" acceptance criteria. Judgment call flagged in
  `design-document.md`'s new SCREEN-10 status note (placement on the
  timeline's reveal node, not a separate screen element; wording/color
  choice) since this section hadn't previously spec'd a score display slot
  at all. New coverage: `PathTimeline.test.tsx`'s
  `describe('REQ-1206: locked point value', ...)` (solved reveal, no
  provisional wording; locked-but-unsolved reveal; still-unlocked shows
  no points; null `points` on an otherwise-locked reveal shows no points
  line) and three `PathScreen.test.tsx` `REQ-1206:` tests (end-to-end
  plumbing for the solved, exhausted-unsolved, and still-unlocked cases).
  `npm run test` (Vitest): 469/469 passed across 26 files, including the
  9 new ones. `tsc -b` and `npm run lint` (oxlint) both clean. REQ-1206.
- 2026-08-08 — `docs/requirements-document.md` (v1.51 → v1.52),
  `docs/implementation-document.md` (v0.89 → v0.90) — bug fix:
  leftover pre-2026-08-02 youth/age-grade national-team `PlayerCareerStint`
  rows (e.g. "Spain national under-16 association football team," "Italy
  national under-20/under-21 football team") were still leaking into xG
  Path's club-reveal clues, reported directly via user testing. The
  2026-08-02 SPARQL fix only stops NEW rows from being fetched — it can't
  retroactively remove rows already sitting in the ~608K-row
  `PlayerCareerStint` table, since `PlayerCareerStintRefreshService` is
  additive-only. Fixed with a new, pure `PathCareerStintFilter`
  (`XGArcade.Games.XGPath`), a read-time filter (not a DELETE/cleanup
  script — no QID exists on already-persisted rows to prove a match
  against, unlike ADR-0059's cleanup) applied at both `GET /path/current`
  (`PathEndpoints.cs`) and `XGPathGameModule.GetEligiblePlayerIdsAsync`'s
  REQ-1201 eligibility check, so a player's eligibility count can no
  longer be inflated by leftover junk rows either. Scoped narrowly
  (`national` + an age-grade `under-\d+` marker) to match only what was
  reported — the valid senior national-team clue and a non-FIFA regional
  side are both deliberately left alone. New coverage:
  `PathCareerStintFilterTests`, `XGPathGameModuleTests`, `PathEndpointTests`
  (all `XGArcade.Games.XGPath.Tests`/`XGArcade.Api.Tests`). `dotnet` is
  unavailable in this sandbox — tests were hand-traced against the
  existing test patterns but not run; will only run in CI. REQ-1203.
- 2026-08-04 — `docs/requirements-document.md` (v1.47 → v1.49),
  `docs/design-document.md` (v0.64 → v0.65) — REQ-213 verification finding
  (content confirmed complete; found the `(ⓘ)` explainer entry point
  orphans onto its own line, disconnected from the round-timer text, at
  420-480px viewport widths — new dated acceptance criterion filed for a
  follow-up fix, REQ-213's status unchanged at Implemented) plus SCREEN-10
  (`PathScreen.tsx`) now rendering the same round end-time indicator
  SCREEN-01 has (REQ-303's 2026-07-21 addition), reusing
  `CurrentPathResponse.endTime` (already present since S-081/S-082) and the
  same shared `lib/roundTime.ts` formatter `GridScreen.tsx` uses — a
  second-consumer status note under REQ-1203 (no acceptance-criteria
  change; format/threshold rules stay owned by REQ-303) and a matching
  status note on SCREEN-10 in the design doc, flagging its wireframe as
  stale on this one point. No architecture change (no new endpoint field,
  no boundary crossed). REQ-213, REQ-303, REQ-1203.
- 2026-08-04 — `docs/requirements-document.md` (v1.47 → v1.48),
  `docs/architecture-document.md` (v0.81 → v0.82) — doc-sync for the
  REQ-1203 follow-up fix (ADR-0059, commits `99c5818`/`d829a25`, branch
  `claude/xg-duplicate-clubs-7ns69u`): xG Path could still show the same
  real career stint as two separate club-reveal nodes even after the
  2026-08-03 `NormalizeClubName` fix, because two independent writers of
  `PlayerCareerStint.ClubName` used different naming conventions (the
  seeded `ClubDefinition.Name` vs. Wikidata's raw `?clubLabel`) with no
  QID-based cross-check — e.g. "Lyon" vs. "Olympique Lyonnais," same club,
  more than a legal-suffix token apart. Fixed by selecting the underlying
  Wikidata `?club` QID (`WikidataCareerStintEntry.ClubQid`) and having
  `PlayerCareerStintRefreshService`/`PlayerCareerPrefetchService`
  canonicalize each fetched stint's `ClubName` against `ClubDefinition.Name`
  by QID, also fixing `GetCareerStintCandidatePlayerIdsAsync`'s (REQ-1201)
  exact-string eligibility match for free. A new narrow, provable-only CLI
  verb (`clean-duplicate-career-stints`/`DuplicateCareerStintCleaner`)
  backfills already-persisted duplicates without a full purge-and-reseed of
  the ~608K-row table — see ADR-0059 for the full reasoning. Requirements
  doc: new 2026-08-04 status note on REQ-1203, alongside (not replacing)
  the existing 2026-08-02/2026-08-03 notes, which cover different bugs.
  Architecture doc: COMP-07 row updated to note `PlayerCareerStintRefreshService`'s
  new `ICategoryValueRepository` dependency and the QID-canonicalization
  data flow — confirmed this is a second call site for an existing
  reference-data-read pattern (ADR-0012), not a new boundary; COMP-11/COMP-06
  rows checked and left unchanged, since neither's documented responsibility
  or data-flow shape actually changed. `docs/decisions/0059-*.md` already
  existed (written alongside the code fix) and was not re-created or
  edited. Both an `architecture-reviewer` and `quality-architect` pass
  already ran on the diff before this doc-sync and returned PASS (two
  low-severity, explicitly non-blocking test-coverage nits, not code
  changes). No test suite was executed as part of this fix or this
  doc-sync — the `dotnet` SDK was not available in this sandbox at any
  point; all new/changed backend code was hand-traced against the actual
  service/query code, not run. `docs/decisions/0058-career-stint-club-name-canonicalization.md`
  was renumbered to `0059-*.md` (and every reference to it) after merging
  latest `main`, since `main`'s own PR #147 (below) had independently
  claimed ADR-0058 for S-093's cycle-tracking decision while this branch
  was in progress. REQ-1203, ADR-0059.

- 2026-08-03 — `docs/backlog.md` — closes a doc-sync gap left by the prior
  two entries below: S-093's status line and body updated to reflect that
  ADR-0058 was amended (2026-08-03, post quality-gate review) to confirm
  `GET /admin/xg-path/cycle`'s `IGameModule` bypass as a deliberate
  extension of ADR-0016/ADR-0048's direct-repository-read pattern to
  cross-instance bookkeeping state (not literally covered by either ADR's
  original per-instance-content scope), plus a note on
  `AddInstanceWithCycleUsageAsync`'s bundled-write shape not being the
  default way to write multiple entities; and a one-line comment typo fix
  (`XGArcada` → `XGArcade`) in `frontend/src/lib/types.ts`. No REQ/ADR
  content changed beyond ADR-0058's own amendment (already committed);
  this entry only catches `docs/backlog.md` up to it. REQ-1208/1209,
  ADR-0058.
- 2026-08-03 — `docs/requirements-document.md` (v1.46 → v1.47),
  `docs/architecture-document.md` (v0.80 → v0.81), `docs/backlog.md` —
  full test coverage landed for REQ-1208/REQ-1209 (S-093): backend unit
  tests (`XGPathGameModuleTests.cs`, new `ManualTimeProvider.cs`) covering
  per-selection usage recording, in-cycle exclusion, rollover once
  remaining-unused drops below N (including reselecting a just-used
  player), a stale usage row from a dropped-out player never blocking
  rollover, and the pre-existing REQ-1202 insufficient-pool abort left
  untouched by cycle state; backend API tests (`RoundEndpointTests.cs`,
  new `AdminXGPathEndpointTests.cs`) covering round generation across a
  rollover boundary and `GET /admin/xg-path/cycle`'s persisted-state/
  no-data-yet/403/401 cases plus its unconditional Production
  registration; frontend Vitest coverage (`AdminScreen.test.tsx`) covering
  full-field render, the no-data-yet empty state, and the
  401/403/other-error handling pattern for `XGPathCycleSection` (459/459
  frontend tests passing, verified in this sandbox). `dotnet` was
  unavailable in the implementation sandbox, consistent with the prior two
  implementation commits' own note — backend tests are written and
  hand-traced against the actual implementation but not compiled or run;
  still need a real `dotnet test` pass in CI before merge. Both REQs'
  status notes and the architecture doc's COMP-11 status (which had been
  left saying "frontend panel not yet built" after the frontend was
  actually implemented in the prior entry) updated to match. REQ-1208/1209,
  ADR-0058.
- 2026-08-03 — `docs/requirements-document.md` (v1.44 → v1.45),
  `docs/architecture-document.md` (v0.79 → v0.80), `docs/backlog.md` —
  backend implementation of REQ-1208/REQ-1209 (S-093, xG Path no-repeat
  target-selection cycle + admin visibility), following ADR-0058's binding
  decisions exactly. New xG Path-scoped entities `PathTargetCycle`/
  `PathCycleTargetUsage` (`XGArcade.Data`, migration
  `20260803140000_AddPathTargetCycle`) — never a field on the shared
  `Player` entity. Four new `IPathInstanceRepository` methods
  (`GetCycleStateAsync`, `GetOrCreateCycleStateAsync`,
  `GetUsedPlayerIdsInCycleAsync`, `AddInstanceWithCycleUsageAsync`);
  `XGPathGameModule.GenerateInstanceAsync` now selects targets only from
  eligible players not yet used in the current cycle, rolling the cycle
  over (tolerant "remaining unused < N" rule) before selecting when
  needed, writing the puzzle/instance and cycle-usage state in one unit of
  work. New `GET /admin/xg-path/cycle` (`AdminXGPathEndpoints`,
  `"Admin"`-policy-gated, registered unconditionally) is a pure read of
  the persisted cycle state — never triggers round generation or a live
  familiarity check. `dotnet` was unavailable in the implementation
  sandbox; the migration's `Designer.cs`/`XGArcadeDbContextModelSnapshot.cs`
  were hand-derived from the existing migration pattern rather than
  machine-generated and still need a real `dotnet build`/`dotnet ef`
  verification in CI. Frontend panel (REQ-1209) and tests (both REQs) not
  yet built — tracked by S-093's own updated status note. REQ-1208/1209,
  ADR-0058.
- 2026-08-03 — `docs/requirements-document.md` (v1.45 → v1.46),
  `docs/backlog.md` — frontend implementation of REQ-1209 (S-093's
  remaining `ui-implementer` pass): new `XGPathCycleSection` in
  `frontend/src/admin/AdminScreen.tsx`, rendered unconditionally alongside
  `AccountMetricsSection` and reusing its exact fetch/gating pattern
  (401 escalates via `onAuthError`, 403 hides the section, any other error
  shows inline, "no data yet" renders via the existing
  `admin-screen__empty` class rather than as an error or a blank section).
  New `fetchAdminXGPathCycle` helper (`frontend/src/lib/api.ts`) and
  `AdminXGPathCycleState` type (`frontend/src/lib/types.ts`) against
  `GET /admin/xg-path/cycle`'s existing response shape. No new CSS/design
  tokens — reuses `admin-screen__metrics`/`admin-screen__metric-label`/
  `admin-screen__metric-value mono-figure`. `npx tsc -b`, `npm run build`,
  `npm run lint`, and the full Vitest suite (453 passing, including the
  pre-existing `AdminScreen.test.tsx` unchanged) all verified locally.
  Tests for REQ-1209's UI coverage (and REQ-1208's backend coverage) still
  tracked as S-093's next, separate `test-writer` pass. REQ-1209.

- 2026-08-03 — `docs/requirements-document.md` (v1.43 → v1.44),
  `docs/decisions/0058-xg-path-target-cycle-tracking.md` (new),
  `docs/architecture-document.md` (v0.78 → v0.79) — `/orchestrate` ran on
  S-093 (xG Path: no-repeat target selection across rounds + admin cycle
  visibility). Requirements pass: added REQ-1208 (targets don't repeat
  until the eligible, ADR-0056-familiarity-filtered pool has cycled once)
  and REQ-1209 (admin-visible cycle status on the existing
  `AdminScreen.tsx`, REQ-503/509/510's surface). Both `Status: Not yet
  implemented — drafted only`; code not yet written, tracked by S-093.
  ADR-0058 records the two decisions the backlog entry explicitly flagged
  as needing one rather than an assumption: cycle-tracking state is xG
  Path's own data (never a field on the shared `Player` entity, per
  ADR-0042's precedent), and a cycle is scored against the live,
  ADR-0056-filtered pool `PickDistinct` actually samples from (not the
  larger structurally-eligible-only pool), with a tolerant
  "remaining-unused-below-N" completion rule rather than an exact-zero
  check, to tolerate that pool's documented live instability. REQ-1201/1202.

- 2026-08-03 — `docs/architecture-document.md` (v0.77 → v0.78) — REQ-1201's
  eligibility check (`XGPathGameModule.GetEligiblePlayerIdsAsync`) no
  longer loads the entire `PlayerCareerStint` table
  (`IPlayerStoreRepository.GetAllCareerStintsByPlayerAsync`, now removed)
  on every xG Path round generation — that table grew to ~608K rows via
  ADR-0055's `prefetch-player-careers` job and kept growing, so a
  full-table read on a live (non-admin) path no longer scaled. Replaced
  with a two-pass narrowing: new `GetCareerStintCandidatePlayerIdsAsync`
  reads only `(PlayerId, ClubName)` pairs and filters to a provable
  superset of REQ-1201's real candidates (never excludes a player the
  unchanged `IsEligible` would accept), then the pre-existing
  `GetCareerStintsByPlayerIdsAsync` loads full data only for that
  narrowed set. Pure internal performance fix — REQ-1201's eligibility
  semantics are unchanged, no REQ/ADR needed; COMP-06/COMP-11 rows in
  the architecture doc updated to match, since they named the removed
  method specifically. See `NOTES.md`'s 2026-08-03 entry for the
  original scale finding.

- 2026-08-03 — `docs/backlog.md` (S-092 status) — ran `/orchestrate` on
  S-092 ("xG Grid: widen player pool using xG Path's full-career data");
  dropped before any code was written. `requirements-writer` and
  `architecture-reviewer` independently found the story's own proposal —
  `GridGameModule`'s correctness path reading `PlayerCareerStint` —
  directly conflicts with ADR-0042 (2026-07-26), which explicitly forbids
  exactly this read path and instructs agents to "stop and flag" rather
  than implement it. Also found `PlayerCareerStint` has no nationality
  field and no reliable QID join to `ClubDefinition`, so it couldn't fully
  satisfy REQ-101/102 even absent the ADR conflict. Escalated to the user
  via `AskUserQuestion`; decision was to drop the story rather than write
  a superseding ADR or scope a narrower variant. No REQ or ADR changes.
  See S-092's backlog entry for full detail. ADR-0042.

- 2026-08-03 — `docs/design-document.md` (v0.63 → v0.64), `NOTES.md` —
  three user-tester bug fixes (xG Path clue reveal, country flags, a
  `PlayerNameIndex.BirthYear` data-quality bug). (1) `PathTimeline.tsx`'s
  solved/failed reveal used to REPLACE the last real clue turn's own node
  instead of appending after it, silently deleting that turn's content
  (a bundled multi-club turn or the year-range/position/nationality/age
  content) the instant the puzzle locked — contradicted this codebase's own
  "every past clue stays visible" rule. Fixed to append the reveal as a
  separate, trailing node; design doc's SCREEN-10 "Solved state" bullet
  status-noted (not rewritten) the same way its existing S-086 note already
  documents a stale assumption. (2) Flags moved from Unicode emoji
  (`categoryDisplay.ts`'s old `flagEmojiFor`) to bundled inline SVGs
  (`frontend/src/lib/countryFlags.tsx`) — Windows Chrome/Edge render emoji
  through the host OS font, and Windows dropped color flag glyphs from its
  system font, so a flag emoji degraded to its two bare Regional Indicator
  Symbol letters (e.g. "GB") with no flag graphic at all; Firefox alone
  avoided this by bundling its own emoji font. Design doc's §1 "Imagery
  note" updated to match. (3) `WikidataClient.ParseNameIndexBindings` and
  `PlayerNameIndexImporter` used to silently pick one of two conflicting
  Wikidata P569 (date of birth) statements for the same player with no
  correctness signal behind the choice (whichever SPARQL row arrived first,
  or whichever birth-year slice ran last) — a real report showed Michael
  Owen's autocomplete entry carrying birth year 1976 instead of his actual
  1979. Fixed to null out the ambiguous value instead of guessing either
  way; see `NOTES.md`'s own entry for the full mechanism and a flagged
  sandbox limitation (no live Wikidata/`dotnet` access this session, so the
  backend fix was verified by manual review, not a real build/test run —
  recommend a CI run before merging).

- 2026-08-03 — `docs/design-document.md` (v0.62 → v0.63),
  `docs/requirements-document.md` (v1.41 → v1.42), `docs/backlog.md`
  (S-094 status) — `ui-implementer` shipped REQ-216's frontend half.
  Design doc first: §2 gained a new "Placeholder avatar" token/component
  entry (reuses `surface-sunken`/`text-muted`, no new color) per the
  amendment's own flagged blocker, and SCREEN-01a's states 2-4 mocks/
  "Persistent correct-cell border" note were updated with a matching
  REQ-216 status note (the three locked-incorrect combinations, and the
  new `.grid-table__cell--incorrect` red border extending the existing
  correct-cell green-border mechanism). Code:
  `frontend/src/grid/CellState.tsx`'s locked-incorrect branch now renders
  a real matched-player photo (reusing the existing `CellPhoto`
  component), the new placeholder avatar (`CellPlaceholderAvatar`), or
  both with/without a canonical name, depending on which of REQ-216's
  three combinations applies — never a checkmark/cross icon there anymore,
  mirroring REQ-214/S-048's own established pattern. State 2 is completely
  unaffected. `frontend/src/grid/Grid.tsx`/`Grid.css` add the red border on
  `.grid-table__cell` (not `.grid-cell`/`CellState.tsx`), mirroring the
  correct-cell border's own placement and reasoning.
  `frontend/src/lib/types.ts` adds `incorrectGuessMatchedPlayerName`/
  `incorrectGuessMatchedPlayerPhotoUrl` to `CurrentRoundGuess`/
  `SubmitGuessResponse`, confirmed camelCase against the backend records.
  Vitest coverage added/updated in `CellState.test.tsx`, `Grid.test.tsx`,
  `GridCell.test.tsx` for state 2 (unaffected), the three locked-incorrect
  combinations, and the fixed-footprint mechanism. REQ-216/ADR-0057/S-094.

- 2026-08-03 — `docs/requirements-document.md` (v1.40 → v1.41),
  `docs/backlog.md` (S-094 status) — `backend-implementer` shipped REQ-216's
  backend half: `GuessSubmissionService` resolves
  `IGameModule.ResolveWrongGuessPlayerAsync` exactly once, only when a cell
  locks with its final guess still incorrect; `GridGameModule`'s
  implementation is cache-first, then ADR-0057's Wikidata-only
  `WikidataClient.QueryPlayerPhotoByNameAsync` for the photo (new
  `IPlayerNameIndexRepository.FindByNormalizedNameAsync` supplies the
  always-resolvable canonical-name fallback); persisted onto two new
  nullable `Guess` columns (migration `AddGuessMatchedPlayerNameAndPhoto`)
  in the same write, read back (never re-resolved) by `GET /rounds/current`
  as `IncorrectGuessMatchedPlayerName`/`IncorrectGuessMatchedPlayerPhotoUrl`.
  Also documents, retroactively, the same-day placeholder-avatar amendment
  (both REQ-216 no-photo branches now show a placeholder graphic instead of
  nothing, per direct product-owner sign-off) that had updated
  `docs/requirements-document.md`/`docs/backlog.md` without a CHANGELOG
  entry — confirmed to require no backend change, since it's a pure
  frontend rendering decision against the same two nullable fields.
  Frontend (`CellState.tsx`) is still queued. REQ-216/ADR-0057/S-094.

- 2026-08-03 — `docs/architecture-document.md` (v0.76 → v0.77),
  `docs/decisions/0057-wrong-guess-photo-lookup-scope.md` — `doc-sync`
  closed two non-blocking architecture-review gaps from REQ-216's
  pre-merge gate. Architecture doc: added a COMP-04/COMP-05 status note
  (the §10 ADR-log entry alone wasn't the "component responsibility/data
  flow changed" update CLAUDE.md step 3 calls for) covering the new
  `IGameModule.ResolveWrongGuessPlayerAsync` method, `GridGameModule`'s
  cache-first-then-Wikidata-only implementation, the two new nullable
  `Guess` columns, and `XGPathGameModule`'s unconditional-null
  implementation. ADR-0057: added a Consequences addendum naming that this
  trigger has no WDQS-level rate-limiting of its own — same uncapped
  exposure as REQ-211's existing guess-time fallback
  (`GridGameModule.RefreshCellFromLiveLookupAsync`) today, not a new gap,
  but worth stating explicitly given REQ-216's plausibly higher firing
  volume. No code, tests, requirements, backlog, or design doc changes.
  REQ-216/ADR-0057/S-094.

- 2026-08-03 — `docs/requirements-document.md` (v1.37 → v1.39, new REQ-216),
  `docs/backlog.md` (new S-094), `docs/decisions/0057-wrong-guess-photo-lookup-scope.md`
  (new), `docs/architecture-document.md` (§10 table) — GitHub feature
  request "show the guessed player's photo on an incorrect guess, with a
  red border" (xG Grid) was flagged before any code, since it reverses a
  deliberate prior decision (`CellState.tsx`'s states-2/3 "no name shown on
  a wrong guess" comment) and has no existing data path (`PlayerNameIndex.PhotoUrl`
  was removed 2026-07-18). Confirmed with the product owner via
  `AskUserQuestion`: wanted, but only on the locked/final-incorrect case
  (state 3/4), never an in-progress guess (state 2). Drafted as REQ-216.
  `architecture-reviewer` then resolved the open "how is the photo
  resolved" question via ADR-0057: reuse ADR-0011's `WikidataClient` as a
  new, distinct, lower-priority trigger — Wikidata-only, no API-Football
  fallback, fires once at cell-lock time, fails silently to no-photo
  rather than fail-closed-as-incorrect, since a wrong guess has no
  correctness verdict left to compute. Not yet implemented — S-094 is
  ready to size/build in a future session.

- 2026-08-03 — `docs/requirements-document.md` (v1.36 → v1.37, REQ-1203
  status note addendum) — a concurrent quality-gate pass on the same
  session's dedup fix (commit `a78e52d`) found and documented (in code
  comments/tests, `WikidataClient.cs`/`WikidataClientTests.cs`) a known,
  accepted limitation the REQ note below hadn't yet stated: dedup is still
  keyed on the full `(ClubName, StartYear, EndYear, AppearanceCount)`
  tuple, so two rows for what could plausibly be the same stint but that
  disagree on `AppearanceCount` (one `null`, one known) still do not merge.
  Deliberately not widened — see the new status note for why loosening the
  match risks a correctness regression, not just a display one. This entry
  adds the corresponding requirements-document.md note; the code/test
  changes themselves were already committed in `a78e52d`, not by this
  doc-sync pass.
- 2026-08-03 — `docs/requirements-document.md` (v1.35 → v1.36, REQ-204
  status note) — direct product feedback: a correct cell (SCREEN-01a
  states 1/4) now also gets a persistent light-green (`--color-accent-green`)
  2px border, always visible, in addition to the existing checkmark/points
  text — previously "correct" was signaled by text alone.
  `frontend/src/grid/Grid.tsx`/`Grid.css` apply the new
  `.grid-table__cell--correct` class on `.grid-table__cell` (the `<td>`),
  never on the button or photo-layer element, so the border renders
  correctly around both the no-photo and photo cell variants.
  `docs/design-document.md`'s matching SCREEN-01a note (v0.62) was already
  added earlier this session; this entry just adds requirements-document.md's
  own status note and its CHANGELOG line. Tests: `Grid.test.tsx`.
- 2026-08-03 — `docs/requirements-document.md` (v1.34 → v1.35, REQ-1203
  status note) — bug fix: `WikidataClient.ParseCareerStintBindings`
  deduplicates career stints by exact `?clubLabel` string (no `?club` QID is
  selected to key on instead), so a real stint whose label appeared in two
  variants — e.g. "Liverpool" and "Liverpool F.C." — surfaced as two
  separate xG Path club-reveal nodes instead of deduping into one, reported
  directly by a player with a screenshot. Fixed with a new
  `NormalizeClubName` step (`backend/src/XGArcade.DataSync/Wikidata/
  WikidataClient.cs`) that strips a small, explicit set of trailing
  football-club legal-suffix tokens (`FC`/`F.C.`/`AFC`/`A.F.C.`) before the
  existing dedup runs — deliberately narrow, not a general fuzzy-name
  matcher, to avoid conflating two different clubs. Tests in
  `WikidataClientTests.cs`.
- 2026-08-03 — `docs/backlog.md` (S-092/S-093 added, queued not built) —
  product feedback session on xG Grid/xG Path raised two future-scope items
  neither ready for implementation this session: (1) widening xG Grid's
  player pool by reading xG Path's already-fetched `PlayerCareerStint` data
  (ADR-0054's own follow-up note already named this as its own future story);
  (2) xG Path no-repeat target selection across rounds plus an admin-visible
  "full cycle completed" signal on `AdminScreen.tsx`. Both queued rather than
  built — neither has a requirements/architecture pass yet. See the three
  entries above for what *was* built from the same feedback batch (the
  Liverpool/Liverpool F.C. duplicate club-node bug, its accepted dedup
  limitation, and the correct-cell green border).
- 2026-08-02 — `docs/decisions/0056-xg-path-familiarity-filter.md` (new),
  `docs/requirements-document.md` (v1.33 → v1.34, REQ-1201/1203/1207 status
  notes), `docs/architecture-document.md` (v0.75 → v0.76, COMP-07/COMP-11),
  `docs/implementation-document.md` (v0.88 → v0.89) — player feedback on xG
  Path ("I got this Austrian guy I had no idea who he is," national team
  showing up as a "club," Position clue rendering a raw Wikidata QID URI
  instead of a name, "Age" label on what's actually a birth year). Three
  fixes: (1) `WikidataClient.QueryPlayerCareerStintsByQidsAsync` now excludes
  national teams from `?club` (Wikidata models caps under the same P54
  property as club membership) — was violating REQ-1203's own "national team
  caps are never revealed as a clue" acceptance criterion. (2) every P413
  ("position")-fetching query now requests `?positionLabel` via
  `SERVICE wikibase:label` instead of the raw `?position` binding, which was
  a bare entity URI, never a label; the backfill query needed the label
  service added outright. (3) new `IWikidataClient
  .QuerySitelinkCountsByQidsAsync` + `PlayerFamiliarityService` — a Wikipedia
  sitelink-count familiarity filter on top of REQ-1201's existing structural
  eligibility checks, fails open on a Wikidata failure or data gap — ADR-0056
  (product owner's chosen signal, among sitelink count/total appearances/
  trophy won). Frontend: `PathTimeline.tsx`'s "Age" clue now displays as
  "Birth year" (the value was already a birth year, never a computed age;
  only the label changed).
- 2026-08-02 — `docs/decisions/0055-proactive-player-data-buildout.md`
  (Consequences/Follow-up amended with real-run findings), `NOTES.md` —
  `prefetch-player-careers`'s first real run processed all 49 seeded
  countries (177,872 players, 607,914 stints) but 4 of many 200-player
  career-fetch batches hit `WikidataClient`'s 15s default timeout (the same
  bug class as the 2026-07-17 `import-player-name-index` timeout entry, not
  the WDQS server-cap risk this ADR had originally flagged, which did not
  materialize). Fixed with the same 60s `queryTimeout` override
  `import-player-name-index` already needed.
- 2026-08-02 — `docs/decisions/0055-proactive-player-data-buildout.md`
  (Proposed → Accepted, same session), `docs/architecture-document.md`
  (v0.74 → v0.75), `docs/implementation-document.md` (v0.87 → v0.88) — three
  follow-up moves to ADR-0054's "build the player-data cache up proactively"
  feedback, all shipped: (1) Celtic added to `ReferenceDataSeeder.Clubs`
  (unverified QID, flagged for human verification, same as every other
  recent addition); (2) `warm-player-cache.yml`/`import-player-name-index.yml`
  moved from `workflow_dispatch`-only to a weekly cron, alongside their
  existing manual trigger; (3) new `prefetch-player-careers` CLI verb/
  workflow (`workflow_dispatch` only for now) and `PlayerCareerPrefetchService`
  sweep every seeded `CountryDefinition`'s full player pool via a new
  `IWikidataClient.QueryPlayerPoolByNationalityAsync`, writing careers
  directly — this is what actually widens xG Path's target-player pool
  beyond whatever xG Grid's own lookups happened to discover, not just
  enriches an already-selected target (ADR-0054's own scope). Note: ADR-0055
  originally proposed sourcing move (3) from `PlayerNameIndex`; that turned
  out to be unworkable (`PlayerNameIndex` has no `WikidataQid` column at
  all, ADR-0007) and was corrected to source from `CountryDefinition`
  instead during implementation — see the ADR's own "Correction" note.
- 2026-08-02 — `docs/decisions/0054-xg-path-direct-career-stint-fetch.md` (new),
  `docs/architecture-document.md` (v0.73 → v0.74),
  `docs/implementation-document.md` (v0.86 → v0.87) — xG Path now fetches its
  own targets' full career directly from Wikidata (`IWikidataClient
  .QueryPlayerCareerStintsByQidsAsync`, a new `IPlayerCareerStintRefreshService`
  in `XGArcade.DataSync`, called from `XGPathGameModule.GenerateInstanceAsync`
  right after target selection) instead of relying solely on whatever xG
  Grid's country/club lookups happened to persist as a byproduct (ADR-0042).
  Fixes a live-reported gap (a Timothy Weah puzzle missing real Juventus/
  Marseille stints, and unable to ever show Celtic at all since it isn't a
  seeded club). Deliberately does not widen xG Path's candidate/eligibility
  pool itself — see ADR-0054's Follow-up section, including an explicit
  product-feedback note that this codebase's player-data cache should move
  toward being built up proactively rather than purely reactively, which is
  its own future story. `Games.XGPath` gained a `ProjectReference` to
  `XGArcade.DataSync` (mirroring `Games.XGGrid`'s existing one) — see
  ADR-0054.
- 2026-08-02 — `docs/requirements-document.md` (v1.31 → v1.32),
  `docs/implementation-document.md` (v0.85 → v0.86) — Backend
  half of the xG Path live user-testing feedback batch, three fixes:
  (1) `PlayerNameNormalizer.Normalize` now transliterates non-decomposable
  Latin letters (Ø/Æ/Œ/Đ/Ł/ß/Þ) that NFKD normalization silently left
  untouched — "Ødegaard" now normalizes the same as "Odegaard," fixing both
  autocomplete and guess correctness for any player whose name contains one
  of these letters; new `PlayerAliasNormalizedAliasBackfiller`
  (`XGArcade.Data.Seeding`, wired into `migrate-and-seed`, mirroring
  `PlayerNameIndexWordBackfiller`'s wiring) re-derives already-stored
  `PlayerAlias.NormalizedAlias` values under the fixed normalizer —
  `Player.NormalizedFullName` needed no equivalent new wiring since the
  pre-existing `PlayerNormalizedFullNameBackfiller` already re-derives it on
  every `migrate-and-seed` run; `PlayerNameIndex.NormalizedName`
  (autocomplete-only, COMP-10) needs a manual one-time
  `import-player-name-index` workflow re-run post-deploy, not a new
  backfiller, since that importer already fully re-derives it on every run.
  (2) `Player.Position`/`Player.BirthYear` (REQ-1207) were only ever set at
  `Player` row creation time, so every row created before migration
  `20260727140000_AddPlayerPositionAndBirthYear` shipped has both
  permanently null — new `PlayerPositionBirthYearBackfillService`
  (`XGArcade.DataSync.Wikidata`), a near-exact mirror of
  `PlayerPhotoBackfillService` (REQ-214), backfills them via a new
  `IWikidataClient.QueryPlayerPositionsAndBirthYearsByQidsAsync`
  (P413/P569-by-QID batch query) and new `IPlayerStoreRepository`
  read/write pair (`GetPlayersMissingPositionOrBirthYearAsync`/
  `UpdatePlayerPositionsAndBirthYearsAsync`), wired to a new
  `dotnet run -- backfill-player-position-birthyear` CLI verb and
  `.github/workflows/backfill-player-position-birthyear.yml`
  (`workflow_dispatch`-only, mirroring `backfill-player-photos.yml`).
  (3) `PathEndpoints.cs`'s `GET /path/current` now reveals the target
  player's name/photo whenever a puzzle is `Locked` (solved OR REQ-1205's
  7-attempt cap exhausted), not only when the guess `IsCorrect` — a puzzle
  that locked via exhausted attempts previously never told the player who
  the answer was. New REQ-1203 status note records the boundary correction
  ("never leak the answer for a puzzle the player can still guess on,"
  replacing the old "unsolved puzzle" phrasing that conflated "unsolved"
  with "still live"). Frontend half of fix 3 already shipped separately
  (see the 2026-08-02 `docs/design-document.md` entry below) and was
  waiting on this backend change. No architecture/ADR change for any of the
  three — all within existing component boundaries (ADR-0007's
  autocomplete/correctness separation confirmed intact: only one shared
  `PlayerNameNormalizer.Normalize` function, never a shared query path;
  REQ-1207's "set once at creation" contract preserved going forward, this
  is purely a backfill of pre-existing rows, same precedent as REQ-214's
  photo backfill).

- 2026-08-02 — `docs/design-document.md` (v0.60 → v0.61) — Live user-testing
  feedback batch on the xG Path puzzle screen (`frontend/src/path/`), three
  fixes, all tokens-only: (1) an empty guess submission is now an
  intentional skip to the next clue instead of a client-side error, with
  the submit button relabeling "Next clue"/"Guess" to match; (2) the
  career-stint year-range clue now renders one club/year-range pair per
  line instead of one dense inline-joined paragraph; (3) `PathTimeline` now
  accepts a `locked` prop (alongside `solved`) and renders a distinct,
  non-gold "✕ Out of attempts" reveal node when a puzzle locks without ever
  being solved (REQ-1205's attempt cap exhausted) — previously this case
  showed no reveal at all. Fix 3 depends on a parallel, separately-scoped
  backend change (`PathEndpoints.cs`) populating `resolvedPlayerName`/
  `resolvedPlayerPhotoUrl` whenever `locked` is true, not only when
  `isCorrect`; the frontend degrades gracefully (label only, no broken
  name/photo line) until that ships. New SCREEN-10 status note added
  documenting all three, including the shake-on-skip and skip-placeholder
  (`"(skipped)"`) judgment calls. No REQ/ADR change — these are
  implementation-level UX fixes within SCREEN-10's existing spec, not new
  requirements.

- 2026-08-02 — `docs/backlog.md` — Doc-sync for S-088 (E2E coverage for
  the full xG Path game loop), covering three commits (`c0bdd3a` backend
  endpoint, `ae382e9` E2E spec, `c8eb356` quality-gate fixes).
  `POST /internal/test-data/seed-guessable-path-round`
  (`backend/src/XGArcade.Api/Rounds/InternalRoundEndpoints.cs`) is a new,
  non-Production-only sibling to `seed-guessable-round` — same
  registration gate and repository-only write discipline (ADR-0006
  boundary rule 4) — that deterministically creates a guessable xg-path
  round (one `Player` with three career stints, one `PathInstance`/
  `PathPuzzle`, an active `Round`) for E2E setup; two new tests added in
  `backend/tests/XGArcade.Api.Tests/RoundEndpointTests.cs`.
  `frontend/tests/e2e/play-path.spec.ts` is the new Playwright spec the
  story asked for: one continuous run through generation → clue reveal →
  wrong guess → correct guess → round close → game-scoped leaderboard
  (confirming xG Path's points are not blended with xG Grid's, per
  REQ-410/ADR-0043). `docs/requirements-document.md`'s REQ-807 (v1.32 →
  v1.33) was already extended in `c8eb356` by a separate
  requirements-writer pass to document the new endpoint and correct its
  stale "only grid/round content is seeded this way" line — noted here
  for completeness, not re-edited. `docs/backlog.md`'s S-088 entry marked
  "— done, 2026-08-02" with a "Built as:" paragraph naming one real
  deviation from the story's literal text (a new sibling endpoint rather
  than a parameter extension of `seed-guessable-round` itself, since that
  endpoint has no game-agnostic shape to extend) and the quality-gate
  outcome (`architecture-reviewer`: pass, no new ADR; `quality-architect`:
  pass after three findings fixed — a REQ-806/REQ-807 comment mislabel, a
  non-REQ-prefixed test name, and de-duplicating the unique-test-player
  boilerplate into a shared helper). `docs/architecture-document.md` and
  `docs/implementation-document.md` were checked and left unchanged: the
  new endpoint is already covered by boundary rule 4's generic wording and
  §6.6's generic test-data-reset flow description (neither names
  `seed-guessable-round` specifically, so neither needed a matching
  addition for its sibling), and the implementation doc's `/e2e` folder
  description is likewise generic (doesn't name individual spec files,
  e.g. `play-grid.spec.ts` isn't named either) — no tech, entity, or
  folder-layout change to record. — REQ-807, S-088
- 2026-08-02 — `docs/backlog.md`, `docs/requirements-document.md` (v1.31 →
  v1.32), `docs/architecture-document.md` (v0.72 → v0.73),
  `docs/implementation-document.md` (v0.85 → v0.86), `docs/design-
  document.md` (v0.60 → v0.61) — Doc-sync for S-087 (frontend leaderboard
  game switcher, SCREEN-03, ADR-0043/REQ-410), covering three commits
  (`caaaade` backend, `8ce49a3` frontend, `f102f6d` tests). Turned out to
  be full-stack, not frontend-only: S-078 had already added `gameKey` to
  every `ILeaderboardService` method, but `LeaderboardEndpoints` (the
  Api/outer-composition layer) still hardcoded
  `GridGameModule.XGGridGameKey` at every call site, so no client could
  ever request xG Path's ranking — this story closed that gap with an
  optional `gameKey` query parameter (defaulting to xg-grid, 400 on an
  unrecognized value, kept in the Api layer per ADR-0003) on every route
  except the single-round-by-id one, alongside the game-switcher tab row
  itself. `docs/backlog.md`'s S-087 entry marked "— done, 2026-08-02" with
  a "Built as" paragraph describing the backend addition, the frontend
  tab row (reusing `GameSelectScreen.tsx`'s existing
  `XG_GRID_GAME_KEY`/`XG_PATH_GAME_KEY` constants), and one deliberate
  scope addition beyond the story's literal text (switching games while a
  specific past round is drilled into now backs out to the round list,
  since a round belongs to exactly one game). `docs/requirements-
  document.md`'s REQ-410 gained a 2026-08-02 status note marking the
  frontend/API-parameter gap closed and its Test-level line updated —
  the API-level cross-game test explicitly flagged as "not yet addable"
  in REQ-410's own S-078-era text is now covered
  (`LeaderboardEndpointTests.cs`'s `REQ410_*` cases); REQ-401/404's own
  status notes referencing S-087 as a pending follow-up got matching
  updates. `docs/architecture-document.md`'s COMP-02 status note gained a
  2026-08-02 addendum describing the same Api-layer change and confirming
  `Core.Leagues` stayed untouched (ADR-0003 boundary respected).
  `docs/implementation-document.md`'s REQ-410/S-078 paragraph, which had
  explicitly said "no route query parameter exists yet," gained a
  matching 2026-08-02 addendum. `docs/design-document.md`'s SCREEN-03
  "Game switcher" note changed from "design only ... not yet built" to
  "built," pointing at the backlog entry for detail. No new ADR:
  ADR-0043's own Consequences section already named this exact frontend
  follow-up as deferred, not undecided — this story is that follow-up
  landing, not a new structural decision, confirmed during this story's
  own quality gate (architecture/quality review performed directly by the
  orchestrating session after both review subagents hit the account's
  session usage limit; see the session's own record for what was
  checked). 419/419 Vitest passing (416 pre-existing + 3 new), clean
  `tsc -b`/`oxlint`. Backend build/tests deferred to CI — `dotnet` is not
  installed in this sandbox; new backend tests were hand-traced against
  the actual endpoint/service code instead of run.
- 2026-08-01 — `docs/backlog.md`, `docs/requirements-document.md` (v1.30 →
  v1.31), `docs/architecture-document.md` (v0.71 → v0.72),
  `docs/implementation-document.md` (v0.84 → v0.85) — Doc-sync for S-091
  (xG Path guess autocomplete, extending REQ-207 to a second consumer),
  covering both commits (`27ed880` the backlog entry, `3dd0027`
  implementation + tests). `docs/backlog.md`'s S-091 entry marked "— done,
  2026-08-01" with a "Built as:" paragraph (matching S-085/S-086's
  convention) confirming the shipped code matched the story's own stated
  scope with no deviations: same debounce/limit constants and
  keyboard-nav/ARIA pattern as `GuessInput.tsx` (xG Grid), no backend
  change, no REQ-209 disambiguation picker. `docs/requirements-document.md`
  gained a small REQ-207 status note (2026-08-01, S-091) noting the second
  UI consumer for discoverability — smaller than the REQ-303/REQ-720
  precedent, since REQ-207's Given/When/Then was never game-scoped to
  begin with, so there was no stale "exactly one game" language to correct;
  the note also glosses REQ-207's one xG-Grid-flavored phrase ("current
  cell") for a game with no cell/category axis. `docs/architecture-
  document.md`'s §6.2b (xG Path clue reveal/guess flow) diagram gained the
  same autocomplete-suggestions step §6.2's xG Grid diagram already
  documents, plus a REQ-207 reference in the section heading — a real,
  if small, data-flow addition (this interaction didn't happen for xG Path
  before S-091); boundary rule 5's autocomplete/correctness separation text
  was already fully game-agnostic and needed no change.
  `docs/implementation-document.md`'s frontend project-structure `/path`
  entry corrected: it previously stated (accurately as of S-086, now
  stale) that xG Path had no autocomplete at all — updated to describe
  S-091's addition while confirming the disambiguation picker (REQ-209)
  and REQ-215 suggestion entry point remain out of scope. No new ADR:
  both `architecture-reviewer` and `quality-architect` confirmed during
  S-091's own quality gate this is pure reuse of an already-generic,
  already-documented capability (`GET /players/autocomplete`,
  ADR-0007) by a second consumer, not a new structural decision, and this
  doc-sync pass found nothing to contradict that.
- 2026-08-01 — `docs/backlog.md`, `docs/design-document.md` (v0.59 →
  v0.60), `docs/implementation-document.md` (v0.83 → v0.84) — Doc-sync for
  S-086 (SCREEN-10 xG Path puzzle screen, growing timeline), covering both
  commits (`18b1cc2` implementation + tests, `928bd85` quality-gate
  fixes — the narrower doc updates that commit already made, covered by
  its own CHANGELOG entry immediately below, are not duplicated here).
  `docs/backlog.md`'s S-086 entry marked "— done, 2026-08-01" with a
  "Built as:" paragraph (matching S-085/S-084's convention) naming both
  commits, the `CategoryLabel`/`CategoryGlyph` relocation to
  `frontend/src/components/` as a deliberate scope addition (same spirit
  as S-085's own `HeaderNav.tsx` addition), and cross-referencing
  `docs/design-document.md`'s status notes for the two deviations from the
  story's literal text (the photo-fallback wording, and "Next puzzle"
  appearing on locked-but-unsolved as well as solved).
  `docs/design-document.md`'s SCREEN-10 section header updated from
  "Design only — no code yet" to "Built as specified" (with the one real
  deviation flagged, not silently claimed as zero-deviation like
  SCREEN-09's own update was). Added a new inline status note below the
  solved-state bullet: the spec's "falling back to the same initials-avatar
  treatment REQ-214 already established" doesn't match anything REQ-214
  has ever actually done — REQ-214/SCREEN-01a's no-photo case has, at
  every point in its history, rendered plain text (name) plus a checkmark,
  never an avatar of any kind — `PathTimeline.tsx`'s `SolvedNode` renders
  today's real text-only fallback rather than a nonexistent avatar
  component; this note was not covered by the quality-gate-fix commit's
  own narrower re-fetch-failure status note. Also documented the "Next
  puzzle on locked-unsolved, not only solved" deviation inline on the same
  bullet.
  `docs/implementation-document.md`'s frontend project-structure section
  updated: added a `/path` entry (mirroring `/grid`'s), a new
  `/components` entry for the relocated `CategoryLabel`/`CategoryGlyph`,
  removed `CategoryLabel` from `/grid`'s own listing, and added
  `pathRules.ts`/`CurrentPathResponse`/`fetchCurrentPath` to the `/lib`
  entry. `docs/requirements-document.md` checked and left unchanged:
  REQ-1203/1204/1205's acceptance criteria are backend-scoped (clue
  sequencing, guess resolution, attempt-cap logic served by `GET
  /path/current`), with no "so I can see/open the app" framing the way
  REQ-303 has — matching the precedent set when REQ-201/202/210 (xG Grid's
  equivalent backend rules) never gained a frontend-build status note when
  their own UI (SCREEN-01/02) shipped; SCREEN-10 is design-doc-owned
  territory, covered above instead. `docs/architecture-document.md`
  checked and left unchanged: no COMP boundary, responsibility, or data
  flow changed (frontend-only diff; `architecture-reviewer` already
  confirmed no boundary drift during the quality gate, and the doc has no
  reference to any of the touched frontend paths to go stale). No new ADR:
  the `CategoryLabel` relocation is a straightforward extension of the
  established per-game-screen-module pattern, not a new structural
  decision, per `architecture-reviewer`'s own quality-gate finding.
- 2026-08-01 — `docs/design-document.md` (v0.58 → v0.59) — S-086
  (SCREEN-10 xG Path puzzle) quality-gate follow-up fixes: moved
  `CategoryLabel`/`CategoryGlyph` from `frontend/src/grid/` to the shared
  `frontend/src/components/` location (was a cross-game-module import from
  `frontend/src/path/PathTimeline.tsx`), fixed a comment/type mismatch on
  `PathClueKind` in `frontend/src/lib/types.ts`, fixed `PathScreen.tsx`'s
  guess-submit handler to distinguish a genuine submission failure from a
  failed/null post-submit re-fetch (documented as a new SCREEN-10 status
  note, since neither case was previously specified), added a same-session
  image-load-failure fallback to `PathTimeline.tsx`'s solved-state photo
  (matching `CellState.tsx`'s existing pattern), dropped the redundant JS
  `usePrefersReducedMotion` hook (`frontend/src/lib/motion.ts`, removed) in
  favor of the CSS-only `@media (prefers-reduced-motion: reduce)` override
  `PathTimeline.css` already had, and fixed a duplicate React key on
  `PathScreen.tsx`'s sibling `PathTimeline`/`PathGuessInput` elements. No
  REQ/ADR changes — implementation-level fixes plus one new design-doc
  status note.
- 2026-08-01 — `docs/requirements-document.md` (v1.29 → v1.30),
  `docs/architecture-document.md` (v0.70 → v0.71), `docs/backlog.md`,
  `docs/decisions/0052-pair-lookup-failure-persistence-and-club-club-query-fix.md`
  (2026-08-01 status note added) — Live-incident follow-up to ADR-0052:
  added `PairLookupFailureCleaner` (`XGArcade.Data.Seeding`) and its
  `clear-pair-lookup-failures` CLI verb (`Program.cs`,
  `.github/workflows/clear-pair-lookup-failures.yml`) — a pair-scoped
  alternative to `clean-stale-club-attributes` for clearing
  `PairLookupFailure` rows stuck at/above `PersistentFailureThreshold`,
  after the first real run under ADR-0052's tracking left 125 Club x Club
  pairs stuck across all 32 seeded clubs, where the existing club-name-
  scoped tool would have wiped ~850 other pairs' worth of good cached data
  to clear them. Touches only `PairLookupFailure`, never
  `PlayerAttribute`/`PlayerData`/`ConfirmedLowMatchPair`. Added a REQ-110
  status note and test-level addendum covering `PairLookupFailureCleanerTests.cs`
  (6 NUnit tests: at-threshold removed, above-threshold removed,
  below-threshold left alone, mixed set only removes the stuck ones,
  empty-table no-op, safe to re-run). No new ADR number — but ADR-0052's
  own "For AI agents" section explicitly required updating that ADR before
  adding a third `PairLookupFailure` invalidation path, so it was amended
  in place (a dated status note, same pattern as ADR-0046's 2026-07-27
  amendment) rather than left to silently drift out of sync — caught by
  `architecture-reviewer` before this was considered done. Backend claims
  were hand-traced against existing patterns, not built or run against a
  live `dotnet` SDK — unavailable in this sandbox; confirm in CI.
- 2026-08-01 — `docs/backlog.md`, `docs/design-document.md` (v0.57 →
  v0.58), `docs/requirements-document.md` (v1.29 → v1.30),
  `docs/implementation-document.md` (v0.82 → v0.83) — Doc-sync for S-085
  (SCREEN-09 multi-tile game select), covering both commits (`58a3ca2`
  implementation + tests, `3829e0d` quality-gate fixes: `aria-describedby`
  for tile descriptions, exhaustive `gameKey` switch typing).
  `GameSelectScreen.tsx` gained a second tile (`XG_PATH_GAME_KEY`) for xG
  Path per SCREEN-09; `App.tsx`'s `Screen` union gained `'path'`
  (placeholder screen only — SCREEN-10's real clue-reveal UI is S-086,
  not yet built); `onSelectGame` is now typed as the exact two-member
  literal union with an exhaustive `switch`/`never` dispatch. A deliberate
  scope addition beyond the story's two literally-named files:
  `frontend/src/nav/HeaderNav.tsx` gained a mirrored "xG Path" entry
  (`isPathCurrent`/`onSelectPath`) so its "Games" list and
  `GameSelectScreen`'s tile order stay in agreement, per REQ-720's own
  "one entry per game" criterion. `docs/backlog.md`'s S-085 entry marked
  "— done, 2026-08-01" with a "Built as:" paragraph (matching S-084's
  convention) naming both commits and the `HeaderNav.tsx`/placeholder-screen
  deviations. `docs/design-document.md`'s SCREEN-09 section updated from
  "design only, no code yet" to "built as specified, no deviations," and
  its matching §7 open-question entry marked resolved-and-built (it had
  previously only been marked spec-resolved). `docs/requirements-document.md`:
  REQ-720's two "Tier 0: exactly one game" asides (the disclosure-list
  criterion and its own "ships now, ahead of a second game" bullet) were
  point-in-time descriptions, not the graded behavior itself (both
  criteria were always written generically, "one entry per game xG Arcade
  currently hosts") — added a status note rather than rewriting the
  criteria, since xG Path becoming real doesn't change what REQ-720
  requires, only which point-in-time aside is now stale; REQ-303's S-021
  bullet got the same treatment (the "no list games endpoint" behavior is
  still exactly true — both game keys remain client-side constants — only
  its "while Tier 0 has exactly one game" framing needed a status note).
  `docs/implementation-document.md`'s `/games` folder-structure entry
  updated from "one static tile for xG Grid" to two tiles, xG Grid then xG
  Path. `docs/architecture-document.md` checked and left unchanged — no
  reference to `GameSelectScreen`/`HeaderNav`/frontend routing exists
  there to go stale, and `architecture-reviewer` confirmed no boundary
  drift during the quality gate (this follows the pre-existing
  `grid`/`isGridCurrent`/`onSelectGrid` pattern exactly, extended to a
  second game). No new ADR: this is UI wiring following an established
  pattern, not a new structural decision — confirmed, not just taken on
  faith from the quality-gate pass. Noted but not resolved (pre-existing,
  predates S-085, out of this doc-sync's scope): REQ-720's own "Flag for
  architecture-reviewer" note about whether the nested "Games" disclosure
  needs an ADR/ADR-0030 amendment is still open. Also noted, not fixed:
  `frontend/tests/e2e/header-nav.spec.ts`'s stale "xG Grid (Tier 0's only
  game)" comment, flagged low-severity by quality-architect, untouched by
  S-085. REQ/ADR refs: REQ-303, REQ-720, ADR-0030 (open flag, unchanged).
- 2026-08-01 — `docs/requirements-document.md`, `MVP-SCOPE.md` — Closed a
  gap the S-089 doc-sync flagged: REQ-215's "Tier framing" note still
  read "flagged, not resolved" even though S-089 had already been built.
  Resolved it explicitly — the player-suggestion pipeline (REQ-215/509/510)
  was pulled forward by deliberate product decision (requested directly,
  by name, same basis as REQ-108/REQ-214/REQ-402-403/REQ-717's own
  precedent), recorded as a new `MVP-SCOPE.md` Tier 1 entry rather than
  left as an unresolved §7 open question.
- 2026-08-01 — `docs/requirements-document.md` (v1.28 → v1.29),
  `docs/architecture-document.md` (v0.69 → v0.70), `docs/backlog.md` —
  Doc-sync for S-089 (REQ-215: player-submitted answer suggestion),
  covering the full session arc: backend (`52f213b`, `POST
  /rounds/{roundId}/cells/{cellId}/suggestions` — guest rejected 403
  server-side, validates playerName/clubs/nationality, persists a
  `PlayerSuggestion`/`PlayerSuggestionClub` row as `Pending`, never writes
  `PlayerAttribute`/`PlayerOverride`/`PlayerNameIndex`/`Guess`), frontend
  (`22608b2`, `SuggestionEntry.tsx` mounted by `GuessInput.tsx` at the two
  REQ-215 trigger points — an incorrect scored guess now shows an outcome
  view instead of closing immediately, and a `LiveLookupUnavailable`
  timeout), test coverage (`ab93894`, `SuggestionEndpointTests.cs` — 11
  NUnit tests — plus `SuggestionEntry.test.tsx`/updated `GuessInput.test.tsx`
  — 382/382 Vitest passing, clean `tsc -b`, clean `oxlint`, all directly
  run), and a same-session architecture fix (`e81189c`): the original
  commit resolved a cell's row/col category types via a direct
  `IGridInstanceRepository`/`GridCell` read from the Api layer, a boundary
  rule 2 violation (ADR-0003) caught by `architecture-reviewer` before
  merge; fixed by adding `IGameModule.GetCellCategoryTypesAsync`
  (implemented by `GridGameModule` and, throwing `NotSupportedException`,
  by `XGPathGameModule`), resolved via the standard `Round.GameKey →
  IGameModuleResolver` path — re-verified as resolved. REQ-215's status
  note now reads "Implemented (submission half only)"; REQ-509's status
  note was checked and needs no change (still correctly "not yet
  implemented," S-090). `architecture-document.md` gained: a COMP-05/
  COMP-11 status note for the new `GetCellCategoryTypesAsync` method, a
  `PlayerSuggestion`/`PlayerSuggestionClub` note on COMP-06's row
  (ADR-0053's "COMP-06-adjacent" placement), and a new §6.2c data-flow
  diagram — closing the gap that no architecture-doc pass happened when
  the feature was first built. Backend claims in this entry (and in
  REQ-215's own status note) were **hand-traced against existing patterns,
  not built or run against a live `dotnet` SDK** — unavailable in this
  sandbox throughout; confirm in CI. One minor, non-blocking gap recorded
  as known-and-accepted, not fixed: `XGPathGameModule
  .GetCellCategoryTypesAsync`'s `NotSupportedException` currently falls
  through to ASP.NET's bare default `500` rather than an explicit
  `ProblemDetails` response — unreachable today since nothing wires
  REQ-215's frontend up for `GameKey = "xg-path"`, worth a deliberate
  `501`/`409` response if that ever changes. `docs/backlog.md`'s S-089
  entry marked "— done, 2026-08-01," matching the convention other
  completed stories (e.g. S-084) already use. No change to
  `docs/design-document.md` (SCREEN-02b already correctly added by the
  frontend implementer) or to REQ-509/REQ-510/S-090, which remain
  correctly not-yet-implemented. REQ/ADR refs: REQ-215, REQ-509, ADR-0003,
  ADR-0053.
- 2026-08-01 — `docs/requirements-document.md` (v1.27 → v1.28),
  `docs/decisions/0052-player-suggestions-separate-admin-view.md` (new),
  `docs/backlog.md` — Finalized the two product decisions the product
  owner made for the REQ-215/509/510 player-suggestion feature drafted
  2026-07-28. (1) **No retroactive rescoring, confirmed final**: REQ-215's
  "No retroactive rescoring" clause is no longer flagged as an open
  question — an admin-approved suggestion (REQ-509) fixes the underlying
  data for future guesses only; the guess that prompted it, and any
  identical guess from another player against the same cell that round,
  keep their original scored outcome unchanged. §7's matching entry is now
  marked resolved 2026-08-01. (2) **Separate admin view, not merged into
  REQ-503's queue**: REQ-509's status note now records this as decided,
  referencing new ADR-0053, which also explicitly reconfirms ADR-0007's
  autocomplete/correctness boundary applies to REQ-509/510's commit paths
  (`PlayerAttribute`/`PlayerOverride` only, never `PlayerNameIndex`) —
  ADR-0007 predates this pipeline and didn't name it explicitly before now.
  Also added two backlog stories implementing this feature: **S-089**
  (REQ-215 backend `PlayerSuggestion` entity/submission endpoint + frontend
  entry point/form, not yet started) and **S-090** (REQ-509/510 admin
  review/commit/manual-search backend + the new separate Suggestions admin
  screen, not yet started, depends on S-089 and ADR-0053). No code was
  written this session — documentation only. REQ/ADR refs: REQ-215,
  REQ-509, REQ-510, REQ-501, REQ-502, REQ-503, ADR-0007, ADR-0053.
- 2026-08-01 — `docs/requirements-document.md` (v1.26 → v1.27) — Flipped
  REQ-718's "UI: logout confirmation and guest-expiry copy" addendum
  (rules 4/5) from "Not yet implemented — drafted only" to "Implemented,
  2026-08-01": `GuestLogoutConfirm.tsx`/`.css`
  (`frontend/src/nav/`) gates a guest's "Log out" click behind a
  confirmation dialog before the existing, unmodified `handleLogout` fires
  (rule 4); `guestExpiryCopy.ts` (`frontend/src/lib/`) is the single
  source of the 7-day/30-day expiry copy shown in the guest banner and
  `SettingsScreen.tsx`'s guest claim section (rule 5). Added and wired in
  `68e09ed`; covered by 8 new tests (`App.test.tsx` x6,
  `SettingsScreen.test.tsx` x2) in `2e36be4` — full suite green at
  367/367 Vitest tests, clean `tsc -b`, clean `oxlint`. No change to
  `docs/architecture-document.md` (client-side-only gate in front of the
  already-documented REQ-718/ADR-0038 deletion flow — no new boundary or
  data flow) or to REQ-215/509/510, which remain correctly drafted-only
  with no code. REQ/ADR refs: REQ-718, ADR-0038.
- 2026-08-01 — `docs/requirements-document.md` (v1.26) — Drafted three new
  requirements for a not-yet-built feature (REQ-215: logged-in,
  non-guest players may submit an answer suggestion — asserted club(s) +
  nationality — after a guess is scored incorrect or a REQ-211 live lookup
  times out, visibly advertised-but-disabled for guests with a
  registration prompt; REQ-509: admin review of pending suggestions with
  an admin-triggered live Wikidata lookup and a commit path that writes
  only through the existing `PlayerAttribute`/`PlayerOverride` mechanism,
  never `PlayerNameIndex`, per ADR-0007's boundary; REQ-510: the same
  admin fetch/review/commit flow usable standalone, with no suggestion
  required). All three are explicitly flagged Tier 1/2-sized new
  pipeline work relative to `MVP-SCOPE.md` — not pulled forward by this
  change — and REQ-509 flags an open question (recorded in §7) on whether
  a new ADR should govern how these suggestions relate to REQ-503's
  existing unverified-data queue. Also added a small additive UI-only
  addendum to REQ-718 (guest account lifecycle): a confirmation prompt
  before a guest's logout-triggered account deletion, and guest-facing
  copy stating the actual 7-day/30-day expiry thresholds — both drafted
  only, no code yet, and no change to REQ-718's existing deletion
  mechanism. §7 gained one new open question (retroactive rescoring on an
  approved suggestion — REQ-215 defaults to "no," unconfirmed by the
  product owner).
- 2026-08-01 — `docs/requirements-document.md` (1.25 → 1.26),
  `docs/architecture-document.md` (0.69 → 0.70),
  `docs/implementation-document.md` (0.81 → 0.82),
  `docs/decisions/0052-pair-lookup-failure-persistence-and-club-club-query-fix.md`
  (new), `NOTES.md` — REQ-110 extended (ADR-0052): `warm-player-cache.yml`
  was reliably getting cancelled at its 90-minute CI ceiling since the
  2026-07-28 same-run-retry extension shipped, and its logs had become
  unreadable. Root cause: the same-run retry doubled every technical
  failure's cost, and nothing persisted a failure across runs, so the same
  structurally-doomed pairs (traced to
  `WikidataClient.BuildClubClubIntersectionQuery`'s plain join producing a
  real 250,000+ row WDQS response for two clubs with a large, overlapping
  squad) got retried at that doubled cost on every run forever. Fixed by
  three changes: removed the same-run retry
  (`PlayerCacheWarmingService.LookupWithSameRunRetryAsync`/
  `MaxAttemptsPerPair` deleted); added a new `PairLookupFailure` table
  (`XGArcade.Data`, migration `AddPairLookupFailure`) reachable only via
  `IPlayerStoreRepository.IsPersistentTechnicalFailureAsync`/
  `RecordTechnicalFailureAsync`/`ClearTechnicalFailureAsync`, so a pair
  failing on 2 consecutive runs is skipped without a live query on the
  third (`CacheWarmingResult.PairsSkippedPersistentFailure`), same
  invalidation surface as `ConfirmedLowMatchPair` (`StaleClubAttributeCleaner`,
  `purge-player-pool`); and `BuildClubClubIntersectionQuery` now wraps each
  club's P54 match in its own `FILTER EXISTS { }` block instead of a plain
  join, eliminating the statement-count cross product at the source.
  Separately, `WikidataClient.RunIntersectionQueryAsync`'s two per-pair
  failure logs moved from `Warning` to `Debug` (filtered out by the
  project's default `Information` log level) since they were the dominant
  contributor to the unreadable logs. See ADR-0052 for the full incident
  and reasoning, and NOTES.md's 2026-08-01 entry for the diagnosis
  narrative.
- 2026-07-28 — `docs/decisions/0051-per-gamekey-round-scheduling.md`
  renumbered from `0050-per-gamekey-round-scheduling.md` while rebasing onto
  `main`, which had independently assigned ADR-0050 to
  `docs/decisions/0050-confirmed-low-match-pair-persistence.md` (REQ-110,
  below) on a diverged branch. Every `ADR-0050` reference belonging to the
  round-scheduling decision (this file, `architecture-document.md`,
  `requirements-document.md`, `implementation-document.md`, `backlog.md`)
  was updated to `ADR-0051`; the cache-warming ADR keeps 0050 unchanged,
  having merged to `main` first. No content change to either decision, only
  the number — same renumbering precedent as ADR-0049's own entry below.
- 2026-07-28 — `docs/requirements-document.md` (1.24 → 1.25),
  `docs/architecture-document.md` (0.68 → 0.69),
  `docs/implementation-document.md` (0.80 → 0.81), `docs/backlog.md` —
  S-084 implemented (REQ-1202's round-scheduling half, ADR-0051): `"xg-path"`
  rounds are now generated on the same schedule `"xg-grid"`'s already are.
  New `IRoundSchedulingOptionsResolver`/`RoundSchedulingOptionsResolver`
  (`XGArcade.Core.Rounds`) resolves one `RoundSchedulingOptions` instance
  per `GameKey` — mirroring `IScoringStrategyResolver`'s per-`GameKey`
  shape (ADR-0040) — rather than a single directly-injected singleton; two
  instances are now registered (`"xg-grid"`, `"xg-path"`), each with its own
  configured `RoundDuration`. `RoundGenerationService
  .GenerateNextRoundIfNeededAsync` gained a leading `gameKey` parameter.
  `POST /internal/generate-round` gained an optional `gameKey` query
  parameter (default `"xg-grid"` for back-compat), dispatching narrowly to
  either the existing `GridTemplateResolver` or a new `PathTemplateResolver`
  (`XGArcade.Api.Path`) to produce the round's opaque `TemplateId`; an
  unrecognized `gameKey` returns 400 "Invalid gameKey" (a quality-gate
  follow-up correcting an initial fall-through 500). `GridSize` moved off
  `RoundSchedulingOptions` onto `Games.XGGrid.GridGenerationOptions`; new
  `Games.XGPath.PathGenerationOptions.PuzzleCount` (default 4) holds
  xG Path's own equivalent. Per the story's own flagged judgment call,
  architecture-reviewer was consulted before implementation and recommended
  extending the existing scheduled job rather than adding a second one:
  `generate-round.yml`'s single daily cron now generates both `GameKey`s'
  rounds, each with its own independent 3-attempt retry loop (ADR-0027's
  own new S-084 addendum). Test coverage:
  `RoundSchedulingOptionsResolverTests.cs` (new, per-`GameKey` resolution
  and isolation, unregistered-`GameKey` failure); extended
  `RoundGenerationServiceTests.cs` (proves REQ-301/REQ-302 hold for
  `"xg-path"` exactly as for `"xg-grid"`, with no cross-`GameKey`
  interference); extended `RoundEndpointTests.cs` (end-to-end
  `POST /internal/generate-round?gameKey=xg-path`, an omitted-`gameKey`
  regression, and the unrecognized-`gameKey` 400). Requirements doc:
  REQ-1202's status note updated from "no round-scheduling wiring yet" to
  implemented, and REQ-301's status note extended to note the same
  per-`GameKey` resolver mechanism now also serves `"xg-path"`. Architecture
  doc: COMP-11's status note and §6.1's round-generation flow description
  both updated to describe `IRoundSchedulingOptionsResolver` and the
  endpoint's `gameKey` dispatch. Implementation doc: project-structure
  entries for `XGArcade.Core`, `XGArcade.Games.XGGrid`, and
  `XGArcade.Games.XGPath` updated to name the new/moved files. Backlog:
  S-084's entry corrected from forward-looking to record what was actually
  decided and built, matching the precedent set correcting S-083's entry.
  New ADR-0051 (already added to architecture doc's ADR table; not
  duplicated here) records the four-part decision. Refs: REQ-1202,
  REQ-301, REQ-302, ADR-0051, ADR-0027.
- 2026-07-28 — `docs/decisions/0049-confirmed-low-match-pair-persistence.md`
  renumbered to `docs/decisions/0050-confirmed-low-match-pair-persistence.md`
  while resolving a merge conflict with the concurrently-merged
  `docs/decisions/0049-scoring-strategy-guess-and-max-attempts-parameter-shape.md`
  (S-083/REQ-1206, below) — both were independently assigned ADR-0049 on
  diverged branches. Every `ADR-0049` reference belonging to the
  cache-warming decision (this file, `architecture-document.md`,
  `implementation-document.md`, `PlayerCacheWarmingServiceTests.cs`) was
  updated to `ADR-0050`; the scoring-strategy ADR keeps 0049 unchanged, having
  merged to `main` first. No content change to either decision, only the
  number.
- 2026-07-28 — `docs/architecture-document.md` (0.67 → 0.68),
  `docs/implementation-document.md` (0.79 → 0.80), `docs/backlog.md` —
  synced docs to the shipped code for REQ-110's three same-day extensions
  (`docs/requirements-document.md` 1.21 → 1.23 and
  `docs/decisions/0050-confirmed-low-match-pair-persistence.md` were
  already updated earlier this session by `requirements-writer`, verified
  here against final code, not re-touched): `CacheWarmingResult
  .PairsWithTechnicalFailure`/`FailingPairs` and `IWikidataClient`/
  `IWikidataLookupService`'s new `onTechnicalFailure` callback (COMP-05,
  COMP-07); the new `WikidataQueryTimeoutTier.CacheWarming` (45s) budget
  and `PlayerCacheWarmingService.LookupWithSameRunRetryAsync` same-run
  retry (COMP-07); and the new `ConfirmedLowMatchPair` table (COMP-06,
  `IPlayerStoreRepository.IsConfirmedLowAsync`/`RecordConfirmedLowAsync`,
  ADR-0050), invalidated by `StaleClubAttributeCleaner` (REQ-111) and
  `purge-player-pool` (REQ-112/S-038). Architecture doc: updated the
  COMP-05/COMP-06/COMP-07 table rows to name the new table/enum/callback
  and confirm boundary rule 1 was respected (COMP-05 reaches
  `ConfirmedLowMatchPair` only via `IPlayerStoreRepository`); no data-flow
  diagram change needed (cache warming isn't one of §6's diagrammed
  request flows). Implementation doc: added `ConfirmedLowMatchPair` to
  the §5 data model, updated §6/§6a's Wikidata-timeout and
  CLI-verb (`warm-player-cache`/`clean-stale-club-attributes`/
  `purge-player-pool`) descriptions. Backlog: appended a short "Resolved
  same day" note to the two 2026-07-28 REQ-110 follow-up entries so they
  no longer read as still-pending. No new ADR written beyond the
  renumbering above (ADR-0050 already covers the one structural decision
  here, confirmed by `architecture-reviewer`). `docs/legal/*.md` checked and
  left unchanged — `ConfirmedLowMatchPair` holds only category-value
  names/an int/a timestamp, no user or player-identifying data, so no data
  collection/retention/third-party-sharing text is affected. REQ-110,
  ADR-0050.
- 2026-07-28 — `docs/requirements-document.md` (1.21 → 1.22),
  `docs/architecture-document.md` (0.67 → 0.68),
  `docs/implementation-document.md` (0.79 → 0.80), `docs/backlog.md`,
  `docs/decisions/0049-scoring-strategy-guess-and-max-attempts-parameter-shape.md`
  (new), `docs/decisions/0040-per-game-scoring-strategy.md` (status line) —
  S-083 implemented (REQ-1206, xG Path's clue-efficiency scoring):
  `ClueEfficiencyScoringStrategy` (`XGArcade.Core.Scoring`) computes
  `round(cluesUsed / maxAttemptsForCell * MaxPointsPerCell)` for a correct
  guess (`cluesUsed` read from the winning `Guess.AttemptCount`, no new
  column) and always reports a null `FinalUniquenessScore`; registered
  against `XGPathGameModule.XGPathGameKey` in `Program.cs`, mirroring
  `UniquenessScoringStrategy`'s `"xg-grid"` registration. Building this for
  real resolved ADR-0040's own deferred "parameter shape" follow-up: added
  ADR-0049, which changes `IScoringStrategy.ScoreCorrectGuess`'s signature
  to take the whole `Guess` being scored plus a plain `int
  maxAttemptsForCell` (resolved once per cell, not per guess, by
  `ScoreLockingService` via ADR-0041's existing `IGameModule
  .GetMaxAttemptsForCellAsync`) rather than giving `IScoringStrategy` a
  direct dependency on `IGameModule`; `UniquenessScoringStrategy` was
  adapted to the new signature with no formula/behavior change. ADR-0040's
  own status line now cross-references ADR-0049 as closing its follow-up,
  same precedent as ADR-0016/ADR-0048. Architecture doc: COMP-11's status
  note and COMP-04's S-083 status note both updated to reflect
  `ClueEfficiencyScoringStrategy` being registered and implemented (no
  longer stubbed); new ADR-0049 row added to the ADR table. Requirements
  doc: REQ-1206's status changed from "Not started (design only)" to
  "Implemented," with an implementation note naming the strategy class,
  how `cluesUsed`/`maxCluesForThisPuzzle` are sourced, and the covering
  `REQ1206_...`-named tests. Implementation doc: the stale "No
  IScoringStrategy registration yet — S-083" project-structure note
  corrected, and a new S-083 correction note added alongside the existing
  S-076 `IScoringStrategy` note describing the signature change. Backlog:
  S-083's entry corrected to describe the actual parameter shape
  (`Guess` + per-cell `maxAttemptsForCell`, resolved once per cell) rather
  than leaving it undescribed. Refs: REQ-1206, ADR-0040, ADR-0049.
- 2026-07-27 — `docs/requirements-document.md` (1.19 → 1.21),
  `docs/architecture-document.md` (0.66 → 0.67),
  `docs/implementation-document.md` (0.78 → 0.79), `docs/backlog.md`,
  `docs/decisions/0048-per-game-display-read-endpoints-confirmed.md` (new),
  `docs/decisions/0016-display-reads-bypass-igamemodule.md` (status line) —
  S-082 implemented (REQ-1203 clue reveal, REQ-1204 guess correctness,
  REQ-1205 fixed 7-attempt cap) end to end: `PathClueSequenceBuilder`/
  `PathClueTurn`, the new `GET /path/current` read endpoint
  (`XGArcade.Api.Path.PathEndpoints`), and
  `XGPathGameModule.ScoreSubmissionAsync`/`GetMaxAttemptsForCellAsync`
  (no longer `NotImplementedException`). REQ-1207 (Wikidata P413/P569
  sourcing for `Player.Position`/`Player.BirthYear`) was drafted and folded
  into this same story mid-session, after REQ-1203's position/nationality/
  age clues turned out to depend on `Player` fields that didn't exist yet.
  Quality-gate follow-up: `GuessScoringException` (`Games.XGGrid`) and
  `PathScoringException` (`Games.XGPath`) now both derive from a new shared
  `XGArcade.Core.Games.GameEntityNotFoundException`, mirroring
  `LiveLookupUnavailableException`'s existing cross-boundary precedent, so
  the game-agnostic `GuessEndpoints` no longer needs compile-time knowledge
  of either game's own exception type; also closed a test coverage gap and
  fixed a stale test comment found during the same pass. Guess submission
  added **no new write endpoint** — xG Path guesses reuse the existing
  generic `POST /rounds/{roundId}/cells/{cellId}/guesses`. Added ADR-0048,
  confirming ADR-0016's direct-repository-read pattern (`GET /path/current`
  reading `PathInstance`/`PathPuzzle` directly, same as `GET /rounds/current`
  reads `GridInstance`/`GridCell`) as the platform's permanent shape for
  read-only display endpoints rather than a Tier-0 stopgap awaiting a
  generic `IGameModule` read method; ADR-0016's own status line now
  cross-references it. Architecture doc: COMP-11's status note updated to
  reflect both previously-stubbed methods now being implemented, a new
  §6.2b data-flow entry covers `GET /path/current` and the reused guess-
  submission path, and xG Path's deliberate lack of a fuzzy-matching stage/
  REQ-209-style disambiguation prompt is recorded as a reviewed, confirmed
  scope decision (not a gap to "fix" later). Requirements doc: REQ-1203's
  Test level note now reads "Unit, API" (new `PathEndpointTests`); REQ-1202's
  stale "no API route exposes this game yet" note corrected without
  implying REQ-1202 itself has API-level coverage (it doesn't — that
  endpoint is REQ-1203's); REQ-1201's eligibility status note corrected,
  since `Player` no longer has "no BirthYear field at all" now that
  REQ-1207 added it (the eligibility check still doesn't read it — same
  correction applied to `docs/backlog.md`'s matching S-081 note). Backlog:
  S-082's entry now names REQ-1207 and why it was folded in.
  REQ-1203/1204/1205 status notes moved from "Not started (design only)" to
  "Implemented." Refs: REQ-1203, REQ-1204, REQ-1205, REQ-1207, ADR-0016,
  ADR-0048.
- 2026-07-27 — `docs/requirements-document.md` (1.18 → 1.19),
  `docs/architecture-document.md` (0.65 → 0.66),
  `docs/decisions/0047-xg-path-seeded-club-appearance-threshold.md` (new,
  renumbered from a draft ADR-0046 during merge — ADR-0046 was already
  claimed by the live-lookup-timeout ADR below, landed on `main` first),
  `docs/backlog.md`, `backend/src/XGArcade.Games.XGPath/
  XGPathGameModule.cs`, `backend/tests/XGArcade.Games.XGPath.Tests/
  XGPathGameModuleTests.cs` — tightened REQ-1201's xG Path eligibility: a
  candidate's seeded-club stint now also needs ≥20 recorded appearances
  there (or an unknown count) to count, closing the gap where a single
  loan/fringe appearance at a big club was enough to qualify an otherwise
  obscure player as a target — ADR-0047. `IsEligible`'s seeded-club check
  now also filters on `PlayerCareerStint.AppearanceCount`; 3 new REQ1201-
  named tests cover below/at/unknown appearance count. `dotnet test` run
  locally: 16/16 passing (13 existing + 3 new).
- 2026-07-27 — `docs/requirements-document.md` (1.17 → 1.18),
  `docs/architecture-document.md` (0.64 → 0.65), `docs/decisions/0041-
  per-cell-attempt-cap.md`, `docs/backlog.md` — revised REQ-1203's xG Path
  clue-reveal mechanic per a product decision: club stints are no longer
  capped at 5 and revealed one-per-clue; every documented stint is now
  revealed, split across exactly 3 club-reveal turns (`N` divided into 3
  as evenly as possible, smallest turn first — e.g. `N=4` → 1-1-2, `N=10`
  → 3-3-4, `N=11` → 3-4-4), each club still carrying its appearance count
  when known. The bundled year-range clue and the fixed
  position/nationality/age tail are unchanged. Net effect: a puzzle's
  total clue count (REQ-1205/1206) becomes a fixed **7** for every xG Path
  puzzle instead of the earlier `min(club stint count, 5) + 4`, which
  varied by target player — updated the stale formula references in
  ADR-0041 and `architecture-document.md` §COMP-04 to match (the ADR's
  actual decision, per-cell resolution through `IGameModule`, is
  unaffected). No code exists for REQ-1203/1205/1206 yet (still "design
  only," S-082/S-083), so this is a pure documentation change.
- 2026-07-27 — `docs/implementation-document.md` (0.77 → 0.78),
  `docs/design-document.md` (0.55 → 0.56) — while merging the two entries
  above against `main`, found two more stale references to the old
  "club-stint clues capped at 5" design that the REQ-1203 revision above
  had missed: `implementation-document.md`'s `IGameModule` interface
  comment ("xG Path's varies per puzzle") and `design-document.md`'s
  SCREEN-10 spec (an explicit "capped at 5" bullet, written before the
  revision). Corrected both to describe the current design (a fixed
  `MinAppearancesAtSeededClub`-independent 7-clue total; all stints shown
  across 3 grouped reveal turns) — no behavior change, both docs were
  already stale relative to `requirements-document.md`.
- 2026-07-27 — `docs/requirements-document.md` (1.16 → 1.17),
  `docs/decisions/0046-live-lookup-timeout-exception-signal.md` — follow-up
  to #123: real usage showed guessing "Clarence Seedorf" for Ajax × AC Milan
  consistently returned `LiveLookupUnavailable` (503) rather than ever
  resolving, since `WikidataClient`'s 15s budget (REQ-103's own, reused
  unmodified by #123's fix) doesn't cover the up-to-27s WDQS latency
  ADR-0011 already documented for this club-club query shape. Added a
  second, wider budget (`guessTimeFallbackQueryTimeout`, 28s) used only when
  `throwOnTimeout` is set (i.e. only `WikidataLookupOrigin.GuessTimeFallback`)
  — REQ-103/grid generation's 15s budget is completely unaffected. New
  status notes on REQ-211 and ADR-0046 explain why this doesn't reopen
  ADR-0046's own rejected "increase the timeout instead" alternative (that
  alternative was rejected in the context of the fallback firing on every
  unresolved guess; REQ-211's `PlayerNameIndex` gate, landed in the same
  PR, already narrowed that to only real, indexed players before this
  follow-up widened the budget further).
- 2026-07-27 — `docs/backlog.md`, `docs/architecture-document.md`
  (0.62 → 0.63), `docs/CHANGELOG.md` — doc-sync pass over S-081's diff
  (REQ-1201/1202, ADR-0045). `docs/backlog.md`'s S-081 Accept-criteria
  sentence previously read as if a test exists for "a candidate outside
  REQ-112's pool" — no such fixture is possible (`Player` has no
  `BirthYear`/`Gender` field), so the wording now matches what was
  actually built: that criterion is satisfied by construction and
  confirmed by inspection, same as `XGPathGameModuleTests`'s own class doc
  comment and REQ-1201's status note already say (flagged independently by
  `architecture-reviewer` and `quality-architect`). Corrected this same
  entry's own prior wording ("REQ-1201's four independent rejection
  rules") to say three rules covered by real fixtures (one, undeterminable
  order, tested via two fixtures) plus the fourth confirmed by inspection.
  `docs/architecture-document.md`'s §10 ADR index table was missing rows
  for ADR-0040 through ADR-0044 as well as the new ADR-0045 (a pre-existing
  gap predating this branch, not introduced by S-081) — added all five so
  ADR-0045 is actually discoverable from the index. Verified
  `docs/requirements-document.md`, `docs/implementation-document.md`, and
  ADR-0045 itself were otherwise accurate against the code; no change to
  `docs/legal/*.md` needed (no data collection/retention/third-party
  change in this story).
- 2026-07-27 — `docs/requirements-document.md` (1.14 → 1.15),
  `docs/architecture-document.md` (0.61 → 0.62),
  `docs/implementation-document.md` (0.76 → 0.77), `docs/decisions/0045-xg-
  path-puzzle-generation-model-and-eligibility.md` (new) — implemented
  S-081 (REQ-1201/1202): `XGPathGameModule.GenerateInstanceAsync`/
  `GetCellIdsAsync` (`XGArcade.Games.XGPath`) now do real work instead of
  throwing `NotImplementedException`. New entities `PathTemplate`/
  `PathInstance`/`PathPuzzle` (`XGArcade.Data`, migration
  `20260727130000_AddPathInstance`) mirror `GridTemplate`/`GridInstance`/
  `GridCell`'s shape; unlike `GridCell`, `PathPuzzle.TargetPlayerId` is a
  real FK into `Player` — see ADR-0045 for why this doesn't cross
  ADR-0003's Core/game boundary. New repository `IPathInstanceRepository`/
  `PathInstanceRepository` (COMP-11's own persistence) and a new
  `IPlayerStoreRepository.GetAllCareerStintsByPlayerAsync` bulk read
  (COMP-06) — the only new cross-component call, same "tolerate a
  full-table-scale read at Tier 0's player-pool size" precedent
  `GetPlayersMissingPhotoAsync` already sets. REQ-1201's eligibility check
  reads two of its acceptance-criteria phrases in specific, documented ways
  (ADR-0045): "≥3 distinct stints" as ≥3 stint *rows*, not 3 distinct
  clubs; "chronological order determinable from start/end dates" as
  rejecting any candidate with two stints sharing an identical
  `(StartYear, EndYear)` pair (including two simultaneously "ongoing"
  stints). REQ-112 pool membership is verified by construction (`Player`
  has no `BirthYear`/`Gender` field), not a runtime check — same precedent
  `GridGameModule` already established. `ScoreSubmissionAsync`/
  `GetMaxAttemptsForCellAsync` (REQ-1204/1205) are untouched, still
  throwing `NotImplementedException` — that's S-082. New
  `XGPathGameModuleTests` (NUnit, real InMemory-backed repositories, no
  fakes, same pattern as `GridGameModuleTests`) covers three of REQ-1201's
  four rejection criteria with real fixtures (fewer than 3 stints; an
  undeterminable stint order, including the two-simultaneously-"ongoing"-
  stints edge case; no stint at a seeded club) plus a positive "3 stints at
  the same club is still eligible" control. The fourth criterion — a
  candidate outside REQ-112's pool — has no corresponding fixture: `Player`
  has no `BirthYear`/`Gender` field to construct a violation against, so
  this is confirmed by inspection instead, per the class-level doc comment
  on `XGPathGameModuleTests` (same scope-note precedent S-079's own
  CHANGELOG entry above used). Also covers REQ-1202's exactly-N/
  insufficient-pool/unknown-template/cell-id-lookup behavior.
- 2026-07-27 — `docs/requirements-document.md` (1.15 → 1.16),
  `docs/architecture-document.md` (0.63 → 0.64),
  `docs/implementation-document.md` (0.76 → 0.77),
  `docs/design-document.md` (0.54 → 0.55, previously landed by the frontend
  half of this same bundle — folded in here rather than left as a separate
  entry), `docs/decisions/0046-live-lookup-timeout-exception-signal.md`
  (new) — doc sync for the `claude/xg-grid-perf-search-r0q708` bug-fix
  bundle (commits f5d10da/f6d06e3), which fixed slow/unreliable guessing,
  stale name-index words, and an autocomplete answer leak:
  - **REQ-211** (requirements-document.md): the guess-time live-lookup
    fallback now gates on a real `IPlayerNameIndexRepository` match before
    calling Wikidata — closing a stale "Tier 1, not built" gap in this
    REQ's own status text (`PlayerNameIndex` has existed since S-032,
    2026-07-17; the un-gated trigger was the dominant cost of the reported
    "guessing is slow" symptom, not a deliberate simplification). Also adds
    a new `GuessSubmissionOutcome.LiveLookupUnavailable` branch (HTTP 503,
    no `Guess` row written, no REQ-210 attempt consumed) so a Wikidata
    timeout during this fallback is distinguishable from a confirmed
    incorrect guess (previously conflated — the reported "guessed Clarence
    Seedorf, got a fetch error, retried, scored incorrect" symptom). New
    acceptance-criterion bullet added. See ADR-0046.
  - **REQ-210** (requirements-document.md): status note cross-referencing
    the new `LiveLookupUnavailable` branch as a fourth "doesn't consume an
    attempt" case, alongside REQ-209's existing disambiguation branch.
  - **REQ-207** (requirements-document.md): 2026-07-27 correction recording
    that the shipped `PlayerAutocompleteSuggestion` DTO leaked
    `Nationality` — a real violation of this REQ's "implies nothing about
    correctness" criterion for nationality-based categories — now removed
    from both the backend DTO and the frontend suggestion type/caption
    (`GuessInput.tsx`, already fixed in this bundle's frontend half,
    f5d10da); `BirthYear` stays, since no xG Grid category is
    birth-year-based.
  - **REQ-208** (requirements-document.md): addendum recording that
    pre-2026-07-26-migration `PlayerNameIndex` rows had no
    `PlayerNameIndexWord` rows (so surname-only search still failed for
    them, e.g. "Seedorf") — fixed by a new, idempotent
    `PlayerNameIndexWordBackfiller` wired into `migrate-and-seed`.
  - **New ADR-0046**: the structural decision behind the
    `LiveLookupUnavailableException`/`GuessSubmissionOutcome
    .LiveLookupUnavailable`/503 signal — a new, narrow exception-based
    cross-boundary contract between `Games.XGGrid` (COMP-05) and
    `Core.Scoring` (COMP-04), kept inside `Core.Games` per ADR-0003, so a
    live-lookup infra failure is never conflated with a confirmed-incorrect
    guess. Covers the alternatives considered (result-type/nullable signal,
    a longer timeout, accepting the false-negative rate) and why the
    exception-based signal was chosen instead.
  - **architecture-document.md** §6.2: corrected the REQ-211 guess-time
    fallback's trigger-condition description (now matches the diagram's
    full shape, per the fix above) and added a note on the new
    exception-based signal crossing the `Games.XGGrid` → `Core.Scoring`
    boundary.
  - **implementation-document.md**: fixed two now-stale references to
    `WikidataLookupService.GetOrCreatePlayerAsync` (replaced by
    `IPlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync`,
    called from the newly-batched `PersistMatchesAsync` — root cause #2 of
    this bundle, one `SaveChangesAsync` per batch instead of per player,
    per `docs/coding-guidelines.md`'s own rule), and corrected the
    intersection-queries'-never-throw claim to note the new opt-in
    `throwOnTimeout` parameter (REQ-211/ADR-0046 only; REQ-103's default
    behavior is unchanged).
  - Not touched: `docs/design-document.md` was already updated correctly by
    this bundle's frontend commit (f5d10da) — reviewed and left as-is,
    consolidated into this entry rather than duplicated as its own.
- 2026-07-27 — `docs/implementation-document.md` (0.75 → 0.76) — S-080's
  §4 project-structure list gained the two new `XGArcade.Games.XGPath`/
  `XGArcade.Games.XGPath.Tests` folders (a gap the two S-080 code reviews
  below didn't catch, found by `doc-sync`) — no other content change.
- 2026-07-27 — `docs/architecture-document.md` (0.60 → 0.61) — S-080
  scaffolded `XGArcade.Games.XGPath`: a new `XGArcade.Games.XGPath` project
  (plus `XGArcade.Games.XGPath.Tests`) implementing `IGameModule`
  (`GameKey = "xg-path"`), added to `backend/XGArcade.sln` and registered
  in `Program.cs` (`AddScoped<IGameModule, XGPathGameModule>()`) alongside
  the existing `GridGameModule` registration, so `IGameModuleResolver`
  now resolves two implementations by `GameKey`. Every `IGameModule`
  method (`GenerateInstanceAsync`, `ScoreSubmissionAsync`,
  `GetCellIdsAsync`, `GetMaxAttemptsForCellAsync`) throws
  `NotImplementedException` — no puzzle-generation or scoring logic yet;
  that's S-081+. No `IScoringStrategy` is registered for `"xg-path"` and
  no route exposes this game. This is the second `IGameModule`
  implementation to exist, so it's also the first real exercise of
  ADR-0003's stated follow-up ("when a second game module is actually
  built, use it to verify this pattern holds") — it held: `Core.Rounds`
  needed no change. The one incidental fix this forced:
  `InternalGridEndpoints.cs`'s `/internal/grid/generate` endpoint took a
  raw `IGameModule` by DI, which is only safe with exactly one
  implementation registered — with two, ASP.NET Core resolves whichever
  was registered last, an implementation detail rather than a documented
  guarantee, silently pointing this xG-Grid-only debug endpoint at the
  wrong module. Switched to `IGameModuleResolver.Resolve(GridGameModule.XGGridGameKey)`,
  matching the pattern every other caller already uses. No new ADR: this
  is a straight application of ADR-0002 (modular monolith, one project per
  game) and ADR-0003 (generic `GameKey`/`GameInstanceId` reference), not a
  new structural decision. `docs/requirements-document.md` REQ-1201–REQ-1206
  are unchanged (still "Not started (design only)") — this story is
  scaffold-only, no gameplay behavior was implemented.
- 2026-07-27 — `docs/architecture-document.md` (0.59 → 0.60),
  `docs/implementation-document.md` (0.74 → 0.75) — implemented S-079
  (ADR-0042, already accepted; no change to the ADR itself): a new
  `PlayerCareerStint` entity (COMP-06, alongside `PlayerAttribute`/
  `PlayerAlias`/`PlayerOverride` in `XGArcade.Data`), a migration, and two
  new `IPlayerStoreRepository` methods (`GetCareerStintsAsync`/
  `AddCareerStintsAsync`). `WikidataClient`'s shared SPARQL query-building
  helper now also captures P580/P582/P1350 statement qualifiers
  (start year/end year/appearance count) already present on the existing
  P54 club-membership statement — no new SPARQL query shape, no new
  external call. `SequenceOrder` is resolved at write time across a
  player's full stint set (existing rows plus newly discovered ones), so a
  stint found later that chronologically precedes existing ones re-numbers
  the whole sequence; `AppearanceCount` is `null`, never `0`, when
  Wikidata's P1350 qualifier isn't present. Persisted only by
  `WikidataLookupService.LookupAndPersistAsync` (the country/nationality x
  club path) — a deliberate scope limit, not an oversight: the other three
  `Lookup*Async` callers (club-club, trophy-country, trophy-club)
  deliberately do not populate this table yet, extending that is a
  separate future decision. No consumer yet — S-081 is the first reader;
  this story is backend data-model plumbing only. `requirements-document.md`
  §4.12 (REQ-1201-REQ-1206) is unchanged — those describe xG Path's
  gameplay behavior, none of which this story implements, so their
  "Not started (design only)" status is still accurate.
- 2026-07-27 — `docs/architecture-document.md` (0.58 → 0.59),
  `docs/requirements-document.md` (REQ-410, 1.13 → 1.14),
  `docs/implementation-document.md` (0.73 → 0.74) — implemented
  S-078 (ADR-0043): `ILeaderboardService.GetGlobalLeaderboardAsync` and
  `IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync` gained a
  required `gameKey` parameter, filtering the latter's existing
  `Guess`-`Round` join with `round.GameKey == gameKey` — no schema change,
  no new join, and REQ-409's median/5-round-qualification formula itself
  is untouched. `LeaderboardEndpoints` passes `GridGameModule.XGGridGameKey`
  explicitly, same convention the other three `ILeaderboardService` scopes
  already used, bringing all four into line. Every existing REQ-409-named
  test now supplies the shared `GameKey` constant explicitly (behavior for
  xG Grid, the only shipped game, is unchanged), and four new REQ-410-named
  tests in `LeaderboardServiceTests.cs` seed a second, real `"xg-path"`
  `GameKey` and confirm qualifying rounds, medians, and the 5-round
  minimum are computed independently per game and never blended, exercised
  through the real EF InMemory `Guess`-`Round` join. Not included in this
  pass: an API-level cross-game test (blocked on `LeaderboardEndpoints` not
  yet accepting a `gameKey` query parameter) and the frontend game-switcher
  (S-087/SCREEN-03) — both tracked as separate
  follow-ups.
- 2026-07-26 — `docs/architecture-document.md` (0.57 → 0.58),
  `docs/implementation-document.md` (0.72 → 0.73),
  `docs/requirements-document.md` (REQ-210, 1.12 → 1.13) — implemented
  S-077 (ADR-0041): `IGameModule` gained
  `GetMaxAttemptsForCellAsync(instanceId, cellId)`, resolved through
  `IGameModuleResolver` the same way `GetCellIdsAsync` already is. xG
  Grid's `GridGameModule` implementation returns `2` unconditionally,
  identical to the old `GuessRules.MaxAttemptsPerCell` global constant it
  replaces, which is now deleted outright (not left as dead code, per
  ADR-0041's own follow-up). `GuessSubmissionService` (REQ-210's lock/cap
  check), `LiveRoundContributionService`, and `RoundEndpoints` all now
  read the cap through the module instead of the deleted constant. Pure
  extraction — no REQ-210 acceptance criteria changed; new tests prove no
  call site hardcodes `2` (a module reporting a non-standard cap is
  actually honored) and that the cap is resolved exactly once per
  submission attempt.
- 2026-07-26 — `docs/architecture-document.md` (0.56 → 0.57),
  `docs/implementation-document.md` (0.71 → 0.72) — implemented S-076
  (ADR-0040): `Core.Scoring` now resolves an `IScoringStrategy` per
  `Round.GameKey` through a new `IScoringStrategyResolver`, mirroring
  `IGameModuleResolver`'s resolution shape exactly. xG Grid's existing
  REQ-204/205 formula is extracted unchanged into
  `UniquenessScoringStrategy` (`GameKey` supplied by `Program.cs`, never
  hardcoded in `XGArcade.Core`, per ADR-0003); `ScoreLockingService` calls
  the resolved strategy instead of `UniquenessCalculator`/`ScoringRules`
  directly for a correct guess. `MaterializeUnansweredCellsAsync`'s
  unanswered-cell penalty is untouched. Pure extraction — no REQ-204/205
  acceptance criteria changed; new tests cover
  `IScoringStrategyResolver`'s resolve/throw behavior.
- 2026-07-26 — `docs/backlog.md` — added Epic 6 (xG Path, second game),
  S-076 through S-088: three shared-infrastructure refactors ordered
  first (S-076 scoring-strategy pluggability/ADR-0040, S-077 per-cell
  attempt cap/ADR-0041, S-078 per-game leaderboard scoping/ADR-0043/
  REQ-410 — each a no-behavior-change extraction for xG Grid), then
  xG Path's own data model (S-079, ADR-0042), module scaffold (S-080),
  generation/clue-reveal/scoring (S-081-083), round scheduling (S-084),
  and three frontend stories (S-085/086/087) plus E2E coverage (S-088).
  Turns the design-only REQ-1201-1206/REQ-410 and ADR-0040-0043 into a
  concrete, dependency-ordered build sequence — no code changed.
- 2026-07-26 — `docs/design-document.md` (0.53 → 0.54) — added SCREEN-09
  (game select, multi-tile — resolves the §7 open question flagged since
  S-021) and SCREEN-10 (xG Path puzzle/clue-reveal screen — design only,
  no code yet, `requirements-document.md` REQ-1201-1206), validated
  against two working prototypes (growing-timeline chosen over a
  spotlight-stepper alternative). Updated SCREEN-03 (Leaderboard) with a
  game-switcher note for the All-time scope (ADR-0043/REQ-410) — the
  only scope not already `gameKey`-scoped. No new colors, typefaces, or
  animation families introduced; both new screens reuse existing tokens
  and the badge-dock/shake motion vocabulary.
- 2026-07-26 — `docs/decisions/0044-player-name-index-per-word-prefix-matching.md`
  (Consequences section corrected), `docs/implementation-document.md`
  (0.70 → 0.71) — quality-gate correction to the REQ-208/ADR-0044 fix:
  `PlayerNameIndexRepository.SearchByPrefixAsync`'s two candidate-id
  branches (`NormalizedName` and `PlayerNameIndexWord.Word` `StartsWith`
  scans) were each unbounded before their union, which could pull a large
  candidate-id list into memory for a short prefix at scale — the exact
  thing ADR-0044 was meant to avoid. Each branch now applies its own
  `OrderBy(...).Take(limit)` before the union; ADR-0044's Consequences
  section now documents this as a real gap the original write-up missed,
  not just "two round trips." Also fixed `PlayerNameIndexWord`'s doc code
  sample (missing `required` on `Word`, drifted from the real entity).
- 2026-07-26 — `docs/requirements-document.md` (REQ-208, 1.11 → 1.12),
  `docs/architecture-document.md` (0.55 → 0.56),
  `docs/implementation-document.md` (0.69 → 0.70),
  `docs/decisions/0044-player-name-index-per-word-prefix-matching.md`
  (new), `infra/scripts/lib/game-data-tables.sh` — implemented REQ-208's
  2026-07-26 correction: `PlayerNameIndexRepository.SearchByPrefixAsync`
  now also matches a query as a prefix of any individual word within a
  player's normalized name (e.g. a surname-only query), not just the whole
  name, via a new `PlayerNameIndexWord` child table/migration
  (`20260726120000_AddPlayerNameIndexWord`) rather than a leading-wildcard
  scan, to stay index-backed at `PlayerNameIndex`'s bulk-imported scale. See
  ADR-0044 for why a per-word table was chosen over `pg_trgm`. Added the new
  table to the prod/dev sync allowlist alongside `PlayerNameIndexEntries` so
  the two never drift apart.
- 2026-07-26 — `docs/requirements-document.md` (REQ-208, 1.10 → 1.11) —
  corrected REQ-208's acceptance criteria: `PlayerNameIndexRepository
  .SearchByPrefixAsync` only ever matched a query against the prefix of a
  player's *whole* normalized name, so a surname-only autocomplete query
  (e.g. "Ibrahimovic") returned no suggestions. Added an acceptance
  criterion requiring the query to also match as a prefix of any
  individual word within the normalized name, additive to the existing
  whole-name-prefix behavior. Diacritic-insensitive matching is unaffected
  and already correct. Documentation only — the corresponding code fix in
  `backend/src/XGArcade.Data/Repositories/PlayerNameIndexRepository.cs` is
  tracked separately.
- 2026-07-26 — `docs/decisions/0043-global-leaderboard-scoped-per-game.md`
  (new), `docs/architecture-document.md` (0.54 → 0.55),
  `docs/requirements-document.md` (§4.4, REQ-410 new, 1.09 → 1.10) —
  planning xG Path's platform integration found the Global League's
  all-time ranking (REQ-409) was the one leaderboard scope with no
  per-`GameKey` filter (the other three already had one). ADR-0043
  documents the fix: `GetGlobalLeaderboardAsync`/
  `GetPerRoundFinalPointsByUserIdsAsync` gain a required `gameKey`
  parameter (no schema change). Added REQ-410 (Status: Not started, design
  only) and forward-pointing status notes on REQ-401/404/409. Updated
  `architecture-document.md`'s COMP-02 status accordingly.
- 2026-07-26 — `docs/decisions/0040-per-game-scoring-strategy.md`,
  `docs/decisions/0041-per-cell-attempt-cap.md`,
  `docs/decisions/0042-player-career-stint-data-model.md`,
  `docs/architecture-document.md` (0.53 → 0.54) — initial design pass for
  xG Path, the platform's second game (guess a player from a
  progressively-revealed career path). Three new ADRs, all Accepted:
  ADR-0040 makes `Core.Scoring` resolve a scoring strategy per `GameKey`
  instead of hardcoding xG Grid's uniqueness formula for every game;
  ADR-0041 makes the guess-attempt cap per-cell (resolved via
  `IGameModule`) instead of the shared `GuessRules.MaxAttemptsPerCell`
  constant; ADR-0042 adds a new `PlayerCareerStint` entity (COMP-06) for
  ordered/dated/appearance-count career data, populated from Wikidata `P54`
  qualifiers the existing query already returns but currently discards.
  Added COMP-11 (Games.XGPath, design only, no code yet) to
  `architecture-document.md`'s component table, updated COMP-06's entry for
  `PlayerCareerStint`, and added a COMP-04 status note tying the two
  scoring/attempt-cap ADRs together — this note also resolves the open
  question on `Guess.CellId` recorded 2026-07-04 below ("revisit when a
  second game is built"): there is no actual EF Core foreign key from
  `Guess` to `GridCell`, so no schema change is needed for a second game to
  use the same column. See the entry directly below for the companion
  `docs/requirements-document.md` REQ-1201–REQ-1206 addition.
- 2026-07-26 — `docs/requirements-document.md` (§4.12, REQ-1201–REQ-1206,
  new; 1.08 → 1.09) — added xG Path's design-only requirements (target
  player eligibility, round structure, clue reveal order, guess
  correctness, per-puzzle attempt cap, clue-efficiency scoring), all
  marked `Status: Not started (design only)` — no xG Path code exists yet.
  References ADR-0040 (per-game scoring strategy) and ADR-0041 (per-cell
  attempt cap), both already Accepted. Does not touch
  `architecture-document.md` or `implementation-document.md` — those
  updates are tracked separately in the same design session.
- 2026-07-26 — `docs/design-document.md` — extended the "Brand mark" note a
  third time: dropped the ball accent added earlier the same day, per
  direct feedback ("too much", didn't look good) — removed outright, not
  kept as an option. `Logo`/`LogoMark`/`favicon.svg` are back to plain
  `x`/`G`/`Arcade` text (still two-tone in `Logo`) with no ball glyph.
- 2026-07-26 — `docs/design-document.md` — extended the "Brand mark" note a
  second time: adopted user-supplied inspiration selectively (two-tone
  "xG" letters, a flat ball glyph) while explicitly declining the parts
  that conflict with §1/§2's flat, gradient-free direction (motion swoosh,
  dissolving pixels, gradient shading). `Logo` is now badge-less
  (`accent-green`/`accent-gold-text` directly on `bg-base`); `LogoMark`
  (favicon/icon use) keeps its white-on-green treatment since raw
  `accent-gold` doesn't read reliably against `accent-green` at icon
  sizes. Recorded two accessible-name gotchas hit and fixed along the way
  (child-element name-joining, flex ignoring whitespace-only text nodes
  for layout).
- 2026-07-26 — `docs/design-document.md` — extended the "Brand mark" note:
  `Logo` moved from `frontend/src/splash/` to `frontend/src/components/` and
  now also replaces the header's plain-text "xG Arcade" title in `App.tsx`
  (both the button and `<h1>` variant), sized down for the header line. Same
  mark, same accessible-name mechanism, no test changes needed.
- 2026-07-26 — `docs/design-document.md` — added a "Brand mark" note
  documenting the new `Logo`/`LogoMark` icon that replaces the plain "xG
  Arcade" text on `SplashScreen`, and the matching `favicon.svg`. Direct
  follow-up to REQ-719, which explicitly shipped without a logo asset.
  Revised same day: the first version used a 2x2-grid glyph, replaced
  outright with an "xG" monogram after direct feedback asked for xG itself
  to be the mark's visual center. No new tokens; reuses `accent-green`
  (fixed across themes) plus a literal white for the monogram text, same
  reasoning as `overlay-scrim`'s theme-invariant foreground pairings.
- 2026-07-25 — `frontend/src/App.test.tsx` — `test-writer` closed a gap
  quality-architect found in the REQ-720/REQ-721 review: `grid`/
  `leaderboard`/`admin` hashes had no navigate-sets-hash or
  reload-restores-screen assertion (only `game-select`/`settings`/
  `leagues` did). All six `Screen` values now have both. 353 → 359
  Vitest tests.
- 2026-07-25 — `frontend/src/App.tsx`, `frontend/src/nav/HeaderNav.tsx`
  (+`.css`/`.test.tsx`), `frontend/src/App.test.tsx`,
  `frontend/tests/unit/App.test.tsx`, `frontend/tests/unit/setup.ts`,
  `frontend/tests/e2e/header-nav.spec.ts`,
  `frontend/tests/e2e/url-routing.spec.ts` (new),
  `docs/design-document.md` (SCREEN-07 rewritten for the new nested
  "Games" disclosure and a pre-existing "Leagues" nav-entry doc gap
  fixed in the same edit, 0.49 → 0.50) — REQ-720/REQ-721 implemented
  by `ui-implementer`, per ADR-0039. `architecture-reviewer`: pass, no
  drift, ADR-0039 fully complied with (no router dependency, no
  popstate/hashchange listener). `quality-architect`: pass; one
  medium finding (REQ-721's `grid`/`leaderboard`/`admin` hashes have no
  test assertion, only `game-select`/`settings`/`leagues` do) routed to
  `test-writer`.
- 2026-07-25 — `docs/decisions/0039-hash-based-hand-rolled-client-routing.md`
  (new), `docs/architecture-document.md` (§10 ADR table, 0.52 → 0.53) —
  ADR-0039: hash-based URLs (`#/grid`), hand-rolled (no `react-router`),
  for REQ-721's URL-reflected navigation. Chosen over path-based routing
  because the frontend's Azure Static Web App host has no
  `staticwebapp.config.json`/SPA-fallback configured, and Playwright E2E
  runs against the Vite dev server (which has its own fallback baked in)
  so a path-based bug would only surface against the real deployed host —
  a known recurring failure class for this project (region restriction,
  `GHCR_TOKEN` expiry, Npgsql format, per `infra/README.md`/`NOTES.md`).
  Hand-rolled chosen over `react-router` since REQ-721 explicitly excludes
  browser back/forward (the library's main value-add) and `Screen` is a
  flat 6-value union with no nesting/params. Also fixed a pre-existing gap
  in the same table: ADR-0038 (guest account cleanup, added 2026-07-25 in
  an earlier session) was missing its row entirely.
- 2026-07-25 — `docs/requirements-document.md` (REQ-720, REQ-721, new) —
  REQ-720: header nav gains a "Games" disclosure listing available games,
  a documented deliberate reversal of S-029's nav simplification now that
  a second game is actually planned. REQ-721: current screen reflected in
  the URL so a reload restores it; implementation left to ADR-0039;
  browser back/forward explicitly out of scope; resolves how URL
  restoration interacts with REQ-303 (post-login always lands on
  game-select, unchanged) and REQ-719 (splash gate never bypassed by a
  URL). Frontmatter version 1.07 → 1.08.
- 2026-07-25 — `docs/requirements-document.md` (REQ-719, new),
  `docs/design-document.md` (0.48 → 0.49) — added REQ-719: an
  unauthenticated splash/landing screen shown before `AuthScreen` on
  every unauthenticated render (first visit, reload, or return from
  logout/account-deletion/a failed silent refresh) — no persisted
  "seen it" flag, so it's never skipped after the first time. Built as
  `frontend/src/splash/SplashScreen.tsx`, token-only styling (no new
  color/typeface/animation, no logo/brand-mark image — that's tracked
  separately), single CTA into the existing login/signup form. Does not
  change REQ-303/S-021's post-login → game-select routing.
  `docs/design-document.md` gets a new §7 open-item entry flagging
  `SplashScreen` alongside `AuthScreen`/`GameSelectScreen` as another
  built-but-unspec'd screen. `architecture-reviewer`: pass, no ADR (pure
  frontend render-state addition, no new component boundary or data
  flow). `quality-architect`: pass; one stale comment in `App.tsx`
  fixed to say "splash screen" instead of "AuthScreen" post-deletion;
  an unrelated pre-existing flaky test (`AdminScreen.test.tsx` REQ-507)
  noted in `NOTES.md`, not fixed as part of this change.
- 2026-07-25 — `CLAUDE.md`, `README.md`, `TODO.md`,
  `docs/architecture-document.md`, `docs/design-document.md`,
  `docs/implementation-document.md`, `docs/requirements-document.md`,
  `mockups/design-mockups.html` — product decision: "xG Arcade" is the
  final product name, not a placeholder. Removed "(working title —
  placeholder name, find-and-replace when a real name is chosen)" and
  equivalent naming-note wording from all doc titles/naming notes; no code
  changes needed since "xG Arcade" was already used throughout the
  codebase (localStorage keys, UI title, etc.). No REQ/ADR affected — this
  is editorial, not a behavior or structural change.
- 2026-07-25 — `docs/decisions/0037-turnstile-captcha-for-guest-creation.md`
  (third amendment), `docs/requirements-document.md`, `SETUP.md`,
  `NOTES.md` — follow-up to the sign-in latency investigation
  (`infra/README.md`'s "Sign-in latency" section, this same day): live
  evidence (an Azure Container App log the product owner shared, showing
  a real login completing server-side in ~1.1s) plus their own repeated
  testing (consistent 8-12s spinners, back-to-back, felt immediately
  after clicking Login, not improving with repetition) pointed away from
  backend/Supabase latency and at the client-side Cloudflare Turnstile
  step instead — `getTurnstileToken()` only ran after the click, so the
  whole chain (script download if uncached, widget render, verification)
  was serialized in front of the actual request. Two fixes shipped in the
  same PR (`frontend/src/lib/turnstile.ts`, `AuthScreen.tsx`,
  `DeleteAccountScreen.tsx`, `ui-implementer`): (1) preload the Turnstile
  *script* on screen mount via new `preloadTurnstileScript()`, never the
  widget render or token mint (tokens expire quickly and are single-use,
  so minting one before the form is filled in risks a stale-token
  rejection — only the script download moves earlier); (2) switched the
  widget from invisible/managed mode to an **always-visible checkbox**
  (`size: 'normal'`), a deliberate product-owner decision, not a bug fix —
  reverses ADR-0037's original Widget UX recommendation, recorded as that
  ADR's third amendment, since an invisible widget shows nothing while
  verifying (read as the app being stuck) and a genuinely invisible-type
  Turnstile site has no interactive fallback if Cloudflare's risk scoring
  is unsure, unlike a visible checkbox. `getTurnstileToken()`'s signature
  changed to take a caller-supplied container element (no longer a single
  hidden `document.body` div `turnstile.ts` owned itself), since a visible
  checkbox needs to render inline in the right spot on whichever screen
  invoked it — `AuthScreen.tsx` now has two containers (login/signup
  share one, "Play as guest" has its own) and `DeleteAccountScreen.tsx`
  has one. Signup's existing second, follow-up-login token call (needed
  because tokens are single-use) now shows an explicit "Verifying again
  to log you in…" status line, since a second visible checkbox appearing
  right after the first is completed needs an explanation it didn't need
  when both renders were invisible. `docs/requirements-document.md`'s
  REQ-717 Widget UX recommendation section and its 2026-07-21
  open-questions log entry corrected to match (marked superseded in
  place, not renumbered/deprecated — this was always documented as a
  recommendation, never a hard acceptance criterion, so no REQ acceptance
  criteria actually changed). `SETUP.md` step 6 corrected: the Cloudflare
  dashboard's widget-mode setting (Managed/Non-Interactive/Invisible) is
  a property of the Turnstile *site itself*, not something the frontend's
  `size` parameter can override after the fact — so an existing dev/prod
  site created under the old "invisible/managed" instruction that
  actually picked Invisible needs its mode switched to Managed in
  Cloudflare's dashboard directly, or the code change alone won't surface
  a checkbox. `docs/architecture-document.md`'s ADR-0037 summary row
  needed no change (already scope-level, doesn't describe widget mode).
  `docs/design-document.md` (0.46 → 0.47) gained matching status notes on
  the still-unspecced login/signup screen (§7) and SCREEN-05 (account
  deletion), following the same pattern already used there for "Play as
  guest"/the guest banner — flagged by `architecture-reviewer`'s gate
  review as a real, if low-priority, documentation-completeness gap since
  this addition (unlike the earlier invisible-mode captcha ones) has an
  actual visible footprint on the rendered screen. `NOTES.md` gained a
  matching entry with the log-evidence timeline. No new ADR: reused
  ADR-0037's established in-place-amendment pattern (this is its third
  same-general-topic amendment, following the two 2026-07-25 scope
  amendments already there) rather than a new ADR number, since the core
  wiring decision (provider, mediation-through-Supabase, secret-key
  boundary) is unchanged — only the widget's visual mode reversed.
  **`quality-architect`'s gate review on this change (same day) found a
  real reliability gap, fixed in the same PR:** `loadTurnstileScript()`
  cached a script-load rejection forever, never clearing it — harmless
  while only `getTurnstileToken()` called it (a failure only ever hit in
  direct response to a user's own submit), but now that
  `preloadTurnstileScript()` fires unattended on every screen mount, one
  transient failure at mount time (a flaky network, a security extension
  blocking the first request) would have silently disabled every
  login/signup/guest/delete attempt for the rest of that page's lifetime,
  recoverable only by a full reload. Fixed by clearing the cache back to
  `null` on rejection (guarded by an object-identity check so a
  since-replaced cache entry can't be clobbered by a stale, late-running
  `.catch`) so a later call gets a genuinely fresh script attempt. The
  review also caught a new test whose name/comment claimed a retry
  already happened when neither the code nor the test itself actually
  demonstrated that — rewritten to test the real (now-fixed) behavior
  instead, plus a matching test for the same gap reached via
  `getTurnstileToken()` directly rather than `preloadTurnstileScript()`.
  Also added container-identity assertions to `AuthScreen.test.tsx`
  (`getTurnstileToken` now takes a caller-supplied container, and the
  form/guest actions each use their own — nothing previously asserted
  the *correct* one was passed, so a copy-paste mix-up between them would
  have compiled and passed every existing test while rendering the wrong
  screen's checkbox in the wrong place).
- 2026-07-25 — `docs/requirements-document.md`, `docs/architecture-document.md`,
  `docs/implementation-document.md`, `docs/backlog.md`,
  `docs/design-document.md` — doc-sync pass (`doc-sync`) for the backend
  half of REQ-507 (admin guest/user metrics view) and REQ-508 (admin bulk
  force-clear guest accounts), both implemented this session: new
  `XGArcade.Api.Admin.AdminAccountsEndpoints` (`GET /admin/accounts/metrics`,
  `GET /admin/accounts/guests/count`, `POST /admin/accounts/guests/clear`,
  registered unconditionally including Production, Admin policy) and four
  new `IUserRepository` methods (`CountUsersAsync`/`CountGuestsAsync`/
  `CountClaimedGuestsAsync`/`GetAllGuestIdsAsync`); the bulk-clear action
  reuses `IAccountDeletionService.DeleteAccountAsync` per ADR-0038's mandate
  (a new `AccountDeletionService.UserNotFoundErrorMessage` const, no
  behavior change, lets it classify per-account outcomes). Added
  `**Status: Implemented**` markers and "Built as (S-073)" notes to both
  REQs (neither had one), and corrected the REQ-508 "Relationship to
  REQ-718" section, which still described REQ-718 as drafted/not-yet-
  implemented even though it shipped earlier this same session (S-072) —
  also resolved the "shared selection-logic building block" question that
  section had left open (the two REQs ended up with deliberately separate
  filtered-vs-unfiltered queries, not a shared one). Added matching
  `architecture-document.md` COMP-01 status note (S-073) and a §6.8 "S-073
  addition" entry (a sixth `IAccountDeletionService` caller). Corrected two
  now-stale literal claims in `implementation-document.md`'s `User` entity
  comments (`IsGuest`/`ClaimedAt` "consulted in exactly one place" no longer
  held once S-072's purge queries and now S-073's admin queries also read
  them). Added backlog entry S-073 (no prior story text existed — these
  REQs were scoped directly, not from a pre-written story — noted
  explicitly in the entry). Fixed a stray "S-076" story reference in
  `design-document.md`'s SCREEN-04 subsection (added by the earlier
  `ui-implementer` frontend pass) to the correct S-073; did not duplicate
  that pass's own CHANGELOG entry below. Reviewed `docs/legal/*.md`
  independently and concluded no update is needed — REQ-507 surfaces only
  aggregate counts of already-collected fields and REQ-508 triggers the
  same anonymize-and-keep deletion mechanism the privacy policy already
  discloses for guest accounts (REQ-718), just on demand instead of on a
  schedule; no new data collected, retained differently, or shared with a
  new party. No new ADR: `architecture-reviewer` had already concluded this
  reuses ADR-0038's existing mandate and REQ-507/508's own already-accepted
  environment/authorization scope rather than a new structural decision —
  confirmed independently.

- 2026-07-25 — `docs/design-document.md` — added SCREEN-04's "Accounts /
  guest-clear (REQ-507/508)" subsection (`ui-implementer`): frontend for the
  admin guest/user metrics view (REQ-507) and bulk force-clear-guests action
  (REQ-508), against `backend/src/XGArcade.Api/Admin/AdminAccountsEndpoints.cs`
  (already landed this session, commit d207a74). Implemented
  `AccountMetricsSection`/`GuestClearSection` in
  `frontend/src/admin/AdminScreen.tsx` — deliberately rendered unconditionally
  (not nested inside the existing `activeRound !== null` Non-Production-only
  gate that round control/user deletion share), since both REQs are
  Production-visible. New `fetchAdminAccountMetrics`/`fetchGuestAccountCount`/
  `clearGuestAccounts` in `frontend/src/lib/api.ts`, matching types in
  `frontend/src/lib/types.ts`. New tokens-only `.admin-screen__metrics`/
  `.admin-screen__metric*` CSS (no new colors/fonts). Judgment calls made
  without prior spec (two sections not one, zero-dry-run-count special case,
  403-hides-section-not-page handling, raw `userId` in the outcome list) are
  recorded in the design doc itself rather than left implementation-only.
  8 new Vitest cases added to `frontend/src/admin/AdminScreen.test.tsx`
  (330 total passing); `tsc -b` and `oxlint` clean. Requirements/architecture
  docs already carry REQ-507/508 from an earlier iteration this same
  session and were not touched here.

- 2026-07-25 — `docs/requirements-document.md`, `docs/architecture-document.md`,
  `docs/implementation-document.md`, `docs/backlog.md`,
  `docs/legal/privacy-policy-draft.md` — implemented REQ-718/ADR-0038 (S-072):
  `User.LastActiveAt` (migration `20260725120000_AddUserLastActiveAt`,
  non-nullable, initialized from `CreatedAt`), updated unconditionally on
  login/guest-creation/claim/guess-submission; new `POST /auth/logout`
  ([Authorize]) deleting an unclaimed guest via the existing
  `IAccountDeletionService`, best-effort, always `204`; new
  `POST /internal/purge-guest-accounts` (bearer-token-gated, same pattern as
  `/internal/generate-round`, run daily by new `purge-guest-accounts.yml`
  at 07:00 UTC) implementing the 30-day-unclaimed and 7-day-inactive purges
  via two new `IUserRepository` queries, deduped before deletion. Extracted
  the existing `/internal/generate-round` bearer-token check into a shared
  `XGArcade.Api.Internal.InternalJobAuthorization` helper rather than
  duplicating it for the new endpoint. Frontend: `lib/api.ts` gained
  `logout()`, called best-effort/non-blocking from `App.tsx`'s
  `handleLogout` so REQ-715's instant local logout is unaffected. Updated
  the privacy policy draft (`docs/legal/privacy-policy-draft.md`) to
  disclose the new `LastActiveAt` tracking and the automatic guest-account
  removal rules, per CLAUDE.md's legal-drafts rule. Added NUnit/API test
  coverage (`AuthEndpointTests.cs`, `GuessEndpointTests.cs`,
  `InternalGuestCleanupEndpointTests.cs`, `UserRepositoryTests.cs`)
  covering REQ-718's own Unit/API/Integration test-level note, including
  the exactly-30-days/exactly-7-days boundary cases and claimed-account
  exclusion. Neither the implementation nor the tests were run against a
  real `dotnet test`/`dotnet build` in this session — no `.NET` SDK
  available in the sandbox; both were hand-traced against REQ-718's own
  acceptance criteria instead and need confirming in CI. REQ/ADR
  refs: REQ-718, REQ-710, REQ-715, REQ-201, ADR-0038, ADR-0036, ADR-0022.
- 2026-07-25 — `docs/requirements-document.md`, `docs/decisions/0038-guest-account-cleanup.md`
  — added REQ-718 (guest account lifecycle cleanup: delete at logout,
  30-day unclaimed purge, 7-day inactive purge), bundling all three
  related behaviors into one REQ per REQ-717's own precedent for a single
  guest-identity-lifecycle requirement. Added ADR-0038 to record the three
  structural decisions this required: a new `User.LastActiveAt` field
  updated only on login/guest-creation/claim/guess-submission (never on
  every request, and never branching on `IsGuest`); reuse of REQ-710's
  existing `IAccountDeletionService` anonymize-and-keep mechanism for all
  three cleanup paths rather than a second deletion code path (a guest's
  `Guess` rows have the identical "other players' uniqueness/leaderboard
  denominators" corruption risk REQ-710 already solved for); and a
  best-effort logout-triggered deletion backed by the 7-day purge as a
  safety net, following the existing `generate-round.yml`/
  `/internal/generate-round` scheduled-job pattern (ADR-0022/ADR-0027) for
  the two time-boxed purges. Flagged, not resolved here: this REQ implies
  a schema change and new endpoints/cron workflow (`architecture-document.md`/
  `implementation-document.md` updates), and a new backend logout call
  where none currently exists (REQ-715's logout is client-side only
  today) — left for `doc-sync`/implementation work, not made here. No
  entry added to §7 — all three open questions the task raised were
  resolved as technical defaults with existing precedent to follow, not
  genuine open product decisions. REQ/ADR refs: REQ-718, REQ-710, REQ-717,
  REQ-201, REQ-204, REQ-409, REQ-715, ADR-0036, ADR-0038, ADR-0022,
  ADR-0027.
- 2026-07-25 — `infra/README.md`, `NOTES.md` — investigated a live report
  that sign-in became slow after the Cloudflare Turnstile captcha rollout
  (ADR-0037). Measured real cold-vs-warm latency against the deployed dev
  Container App via a temporary `workflow_dispatch` diagnostic workflow
  (added, run, then deleted in the same PR — this repo's own sandbox
  environments can't reach `*.azurecontainerapps.io` directly, same proxy
  restriction NOTES.md already documents for `wikidata.org`). Confirmed
  both hypotheses are real and additive, not either/or: a cold
  `GET /health` after ~22 idle minutes took 9.93s (0.13s of that was the
  TCP connect — the rest was `minReplicas: 0`'s Container Apps cold
  start, pre-existing since ADR-0004/S-001, not new); a warm `/health`
  took ~0.35s; the first `POST /auth/login` on an already-warm backend
  cost 1.97s (a ~1.6s one-time premium for the backend's first
  Supabase-mediated call, which now also verifies the captcha token
  against Cloudflare), dropping to ~0.45s on the next two attempts. Added
  a new `infra/README.md` section (after the cost-reality-check table)
  documenting the numbers, the two distinct additive causes (Container
  Apps cold start vs. captcha's added external hop, the latter split into
  a backend→Supabase→Cloudflare cost measured here and a frontend
  Turnstile-widget cost that is real but wasn't measured by this
  backend-only probe), and the explicit decision to keep `minReplicas: 0`
  given this project's stated free-tier-only constraint rather than pay
  ~$10–12/month to eliminate the cold start — recorded as a known,
  accepted Tier 0 trade-off (`MVP-SCOPE.md`), not a bug, with a named but
  not-implemented free-tier-compatible mitigation (an hours-scoped
  keep-alive ping) for if this is revisited later. No requirements/
  architecture/implementation-document changes — no application behavior
  changed, investigation and doc-only. `NOTES.md` gained a matching entry
  with the same numbers, since this is exactly the kind of "surprising
  enough to want to have known it going in" gotcha that file exists for.
- 2026-07-25 — `docs/architecture-document.md` (0.48 → 0.49),
  `docs/implementation-document.md` (0.65 → 0.66), `docs/
  requirements-document.md` (1.00 → 1.01), `SETUP.md` — doc-sync pass for
  the captcha-scope-widening bug fix on `claude/captcha-login-signup-fui8nb`
  (10 commits since `5ebcb08`; REQ-717/REQ-701/REQ-710, ADR-0037's two
  same-day amendments). Root cause and fix are already fully recorded in
  ADR-0037's two 2026-07-25 amendments and the requirements-document.md
  edits made mid-implementation (REQ-717's scope-correction addition,
  REQ-701's new signup/login captcha criterion, REQ-710's new
  account-deletion password re-confirmation captcha criterion) — this pass
  only closes what those changes explicitly flagged as deferred to
  `doc-sync`, plus a wording correctness check now that all the code
  actually exists.
  `architecture-document.md` §10's ADR-0037 row updated to describe the
  widened scope (guest + signup + login + account-deletion
  re-confirmation, not guest-only) — flagged directly in the ADR's own
  Consequences/Follow-up section as this agent's own scope boundary
  (`requirements-writer`/`backend-implementer` never edit
  `architecture-document.md` directly). No other architecture-document.md
  section needed a change: the "For AI agents"/boundary rules and COMP-01
  status note were already accurate, since this fix widened which
  endpoints participate in an unchanged mediate-through-Supabase pattern,
  not any component boundary or responsibility (`architecture-reviewer`
  already confirmed this same judgement mid-session — no new ADR).
  `implementation-document.md` §4's project-structure listing for
  `frontend/tests/e2e` now names the new `turnstile-stub.ts` helper (stubs
  `window.turnstile` via `page.addInitScript()` so E2E specs that only
  need an authenticated session aren't blocked by a captcha widget that
  can't mint a real token in CI) — judged worth a one-line mention at the
  same level of detail already given to named `/lib` files, not a deeper
  treatment; the `ISupabaseAuthClient.SignUpAsync`/
  `SignInWithPasswordAsync` signature change (added `captchaToken`
  parameter) was judged adequately covered by ADR-0037/REQ-701/REQ-710
  already, since this document doesn't track individual interface method
  signatures anywhere else either. Pre-existing gap noted but *not* fixed
  in this pass (out of scope of this diff): `implementation-document.md`
  §1's tech-stack table has never listed Cloudflare Turnstile as an
  adopted third-party service, even from ADR-0037's original guest-only
  landing — flagged for a human/future pass, not fixed here.
  `requirements-document.md`: fixed a genuine inconsistency a
  `ui-implementer` mid-session review flagged and this pass verified
  directly against `AuthController.cs` before trusting it — REQ-701's and
  REQ-710's 2026-07-25 captcha additions each said the requirement "holds
  regardless of whether Supabase's captcha protection setting happens to
  be enabled ... when it is disabled, no token is required," which
  contradicts the shipped code: `AuthController.Signup`/`Login`/
  `DeleteAccount` all reject a missing `CaptchaToken` unconditionally,
  with no code path that checks or could check Supabase's dashboard
  toggle state at request time (matching REQ-717's own guest-flow
  phrasing, which never had this conditional framing to begin with). Both
  bullets corrected in place, plus their "not yet built as of this
  addition" status notes updated to reflect that the code is now built.
  `SETUP.md` step 6 corrected per ADR-0037's own flagged follow-up (both
  amendments): it read as if Supabase's "Enable Captcha Protection" toggle
  only affected guest-account creation, which is what let this bug reach a
  live deployment undetected; now explains the toggle is project-wide
  (covers `signup`, `token?grant_type=password`, and anonymous sign-in
  alike) and that all four call sites now send a token, so enabling it is
  safe. No new ADR: this pass reused the mid-session judgement already
  made twice on this branch (amend ADR-0037 in place rather than write
  ADR-0038/0039) — the wiring decision (provider, mediation-through-
  Supabase, secret-key boundary) never changed, only which endpoints
  participate, confirmed again here rather than re-litigated. Open
  question for a human: whether `docs/legal/*.md` needs updating too, since
  every signup/login/account-deletion page now loads Cloudflare's Turnstile
  script (a new-to-those-flows third-party runtime dependency, same class
  of change the Google Fonts CDN entry in `implementation-document.md` §1
  already treats as privacy-doc-relevant) — not touched in this pass since
  it wasn't in this task's explicit scope and legal drafts need human
  review regardless. ADR-0037/REQ-717/REQ-701/REQ-710.
- 2026-07-22 — `SETUP.md`, `MVP-SCOPE.md`, `docs/backlog.md` (new S-071
  entry) — final doc-consolidation pass for REQ-717's captcha hardening
  (ADR-0037) now that both backend and frontend halves are merged and
  quality-gated on `claude/orchestrate-grid-leaderboard-ux-wh2876`.
  `SETUP.md` step 6 no longer says the frontend Turnstile widget/token
  acquisition "is not yet built, so complete this step before that lands"
  — it's built (`frontend/src/lib/turnstile.ts`, `AuthScreen.tsx`,
  `a89fc53`/`6f267a4`), so the step's only remaining piece is the manual
  Cloudflare/Supabase dashboard configuration, not a precondition to code
  landing. `MVP-SCOPE.md`'s captcha bullet referenced "`SETUP.md`'s
  Supabase section (step 5)" for the Turnstile/captcha-setup instructions
  — that content is actually step 6 (step 5 is the unrelated Anonymous
  Sign-ins toggle, added in a same-day follow-up after the captcha step
  was originally drafted as step 5); corrected in both places it appeared.
  `docs/backlog.md` had no story entry at all for the captcha work despite
  three implementation commits (backend `e957029`, frontend `a89fc53`,
  test coverage `5d101f2`) plus a quality-review fix (`6f267a4`) — added
  **S-071** following the S-069/S-070 shape (Accept/Deps/Built as).
  Verified `docs/requirements-document.md`'s REQ-717 captcha status notes,
  `docs/architecture-document.md`'s ADR-0037 table row, and the existing
  CHANGELOG entries below already describe the final shipped,
  end-to-end-complete state accurately (each below is a point-in-time
  record and later entries explicitly close the gaps earlier ones flagged
  — no edit needed). Confirmed the 314-frontend-test total cited in the
  new S-071 entry against a live `npx vitest run` (20 files, 314 passed).
  REQ-717/ADR-0037.
- 2026-07-22 — `docs/requirements-document.md` (0.97 → 0.98) — implemented
  the frontend half of ADR-0037's Cloudflare Turnstile captcha hardening for
  "Play as guest" (REQ-717's 2026-07-21 "Bot-check (captcha)" addition, the
  gap the previous entry below flagged as pending). New
  `frontend/src/lib/turnstile.ts`: a small, promise-based wrapper
  (`getTurnstileToken()`/`resetTurnstileWidget()`) that lazily loads
  Cloudflare's script once, renders the invisible/managed widget (REQ-717's
  recommended mode), and tears down/re-renders the widget on every call so
  a fresh token is always obtained — never a placeholder or reused token.
  `frontend/src/lib/api.ts`'s `playAsGuest()` now takes a `captchaToken`
  parameter and sends it as `POST /auth/guest`'s JSON body
  (`{ captchaToken }`), fixing the expected fallout the prior backend
  commit (e957029) flagged: the endpoint now requires a body and would
  otherwise auto-reject every guest sign-in. `AuthScreen.tsx`'s
  `handlePlayAsGuest` calls `getTurnstileToken()` before ever calling
  `playAsGuest()`, and — the REQ's explicit acceptance criterion — calls
  `resetTurnstileWidget()` only when the caught error is an `ApiError` with
  `title === 'Captcha verification failed'` (the backend's distinct
  captcha-rejection response), never on any other guest-sign-in failure.
  Tests: `frontend/src/lib/turnstile.test.ts` (script-load-once, widget
  render/teardown, reset-forces-fresh-render, script/Turnstile error
  rejection — all against a fake `window.turnstile`, no live Cloudflare
  site key exists in this sandbox) and new/updated cases in
  `frontend/src/auth/AuthScreen.test.tsx` (token sent in the request body;
  the distinct captcha rejection resets the widget and shows its detail
  text; a generic guest-sign-in failure does not reset the widget; a
  `getTurnstileToken()` failure never calls `POST /auth/guest` at all).
  `frontend/src/App.test.tsx` needed `./lib/turnstile` mocked at the top of
  the file too, since its existing guest-banner tests click "Play as
  guest" and would otherwise hang waiting on a real (untestable) Cloudflare
  script load. No `docs/design-document.md` change: invisible/managed mode
  renders no visible UI in the common case, so no new color/font/animation
  token was needed (checked per this task's own instruction) — if
  Cloudflare's own interactive-challenge fallback ever fires, that's
  Cloudflare's UI, not this app's, and stays unthemed.
- 2026-07-22 — `docs/requirements-document.md` (0.96 → 0.97), `SETUP.md`,
  `infra/README.md`, `.github/workflows/deploy.yml`, `MVP-SCOPE.md` —
  implemented the
  backend half of ADR-0037's Cloudflare Turnstile captcha hardening for
  `POST /auth/guest`: `GuestRequest.CaptchaToken` threaded through
  `ISupabaseAuthClient.SignInAnonymouslyAsync` to Supabase's
  `gotrue_meta_security.captcha_token` field, and a new
  `SupabaseAuthResult.IsCaptchaRejection` signal (parsed from Supabase's
  `error_code`/message on a failed anonymous sign-in) lets
  `AuthController.Guest` return a distinct "Captcha verification failed"
  (400) response instead of the generic "Guest sign-in failed" (500) for a
  missing/expired/invalid token, per REQ-717's 2026-07-21 acceptance
  criteria. `requirements-document.md`: noted the backend side as
  implemented, frontend Turnstile widget/token acquisition still pending.
  `infra/README.md`/`deploy.yml`: added `DEV_TURNSTILE_SITE_KEY`/
  `PROD_TURNSTILE_SITE_KEY`, wired into `deploy-frontend`'s
  `VITE_TURNSTILE_SITE_KEY` the same way `VITE_API_BASE_URL` already is —
  discovered along the way that `VITE_API_BASE_URL` itself is wired
  directly in `deploy.yml`'s Oryx build step, not through Bicep (no Bicep
  module touches frontend build-time config at all), so this follows that
  actual pattern rather than the Bicep-module assumption in the original
  task description. `SETUP.md` step 6 updated to reflect the backend
  pass-through now being built and to name the new deploy-time secret.
  Not independently verified against a live Supabase project (no network
  access in this environment) — Supabase's `gotrue_meta_security
  .captcha_token` request field and its `error_code: "captcha_failed"`
  response field are both recorded from documentation/training knowledge,
  not confirmed live; flagged for manual verification, same caveat
  `SignInAnonymouslyAsync`/`LinkEmailPasswordAsync` already carry from
  ADR-0036/ADR-0037.
- 2026-07-21 — `docs/architecture-document.md` (0.47 → 0.48), `SETUP.md` —
  closed two gaps flagged by the ADR-0037/REQ-717 captcha entry directly
  below: added the missing ADR-0037 row to `architecture-document.md` §10's
  ADR table, and gave `SETUP.md`'s Supabase section its own explicit step
  for turning on Anonymous Sign-ins (off by default, never documented as a
  precondition before a live deployment hit "Could not start a guest
  session" because of it) ahead of the Turnstile captcha step, rather than
  leaving it implied inside that step.
- 2026-07-21 — `docs/requirements-document.md` (0.95 → 0.96),
  `docs/decisions/0037-turnstile-captcha-for-guest-creation.md` (new),
  `MVP-SCOPE.md`, `SETUP.md` — Cloudflare Turnstile added as a second,
  complementary abuse-prevention layer for guest-account creation
  (`POST /auth/guest` only — not signup/login), directly motivated by
  Supabase's own dashboard warning on enabling Anonymous Sign-ins.
  `requirements-document.md`: REQ-717 gained a dated "Bot-check (captcha)
  for guest creation" acceptance-criteria addition (acceptance criteria
  only, not yet built) — token obtained client-side before calling
  `POST /auth/guest`, passed through unmodified to Supabase's native
  `gotrue_meta_security.captcha_token` verification (no independent
  verification in this backend), a distinct rejection response required
  for a missing/invalid/expired token (not the existing generic "Guest
  sign-in failed"), and a recommendation for Turnstile's invisible/managed
  widget mode over the visible checkbox mode; its Test level line was
  extended to match. New ADR-0037 records the provider choice (Turnstile
  over hCaptcha) and the wiring decision (Supabase-native verification,
  never a direct Cloudflare call from this backend) — judged to warrant
  its own ADR rather than folding into ADR-0036, since "which provider and
  how it's wired" is a real structural choice with alternatives, distinct
  from ADR-0036's own guest-identity-mechanism decision. Also decided:
  the Turnstile site key is a new frontend build-time config value,
  `VITE_TURNSTILE_SITE_KEY`, following the existing `VITE_API_BASE_URL`
  convention (`frontend/src/lib/api.ts`); the secret key belongs solely in
  Supabase's own Auth dashboard settings, never in this backend's
  configuration. `MVP-SCOPE.md`'s "Guest play" bullet got a short addendum
  recording this as "specified, not yet built." `SETUP.md`'s Supabase
  section gained a new step 5 for the manual Cloudflare Turnstile site
  setup and Supabase Auth captcha-settings step — this also incidentally
  documents enabling Supabase's Anonymous Sign-ins toggle itself for the
  first time, closing a pre-existing gap (ADR-0036/REQ-717 shipped without
  ever adding that toggle as a documented precondition anywhere; flagged
  here rather than silently left, in case a fuller doc-sync pass wants to
  treat it as its own line item). No `architecture-document.md` or
  application code change made by this pass — flagged for `doc-sync`/
  `backend-implementer`/`ui-implementer` follow-up (ADR-0037's own
  Follow-up section lists the concrete deltas: `ISupabaseAuthClient.
  SignInAnonymouslyAsync`'s new `captchaToken` parameter, `POST
  /auth/guest`'s new request body, `AuthController.Guest`'s split error
  handling, `infra/bicep`/`infra/README.md`'s new frontend build-time
  variable, and `architecture-document.md` §10's ADR table row).
  REQ-717/ADR-0037.
- 2026-07-21 — `docs/requirements-document.md` (0.94 → 0.95),
  `docs/design-document.md` (0.44 → 0.45) — bug fix:
  `ScoringExplainer.tsx`'s card (`ScoringExplainer.css`) had no
  `max-height`/`overflow-y`, so S-068's content growth (six to nine
  paragraphs) pushed it past the viewport on short/mobile screens with no
  way to scroll — reported by a player as breaking the UI. Fixed with
  `max-height: calc(100vh - var(--space-4) * 2); overflow-y: auto`. Found
  and fixed the identical gap in `GuessInput.css`'s `.guess-input` card
  (`max-height: 90vh; overflow-y: auto`), which hosts the SCREEN-02a
  disambiguation prompt. No other modal/backdrop pattern found in
  `frontend/src`. No REQ acceptance criteria changed, no ADR needed (pure
  CSS fix using existing tokens) — documented as an implementation note
  under REQ-213.
- 2026-07-21 — `docs/requirements-document.md` (0.93 → 0.94), `MVP-SCOPE.md`,
  `docs/backlog.md` (S-070 addendum) — doc-sync pass, plus the same-day
  follow-up work it's reconciling: `backend-implementer` added
  `MeResponse.IsGuest` (mirrors `User.IsGuest` directly), and a matching
  frontend commit switched `AuthScreen.tsx`/`SettingsScreen.tsx`/`App.tsx`
  over to `CurrentUser.isGuest`, removing the `email === null` inference
  the S-070 entry below had flagged as a gap. `test-writer` then added the
  remaining REQ717-named coverage S-069/S-070 had left open (28
  REQ717-named tests total across `AuthEndpointTests.cs`,
  `UserRepositoryTests.cs`, `LeaderboardServiceTests.cs`,
  `RoundCloseServiceScoringTests.cs`, `GuessSubmissionServiceTests.cs`, and
  `App.test.tsx`): a guest's guess counting fully toward a real account's
  uniqueness denominator, REQ-409's exact-`ClaimedAt` cutoff and
  post-claim-only 5-round floor, explicit REQ-406/407/408 participation,
  guess-attempt-limit parity, `DeleteAccount`'s guest-rejection branch, and
  the header banner's show/hide/disappears-after-claim behavior.
  `quality-architect` then gave `AuthController.
  GenerateUniqueGuestDisplayNameAsync` an optional `Random` seam (the same
  pattern `GridGameModule` already uses) so its collision-retry branch is
  now deterministically testable, extracted `SupabaseAuthClient`'s
  duplicated error-parsing into one shared helper, and merged a
  near-duplicate guest-guess-seeding test helper into the existing one —
  no behavior change, no new ADR (pure internal refactor). This pass
  updates the two stale spots this follow-up work left behind: REQ-717's
  frontend status note (`requirements-document.md`) and `MVP-SCOPE.md`'s
  "Guest play" bullet both still described the now-closed `isGuest` gap as
  open. REQ-717/ADR-0036/S-069/S-070.
- 2026-07-21 — `docs/requirements-document.md` (0.92 → 0.93),
  `docs/design-document.md` (0.43 → 0.44), `MVP-SCOPE.md`,
  `docs/backlog.md` (new S-070) — REQ-717/ADR-0036 guest play **frontend
  half built (S-070)**: `AuthScreen.tsx` gained a "Play as guest" entry
  point (`playAsGuest()` in `lib/api.ts`, `POST /auth/guest`, routed through
  the existing login/signup success path — no separate "guest mode"
  client-side state); `SettingsScreen.tsx` gained a "Save your progress"
  claim section, visible only for a guest account (`claimAccount()` in
  `lib/api.ts`, `POST /auth/claim`); `App.tsx` gained a small header banner
  nudging a guest toward that section (a UX addition beyond this REQ's own
  acceptance criteria, documented in `design-document.md` §3/§7). **Real gap
  found and flagged, not silently worked around:** the backend's
  `MeResponse` DTO has no dedicated `isGuest` field (S-069 never added
  one) — the frontend derives guest status as `email === null` instead
  (correct today, since only a guest row ever has a null email, but less
  robust/self-documenting than a real field would be); recommended as a
  small backend follow-up, not added here. Vitest coverage added in
  `AuthScreen.test.tsx`/`SettingsScreen.test.tsx`; exhaustive REQ717-named
  coverage remains `test-writer`'s to add. No Playwright E2E spec needed
  updating — none asserts on the behavior this change touches.
  REQ-717/ADR-0036/S-070.
- 2026-07-21 — `docs/requirements-document.md` (0.91 → 0.92),
  `docs/architecture-document.md` (0.46 → 0.47),
  `docs/implementation-document.md` (0.64 → 0.65), `MVP-SCOPE.md`,
  `docs/backlog.md` (new S-069),
  `docs/legal/privacy-policy-draft.md` (0.7 → 0.8, noted guest play as a
  data-collection variant: no email/password held until claimed) —
  REQ-717/ADR-0036 guest play **backend half built**: `POST /auth/guest` (Supabase Anonymous Sign-in, mediated
  through a new `ISupabaseAuthClient.SignInAnonymouslyAsync`, rate-limited
  by a new, tighter `auth-guest` policy — 3/min default vs. auth-signup/
  auth-login's 10/min), `POST /auth/claim` (claim/upgrade path, a new
  `ISupabaseAuthClient.LinkEmailPasswordAsync` + `IUserRepository.
  ClaimGuestAsync`, preserving every `Guess`/`LeagueMembership` row
  unchanged), `User.IsGuest`/`User.ClaimedAt` columns (migration
  `20260721140000_AddGuestPlaySupport`), `User.Email` made nullable (a
  real ripple, audited across every existing caller), and REQ-409's
  qualifying-rounds query (`GuessRepository.
  GetPerRoundFinalPointsByUserIdsAsync`) narrowed to exclude guest rows and
  a claimed account's pre-claim rounds. No other REQ-201-210/204/406/407/408
  code path touched, per ADR-0036. Frontend (guest entry point, claim UI)
  remains a separate, not-yet-scoped follow-up story — `MVP-SCOPE.md`'s
  "Guest play" bullet updated to record the backend as implemented and the
  frontend as still open. Two Supabase API call shapes
  (`SignInAnonymouslyAsync`/`LinkEmailPasswordAsync`) could not be verified
  against a live Supabase project from the build environment — flagged in
  `SupabaseAuthClient`'s own doc comments for manual verification.
  REQ-717/ADR-0036/S-069.
- 2026-07-21 — `docs/requirements-document.md` (0.90 → 0.91),
  `docs/decisions/0036-guest-play-anonymous-auth.md` (new),
  `docs/architecture-document.md` (ADR table only, new row), `MVP-SCOPE.md`
  — guest play designed and discussed with the product owner (not built):
  added **REQ-717** (auto-provisioned guest `User`, no email/password
  required; guesses count fully toward REQ-204's uniqueness and REQ-206's
  totals; guests appear on REQ-406/407/408's round-scoped/live
  leaderboards via ordinary `LeagueMembership`, no new query logic;
  excluded from REQ-409's all-time median ranking via a new `User.IsGuest`
  flag, both because guests rarely reach the 5-round floor and because a
  guest identity isn't durably "the same person" across sessions the way
  REQ-409's median assumes; a dedicated rate limit tighter than REQ-606's
  existing auth-endpoint limits; a claim/upgrade path converting a guest
  to a real account in place, preserving guess history, with pre-claim
  rounds explicitly not retroactively qualifying for REQ-409) and
  **ADR-0036** (the identity mechanism: backend-mediated Supabase
  Anonymous Sign-ins, following ADR-0013's exact mediation precedent,
  rejecting a fully client-local/no-server-identity scheme as unable to
  satisfy REQ-210's attempt limits or REQ-406/407/408's leaderboard
  participation at all). Added a new Tier 1 entry to `MVP-SCOPE.md`
  recording this as a deliberate pull-forward decision (same pattern as
  REQ-108/214/402-403), not a fired trigger — implementation is a
  separate, not-yet-scoped future story. REQ-717/ADR-0036.
- 2026-07-21 — `docs/requirements-document.md` (0.89 → 0.90),
  `docs/design-document.md` (0.42 → 0.43), `docs/backlog.md` — S-068
  (leaderboard scoring/median/fairness explainer, REQ-213 extension) shipped
  and marked built. `requirements-writer` had already extended REQ-213
  earlier this session; `ui-implementer` built it (`LeaderboardScreen.tsx`/
  `.css` gained a second `(ⓘ)` entry point reusing `frontend/src/grid/
  ScoringExplainer.tsx` with no new props; that component gained three new
  content paragraphs covering REQ-409's median/participation-gate ranking
  and REQ-404/406/407's never-played/live-scope fairness rules), and
  `test-writer` added 8 new tests (288 total). `quality-architect` passed
  with one trivial comment fix and flagged REQ-213's own status text as
  stale ("decided, not yet built" in two spots, ~line 1417 and ~1452) now
  that the story is done; `architecture-reviewer` passed clean, no ADR
  needed. Doc-sync pass: corrected REQ-213's stale status wording to
  "Implemented"/"built"; updated `design-document.md` SCREEN-03 (added the
  `(ⓘ)` entry point to its mock, and — a pre-existing staleness this story
  was the right moment to fix, not just build on top of — corrected the
  all-time scope's description, which had never mentioned the median/≥5-
  round gate decided 2026-07-20, and the Current Round scope's description,
  which had never mentioned S-056's untouched-cell-at-max fairness rule) and
  SCREEN-06 (documented the second entry point and three new content
  paragraphs); marked S-068 built in `docs/backlog.md` with a "Built as"
  section. REQ-213, REQ-409, REQ-404, REQ-406, REQ-407.
- 2026-07-21 — `docs/requirements-document.md` (0.87 → 0.88),
  `docs/design-document.md` (0.41 → 0.42), `docs/implementation-document.md`
  (0.63 → 0.64), `docs/backlog.md` — round end-time indicator shipped in
  the grid header: `requirements-writer` added dated acceptance criteria
  to REQ-303 (relative duration buckets, "Ending soon" fallback, accessible
  absolute-time name, no live ticking — deliberate Tier 0 simplification),
  `ui-implementer` built it (`frontend/src/lib/roundTime.ts`,
  `GridScreen.tsx`/`.css`), `test-writer` added unit + integration coverage,
  and `quality-architect` found and fixed a real bug (a malformed
  `endTime` rendered `"Ends in NaNm"`) with a regression test.
  `architecture-reviewer` passed the diff clean (no boundary issue, no ADR
  needed). Doc-sync pass: corrected `design-document.md`'s SCREEN-01 mock,
  which had shown a bare `⏱ 1d 4h` clock icon and a `Round #14` label with
  no backing field — now reads `Ends in 1d 4h` (matching what was actually
  built) with the round number dropped rather than invented; and added
  `roundTime.ts` to `implementation-document.md`'s `/lib` project-structure
  listing, which had drifted out of date. Also folds in this session's
  separate `docs/backlog.md` addition of **S-068**, queuing (not yet
  built) an extension of REQ-213's scoring explainer with REQ-409's
  median/participation-gate content and SCREEN-03 reachability — kept as
  its own backlog story per the one-story-per-session rule, not bundled
  into this change. REQ-303.
- 2026-07-21 — `docs/requirements-document.md` (0.86 → 0.87) — two real CI
  failures on PR #93 fixed after this session's own feature work collided
  with itself. (1) `/internal/test-data/seed-guessable-round` created
  identically-named "Thierry Henry"/"Robert Pires" players on every call
  with no reuse; concurrent E2E calls against one CI Postgres accumulated
  duplicate matches for the same cell, which REQ-209's now-correct
  multi-match handling surfaced as an unexpected disambiguation prompt
  instead of the old auto-accept masking it — fixed with a short unique
  name suffix per call (mirrors the existing `WikidataQid` pattern).
  (2) This session's own test-coverage-extension work (custom leagues +
  dark mode E2E tests) pushed the whole Playwright suite's total
  signup+login count for one CI run just over REQ-606's 10/minute-per-IP
  limit, since every spec file's traffic lands on one backend process from
  the single CI-runner IP within the same window. Made both permit counts
  configurable (`RateLimiting:AuthSignupPermitLimit`/`AuthLoginPermitLimit`,
  default 10, unchanged) and raised them for `ci.yml`'s E2E job only — see
  REQ-606's new status note. (3) `play-grid.spec.ts`'s REQ-401 leaderboard
  assertion predated REQ-409's 5-qualifying-round minimum on the "All-time"
  scope it reads; updated to loop its seed/guess/close flow 5 times per
  player (cheap — these guesses hit pre-seeded `PlayerAttribute` rows, no
  live Wikidata lookup) instead of once. All 9 E2E tests + 643 backend
  tests re-verified green locally against a real Postgres+backend stack
  before pushing.
- 2026-07-21 — `docs/backlog.md` (S-027 addendum) — quality-architect pass
  over this session's diff: fixed a "rolling window" vs. "calendar-aligned
  window" terminology drift in `frontend/src/lib/api.ts`'s REQ-405 doc
  comments (comment-only, no behavior change) and factored a duplicated
  bulk-fetch-by-player-id helper out of `PlayerStoreRepository`
  (`GetPlayerAliasesByPlayerIdsAsync`/`GetPlayerAttributesByPlayerIdsAsync`
  now share one private `GroupByPlayerIdAsync<TEntity>`). Backend suite
  (642 tests) and frontend suite (256 tests) both re-verified green after
  the change. Same drift still present in `design-document.md` SCREEN-03's
  "Time Windows" bullet — flagged, not edited, for a `doc-sync`/
  `requirements-writer` pass.
- 2026-07-21 — `docs/requirements-document.md` (0.85 → 0.86), `MVP-SCOPE.md`
  (Tier 1 struck through), `docs/backlog.md` (new S-067 entry) —
  disambiguation UI (REQ-209) pulled forward and implemented, replacing
  the auto-accept-lowest-id fallback: a guess matching more than one
  fitting candidate now shows a real picker. `GuessSubmissionService`
  returns the disambiguation-needed outcome before ever touching the
  Guess repository — no row persisted, no attempt consumed — making
  REQ-210's "not a separate attempt" guarantee structural. New
  `SubmitGuessRequest.ChosenPlayerId`/`SubmitGuessResponse.Candidates`
  API fields; `GuessInput.tsx` renders the SCREEN-02a picker. 15 new
  backend tests (642 total) + 8 new frontend tests (256 total), both
  suites green. REQ-209, REQ-210 (structural clarification).
- 2026-07-21 — `docs/requirements-document.md` (0.84 → 0.85), `docs/
  decisions/0035-national-team-query-property-flag-on-country-definition.md`
  (new), `docs/implementation-document.md`, `MVP-SCOPE.md`, `docs/
  backlog.md` (new S-066 entry) — national teams (England/Scotland/Wales/
  Northern Ireland) pulled forward and implemented (REQ-114): a per-row
  `UsesCountryForSportProperty` flag on `CountryDefinition`, queried via
  Wikidata's `P1532` instead of `P27`, rather than a new category type —
  see ADR-0035 for the full alternatives-considered record. The
  `P27`-vs-`P1532` choice is made in exactly one place
  (`WikidataLookupService.LookupAndPersistAsync`); `GridGameModule`'s
  pairing/dispatch logic needed no changes. QIDs unverified against live
  Wikidata, flagged accordingly. Known follow-up: Country × Trophy doesn't
  yet honor the flag (unreachable in production today). 20 new tests; full
  backend suite (627 tests) green. REQ-114, ADR-0035.
- 2026-07-20 — `docs/requirements-document.md` (0.83 → 0.84), `docs/
  backlog.md` (S-064 "Built as") — REQ-716 (dark mode) fully implemented:
  System/Light/Dark toggle on Settings, `localStorage`-persisted, applied
  before first paint (no flash of the wrong theme). Every dark token value
  copied verbatim from the design pass's table; verified visually via a
  real Chromium screenshot in addition to 16 new unit tests. One
  coincidental-not-derived contrast finding flagged (login button text via
  `--color-surface-card` reuse, 4.64:1 in dark theme — passes AA, narrowly,
  by coincidence). Full frontend suite (248 tests) green. REQ-716.
- 2026-07-20 — `docs/requirements-document.md` (0.82 → 0.83), `docs/
  backlog.md` (new S-065 entry) — REQ-208 fully implemented: guess-time
  matching now tries exact name, then `PlayerAlias`, then a bounded
  edit-distance fuzzy pass (length-tiered tolerance: 0/1/2 for
  <=4/5-8/>=9 character names), each stage only reached if the previous
  found nothing. Stays on the correctness-checking side only, no new read
  path into `PlayerNameIndex` (ADR-0007). 27 new tests; full backend
  suite (607 tests) green. REQ-208.
- 2026-07-20 — `docs/design-document.md` (0.39 → 0.40), `docs/
  requirements-document.md` (0.81 → 0.82) — REQ-716 (selectable color
  themes / dark mode) design pass: decided and contrast-verified a full
  dark-theme token set in `design-document.md` §2 (WCAG relative-luminance
  ratios computed for every text/icon-on-background pairing that carries
  real information — body/muted text, and the `accent-green`/`accent-gold`/
  `accent-red` correctness colors; the photo-overlay scrim set needs no
  theme-specific value at all, since it's calibrated against a photo's own
  brightness, not app chrome). Mechanism decided: an explicit System/Light/
  Dark toggle on `SettingsScreen.tsx`, persisted in `localStorage`
  (device-local, no `User`-level sync, same reasoning as ADR-0033),
  defaulting to `prefers-color-scheme` — not an automatic-only approach,
  since REQ-716's own request text asks to *choose*. Colors only — layout,
  spacing, type, and animation tokens are unaffected. Design/spec only;
  no component code changed. REQ-716 moved from "Proposed, placeholder,
  not implementation-ready" to "Proposed, implementation-ready" (not
  Implemented). §7 open questions in both docs updated to record the
  resolution. Implementation is a separate, not-yet-queued
  `docs/backlog.md` story. `docs/decisions/0034-dark-mode-explicit-toggle-localstorage.md`
  (new) records the mechanism/persistence choice (explicit toggle over
  automatic-only; `localStorage` over a `User`-level column) — a real,
  could-have-gone-another-way decision, same bar as ADR-0033. REQ-716,
  ADR-0034.
- 2026-07-20 — `docs/requirements-document.md` (0.80 → 0.81), `MVP-SCOPE.md`
  (Tier 0/Tier 1 sections updated), `docs/backlog.md` (new S-063 entry) —
  REQ-402/403 (custom leagues create/join) pulled forward and implemented,
  ahead of `MVP-SCOPE.md`'s original Tier 1 trigger, by deliberate choice.
  `POST /leagues`, `POST /leagues/join`, `GET /leagues/mine`
  (`LeagueEndpoints`/`LeagueService`), `LeaguesScreen.tsx`. 6-character
  invite codes, uniqueness via an in-app pre-check plus a new DB unique
  index (migration included). REQ-404's full per-custom-league leaderboard
  remains deferred. 18 new backend + 12 new frontend tests. REQ-402,
  REQ-403.
- 2026-07-20 — `docs/requirements-document.md` (0.79 → 0.80), `docs/
  backlog.md` (new S-062 entry) — REQ-701/606 fully implemented: password
  policy (min 8 chars) and account-enumeration-safe signup errors
  (identical generic body for every Supabase rejection reason), plus
  signup/login rate limiting (10 req/min per IP, ASP.NET Core built-in
  `RateLimiting`, 429, no queueing). 7 new backend + 3 new frontend tests.
  REQ-701, REQ-606.
- 2026-07-20 — `docs/requirements-document.md` (0.79 → 0.80, REQ-404's
  interim-state note superseded), `docs/backlog.md` (S-060 "Built as") —
  REQ-409 implemented: the all-time leaderboard now ranks by median
  per-round score (>= 5 qualifying rounds), replacing the raw-sum ranking.
  REQ-406's live-round fold removed from this endpoint (no resolved
  meaning for a live round in a median). 9 new unit + 2 new API tests;
  full backend suite (580 tests) green. REQ-409, REQ-404 (status note).
- 2026-07-20 — `docs/requirements-document.md` (0.78 → 0.79), `docs/
  backlog.md` (new S-061 entry) — REQ-503 fully implemented: `POST
  /admin/player-data/remove` (bulk, hard-delete, `ILogger`-based audit
  logging, no "must be unverified" precondition unlike "approve"),
  `AdminScreen.tsx` gained "Remove selected." Approve/correct/remove are
  now all built. REQ-503.
- 2026-07-20 — `docs/requirements-document.md` (0.77 → 0.78) — REQ-409
  decided (Status: Proposed, implementation-ready, not yet built): the
  all-time leaderboard ranks by the median of each player's per-round
  `SUM(FinalPoints)` totals (locked rounds only, no live component),
  requiring at least 5 qualifying rounds to appear ranked, replacing (not
  adding a tab alongside) REQ-401/404's raw-sum ranking; below-threshold
  players excluded the same way REQ-404's zero-guess exclusion already
  works. REQ-404 gained a cross-referencing status note; removed from §7's
  open-questions list as resolved. Implementation not yet queued in
  `docs/backlog.md`.
- 2026-07-20 — `docs/requirements-document.md` (0.76 → 0.77),
  `docs/architecture-document.md` (0.43 → 0.44), `docs/
  implementation-document.md` (0.60 → 0.61), `docs/backlog.md` (S-031
  "Built as"), `MVP-SCOPE.md` — REQ-108 implemented
  (Tier 0, S-031, ADR-0012): Trophy as a third grid category type, seeded
  with exactly one value, Ballon d'Or (individual award, Wikidata `P166`
  "award received"). `CategoryPairingRules.Trophy` added;
  `GridGameModule.SelectPairing` generalized from S-030's two-way coin flip
  to a uniform-random choice among however many of five candidate pairings
  (Country×Club, Club×Club, Country×Trophy, Club×Trophy, Trophy×Trophy)
  the seeded data supports; `MapAttributeType`/`ResolveCandidateAsync`/
  `LookupLiveMatchesAsync` gained Trophy branches (Trophy×Trophy has no
  live-lookup persist method — unreachable in practice, so falls through to
  the existing fail-closed `null`). `WikidataClient` gained
  `QueryTrophyCountryIntersectionAsync`/`QueryTrophyClubIntersectionAsync`
  (P166 truthy — a documented, deliberate call distinct from P54's
  non-truthy rule — + P27/P54), reusing `BuildIntersectionQuery`'s shared
  plumbing; `WikidataLookupService` gained
  `LookupAndPersistTrophyCountryAsync`/`LookupAndPersistTrophyClubAsync`,
  reusing `PersistMatchesAsync`. `ReferenceDataSeeder` gained a `Trophies`
  array seeding Ballon d'Or (`Q166177`, `IsTeamTrophy=false`) — **this QID
  was not independently verified against a live Wikidata page this
  session** (sandbox has no wikidata.org access, same limitation that bit
  S-036/S-037's guessed club QIDs) — flagged for a human to check before
  relying on it in production. **Load-bearing consequence, asserted by
  test, not just documented:** with only this one seeded trophy, every
  Trophy pairing is infeasible for any realistic grid size, so Trophy is
  mechanically wired up but structurally never selected in production yet —
  proven correct via a larger faked trophy pool in `GridGameModuleTests`
  instead. 42 new REQ108/REQ211-named tests across
  `GridGameModuleTests.cs`, `WikidataClientTests.cs`,
  `WikidataLookupServiceTests.cs`, `ReferenceDataSeederTests.cs`; full
  backend suite (552 tests) passes. Frontend not touched.

- 2026-07-20 — `docs/requirements-document.md` (0.75 → 0.76), `docs/
  backlog.md` (S-027 "Built as") — REQ-405 implemented (Tier 0, S-027):
  round/week/month/year leaderboard resolutions, `GET
  /leagues/global/leaderboard/window/{resolution}`, summing locked
  `Guess.FinalPoints` for closed rounds whose `EndTime` falls in a
  calendar-aligned UTC window (round = single most-recently-closed round).
  New `IRoundRepository.GetClosedIdsWithinWindowAsync` and
  `IGuessRepository.GetTotalFinalPointsByRoundIdsAsync`; no new migration —
  REQ-408's existing `Round(GameKey, EndTime)` index and `Guess`'s existing
  `(RoundId, UserId, CellId)` index already cover both new query shapes.
  18 new REQ405-named tests; full backend suite (510 tests) passes.
  Frontend landed same session: `LeaderboardScreen.tsx` gained a 4th "Time
  Windows" scope with round/week/month/year sub-tabs (`design-document.md`
  SCREEN-03 updated, also backfilling a pre-existing gap where the
  `live`/`past` scopes were never documented there). 4 new frontend
  REQ405 tests; full frontend suite (205 tests), `tsc -b`, lint all clean.
  REQ-405 is now fully implemented, frontend and backend.

- 2026-07-20 — **Doc-sync pass** (this entry and the four below it) —
  `docs/requirements-document.md` (0.74 → 0.75), `docs/architecture-
  document.md` (0.42 → 0.43), `docs/implementation-document.md`
  (0.59 → 0.60), `docs/design-document.md` (0.36 → 0.37), `docs/backlog.md`
  (new S-055/S-056/S-057/S-058 entries) —
  reconciles docs against a 10-commit batch (mobile grid fix, leaderboard
  tab rename, Wikidata auto-verify-everywhere, admin bulk-approve,
  leaderboard scoring fairness, display-name editing, refresh-token login)
  that was already implemented, tested, and merged, but whose own commits
  had left several `docs/requirements-document.md` REQ status headers
  reading "Proposed, not yet implemented" despite being fully built, never
  added the `docs/backlog.md` "Built as" entries this repo's convention
  requires for every completed story, and left two other docs stale — see
  the four feature-level entries directly below for what each piece of
  work actually changed; this entry covers only the corrections found
  independently of that batch's own (incomplete) doc updates. **REQ status
  flips (each verified against the actual merged code before flipping, not
  assumed — see the entries below for the specific classes/methods
  checked):** REQ-401/404's zero-guess-ever exclusion, REQ-406/407's
  zero-guess-cell `MaxPointsPerCell` credit, and REQ-503's bulk-approve
  extension all flip from "Proposed, not yet implemented" to
  "Implemented"; REQ-714 and REQ-715 (both newly drafted this session)
  flip from "Proposed" to "Implemented, Tier 0, S-058." All five match
  their drafted acceptance criteria exactly — no acceptance-criteria text
  needed rewriting, only the status line and a short "built as"
  confirmation each. **Stale literal quotes fixed:** REQ-407/408's status
  notes and `architecture-document.md` §6.2a's leaderboard-flow summary
  quoted the leaderboard's scope-tab labels as `"This round (live)"`/
  `"Past rounds"`; the actual UI now reads "Current Round"/"Previous
  Rounds" (renamed by the batch below) — updated to match, with the
  rename's own history preserved in one explicit cross-reference rather
  than silently rewritten. **`architecture-document.md` §6.3 fixed:** its
  data-sync-flow status notes said "there is no way to flip a `PlayerData`
  row's `Confidence`... via any endpoint yet" and that "'Mark PlayerData
  verified via an endpoint'... remain[s] unbuilt" — both false as of this
  batch's `POST /admin/player-data/approve`; added a dated status note
  describing what's actually built, reached through `IPlayerStoreRepository`
  (COMP-06) per the existing boundary rule, and reconciled §6.1/§6.2's own
  diagram lines describing REQ-211's guess-time fallback as still
  persisting `"unverified"` (superseded by ADR-0032). **`implementation-
  document.md` fixed, found independently of the 5-item review list this
  pass started from:** `PlayerData`'s entity sketch (§5) was missing the
  `ApprovedByAdminId`/`ApprovedAt` columns the `AddPlayerDataApproval`
  migration actually added — caught by reading `PlayerData.cs` directly
  rather than trusting the doc; also corrected a pre-existing, unrelated
  body/frontmatter version-number mismatch in both `implementation-
  document.md` (body said 0.51/2026-07-17 while frontmatter already said
  0.59/2026-07-19) and `design-document.md` (0.33/2026-07-19 vs.
  0.36/2026-07-20) — neither caused by this batch, both now in sync.
  **`docs/design-document.md` SCREEN-08 fixed:** REQ-714's own commit
  updated `SettingsScreen.tsx` to add a display-name edit form but never
  touched SCREEN-08's mock/description, which still showed only the
  admin-only link and delete-account flow; added the missing section
  (mock row, form behavior, confirms no new design tokens — verified
  directly against `SettingsScreen.css`'s diff). **`docs/backlog.md`:**
  added the four new entries below (S-055/056/057/
  058), sourced from the merged code and the implementing commits' own
  messages, following the existing S-052/053/054 convention;
  `design-document.md` §4 already referenced "S-055" by name for the
  grid-cell-sizing fix before this backlog entry existed, which is what
  surfaced the gap. No code or test files touched by this pass —
  documentation only. REQ-401, REQ-404, REQ-406, REQ-407, REQ-503,
  REQ-714, REQ-715, ADR-0032, ADR-0033.
- 2026-07-20 — `docs/requirements-document.md` (new REQ-714/715 entries;
  status flipped to Implemented by the doc-sync pass above),
  `docs/design-document.md` (SCREEN-08 gained the display-name form,
  added by the doc-sync pass above — the implementing commit updated
  `SettingsScreen.tsx` but not this doc),
  `docs/legal/privacy-policy-draft.md` (0.6 → 0.7, "What we collect" now
  notes a display name can be changed later, not only chosen at signup —
  found by the doc-sync pass above),
  `docs/decisions/0033-refresh-token-storage-localstorage.md` (new),
  `docs/backlog.md` (new
  S-058 entry) — **REQ-714 (edit display name from Settings) and REQ-715
  (persistent login via refresh token), both new.** `PUT
  /auth/display-name` (`AuthController.UpdateDisplayName`) reuses REQ-701's
  exact 1-30 character bound and `IUserRepository.DisplayNameExistsAsync`,
  now with an `excludeUserId` parameter so a no-op resubmission of the
  caller's own current name (including a pure-casing change) is never
  treated as a conflict against itself; `frontend/src/settings/
  SettingsScreen.tsx` hosts the edit form. `POST /auth/refresh`
  (`AuthController.Refresh`) exchanges a stored refresh token for a new
  access token, mediated through Supabase Auth exactly like `/auth/login`/
  `/auth/signup` (ADR-0013) — never a direct frontend-to-Supabase call —
  sharing `SupabaseAuthClient`'s request plumbing rather than a parallel
  implementation; `App.tsx` now stores the refresh token in `localStorage`
  alongside the access token and attempts a silent refresh on a missing/
  401'd access token before falling back to a full logout, with both
  tokens cleared on logout and account deletion. **ADR-0033** (new):
  `architecture-reviewer` was asked where the refresh token should live
  before any code was written — `localStorage`, matching the existing
  access-token pattern, was chosen over an httpOnly cookie specifically
  because this codebase has no CORS-credentials/cookie/CSRF infrastructure
  today and introducing it for one token would add more new surface than
  a one-person team's current threat model justifies; the XSS-exposure
  trade-off this accepts is recorded explicitly, with a revisit trigger
  (any third-party script surface, or a real incident). One deliberate
  omission flagged at implementation time, not a gap found later: no
  explicit server-side refresh-token revocation on logout — REQ-715's own
  acceptance criteria only require clearing the frontend's stored copy,
  and account deletion (REQ-710) already invalidates any outstanding
  refresh token as a side effect of deleting the underlying Supabase
  identity. `docs/backlog.md` gained a new S-058 entry (this pass).
  Backend and frontend suites extended (`UserRepositoryTests.cs`,
  `AuthEndpointTests.cs` including an exact-30-character boundary case,
  `SettingsScreen.test.tsx`, `App.test.tsx`). REQ-714, REQ-715, ADR-0033.
- 2026-07-20 — `docs/requirements-document.md` (REQ-211 status note
  revised, REQ-503 extended; status flipped to Implemented by the doc-sync
  pass above), `docs/design-document.md`
  (0.35 → 0.36), `docs/decisions/0032-wikidata-guess-time-fallback-also-
  auto-verified.md` (new, supersedes 0029), `docs/backlog.md` (new S-057
  entry) — **Wikidata guess-time
  fallback data is now auto-verified too, and REQ-503 finally gets a
  working "approve" action.** One day after ADR-0029 deliberately kept
  REQ-211's guess-time fallback lookup persisting `Confidence =
  "unverified"` so an admin could still spot-check that narrower,
  less-vetted path, the product owner decided all Wikidata-sourced data
  should be verified by default, including that path. **ADR-0032**
  (supersedes ADR-0029, whose own status line is updated to "Superseded by
  ADR-0032" rather than deleted): `WikidataLookupService.ConfidenceFor` now
  maps both `WikidataLookupOrigin` values to `"verified"`; the enum and its
  two call sites are kept, not collapsed away, since the distinction stays
  meaningful for logging even though it no longer drives a different
  `Confidence` value. A second run of the existing `verify-wikidata-
  player-data` CLI verb (idempotent, from ADR-0029) is still needed against
  the deployed database to flip the 2026-07-19→2026-07-20 window of
  fallback rows still sitting as `unverified` — flagged as a manual
  follow-up, not run as part of this change. Separately, REQ-503's "approve
  → verified" action — missing since S-012, a gap S-052/ADR-0029 narrowed
  the queue around but never actually built — now exists: `POST
  /admin/player-data/approve` (`AdminEndpoints`, Admin policy) is
  bulk-capable from the start (a single id is just the N=1 case), requires
  no `reason` field (unlike `PlayerOverride`'s "correct" action), and
  reports per-id success/failure rather than succeeding or failing an
  entire batch as one unit; new `PlayerData.ApprovedByAdminId`/`ApprovedAt`
  columns (`AddPlayerDataApproval` migration) mirror `PlayerOverride`'s
  existing `LockedByAdminId`/`LockedAt` audit shape. `AdminScreen.tsx`
  (SCREEN-04, `docs/design-document.md` updated in the same batch) adds a
  checkbox per row, "select all," a selected-count readout, and an
  "Approve selected" button, plus a persistent per-row results list.
  `docs/backlog.md` gained a new S-057 entry (this pass). REQ-211, REQ-503,
  ADR-0032.
- 2026-07-20 — `docs/requirements-document.md` (REQ-401/404/406/407 status
  notes revised; status flipped to Implemented by the doc-sync pass
  above), `docs/backlog.md` (new
  S-056 entry) — **Leaderboard scoring fairness (REQ-401/404/406/407) and
  a cosmetic scope-tab rename.** Two independent fairness fixes to S-053's
  leaderboard work, shipped together: (1) a league member who has never
  submitted a single `Guess` previously defaulted to a total of `0`, which
  under ADR-0021's lowest-wins golf model is the *best* possible score —
  such a member ranked #1 ahead of everyone who had actually played; now
  excluded from the ranked list entirely via a new `IGuessRepository
  .GetUserIdsWithAnyGuessAsync`, kept separate from the existing
  locked-only total query so a member active only in the current unlocked
  round isn't mistaken for never-played (REQ-401/404). (2) the active-round
  live estimate never credited an untouched cell, so a freshly-initiated
  grid read as unfairly low the moment a player made their first guess
  instead of starting near the theoretical max and counting down; now, for
  a round participant (≥1 guess anywhere in that round, ADR-0021's existing
  definition), every cell they've made zero guesses on at all contributes
  `MaxPointsPerCell` via `LiveRoundContributionService`, same as a
  locked-incorrect cell — a cell with one of two attempts used and still
  unresolved is unaffected (REQ-406/407). Also renamed SCREEN-03's scope
  tabs "This round (live)"/"Past rounds" → "Current Round"/"Previous
  Rounds" (`LeaderboardScreen.tsx`) — purely cosmetic, no REQ specifies
  exact tab wording. `docs/backlog.md` gained a new S-056 entry (this
  pass). REQ-401, REQ-404, REQ-406, REQ-407.
- 2026-07-20 — `docs/design-document.md` (0.34 → 0.35), `docs/backlog.md`
  (new S-055 entry) — **Mobile/tablet grid cell sizing fix: uniform column
  widths regardless of name length.** Reported via direct user screenshots
  of a 3×3 grid: `table-layout: auto` (the browser default, left in place
  above the 480px breakpoint since S-047/S-049) sizes each `<table>` column
  independently from the widest cell/header content in that column
  specifically, so a long team/player name ("Atletico Madrid") rendered
  its column visibly wider than a short one ("Sevilla") — most visible at
  mobile/tablet widths, still measurably present at desktop (measured
  92.75px/147.97px/141.59px across three columns at a 700px viewport
  before the fix). Fixed by making `table-layout: fixed` unconditional and
  giving every data column an explicit, equal `<col>` width via a new
  `grid-table__data-col` class (`Grid.tsx`'s `<colgroup>`), reusing
  existing width values (90px at 481-959px, 120px at ≥960px) rather than
  inventing new ones; also closed a `design-document.md` aspect-ratio
  violation the fix surfaced at 481-959px (cells were ~2.8:1, outside the
  documented 1:1–1.3:1 bound). Verified via real Chromium render at
  390/700/1280px with mixed-length headers: uniform column widths, no
  horizontal scroll, wrapped (not clipped) header text. No REQ change —
  visual bug fix against `design-document.md` §4's existing uniform-
  cell-size intent, not new product behavior. `docs/backlog.md` gained a
  new S-055 entry (this pass) — `design-document.md` §4 had already
  referenced "S-055" by name when it was updated as part of this same
  batch, before the corresponding backlog entry existed.
- 2026-07-19 — `docs/requirements-document.md` (0.71 → 0.72),
  `docs/architecture-document.md` (0.41 → 0.42),
  `docs/implementation-document.md` (0.58 → 0.59), `docs/backlog.md`
  (S-053/S-054 entries gain "Built as" notes) — implements REQ-406, REQ-407,
  REQ-408 (`docs/backlog.md` S-053/S-054), the live/per-round leaderboard
  feature whose requirements were drafted in the immediately preceding
  session. REQ-406/407 flip from "Proposed" to "Implemented (S-053)":
  `GET /leagues/global/leaderboard` now folds a live, recomputed-on-every-
  read contribution from the active round on top of the existing locked
  `SUM(FinalPoints ?? 0)`, and a new `GET
  /leagues/global/leaderboard/active-round` route exposes that same
  contribution as its own participant-only, standalone scope (404 "No
  active round" when none exists) — both share one computation, a new
  `ILiveRoundContributionService`/`LiveRoundContributionService`
  (`XGArcade.Core.Scoring`), resolving cells only through
  `IGameModuleResolver`/`IGameModule.GetCellIdsAsync` per ADR-0003. REQ-408
  flips to "Implemented (S-054)": a new nullable `Round.ClosedAt` column
  (`AddRoundClosedAt` migration) — executing the exact follow-up ADR-0022's
  own "Follow-up" section already anticipated, no new ADR needed — backs
  two new routes (`GET /leagues/global/leaderboard/closed-rounds`,
  paginated list, and `.../closed-rounds/{roundId}`, that round's locked
  leaderboard, with distinct 404/409 responses for not-found vs.
  not-closed-yet). Frontend: `LeaderboardScreen.tsx` (SCREEN-03) gained a
  three-way scope selector ("All-time" / "This round (live)" / "Past
  rounds"), reusing the existing "~N pts estimated" wording
  (`GridScreen.tsx`/`CellState.tsx` precedent) for the live scope's
  provisional framing — no new design token. **Two real bugs were found by
  `architecture-reviewer`/`quality-architect`'s pre-merge quality-gate pass
  and fixed before merge, not after:** (1) frontend — the live/past-rounds
  scopes' `useRef` "fetch once" guards never reset, so re-entering a scope
  after switching away silently showed indefinitely stale data; fixed to
  refetch on every genuine transition into the scope (previous-scope
  comparison) while still avoiding the original React StrictMode
  double-fetch race the guard existed to prevent, with new regression tests
  for the leave-and-return case. (2) backend —
  `RoundCloseService.CloseRoundAsync` originally persisted `ClosedAt`
  *before* `LockRoundScoresAsync` finished, which could let REQ-408's
  closed-round endpoint read a round as final while some guesses still had
  `FinalPoints == null`; reordered so `ClosedAt` is only set after locking
  completes successfully, with new tests covering both the failure and
  successful-retry paths. Also deduplicated `LeaderboardEndpoints.cs`'s
  four routes' identical requesting-user-resolution block into one helper
  (a `quality-architect` low-severity finding, fixed alongside the two
  above). `docs/architecture-document.md`: COMP-02's dependency on
  `IRoundRepository`/`ILiveRoundContributionService` — already accepted as
  a consequence in ADR-0031 — is now described as built, not hypothetical;
  §6.2a's global leaderboard flow diagram updated for all three routes; new
  COMP-03 status note on `Round.ClosedAt`. `docs/implementation-document.md`:
  `Round`'s entity sketch gains the `ClosedAt` field.
  `docs/backlog.md`'s S-053/S-054 entries gain "Built as" notes recording
  both quality-gate bugs and fixes, per this repo's convention for
  completed stories. Full backend suite: 465/465 passing. Full frontend
  suite: 170/170 passing, `tsc -b --noEmit`/lint clean. No new ADR beyond
  the already-existing ADR-0031 (governed this story's live-recompute
  approach directly) and ADR-0022 (whose own anticipated follow-up,
  `Round.ClosedAt`, this story executes). REQ-406, REQ-407, REQ-408.
- 2026-07-19 — `docs/requirements-document.md` (0.70 → 0.71),
  `docs/architecture-document.md` (0.40 → 0.41), `docs/decisions/0031-live-leaderboard-recomputed-on-every-read.md`
  (new), `docs/backlog.md` (new S-053, S-054 entries) — feature request,
  routed through `requirements-writer` first per instruction (real product/
  scoring decisions, not rendering fixes): make the leaderboard reflect
  live/provisional points while a round is in progress, not only after
  close, and add a per-round leaderboard view. Drafted as three new REQs
  rather than rewriting REQ-206/401/404 (whose existing definitions are
  unchanged, not superseded): **REQ-406** folds a live, recomputed-on-every-
  read contribution from the active round into the existing shared/
  per-league total; **REQ-407** exposes that same live contribution as its
  own standalone active-round-scoped leaderboard, reached from SCREEN-03 as
  an additional scope option, not a separate screen; **REQ-408** adds
  individually browsable past *closed* round leaderboards (locked-only, no
  live component), paginated per REQ-607's existing `cursor`/`pageSize`
  shape. REQ-206 and REQ-404 each gained a dated status note cross-
  referencing the new REQs rather than having their existing text silently
  rewritten. Deliberately does **not** touch REQ-405/S-027 ("leaderboard
  time-window resolutions") — that REQ's "round" already means the single
  most-recently-*closed* round only, is fully drafted, and is already
  implementation-ready; the product owner explicitly asked for it to be
  routed separately, not folded into this work. The three open product
  questions (does a live rank include still-changeable guesses and what
  happens when one flips before close; separate view vs. tab; bounded vs.
  unbounded past-round browsing) are resolved explicitly in the new REQs'
  own text, not left open: live figures recompute on every read with no
  snapshot (a not-yet-attempted cell in an active round contributes
  nothing — deliberately neither `0`, ADR-0021's "best score," nor
  `MaxPointsPerCell`, which only applies at close); per-round leaderboards
  are an additional scope/tab on SCREEN-03, not a new screen; past-round
  browsing reuses REQ-607's exact pagination shape rather than inventing a
  second convention. **ADR-0031** (new): `architecture-reviewer`, asked to
  assess REQ-406/407's "always live, never cached" requirement before any
  code is written, found this is a genuine architectural decision, not
  just a bigger instance of REQ-204's existing per-cell live-points
  pattern — it reverses `architecture-document.md` §6.2a's deliberate
  DB-side-aggregate leaderboard computation and narrows REQ-607/S-034's
  bounded-read-cost guarantee (the response page stays bounded; the cost
  to produce the full ranking behind it no longer is). ADR-0031 records
  that tradeoff explicitly — full live recompute chosen over a periodic
  snapshot/materialized view, a push-based incremental update on guess
  submission, or a short-TTL cache — with an explicit, observable revisit
  trigger (participant count, real-environment latency, or grid-size
  growth), matching the existing ADR-0016/0019/0021 "small now, revisit on
  evidence" pattern. `docs/backlog.md` gained two new Tier 0 stories
  queuing the actual implementation for a future session (**S-053** for
  REQ-406/407, **S-054** for REQ-408, depending on S-053 for the shared
  SCREEN-03 scope-selector) — per this repo's one-story-per-session rule
  and the product owner's own instruction, no `backend-implementer`/
  `ui-implementer` work was started in this session; this iteration is
  requirements + architecture decision only. REQ-406, REQ-407, REQ-408,
  ADR-0031.
- 2026-07-19 — `docs/decisions/0029-wikidata-sync-data-is-auto-verified.md`
  (new), `docs/requirements-document.md` (0.69 → 0.70),
  `docs/architecture-document.md` (0.38 → 0.39), `docs/backlog.md` (new S-052
  entry) — S-026's admin page gave `GET /admin/player-data/unverified` its
  first real UI caller, which surfaced that the review queue had reached
  52,782 rows (every Wikidata sync since S-006 — `Confidence` was never
  conditional on anything). ADR-0029: a routine sync (grid-generation
  cache-miss or cache-warming, `WikidataLookupOrigin.Sync`) now persists
  `Confidence = "verified"` directly; only REQ-211's guess-time fallback
  (`WikidataLookupOrigin.GuessTimeFallback`) still persists `"unverified"`.
  A new one-time CLI verb (`verify-wikidata-player-data`) bulk-cleared the
  pre-existing backlog to match. REQ-103, REQ-502, REQ-503 gained status
  notes describing the revision; REQ-211 unchanged (its fallback still
  writes `"unverified"`, exactly as before). REQ-502/503/103.
- 2026-07-19 — `docs/decisions/0030-mobile-hamburger-nav-and-settings-screen.md`
  (new), `docs/architecture-document.md` (0.39 → 0.40),
  `docs/implementation-document.md` (0.57 → 0.58) — added ADR-0030
  (renumbered from an initial ADR-0029 that collided with the
  Wikidata-auto-verify ADR above, merged to main first),
  recording the decision to collapse the header nav behind a mobile-only
  hamburger toggle (REQ-712) and consolidate the standalone "Delete
  account"/"Admin" links into one "Settings" screen (REQ-713), reversing
  `design-document.md` SCREEN-05's prior "no general profile/settings page"
  note. No architecture-document.md component/boundary change — frontend
  only. implementation-document.md §4's project structure gained `/nav`
  (`HeaderNav`) and `/settings` (`SettingsScreen`) folder entries. REQ-712,
  REQ-713, REQ-504, REQ-710, ADR-0030.
- 2026-07-19 — `docs/design-document.md` (0.33 → 0.34) — implemented
  REQ-712 (mobile hamburger nav toggle) and REQ-713 (Settings screen
  consolidating "Delete account"/"Admin" into one nav entry). Added
  SCREEN-07 (header nav mobile menu) and SCREEN-08 (Settings, hosting
  SCREEN-05's unchanged delete-account flow plus an admin-only link to
  SCREEN-04) with status notes on SCREEN-04/SCREEN-05 correcting the
  now-outdated "reached via a standalone top-level link"/"no general
  settings page exists" claims. §4 gained a new "Header nav breakpoint"
  note recording the choice to reuse the existing 480px narrow-phone value
  (not the 960px desktop-cap one) and why, plus that the mechanism is
  CSS-only (no JS viewport detection), matching the app's existing
  responsive approach. Frontend: `frontend/src/nav/HeaderNav.tsx`+`.css`
  (new), `frontend/src/settings/SettingsScreen.tsx`+`.css` (new),
  `frontend/src/App.tsx`/`App.css` updated to wire both in and drop the
  old flat `Leaderboard`/`Delete account`/`Admin`/`Log out` row;
  `frontend/tests/unit/App.test.tsx` updated for the new nav/Settings
  structure (existing REQ-710/REQ-504 cases re-pointed at "Settings," one
  new REQ-712 toggle case added). `AdminScreen`/`DeleteAccountScreen`
  themselves, and their own tests, are unchanged. REQ-712, REQ-713.
- 2026-07-19 — `docs/requirements-document.md` (0.68 → 0.69),
  `docs/architecture-document.md` (0.37 → 0.38) — doc-sync for S-026 (admin
  UI page + round control + user deletion), which was fully implemented,
  tested, and merged with REQ-504/505/506 still marked "Proposed." Flipped
  all three to `Status: Implemented (Tier 0, S-026)` and described what was
  actually built: `AdminScreen.tsx`'s three sections and Production-absence
  detection (REQ-504); `AdminManagementEndpoints`' round-control routes,
  their reuse of `IRoundCloseService` (REQ-205), and a noted deliberate
  deviation from the drafted criteria (the active-round probe returns `200
  {hasActiveRound, round}` rather than a not-found response, REQ-505); and
  the user-deletion endpoint's reuse of `IAccountDeletionService`
  (REQ-710, REQ-506). Architecture doc gained status notes recording
  `AdminManagementEndpoints` as a second caller of COMP-01's
  `IAccountDeletionService` and a third caller of COMP-03's
  `IRoundCloseService` (no new data-access path either way), plus a §7 note
  that ADR-0006's fail-closed pattern has, for the first time, been reused
  by an admin-facing (not test-only) endpoint group — a growth in scope of
  an existing decision, not a new one, so no new ADR. `docs/design-document.md`
  (0.32 → 0.33) was already updated earlier in the same branch (SCREEN-04's
  mock rewritten to match what was actually built) — noted here for the
  record, not re-touched. `docs/backlog.md`'s S-026 entry also gained its
  own "Built as" paragraph, matching every other completed story's
  convention. REQ-504/505/506.
- 2026-07-19 — `design-document.md` (v0.32, new S-051 status note under
  SCREEN-01a plus superseding marks on the 2026-07-18 REQ-214 note and the
  S-049 §4 note, both of which described `object-fit: cover` as
  current/unchanged), `requirements-document.md` (v0.68, new REQ-214 status
  note plus a "Test level" addition), `docs/backlog.md` (new S-051 entry),
  `frontend/src/grid/{CellState.css,CellState.test.tsx}` — S-051, a direct
  product decision, not a discovered bug (unlike S-047 through S-050,
  which were each root-caused from a report of broken/ugly behavior): the
  user asked directly "I want the full picture to be visible within the
  cells, so they are not cut off," was shown the trade-off explicitly via
  `AskUserQuestion` — "Crop photo to fill the cell completely (today's
  behavior)" vs. "Show full photo, allow empty space (letterbox)" — and
  chose letterboxing. Mechanical change: `.cell-state__photo-img`'s
  `object-fit` `cover` → `contain`, so the whole photo always renders,
  scaled to fit, never cropped, at the cost of a background strip on two
  opposite sides whenever a photo's aspect ratio doesn't match the cell's.
  Made load-bearing rather than left incidental: `.cell-state--photo` gets
  its own explicit `background-color: var(--color-surface-card)` — before
  this story that box had no background of its own and relied on
  `.grid-cell`'s (Grid.css) background showing through its transparent box,
  true but untied to this element, so a future `.grid-cell` state-treatment
  change could have silently changed the letterbox color without anyone
  touching photo code at all. Confirmed (not assumed) via an independent
  review pass against `frontend/src/index.css` that `--color-surface-card`
  is `#ffffff`, exactly the value `overlay-scrim`'s existing contrast math
  already treats as its worst case, so no new token or contrast
  recalculation was needed; REQ-214's fixed-cell-footprint guarantee is
  unaffected (the mechanism is `inset: 0` + explicit `width`/`height`, never
  the fit mode). `CellState.test.tsx`: existing `object-fit` assertion
  updated `'cover'` → `'contain'`; one new test asserts
  `.cell-state--photo`'s `background-color` resolves to the `surface-card`
  token. Full Vitest suite 129/129 passing (was 128); `tsc -b --noEmit` and
  `oxlint` both clean. No `architecture-document.md`/
  `implementation-document.md` change — checked directly, not assumed:
  neither doc references `object-fit`, `CellState.css`, or the
  `surface-card` token at all, confirming this is a pure design/requirements
  concern with no component boundary, data flow, or data model touched. No
  ADR — two independent review passes (architecture-reviewer,
  quality-architect) already concluded no structural/component-boundary
  choice was made here, same CSS/layout-only precedent as S-040/S-041/
  S-047/S-048/S-049/S-050; this one is a recorded product *decision* via
  `AskUserQuestion` rather than a bug fix, but that distinction doesn't
  change the ADR calculus. REQ-214 ref.
- 2026-07-19 — `requirements-document.md` (v0.67, new REQ-214 status note),
  `design-document.md` (v0.31, new S-050 note under §4's "Grid cell photo
  fill" heading), `docs/backlog.md` (new S-050 entry with full before/after
  measurements), `frontend/src/grid/{Grid.css,CellState.css,CellState.tsx,
  Grid.test.tsx}` — S-050, a fourth round of direct user feedback on
  `/grid`, this time with real screenshots at both a mobile and a "Request
  desktop site" viewport: "see how they are not tall enough to show full
  pictures.. we need to make sure that the pictures actually fits the
  cell." Root-caused via `getBoundingClientRect` on a real Chromium render
  before any CSS was touched (a prior static read of `Grid.css`/
  `CellState.css` found nothing obviously wrong, since the S-047/REQ-214
  mechanism as documented *should* work). Actual cause, one level further
  out than expected: a correct cell's photo (`.cell-state--photo`,
  `CellState.css`) bled through `.grid-cell`'s (the button's) own padding
  exactly as already documented, but `.grid-cell` itself sits inside
  `.grid-table__cell` (the `<td>`), which has its own, separate,
  never-bypassed padding — so the photo always stopped short of the cell's
  actual bordered edge by exactly that amount, symmetric on all four sides
  (4px below 960px, 12px at/above it), not literally bottom-only as first
  described (most visually obvious where two photo cells stack vertically
  and that gap doubles across the shared row border). A first fix
  (`.grid-table__cell:has(.cell-state--photo) { padding: 0; }`) was tried
  and rejected after real-browser verification found it would tie
  `.grid-cell`'s own rendered size to whether a photo is *currently*
  showing — `CellState.tsx` unmounts `.cell-state--photo` on image load
  failure, so that approach would have made the button visibly resize the
  moment an already-shown photo failed to load, exactly the shift REQ-214's
  "constant footprint regardless of load failure" guarantee forbids
  (confirmed via a deliberately-broken photo URL before rejecting it).
  Shipped fix instead: move `position: relative` (the abs-positioning
  containing block for `.cell-state--photo`'s `inset: 0`) from `.grid-cell`
  up to `.grid-table__cell` — one DOM level further out, past both padding
  layers — with no change to either element's own `width`/`height`/padding
  rules, so `.grid-cell`'s own box stays governed solely by its own
  unconditional CSS regardless of photo presence/load outcome (verified:
  identical computed width/height/padding with and without a photo
  present, and pixel-identical `getBoundingClientRect()` before/after the
  same broken-photo-URL scenario). Remaining gap after the fix: 0.5px on
  every side at both breakpoints — this rule's own 1px border split by
  sub-pixel rounding, i.e. the cell's actual visible edge. `CellState.css`
  and `CellState.tsx` changes in this diff are comments only, describing
  the new containing block accurately — no property/behavior change in
  either file. No `architecture-document.md`/`implementation-document.md`
  change (CSS-only, no component boundary or data flow touched) and no ADR
  (same CSS/layout-only precedent as S-040/S-041/S-047/S-048/S-049; the
  rejected `:has()` approach never shipped, so there's nothing to revert in
  an ADR sense either). `requirements-document.md`'s new REQ-214 status
  note clarifies the "filling the cell" acceptance criterion was, through
  S-049, only true up to this same measured gap, and that the
  footprint-invariance bullet's load-failure clause was re-verified (not
  just assumed unaffected) as part of this fix. `Grid.test.tsx` gained 2
  new tests (a raw-stylesheet check that `.grid-table__cell` now carries
  `position: relative` and `.grid-cell` no longer does, and a rendered-DOM
  check that `.grid-cell`'s computed width/height/padding are identical
  with and without a photo). Full Vitest suite 128/128 passing (was 126);
  `tsc -b --noEmit` and `oxlint` both clean. No `tests/e2e/play-grid.spec.ts`
  change needed — its `cell.boundingBox()` assertions target `.grid-cell`
  via `data-testid`, the exact element this fix keeps load-outcome-
  independent (confirmed by reading the file). REQ-214 ref.
- 2026-07-19 — `design-document.md` (v0.30, new S-049 note extending §4's
  S-047 aspect-ratio rule with a concrete desktop target size),
  `docs/backlog.md` (new S-049 entry), `frontend/src/grid/{Grid.css,
  CellState.css,Grid.test.tsx}` — S-049, a third round of direct
  user feedback on `/grid` after mobile was confirmed good: "if i switch
  to desktop view in the mobile it still looks weird.. feels like the grid
  could be larger? and the cell + picture should look nice." Root cause
  (verified, not guessed): S-047's `.grid-table__cell` `min-width`/`height`
  at `≥960px` (64px, from S-040) fixed cells stretching into flat
  rectangles but was only ever a *floor*, never a deliberate *target* — a
  Tier-0 grid's 3-5 columns never need more than that floor, so the grid
  rendered at its smallest reasonable size (~300-400px) inside `.app`'s
  1200px desktop cap. Fixed by raising the same floor the table's
  shrink-to-fit column sizing already keys off, not by switching mechanism:
  `min-width`/`height` 64px → 120px, padding `--space-2` → `--space-3`,
  scoped to the existing `≥960px` breakpoint only (481-959px and ≤480px
  unaffected). A matching `CellState.css` change bumps the photo-overlay's
  revealed name/points type (12px/10px → 15px/12px, also `≥960px`-scoped) —
  S-047's mobile-tuned sizes read undersized once the cell nearly doubled,
  the same feedback from a different angle. This is pure visual/layout
  polish, not a behavior change: no REQ's acceptance criteria depends on a
  specific cell pixel size (checked directly — the only place 44px/64px
  appear in `requirements-document.md` is inside a narrative "Built as"
  implementation-history note under REQ-204, not phrased as a Given/When/
  Then criterion), so `requirements-document.md` is deliberately untouched
  this time, unlike S-047/S-048 which each narrowed a REQ's actual
  acceptance criteria. No `architecture-document.md`/
  `implementation-document.md` change (frontend CSS/layout only, no
  component boundary or data-flow touched) and no ADR (same CSS/layout-only
  precedent as S-040/S-041/S-047/S-048). Real-browser verification: a
  temporary, not-committed Vite + Playwright harness (Chromium at
  `/opt/pw-browsers`, deleted before finalizing, same approach S-047/S-048
  used) confirmed a 3×3 grid renders ~490×406px and a 5×5 grid ~787×646px
  at a 1280px viewport (both inside the 1200px cap, cells ~1.14:1 —
  square), the fixed-cell-footprint guarantee (REQ-214) still holds
  (pixel-identical bounding box before/after a reveal click), and a
  deliberately long name still clamps to one ellipsis-truncated line with
  no clipping at the larger size. `Grid.test.tsx` gained 2 new tests
  reading `Grid.css`'s raw source text rather than computed style, since
  jsdom doesn't apply `@media`-scoped rules at all (confirmed directly:
  `window.matchMedia` isn't implemented in this jsdom version). Full
  Vitest suite 126/126 passing (was 124); `tsc -b --noEmit` and `oxlint`
  both clean. No `tests/e2e/play-grid.spec.ts` change needed — its cell-box
  assertions are all relative before/after comparisons, never hardcoded
  pixel values (confirmed by reading the file). No REQ/ADR refs — visual
  polish only.
- 2026-07-19 — `requirements-document.md` (v0.66), `design-document.md`
  (v0.29), `docs/backlog.md` (new S-048 entry, by the implementing
  session — see that entry's own note), `frontend/src/grid/{CellState.tsx,
  CellState.css,CellState.test.tsx}`, `frontend/src/index.css` (comments
  only), `frontend/tests/e2e/play-grid.spec.ts` (comments only) — S-048,
  a further direct-user-feedback simplification of REQ-214's photo-cell
  overlay on top of S-047 (just merged): "at rest, only picture. on click
  name + points only in an overlay." Before this story, a correct cell with
  a photo showed a checkmark+points overlay unconditionally at rest
  (S-041/S-047's shared behavior with the no-photo case) and only added the
  name on click; after this story, a photo cell shows the bare photo and
  nothing else at rest, and clicking/tapping it reveals an overlay with the
  name and points only — no checkmark, ever, for a photo cell. This is a
  real narrowing of what REQ-204 guarantees is always visible without
  clicking (before: checkmark+points, for every correct cell, photo or not;
  after: that guarantee no longer holds for the photo case, where the
  photo's own presence is the only always-visible "this cell is done"
  signal) and of what REQ-212's reveal shows for a photo cell specifically
  (name+points, not name alone) — both got dated 2026-07-19 status notes
  rather than a silent rewrite of the existing Given/When/Then text, and
  `design-document.md` SCREEN-01a's mocks for both states 1 and 4's photo
  case were redrawn to match, with the trade-off (score signal lost at
  rest, "done" signal retained via the photo) recorded plainly as the
  user's own explicit choice, not an invented justification. Verified this
  against the actual code diff rather than trusting the implementing
  agent's doc updates on faith: confirmed `CellState.tsx`'s photo branch no
  longer builds or reuses the shared `overlayContent`, renders `<CellPhoto>`
  unconditionally, and only mounts `.cell-state__overlay` (plain name span +
  existing points `<p>`, no `Row` call, so structurally no checkmark and no
  badge dock) when `revealed`; confirmed the no-photo branch is untouched;
  confirmed `CellState.css` removed exactly the three now-unreachable
  photo-variant rules (`.cell-state__row` gap, `.cell-state__icon`
  size, `.cell-state__icon--correct` color) with removal notes rather than
  silent deletion, while `--color-accent-green-scrim` (`index.css`,
  design-document.md §2) is kept defined but now documented as dormant, not
  deleted, per this repo's existing "document, don't drop" pattern for
  superseded values; confirmed `CellState.test.tsx`'s photo-reveal describe
  block was rewritten in place (not left stale) to assert the new
  invariants — nothing overlaid at rest, name+points-only on reveal, no
  checkmark in either state, structural (not merely CSS `display: none`)
  absence of `.cell-state__row`/icon/badge-dock inside a photo cell; and
  confirmed `play-grid.spec.ts`'s two descriptive-comment updates (the
  correct-guess-at-rest assertion, and the S-047 badge-dock-hidden
  assertion) accurately describe the new no-DOM-element mechanism rather
  than S-047's CSS-hide mechanism — including one stale reference ("CSS-
  hidden") the orchestrator corrected in the working tree after a
  `quality-architect` pass flagged it, ahead of this doc-sync pass. No
  `architecture-document.md` or `implementation-document.md` change:
  checked both against their own `update_when` triggers directly against
  the diff (frontend component-internal TSX/CSS + tests only, no new
  library, no data-model/project-structure change, no component
  responsibility or data-flow change) rather than deferring to the prior
  no-op precedent alone. No ADR — the orchestrator's own read plus an
  independent `architecture-reviewer` pass found no `XGArcade.Core`/game-
  module boundary touched, same precedent as S-040/S-041/S-047. Full
  Vitest suite 124/124 passing (unchanged count from S-047's own final
  tally — tests rewritten in place, not net-added); `tsc -b --noEmit` and
  `oxlint` both clean (verified by the orchestrator in this sandbox; not
  re-run here). REQ-204, REQ-212, REQ-214.
- 2026-07-19 — `requirements-document.md` (v0.65), `docs/backlog.md`,
  `design-document.md` (v0.28, by the implementing session — see that
  entry's own note), `frontend/src/grid/{CellState.css,CellState.test.tsx,
  Grid.css}`, new `frontend/src/grid/Grid.test.tsx`,
  `frontend/tests/e2e/play-grid.spec.ts` — S-047, two direct-user-feedback
  UI fixes on `/grid`, both root-caused before scoping: (1) REQ-214's photo
  overlay (`.cell-state__overlay`) covered ~40-45% of a real mobile cell
  (90-110px), against the design doc's original ~30% intent — tightened
  padding (`--space-1`/`--space-2`, down from a uniform `--space-2`) and
  smaller photo-variant type (checkmark 11px, meta 10px, name 12px/1.2)
  bring the at-rest overlay toward a ~35% target. (2) `Grid.css`'s
  `.grid-table` used `width: 100%` unconditionally, which combined with the
  browser's default `table-layout: auto` above 480px stretched a Tier-0
  3-column grid's cells into flat rectangles at any wide viewport (desktop,
  or a phone's "Request desktop site") — fixed with `width: auto; margin: 0
  auto`, letting the table shrink-to-fit per CSS2.1's automatic table-layout
  algorithm; the ≤480px breakpoint keeps S-040's `width: 100%` +
  `table-layout: fixed` unchanged. Two further, more severe bugs were found
  during this story's own required real-browser verification and fixed in
  the same pass, and are the reason this entry touches
  `requirements-document.md` (not just visual polish): at a typical Tier-0
  mobile photo cell's content width, the revealed row's four flex items
  (row badge, name, column badge, checkmark) didn't fit on one line for
  *any* real name — "Thierry Henry" rendered completely invisible once
  revealed, and a longer name could get silently clipped from the *top* by
  `.cell-state--photo`'s pre-existing `overflow: hidden`, showing an
  unreadable middle fragment. Fixed, on the photo variant only, by hiding
  the badge dock on reveal and clamping the name to a single
  ellipsis-truncated line (`-webkit-line-clamp: 1`) — this is a genuine
  narrowing of REQ-212's "reveals the canonical name and its badge dock"
  acceptance criterion for the photo case specifically (the no-photo case
  is unaffected), not pure implementation detail, so both REQ-212 and
  REQ-214 got a dated status note recording the supersession rather than
  silently editing the existing Given/When/Then text away. No
  `architecture-document.md` or `implementation-document.md` change —
  frontend-component-internal CSS/layout only, no component
  responsibility, data flow, or data-model/tech-stack change; confirmed
  against both docs' own `update_when` triggers. No ADR —
  `architecture-reviewer` already ran during the story's own quality gate
  and found no `XGArcade.Core`/game-module boundary or data-flow touched,
  same precedent as S-040/S-041. Full Vitest suite 124/124 passing (was
  116 before this story per `docs/backlog.md`'s S-047 entry); `tsc -b
  --noEmit` and `oxlint` both clean; `play-grid.spec.ts`'s existing
  REQ-212 badge-dock assertion updated to branch on photo presence rather
  than unconditionally expecting the badge dock visible after reveal (not
  executed in this sandbox — no `dotnet`/Postgres available here, logic-
  reviewed only, same gap recorded for this file in S-041's entry).
  REQ-212, REQ-214.
- 2026-07-19 — `design-document.md` (v0.27), `frontend/src/index.css`,
  `frontend/src/grid/CellState.css`, `frontend/src/grid/CellState.test.tsx` —
  REQ-214, direct user feedback: the checkmark overlaid on a correct cell's
  photo scrim is now green, not gold (the points value beside it, and every
  other correct-checkmark instance in the app, stays gold — this is a
  narrow, one-off exception, not a general recolor). Neither existing green
  token cleared WCAG AA's 4.5:1 floor against the scrim's worst-case
  blended background (`accent-green` measures 3.49:1; `accent-green-text`,
  being darker, fails further) — added a new token, `accent-green-scrim`
  (`#23B874`, same hue/saturation as `accent-green`, lightness raised to
  43%), measured at 4.65:1 against the same `rgb(51, 56, 53)` backdrop
  `overlay-scrim`'s own gold math uses (one point of lightness lower, 42%,
  drops to 4.46:1 and fails). `design-document.md` §2 documents the full
  derivation plus a plain acknowledgment that this breaks the "green means
  live, gold means settled/correct" convention for this one glyph,
  deliberately, at the user's explicit request. `CellState.css`'s merged
  `.cell-state--photo .cell-state__icon--correct, .cell-state--photo
  .cell-state__meta` rule (from the immediately-preceding 94%→89% cleanup
  commit) is split back into two — the icon gets the new green token, the
  meta rule keeps `accent-gold`. `CellState.test.tsx`'s REQ-214
  gold-pairing test updated to check the points value alone; a new test
  added asserting the icon/meta colors now differ and the icon specifically
  uses `accent-green-scrim`. Full Vitest suite passes (117/117). Verified in
  a real Chromium browser: seeded a test round via
  `/internal/test-data/seed-guessable-round`, submitted the correct guess
  via the API, injected a data-URI test photo directly via SQL
  (`UPDATE "Players" SET "PhotoUrl" = ...`), and screenshotted both at-rest
  and revealed states — checkmark reads clearly green against the scrim,
  distinct from the still-gold points value beside it, not a jarring
  mismatch.
- 2026-07-18 — `design-document.md` (v0.26), `frontend/src/index.css` —
  lightened REQ-214's `overlay-scrim` token from `rgba(26, 31, 28, 0.94)` to
  `rgba(26, 31, 28, 0.89)` after direct user visual feedback that the
  original 94% opacity read as a heavy black shadow over the photo rather
  than a scrim. Re-did the relative-luminance contrast math for the new
  value against the same worst-case backdrop (pure-white photo showing
  through): at 89%, the blended background is `rgb(51, 56, 53)`, giving
  `accent-gold` (the checkmark/points color) a 4.65:1 contrast ratio and
  `surface-card`/white (the revealed-name color) 11.99:1 — both still clear
  the 4.5:1 AA floor; `accent-gold` is the binding constraint and 89% is the
  lightest whole-percent value that clears it (88% measures 4.49:1 and
  fails). `CellState.css`/`CellState.tsx` unchanged — they reference
  `--color-overlay-scrim` and the `accent-gold`/`surface-card` pairing
  directly, no hardcoded opacity to update there. Full Vitest suite
  unaffected (`CellState.test.tsx`'s REQ-214 contrast-pairing tests assert
  token/pairing usage, not a hardcoded opacity number). Verified visually in
  a real Chromium browser against a seeded test round with a data-URI photo
  — scrim reads noticeably lighter, checkmark/points and revealed name both
  still clearly legible.
- 2026-07-18 — `design-document.md` (v0.25), `backlog.md` (S-046) —
  implemented REQ-214's
  photo-decoupled-from-reveal status note (frontend half, same day as the
  requirements-doc revision): `CellState.tsx`/`CellState.css` now show a
  correct cell's photo automatically at rest, filling the cell, independent
  of REQ-212's click/tap reveal (which continues to gate only the name/badge
  dock). Closed the open gap the requirements revision flagged — §2 had no
  overlay/scrim token for text-or-icon-on-photo contrast — by adding
  `overlay-scrim` (`rgba(26, 31, 28, 0.94)`, verified against a worst-case
  pure-white photo showing through), and documented that on this dark
  backdrop the *lighter* `accent-gold`/`surface-card` tokens (not the
  darkened `accent-gold-text`/near-black `text-primary` used everywhere else
  in this document) are the ones that actually clear the contrast floor —
  the `surface-card`-for-the-revealed-name half of that was found only via
  this session's own required real-browser verification (name was
  illegible against the scrim with `text-primary`), not the initial
  contrast-math pass, which only covered the checkmark/points explicitly
  named in REQ-214's acceptance criteria. §7's matching open question marked
  resolved. Also renamed the old `.cell-state__avatar` 18px-circle class to
  `.cell-state__photo-img` (full-cell-bleed, absolutely positioned against
  `.grid-cell`'s padding edge so it ignores that button's own padding and
  fills to its actual corners) — `Grid.css`'s `.grid-cell` gained
  `position: relative` as the positioning context this needs.
  `CellState.test.tsx`/`GridCell.test.tsx`'s REQ-214 blocks rewritten for
  the new independent-of-`revealed` behavior (photo-at-rest-without-a-click,
  reveal-adds-name-without-touching-photo, hide-again-photo-stays,
  no-photo/null/load-failure cases re-verified unaffected, declared-CSS
  mechanism tests replacing the old fixed-18px-slot ones);
  `tests/e2e/play-grid.spec.ts`'s dimension-invariance check now captures
  the cell's box right after lock (the new at-rest photo moment) in
  addition to after reveal. Full Vitest suite (116 tests) and full
  Playwright E2E suite (4 tests, real Postgres + real Chromium) both green;
  real-browser check of the photo-filled cell (data-URI test photo, since
  this sandbox has no network path to Wikidata) confirmed visually — REQ-214
- 2026-07-18 — `docs/backlog.md` — added an addendum to S-045's entry
  covering the malformed-QID crash and fix below (`quality-architect`
  flagged the entry as reading like the story shipped clean when it
  actually had a crash-and-fix history across two commits); also corrected
  the entry's stale "no real Postgres/no network available" caveat — this
  session did independently verify both live (Postgres install + a real
  Wikidata-network-blocked reproduction), that just hadn't happened yet
  when S-045's own entry was written
- 2026-07-18 — `NOTES.md` only (no requirements/architecture/implementation
  doc changed — this is a bug fix, not a behavior/acceptance-criteria
  change) — fixed a crash in `backfill-player-photos` (REQ-214, S-045)
  found by running it against a real Postgres database seeded with
  `/internal/test-data` fixtures: a malformed `Player.WikidataQid` made
  `WikidataClient.QueryPlayerPhotosByQidsAsync` throw a plain
  `ArgumentException`, which `PlayerPhotoBackfillService.BackfillAsync`'s
  `catch (WikidataQueryException)` never caught, crashing the whole run
  instead of the documented log-and-continue behavior. Extracted QID-format
  validation into a shared `WikidataQid.IsValid` helper
  (`XGArcade.DataSync.Wikidata`) and had `PlayerPhotoBackfillService`
  pre-filter each batch with it before calling
  `QueryPlayerPhotosByQidsAsync`, logging one warning per skipped player,
  rather than a whole batch paying for one bad row.
  `WikidataClient`'s own `ArgumentException` contract on all three
  QID-validating methods is unchanged. New tests:
  `PlayerPhotoBackfillServiceTests
  .REQ214_BackfillAsync_BatchContainsMalformedWikidataQid_SkipsThatPlayerButBackfillsTheRestWithoutThrowing`
  and `..._EveryPlayerInBatchHasMalformedWikidataQid_CompletesWithoutThrowing`.
  Full backend suite re-run in this environment: 411 passed, 0 failed,
  0 skipped, across all five backend test projects — REQ-214
- 2026-07-18 — `requirements-document.md` (v0.63), `implementation-document.md`
  (v0.57), `backlog.md` (S-045) — added a one-off `backfill-player-photos`
  CLI verb (`PlayerPhotoBackfillService`, `XGArcade.DataSync.Wikidata`) to
  fill `Player.PhotoUrl` for every already-existing player row REQ-214's
  P18 addition never revisits (`WikidataLookupService
  .GetOrCreatePlayerAsync` only sets it at row-creation time) — an
  idempotent backfill instead of the destructive `purge-player-pool` +
  `warm-player-cache` wipe-and-rerun the user explicitly rejected. New
  `IWikidataClient.QueryPlayerPhotosByQidsAsync` (batched, direct-by-QID
  SPARQL VALUES lookup, throws `WikidataQueryException` on failure per
  `docs/coding-guidelines.md`'s 2026-07-18 error-handling guideline) and
  new `IPlayerStoreRepository.GetPlayersMissingPhotoAsync`/
  `UpdatePlayerPhotosAsync`. Squarely inside ADR-0024's existing "CLI verb,
  never HTTP/background task" decision — no new ADR. New workflow
  `backfill-player-photos.yml` (`workflow_dispatch` only). Tests:
  `REQ214`-named, added to `WikidataClientTests.cs`,
  `PlayerStoreRepositoryTests.cs`, and a new `PlayerPhotoBackfillServiceTests.cs`.
  Full backend suite run in this environment: 409 passed, 0 failed, across
  all five backend test projects (`dotnet`/`dotnet test` were available
  this session, unlike some prior stories) — no real Postgres available, so
  only the InMemory-provider path was exercised; the new SPARQL query shape
  could not be verified against live `wikidata.org` (no network access) —
  REQ-214
- 2026-07-18 — no docs touched (code-only refactor) — extracted
  `WikidataClient`'s two intersection query builders' shared SPARQL
  header/predicates/footer into a new `BuildIntersectionQuery(candidateClauses)`
  helper, per `quality-architect`'s REQ-214 quality-gate suggestion: both
  builders had to be hand-edited identically to add `P18`, which is exactly
  the kind of place a future addition could silently land in only one.
  `BuildCountryClubIntersectionQuery`/`BuildClubClubIntersectionQuery` now
  supply only the candidate-matching clauses that actually differ between
  them. Verified via the existing `WikidataClientTests` query-content
  assertions (all still pass unmodified) rather than new tests, since this
  is a pure internal refactor with no behavior change — REQ-101/103/113/214
- 2026-07-18 — `architecture-document.md` (v0.37), `docs/decisions/0028-*.md`
  (new) — added ADR-0028, formalizing REQ-214's `Player.PhotoUrl` (not
  `PlayerAttribute`) placement decision per `architecture-reviewer`'s
  quality-gate ruling: single-valued Wikidata properties belong on `Player`
  going forward, with the accepted trade-off spelled out explicitly (no
  `PlayerOverride` correction path for `Player`-level fields, acceptable
  here only because a photo carries no correctness weight) — REQ-214,
  COMP-06
- 2026-07-18 — `requirements-document.md`, `backlog.md`, `design-document.md`
  — REQ-214 (photo reveal on a locked, correct cell) frontend half (S-044),
  landed in parallel with the backend half (S-043): `CellState.tsx`/
  `GridCell.tsx` render an optional player photo alongside the REQ-212 name
  reveal, in a fixed 18px avatar slot reusing the existing badge-dock
  "small" size (no dedicated avatar token exists in §2 yet — flagged as an
  open item, not invented ad hoc); falls back to exactly today's text-only
  reveal with no broken-image icon whenever no photo is available.
  `frontend/src/lib/types.ts`'s `resolvedPlayerPhotoUrl` field name was
  written before the backend DTO was confirmed and checked afterward to
  match exactly. `vite.config.ts` test config gained `css: true` so
  Vitest/jsdom assertions can check real computed CSS dimensions (needed
  for a genuine layout-regression test, not just a snapshot) — verified
  this doesn't change any existing test's outcome first. Real-browser
  (Playwright) verification was attempted and could not complete in this
  sandbox (chromium download blocked by the outbound proxy), flagged rather
  than silently skipped — REQ-214/S-043/S-044

- 2026-07-18 — `requirements-document.md` (v0.61), `implementation-document.md`
  (v0.56), `backlog.md` — REQ-214 backend half (S-043): `WikidataClient`'s
  two intersection query builders now fetch Wikidata's `P18` (image)
  `OPTIONAL`, carried through `WikidataPlayerMatch` and
  `WikidataLookupService` into a new `Player.PhotoUrl` column
  (`AddPlayerPhotoUrl` migration), exposed additively alongside
  `ResolvedPlayerName` in both `POST .../guesses`' and `GET /rounds/current`'s
  reveal responses. Deliberately a `Player` column, not `PlayerAttribute` —
  see `Player.PhotoUrl`'s doc comment and S-043's backlog entry for why;
  flagged for `architecture-reviewer` as a placement decision that could
  reasonably have gone the other way. Frontend rendering is a separate,
  not-yet-delegated task. `P18`'s Special:FilePath URL shape and the
  migration are both unverified against a live environment (no
  `wikidata.org`/`dotnet` access) — flagged for manual verification —
  REQ-214
- 2026-07-18 — `docs/legal/privacy-policy-draft.md` (v0.5) — added a
  Wikimedia Commons third-party-CDN disclosure (same shape as the existing
  Google Fonts entry) ahead of REQ-214's frontend half actually shipping,
  since the backend now stores/serves a photo URL that a browser will
  eventually load directly from `commons.wikimedia.org`
- 2026-07-18 — `coding-guidelines.md` (v0.5) — new error-handling
  guideline: swallow-to-empty external-client contracts are only valid
  where failure and no-data must be treated identically (interactive
  REQ-103-style paths); batch jobs whose success metric is the row count
  must throw. Promoted from the S-032 `import-player-name-index`
  silent-exit-0 incident (NOTES.md 2026-07-18), per the doc's own
  "recurring review comment becomes a guideline" trigger — REQ-207
- 2026-07-18 — `implementation-document.md`, `requirements-document.md`,
  `backlog.md`, `MVP-SCOPE.md`, `NOTES.md` — S-032 bug follow-up:
  `import-player-name-index`
  imported 0 rows in production because the player-pool query's
  `ORDER BY`/`OFFSET` pagination hit WDQS's hard ~60s server-side timeout
  on every page and the swallowed timeout read as end-of-data. Replaced
  with birth-year slicing (`QueryPlayerPoolBirthYearAsync`, 1939 → current
  year, no `ORDER BY`/`LIMIT`/`OFFSET`) plus a fail-loud contract
  (`WikidataQueryException`, per-slice retries, run fails red if any slice
  fails); dropped the never-read `PhotoUrl` column/P18 fetch
  (`RemovePlayerNameIndexPhotoUrl` migration). Bug fix within COMP-07's
  existing responsibility — no ADR, per the S-042 truthy-P54 precedent —
  REQ-207/ADR-0007/ADR-0025
- 2026-07-17 — `MVP-SCOPE.md` — doc-sync pass over the S-032 diff
  (REQ-207/ADR-0007/COMP-10): the Tier 0 "Guessing" bullet still said
  "plain text input, no autocomplete... defer `PlayerNameIndex`/ADR-0007
  entirely" — stale now that autocomplete/`PlayerNameIndex` actually
  shipped. Rewrote it to describe what's built and point at the Tier 1
  section for detail; updated that Tier 1 section's own S-032 entry from
  "queued" to "built, 2026-07-17" with the shipped shape (`PlayerNameIndexImporter`,
  `GET /players/autocomplete`, `GuessInput.tsx`'s debounced suggestion
  list), matching the existing "trigger hit and pulled forward" pattern
  already used there for REQ-211/S-031. No frontmatter to bump — this file
  has none. Checked `docs/requirements-document.md`,
  `docs/architecture-document.md`, `docs/implementation-document.md`,
  `docs/backlog.md`, `docs/design-document.md`, and `docs/legal/*.md`
  against the full S-032 diff independently: all found already accurate
  (the implementing agent's own doc updates, plus the later id-space
  quality-gate fix below, hold up) — `docs/legal/*.md` specifically needs
  no change since `PlayerNameIndex` stores only public Wikidata data about
  footballers (name, birth year, nationality), already covered generically
  by the privacy policy draft's existing "Data sources for gameplay
  content" section, and the autocomplete query string itself is never
  persisted (an in-memory `IPlayerNameIndexRepository.SearchByPrefixAsync`
  read only), so it's no more "collected" than any other request path
  already covered by "standard web server logs."
- 2026-07-17 — `docs/implementation-document.md` (0.53 → 0.54),
  `backend/src/XGArcade.Data/Entities/PlayerNameIndex.cs`,
  `backend/src/XGArcade.Data/Repositories/IPlayerNameIndexRepository.cs` —
  quality-gate follow-up on S-032 (REQ-207/ADR-0007): corrected a false
  "same id space as `Player.Id`" claim in `PlayerNameIndex.PlayerId`'s doc
  comments — it's actually a synthetic, QID-derived key local to
  `PlayerNameIndex`/COMP-10 (`PlayerNameIndexImporter.DeterministicPlayerId`),
  with no guaranteed relationship to the separately-minted `Player.Id`
  (`Guid.NewGuid()`, `WikidataLookupService`) for the same real person, and
  no reconciliation between the two exists. Comment/doc text only, no
  behavior change, no new ADR (both `architecture-reviewer` and
  `quality-architect` agreed this doesn't need one). Also added
  `PlayerNameIndexImporterTests.ImportAsync_RepositoryUpsertThrows_PropagatesException_NotSwallowed`
  covering the previously-untested write-failure propagation path, by
  `backend-implementer`.
- 2026-07-17 — `docs/requirements-document.md` (0.57 → 0.58: REQ-207's
  status note rewritten from "Proposed, queued as S-032" to "Implemented
  (S-032)", describing the shipped `GET /players/autocomplete` contract),
  `docs/architecture-document.md` (0.35 → 0.36: COMP-10's row and the
  guess-submission flow diagram's Tier 0 status notes both updated —
  `PlayerNameIndex`/`IPlayerNameIndexRepository` now exist, and
  `PlayerNameIndexImporter` is noted living in `XGArcade.DataSync` rather
  than `XGArcade.Data/Seeding`, forced by the existing one-way
  `XGArcade.DataSync` → `XGArcade.Data` project-reference direction),
  `docs/implementation-document.md` (0.52 → 0.53: `PlayerNameIndex`'s
  entity sketch gains a note on the deterministic-hash `PlayerId`
  derivation in place of a `WikidataQid` column; §5's required-indexes
  table row and §6a both updated with the new paginated bulk-import
  query's shape), `docs/backlog.md` (S-032 entry gains a "Built as" note,
  including the two deviations forced by the project-reference graph),
  `infra/scripts/lib/game-data-tables.sh` (corrected the `PlayerNameIndex`
  placeholder entry to the real EF Core table name,
  `PlayerNameIndexEntries`, now that the table exists — no other allowlist
  entry touched, per ADR-0009), by `backend-implementer` — closes
  REQ-207/ADR-0007's `PlayerNameIndex` gap (S-032, pulled forward from
  Tier 1): a new `PlayerNameIndex` table/repository (COMP-10, structurally
  separate from COMP-06's `IPlayerStoreRepository`), a bulk, paginated
  Wikidata importer (`PlayerNameIndexImporter`, the
  `import-player-name-index` CLI verb/workflow per ADR-0024), and
  `GET /players/autocomplete?query=&limit=` (bearer-token authenticated).
  Backend suite: 361/361 passed across all five projects (`dotnet` SDK
  freshly installed in this sandbox via `apt-get install
  dotnet-sdk-10.0`); a real EF Core migration (`AddPlayerNameIndex`) was
  generated via `dotnet ef migrations add`, not hand-written.
- 2026-07-17 — `docs/design-document.md` (0.20 → 0.21) — S-032: added a
  frontend implementation note under SCREEN-02 for the shipped autocomplete
  suggestion list (`GuessInput.tsx`) — neutral-tokens-only styling (no
  accent-green/accent-gold, per REQ-207/ADR-0007's "suggestion ≠
  correctness" boundary), the select-fills-but-never-auto-submits
  behavior, the 275ms/2-character debounce, and the standard
  combobox/listbox ARIA pattern used for keyboard nav — none of which had
  an existing spec to follow. Flagged that the photo/silhouette avatar
  SCREEN-02 already described isn't shippable yet since the
  `PlayerNameIndex` contract this story builds against has no photo field.
- 2026-07-17 — `docs/requirements-document.md` (0.56 → 0.57: REQ-607's
  status note rewritten from "Partially implemented... currently-unmet
  gap" to "Implemented (S-034)" describing the shipped `cursor`/`pageSize`
  contract; REQ-404's status note and REQ-405's "Performance" design-
  question note both had their stale cross-references to REQ-607's
  unbounded-response gap corrected), `docs/architecture-document.md`
  (0.34 → 0.35: §6.2a's global leaderboard flow diagram corrected — no
  longer says "response never paginated yet," now describes the in-memory
  rank/slice step added by S-034; architecture-reviewer's "no boundary
  change, no ADR needed" verdict from the S-034 quality gate confirmed,
  not re-litigated), `docs/implementation-document.md` (0.51 → 0.52: §6's
  "Tier 0 status (S-011)" paragraph under "Leaderboard pagination
  (REQ-607)" replaced with a "Built as (S-034)" note covering the query
  params, response DTO shape/explicit `Rank` field, default/max pageSize,
  cursor-validation behavior, and the accepted in-memory-slice MVP-scale
  tradeoff), `docs/backlog.md` (S-034 entry gained a "Built as" note,
  including the page-1-reorder dedup bug found and fixed during the
  quality gate), `docs/design-document.md` (0.19 → 0.20: SCREEN-03's
  mockup gains the "Load more" control and pinned "you" footer, both
  reusing existing surface/border/accent tokens, no new design decision),
  by `doc-sync` and the orchestrator — closes REQ-607's leaderboard-
  pagination gap (S-034): `GET /leagues/global/leaderboard` now takes
  `cursor`/`pageSize` query params and returns a bounded page with an
  explicit global `Rank` per row and an always-present `RequestingUserRow`.
  Backend suite verified in full this session (`dotnet` SDK installed per
  `NOTES.md`'s documented fix): 328/328 passed across all five backend
  test projects; frontend suite 96/96, `tsc -b`/lint clean.
- 2026-07-17 — `docs/requirements-document.md` (0.55 → 0.56: REQ-301's
  Status block rewritten — configurable round duration is now built, not a
  gap), `docs/architecture-document.md` (0.33 → 0.34: ADR index gains
  ADR-0027), `NOTES.md` (new 2026-07-17 entry superseding the 2026-07-10
  Tue+Fri-cadence derivation with the new daily-cron/24h-safety-margin
  reasoning), by `doc-sync` — closes REQ-301's "configured...so play
  frequency can be adjusted without a code change" gap:
  `RoundSchedulingOptions.RoundDuration`'s default is now read from
  `RoundScheduling:RoundDurationHours` config (default 48h, overridable via
  the deployed Container App's `RoundScheduling__RoundDurationHours` env
  var with no redeploy), `POST /internal/generate-round` accepts an
  optional `roundDurationHours` query parameter (floor 24h) for a one-off
  override, and `generate-round.yml`'s cron moved from Tue+Fri to daily
  (`0 6 * * *`) with a `workflow_dispatch` input plumbed through — the old
  hand-matched `RoundDuration`/cron-gap coupling is replaced by the
  structural invariant `RoundDuration >= 24h` (the daily cron's constant
  max gap). See ADR-0027 for full reasoning, including why a `*/2`
  day-of-month cron was rejected. `docs/backlog.md` checked (S-008): no
  stale cadence references found, no change needed.
- 2026-07-17 — `docs/requirements-document.md` (0.54 → 0.55, by
  `requirements-writer`: new **REQ-113** "club membership means ever
  played for," **REQ-111** extended with all-clubs mode),
  `docs/implementation-document.md` (0.50 → 0.51: §6a sample intersection
  query switched to the full `p:P54`/`ps:P54` statement path, rules list
  3 → 4 with the new never-truthy-P54 rule, senior-career-only note
  clarified to be about club *entities* per REQ-109 not statement ranks,
  §6's `clean-stale-club-attributes` verb gains the `--all-clubs`
  mode/guards), `NOTES.md` (2026-07-13 entry's now-stale query-shape
  quote annotated; new 2026-07-17 incident entry with operator recovery
  order and the open Tonali/"Tottenham" verification item),
  `docs/backlog.md` (retroactive **S-042** entry with "Built as" note,
  per S-033/S-035/S-037 precedent for incident-driven work) — truthy
  `wdt:P54` is best-rank-only, so preferred-ranked current clubs silently
  dropped normal-rank historical clubs ("ever played for" became
  "currently plays for"; Sandro Tonali × AC Milan scored incorrect);
  fixed via the full statement path excluding only deprecated rank in
  both `WikidataClient` builders, recovered via the new
  `clean-stale-club-attributes --all-clubs` mode. **No ADR** —
  `architecture-reviewer` and `quality-architect` concurred this is a bug
  fix restoring already-documented semantics (conditional on the §6a
  update, done here), and `--all-clubs` extends the existing S-037/
  REQ-111 mechanism; `docs/architecture-document.md` checked, no change
  (COMP-07-internal query shape + COMP-06-internal tooling, no
  boundary/data-flow change). Tests: 2× REQ113 query-shape
  (`WikidataClientTests.cs`), 4× REQ111
  (`StaleClubAttributeCleanerTests.cs`); backend suite not runnable in
  this sandbox (no dotnet SDK), deferred to CI; frontend suite 89/89
  green (untouched by the diff, run for completeness). REQ-111, REQ-113.
- 2026-07-17 — new `.github/pull_request_template.md`, `CLAUDE.md` (Git
  and PR conventions section) — PR descriptions were getting bloated
  (free-form prose leaking this repo's CHANGELOG-style thoroughness
  straight into PR bodies). Added a template with four sections (Summary,
  Why, How — only if non-obvious, Testing & docs) plus an optional
  "Agents involved" section (one line per agent, only when it adds real
  signal, e.g. which lane owns a needed follow-up) — omitted entirely for
  small or single-agent changes. Deliberately no dedicated PR-writing
  agent: same reasoning already recorded for git/PR operations generally
  (a persona wrapped around a built-in capability adds a layer without
  adding value) — the template constrains the orchestrator's existing PR
  authoring instead. Written so Summary/Why read standalone, intended to
  double as release-notes material later. No REQ/ADR — process/tooling
  only, no product behavior change.
- 2026-07-17 — new `docs/ai/agent-migration-plan.md`, `CLAUDE.md`
  (agent/command tables, document map row, conventions line),
  `.claude/README.md` (rewritten for the new organization),
  `docs/coding-guidelines.md` (0.3 → 0.4, "For AI agents" note now names
  `quality-architect` as its enforcement point and owner) — agent
  ecosystem redesign into an explicit engineering organization. The main
  session is formalized as the **orchestrator** (new `/orchestrate`
  command: intake → scope check → decomposition → delegation → quality
  gate → docs → done-validation; deliberately a main-session protocol,
  not a subagent, since subagents can't delegate to subagents — same
  reasoning as the existing no-git-persona decision). `code-reviewer`
  retired and merged into a new **`quality-architect`** agent that keeps
  every review duty verbatim and additionally owns the three previously
  orphaned responsibilities: deliberate refactoring (code-reviewer was
  explicitly forbidden from it, and nobody else held it), test
  architecture (fake/fixture/builder strategy, flaky/slow tests, the
  E2E-drift trap S-029 hit), and quality gates (new `/quality-gate`
  command — fixed review order, fails closed, "deferred to CI" is an
  explicit status). New **`backend-implementer`** delivery agent codifies
  backend knowledge previously living only in NOTES.md/CHANGELOG history
  (InMemory-provider `ExecuteUpdate/DeleteAsync` trap, request-scoped
  `DbContext` concurrency trap, CLI-verb-not-endpoint job pattern per
  ADR-0022/0024, no-`dotnet`-SDK/no-Docker/no-wikidata.org sandbox
  constraints and their report-honestly precedents). `test-writer`,
  `ui-implementer`, `architecture-reviewer` got small boundary-clarifying
  edits; `doc-sync`, `requirements-writer`, `game-scaffolder` and all
  four existing commands unchanged. Full inventory, keep/merge/retire
  rationale, knowledge-transfer matrix, and the after ownership matrix
  (every responsibility → exactly one owner) are in the new plan doc;
  historical "`code-reviewer` pass" mentions in backlog/requirements/
  design docs deliberately left as accurate history. No REQ/ADR — process
  and tooling only, no product behavior or architecture change.
- 2026-07-14 — `docs/requirements-document.md` (0.53 → 0.54),
  `docs/design-document.md` (0.18 → 0.19), `docs/backlog.md` — same
  feedback round as the S-033/REQ-206 fix below, two follow-up requests.
  (1) SCREEN-01a state 3's "no attempts left · 100 pts" simplified to just
  "100 pts", matching a correct cell's own minimal "✕/✓ + points"
  structure exactly — the qualifier text read as redundant once the
  points value itself said "this cell is done." State 4's incorrect
  outcome brought in line the same way. (2) SCREEN-06's explainer (REQ-213)
  gained three more required content points, none previously documented
  anywhere player-facing: the attempt count, that a wrong guess and an
  unanswered cell lock at the same maximum score (previously each only
  documented in isolation), and the player-pool restriction (REQ-112/
  ADR-0025, male footballers born 1939 or later). Also fixed a stale
  `docs/design-document.md` §5 "Copy and voice" bullet left over from
  S-041's own doc-sync pass (still told writers to say "live"/"final,"
  a distinction that story had already removed from the cell entirely).
  REQ-204/213.
- 2026-07-14 — `docs/requirements-document.md` (0.52 → 0.53),
  `docs/backlog.md`, `docs/implementation-document.md` (0.49 → 0.50) —
  implemented S-033 (finally) and fixed a connected REQ-206 bug, both
  reported directly by a player on the deployed app: a locked-incorrect
  cell showed no point value at all, and the header's running total
  silently excluded it too, so a wrong guess read as scoring 0 (the best
  possible score under ADR-0021's golf model) instead of the guaranteed
  `MaxPointsPerCell` worst case it actually locks at. New
  `frontend/src/lib/scoringRules.ts` (`MAX_POINTS_PER_CELL`), used by both
  `CellState.tsx`'s state-3 branch and `GridScreen.tsx`'s running-total
  sum. REQ-204/206.
- 2026-07-14 — doc-sync pass on S-041's implementation:
  `docs/requirements-document.md` (0.51 → 0.52, REQ-212 and REQ-213 status
  changed from "Proposed" to "Implemented (Tier 0, S-041)" with "Built as"
  notes describing what actually shipped — including two real fixes found
  during implementation that weren't in the original acceptance criteria:
  the `.cell-state__name` zero-width-on-narrow-cell CSS bug (a revealed
  player name could shrink to invisible under flexbox's automatic
  min-size-0 behavior, found via required manual browser verification, not
  just tests) and `ScoringExplainer`'s missing focus-management/z-index
  handling, caught by a `code-reviewer` pass that also found the design
  doc's SCREEN-06 entry falsely claiming this already matched `GuessInput`'s
  behavior), `docs/backlog.md` (S-041 entry gained a "Built as" note — same
  two fixes, plus the `GridCell.tsx`/`CellState.tsx` state-ownership move
  and final test counts), `docs/implementation-document.md` (0.48 → 0.49,
  §4's `/grid` project-structure line gained `ScoringExplainer`, the one
  genuinely new top-level component file this story added — matching how
  the existing list already names `GridScreen`/`Grid`/`GridCell`/
  `CellState`/`GuessInput`/`CategoryLabel` individually rather than
  generically; also fixed a pre-existing, unrelated stale in-body version
  header, "Version 0.41" → matching frontmatter's 0.48 at the time),
  `docs/CHANGELOG.md` (this entry, plus the missing entry below for
  `docs/requirements-document.md`'s 0.49 → 0.51, `docs/backlog.md`'s new
  S-041 entry, and `docs/design-document.md`'s 0.17 → 0.18 update — all done
  as part of implementing S-041 per `CLAUDE.md`'s design-before-code rule,
  but never logged here, flagged as missing by a `code-reviewer` pass on the
  diff, same gap S-040's own doc-sync pass caught previously). Checked and
  found accurate, no change needed: `docs/architecture-document.md` (this
  story is a frontend component-internal change — no component boundary,
  responsibility, or data-flow change; CONT-01's "Web Frontend" row doesn't
  enumerate individual React components or props, so neither the new
  `ScoringExplainer` component nor the removed `roundEndTime` prop on
  `Grid`/`GridCell` need a mention there) and `docs/design-document.md`
  (SCREEN-01a's redesign and the new SCREEN-06 entry already matched what
  shipped, including the focus-management correction the `code-reviewer`
  pass required — verified against `ScoringExplainer.tsx`/`GridCell.tsx`/
  `CellState.tsx` directly, not just that a CHANGELOG entry existed). No new
  ADR — dropping the per-cell live/final distinction, moving to click-only
  reveal, and adding a general explainer modal are UI/UX decisions within
  `design-document.md`'s existing token/interaction conventions, not a new
  component boundary or structural decision. REQ-204/212/213.
- 2026-07-14 — `docs/requirements-document.md` (0.49 → 0.51),
  `docs/design-document.md` (0.17 → 0.18), `docs/backlog.md` — S-041's own
  scoping/implementation pass: REQ-204 amended with three of its acceptance
  criteria marked `Superseded 2026-07-14` (kept for history, per this
  document's ID-stability discipline) rather than rewritten — the
  permanent live-dot/"live" text indicator, the S-019/S-040 tap-or-hover/
  focus %-breakdown/round-end disclosure, and the "unmistakably
  provisional" wording rule — replaced by two new requirements: REQ-212
  (click/tap anywhere on a locked+correct cell toggles the guessed player's
  name/badge dock, replacing the old in-cell toggle) and REQ-213 (a general
  scoring/live-updates explainer, reachable from a new header `(ⓘ)` entry
  point, replacing the per-cell %-breakdown/round-end text with content
  that's the same regardless of which cells a player has attempted).
  `design-document.md`'s SCREEN-01a states 1/4 mocks redesigned to show
  only a checkmark + points value at rest (no dot, no "live"/"final" text,
  no percent), and a new SCREEN-06 entry added for the explainer modal.
  New backlog story **S-041** added, scoping all three changes together
  since they replace each other. This entry was missed in the original
  S-041 scoping/implementation pass and is added now by the doc-sync entry
  above. REQ-204/212/213.
- 2026-07-14 — doc-sync pass on S-040's implementation:
  `docs/requirements-document.md` (0.49 → 0.50, REQ-204's "Acknowledged
  gap, queued as S-040" note replaced with a "Built as" note describing
  what actually shipped, including two real bugs found and fixed along the
  way that weren't in the original planned-gap note — the `table-layout:
  fixed`/`<colgroup>` root-cause fix for the mobile header crush, and the
  `.cell-state__reveal-toggle` `font: inherit` font-size cascade bug),
  `docs/backlog.md` (S-040 entry gained a "Built as" note — same two bugs,
  plus the `useRevealDisclosure`/`RevealToggle` rename and the chosen
  `960px` desktop breakpoint), `docs/CHANGELOG.md` (this entry, plus the
  missing `docs/design-document.md` 0.16 → 0.17 entry below — the mock
  content update for SCREEN-01a states 1/4, done as part of implementing
  S-040 per CLAUDE.md's design-before-code rule, flagged as missing a
  CHANGELOG entry by a `code-reviewer` pass on the diff). Checked and found
  accurate, no change needed: `docs/architecture-document.md` (this is a
  frontend component-internal change — no component boundary, responsibility,
  or data-flow change) and `docs/implementation-document.md` (§4's project
  structure listing already just names `CellState` generically, no
  now-stale internal detail like `LiveMetaDisclosure`'s old name). No new
  ADR — the toggle-mechanism reuse and breakpoint choice are implementation
  detail within an already-decided design (S-019's toggle pattern), not a
  new structural decision. REQ-204.
- 2026-07-14 — `docs/design-document.md` (0.16 → 0.17) — SCREEN-01a's state
  1 and state 4 mocks updated to show the new at-rest/revealed content split
  (name gated behind the reveal toggle in both states; state 1's live point
  estimate moved to always-visible) as part of implementing S-040, per
  CLAUDE.md's design-before-code rule — this entry was missed in the
  original S-040 scoping/implementation pass and is added now by the
  doc-sync entry above. REQ-204.
- 2026-07-14 — `docs/requirements-document.md` (0.48 → 0.49),
  `docs/design-document.md` (0.15 → 0.16, also fixed a pre-existing stale
  in-body version header, "Version 0.5" → matching frontmatter),
  `docs/backlog.md` — scoped a real gap found from direct product feedback
  (two screenshots: the deployed app on a phone, and on a wide/"desktop
  site" viewport). Root-caused before scoping (not assumed): the mobile
  header-crush bug (a country name rendering one character per line) traces
  to `Grid.css`'s row-header `max-width` not being enforced by the table's
  browser auto-layout, so a wide cell (full player name + badge + checkmark
  + live text) in the same row squeezes the header column, and
  `overflow-wrap: anywhere` then breaks mid-word. The desktop layout issue
  traces to `.app`'s `max-width: 900px` cap never actually being art-
  directed past mobile — and, separately, confirmed `design-document.md`
  SCREEN-01's documented desktop side-panel variant was never actually
  built, only the single-column mock. New story **S-040**: redesigns
  SCREEN-01a states 1 and 4 (the only two showing a player name) to show
  only checkmark/✕ + points at rest on every screen size, name gated behind
  S-019's existing tap/hover/focus toggle (extended, not duplicated);
  polishes desktop spacing/sizing; explicitly defers the side-panel variant
  to its own future story. REQ-204 gained a status note pointing to S-040;
  SCREEN-01 gained a status note recording the side-panel gap. REQ-204.

- 2026-07-14 — doc-sync pass on S-039's REQ-710 UI work: `docs/architecture-document.md`
  (0.32 → 0.33, CONT-01/Web Frontend row description was missing auth/account
  screens entirely — a pre-existing gap predating this story, since AuthScreen
  was never listed there either; fixed now rather than left to compound,
  since it's a one-line accuracy correction, not a boundary/responsibility
  change), `docs/implementation-document.md` (0.47 → 0.48, §4 project
  structure's `/auth` entry now also lists `DeleteAccountScreen`). Checked
  and found already accurate/complete, no change made:
  `docs/requirements-document.md` (REQ-710's S-039 "Built as" note and Test
  level line), `docs/design-document.md` (SCREEN-05), `docs/backlog.md`
  (S-039 entry), `docs/legal/privacy-policy-draft.md` (deletion language
  already matches; note its "export... directly from your account settings"
  sentence is aspirational — REQ-711/data export has no "Built as" note and
  isn't implemented at all yet, and there's no general "account settings"
  screen, only the single "Delete account" header link — but this predates
  S-039 and wasn't touched by this story's diff, so left for a separate
  pass). No new ADR — this story added a frontend UI for an
  already-decided/implemented backend behavior (S-025/ADR-0026), no new
  architecturally-significant choice. REQ-710.
- 2026-07-14 — `docs/requirements-document.md` (REQ-710 status restored to
  "Implemented, Tier 0, S-025/S-039" now that the gap #49 flagged is closed;
  "Built as" note gained a S-039 frontend addendum, test level now includes
  UI), `docs/design-document.md` (0.14 → 0.15, new SCREEN-05: Delete
  account), `docs/backlog.md` (S-039's "Built as" note added to the story
  #49 scoped) — S-039: delete-account UI (REQ-710). S-025 built `DELETE
  /auth/account` with no frontend; this closes that gap. New
  `deleteAccount()` (`frontend/src/lib/api.ts`) and `DeleteAccountScreen`
  (`frontend/src/auth/`), reached only via a "Delete account" header link
  (no general profile/settings page). Re-enters and re-verifies the current
  password server-side (no bare confirmation checkbox), shows an explicit
  irreversibility warning, and on success signs the user out and returns to
  the login/landing screen via the existing `handleLogout`. A wrong-password
  401 (`ProblemDetails.title === "Incorrect password"`) shows inline and
  changes nothing; any other 401 is treated as an expired/invalid JWT, same
  as every other authenticated screen.
- 2026-07-14 — `docs/requirements-document.md` (0.47 → 0.48, also fixed a
  pre-existing stale in-body version header, 0.42 → 0.48), `docs/backlog.md`
  — scoped a real gap found right after S-025 merged: `DELETE /auth/account`
  is fully implemented and tested, but no frontend code anywhere calls it —
  S-025's own acceptance criteria was backend-only, so self-service account
  deletion currently has no way for a real player to reach it, and there's
  no account/settings screen defined in `design-document.md` either. New
  story **S-039**, deliberately scoped narrow (delete-account flow only, no
  general profile/settings page) — REQ-710 status note added pointing to
  it. This is a scoping gap in how S-025 was originally written, not
  anything S-025's implementation did wrong (it matched its acceptance
  criteria exactly). A `requirements-writer` review pass on this change
  found the REQ-710 heading itself still overclaimed: `Status: Implemented`
  was no longer accurate once this gap is documented (a GDPR-driven legal
  right that no real user can currently invoke isn't a minor edge-case gap)
  — requalified to `Status: Partially implemented — backend only ...; no
  player-facing entry point yet, see docs/backlog.md S-039`, matching this
  doc's existing "Partially implemented" precedent (e.g. REQ-208/REQ-209).
  REQ-710. (Superseded same day by the two entries above once S-039 itself
  was built.)
- 2026-07-14 — `docs/requirements-document.md` (0.46 → 0.47, REQ-710 marked
  Implemented), `docs/architecture-document.md` (0.31 → 0.32, new COMP-01
  status note), `docs/implementation-document.md` (0.45 → 0.46, §6.8 "Built
  as" note), `docs/backlog.md` (S-025 "Built as" note), new
  `docs/decisions/0026-service-role-key-for-account-deletion.md`,
  `MVP-SCOPE.md`/`infra/README.md`/`SETUP.md` (new
  `DEV_SUPABASE_SERVICE_ROLE_KEY`/`PROD_SUPABASE_SERVICE_ROLE_KEY` secrets)
  — S-025: self-service account deletion (REQ-710). New
  `IAccountDeletionService` (`XGArcade.Core.Auth`) anonymizes `Guess` rows,
  removes `LeagueMembership` rows, deletes the local `User` row, then
  deletes the Supabase Auth identity via a new `Supabase:ServiceRoleKey`
  secret (ADR-0026) — built as reusable service logic (identified by local
  `User.Id`, not a JWT) so S-026's admin-triggered deletion can reuse it.
  New `DELETE /auth/account` endpoint, confirmation-gated by re-verifying
  the caller's password against Supabase Auth.
- 2026-07-14 — doc-sync pass on S-025's REQ-710 work:
  `docs/architecture-document.md` (§6.8's flow diagram corrected from
  `DELETE /account` to the actual built route `DELETE /auth/account`),
  `docs/implementation-document.md` (0.46 → 0.47, new §6a entry
  documenting `SupabaseAuthClient.DeleteUserAsync`'s
  `DELETE {Supabase:Url}/auth/v1/admin/users/{id}` call and its
  `Supabase:ServiceRoleKey` header override — this REST call shape wasn't
  previously catalogued alongside signup/login's), `docs/coding-guidelines.md`
  (0.2 → 0.3, new EF Core guideline: load-then-`SaveChangesAsync` through
  the change tracker rather than `ExecuteUpdateAsync`/`ExecuteDeleteAsync`
  for repository writes, since the InMemory test provider can't translate
  the latter — generalizes the pattern S-025's three new repository
  methods established). `docs/legal/privacy-policy-draft.md` was checked
  against what was actually built and found already accurate (its
  deletion/rights language predates this story and already described this
  exact behavior) — no change made. REQ-710, ADR-0026.
- 2026-07-13 — `docs/requirements-document.md` (0.45 → 0.46, new REQ-112),
  `docs/implementation-document.md` (0.44 → 0.45), `docs/backlog.md` (new
  S-038), new
  `docs/decisions/0025-player-pool-restricted-to-male-born-1939-or-later.md`
  — user-identified scope issue: the player pool had no gender or era
  restriction. Both `WikidataClient` SPARQL query builders now require
  `wdt:P21 wd:Q6581097` (male) and a `wdt:P569`/`FILTER` requiring date of
  birth on/after a fixed `1939-01-01T00:00:00Z` cutoff (a first pass used a
  `TimeProvider`-driven rolling "latest 100 years" window; the user
  corrected this to the fixed date, which also removed the clock
  dependency entirely). Existing cached player data couldn't be
  selectively corrected (neither property was ever recorded on cached
  rows) so a new `purge-player-pool "delete all player data"` CLI verb +
  workflow deletes the entire pool (Player, cascading through
  PlayerData/PlayerOverride/PlayerAttribute/PlayerAlias) behind a required
  exact-confirmation-phrase gate, same extra-friction pattern as
  `promote-dev-to-prod.sh`. Reference tables and account/game-history
  tables are untouched. `docs/architecture-document.md` checked, no change
  needed — same component responsibility, stricter query only. A
  `code-reviewer` pass on the earlier rolling-window draft caught the
  cutoff being formatted as a date-only literal but typed `^^xsd:dateTime`
  in the SPARQL `FILTER` — malformed for that XSD type (a SPARQL type
  error in a `FILTER` silently excludes everything rather than throwing);
  the fixed-date cutoff carries the same `T00:00:00Z` time component this
  fix required.
- 2026-07-13 — `docs/backlog.md` (new S-037) — the user manually verified
  S-036's new club Wikidata QIDs against live Wikidata pages (this sandbox
  can't reach `wikidata.org`) and found 4 of 6 wrong: Napoli, AS Roma,
  Sevilla, Porto. Each wrong QID happened to be some *other* real Wikidata
  entity, so queries against them silently returned real-but-wrong player
  data rather than failing loudly — S-036's own doc comment predicting
  "self-limiting, not dangerous" was wrong for these 4. Corrected in
  `ReferenceDataSeeder.cs`, plus 11 further clubs with verified (not
  guessed) QIDs, 21→32 total. Two real gaps fixed alongside the QID
  correction itself: `ReferenceDataSeeder.SeedAsync` only ever added a
  missing row, never corrected an existing one's `WikidataQid`, so editing
  the QID literals alone would have silently done nothing against the
  already-seeded dev database — now updates in place. New
  `StaleClubAttributeCleaner` (`dotnet run -- clean-stale-club-attributes`,
  via a new `clean-stale-club-attributes.yml` workflow) purges whatever
  got persisted under a club's name while its QID was wrong, since nothing
  in the persisted data can tell old from new after the fact — deliberately
  a manual, argument-driven CLI verb, not wired into `migrate-and-seed`'s
  automatic chain, since running it on every deploy would eventually wipe
  freshly-fetched correct data too. `docs/architecture-document.md` checked,
  no change needed — stays within COMP-06's existing responsibility, no
  boundary change. REQ-109. (A `requirements-writer` pass below revised the
  "no new REQ" call for `docs/requirements-document.md` specifically.)
- 2026-07-13 — `docs/implementation-document.md` (0.43 → 0.44) — doc-sync
  pass on S-037 (PR #46): added a §6 paragraph documenting
  `ReferenceDataSeeder.SeedAsync`'s new in-place `WikidataQid` correction
  behavior and the third `clean-stale-club-attributes` CLI verb
  (`StaleClubAttributeCleaner`), following the same documentation pattern
  already used for `migrate-and-seed`/`warm-player-cache` — this doc had no
  mention of either at all before this pass, and its own `update_when`
  ("a new tool is adopted") applies. `docs/architecture-document.md`
  re-confirmed accurate, no further change. REQ-109. (`docs/requirements-
  document.md`'s "no change needed" call from this same pass was revised by
  a subsequent `requirements-writer` review below.)
- 2026-07-13 — `docs/requirements-document.md` (0.44 → 0.45) —
  `requirements-writer` pass on S-037 (PR #46): added **REQ-111 –
  Recovery from a corrected reference-data QID**, right after REQ-110.
  Two earlier passes (this session's own judgment, then a `doc-sync`
  review) had both filed `StaleClubAttributeCleaner`'s cache-purge/recovery
  behavior under REQ-109 by association rather than giving it its own
  requirement — a `code-reviewer` pass flagged this as a real stretch of
  REQ-109's language, which only covers reference-table QID resolution, not
  purging the derived `PlayerAttribute`/`PlayerData` cache once a QID is
  corrected. `StaleClubAttributeCleanerTests.cs`'s 6 tests renamed from
  `REQ109_...` to `REQ111_...` to match; the two `ReferenceDataSeederTests.cs`
  tests proving `SeedAsync`'s in-place QID correction stay under REQ-109,
  since that behavior — correcting the reference table itself — is what
  REQ-109 already covers.
- 2026-07-13 — `docs/requirements-document.md` (0.43 → 0.44),
  `docs/implementation-document.md` (0.42 → 0.43),
  `docs/architecture-document.md` (0.30 → 0.31), `docs/backlog.md`
  (new S-036), `docs/decisions/0024-cache-warming-runs-as-a-cli-verb.md`
  (new) — the very next `generate-round.yml` dispatch after S-035's
  `MaxDuration` fix merged failed fast with `GridGenerationException: "Ran
  out of candidates before completing the grid."` — the data-sparsity half
  of the same problem S-011's backlog entry predicted back when
  `MinValidAnswers` was raised to 5 (S-014): only 15 reference clubs means
  many real country/club pairs, especially smaller-market countries,
  genuinely don't have 5+ shared historical players, and no amount of
  retrying fixes that. Added new REQ-110 (proactive player-attribute cache
  warming, `PlayerCacheWarmingService`, `XGArcade.Games.XGGrid`) plus a
  widened reference pool (`ReferenceDataSeeder.cs`: 20→45 countries,
  15→21 clubs). The warming job is a `dotnet run -- warm-player-cache` CLI
  verb (same shape as `migrate-and-seed`) run via a new
  `warm-player-cache.yml` workflow, deliberately not an HTTP endpoint or a
  fire-and-forget background task — both would be unsafe against this
  Container App's ~240s ingress timeout and `minReplicas: 0` scale-to-zero
  respectively; see the new ADR-0024 for the full alternatives-considered
  reasoning (an architecture-reviewer pass on the first draft of this
  change flagged that this execution-model decision needed an indexed ADR,
  not just scattered prose — added, along with the previously-unlisted
  ADR-0023 from S-035, both now in architecture-document.md §10's table).
  Same review pass also caught `Program.cs`'s CLI verb hand-duplicating the
  real `AddHttpClient<IWikidataClient, WikidataClient>` registration's
  `BaseAddress`/`User-Agent` — extracted into a shared
  `ConfigureWikidataHttpClient` local function so the two can't drift.
  REQ-110.
- 2026-07-13 — `docs/requirements-document.md` (0.42 → 0.43),
  `docs/implementation-document.md` (0.41 → 0.42), `docs/backlog.md`
  (new S-035), `docs/decisions/0023-grid-generation-wall-clock-deadline.md`
  (new) — a real `generate-round.yml` dispatch chained enough live
  Wikidata lookups (`GridGameModule.PickHeadersAsync`) to run 4+ minutes
  before Azure's ingress killed the connection with a 504; `MaxAttempts`
  (500) never bounds wall-clock time in practice since the reference-data
  pool is far smaller. Added `GridGenerationOptions.MaxDuration` (default
  90s), checked alongside the existing abort conditions, so generation
  always resolves — success or a clean, logged failure — well under any
  known infrastructure timeout. A bounded-concurrency candidate search was
  also attempted (to raise success odds, not just fail faster) but reverted
  before commit: `PlayerStoreRepository`/`CategoryValueRepository`/
  `WikidataLookupService` share one request-scoped `XGArcadeDbContext`,
  and concurrent use of a single `DbContext` isn't safe in EF Core — would
  have passed tests against the InMemory provider while throwing against
  real Npgsql. Recorded as ADR-0023's explicit follow-up, not silently
  dropped. REQ-101.
- 2026-07-12 — `docs/coding-guidelines.md` (version 0.1 → 0.2) — a manual
  `generate-round.yml` dispatch returned an opaque, empty HTTP 500 (see
  NOTES.md's 2026-07-12 entry) because `InternalRoundEndpoints.cs`'s
  `/internal/generate-round` handler only caught `GridGenerationException`;
  any other exception fell through uncaught. Fixed by adding a catch-all
  `Exception` branch that logs server-side and returns the exception's own
  `Message` as the problem-details `detail`. That surfaces the actual
  failure in the CI log without needing Container App log access, but
  returning raw exception text contradicts this doc's existing "no raw
  exception messages to the client" rule — `architecture-reviewer` caught
  that the code's original justification for the exception lived only in
  an inline comment, not in any doc it claimed to be consistent with. Added
  an explicit, narrow carve-out to the rule here instead: `/internal/*`
  endpoints whose only caller is a bearer-token-gated scheduled job (today,
  just this one) may return raw exception detail, since the only "client"
  reading it is the job's own log, not a player-facing surface. REQ-301.
- 2026-07-12 — `docs/architecture-document.md` (version 0.29 → 0.30),
  `docs/requirements-document.md` (version 0.41 → 0.42),
  `docs/implementation-document.md` (version 0.40 → 0.41), `docs/backlog.md`
  — doc-sync for S-030's landed implementation (branch
  `claude/s-030-grid-pairing-301ek8`, `git diff 8c8c638..HEAD`): Club × Club
  grid pairing is now built, not just permitted (REQ-107), via
  `GridGameModule.SelectPairing` choosing randomly between Country×Club and
  Club×Club per instance when the seeded reference data supports both, with
  a deterministic fallback otherwise. REQ-211's guess-time live-lookup
  fallback (ADR-0018) now also covers Club×Club cells, dispatched through a
  new shared `LookupLiveMatchesAsync` helper so generation-time and
  guess-time code can't drift on which pairings are handled. Updated
  architecture-document.md §6.1 (data flow diagram note, no longer
  describing a fixed Country-rows/Club-columns axis) and §6.2 (REQ-211
  fallback description), requirements-document.md REQ-107's status note
  (queued → implemented, describing the coin-flip/fallback behavior) and
  REQ-211's status note (Country×Club → Country×Club-or-Club×Club),
  implementation-document.md's `GridCell` data-model comment and the
  grid-generation/guess-scoring pseudocode status notes, and added a
  retroactive "Built as" paragraph to `docs/backlog.md`'s S-030 entry
  (matching this file's convention for other completed stories) noting the
  `Random? random` testability seam and the code-review-driven dispatcher
  consolidation. `MVP-SCOPE.md`'s "Grid content" line was checked and needs
  no change — it already reads correctly for the landed state. No ADR
  needed (architecture-reviewer pass on this diff found no boundary
  violations). REQ-107, REQ-211, ADR-0018.
- 2026-07-12 — `docs/requirements-document.md` (version 0.40 → 0.41),
  `docs/backlog.md` — two more acknowledged gaps, previously flagged but
  never turned into stories, scoped into the backlog: **S-033** (`CellState`
  never renders a point value on the "incorrect, no attempts left" cell
  state, even though `design-document.md`'s mock has shown it since S-028
  — frontend-only rendering fix, REQ-204 status note added) and **S-034**
  (the global leaderboard endpoint is still unbounded, REQ-607's own
  acknowledged gap since S-011 — pagination shape was already fully
  specified in `implementation-document.md` §6, just never built; REQ-607
  status note updated to record it as queued rather than waiting on the
  original "membership grows large" trigger). No architecture/
  implementation doc changes needed — both stories build to an
  already-decided design, no new structural decision. REQ-204, REQ-607.
- 2026-07-12 — `infra/scripts/lib/game-data-tables.sh` (ADR-0009) — fixed
  the singular/plural table-name bug NOTES.md flagged on 2026-07-09
  (S-006): six of the allowlist's nine entries used the entity's singular
  name instead of its real EF Core table name — verified directly against
  `XGArcadeDbContext.cs`'s `DbSet<T>` properties (`Player`→`Players`,
  `PlayerOverride`→`PlayerOverrides`, `PlayerAttribute`→`PlayerAttributes`,
  `PlayerAlias`→`PlayerAliases`, `TrophyDefinition`→`TrophyDefinitions`,
  `GridTemplate`→`GridTemplates`; `PlayerData` was already correct).
  `PlayerNameIndex`/`ClubCrest` left as-is and commented — both are
  placeholders for tables that don't exist yet (S-032, Tier 2), so their
  real names can't be confirmed until built. Harmless in practice today —
  `sync-prod-to-dev.sh`/`promote-dev-to-prod.sh` are still unused until
  Tier 1's dev/prod split (T-106) — but would have broken the first real
  sync. Corresponding NOTES.md entry removed (resolved, not just noted).
  No REQ/ADR change — this corrects a data value in an existing script
  against an already-decided design (ADR-0009), not a new decision.
- 2026-07-12 — Post-Tier-0 planning session: `MVP-SCOPE.md`,
  `docs/backlog.md`, `docs/requirements-document.md` (version 0.39 → 0.40),
  `TODO.md` — no code changed, this is scope/story planning only. Reviewed
  what's left in Tier 1 against real Tier 0 play-testing and pulled three
  items forward by explicit product decision (not all strictly trigger-fired
  per `MVP-SCOPE.md`'s own discipline — recorded as such, not silently
  reclassified): **Club × Club grid pairing** (not actually a Tier 1 item —
  REQ-107 already allowed it, Tier 0 generation just never used it; queued
  as new story S-030), **Trophy category** (`MVP-SCOPE.md`'s "feels
  repetitive after a couple weeks" trigger judged hit; queued as S-031,
  deliberately scoped to individual awards only — Ballon d'Or, via
  Wikidata's `P166` — deferring team-competition trophies which need a
  structurally different query), and **Autocomplete + `PlayerNameIndex`**
  (trigger not strictly observed; pulled forward anyway by deliberate
  choice; queued as S-032, building exactly what ADR-0007 already
  specifies, no new ADR needed). Also resolved REQ-405's three previously-
  open design questions for leaderboard time-window resolutions (S-027,
  now unblocked): calendar-aligned windows, UTC, locked-rounds-only —
  closing `requirements-document.md` §7's last open question. `TODO.md`'s
  Tier 1 checklist updated to match (guess-time live verification checked
  off as already built; autocomplete/Trophy annotated as queued, not
  built). No architecture/implementation doc changes — none of this
  changed a component boundary or added a structural decision beyond what
  ADR-0007/ADR-0012 already cover; doc updates for architecture/
  implementation will follow the usual per-story `/update-docs` pass once
  each is actually implemented. REQ-107, REQ-108, REQ-207, REQ-405.
- 2026-07-12 — CI-caught E2E fix for S-029 (same branch, third commit,
  PR #40): `ci.yml`'s real Playwright run against a live backend (this
  sandbox has no `dotnet` SDK, so it can't run this suite locally — same
  limitation prior S-0xx entries recorded) failed on
  `frontend/tests/e2e/play-grid.spec.ts`'s "wrong guess shows incorrect +
  attempts left, correct guess locks the cell live" and "two wrong guesses
  ... lock the cell" tests: both had a pre-existing assertion that an
  incorrect guess's raw as-typed text stays visible in the cell — exactly
  what S-029's own name-display fix intentionally changed (no name shown at
  all for an incorrect guess). Neither the frontend unit suite (mocked
  fetch, doesn't exercise the real Playwright spec) nor either review pass
  below caught this, since none of them ran the actual E2E suite. Fixed by
  flipping both assertions to `.not.toBeVisible()`; the correct-guess
  assertion in the same test needed no change (`resolvedPlayerName` and the
  seed's `correctPlayerName` are the identical string, typed with matching
  case). Test-only fix, no product code changed, no doc other than
  `backlog.md`'s S-029 entry needed updating. REQ-303.
- 2026-07-12 — S-029 (branch claude/arcade-nav-ui-improvements-k8sbwj):
  five separate pieces of direct product feedback from playing the deployed
  app on a phone, bundled into one session per this repo's S-022/023/024
  precedent. **(1) Nav simplification:** the header wrapped onto a second
  line on a narrow phone with four separate buttons ("Games"/"Grid"/
  "Leaderboard"/"Log out") — "Games" and "Grid" both duplicated the existing
  game-selection landing page (S-021), so the "xG Arcade" title itself now
  routes there and those two buttons were removed, leaving "Leaderboard"/
  "Log out". **(2) Uniqueness copy fix:** "X% unique" read as backwards
  once paired with ADR-0021's golf-style points (higher uniqueness = fewer
  points) — `CellState.tsx` now shows the same number reframed as its
  complement, "N% of others guessed this too" (N = `round((1 - uniqueScore)
  * 100)`), so the percentage and point value move in the same direction;
  no formula changed, wording only, applied to both the live disclosure and
  the closed/final text. **(3) Mobile grid fit:** a Tier 0 3×3 grid still
  forced horizontal scrolling on an ordinary phone — the actual cause was
  uncapped-width, nowrap header label text ("Paris Saint-Germain," "United
  Kingdom"), not the 44px touch-target floor (which is unchanged and still
  applies to cells). Below a 480px viewport, header labels now wrap onto
  two lines and shrink their own width floor (`Grid.css`); the floor-plus-
  scroll design itself is unchanged for whatever's still too wide.
  **(4) Guessed-name display fix:** a guessed name was shown exactly as
  typed, including wrong casing for a correct guess, and shown at all for a
  wrong one (not useful information). New `GuessSubmissionResult`/
  `SubmitGuessResponse`/`CurrentRoundGuessResponse` field
  `ResolvedPlayerName` (the canonical `Player.FullName`, resolved via a new
  bulk `IPlayerStoreRepository.GetPlayersByIdsAsync` and a direct
  `GetPlayerByIdAsync` call from `GuessSubmissionService`) is null unless
  `IsCorrect`; the frontend now shows it instead of the raw `submittedName`
  for a correct guess, and no name at all for an incorrect one (`Row` in
  `CellState.tsx` gained an optional `name`). **(5) Round-closing fix, the
  real bug behind "I can't see my points":** direct play-testing found that
  a completed grid's score never reached the leaderboard in the deployed
  dev environment — nothing had ever called round-close automatically, so
  `Guess.FinalPoints` stayed null forever and every leaderboard total summed
  to 0 (REQ-205's own status note had already flagged this exact gap as
  "still missing"). `RoundGenerationService` (the code `generate-round.yml`'s
  cron actually invokes, Tier 0's only production-scheduled trigger point)
  now also closes a round before deciding whether to generate its successor
  — new `IRoundRepository.GetPreviousByGameKeyAsync` finds the correct round
  to close, which is never `latest` itself (REQ-301's "one round ahead"
  design means a round stops being `latest` long before it actually ends —
  see new `docs/decisions/0022-round-closing-runs-inside-generation-job.md`
  for the full derivation and the alternatives considered, including why no
  `Round.ClosedAt` schema migration was attempted this pass with no `dotnet`
  SDK available to verify one). Also added, smaller: `GridScreen.tsx` now
  shows a live "~N pts estimated" running total, summed client-side from
  the same per-cell `LivePoints` REQ-204 already returns (REQ-206's
  design-document.md SCREEN-01 mock already speced a "Total" line; never
  built until now). Trade-off recorded, not fixed: any rounds that had
  already ended-but-never-closed *before* this shipped need one additional
  cron cycle each to catch up, or a manual
  `POST /internal/test-data/force-close-round/{roundId}` call.
  `requirements-document.md` (REQ-204/205/206/303 status notes; version
  0.38 → 0.39), `architecture-document.md` (COMP-03/COMP-04 status notes,
  §6.2's diagram and prose corrected for the now-real scheduled trigger,
  new ADR-0022 table row; version 0.28 → 0.29), `design-document.md`
  (SCREEN-01's mock total line, SCREEN-01a's four state mocks — reworded
  uniqueness copy, removed the guessed name from both incorrect states and
  the closed-incorrect case, replaced the now-obsolete "point value moves
  opposite the percentage" explanatory note; a new note on the mobile
  header-wrap fix in §4; version 0.13 → 0.14), `backlog.md` (new S-029
  entry with a "Built as" note). Backend test suite could not be executed
  in this environment (no `dotnet` SDK available, same limitation prior
  S-0xx entries recorded) — new/changed backend logic was hand-traced
  against concrete round-chain timelines instead, and new
  `RoundGenerationServiceTests`/`GuessSubmissionServiceTests`/
  `GuessEndpointTests`/`CurrentRoundEndpointTests` cases were added
  following this repo's existing patterns (hand-rolled `FakeRoundCloseService`,
  no mocking framework). Frontend suite run for real (73/73 green,
  `npm run test`), `tsc -b` and `npm run lint` (`oxlint`) both clean —
  `CellState.test.tsx`'s uniqueness-copy assertions and two
  `GridScreen.test.tsx` guess-submission tests were updated to match the new
  wording/name-display behavior. No new Tier 1 trigger fired — all five
  fixes stayed inside Tier 0's existing scope. REQ-204, REQ-205, REQ-206,
  REQ-303, ADR-0022.
- 2026-07-12 — S-029 review pass (same branch, second commit): independent
  architecture-reviewer, code-reviewer, test-writer, ui-implementer, and
  requirements-writer passes over the S-029 diff above.
  **architecture-reviewer** and **ui-implementer** found the diff clean — no
  boundary violations (the new `ResolvedPlayerName` lookups stay plain
  by-ID reads, never touching `PlayerNameIndex`/ADR-0007's separation), no
  ad-hoc design tokens. **requirements-writer** fixed a real contradiction
  in REQ-206's status note — it said the per-round locked total "still only
  exists ... via the leaderboard," which wrongly implied a player can see it
  distinctly there, then immediately said the opposite (no per-round total
  is ever surfaced anywhere); reworded to state plainly that it's folded,
  uncredited, into the all-time sum. Also moved an inline `**(S-029)**` tag
  in REQ-303 into a proper Given/When/Then acceptance-criterion bullet,
  matching this doc's own convention elsewhere. **test-writer** found and
  closed two real coverage gaps: the new live "~N pts estimated"
  `GridScreen` total had no test at all (new cases added to
  `GridScreen.test.tsx`), and `RoundGenerationService`'s predecessor-closing
  logic had no test for a repeated call against the same clock/state (a
  retried cron tick) — new
  `REQ205_GenerateNextRoundIfNeeded_CalledAgainAfterSuccessorAlreadyGenerated_DoesNotCloseOrGenerateAgain`
  confirms a second run is a total no-op. Backend test names in
  `CurrentRoundEndpointTests.cs`/`GuessEndpointTests.cs`/
  `GuessSubmissionServiceTests.cs` and the two `App.test.tsx` REQ-303 cases
  above also picked up this repo's `REQ###_`/`REQ-###:` prefix convention
  where missing. `requirements-document.md` updated again (REQ-206/REQ-303
  wording only, no version bump beyond the 0.39 already recorded above,
  since both commits landed as one unreleased iteration). Frontend suite
  now **75/75 green** (`npm run test`, superseding the 73/73 figure recorded
  above — 2 tests were added by this pass), `tsc -b`/`npm run lint` both
  still clean. No architectural or requirements change beyond wording
  fixes and test coverage — no new ADR. REQ-206, REQ-303.
- 2026-07-12 — doc-sync pass over the full S-029 branch diff (both commits
  above, `docs/backlog.md`'s S-029 entry, and this CHANGELOG's own two
  S-029 entries above). Confirmed accurate and needing no change:
  `requirements-document.md`, `architecture-document.md`,
  `design-document.md` (all already correctly updated by the session
  itself, cross-checked line-by-line against the final code — including
  the review-pass commit's own fixes), and `docs/legal/*.md` (nothing in
  this diff touches data collection, retention, or third-party sharing —
  confirmed, not assumed). Found and fixed two real gaps: (1)
  `implementation-document.md` was untouched by this session, but its §6
  Tier 0 status note for round scheduling/scoring still flatly asserted
  "there is still no automated scheduled job that calls round-close ... in
  any environment" — false as of ADR-0022; corrected to describe
  `RoundGenerationService`'s new predecessor-closing call, matching
  architecture-document.md's own already-updated §6.2. (Checked, not
  added: this doc never itemizes every repository method for other REQs
  either — `GetPreviousByGameKeyAsync`/`GetPlayersByIdsAsync`/
  `ResolvedPlayerName` don't need their own entries in §5's data model,
  since none of them are persisted schema and this doc's granularity for
  DTOs/repository methods has never gone that deep.) Version 0.39 -> 0.40
  (frontmatter and the stale in-body "Version 0.33 · 2026-07-11" header,
  itself already out of sync with frontmatter before this branch,
  corrected to match). (2) The S-029 backlog entry's and this CHANGELOG's
  first S-029 entry's "73/73 green" frontend test count was accurate for
  the first commit but stale after the review-pass commit added 2 more
  tests (actual final count, re-run: 75/75) — `docs/backlog.md`'s
  "Built as" note updated in place to record the review pass and the
  corrected count (CHANGELOG's own historical entries left as written,
  each accurate as of the commit it describes; the second S-029 CHANGELOG
  entry above already states the corrected 75/75 total). No ADR needed —
  both fixes are doc-accuracy corrections, not new decisions.
- 2026-07-12 — independent test-writer and requirements-writer passes over
  the same S-022/023/024/028 branch (claude/points-ui-concerns-z9tvc2),
  run alongside the doc-sync pass below. **requirements-writer** fixed
  three leftover inconsistencies in requirements-document.md from the
  ADR-0021 golf-scoring flip that the author's own pass had missed: REQ-210
  still said an exhausted-attempts guess is "guaranteed 0 points" (now the
  *best* score, not a penalty — corrected to `ScoringRules.MaxPointsPerCell`);
  REQ-203's status note quoted stale "0 points regardless of uniqueness"
  wording; REQ-505/506 were missing the "Status: Proposed" marker REQ-405/
  504 already had, despite being equally unbuilt; REQ-504's Given clause
  wrongly implied it defines its own endpoints (REQ-505/506 do); and §7
  Open Questions still read "None" despite REQ-405 explicitly flagging
  unresolved product decisions — added a cross-reference rather than
  duplicating REQ-405's own list. Version 0.37 -> 0.38, and the stale
  in-body "Version 0.30 · 2026-07-10" header line (already out of sync with
  frontmatter before this branch) corrected to match. **test-writer** found
  two real test-coverage gaps: `ScoringRules.PointsFromUniqueScore` was
  only ever exercised indirectly through DB-backed scenarios that happened
  to land on exact 0.0/0.5/1.0 `uniqueScore`s, never verifying
  `Math.Round`'s default `MidpointRounding.ToEven` behavior at a real .5
  boundary — new `backend/tests/XGArcade.Core.Tests/Scoring/ScoringRulesTests.cs`
  covers the two opposite-direction midpoint cases (0.625->38, 0.375->62)
  plus a monotonicity regression guard; and `MaterializeUnansweredCellsAsync`
  resolving a `Round.GameKey` with no registered `IGameModule` was untested
  at the `CloseRoundAsync` integration level (only `GameModuleResolverTests`
  covered the resolver in isolation) — new
  `REQ206_CloseRoundAsync_RoundGameKeyHasNoRegisteredGameModule_ThrowsInvalidOperationException`
  confirms it fails loudly rather than silently defaulting unanswered cells
  to the best possible score. Also: renamed one `GridGameModuleTests.cs`
  test to carry its `REQ206_` prefix (it verifies a real acceptance
  criterion, unlike the file's unprefixed defensive-error-path tests), and
  strengthened `LeaderboardScreen.test.tsx`'s original REQ-404 test, which
  predated this branch and still used a descending-order mock asserting
  only that names appeared somewhere in the document — a regression back to
  descending sort would have passed it silently; now asserts actual DOM
  order and rank numbers against an ascending mock. Frontend suite 72/72
  green after these changes (`npm run test`), `tsc -b`/`npm run lint`
  clean. REQ-203, REQ-210, REQ-405, REQ-504, REQ-505, REQ-506, REQ-206.
- 2026-07-12 — independent doc-sync verification pass over the S-022/023/024
  (points-ui-concerns) and S-028 (golf-style scoring) commits on
  claude/points-ui-concerns-z9tvc2, run after the author's own substantial
  manual doc updates (both entries below). Found and fixed one real gap:
  architecture-document.md §6.2's guess-submission-and-scoring data-flow
  diagram/prose (the `[scheduled, at Round.EndTime]` block) had not been
  updated for ADR-0021's `MaterializeUnansweredCellsAsync` step — §5's
  COMP-04 status note already described it fully, but §6.2 still showed
  round-close as `Core.Scoring → Database` only, with no mention of the new
  `IRoundRepository`/`IGameModuleResolver`/`IGameModule.GetCellIdsAsync`
  dependency chain or the synthesized-`Guess`-row step. Added a bullet to
  §6.2's "what's built" prose and a corresponding block to the ASCII
  diagram itself describing the new step and its dependency edges to
  `Core.Rounds`/`Games.XGGrid` (COMP-05); version 0.27 -> 0.28. Checked and
  confirmed accurate, no further edit needed: implementation-document.md's
  `IGameModule` interface listing and §6a scoring pseudocode (verified
  line-by-line against the real `IGameModule.cs`, `ScoreLockingService.cs`,
  `ScoringRules.cs`, `UniquenessCalculator.cs`, `GridGameModule.cs`
  source), requirements-document.md's REQ-203/204/205/206/401/404/405
  updates, design-document.md's SCREEN-01a/SCREEN-03 mocks (point-value
  arithmetic and CSS class/token names cross-checked against
  `CellState.tsx`/`LeaderboardScreen.tsx`/`.css`), backlog.md's S-022/023/
  024/028 "Built as" and S-025/026/027 proposed entries, both ADRs
  (0020/0021), and every doc's frontmatter version/last_updated bump. No
  backend/frontend file changed by this diff was found undocumented. REQ-203,
  REQ-204, REQ-205, REQ-206, REQ-401, REQ-404, ADR-0021.
- 2026-07-12 — golf-style scoring model, S-028 (branch
  claude/points-ui-concerns-z9tvc2): direct follow-up product feedback,
  immediately after the S-022/ADR-0020 entry below shipped, asked for the
  opposite scoring direction from what was just built — rarer/more-unique
  correct answers should score FEWER points, and a player's/the
  leaderboard's goal is to MINIMIZE their total (golf-style), not maximize
  it. Two follow-up questions confirmed before implementation (not
  assumed): an incorrect guess scores the max penalty (0 is now the *best*
  score, so a wrong guess must never tie the best correct one), and an
  unanswered cell is penalized the same as a wrong guess for any round a
  player participated in. New `docs/decisions/0021-golf-style-lowest-wins-
  scoring.md` — builds on ADR-0020 (does not revert it; `uniqueScore`
  itself is unchanged, only its mapping to points is inverted).
  `ScoringRules.PointsFromUniqueScore` inverted
  (`round((1 - uniqueScore) * MaxPointsPerCell)`); incorrect guesses now
  lock at `MaxPointsPerCell`; `LeaderboardService` sorts ascending. New:
  `IGameModule.GetCellIdsAsync` (implemented in `GridGameModule`),
  `ScoreLockingService.MaterializeUnansweredCellsAsync` (penalizes a round
  participant's unattempted cells at round close, resolved through
  `IGameModule` per ADR-0003, never a direct game-table read),
  `IGuessRepository.AddRangeAsync`. requirements-document.md: REQ-203/204/
  205/206/401/404/405 all updated (glossary, status notes, acceptance
  criteria — "lowest wins," incorrect/unanswered = max penalty, leaderboard
  sort ascending); version 0.36 -> 0.37. architecture-document.md: COMP-04
  status note, §6 leaderboard data-flow diagram's sort direction, ADR
  table gained both ADR-0020 (missing from a prior pass — added now) and
  ADR-0021; version 0.26 -> 0.27. implementation-document.md: §6a
  pseudocode rewritten for the materialization step and inverted formula,
  `IGameModule`'s interface listing gained `GetCellIdsAsync`, REQ-607's
  pagination pseudocode's `ORDER BY` flipped to `ASC`; version 0.38 ->
  0.39. design-document.md: SCREEN-03's mock re-sorted ascending with a new
  "Lowest total wins" subtitle line (`LeaderboardScreen.tsx`/`.css` gained
  the matching `leaderboard-screen__subtitle`, `text-muted` token only, no
  new color); SCREEN-01a's state-1 mock corrected from "~12 pts estimated"
  to "~88 pts estimated" for its own "12% unique" example (was
  inconsistent with the formula even before this ADR) and state-3's "no
  attempts left · 0 pts" corrected to "100 pts", each with a short
  ADR-0021 explanatory note; version 0.12 -> 0.13. backlog.md: S-028 added
  as a completed "Built as" story. Every existing REQ-204/205/401/404-named
  backend test recomputed by hand against the corrected formulas (no
  dotnet SDK in this environment, same limitation S-018/S-022 recorded);
  new tests added for unanswered-cell materialization (a participant's
  missed cell, a non-participant's exemption, idempotency across repeated
  round-close calls) and for `IGameModule.GetCellIdsAsync` itself; frontend
  suite 72/72 green (`npm run test`), `tsc -b`/`npm run lint` clean. Flagged,
  not fixed (pre-existing, unrelated to this ADR): `CellState.tsx`'s state 3
  (incorrect, no attempts left) still renders no point value at all — a gap
  predating S-011 that the design doc's mock has always shown but the
  component never built; left as-is rather than scope-creeping a new
  feature into this change. REQ-203, REQ-204, REQ-205, REQ-206, REQ-401,
  REQ-404, REQ-405, ADR-0021.
- 2026-07-12 — points-ui-concerns (branch claude/points-ui-concerns-z9tvc2):
  three real bugs found via direct product feedback, fixed, and documented
  as S-022/023/024; three larger feature requests from the same feedback
  (admin UI, self-account deletion, leaderboard time-window resolutions)
  drafted as new requirements and queued as S-025/026/027 rather than
  implemented in the same session, per this repo's one-story-per-session/PR
  convention. requirements-document.md: REQ-204/205 status notes and the
  glossary's "Uniqueness score" definition corrected for S-022's formula fix
  (a lone/first correct guesser now scores 100% unique, not 0% — see
  ADR-0020); new REQ-405 (leaderboard time-window resolutions, explicitly
  left with open design questions, not implementation-ready as written) and
  new REQ-504/505/506 (admin UI page, admin round control, admin user
  deletion) added as "Status: Proposed, not yet implemented"; REQ-504
  amended post-architecture-review to require the round-control/user-
  deletion sections be hidden entirely (not just non-functional) outside
  Production, per ADR-0006's fail-closed pattern; version 0.34 -> 0.36.
  architecture-document.md: one-line COMP-04 status note for
  S-022 (no boundary/data-flow change, pure formula fix); version 0.25 ->
  0.26. implementation-document.md: §6a pseudocode rewritten for S-022's
  self-exclusion formula, "Tier 0 status" note updated; version 0.37 ->
  0.38. backlog.md: S-022 (uniqueness formula fix), S-023 (live-meta-
  disclosure second-click-doesn't-close fix), and S-024 (leaderboard
  auto-refresh polling) added as completed "Built as" stories; landing-page
  routing concern verified already correct via S-021, recorded as such (no
  new story); S-025/026/027 added as proposed-not-built stories for the
  three larger feature requests. New `docs/decisions/0020-uniqueness-
  formula-excludes-self-comparison.md` — reverses a previously-recorded
  "not a bug" decision from S-011 (see the ADR for the full history and
  why the self-inclusive formula was wrong, not just incomplete). Backend
  test suite could not be executed in this environment (no dotnet SDK
  available, same limitation S-018 recorded) — an architecture-reviewer and
  code-reviewer pass both ran against the diff instead; the code-reviewer
  hand-verified the scoring arithmetic (clean) and caught a real second bug
  the S-023 fix had missed (the identical hover-suppression problem also
  existed on the keyboard-focus path, worse: a panel could get stuck open
  after an odd number of Enter presses then tabbing away), fixed the same
  way with a mirrored `keyboardSuppressed` flag, plus two smaller gaps in
  S-024's polling (swallowed background errors now logged; `setInterval`
  swapped for a self-rescheduling `setTimeout` so at most one fetch is
  ever in flight) — S-023/024's "Built as" notes above updated to record
  both fixes. Frontend suite (71/71, including the new keyboard-focus
  regression tests) run and green after all fixes. REQ-204, REQ-205,
  REQ-206, REQ-401, REQ-404, REQ-405, REQ-504, REQ-505, REQ-506, REQ-710,
  ADR-0020.
- 2026-07-12 — doc-sync for S-021 (branch claude/story-s-021-h1qbxp):
  requirements-document.md's REQ-303 was already updated by the author
  (user story/acceptance criteria now describe "open the app, select a
  game, see that game's current round," plus a bullet noting the endpoint
  contract is unchanged) — verified accurate, no further edit needed.
  design-document.md's §7 open-questions bullet flagging the missing
  SCREEN-xx spec for the new game-selection landing screen — also verified
  accurate, no further edit needed. architecture-document.md — checked, no
  change needed: an architecture-reviewer pass confirmed no `COMP-xx`
  boundary touched (pure frontend routing, no backend endpoint added or
  changed, `XG_GRID_GAME_KEY` has no coupling to `GridGameModule`'s backend
  `GameKey`), and architecture-document.md's data flows (§6) don't describe
  frontend screen routing at all. implementation-document.md §4 — added a
  `/games` entry to the frontend project-structure tree (new
  `GameSelectScreen`, S-021) and corrected the `/tests/unit` note, which
  had said only the pre-S-010 App/health-check test remained there — no
  longer true now that REQ-303's game-selection routing tests were added to
  the same `App.test.tsx` (App.tsx isn't under a feature folder, so its
  tests still live in /tests/unit rather than co-located under /src);
  version 0.36 -> 0.37. backlog.md — added a "Built as:" note to S-021
  covering the header "Games" nav button (a deviation from the original
  story text, added as the natural way back to the landing screen), the
  code-reviewer-flagged comment on the discarded `gameKey` argument in
  `App.tsx`, and the added "Games" nav round-trip test; no frontmatter
  bump (none exists). No new ADR — confirmed a pure frontend routing
  change with no component/boundary change. REQ-303, ADR (none).
- 2026-07-12 — doc-sync for S-020 (branch claude/story-s-020-pm8xzq):
  design-document.md's §2 "Rejected-guess cue" paragraph and SCREEN-01a
  state 2/3 mock annotations (already added by the author in the first
  commit, before implementation, per CLAUDE.md's rule against undocumented
  animations) verified accurate against the final, bug-fixed code — its
  "fires on every rejected guess" line is now actually true after the
  second commit's fix, so no further edit needed; version 0.11 confirmed
  correct as-is, not bumped further. requirements-document.md and
  architecture-document.md — checked, no change needed: no REQ describes
  this animation (REQ-210's two-guesses-per-cell acceptance criteria don't
  mention UI feedback, same gap S-015 left undocumented for the
  correct-guess case), and architecture-document.md has no mention of
  frontend animation at all — this is a frontend-only presentational
  addition inside the existing `CellState` component, no new `COMP-xx`,
  API surface, or data flow. backlog.md — added a "Built as:" note to
  S-020 covering the `useShakeToken` hook/keyframes, the clean
  architecture-reviewer pass, the code-reviewer-found bug (a cell's
  first-ever rejected guess mounted `CellState` directly into the rejected
  state, indistinguishable from a page-reload mount without the new
  `submittedThisSession` prop) and its fix, and the identical
  `useRevealToken`/first-correct-guess gap deliberately left unfixed (out
  of scope, same pattern as other acknowledged-gap notes in the backlog).
  No frontmatter bump for backlog.md (none exists).
- 2026-07-12 — doc-sync for S-019 (branch claude/story-s-019-bs4t7x):
  design-document.md's SCREEN-01a state-1 mock (already reworded by the
  author, ahead of this pass, to show an "at rest"/"revealed" split plus a
  new explanatory paragraph) and requirements-document.md's REQ-204 status
  note/acceptance criteria (already updated the same way) both verified
  accurate against the final code — including the click/hover/focus
  interaction semantics (click toggles a persistent open/closed state;
  hover and keyboard-focus each independently reveal transiently and close
  on mouseleave/blur; the three combine via OR, so e.g. hovering keeps the
  panel open across an intervening click) and the Playwright
  `kAriaDisabledRoles` claim behind `GridCell.tsx`'s new
  `role="group"` div (confirmed directly against
  `playwright-core`'s bundled source: `"group"` is in that list, a bare
  `<div>`'s implicit role is not) — no further edit needed to either doc
  beyond what the author already made; versions 0.9 → 0.10 (design) and
  0.32 → 0.33 (requirements) confirmed correct as-is. architecture-document.md
  and implementation-document.md — checked, no change needed: this story
  touches zero backend `COMP-xx` components, no new API surface, and no new
  data flow (the existing `GET /rounds/current` → `UniquePercent`/
  `LivePoints` data flow in architecture-document.md §6 already stops at
  the API boundary and says nothing about frontend disclosure UI; the
  implementation-document.md frontend folder listing (§4) is unchanged —
  `LiveMetaDisclosure` is a sub-component inside the existing
  `CellState.tsx`, not a new file). backlog.md — added a "Built as:" note
  to S-019 covering the `LiveMetaDisclosure` three-flag
  (click/hover/keyboard-focus) design and the click-before-focus race bug
  it fixes (found via a code-reviewer pass mid-implementation), the
  `GridCell.tsx` button→`div role="group"` restructure and why, the new
  `GridCell.test.tsx` file, and the final 54/54 frontend test count
  (`npm run test`/`tsc -b`/`npm run lint` all clean; no backend files
  changed, so no `dotnet test` run for this story). REQ-204.
- 2026-07-12 — doc-sync for S-018 (branch claude/story-s-018-of5t7c):
  requirements-document.md's REQ-204 entry — reworded the S-018 addition and
  its two new acceptance-criteria bullets to name the actual extracted
  method, `ScoringRules.PointsFromUniqueScore(double)`, rather than just
  restating the formula (`RoundEndpoints`'s new `LivePoints` and
  `ScoreLockingService`'s existing `FinalPoints` now call the same method
  instead of two independently-written copies of `round(uniqueScore *
  MaxPointsPerCell)`), and updated REQ-205's status note the same way;
  version 0.31 → 0.32. architecture-document.md — documented
  `ScoringRules.PointsFromUniqueScore` in the COMP-04 status note (§5) as
  the formula's single shared entry point, and updated §6's data-flow prose
  to mention the new `LivePoints` field on `GET /rounds/current`; version
  0.24 → 0.25. design-document.md — SCREEN-01a's state-1 mock now shows
  "~N pts estimated" alongside the live uniqueness %, with a note on why
  that wording is deliberately distinct from state 4's locked "Y pts", and
  named `ScoringRules.PointsFromUniqueScore` explicitly rather than
  restating the formula; also added the same live-points mention to
  SCREEN-01's top-level "a live cell" bullet, which had drifted out of sync
  with SCREEN-01a; version 0.8 → 0.9. implementation-document.md — the
  REQ-204/205 pseudocode's "Tier 0 status" note and the
  `MAX_POINTS_PER_CELL` paragraph both only described the pre-S-018 shared
  `UniquenessCalculator`; added the S-018 `PointsFromUniqueScore` extraction
  to both, since this doc's job is to track the concrete implementation
  most literally; version 0.35 → 0.36. backlog.md — added a "Built as:"
  note to S-018 covering the `PointsFromUniqueScore` extraction, the
  frontend wiring, and the deliberate additive-assertion-over-new-tests
  deviation for the 3 pre-existing REQ-204 API tests (no frontmatter —
  backlog.md is not one of the three versioned governing docs).
  REQ-204/REQ-205.
- 2026-07-11 — doc-sync for S-017 (branch
  claude/story-s-017-displayname-pk0ct1, commits 5a8e195/710e896/240bc54):
  requirements-document.md's REQ-701 status note (added directly by the
  author, ahead of this pass) verified accurate against the final code, no
  further edit needed. architecture-document.md — added ADR-0019 to §10's
  table (was missing) and a new "COMP-01 status (S-017)" note documenting
  `User.NormalizedDisplayName`'s unique index and its pre-check/DB-backstop
  shape; version 0.23 → 0.24. implementation-document.md — the `User`
  entity code block and the "Required indexes" table were both missing
  `NormalizedDisplayName`/its unique index entirely (drifted ahead of this
  pass); added both, referencing ADR-0019 for the migration's
  collision-resolution step; version 0.34 → 0.35. backlog.md — added a
  "Built as:" note to S-017 summarizing the `NormalizeCase` extraction, the
  `ILogger`/`DisplayNameConflictProblem` code-review fixes, the ADR-0019
  addition, and the final 228-test count. This pass ran while commit
  240bc54 (the `NormalizeCase` extraction, `ILogger`/
  `DisplayNameConflictProblem` fixes, and trim+case test) was still
  uncommitted working-tree state; it has since been committed and pushed,
  resolving what would otherwise be an open question here. REQ-701,
  ADR-0019.

- 2026-07-11 — doc-sync for S-016 (branch claude/story-s-016-t31r8j, commit
  08ab8b2): requirements-document.md (REQ-701) — added the confirm-password
  Given/When/Then clause to the acceptance criteria and updated the status
  note to record it as built and enforced both server-side
  (`AuthController.Signup`, checked before the DisplayName/AgeConfirmed
  pre-checks and before Supabase Auth is ever called, same discipline as
  ADR-0013) and client-side (`AuthScreen.tsx`), matching the existing
  age-checkbox/DisplayName pattern; version 0.30 → 0.31. backlog.md — added
  a "Built as:" note to S-016 summarizing the implementation (matches the
  plan exactly, no deviations) since this wasn't done during
  implementation. architecture-document.md and implementation-document.md
  checked against the diff and left unedited: `ConfirmPassword` is a
  request-only DTO field, never persisted (same category as the existing
  `AgeConfirmed` field, which neither doc mentions at the field level) —
  unlike `DisplayName`, which is a persisted `User` column and is
  documented in implementation-document.md's data model. No component,
  boundary, or data-flow change; no new ADR — an architecture-reviewer pass
  already confirmed this before this doc-sync ran. 220 backend / 39
  frontend tests green. REQ-701.

- 2026-07-11 — doc-sync for S-015 (branch claude/s-015-badge-dock-hs9b42,
  commits 23b889b/0e069ae): no docs edited this pass — checked
  requirements-document.md (REQ-204/205), architecture-document.md, and
  implementation-document.md against the diff and found each already
  accurate. REQ-204/205's acceptance criteria describe the live/final
  *data* distinction, not the reveal animation itself, and neither cites
  design-document.md's badge-dock spec — consistent with the existing
  pattern of the animation living entirely in design-document.md §2/
  backlog.md's S-015 entry with no REQ ID (S-020's incorrect-guess
  animation entry is the same pattern). Confirmed design-document.md §2
  already fully specified the badge dock before S-015 built it, so no
  design-doc edit was needed either. architecture-document.md has no
  component-level entries below `CONT-01` (Web Frontend) for individual
  React components, so the `CategoryGlyph` extraction and `CellState`
  reveal-token logic are below this doc's granularity — no boundary or
  data-flow change. implementation-document.md §4's project-structure
  listing already names `CategoryLabel`/`CellState` at the file level
  (not per-export), same depth as before S-015 — `CategoryGlyph` is a new
  export within the existing `CategoryLabel.tsx` file, not a new
  component, so no line changes there either. Already went through
  architecture-reviewer and code-reviewer per S-015's own workflow. No new
  ADR — no decision here was architecturally significant enough to
  reasonably have gone another way.

- 2026-07-11 — doc-sync for S-014 (commit 689bab5): docs/implementation-document.md
  (version 0.33 → 0.34), docs/decisions/0010-guess-time-live-verification.md
  (no frontmatter to bump) — fixed two remaining `MinValidAnswers`
  default-value mentions (3 → 5, REQ-101) that the S-014 commit itself
  missed (it had already updated `GridGenerationOptions.cs` and
  requirements-document.md). Checked docs/architecture-document.md,
  docs/backlog.md, and this file for other stale mentions: none found —
  the remaining "default 3" references in docs/backlog.md and this file
  are historical narrative describing the pre-change value, not stale
  claims about current behavior, so left as-is. No component boundary or
  data flow changed, so no ADR.

- 2026-07-11 — docs/backlog.md (Epic 5 extended: S-021) — reconsidered the
  post-login game-selection landing page after re-checking it specifically
  for contradictions rather than just "is it in scope." No REQ/ADR
  outright forbids it, but it sits in tension with REQ-303's own user
  story ("open the app and see the current round's grid") and would break
  the existing S-010 E2E flow (`play-grid.spec.ts`) that goes straight
  from signup to the grid — both called out as required updates within
  S-021's scope, not silently left inconsistent. No backend "list games"
  endpoint needed (confirmed no `COMP-xx` for a game catalog exists) since
  Tier 0 only ever has one game — S-021 is a static single-tile landing
  screen, not new backend surface.

- 2026-07-11 — docs/backlog.md (Epic 5 extended: S-016 through S-020) —
  follow-up to the same day's Tier 0 findings triage. Worked through the
  items previously flagged as open product decisions with the user; five
  more were confirmed in-scope and added as backlog stories: S-016 (signup
  repeat/confirm password), S-017 (display-name uniqueness, spaces still
  allowed — REQ-401/701), S-018 (live indicative points per cell, clearly
  marked provisional — REQ-204/206), S-019 (tap/long-press reveal of
  per-cell live text instead of always-on, to reduce clutter across a
  grid's live cells — REQ-204/SCREEN-01a redesign), S-020 (incorrect-guess
  shake + red-flash animation, reduced-motion fallback — SCREEN-01a
  extension). Three items stay explicitly open/deferred, not scoped: a
  post-login game-selection landing page (no second game exists yet), a
  scheduled cache pre-warming job (no evidence on-demand fetching is
  actually a problem), and selectable color themes/dark mode
  (design-document.md already tracks this as a deliberately unresolved
  question — left that way rather than resolved here).

- 2026-07-11 — docs/backlog.md (new Epic 5: S-014, S-015) — triaged a batch
  of Tier 0 play-testing findings against `MVP-SCOPE.md`'s Tier 0/Tier 1
  split. Two findings were genuine Tier 0 gaps and added as new backlog
  stories: S-014 (raise `MIN_VALID_ANSWERS` default 3→5, REQ-101) and S-015
  (build the already-designed but never-implemented "badge dock" guess
  animation, `design-document.md` §2/SCREEN-01a). No Tier 1 trigger was
  confirmed fired by this round of findings. The remaining findings (live
  points display, reducing per-cell live text, an incorrect-guess
  animation, a post-login game-selection landing page, selectable color
  themes, display-name uniqueness/format, a signup repeat-password field)
  were flagged as open product decisions, not scoped into any story —
  requirements-document.md/design-document.md left otherwise unchanged
  pending those decisions.

- 2026-07-11 — doc-sync verification of the S-013 entry below:
  docs/design-document.md (wording only, no version change), docs/CHANGELOG.md
  (this entry's own section references), docs/backlog.md — three section/
  wording inaccuracies fixed. (1) The §6/§7 references describing the
  gold-on-white/green-on-white contrast fix were wrong: the open item lived
  in design-document.md §6 ("Accessibility and quality floor"); §7 ("Open
  questions") never named it and wasn't touched by that diff — corrected in
  design-document.md's own prose and in this file's S-013 entry below. (2)
  backlog.md's pre-existing S-013 acceptance criteria said "deployed prod
  URL," inconsistent with the same story's own "Built as" note (added this
  session) and with the rest of the repo, which has called Tier 0's only
  environment "dev" since the 2026-07-07 prod→dev rename (see this file's
  earlier entry on that rename) — corrected to "deployed dev URL." Verified
  the rest of the S-013 documentation (backlog.md, TODO.md, NOTES.md, the
  design-document.md token/CSS changes, and the play-grid.spec.ts timeout
  fix) against the actual diff and a live re-run of the backend (218 NUnit
  tests, 5 projects) and frontend (30 Vitest tests) suites: accurate,
  no further changes needed. requirements-document.md and
  architecture-document.md correctly left unchanged — no REQ acceptance
  criteria or component boundary changed by this diff, and there is no
  REQ ID for accessibility/contrast to begin with.

- 2026-07-11 — docs/design-document.md (version 0.7 → 0.8, §2/§6),
  docs/backlog.md (S-013 entry), TODO.md, NOTES.md — S-013 (First-release
  QA pass). Ran the full local-stack test suite for real for the first
  time this session (backend: 218 NUnit tests across 5 projects; frontend
  unit: 30 Vitest tests; E2E: `tests/e2e/play-grid.spec.ts` +
  `app-loads.spec.ts` against a locally-run Postgres 16 + the real API +
  Vite dev server, this sandbox's substitute for `ci.yml`'s Docker-based
  service container, since no Docker daemon is available here). Found and
  fixed one real bug the suite had never actually caught before: the E2E
  spec's dialog-close assertions were sized for a pre-ADR-0018 cache-only
  guess-submission latency (5s), but REQ-211/ADR-0018's live-lookup
  fallback (built after this spec was last touched) means any guess that
  misses cache now costs one live Wikidata HTTP round trip — bounded by
  ADR-0011's own 15s timeout, with 9-27s observed for real WDQS queries —
  before the response returns. Widened only the assertions that follow a
  cache-missing guess (20s) and the spec's overall per-test timeout (60s);
  no product code changed, no ADR revisited — see backlog.md's S-013 entry
  and NOTES.md for the full diagnosis. Resolved design-document.md §6's
  long-open "verify gold-on-white/green-on-white contrast" item: computed
  WCAG contrast found both `accent-gold` (~2.6:1) and `accent-green`
  (~3.4:1) fail their applicable floors when used as text/icon/button-label
  color against `surface-card`; added `accent-gold-text`/
  `accent-green-text` (darkened, same-hue, ~4.9:1/~5.1:1) to §2 for that
  use, leaving the original tokens for their existing non-text/decorative
  uses (which already clear the 3:1 non-text floor as-is). Applied across
  `CellState.css` (the four cell states this story's acceptance criteria
  names), `GuessInput.css`/`AuthScreen.css`'s submit buttons, and
  `LeaderboardScreen.css`'s "you" tag (same bug class, found during the
  same pass). Not performed, flagged instead: the manual smoke test
  against the deployed dev URL and a live rejected-guess spot-check both
  need network access this sandbox doesn't have (same `wikidata.org`-proxy
  limitation NOTES.md already records from S-006) — recorded as explicit
  TODO.md follow-ups rather than skipped silently. No new Tier 1 trigger:
  both real issues found were fixable inside Tier 0. No requirements-
  document.md/architecture-document.md change — nothing here changed a
  REQ's acceptance criteria or a component boundary. No new ADR — the
  contrast-token addition is refining an already-documented, unresolved
  gap in an existing doc (design-document.md §6 already named the
  question), not a new structural decision with real alternatives; the
  E2E timeout fix is a test-correctness fix, not a design choice.

- 2026-07-10/11 — docs/requirements-document.md (version 0.29 → 0.30),
  docs/architecture-document.md (version 0.22 → 0.23),
  docs/implementation-document.md (version 0.31 → 0.33, merged with S-012's
  independent 0.31 → 0.32 bump below), MVP-SCOPE.md,
  docs/decisions/0010-guess-time-live-verification.md (status line
  annotated), docs/decisions/0018-req-211-tier-0-without-playername-index.md
  (new, then extended) —
  Fixed a reported major bug: genuinely correct guesses (e.g. Messi for
  Argentina×Barcelona) were wrongly marked incorrect because grid
  generation's cache-based validity check (REQ-101/MinValidAnswers) only
  ever needed to prove a cell had *some* cached answers, never every one
  (ADR-0010's documented gap). `GridGameModule.ScoreSubmissionAsync` now
  falls back to a live Wikidata lookup (re-running the cell's own
  country×club query) when cached data doesn't already answer a guess,
  pulling REQ-211 forward from Tier 1 once MVP-SCOPE.md's own trigger for
  it fired — but without its `PlayerNameIndex` prerequisite (still Tier 1,
  see ADR-0018 for why that's safe for Tier 0). Follow-up pass
  (test-writer + architecture-reviewer) expanded coverage to 8
  `REQ211_ScoreSubmissionAsync_*` tests in `GridGameModuleTests.cs`
  (including the exact reported repro shape — a player already cached with
  one category from an unrelated cell — plus the non-Country/Club and
  unresolvable-reference-table guard clauses and a single-call assertion
  for the fallback), and extended `FakeWikidataLookupService` with
  `GetCallCount` to support it. Same pass closed doc-completeness gaps this
  surfaced: REQ-203's status note corrected to match REQ-211's new
  behavior, ADR-0018 added to architecture-document.md §10's ADR table,
  ADR-0010 annotated to point at ADR-0018's further revision of its trigger
  condition, architecture-document.md's boundary-rule-1 worked example and
  §8 "Consistency of correctness" row updated to state the new live-call
  trade-off explicitly rather than silently contradict it, and
  implementation-document.md §6's guess-scoring pseudocode's Tier 0 status
  notes corrected (previously said the REQ-211 live-lookup block "does not
  exist," which is no longer true) — REQ-101/REQ-103/REQ-203/REQ-211,
  ADR-0010/ADR-0018.
- 2026-07-10 — docs/requirements-document.md (version 0.29 → 0.30),
  docs/architecture-document.md (version 0.22 → 0.23),
  docs/implementation-document.md (version 0.31 → 0.32), docs/backlog.md
  (S-012 entry) — doc sync for S-012 (Admin data correction, REQ-501/502/
  503). REQ-501: added a status note — the override-precedence merge logic
  predates this story, S-012's addition is the admin-facing
  `POST/GET/PUT/DELETE /admin/player-overrides[/{id}]` CRUD behind the new
  "Admin" authorization policy, covered end-to-end by
  `REQ501_CreatePlayerOverride_FlipsCellCorrectness_ForSubsequentGuess`.
  REQ-502/503: added status notes recording real gaps against the full
  acceptance criteria — `GET /admin/player-data/unverified` only surfaces
  unverified rows (not "any player data point," REQ-502) and there is no
  approve-to-verified or remove-the-data-point action (REQ-503) — no new
  REQ text invented, just grounding against what's real, same pattern as
  REQ-701's existing status note. architecture-document.md: added a "Tier 0
  status (S-012)" note to §6.3's data sync flow (no prior status note
  existed there) recording which half of that diagram is now real, and a
  one-line addition to COMP-06's row noting `AdminEndpoints` as a second
  caller reached only through `IPlayerStoreRepository` — no boundary
  change. implementation-document.md: updated §4's security-pipeline "Tier
  0 status" note — admin authorization is now wired (was previously "not
  yet implemented, S-012's job"); rate limiting remains the one
  still-unbuilt pipeline step. backlog.md: added S-012's "Built as:" note
  (previously empty), following the S-009/S-010/S-011 pattern — notes the
  deliberate backend-only scope (no admin page/SCREEN-04) and the specific
  REQ-503 actions not built. No ADR added (architecture-reviewer and
  code-reviewer both confirmed this implements an already-decided design
  from implementation-document.md §4, not a new structural choice).
  design-document.md and decisions/ untouched — no frontend work, no new
  architecturally significant decision.
- 2026-07-10 — docs/requirements-document.md (version 0.28 → 0.29),
  docs/architecture-document.md (version 0.21 → 0.22),
  docs/implementation-document.md (version 0.30 → 0.31),
  docs/legal/privacy-policy-draft.md (version 0.3 → 0.4), docs/backlog.md
  (S-011 entry) — doc sync for S-011 (Scoring + leaderboard, REQ-204/205/
  206/401). REQ-204: status flipped to Implemented — `UniquenessCalculator`
  (`XGArcade.Core.Scoring`) now backs a live `UniquePercent` on `GET
  /rounds/current`. REQ-205: status updated to reflect `IScoreLockingService`
  /`ScoreLockingService` locking `FinalUniquenessScore`/`FinalPoints` at
  round close (still no production scheduling job — that gap remains).
  REQ-206: added an explicit status note recording a real, non-regression
  gap — `ScoreCalculator.CalculateTotalPoints` is correct and tested, but
  there is nowhere to view one round's total distinctly from the
  leaderboard's all-time running total (no past-round-detail screen yet).
  REQ-401: added a status note (COMP-02/Core.Leagues' first real code —
  auto-enrollment at signup via `ILeagueRepository`). REQ-404: added a
  status note (global league only; unbounded response, see REQ-607).
  REQ-607: added a status note recording the leaderboard's unbounded
  response as a real, acknowledged (not tiered-out) gap against its own
  pagination clause, with an explicit revisit trigger — flagged by an
  architecture-reviewer pass, deliberately not fixed this story. REQ-701:
  added a `DisplayName` (1-30 chars) acceptance criterion and updated its
  status note — this is a deliberate, explicitly-confirmed scope addition
  (not a silent expansion) so the leaderboard never has to show another
  player's email. REQ-807: recorded its extension (`AlternateCorrectPlayerName`
  in the seed response, needed for a meaningful REQ-204 uniqueness test).
  Fixed a real pre-existing bug in implementation-document.md §6's
  "Uniqueness score" pseudocode, unrelated to new drift from this story:
  the `totalGuesses`/`sameAnswer` denominator/numerator still counted ALL
  guesses including incorrect ones, the exact bug
  review-2026-07-07-design.md finding 2 already fixed in the real
  implementation and in REQ-204's own prose — this one pseudocode block
  had just never been updated to match; now reads `WHERE ... AND IsCorrect
  = true`. Recorded `MAX_POINTS_PER_CELL = 100`
  (`ScoringRules.MaxPointsPerCell`) as the resolved Tier 0 default for a
  previously-unspecified placeholder. Updated the "Leaderboard pagination"
  section with a Tier 0 status note (built: the aggregation query, for the
  global league only, unpaginated; not built: the `{leagueId}` route and
  cursor/offset pagination itself). Added a `User.DisplayName` field and a
  `League` filtered-unique-index row to the data model/required-indexes
  sections, and a `/leaderboard` line to the frontend project-structure
  tree. architecture-document.md: added a "COMP-02 status (S-011)" note
  mirroring COMP-04's own S-009 note, updated COMP-04's status note to
  describe the now-built uniqueness/score-locking code (including that an
  architecture-reviewer pass caught this logic initially misplaced in
  `Core.Rounds`/the API layer and had it extracted before merge — no new
  ADR needed, this was a fix, not a new structural decision), updated
  §6.2's data-flow diagram caveats (the "not built... deferred to S-011"
  bullets for REQ-204's live-read and REQ-205's round-close-lock are now
  stale and were corrected to describe what's actually built, including
  one new attribution note: the live-uniqueness read happens on a separate
  `GET /rounds/current` request, not inline in the guess-submission
  response), and added a new §6.2a data-flow diagram for the
  signup-auto-enrollment and global-leaderboard-read flows (REQ-401/404),
  which had no diagram before. docs/legal/privacy-policy-draft.md: added
  DisplayName under "what we collect" and a new "Other players" bullet
  under "who we share it with" — display names (never email addresses) are
  now visible to every other player on the leaderboard, a new
  visible-to-third-parties-shaped exposure this draft needs to reflect.
  docs/backlog.md: updated S-011's entry with a "Built as:" note (mirroring
  S-010's own) covering the DisplayName addition, the REQ-807 extension,
  the architecture-reviewer extraction fix, and the REQ-607 gap; confirmed
  S-010's entry needed no change (it doesn't reference the old
  single-player seed-response shape). No new ADR: the
  architecture-reviewer-flagged component misplacement was fixed by
  extraction, not documented as a permanent decision, so ADR-0001/0002/
  0003/0007/0014/0015/0016 remain accurate as-is. REQ-204/205/206/401/404/
  607/701/807.

- 2026-07-10 — docs/decisions/0017-supabase-jwks-validation.md (new),
  docs/architecture-document.md (§6.4 auth-flow status note, §10 ADR
  table), docs/implementation-document.md (JWT validation specifics),
  MVP-SCOPE.md (precondition secrets checklist), SETUP.md (JWT secret
  step removed, both secrets tables, both manual-deploy examples),
  infra/README.md (both secrets tables, both manual-deploy examples, new
  `supabaseJwksPath` override note) — fixed a real production bug found
  while manually testing the deployed dev environment after S-010: signup
  and login both succeeded, but every subsequent authenticated request was
  silently rejected (401), bouncing the player straight back to the login
  screen. Live log-stream debugging traced this to `IDX10503: Signature
  validation failed... Number of keys in Configuration: '0'` — the
  deployed Supabase project signs tokens with its newer asymmetric JWT
  Signing Keys system (a `kid` header claim identifies the rotating key),
  not the static HS256 shared secret `Program.cs`'s JWT validation
  (`Auth:SupabaseJwtSecret`, built under ADR-0013) assumed. No secret
  value could ever have fixed this — replaced with JWKS-endpoint
  validation via a new `SupabaseJwksConfigurationRetriever`
  (`XGArcade.Api.Auth`) feeding a `ConfigurationManager
  <OpenIdConnectConfiguration>` (framework's own async caching/refresh,
  not a hand-rolled blocking resolver — see ADR-0017 for why that
  distinction matters and the alternatives considered), with the JWKS path
  configurable (`Auth:SupabaseJwksPath`) so a wrong path is a one-line env
  var correction, not a rebuild. `Auth:SupabaseJwtSecret`/
  `DEV_SUPABASE_JWT_SECRET` removed entirely, not left as dead config — no
  code reads it anymore and no live prod environment exists yet to
  accidentally depend on it (confirmed via `deploy.yml`: no prod deploy
  job exists). `Auth:Mode=local-e2e` (CI's fake in-process auth) is
  unchanged; the three `XGArcade.Api.Tests` files that previously minted
  their own JWT against the now-removed static-secret branch
  (`AuthEndpointTests`, `CurrentRoundEndpointTests`, `GuessEndpointTests`)
  were reconfigured to use `Auth:Mode=local-e2e` via a new
  `LocalE2EAuth.MintToken` method instead — API/unit tests must never
  depend on live network (`docs/coding-guidelines.md`), and the removed
  branch now requires it. Added `SupabaseJwksConfigurationRetrieverTests.cs`
  (the one genuinely new piece of logic with no other coverage) — writing
  it caught a real bug in the first draft of the retriever itself: setting
  `OpenIdConnectConfiguration.JsonWebKeySet` does not auto-populate
  `.SigningKeys` (undocumented behavior of
  `Microsoft.IdentityModel.Protocols.OpenIdConnect` 8.0.1, verified
  directly against the resolved assembly), so `.SigningKeys` must be
  populated explicitly from `JsonWebKeySet.GetSigningKeys()`. A follow-up
  `code-reviewer` pass on this same branch (second commit) found one more
  gap in the retriever: a syntactically valid JWKS document with zero
  usable signing keys (an empty `keys` array, or every key missing fields
  `GetSigningKeys()` needs) would otherwise have silently reproduced the
  exact "Number of keys in Configuration: '0'" symptom this whole fix
  exists to make diagnosable, just one layer downstream in a generic
  authentication-failure log instead of at the source — the retriever now
  throws `InvalidOperationException` immediately in that case, covered by
  a new
  `GetConfigurationAsync_EmptyKeysArray_ThrowsRatherThanSilentlyProducingZeroKeys`
  test; the doc edits and ADR-0017 listed above already describe this
  corrected final state, not the first commit alone — no further doc
  change needed for this addition beyond this note. §6.4's
  auth-flow status note and the JWT validation paragraph in
  implementation-document.md updated to describe JWKS validation instead
  of a static secret; §10 gained a new ADR-0017 row. `MVP-SCOPE.md`'s
  precondition checklist, `SETUP.md`, and `infra/README.md` all had their
  "JWT secret" copy-step/secrets-table-row/manual-deploy-parameter removed
  and replaced with a note that JWT validation now derives from the
  already-saved Supabase project URL alone, plus documentation of the new
  `supabaseJwksPath` override escape hatch. No requirements-document.md
  change: REQ-606 describes JWT validation *behavior* ("the backend
  validates JWTs on every request"), not the signing algorithm, so this
  fix doesn't change any acceptance criteria. ADR-0017.

- 2026-07-10 — docs/design-document.md (§7 open questions, frontmatter
  version 0.6 → 0.7) — doc sync for S-010 (Grid UI, SCREEN-01/01a/02):
  flagged two open gaps found while implementing against this document
  rather than resolving them silently — (1) no SCREEN-xx spec exists for the
  login/signup screen, built functionally with tokens-only styling but
  unreviewed; (2) §2 has no numeric spacing scale, implementation used an
  unreviewed 4px-based scale — and recorded a third as fixed within this
  same story rather than left open: (3) `GET /rounds/current` originally
  never returned the guessed/revealed player's name, so SCREEN-01a could
  only show it for a guess submitted in the current browser session; closed
  by adding `SubmittedName` to that endpoint's response (REQ-303) before
  this story's UI work finished, so §7 records it struck through as
  "fixed," not as an open recommendation. No REQ/ADR changed by this
  specific edit; frontend code isn't tracked in this changelog per its own
  header note, but the design-doc edit is — the REQ-303 change itself is
  logged separately below.
- 2026-07-10 — docs/requirements-document.md (REQ-303, REQ-807),
  docs/architecture-document.md (§5 boundary rule 2, §10 ADR table),
  docs/decisions/0016-display-reads-bypass-igamemodule.md (new),
  docs/design-document.md (§7, one more flagged gap), docs/backlog.md
  (S-010 entry), docs/implementation-document.md (§1 tech-stack table, §4
  project structure, frontmatter version 0.28 → 0.29),
  docs/legal/privacy-policy-draft.md (§"Who we share it with", frontmatter
  version 0.2 → 0.3) — doc sync for the rest of S-010's diff beyond the
  design-doc pass logged above: two new backend endpoints the Grid UI
  needed to have anything real to render/seed against. **REQ-303** (`GET
  /rounds/current`, `XGArcade.Api.Rounds.RoundEndpoints`) — the read path
  for "the round I can currently play," resolving the caller from their
  bearer token and returning the active round's cells joined with only the
  caller's own `Guess` rows (`IRoundRepository.GetActiveByGameKeyAsync`,
  `IGuessRepository.GetByRoundAndUserAsync`, both new), including
  `SubmittedName` per the fix already logged above. **REQ-807** (`POST
  /internal/test-data/seed-guessable-round`, non-Production only, same
  discipline as REQ-806) — deterministically seeds a one-cell `GridInstance`
  plus a `Player`/`PlayerAttribute` pair that satisfies it, entirely through
  each component's normal repository writes (ADR-0006 boundary rule 4),
  used as Playwright E2E setup so the suite never depends on a live
  Wikidata call. **ADR-0016** (new): `architecture-reviewer` found that
  `GET /rounds/current` reading `GridInstance`/`GridCell` directly via
  `IGridInstanceRepository` is a genuine exception to ADR-0003's boundary
  rule 2 — not covered by the existing `GridTemplateResolver` precedent,
  which is about `GridTemplate` specifically (resolved before generation,
  not player data). Rather than design a speculative generic read method on
  `IGameModule` against a single game module, ADR-0016 records this as a
  narrow, Tier-0-scoped, display-reads-only exception (never for generation
  or scoring), with an explicit trigger to revisit once a second game module
  exists to design the real interface against; architecture-document.md's
  boundary rule 2 and REQ-303's status note were updated to reference it.
  `GuessRules.MaxAttemptsPerCell` was also extracted from a private constant
  in `GuessSubmissionService` to a shared `Core.Scoring` constant so
  REQ-303's read path and REQ-210's write path enforce/report the same
  attempt cap from one place — a pure refactor, no documented behavior
  changed, so no doc edit was needed for it beyond what §5's existing
  "capped at 2" note already said. design-document.md gained a fourth §7
  entry (added by a later commit in this same story, never logged until
  now): `code-reviewer` found §2 also has no type scale or border-radius
  scale, the same kind of gap as the already-logged spacing-scale one,
  citing the exact ad-hoc px values used across six component stylesheets.
  docs/backlog.md's S-010 entry was corrected, not just checked: its
  original accept criteria implied all four SCREEN-01a cell states were
  exercised through the Playwright suite, but the "round closed/final"
  state isn't reachable through `GET /rounds/current` yet (S-011 scope,
  same reason design-document.md's implementation note gives) and is only
  covered by `CellState.test.tsx` (Vitest, constructed props) — reworded to
  say so precisely, and to name REQ-303/REQ-807 as part of what this story
  built, not only the UI. docs/implementation-document.md gained a new
  tech-stack row for Google Fonts (`frontend/index.html` now loads Space
  Grotesk/Inter/IBM Plex Mono — already specified in design-document.md §2
  — directly from `fonts.googleapis.com`/`fonts.gstatic.com`) and its §4
  frontend project-structure block was corrected from the original
  `/components`/`/pages`/`/api` layer-folder sketch to the feature-folder
  layout actually built (`/src/auth`, `/src/grid`, `/src/lib`, with
  component tests co-located under `/src` rather than kept in a separate
  `/tests/unit` tree, per `docs/coding-guidelines.md`) — this is the same
  kind of "keep the illustrative shape honest" correction prior stories'
  doc-sync passes made for backend entities. docs/legal/privacy-policy-draft.md
  gained a new "Who we share it with" line for Google Fonts: loading fonts
  directly from Google's CDN in the browser means Google sees every
  visitor's IP address on every page load, a real third party this draft
  didn't previously name, per CLAUDE.md's rule that any change touching
  which third parties see data must update the legal draft in the same
  iteration — flagged back as worth a human call on whether to self-host
  the fonts instead, not decided here. Also corrected a stale claim in this
  same CHANGELOG file's own S-010 design-doc entry above (see that entry's
  rewritten text) — it described the `SubmittedName` gap as still open when
  the same commit that wrote it had already closed it. No REQ/ADR text was
  invented or renumbered; REQ-303/REQ-807/ADR-0016 were authored earlier in
  this same session/branch and are only being reconciled against the final
  code and logged here for the first time. REQ-303, REQ-807, ADR-0016.
- 2026-07-10 — docs/requirements-document.md (REQ-201, REQ-202, REQ-203,
  REQ-204, REQ-205, REQ-208, REQ-209, REQ-210, REQ-302),
  docs/architecture-document.md (§5 COMP-04/COMP-06 rows, §5 "Maps to"
  footnote, §5 boundary rule 1, §6.2 flow diagram status note, §10 ADR
  table), docs/implementation-document.md (§5 `Player`/`Guess` illustrative
  shapes, §5 required-indexes table, §6 `normalize()` formula and
  name-matching/disambiguation pseudocode status note, §6 uniqueness-score
  status note) — doc sync for S-009 (Guess submission): `Guess` entity
  (`XGArcade.Data`, COMP-04 per ADR-0014, same pattern as `Round`/COMP-03)
  with `PlayerAnswerId` nullable and a new `SubmittedName` field, both
  diverging from implementation-document.md §5's old illustrative shape;
  `Player.NormalizedFullName` (auto-maintained by `FullName`'s setter,
  backfilled via `PlayerNormalizedFullNameBackfiller`);
  `PlayerNameNormalizer` gained punctuation-stripping (closes a real
  pre-existing S-006 gap — REQ-208/MVP-SCOPE.md both called for it, the
  original implementation never did it); `IPlayerStoreRepository
  .GetPlayersByNormalizedFullNameAsync`/`HasEffectiveAttributeAsync`
  (override-aware, see ADR-0015); `Core.Scoring`'s first real code
  (`GuessSubmissionService`/`IGuessSubmissionService`/
  `GuessSubmissionResult`) — REQ-201/202/210's guess-acceptance,
  guess-change-policy, and attempt-cap/lock rules, checked before any name
  resolution work; `GridGameModule.ScoreSubmissionAsync` implemented
  (REQ-207/208/209's name-resolution, was `NotImplementedException`);
  `POST /rounds/{roundId}/cells/{cellId}/guesses`
  (`XGArcade.Api.Guesses.GuessEndpoints`), mapping every rejection outcome
  to a distinct `ProblemDetails` title (REQ-202). REQ-201/202/210 gained
  "Status: Implemented (Tier 0, S-009)" notes — their acceptance criteria
  are fully satisfied for what Tier 0 scopes them to. REQ-203 gained a
  "Status: Partially implemented" note: the override-precedence
  effective-data check and immediate correctness/lock are fully built, but
  it only ever runs against REQ-208's Tier 0-scoped candidates and never
  triggers REQ-211's live lookup (Tier 1, not built) — a genuinely correct
  guess for a real player with no cached `PlayerAttribute` data is
  currently scored incorrect, not looked up live. REQ-208 gained a
  precise "Tier 0's simple half only" status note: normalization
  (lowercase/diacritics/punctuation, now complete) is built; the
  maintained alias list and edit-distance fuzzy tolerance are not (both
  deliberately deferred per `MVP-SCOPE.md`, not oversights). REQ-209
  gained a matching status note: the auto-accept-when-exactly-one-fits and
  incorrect-when-none-fit branches are fully built; the
  more-than-one-fits branch is Tier 0's simplified handling (auto-accept
  lowest `Id`, logged) rather than the full disambiguation-prompt UI.
  REQ-204 gained a brief status note: still unimplemented (S-011), but the
  `Guess.PlayerAnswerId` data it will read is now being recorded correctly
  via REQ-209's deterministic lowest-Id pick. REQ-205's existing status
  note updated: `Guess`/`Core.Scoring`'s guess-acceptance half now exist
  (S-009), but `RoundCloseService` still doesn't read/write `Guess` at all
  and still computes no `final_uniqueness_score`/`final_points` (S-011) —
  the note previously implied `Guess`/`Core.Scoring` didn't exist yet at
  all, which is now stale. REQ-302's existing status note updated: "only
  active rounds accept new guesses" is now enforced
  (`GuessSubmissionService` checks `GetStatus` and rejects
  `RoundNotActive`), correcting the S-008-era note that said no
  guess-submission endpoint existed yet to enforce it. Architecture-
  document.md's COMP-04 row gained a "Maps to" detail and a new "COMP-04
  status (S-009)" note clarifying `GuessSubmissionService` is COMP-04's
  first real code, but REQ-204/205's actual namesake responsibility
  (uniqueness calculation, score locking) isn't built yet; COMP-06's row
  and boundary rule 1 gained pointers to the new ADR-0015; the §5 "Maps
  to" footnote now names COMP-04 alongside COMP-01/03/05 for the same
  "entity lives in `XGArcade.Data` despite the table's 'maps to' column"
  reason (`Guess`/`IGuessRepository`/`GuessRepository`); §6.2's guess-
  submission-and-scoring flow diagram gained a "Tier 0 status (S-009)"
  note (matching §6.1's established pattern) — the diagram misattributes
  two checks to the wrong component even for what Tier 0 built (round-
  active/guess-change-policy and the REQ-210 lock/attempt-cap check are
  both `Core.Scoring`, not `Core.Rounds`/`Games.XGGrid` as the diagram's
  arrows imply), and several branches aren't built at all yet
  (`PlayerNameIndex`/autocomplete, alias/fuzzy matching, REQ-209's
  disambiguation prompt, REQ-211's live lookup, REQ-204's live uniqueness
  calc, and REQ-205's round-close scoring — all Tier 1 or S-011, per
  `MVP-SCOPE.md`); §10's ADR table gained a row for ADR-0015 (already
  accepted and committed on this branch, not authored in this pass).
  Implementation-document.md §5's `Player` illustrative shape gained the
  real `NormalizedFullName` field it was missing; `Guess`'s illustrative
  shape fixed to match the built entity (`PlayerAnswerId` now nullable,
  new `SubmittedName` field) — same "keep the illustrative shape honest"
  precedent as S-007's `GridCell` fix; the required-indexes table's
  `Guess` row corrected from `(RoundId, UserId)` to the actually-built
  `(RoundId, UserId, CellId)` unique index (a plain `(RoundId, UserId)`
  index can't be unique — a user has many guesses per round), and gained a
  new `Player (NormalizedFullName)` row; §6's `normalize()` formula gained
  the punctuation-stripping step to match `PlayerNameNormalizer`; §6's
  name-matching/disambiguation pseudocode gained a Tier 0 status note
  (matching the existing grid-generation/uniqueness-score note pattern)
  spelling out exactly which lines are real (the two lock/cap checks,
  `normalize()`, the 0-and-1-candidate branches) versus deliberately
  unbuilt (alias/fuzzy matching, REQ-211's live lookup, the disambiguation
  prompt); §6's uniqueness-score status note corrected — it previously
  said `Guess` "doesn't exist as an entity until S-009," which is now
  stale since `Guess` exists as of this story; clarified that neither the
  live nor round-close halves of the calculation read it yet regardless
  (still S-011). docs/backlog.md's S-009 entry checked against the actual
  diff and found already accurate — no change made. MVP-SCOPE.md checked
  against the diff and confirmed nothing Tier 1 was pulled forward (no
  `PlayerNameIndex`, no alias table, no fuzzy tolerance, no disambiguation
  UI, no guess-time live lookup) — no change made. No new ADR needed for
  this pass: ADR-0015 (override replaces entire attribute type) was
  already authored and accepted on this branch, reviewed by
  architecture-reviewer prior to this doc-sync pass; this pass only added
  the cross-references to it from architecture-document.md that were
  still missing. `PlayerOverride.cs` (`XGArcade.Data.Entities`)'s own doc
  comment — flagged by this pass as still only saying "see REQ-501" with no
  pointer to ADR-0015's precedence semantics — was fixed directly afterward
  (source change, not a doc-sync edit). REQ-201/202/203/204/205/208/209/210/302,
  ADR-0015.
- 2026-07-10 — docs/requirements-document.md (REQ-301, REQ-302, REQ-205),
  docs/architecture-document.md (§5 table footnote, §6.1, §10 ADR table),
  docs/implementation-document.md (§5 Core-entities header comment, §6
  grid-generation and uniqueness-score pseudocode) — doc sync for S-008
  (Rounds + scheduling): `Round` entity + `IRoundRepository`/`RoundRepository`
  (`XGArcade.Data`, per ADR-0014, same pattern as `User`/COMP-01 and
  `GridTemplate`/COMP-05); `RoundGenerationService` implements REQ-301's
  one-round-ahead rule via the new `IGameModuleResolver`; `RoundStatusExtensions`
  implements REQ-302's live status calculation; `RoundCloseService` is
  REQ-205's close-only Tier 0 stub (real scoring lands in S-011 once
  `Guess`/`Core.Scoring` exist); `POST /internal/generate-round`
  (bearer-token-protected, every environment — CONT-05's real job) and
  REQ-806's `POST /internal/test-data/force-close-round/{id}` (non-Production
  only). `generate-round.yml`'s cron re-enabled; `RoundSchedulingOptions.RoundDuration`
  set to 4 days to match the longest gap in the cron's alternating Tue/Fri
  schedule (full derivation in NOTES.md). REQ-301/302/205 each gained a
  "Status: Partially implemented (Tier 0, S-008)" note (same pattern as
  REQ-102/103/701): REQ-301's one-round-ahead idempotency rule and cron
  trigger are built, but "configured...without a code change" isn't —
  `RoundSchedulingOptions` is a plain C# object with hardcoded defaults in
  `Program.cs`, and the schedule itself lives in `generate-round.yml`'s cron
  expression, so changing frequency today means editing code either way;
  REQ-302's status calculation is fully built and tested, but "only active
  rounds accept guesses" isn't enforced yet (no guess endpoint exists until
  S-009); REQ-205's `RoundCloseService` only pulls a round's `EndTime`
  forward and is only ever invoked via REQ-806's endpoint today — there is
  no automated scheduled job calling it at a round's real `end_time`, and it
  computes no `final_uniqueness_score`/`final_points` at all (S-011). REQ-806
  checked against the diff and found already accurate — no change made.
  Architecture-document.md's §5 ADR-0014 footnote now names COMP-03
  alongside COMP-01/COMP-05 (identical "entity lives in `XGArcade.Data`
  despite the table's 'maps to' column" pattern); while there, also added a
  missing §10 ADR-table row for ADR-0014 itself (accepted in S-007's
  doc-sync but never given a row in that table — a pre-existing gap, not
  caused by this diff, fixed here since it's directly adjacent to the
  footnote edit). §6.1's grid-generation flow status note rewritten: the
  full flow (Round Scheduler Job → Games.XGGrid → ... → Core.Rounds: create
  Round) is now real end to end, but two things the S-007-era note predicted
  did not happen as expected — `POST /internal/grid/generate` (S-007) was
  deliberately kept rather than retired (still useful for isolated manual
  testing, has its own test coverage), and the new `/internal/generate-round`
  endpoint's own template resolution still bypasses `IGameModule` (a shared
  `GridTemplateResolver` helper calls `IGridInstanceRepository` directly,
  same shortcut S-007 already took — not a boundary violation, `GridTemplate`
  isn't player data, but the "temporary until S-008" gap actually carried
  forward into the production-intended endpoint instead of closing).
  Implementation-document.md §5's Core-entities header comment (preceding
  `User`/`Round`/`Guess`/`League`) gained the same ADR-0014 pointer the xG
  Grid entities section already had (S-007) — it previously implied `Round`
  (and `User`, before it) were physically defined inside `XGArcade.Core`,
  which is only true of the business logic, not the EF Core class; the
  `Round` illustrative shape itself already matched the built entity exactly
  (`Id`/`GameKey`/`GameInstanceId`/`StartTime`/`EndTime`/`AllowGuessChange`),
  no field-level change needed, unlike `GridCell`'s S-007 gap. §6's
  grid-generation pseudocode's Tier 0 status note updated to note the abort
  path (log + 500) is now reachable from both grid-generation endpoints, not
  just `/internal/grid/generate`. §6's uniqueness-score pseudocode gained a
  new Tier 0 status note: only the closure half exists, and only as a stub
  (`RoundCloseService`), invoked only via REQ-806 today; the actual
  scoring/locking body has no implementation at all yet (`Guess` doesn't
  exist until S-009, the logic itself is S-011). docs/backlog.md's S-008
  entry checked against the actual diff and found already accurate — no
  change made. No new ADR: architecture-reviewer/code-reviewer passes on
  this story's diff found no boundary violations and no decision requiring
  one (the `XGArcade.Data → XGArcade.Core` reference swap to
  `XGArcade.Core → XGArcade.Data` follows ADR-0014's already-established
  direction, not a new one). REQ-301/302/205/806, ADR-0003, ADR-0014.
- 2026-07-09 — docs/decisions/0014-shared-data-project-for-all-entities.md
  (new), docs/architecture-document.md (§5 table footnote, §6.1),
  docs/implementation-document.md (§5 header comment + `GridCell`, §6 grid-
  generation pseudocode), docs/requirements-document.md (REQ-102, REQ-103) —
  doc sync for S-007 (Grid generation): `IGameModule`/`RoundConfig`/
  `GameInstance`/`ScoreResult` added to `XGArcade.Core.Games`;
  `GridTemplate`/`GridInstance`/`GridCell` entities + `IGridInstanceRepository`
  added to `XGArcade.Data`; `GridGameModule` (`XGArcade.Games.XGGrid`,
  COMP-05) implements `GenerateInstanceAsync` for Tier 0's Country×Club-only
  scope (`ScoreSubmissionAsync` still throws `NotImplementedException`,
  that's S-009); a non-Production-only `POST /internal/grid/generate`
  endpoint exercises it end to end ahead of S-008's real `Core.Rounds`
  caller. Added ADR-0014 (an architecture-reviewer pass on this story
  flagged that S-004's `User`/COMP-01 and now S-007's `GridTemplate`/
  `GridInstance`/`GridCell`/COMP-05 both live in `XGArcade.Data` despite
  architecture-document.md §5's "maps to" column naming a different
  project, without ever documenting why) — the §5 table gained a footnote
  pointing at it, and implementation-document.md §5's xG-Grid-entities
  header comment now points at the ADR instead of implying the entities are
  physically defined inside `XGArcade.Games.XGGrid`. §6.1's grid-generation
  flow gained a Tier 0 status note (same pattern as §6.4's auth-flow note):
  the diagram's "Round Scheduler Job → Games.XGGrid → ... → Core.Rounds:
  create Round" still describes the full/long-term flow, but S-008
  (`Core.Rounds`) doesn't exist yet, so today's real entry point is the
  temporary internal endpoint calling `IGameModule` directly, and the
  endpoint returns the persisted `GridInstance` itself rather than a
  `Round`. Implementation-document.md §5's `GridCell` pseudocode gained the
  `GridInstanceId` FK and `RowCategoryType`/`ColCategoryType` fields that
  were missing from its original illustrative shape (present in the actual
  entity since S-007, needed so future guess-checking, S-009, knows which
  `PlayerAttribute.AttributeType` to query per cell without re-deriving it);
  §6's grid-generation pseudocode gained a Tier 0 status note explaining
  `GridGameModule`'s actual algorithm (N row headers fixed once, then
  column headers picked one at a time and validated against every fixed
  row in one pass) is structurally different from, but acceptance-
  criteria-equivalent to, the pseudocode's simpler independent-per-cell-
  retry model, and that "alert admin" on abort isn't implemented (only
  `ILogger.LogError` + a 500 response). REQ-103 gained a "Status: Partially
  implemented (Tier 0, S-006/S-007)" update: grid generation is now the
  real caller of `WikidataLookupService`, invoked when a local cache miss
  occurs during `GenerateInstanceAsync`, but the API-Football fallback branch
  still doesn't exist, so a Wikidata miss is treated as an ordinary 0-match
  result, not "neither source found a match." REQ-102 gained a "Status:
  Partially implemented (Tier 0, S-007)" note: the size/uniqueness
  acceptance criteria are satisfied by the internal endpoint, but there is
  no admin CRUD for `GridTemplate` yet — it find-or-creates one by size on
  demand. No requirements-document.md acceptance-criteria text was changed,
  only status notes added, matching the existing REQ-103/REQ-701 pattern.
  docs/backlog.md's S-007 entry checked against the actual diff and found
  already accurate — no change made. REQ-101/102/103/107/109, ADR-0003,
  ADR-0006, ADR-0011, ADR-0014.
- 2026-07-09 — docs/decisions/0011-wikidata-first-lookup-waterfall.md
  (addendum), docs/implementation-document.md (§6, §6a), docs/backlog.md
  (S-006) — raised `WikidataClient`'s query timeout from 8s to 15s, per
  direct PR review feedback on S-006 (#20): ADR-0011's original "e.g.
  5-10s" was only an illustrative example, and the ADR's own evidence
  (WDQS queries observed taking 9-27s under load) argues for a longer
  default — 8-10s would misclassify a meaningful share of genuinely-
  successful-but-slow queries as timeouts, needlessly pushing otherwise-
  answerable lookups onto the Tier 1 API-Football fallback or discarding a
  valid grid combination (REQ-101). Added as an ADR-0011 addendum rather
  than editing the original decision text, matching this project's
  established pattern for refining an already-accepted ADR. No requirements-
  document.md/architecture-document.md change — the timeout value isn't
  part of either document.
- 2026-07-09 — docs/requirements-document.md (REQ-103), docs/architecture-document.md
  (§2 banner, §5 COMP-06/COMP-10 table, boundary rule 5) — doc sync for
  S-006 (Wikidata client, COMP-07 Tier 0 half): `WikidataClient`/
  `WikidataLookupService` (`XGArcade.DataSync.Wikidata`) run the SPARQL
  country×club intersection query (implementation-document.md §6a),
  persist matches as unverified `PlayerData`/`PlayerAttribute`, and upsert
  `skos:altLabel` results into a new `PlayerAlias` entity via two new
  `IPlayerStoreRepository` methods; not yet called by anything (S-007 is
  the first caller). REQ-103 gained a "Status: Partially implemented (Tier
  0, S-006)" note (only the Wikidata half is built, no API-Football
  fallback yet, not yet wired to grid generation) and its `source` clause
  was corrected — the actual stored value is the specific provider
  (`"wikidata"`) per implementation-document.md §5's pre-existing `Source`
  enum, not a generic `"live_lookup"` literal as the old wording implied.
  Architecture-document.md's COMP-06 row now lists `PlayerAlias` alongside
  PlayerData/PlayerOverride/PlayerAttribute (it's populated incrementally
  like the rest of COMP-06, not bulk-imported like COMP-10's index), and
  boundary rule 5 is clarified: it governs autocomplete (COMP-10-only,
  no exceptions) and correctness-checking (COMP-06-only), not "COMP-06 and
  COMP-10 can never be read together" — REQ-208's post-submission
  candidate-resolution step (already documented in implementation-document.md
  §6's `normalize()` pseudocode, predating this story) deliberately reads
  both `PlayerNameIndex` (COMP-10) and `PlayerAlias` (COMP-06) to build the
  candidate set, which is the intended design, not a violation. Also fixed
  two stale "Wikidata client is Tier 1" banner lines (architecture-document.md
  §2, implementation-document.md top-of-doc note) — Wikidata has been Tier 0
  since the ADR-0011 correction; only the API-Football fallback and
  `CountryDefinition`/`ClubDefinition`'s *dynamic* external-ID resolution
  remain Tier 1. Updated `IPlayerStoreRepository`'s header doc-comment to
  list `PlayerAlias` alongside the entities it already gated. No new ADR:
  `PlayerAlias`'s shape and COMP-06-style incremental-growth pattern were
  already specified in implementation-document.md §5/§6a and
  architecture-document.md §6.7's sync allowlist before this story — this
  was a documentation gap (COMP-06's own §5 row and boundary rule 5 hadn't
  caught up), not a new structural decision. Flagged back, not fixed here:
  `infra/scripts/lib/game-data-tables.sh` lists the sync allowlist entry as
  `public."PlayerAlias"` (singular), but the actual EF-generated table name
  is `"PlayerAliases"` (plural, following the `DbSet<PlayerAlias> PlayerAliases`
  property name, same convention as `Players`/`PlayerAttributes`/
  `PlayerOverrides`) — worth a follow-up fix, out of scope for a docs-only
  change. REQ-103/REQ-109.
- 2026-07-09 — .github/workflows/deploy.yml, infra/README.md, SETUP.md,
  NOTES.md — fixed a real bug in `deploy-infra`: unquoted
  `${{ secrets.X }}` interpolation in the `az deployment group create`
  `--parameters` line let an unquoted `;` in the (correctly-formatted)
  Postgres connection string act as a bash command separator, silently
  truncating the command and dropping `supabaseJwtSecret`/`supabaseUrl`/
  `supabaseAnonKey` from the deployment (`ERROR: Missing input parameters`).
  Quoted every interpolated value in `deploy.yml` and the matching manual-
  deploy examples in `infra/README.md`/`SETUP.md`. No requirements/
  architecture/implementation-document changes — infra/CI behavior only.
- 2026-07-09 — SETUP.md, infra/README.md, NOTES.md — investigated
  `deploy.yml`'s three latest failed runs; both root causes are dev secret
  configuration (empty `DEV_SUPABASE_ANON_KEY`, `DEV_DATABASE_CONNECTION_STRING`
  saved in Supabase's URI form instead of the .NET/ADO.NET format Npgsql
  needs), not application or Bicep bugs. Clarified the connection-string
  format requirement and the anon key's required-at-startup status in both
  docs; no code change made since neither failure is fixable without the
  actual secret values. No requirements/architecture/implementation-document
  changes — no behavior changed.
- 2026-07-09 — no changes to docs/requirements-document.md,
  docs/architecture-document.md, or docs/implementation-document.md —
  doc-sync review for S-005 (seed reference data, REQ-109):
  `ReferenceDataSeeder.SeedAsync` now inserts the hand-curated 15
  clubs/20 countries (Name + WikidataQid) from `MVP-SCOPE.md`'s
  already-verified tables into `CountryDefinition`/`ClubDefinition`,
  idempotent by `Name`; the `migrate-and-seed` CLI verb (`Program.cs`)
  now calls it after `Database.MigrateAsync()` instead of being a
  documented no-op; and `deploy.yml` gained a `migrate-and-seed-database`
  job that runs both against dev's actual Supabase Postgres instance —
  previously nothing in the deploy pipeline ever applied migrations or
  seed data there, only `ci.yml`'s ephemeral local Postgres container
  (used for E2E) ever got seeded. Checked REQ-109's acceptance criteria
  (values come from the reference tables; a null QID isn't an error)
  against the diff: still accurate as the full/long-term requirement, no
  edit needed — same conclusion as the S-003 entry below. Checked
  `implementation-document.md`'s top Tier-1 banner
  (`CountryDefinition`/`ClubDefinition`'s external-ID *resolution*
  remains Tier 1) against what actually got built: still accurate — that
  banner refers to the dynamic resolution mechanism (an admin-driven
  incremental flow for new clubs, and `ApiFootballTeamId` resolution),
  which is still unbuilt; Tier 0's fixed list having its QIDs hand-looked-up
  and hardcoded rather than dynamically resolved was already explicit in
  `MVP-SCOPE.md`'s Tier 0 section, so no duplicate note was added. Checked
  `architecture-document.md`'s COMP-06 boundary rule 1 and
  `ICategoryValueRepository`'s doc comment against the new seeder: it
  writes `CountryDefinition`/`ClubDefinition` rows directly via
  `DbContext` rather than through the repository's own
  `AddCountryAsync`/`AddClubAsync` methods — an internal inconsistency
  worth a follow-up code-review look (flagged back, not fixed here), but
  not a cross-component boundary violation, since boundary rule 1 governs
  game modules reading COMP-06's data, not COMP-06's own internal seeding
  path — no architecture-document.md edit. No new ADR: `deploy.yml`'s new
  `migrate-and-seed-database` job reuses the exact `migrate-and-seed` CLI
  verb `ci.yml` already established (S-002) against the same dev database
  `deploy.yml` already targets since the prod→dev rename — this closes an
  operational gap (dev's database was never automatically migrated/seeded
  before), not a new structural decision with a real alternative. The
  `infra/README.md` secrets-table update (noting the new job's use of
  `DEV_DATABASE_CONNECTION_STRING`) was made by hand alongside the code
  and verified correct/sufficient here, not redone.

- 2026-07-09 — docs/requirements-document.md (REQ-701), docs/architecture-document.md
  (§6.4, §7 cross-cutting concerns), docs/implementation-document.md (§3
  security middleware pipeline, §6a external API shapes) — doc sync for
  S-004 (backend-mediated signup/login + JWT middleware, ADR-0013).
  REQ-701 gained a "Status: Partially implemented (Tier 0, S-004)" note —
  only the 16+ checkbox clause is built and server-enforced; password
  policy and enumeration-safe errors remain unimplemented (Supabase's own
  errors pass through as-is), consistent with `MVP-SCOPE.md`/`docs/backlog.md`
  S-004 scoping. Fixed §6.4's signup/confirmation flow, which still read as
  if REQ-701–705 were fully built: added a Tier 0 status note (checkbox-only
  signup/login via `AuthController`, confirm-email off, `User.EmailConfirmed`
  hardcoded `true` at creation, REQ-702–705 not yet built) ahead of the
  full/long-term flow diagram, which is unchanged. Added an ADR-0013
  reference to §7's Authentication row alongside the existing ADR-0004
  reference. Corrected §6a's Supabase paragraph, which claimed the backend
  "is not accessed as a REST API from the backend at all" — true for data
  access (EF Core/Npgsql), no longer true for Supabase Auth specifically,
  which `SupabaseAuthClient` now calls directly per ADR-0013; split into two
  paragraphs (data vs. auth) rather than editing the data claim itself.
  Updated §3's security middleware pipeline with a "Tier 0 status" note:
  only HTTPS redirection/CORS/JWT validation are actually wired in
  `Program.cs` (rate limiting and admin authorization remain unbuilt, per
  `docs/backlog.md`'s S-012 for the latter), plus the concrete JWT details
  (`MapInboundClaims = false`, issuer/audience/secret sourcing, and the
  `Auth:Mode=local-e2e` test-only branch gated by `IsDevelopment()`).
  Confirmed §5's `User` entity already matched the built shape exactly — no
  change needed there. No new ADR beyond the already-committed ADR-0013 (not
  this pass's job) and no requirements-document.md acceptance-criteria text
  changed — REQ-701–705's full definitions are unchanged, only how much of
  REQ-701 is currently built.

- 2026-07-09 — docs/implementation-document.md (§5 data model) — doc sync
  for S-003 (database + EF Core baseline, REQ-109): reviewed the actual
  `XGArcade.Data` entities/DbContext/migration against §5 and the
  "Required indexes" table — all indexes match exactly (`Player.WikidataQid`
  unique-filtered, `PlayerAttribute(AttributeType, AttributeValue)`,
  `CountryDefinition`/`ClubDefinition`/`TrophyDefinition(Name)` unique).
  Added a short note that `PlayerData`/`PlayerOverride`/`PlayerAttribute`
  carry a cascade-delete FK to `Player.Id` (new in this story, not
  previously documented) and why that's unlike ADR-0003's deliberate
  Round→GridInstance FK omission — those three live inside the same
  component (COMP-06) as `Player`, so there's no boundary reason to leave
  them unconstrained. No architecture-document.md change: COMP-06's
  boundary rule 1 and the CategoryValueRepository/PlayerStoreRepository
  split already match what's built (repositories are the concrete
  realization of an already-documented boundary, not a new one) — checked
  against `ICategoryValueRepository`/`IPlayerStoreRepository`'s own doc
  comments and the REQ109-named tests in `XGArcade.Data.Tests`. No
  requirements-document.md change: REQ-109's acceptance criteria (values
  come only from the reference tables; a null QID isn't an error) are
  still accurate as the full/long-term requirement — the doc's existing
  "this document describes the full system, not what's being built now"
  note (implementation-document.md, top) plus MVP-SCOPE.md's already-explicit
  "no `ApiFootballTeamId` needed for Tier 0 at all" already cover
  `ClubDefinition`'s Tier-0-vs-Tier-1 scoping, so no duplicate note was
  needed there. No new ADR — FK constraints and the repository-per-component
  split are normal implementation detail, not a decision that could
  reasonably have gone another way in a way worth recording (already
  confirmed by architecture-reviewer/code-reviewer on the story's PR).

- 2026-07-09 — docs/requirements-document.md (REQ-606), docs/architecture-document.md
  (§7 cross-cutting concerns), MVP-SCOPE.md, docs/backlog.md, infra/README.md,
  NOTES.md — doc sync for S-002 (trivial end-to-end slice: `GET /health` +
  frontend page, `migrate-and-seed` CLI stub, `ci.yml` e2e-tests restored to
  its full Postgres-service/migrate-and-seed/wait-on-health form, CORS wired
  end-to-end via `Cors:AllowedOrigins`/`Cors__AllowedOrigins` fed from a new
  `corsAllowedOrigin` Bicep parameter and `DEV_FRONTEND_HOSTNAME`, plus a
  post-review fix so `deploy.yml`'s frontend build also gets
  `VITE_API_BASE_URL` from `DEV_BACKEND_HOSTNAME`). REQ-606 gained an
  explicit CORS-restriction bullet — `implementation-document.md` §3's
  security middleware pipeline already described CORS as realizing REQ-606,
  and a code comment in `Program.cs` cited REQ-606 for its CORS policy, but
  REQ-606's own acceptance criteria never said so; closed that gap rather
  than inventing a new requirement. Added a matching CORS row to
  `architecture-document.md` §7's cross-cutting concerns table for the same
  reason — CORS is now actually implemented, not just described in the
  pipeline diagram, and §7 had no row for it at all despite rows for every
  other item in that same pipeline (transport security, rate limiting,
  authorization, dependency scanning). No `implementation-document.md`
  change: checked its tech-stack table, §3 pipeline diagram, §4 project
  structure, §5 data model, and §7/§8 testing/CI descriptions individually
  against the diff — all already accurate at the level of detail they
  operate at (none name specific endpoints, and `/health`/`migrate-and-seed`
  are infra plumbing, not product behavior, so no REQ was invented for
  them either). MVP-SCOPE.md/docs/backlog.md/infra/README.md/NOTES.md
  updates from the same iteration (DEV_FRONTEND_HOSTNAME precondition,
  S-002 acceptance criteria, secrets table rows, migrate-and-seed-is-a-stub
  and dotnet-SDK-unavailable-in-sandbox notes) were made by hand alongside
  the code and verified correct/sufficient here, not redone. Also fixed
  `requirements-document.md`'s in-body "Version 0.22 · 2026-07-07" header
  line, left stale by the earlier hand-edit that only bumped the
  frontmatter to 0.23/2026-07-09. REQ-606, no new ADR (CORS was already an
  implemented-per-plan pipeline stage, not a new structural decision).

- 2026-07-09 — docs/backlog.md (S-002 acceptance criteria) — `main`'s
  branch protection requires every `ci.yml` status check to pass with no
  bypass, but `e2e-tests` cannot pass in S-001's PR (needs `/health` and
  `migrate-and-seed`, both S-002 scope). Rather than weaken branch
  protection, `ci.yml`'s `e2e-tests` job had its Postgres
  service/migrate-and-seed/Start-API steps commented out (not deleted) so
  it only runs the backend-free placeholder Playwright test for now.
  Added an explicit restore step to S-002's acceptance criteria
  (uncomment those steps, add a real `/health`-wait loop) so it isn't
  forgotten — full rationale, including two rejected approaches
  (`timeout-minutes` alone, `continue-on-error`), in `NOTES.md`.

- 2026-07-09 — docs/implementation-document.md (§4 project structure) —
  S-001 (repo + pipeline skeleton) landed the first real code in the repo
  (`backend/XGArcade.sln` with the Tier 0 project subset, `backend/Dockerfile`,
  `frontend/` Vite+React+TS scaffold — commit 9aedd28, no REQ/ADR
  attached, pure scaffolding). Cross-checked the actual folder layout
  against §4: the Tier 0 subset (Api/Core/Games.XGGrid/Data/DataSync +
  matching `.Tests` projects) matches, and the project-reference graph
  respects ADR-0003 (`Core` never references `Games.XGGrid`) exactly as
  `architecture-document.md`'s COMP-05/06/07 table already implied — no
  architecture-document.md or requirements-document.md change needed.
  Found and fixed a pre-existing gap while checking §4 literally against
  disk: its `/tests` listing named only `Core.Tests`/`Games.XGGrid.Tests`/
  `Api.Tests`, omitting `Data.Tests` and `DataSync.Tests`, which now exist.
  `XGArcade.Email`/`XGArcade.Testing` remain correctly absent from disk —
  both are Tier 1/deferred per `MVP-SCOPE.md` and CLAUDE.md's Getting
  Started scoping, not a doc/code mismatch. The `Microsoft.AspNetCore.OpenApi`
  package removal (NOTES.md, 2026-07-09) is an implementation detail with
  no tech-stack-table or boundary impact, so intentionally not duplicated
  here.

- 2026-07-08 — MVP-SCOPE.md, docs/implementation-document.md,
  docs/backlog.md (S-006) — Swapped England (Q21) for United Kingdom
  (Q145) in Tier 0's country list, per direct feedback: since UK is a
  normal sovereign state, this makes every country query in Tier 0
  uniformly `P27`-based with zero special cases, removing the P1532
  exception entirely from Tier 0's scope rather than just documenting
  around it. The P1532 knowledge wasn't discarded — it's relocated to a
  new, explicit Tier 1 backlog item ("national teams as distinct
  footballing entities": England/Scotland/Wales/Northern Ireland via
  `P1532`, genuinely a different concept from citizenship, not a
  simplification to collapse away later). Also corrected a mistake in
  this same conversation's prior explanation (not in any file, caught
  before it was written down): an illustrative example described a
  "France×England" grid, which REQ-107 explicitly forbids (no
  Country×Country pairings) — the example was simply wrong, not a design issue.

- 2026-07-08 — MVP-SCOPE.md (QID tables filled in), docs/implementation-document.md
  (§6a England/P1532 exception), docs/backlog.md (S-005/S-006 updated) —
  Looked up and verified all 35 Wikidata QIDs (15 clubs, 20 countries)
  directly against live Wikidata pages, closing the last open Tier 0
  precondition — this is now pure data entry, no research left. Verification
  surfaced a real, non-obvious correctness issue: England (and by extension
  Scotland/Wales/Northern Ireland, if ever added) can't use the standard
  citizenship property (P27) the way every other country does, since none
  of the UK's home nations are sovereign states — English players' P27
  citizenship is uniformly "United Kingdom," never "England" specifically.
  A naive implementation querying P27 for every country would silently
  return zero results for every England cell. Documented the fix (use
  `P1532`, "country for sport" — Wikidata's own property for exactly this
  distinction) in the implementation doc's semantics note and as an
  explicit backlog test case in S-006, rather than leaving it to be
  discovered as a confusing bug during actual development.

- 2026-07-08 — MVP-SCOPE.md (concrete club/country list added) — The Tier 0
  precondition checklist said "~15 clubs' and ~15-20 countries'" without
  ever naming which ones, leaving the actual lookup task undoable.
  Recorded the specific decided list (15 clubs led by Real Madrid/Barcelona/
  Manchester United/etc., 20 countries led by Brazil/Argentina/France/etc.)
  so it's not lost to chat history. QIDs themselves still pending manual
  lookup — that remains the one open precondition.

- 2026-07-07 — docs/requirements-document.md (REQ-109 extended),
  docs/implementation-document.md (§6a senior-club semantics note),
  docs/backlog.md (S-006 acceptance), docs/review-2026-07-07-design.md
  (corrected a stale judgment) — Recorded the "senior career only"
  decision for the Club category (youth academy appearances don't count).
  Corrected the earlier design review, which had judged Wikidata's P54
  including youth teams as "harmless" before this decision existed.
  Documented honestly, not as a solved problem: querying the senior
  club's specific QID excludes youth appearances when that club's youth
  setup has its own distinct Wikidata item, but a thin/poorly-maintained
  page could record a youth-only spell directly against the senior QID
  with no distinction — no secondary filter is planned to catch this in
  Tier 0 (an inconsistently-populated "appearances" qualifier isn't
  reliable enough to build logic around); mitigated by the existing
  manual override (S-012), not a new mechanism. Also made explicit (it
  was previously only implied by a flow diagram) that every live lookup a
  round's cells need happens during generation, strictly before that
  Round is created and visible to players — this is what makes the
  local-DB-only guess-checking strategy defensible.

- 2026-07-07 — docs/requirements-document.md (REQ-806, new),
  docs/backlog.md (S-008/S-011 wired to REQ-806) — Added the minimal
  round-closure test control Tier 0's E2E testing was silently missing:
  S-011's acceptance criteria said "round closes" with no defined
  mechanism to make that happen without waiting for real time. REQ-806
  adds a narrow, environment-gated `POST /internal/test-data/force-close-round/{id}`
  endpoint (absent outside `Production`, same discipline as REQ-801) —
  deliberately much smaller than REQ-801-804's full dev-environment
  vision, scoped to the local/ephemeral stack `ci.yml` already runs E2E
  against. Test users/guesses still go through the real signup/guess
  endpoints — no separate seeding API needed.

- 2026-07-07 — MVP-SCOPE.md, TODO.md, SETUP.md, infra/README.md,
  docs/backlog.md, .github/workflows/deploy.yml (rewritten),
  .github/workflows/generate-round.yml, .github/workflows/backup-database.yml,
  docs/decisions/0006-environment-and-test-data-strategy.md (second
  addendum) — **Renamed Tier 0's single environment from "prod" to
  "dev."** Reasoning: Tier 0 has no backups, no email confirmation, no
  legal docs — that's what a dev environment is, not a production one;
  calling it "prod" was reusing leftover naming from the original
  two-environment design, not a deliberate choice. Practical benefit: the
  "dev" naming already existed from the environment-split work
  (`xg-arcade-dev-rg`, `DEV_*` secrets, `main.parameters.dev.json`) — Tier
  0 now just uses it directly, no new naming needed. **Tier 1 no longer
  "adds a dev environment" — it creates the first real "prod"**, at
  exactly the point the backup/alerting/legal-docs bright lines get
  crossed, which is a cleaner story than upgrading an existing "prod"
  in place. `deploy.yml` rewritten to target dev; `generate-round.yml`
  repointed from `PROD_BACKEND_HOSTNAME` to `DEV_BACKEND_HOSTNAME`;
  `backup-database.yml` left targeting `PROD_*` with a comment clarifying
  it's a Tier 1 workflow for the prod environment that will exist by
  then. Every setup doc (`SETUP.md` especially — its dev/prod secrets
  tables and manual-deploy commands were fully swapped) and the backlog
  updated to match.

- 2026-07-07 — .claude/commands/test.md (rewritten Tier 0-correct, also
  fixing a "devuction" text corruption left by an earlier automated
  rename), .claude/README.md (testing section), .github/workflows/sync-players.yml
  and generate-round.yml (schedules disabled with re-enable points: T-101
  and S-008 respectively — both would otherwise have failed on a timer
  from day one), docs/design-document.md (MVP banner added, matching the
  other core docs), docs/backlog.md (S-008 now includes re-enabling the
  cron) — Supporting-files review pass covering READMEs, agents, commands,
  workflows, and the design doc. Agents and remaining files verified clean
  of stale references; the seven agent definitions needed no changes.

- 2026-07-07 — docs/review-2026-07-07-design.md (new), .github/workflows/ci.yml
  (rewritten Tier 0-shaped), docs/requirements-document.md (REQ-204 formula
  fixed, REQ-301 pre-generation), docs/implementation-document.md
  (Player.WikidataQid, §6a query rules, admin authorization), docs/backlog.md
  (S-002/006/008/012/013), SETUP.md, infra/README.md, CLAUDE.md — Full
  design/plan review (distinct from the earlier file-quality review) found
  and fixed eight real issues, the biggest being that `ci.yml` was
  structurally unrunnable inside Tier 0's own rules (E2E depended on a dev
  environment and a test-data API that are both Tier 1) — rewritten so E2E
  runs against a local stack in CI. Also fixed: REQ-204's uniqueness
  denominator counted incorrect guesses (would have distorted all scoring);
  `Player` lacked a dedup identity across intersection queries (now
  `WikidataQid`, upsert-only); the intersection query's completeness is now
  an explicit no-LIMIT rule (it's what makes cache-only guess checking fair
  without REQ-211); `skos:altLabel` aliases fetched free in the same query;
  rounds now generate one ahead so a silent generation failure has a full
  round of headroom (no alerting exists in Tier 0); admin authorization
  defined (`Admin__UserIds` env var); Country formally defined as
  citizenship (P27). Review doc records what was judged and deliberately
  left alone, so it isn't relitigated.

- 2026-07-07 — docs/backlog.md (new), TODO.md, README.md, CLAUDE.md,
  MVP-SCOPE.md — Full-set sync review after the Wikidata pivot found and
  fixed three stale spots still implying API-Football was an MVP
  prerequisite (TODO.md's account checklist, README.md's and CLAUDE.md's
  SETUP.md table rows); the core docs' full-system content was verified as
  correctly covered by their MVP-scope banners, with no contradictions
  found. Added `docs/backlog.md`: 13 ordered, session-sized Tier 0 stories
  (S-001 repo/pipeline skeleton → S-013 first-release QA pass) across four
  epics, each with acceptance criteria tied to REQ IDs for test naming,
  explicit dependencies, and the rule that every story leaves the system
  deployable and testable; Tier 1 items listed unordered at the end, each
  gated on its `MVP-SCOPE.md` trigger. Wired the backlog into the doc maps
  and getting-started flows so an agent session starts by picking the next
  unfinished story rather than re-deriving an order.

- 2026-07-07 — MVP-SCOPE.md (Tier 0 data source reversed), TODO.md,
  SETUP.md, CLAUDE.md — **Reversed the Tier 0 data-source decision** based
  on explicit direction to prioritize full historical correctness over
  club-count breadth. Tier 0 now uses **Wikidata only** (not API-Football)
  from the start, with a smaller, hand-curated list (~15 clubs, ~15-20
  countries) — each entered with its Wikidata QID looked up by hand, no
  automated resolution needed. This works cleanly because Wikidata's `P54`
  ("member of sports team") property is multi-valued — a simple query
  checking `P54 = Arsenal` already covers a player's entire career, not
  just a current team, so "ever played for" needs no special handling.
  API-Football moves to Tier 1, as a fallback source for when the club
  list grows beyond what's worth manually looking up, or for clubs/players
  with poor Wikidata coverage. This also means Tier 0 needs no
  `ApiFootballTeamId` resolution and no `ExternalApiUsage` budget tracking
  at all (Wikidata has no small daily cap to manage) — both genuinely
  become Tier 1 concerns now. Corrected the same backwards reference in
  three places (`TODO.md`, `CLAUDE.md`'s Getting Started section) that had
  said to skip Wikidata and build API-Football first.

- 2026-07-07 — MVP-SCOPE.md (Tier 0 fetch mechanics corrected, Wikidata
  trigger revised) — Corrected a real gap: Tier 0's player-fetching
  mechanics implicitly assumed "current squad" when the actual requirement
  is "ever played for this club," which current-season fetching can't
  satisfy. Clarified that the player database itself was never the real
  constraint (even a club's full ~140-year history is a genuinely small,
  ordinary-sized table — tens of thousands of rows, not "massive"); the
  real constraint is API-Football's per-season endpoint making full
  historical backfill expensive in API calls specifically. Tier 0 now
  explicitly scopes to the last ~10-15 seasons per club (a documented,
  honest limitation, not a hidden bug) at a one-time cost of ~300-450
  calls total across 30 clubs. Reprioritized Wikidata from a distant,
  capacity-driven Tier 1 item to a likely *early* one, since a single
  SPARQL query answers "entire career history" in one call regardless of
  how far back it goes — the natural fix for the recent-era limitation,
  not just a rate-limit safety valve.

- 2026-07-07 — MVP-SCOPE.md (corrected + extended), docs/implementation-document.md
  (cross-reference added) — Fixed two real clarity gaps found by re-reading
  `MVP-SCOPE.md` critically: (1) it had wrongly claimed Tier 0 needs no
  `ApiFootballTeamId` at all — corrected, since API-Football's team-centric
  endpoints genuinely require one; what Tier 0 actually skips is the
  Wikidata QID and manual admin resolution, not ID resolution entirely.
  Added the concrete mechanics: fetch a club's whole squad once, cache
  every player's real nationality (not just the one being searched for),
  so one API call per club answers many country combinations at once —
  at most ~30-60 calls total for the whole Tier 0 club list, ever.
  (2) Added a self-contained "Preconditions to actually start" checklist
  at the top of `MVP-SCOPE.md` so it doesn't require cross-referencing
  `SETUP.md`/`infra/README.md` to know what's actually needed, and
  replaced vague Tier 1 triggers ("add if it becomes a problem") with
  concrete, observable ones (specific request-count thresholds, "someone
  actually asks," bright-line rules for backups/legal docs before real users).

- 2026-07-07 — MVP-SCOPE.md (new), CLAUDE.md (Getting started rewritten,
  doc map + conventions updated), TODO.md (restructured around MVP-first),
  SETUP.md (Tier 1 steps marked, skippable for MVP), README.md,
  docs/requirements-document.md, docs/architecture-document.md,
  docs/implementation-document.md (AI-agent banners updated) — Introduced
  explicit build-order tiering after recognizing the design work had grown
  well ahead of what a first playable version actually needs. Nothing was
  deleted — `MVP-SCOPE.md` tiers the existing REQ/ADR/component set into
  Tier 0 (build now: single environment, Country×Club only, API-Football
  only, no autocomplete, no email confirmation, global leaderboard only),
  Tier 1 (add only once real testing shows a specific need: Wikidata,
  guess-time live verification, autocomplete, disambiguation UI, Trophy
  category, dev/prod split, backups, email confirmation, custom leagues),
  and Tier 2 (already-deferred Phase 2 items, unchanged). `CLAUDE.md`'s
  "Getting started" section and document map now point to `MVP-SCOPE.md`
  first, with an explicit convention that a REQ/ADR existing and looking
  "ready" is not permission to build it if it's Tier 1/2. `TODO.md` and
  `SETUP.md` were restructured so the actual near-term setup burden is
  visibly much smaller (one Supabase project, no Resend, no dev
  environment) rather than looking like all prior setup work is required
  up front.

- 2026-07-07 — docs/decisions/0012-category-value-reference-tables.md
  (new), docs/requirements-document.md (REQ-109, new), docs/architecture-document.md,
  docs/implementation-document.md (`CountryDefinition`, `ClubDefinition`
  entities, `TrophyDefinition.WikidataQid` added, grid generation
  pseudocode filled in, `live_lookup()` updated) — Closed a real gap:
  grid generation's pseudocode always said "pick random categories"
  without ever specifying where the pool of actual country/club values
  came from, and Wikidata queries need resolved entity IDs (QIDs) that
  plain strings like "France" or "Arsenal" don't provide on their own.
  Fixed via ADR-0012: `CountryDefinition`/`ClubDefinition`/`TrophyDefinition`
  are now the explicit source of truth grid generation picks from, each
  caching its external IDs (Wikidata QID, and for clubs an API-Football
  team ID) once resolved rather than re-resolving per query. Countries are
  bulk-seeded once (a small, stable ~200-row exception to ADR-0001, same
  class as `PlayerNameIndex`'s exception); clubs are resolved incrementally
  when an admin adds one; trophies are resolved manually given the tiny
  table size. A still-unresolved QID is an explicit valid state, not an
  error — the live-lookup waterfall (ADR-0011) just skips Wikidata for
  that value and falls back to API-Football, which doesn't need a QID at all.

- 2026-07-07 — docs/implementation-document.md (§6a, new) — Added a
  concrete reference for the actual request/response shapes of each
  external API `DataSync.Clients` (COMP-07) integrates with, since they
  aren't uniform: API-Football and Resend are conventional REST+JSON with
  a single auth header; Wikidata is a genuinely different paradigm (SPARQL
  graph queries, not resource fetching, with its own property/entity ID
  vocabulary and result format). Documented concretely now rather than
  discovered as unplanned complexity mid-implementation. Also noted
  Supabase is accessed as a plain Postgres connection via EF Core/Npgsql
  for normal data access, not through its REST/GraphQL layer.

- 2026-07-07 — docs/requirements-document.md, docs/implementation-document.md
  (`ClubCrest` comment), docs/decisions/0008-data-provider-compliance.md,
  infra/README.md — Confirmed and clarified the Phase 2 crest-sourcing
  plan: yes, API-Football, and it's genuinely low-risk on two counts —
  their own docs confirm logo/crest calls don't count against the 100/day
  quota at all, and the universe of distinct clubs ever needed as a
  category value is small and largely static compared to individual
  player lookups. Also fixed a small recurring error found while updating
  this: three places incorrectly attributed `ClubCrest`'s design to
  ADR-0007 (which is actually about the unrelated player name index) —
  corrected to reference ADR-0008 and implementation-document.md instead,
  where `ClubCrest` is actually defined.

- 2026-07-07 — docs/decisions/0011-wikidata-first-lookup-waterfall.md (new),
  docs/decisions/0010-guess-time-live-verification.md (status updated),
  docs/requirements-document.md (REQ-103, REQ-211 revised),
  docs/architecture-document.md, docs/implementation-document.md
  (`ExternalApiUsage` corrected, shared `live_lookup()` waterfall function
  added), infra/README.md, CLAUDE.md — **Corrected a real error from
  earlier the same day**: ADR-0010's guess-time live-lookup design was
  built around API-Football alone, as if it were the only live-lookup
  source, when ADR-0001 had already established Wikidata as a second
  source for exactly this purpose. Verified Wikidata's actual public
  SPARQL endpoint limits directly — it throttles by query time (60s/minute
  per IP), not a small daily request count, making it far better suited as
  the *primary* live-lookup source than API-Football's 100/day cap. Fixed
  via ADR-0011: every live lookup now tries Wikidata first (timeout-bounded),
  falling back to API-Football only when Wikidata can't resolve it. This
  makes the 100/day cap a rarely-touched fallback safety net rather than
  the practical bottleneck on either grid generation or guess-time
  verification. Followed the same discipline as every other correction in
  this project — didn't silently rewrite the flawed ADR, superseded it
  with a new one that explains what was wrong and why.

- 2026-07-07 — docs/decisions/0010-guess-time-live-verification.md (new),
  docs/requirements-document.md (REQ-211, new), docs/architecture-document.md,
  docs/implementation-document.md (`ExternalApiUsage` entity, algorithm
  extended), infra/README.md, CLAUDE.md — Closed a real correctness gap:
  `PlayerAttribute` (the narrow validation cache) was never guaranteed to
  contain every valid answer for a cell, only the sample grid generation
  happened to need — meaning a genuinely correct guess for a player outside
  that sample would have been wrongly marked incorrect. Fixed by adding a
  guess-time live-lookup path (REQ-211), narrowly scoped: only triggers
  when the guess matches a real `PlayerNameIndex` candidate with no
  existing attribute data at all (never for names matching nothing),
  persists immediately rather than batching (consistent with ADR-0001's
  existing pattern, and avoids repeatedly re-triggering the same gap).
  Because this shares API-Football's 100/day cap with grid generation's
  own live-lookup fallback (REQ-103), added a tracked shared daily budget
  (`ExternalApiUsage`) with guess-time lookups reserved to 80/day, leaving
  20 for scheduled grid generation so a busy guessing day can't starve
  round creation — ADR-0010.

- 2026-07-07 — docs/decisions/0009-bidirectional-game-data-sync.md (new),
  docs/decisions/0006-environment-and-test-data-strategy.md (status
  updated), infra/scripts/lib/game-data-tables.sh (new, shared allowlist),
  infra/scripts/sync-prod-to-dev.sh (rewritten to source shared allowlist),
  infra/scripts/promote-dev-to-prod.sh (new), .github/workflows/sync-prod-to-dev.yml
  (renamed from sync-environments.yml), .github/workflows/promote-dev-to-prod.yml
  (new), docs/requirements-document.md (REQ-804 revised, REQ-805 new),
  docs/architecture-document.md, docs/implementation-document.md,
  infra/README.md, SETUP.md, CLAUDE.md — Sync is now bidirectional
  (ADR-0009, superseding ADR-0006's one-way-only clause) but tightened
  rather than loosened: only football/game reference data (players, clubs,
  trophies, grid templates) is ever eligible to sync, in either direction —
  results (`Guess`, `Round`, `GridInstance`, `GridCell`) and customer data
  (`User`, `NotificationPreference`, `League`, `LeagueMembership`) are a
  categorical exclusion, not an incidental allowlist gap. Both directions
  now share one allowlist file so they can't drift apart.
  `promote-dev-to-prod.sh` is the new, recommended day-to-day direction
  (curate in dev, ship to prod); `sync-prod-to-dev.sh` remains as the
  fallback for when prod's game data changed directly. The prod-writing
  direction requires a longer confirmation phrase as deliberate extra
  friction. Also expanded the allowlist to include `PlayerNameIndex`,
  `PlayerAlias`, `TrophyDefinition`, and `ClubCrest` (all game-reference
  data per ADR-0007/REQ-108, previously missing from the list), and fixed
  a documentation error found while updating this — two docs had
  incorrectly implied `GridInstance` was part of the synced allowlist; it
  never was, and both were corrected.

- 2026-07-07 — infra/bicep/main.parameters.dev.json (renamed from
  .nonprod.json), infra/scripts/sync-prod-to-dev.sh (renamed),
  .github/workflows/ci.yml (new `deploy-dev` job), .github/workflows/deploy.yml,
  .github/workflows/sync-environments.yml, .github/workflows/sync-players.yml,
  .github/workflows/generate-round.yml, infra/README.md, SETUP.md, CLAUDE.md,
  .claude/README.md, .claude/commands/test.md, docs/architecture-document.md,
  docs/implementation-document.md, docs/requirements-document.md,
  docs/decisions/0006-environment-and-test-data-strategy.md (addendum) —
  Two real changes, done together since they touched the same files:
  (1) renamed the "non-prod"/"nonprod" environment to **dev** everywhere —
  file names, Bicep `environmentTag` values, resource names
  (`xg-arcade-api-dev`, etc.), GitHub secrets (`DEV_*`), and doc prose,
  while leaving CHANGELOG/review history untouched since those describe
  what was actually true at the time; (2) built real two-environment CI/CD
  automation — `ci.yml` gained a `deploy-dev` job that builds/pushes a
  dev-tagged image and redeploys dev via Bicep on every PR/push, with E2E
  tests now depending on it completing, closing the gap where dev could
  silently go stale relative to the code being tested. Also fixed the
  resource-group naming asymmetry found in the prior conversation
  (`xg-arcade-rg` → `xg-arcade-prod-rg`, matching `xg-arcade-dev-rg`'s
  pattern) and fully symmetrized secret names (`PROD_*`/`DEV_*` for
  everything environment-specific, shared secrets unprefixed) — this
  also caught and fixed a redundant pair (`DATABASE_CONNECTION_STRING`
  and `PROD_DATABASE_CONNECTION_STRING` existed as separate secrets for
  the same value; now just `PROD_DATABASE_CONNECTION_STRING`) and a
  missing symmetric secret (`BACKEND_HOSTNAME` had no `DEV_` counterpart
  until now). Also fixed two small leftover issues found while editing:
  the sync script's usage comment still referenced its old filename, and
  its temp-file prefix still said `platform-sync` from before the xG
  Arcade rename.

- 2026-07-07 — SETUP.md (§9 expanded) — Added the actual Claude Code +
  VS Code + GitHub local setup walkthrough (extension install, CLI install,
  gh CLI auth, cloning and opening this repo), replacing the placeholder
  "hand off to Claude Code" line. Noted Claude Code on the web as the
  phone-only alternative to this local path.

- 2026-07-07 — docs/decisions/correspondence/api-football-confirmation-email.md
  (new), SETUP.md — Drafted the ADR-0008 confirmation email to API-Football
  and linked it from SETUP.md's step 4. Also confirmed directly against
  Resend's own docs that no domain is required to send real emails (no
  sandbox/recipient restriction, unlike most providers — only the sender
  address is unbranded until a domain is verified) and noted this in
  SETUP.md, since Azure's default subdomains mean nothing in the setup
  path actually requires owning a domain yet.

- 2026-07-07 — SETUP.md (new), infra/README.md (secrets table corrected),
  README.md, TODO.md, CLAUDE.md — Wrote a step-by-step external-accounts
  setup guide (GitHub → Supabase → Resend → API-Football → Azure →
  secrets → first deploy, in dependency order). Writing it surfaced real
  drift in `infra/README.md`'s secrets table: it listed a nonexistent
  `AZURE_CREDENTIALS` secret when `deploy.yml` actually uses OIDC via
  `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID`, and was
  missing `INTERNAL_JOB_TOKEN` and `BACKEND_HOSTNAME` entirely despite both
  being referenced by `sync-players.yml`/`generate-round.yml`. Table
  corrected to match what the workflows actually reference, verified
  directly against every workflow file rather than assumed.

- 2026-07-07 — infra/bicep/modules/*.bicep, requirements-document.md
  (REQ-204/205/206 reordered), docs/legal/privacy-policy-draft.md,
  infra/scripts/sync-prod-to-nonprod.sh, .github/workflows/sync-environments.yml,
  docs/decisions/0004-hosting-and-iac.md, docs/CHANGELOG.md,
  mockups/design-mockups.html, README.md — Acted on `docs/review-2026-07-07.md`'s
  concrete findings: (1) bumped all three Bicep modules' API versions,
  verified against Microsoft's current documentation (containerApps/
  managedEnvironments 2024-03-01→2026-01-01, Log Analytics workspaces
  2023-09-01→2025-07-01, Static Web Apps 2023-12-01→2025-03-01) — these had
  never been deployed, so the staleness was never caught; (2) reordered
  REQ-204/205/206 to appear before REQ-207-210 in the document, matching
  their numeric order (moved text only, no IDs changed); (3) added a
  minimum-age statement to the privacy policy draft, matching the ToS
  draft; (4) added a `--dry-run` mode to the prod→non-prod sync script and
  a matching workflow input; (5) added an archiving policy note to this
  changelog and a stack-version pointer to README.md. One review finding
  turned out to be inaccurate on closer inspection during the fix pass
  (the "backup procedure duplication" — it was actually a correct
  reference, not a restatement) and has been corrected in the review doc
  rather than "fixed" as if it were real.

- 2026-07-07 — .claude/agents/requirements-writer.md (new),
  .claude/agents/code-reviewer.md (new), docs/coding-guidelines.md (new),
  NOTES.md (new), CLAUDE.md, .claude/README.md, README.md — Evaluated five
  proposed additions and added three: `requirements-writer` (drafts/reviews
  REQ entries in the established format) and `code-reviewer` (general
  code-quality/refactor review against a new `docs/coding-guidelines.md`,
  distinct from `architecture-reviewer`'s structural-boundary-only focus).
  Declined a dedicated git/PR agent as unnecessary — Claude Code's native
  git/PR handling covers this; added a "Git and PR conventions" section to
  CLAUDE.md instead (commit message format referencing REQ/ADR IDs, branch
  naming, PR description requirements). Added `NOTES.md` as a lightweight
  running-notes file for gotchas/context that don't warrant a formal ADR —
  distinct from `CLAUDE.md` (which already serves as Claude Code's primary
  persistent memory) rather than a redundant second "memory" file.

- 2026-07-07 — README.md (new), TODO.md (new), .claude/README.md (new),
  .claude/agents/game-scaffolder.md (new), .claude/agents/ui-implementer.md
  (new), .claude/commands/new-game.md (new), .claude/commands/test.md (new),
  CLAUDE.md — Filled three gaps: (1) no human-facing guide existed for
  actually using the agents/commands — added `.claude/README.md` with
  concrete development/testing/new-game/design workflows; (2) no
  consolidated action-item checklist existed — action items were scattered
  across ADRs and infra docs, now gathered into `TODO.md`; (3) no agent
  existed for the two workflows explicitly asked about — added
  `game-scaffolder` (new game modules, enforcing the ADR-0002/0003
  boundaries) and `ui-implementer` (frontend work, enforcing the
  design-document.md token system). Added a root `README.md` as the
  human entry point to the repo, which didn't exist before (only
  `CLAUDE.md`, which is agent-facing).

- 2026-07-06 — requirements-document.md (§7 resolved), docs/legal/terms-of-service-draft.md —
  Resolved the last two open questions: minimum age is 16, enforced via a
  self-declared checkbox at signup (REQ-701) with no independent
  verification; governing law is Sweden, operated as a personal project
  rather than under SyVe or a separate entity. No open questions remain.

- 2026-07-05 — requirements-document.md (REQ-201/202/203/210 rewritten,
  §6 crest decision revised), architecture-document.md, implementation-document.md,
  design-document.md, mockups/design-mockups.html — Two design tightenings:
  (1) club crests deferred entirely to Phase 2 — v1 ships with the
  placeholder initial-badges as the actual design, not a stand-in; the
  `ClubCrest` caching approach stays designed but unbuilt, same pattern as
  the notifications deferral; (2) replaced the 10-attempt brute-force cap
  with a much tighter rule: max 2 guesses per cell, and a correct answer
  locks the cell immediately (even on attempt 1) rather than waiting for
  round close. This required making explicit that correctness is revealed
  to the player immediately on submission (REQ-203), not withheld until
  round close — the design doc now specifies four distinct cell states
  instead of two (correct-live, incorrect-with-retry, incorrect-exhausted,
  final), and disambiguation resolution no longer consumes an extra attempt.
- 2026-07-05 — docs/decisions/0008 (new), requirements-document.md (REQ-210,
  REQ-710, REQ-711, REQ-901, REQ-902, §7 updated), architecture-document.md,
  implementation-document.md, infra/README.md, .github/workflows/backup-database.yml
  (new), docs/legal/privacy-policy-draft.md (new), docs/legal/terms-of-service-draft.md
  (new), CLAUDE.md — Added the four gaps flagged in review: (1) verified
  API-Football's actual terms directly — fantasy-game use is explicitly
  named as intended, crest caching is their own recommendation, one clause
  is ambiguous enough to warrant a pre-launch confirmation email (ADR-0008);
  (2) drafted a privacy policy and terms of service grounded in the
  system's real data flows, clearly marked as unreviewed drafts, which
  surfaced two genuine open questions (minimum age, governing law/entity)
  now tracked in §7 rather than guessed at; also added REQ-710 (account
  deletion, anonymizing rather than hard-deleting `Guess` rows to preserve
  other players' historical scores) and REQ-711 (data export); (3) added
  REQ-210: a per-cell guess-attempt limit (default 10, later tightened to 2
  — see the entry above) to prevent brute-forcing a cell's answer via the
  immediate correctness feedback in REQ-203, via a new `Guess.AttemptCount`
  field; (4) confirmed directly against Supabase's docs that the free tier
  has zero automated backups — added a daily `backup-database.yml`
  workflow with a documented restore procedure (REQ-901), and REQ-902 for
  scheduled-job failure alerting via GitHub's built-in notifications.
- 2026-07-05 — requirements-document.md (REQ-108 new, REQ-706 resolved,
  §5/§6/§7 reorganized), implementation-document.md (TrophyDefinition,
  ClubCrest entities), design-document.md, infra/README.md — Resolved the
  three remaining open questions: (1) round-result notifications default
  opted-in with easy unsubscribe, with a compliance note distinguishing
  this from marketing consent under GDPR; (2) Trophy added as a v1 category
  type alongside Country/Club (REQ-108), Position/Era explicitly deferred
  rather than left ambiguous; (3) club crest imagery sourced from
  API-Football (verified free tier: 100 req/day, fits the platform's
  cache-once model per ADR-0001 since each crest is fetched once and never
  re-polled). No open questions remain as of this entry.
- 2026-07-05 — requirements-document.md (REQ-107, REQ-207–209, §5/§6
  reorganized), architecture-document.md, implementation-document.md,
  design-document.md, .github/workflows/ci.yml, .github/dependabot.yml
  (new) — Fixed two gameplay gaps: (1) autocomplete was scoped to the
  narrow incrementally-built attribute cache, which leaked answer validity
  and made guessing trivially easy — fixed via a new broad
  `PlayerNameIndex` (COMP-10) used only for autocomplete, kept strictly
  separate from the correctness-checking cache — ADR-0007; (2) name
  matching now normalizes diacritics/case/punctuation, checks a
  `PlayerAlias` table for nicknames (e.g. "Kaká"/"Kaka"), tolerates minor
  typos, and disambiguates multiple same-named players by checking each
  against the cell's categories, only prompting the player when genuinely
  ambiguous (REQ-208/209, SCREEN-02a). Added REQ-107: grids are Club×Club
  or Club×Country, never Country×Country. Updated framework versions to
  current verified-stable (.NET 10 LTS, Node.js 24 Active LTS, React 19)
  and added Dependabot to keep minor/patch versions from drifting.
  Restored a requirements-doc section heading that had been accidentally
  dropped in an earlier edit. Resolved several previously-open questions as
  concrete technical defaults (password policy, synthetic user domain,
  league limits, rate-limit thresholds) rather than leaving them open.
- 2026-07-04 — design-document.md (v0.2, superseding v0.1), requirements-document.md
  (REQ-107, new), mockups/design-mockups.html (rebuilt) — Redesigned from a
  dark broadcast-scoreboard direction to a light, clean, imagery-led one:
  flags (emoji, no licensing concern) and club badges (placeholder
  initial-chips — real crests are trademarked, sourcing tracked as an open
  question) now carry the visual personality instead of a dark palette.
  Recolored tokens (green=live, gold=final/correct, red=incorrect) for a
  light surface. Replaced the split-flap signature animation with a
  "badge dock" reveal tied to the actual game mechanic. Added REQ-107:
  grids may be Club×Club or Club×Country, never Country×Country.
- 2026-07-04 — requirements-document.md (REQ-606, REQ-607, §4.9 new),
  architecture-document.md, implementation-document.md, infra/README.md,
  CLAUDE.md — Added testability via a non-prod-only test-data API
  (create/reset/scenario, REQ-801–804), a security baseline (REQ-606: HTTPS
  everywhere, admin authorization tests, input validation, dependency
  scanning, rate limiting on auth endpoints), and a performance baseline
  (REQ-607: leaderboard pagination, required indexes). Introduced a
  two-Supabase-project environment split (prod + non-prod, using both of
  the free plan's project slots) with a one-way, non-PII sync script
  (`infra/scripts/sync-prod-to-nonprod.sh`, allowlist-based) and a
  manual-only `sync-environments.yml` workflow — ADR-0006. Added COMP-09
  Testing.SeedManager and boundary rule 4 (test data only via normal write
  paths). Added `main.parameters.nonprod.json` and wired `ci.yml` to reset
  non-prod test data before E2E runs.
- 2026-07-04 — requirements-document.md (§4.7, new), architecture-document.md,
  implementation-document.md, infra/README.md, CLAUDE.md — Added account
  creation with email confirmation (REQ-701–705: signup, blocked actions
  until confirmed, link-or-code confirmation email, resend, expiry) and a
  deferred REQ-706 for round-result notification emails. Added COMP-08
  Core.Notifications and the email-sending boundary (auth emails via
  Supabase custom SMTP; product emails via direct Resend API calls) —
  ADR-0005. Added `User` and `NotificationPreference` entities to the data
  model and a `XGArcade.Email` project. Updated infra/README.md with Resend
  cost numbers and the manual Supabase SMTP setup steps.
- 2026-07-04 — design-document.md (new), CLAUDE.md — Added the UX/design
  document: color/type/layout token system (pitch-dark + gold/teal accents,
  Space Grotesk/Inter/IBM Plex Mono), key screens (grid home, guess input,
  leaderboard, admin review), the split-flap reveal as the signature
  interaction, responsive strategy, copy voice, and accessibility floor.
  Wired into CLAUDE.md's doc map and a new "frontend visual consistency"
  convention.
- 2026-07-04 — infra/README.md — Added a verified cost reality check
  (free-tier limits per service) and flagged the Supabase 7-day pause as an
  accidental dependency on the daily sync-players.yml job.
- 2026-07-04 — architecture-document.md, implementation-document.md,
  CLAUDE.md — Resolved backend/frontend hosting to Azure Container Apps +
  Azure Static Web Apps, IaC to Bicep, registry to GHCR, auth bundled into
  Supabase — ADR-0004. Added `/infra/bicep` modules, `/infra/README.md`,
  and `/.github/workflows` (ci.yml, deploy.yml, sync-players.yml,
  generate-round.yml). Added a "Getting started" scaffold checklist to
  CLAUDE.md since no application code exists yet.
- 2026-07-04 — requirements-document.md, architecture-document.md,
  implementation-document.md, CLAUDE.md — Renamed root project from
  "Grid Guess" to "Platform" (placeholder, later renamed again to
  "xG Arcade" on 2026-07-07) with the grid game (later "xG Grid") as the
  first game; generalized `Round` to reference games via opaque `GameKey`/
  `GameInstanceId` instead of a direct `GridInstanceId` FK — ADR-0003.
  Flagged `Guess.CellId` as an accepted v1 simplification with the same
  issue, to be revisited when a second game is built.
- 2026-07-04 — requirements-document.md, architecture-document.md,
  implementation-document.md — Initial documentation set created, including
  incremental data cache strategy — ADR-0001, ADR-0002
