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
