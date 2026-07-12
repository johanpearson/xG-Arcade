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
