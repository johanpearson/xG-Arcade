# Development Backlog — xG Arcade

Ordered stories for building Tier 0 (see `MVP-SCOPE.md`) incrementally.
**Work top to bottom.** Every story leaves the system deployable and
testable — no story depends on a later one. Each references the REQ IDs
its tests must be named after (`REQ###_...`, see `docs/coding-guidelines.md`).

> **For AI agents:** treat one story as one working session/PR. Definition
> of done per story: acceptance criteria met, tests named after the listed
> REQ IDs pass, `ci.yml` green, docs updated if reality diverged
> (`/update-docs`), CHANGELOG entry if docs changed. Do not start a story
> before its dependencies are merged. Do not pull Tier 1 items forward.

## Epic 0 — Foundations (no game logic yet)

**S-001 · Repo + pipeline skeleton**
Scaffold `backend/XGArcade.sln` (Api, Core, Games.XGGrid, Data, DataSync +
Tests projects — empty but compiling), `frontend/` (Vite + React + TS,
Vitest + Playwright wired), `backend/Dockerfile` (port 8080).
*Accept:* `dotnet test` and `npm run test` pass locally with placeholder
tests; Docker image builds. *Deps:* none.

**S-002 · Trivial end-to-end slice deployed**
`GET /health` endpoint + a frontend page that calls and displays it.
`ci.yml` (Tier 0 shape: unit tests + local-stack E2E, no dev deploy — see
its header comment) passes; `deploy.yml` deploys both to Azure **dev**
(Tier 0's one environment — see `MVP-SCOPE.md` for why it's named "dev,"
not "prod"); fill in the post-deploy secrets (`DEV_BACKEND_HOSTNAME`,
`DEV_FRONTEND_HOSTNAME` — feeds the backend's CORS-allowed origin, see
`infra/README.md` —, and the static web app token). Also restore `ci.yml`'s `e2e-tests` job to its full
version — S-001's PR commented out the Postgres service container,
migrate-and-seed, and "Start API"/wait-on-`/health` steps (branch
protection couldn't be relaxed, and those steps need things that didn't
exist yet); uncomment them, add a real `/health`-wait step, and remove the
"dumbed down" note once `/health`/`migrate-and-seed` exist for real.
*Accept:* the deployed URL shows the health status from the deployed API.
*Deps:* S-001, `MVP-SCOPE.md` preconditions all checked.

**S-003 · Database + EF Core baseline**
Npgsql/EF Core wired to the Supabase connection string; initial migration
with `CountryDefinition`, `ClubDefinition` (Name + WikidataQid only),
`TrophyDefinition` (exists but unused in Tier 0), `Player`, `PlayerData`,
`PlayerAttribute`, `PlayerOverride`; unique indexes per
`implementation-document.md` §5. Repository pattern per
`coding-guidelines.md` (no DbContext in controllers).
*Accept:* migration applies cleanly against prod; REQ-109-named test
proves category values come only from reference tables. *Deps:* S-002.

**S-004 · Auth (Supabase, no email confirmation)**
Email+password signup/login via Supabase Auth (confirm-email OFF), JWT
validation middleware in the API, 16+ self-declaration checkbox at signup
(REQ-701's checkbox clause only — defer the rest of 7xx).
*Accept:* REQ701-named test: signup blocked without checkbox; a protected
endpoint rejects anonymous calls. *Deps:* S-003.

## Epic 1 — Game data (Wikidata)

**S-005 · Seed reference data**
Seed (SQL or seeder) with the actual decided list — QIDs already verified,
see the tables in `MVP-SCOPE.md`: 15 clubs, 20 countries. Pure data entry
now, no research needed.
*Accept:* seed is idempotent; rows present in dev (Tier 0's one environment). *Deps:* S-003.

**S-006 · Wikidata client (COMP-07, Tier 0 half)**
SPARQL intersection query per `implementation-document.md` §6a (P106/P27/
P54, QIDs from the reference tables), ~15s timeout (ADR-0011 addendum), bindings-format parser,
results persisted to `PlayerData`/`PlayerAttribute` as `unverified`
(REQ-103's persist-immediately rule; skip the API-Football fallback half).
Three correctness rules from §6a are non-negotiable: **no LIMIT** on the
intersection query (its results are the cell's complete answer key),
**upsert by `WikidataQid`** (never insert per query), and fetch
`skos:altLabel` into `PlayerAlias` in the same query. Tier 0's country
list uses United Kingdom, not England — every country query is uniform
`P27`, no special case needed here (see `MVP-SCOPE.md`; the `P1532`
exception for home nations is Tier 1's "national teams" feature, not
something this story needs to handle).
*Accept:* REQ103-named tests with mocked HTTP: hit persists players +
aliases, re-running the same query creates zero duplicate Players,
timeout/no-match returns empty without throwing. Manually verify at least 2-3 seeded clubs' QIDs point at the
senior/first-team item, not a generic club concept (REQ-109) — this can't
be unit-tested, it's a data-curation check against real Wikidata pages.
*Deps:* S-005.

## Epic 2 — Grid generation

**S-007 · Grid generation (REQ-101/102/107/109)**
`IGameModule` + `GenerateInstanceAsync` in Games.XGGrid: pick values from
reference tables, never Country×Country, cache-first then S-006 lookup,
`MIN_VALID_ANSWERS` threshold, retry/abort logic, `GridInstance`/`GridCell`
persisted; Core.Rounds references only `GameKey`/`GameInstanceId` (ADR-0003).
*Accept:* REQ101/102/107/109-named unit tests all branches; an internal
endpoint generates a real grid in dev. *Deps:* S-006.

**S-008 · Rounds + scheduling (REQ-301-30x, REQ-806)**
Round entity (start/end, `allow_guess_change`), `/internal/generate-round`
(bearer `INTERNAL_JOB_TOKEN`) wired to `generate-round.yml`; generation
runs **one round ahead** (REQ-301) so a failed generation has a full
round-length window before players see a gap; round-close logic (real
scoring lands in S-011) plus REQ-806's `POST /internal/test-data/force-close-round/{id}`,
gated to non-Production — this is what makes S-011's E2E test possible at
all without waiting for real time.
*Accept:* scheduled workflow creates round N+1 while N is active in dev;
`generate-round.yml`'s cron re-enabled (it ships commented out); the
force-close endpoint is absent when `ASPNETCORE_ENVIRONMENT=Production`.
*Deps:* S-007.

## Epic 3 — The game loop

**S-009 · Guess submission (REQ-201/202/203/208/210 + simplified 209)**
POST guess: active-round check, 2-attempt cap with immediate lock-on-correct
(REQ-210, checked before name resolution), basic normalization only
(lowercase/diacritics/punctuation — no aliases, no fuzzy), simplified
disambiguation (any matching player fitting the cell → accept; log
multi-fit cases per `MVP-SCOPE.md`'s Tier 1 trigger), correctness shown
immediately, distinct rejection reasons (REQ-202).
*Accept:* REQ201/202/203/208/210-named tests covering every branch listed
in those REQs' test-level notes. *Deps:* S-008, S-004.

**S-010 · Grid UI (SCREEN-01/01a/02)**
Grid home + guess input per `docs/design-document.md` (ui-implementer
rules: tokens only, four cell states, text-not-color-only, 44px targets,
reduced-motion). Plain text input — no autocomplete. Also added the two
backend pieces this screen needed to have anything real to render/seed
against: `GET /rounds/current` (REQ-303) and the non-Production
`POST /internal/test-data/seed-guessable-round` (REQ-807).
*Accept:* Playwright: log in → open round → submit a wrong guess (see
immediate incorrect + attempt count) → submit the correct one (locks live);
a second E2E case covers the two-wrong-guesses lock path. **Built as:**
three of the four cell states (correct/live, incorrect-with-attempts,
incorrect-locked) are exercised through Playwright; the fourth
(round-closed/"final") isn't reachable via the live API yet (`GET
/rounds/current` only ever returns an Active round — round-close is S-011
scope) and is instead covered by `CellState.test.tsx` (Vitest, constructed
props) — so "all four cell states render" is true, but not all four via
Playwright as originally phrased here. *Deps:* S-009.

**S-011 · Scoring + leaderboard (REQ-204/205/206/401)**
Live uniqueness on read; round-close job locks `final_*` fields and blocks
further guesses; total score; global-leaderboard endpoint + SCREEN-03.
*Accept:* REQ204/205/206-named tests; E2E: two users guess, REQ-806's
force-close endpoint ends the round, leaderboard shows locked totals. *Deps:* S-010.
**Built as:** matches the plan closely, plus one deliberate scope addition
and one acknowledged gap. `UniquenessCalculator`/`ScoreLockingService`/
`ScoreCalculator` (`XGArcade.Core.Scoring`) and `ILeaderboardService`
(`XGArcade.Core.Leagues`, COMP-02's first real code) implement REQ-204/
205/206/401 as scoped. Added, not originally planned: a required
`User.DisplayName` field collected at signup (`AuthController.Signup`,
`AuthScreen.tsx`), so the leaderboard never has to show another player's
email — this was a deliberate, explicitly-confirmed scope decision, not a
silent expansion (touches REQ-401/404/701; see
`docs/legal/privacy-policy-draft.md`). REQ-807's seeding endpoint was
extended (not replaced) to seed a second valid player per cell
(`AlternateCorrectPlayerName` in the response) so two players could each
score a different correct answer for a meaningful REQ-204 uniqueness test.
An architecture-reviewer pass caught score-locking/leaderboard-aggregation
logic initially living in the wrong components (inline in `Core.Rounds`/
the API layer) and it was extracted into `Core.Scoring`/`Core.Leagues`
before merge — no ADR needed, this was a fix, not a new structural
decision. **Acknowledged gap, not fixed this story:** `GET
/leagues/global/leaderboard` returns every league member unbounded — REQ-
607's pagination clause is not met; see that REQ's status note for the
explicit revisit trigger. Custom leagues (REQ-402/403) remain unbuilt, as
planned.

## Epic 4 — Playable-release hardening (still Tier 0)

**S-012 · Admin data correction (REQ-501-503, minimal)**
`PlayerOverride` CRUD (override always wins — REQ-501 test) via a minimal
protected admin endpoint/page; list unverified `PlayerData`. Admin
authorization = `Admin__UserIds` env var per `implementation-document.md`
§4 — no role tables.
*Accept:* REQ501-named test: override flips a cell's correctness; a
non-admin user gets 403. *Deps:* S-009.
**Built as:** backend-only, a deliberate scope decision — "page" in the
plan above did not get built (SCREEN-04/`design-document.md` untouched);
only the API did. New "Admin" authorization policy
(`AdminRequirement`/`AdminAuthorizationHandler`,
`XGArcade.Api.Auth.AdminAuthorization.cs`) checks the JWT `sub` claim
against `Admin:UserIds`/`Admin__UserIds`, re-parsed per request, exactly as
`implementation-document.md` §4 already planned. `XGArcade.Api.Admin
.AdminEndpoints` adds `GET /admin/player-data/unverified` (REQ-503's list
half) and full `PlayerOverride` CRUD (REQ-501) — `POST` 400s on missing
field/value/reason, 404s on an unknown `PlayerId`, 409s if an override
already exists for that `(PlayerId, Field)` (use `PUT` to update — one
override per field, per ADR-0015). Reached `Data.PlayerStore`/COMP-06 only
through the existing `IPlayerStoreRepository` interface — five new methods
added there, no new data-access path, no schema/migration change. Not
built, and out of scope for this story's acceptance criteria: REQ-503's
"approve → verified" and "remove the data point" actions (no endpoint
flips `PlayerData.Confidence` or deletes a row), and any separate audit-log
table beyond `PlayerOverride.LockedByAdminId`/`LockedAt` on the override
row itself. Rate limiting remains unimplemented, unrelated to this story.
`Admin__UserIds` threaded through `infra/bicep` → `deploy.yml` from a new
`DEV_ADMIN_USER_IDS` GitHub secret — not yet created, needs a human to set
it to their own Supabase auth user id before any admin endpoint will
succeed for anyone. Tests: `AdminEndpointTests.cs` (new file, full endpoint
coverage plus the two REQ501-named tests the acceptance criteria call for)
and new `PlayerStoreRepositoryTests.cs` coverage for the five repository
additions. An architecture-reviewer pass and a code-reviewer pass both ran
clean — no boundary violations, no ADR needed (this implements an
already-decided design from `implementation-document.md` §4, not a new
architecturally significant choice).

**S-013 · First-release QA pass**
Full E2E suite green in CI (local stack); a manual smoke test of the same
flows against the deployed dev URL (login → guess → score); spot-check a
sample of rejected guesses (seeds the Tier 1 triggers in `MVP-SCOPE.md`);
accessibility pass on the four cell states (contrast — resolves the design
doc's open gold-on-white question); fix what falls out.
*Accept:* you can play a full round end-to-end on your phone and the
result feels correct and fair. *Deps:* S-011, S-012.
**Built as:** ran the full local-stack E2E suite for real (this repo's
sandbox has no Docker daemon, so Postgres 16 was run directly via
`pg_ctlcluster` instead of `ci.yml`'s service container — same schema/
seed/migrate path either way) and it caught a real, previously-unverified
bug: `tests/e2e/play-grid.spec.ts` had never actually run against a real
`WikidataClient` before — REQ-211/ADR-0018's live-lookup fallback (a
guess that misses cache re-runs the cell's Wikidata query, added after
this spec was last touched) means every wrong guess now costs one live
HTTP round trip before the guess response returns, and the spec's
dialog-close assertions were still sized for the pre-ADR-0018 cache-only
path (5s default) instead of the latency budget ADR-0011 already
documents for that call (its own 15s timeout; 9-27s observed for real
WDQS queries). Confirmed directly against the running API (`curl`-timed
guess submissions) before touching the test: a wrong guess consistently
took 0.4-6s in this sandbox (Wikidata itself is unreachable here, so the
cost is however long the network layer takes to fail, not real query
time) — not a hang, not a deadlock, just a real network round trip the
test never budgeted for. Fixed by widening only the assertions that
follow a cache-missing guess to `WRONG_GUESS_TIMEOUT_MS` (20s) and giving
the whole spec file a 60s per-test timeout (`test.describe.configure`),
rather than loosening the global Playwright config or touching
`GridGameModule`'s already-accepted ADR-0018 behavior — this is a test
correctness fix (the test's own timing assumption was stale), not a
product behavior change. Backend suite (218 tests across 5 projects) and
frontend unit suite (30 tests) both passed unmodified. **Accessibility
pass, gold-on-white (and green-on-white) resolved:** computed WCAG
relative-luminance contrast for both original accent tokens against
`surface-card`/`#FFFFFF` — `accent-gold` measured ~2.6:1 (fails even the
3:1 large-text/icon floor) and `accent-green` ~3.4:1 (fails the 4.5:1
normal-text/button-label floor, though it does clear 3:1 for its existing
non-text uses). Added two darkened, same-hue tokens
(`accent-gold-text` `#8D6C20` ~4.9:1, `accent-green-text` `#187E4F`
~5.1:1) to `design-document.md` §2 rather than editing the originals in
place, since the lighter/more saturated originals remain correct for
non-text/decorative use (live-dot, focus ring, tab underline — all
already clear the applicable 3:1 non-text floor). Applied the new tokens
everywhere gold/green painted text, an icon, or a button label carrying
white text: `CellState.css` (correct icon + correct-state meta text —
the actual "four cell states" this story's acceptance criteria names),
plus `GuessInput.css`/`AuthScreen.css`'s submit buttons and
`LeaderboardScreen.css`'s "you" tag (found during the same pass, same
class of bug, fixed for the same reason — accent-red's existing ~4.9:1
needed no change). Verified visually via a local screenshot of a
locked/correct cell (dev server + seeded round) before and after.
**Not performed, flagged rather than faked:** the manual smoke test
against the deployed dev URL and a live spot-check of rejected guesses
both require network access this sandbox doesn't have (same
`wikidata.org`/proxy-blocked limitation NOTES.md already records for
S-006/ADR-0017) — no deployed-environment credentials or reachable
`DEV_BACKEND_HOSTNAME`/`DEV_FRONTEND_HOSTNAME` either. Neither is a Tier 1
trigger (both are one-time manual QA steps, not deferred features) — left
as an explicit follow-up for whoever next has real access, same pattern
as the existing Wikidata-QID-verification note. No new Tier 1 trigger
observed this session: the only real bugs found (E2E timeout assumption,
contrast) were both fixable within Tier 0 without pulling anything
forward.

**Tier 0 complete when S-013 passes.** Play it for a while before touching Tier 1.

## Epic 5 — Post-launch tuning (Tier 0, found during play-testing)

Findings from playing the completed Tier 0 build, triaged against
`MVP-SCOPE.md`'s Tier 0/1 split on 2026-07-11 (see that session's
discussion) — both items below tune or complete already-decided Tier 0
scope, neither pulls Tier 1/2 complexity forward.

**S-014 · Raise minimum valid answers per cell (REQ-101)**
Live play testing found cells generated with only `MIN_VALID_ANSWERS`
(default 3) matching players felt too thin. Raise the default to 5 in
`GridGenerationOptions`; update REQ-101's acceptance text to match the new
default.
*Accept:* REQ101-named test asserts the new default; existing
grid-generation unit tests updated for the new threshold. *Deps:* S-007.

**S-015 · Badge-dock guess animation (SCREEN-01a, `design-document.md` §2)**
Implement the "badge dock" slide-in animation already specified in
`design-document.md` §2 (row/column badge slides inward and settles by the
revealed player name) on a correct guess and on round-close reveal,
including the already-specified `prefers-reduced-motion` fallback (a
background color flash instead of the slide). This was part of the
original design S-010 was scoped against but the animation itself was
never built — closing that gap, not designing something new.
*Accept:* Playwright/Vitest coverage confirms a correct guess triggers the
animation (or its reduced-motion fallback); verified visually against
`design-document.md`'s mock. *Deps:* S-010.

**S-016 · Repeat/confirm password field at signup (REQ-701)**
Add a "confirm password" field to the signup form and API; reject the
request if it doesn't match the primary password, before Supabase Auth is
ever called (same pattern as the existing age-checkbox/DisplayName
pre-checks in `AuthController.Signup`). Update REQ-701's acceptance
criteria to include this clause.
*Accept:* REQ701-named test: signup is rejected with mismatched
confirm-password, without calling Supabase Auth. *Deps:* S-004.
**Built as:** matches the plan exactly, no deviations. `SignupRequest`
(`AuthDtos.cs`) gained `ConfirmPassword`; `AuthController.Signup` checks
`Password != ConfirmPassword` first, before the existing DisplayName/
AgeConfirmed pre-checks and before Supabase Auth is ever called, same
"checked before Supabase" discipline as ADR-0013. `AuthScreen.tsx` adds a
signup-only "Confirm password" field with a matching client-side check
("Passwords do not match.") that blocks submission without calling the
API. `REQ701_Signup_BlockedWithMismatchedConfirmPassword`
(`AuthEndpointTests.cs`) and a matching Vitest case
(`AuthScreen.test.tsx`) cover it; `tests/e2e/play-grid.spec.ts`'s signup
step was updated to fill the new field so the existing E2E flow keeps
passing. 220 backend / 39 frontend tests green. No new component or
boundary — architecture-reviewer pass confirmed no ADR needed.

**S-017 · Enforce display-name uniqueness (REQ-401/701)**
Add a case-insensitive uniqueness constraint on `User.DisplayName` (DB
unique index + a clear signup-time error, not a generic failure) — spaces
remain allowed, this only closes the uniqueness gap, not a username-style
format change. Update REQ-701's acceptance criteria to state the
uniqueness requirement explicitly.
*Accept:* REQ701-named test: signup with an already-used display name (any
casing) is rejected with a clear error; existing display names unaffected.
*Deps:* S-011 (DisplayName exists).
**Built as:** matches the plan, plus one migration-safety addition and two
code-review fixes. `User.DisplayName`'s setter now also maintains a new
`NormalizedDisplayName` column (lowercase-folded via a new public static
`User.NormalizeCase`, the one place "case-insensitive" is defined) backed
by a DB unique index (`XGArcadeDbContext`); `IUserRepository` gained
`DisplayNameExistsAsync`, called by `AuthController.Signup` as a pre-check
before Supabase Auth is ever called (ordered after the free local checks,
last since it's the only one costing a DB round trip), returning 409 via a
new shared `DisplayNameConflictProblem()` helper. `UserRepository.AddAsync`
catches the DB constraint violation as a race-safety net and throws the new
`DisplayNameAlreadyInUseException`, which the controller also maps to the
same 409 (now logged via a new `ILogger<AuthController>` constructor
parameter, per a code-reviewer finding that the race path was otherwise
silent). Deviation from the plan: the migration
(`20260711203352_AddDisplayNameUniqueness`) also had to resolve
pre-existing case-insensitive collisions and empty `DisplayName` rows
before the unique index could be added — an architecture-reviewer pass
flagged this silent-rename-on-collision as a genuine decision needing its
own record, now `docs/decisions/0019-displayname-collision-migration-strategy.md`.
A code-reviewer pass separately flagged the case-normalization logic as
duplicated between `User.cs` and `UserRepository.cs` (fixed via
`NormalizeCase` above) and asked for a trim+case interaction test
(`REQ701_Signup_BlockedWhenDisplayNameMatchesExistingUserAfterTrimming`).
4 new tests in `AuthEndpointTests.cs`, plus a new
`UserRepositoryTests.cs` (4 tests). 228 backend tests green across all 5
projects. No new component or boundary beyond ADR-0019 above —
architecture-reviewer pass otherwise confirmed no boundary violation.

**S-018 · Live indicative points per cell (REQ-204/206 extension)**
Show a live, clearly-marked-as-provisional point value alongside the
existing live uniqueness % for each correctly-guessed cell while the round
is active, computed with the same formula `ScoreLockingService`/
`UniquenessCalculator` already use for the locked score. Update REQ-204's
acceptance criteria and SCREEN-01a's state-1 mock to include it, with
wording that makes clear it's an estimate that can still change, not a
preview of the locked score (avoid it reading as a promise).
*Accept:* REQ204-named test: the live point value returned by `GET
/rounds/current` for a correct cell equals `round(uniqueScore *
MaxPointsPerCell)` at read time; UI test confirms it's visually distinct
from a locked score. *Deps:* S-011.
**Built as:** matches the plan, plus one refactor called out in the task
itself. `CurrentRoundGuessResponse` gained `LivePoints` (int?, null exactly
when `UniquePercent` is), computed in `RoundEndpoints.cs` — but rather than
writing `round(uniqueScore * MaxPointsPerCell)` a second time next to
`ScoreLockingService`'s existing copy, that formula was extracted into a
single new method, `ScoringRules.PointsFromUniqueScore(double uniqueScore)`
(`XGArcade.Core.Scoring`), and both `ScoreLockingService.LockRoundScoresAsync`
(REQ-205's locked `FinalPoints`) and `RoundEndpoints` (this story's live
`LivePoints`) now call it — one formula, one place, not two
independently-written copies that could drift. Frontend: `livePoints`
threaded through `types.ts` → `GridScreen.tsx` → `GridCell.tsx` →
`CellState.tsx`, rendered in state 1 only as "~N pts estimated" appended to
the existing "X% unique" line — wording deliberately different from state
4's plain "X% unique · Y pts" so it never reads as a preview or promise of
the locked score; `GridScreen.tsx`'s optimistic post-guess state sets both
`uniquePercent` and `livePoints` to `null` for the same reason the former
already was (the write response doesn't echo either, only the next `GET
/rounds/current` does). Deviation from a literal reading of the accept
criteria: the 3 pre-existing REQ-204 API tests in
`CurrentRoundEndpointTests.cs` (0% unique, 50% unique, incorrect-guess-is-
null) got additive `LivePoints` assertions appended to their existing test
bodies rather than 3 new, separately-named REQ-204 tests — each of those
scenarios already exercised the exact `UniquePercent` state `LivePoints`
derives from, so a parallel set of near-identical tests would have doubled
the file for no additional coverage; the null-propagation and
formula-correctness assertions are still explicit and independently
readable within each test. 2 new dedicated tests were added in
`frontend/src/grid/CellState.test.tsx` (REQ-204-named), since these exercise
genuinely new rendering/wording behavior with no pre-existing equivalent —
41/41 frontend tests green (full suite run). Backend test suite could not be
executed in this environment (no dotnet SDK available); an
architecture-reviewer pass and a code-reviewer pass both reviewed the diff
instead and confirmed the formula-reuse fix and no boundary violation. No
new ADR — this is a refactor consolidating an already-decided formula
(REQ-205's), not a new architecturally significant choice, same reasoning
as S-011's inline-logic extraction.

**S-019 · Tap/long-press reveal of live per-cell info (REQ-204/SCREEN-01a
redesign)**
Replace the always-visible "X% unique · updates until round closes" text
in cell state 1 with the same text shown only on tap/long-press (or
equivalent focus/hover on desktop), keeping the existing quiet green dot
as the permanent at-rest "still live" indicator — addresses the clutter of
every unresolved cell showing full live text at once. Must keep REQ-204's
text-not-icon-only accessibility rule intact (the text still exists, it's
just not always rendered), and the interaction itself must be
keyboard/screen-reader accessible, not mouse/touch-only. Update
design-document.md SCREEN-01a's state-1 mock and REQ-204's UI acceptance
criteria to describe the new interaction.
*Accept:* REQ204-named UI test: live text is not present/visible until the
interaction fires, and is exposed accessibly (e.g. `aria-expanded`/
`aria-live` as appropriate) once revealed. *Deps:* S-010.
**Built as:** matches the plan, plus one race-condition fix found by a
code-reviewer pass mid-implementation. `CellState.tsx` gained a new
`LiveMetaDisclosure` sub-component (not a new file — it lives alongside
`CellState`) driven by three independent boolean flags — `toggledOpen`
(click), `hovering` (mouseenter/mouseleave), `keyboardFocused`
(focus/blur) — OR'd together as `revealed`, rather than one shared
toggle: a real mouse click fires a native `focus` event immediately
before its `click` event, so a single merged toggle would flash the
panel open (via focus) and instantly closed again (via the click's own
toggle) within the same physical click. A `pointerDownRef` flag
distinguishes a focus caused by a preceding mousedown (not counted as
`keyboardFocused`, since `hovering` already covers that case) from a real
keyboard Tab (still counted). The permanent "live" dot/text is now itself
the toggle button (`aria-expanded`, `aria-controls`), and the revealed
panel is `aria-live="polite"`. `GridCell.tsx` was restructured alongside
this: a locked cell (correct-and-live, or out-of-attempts) now renders
`<div role="group" aria-disabled="true">` instead of `<button disabled>`,
since nesting the new focusable reveal-toggle inside a disabled `<button>`
would make it keyboard-unreachable (and is invalid HTML besides);
`role="group"` was specifically chosen (verified against Playwright's own
`kAriaDisabledRoles` list in `playwright-core`) so the existing
`toBeDisabled()`/`toBeEnabled()` assertions in
`tests/e2e/play-grid.spec.ts` keep working unchanged — a bare `<div>`'s
implicit role is not in that list, `"group"` is. New
`frontend/src/grid/GridCell.test.tsx` covers the button/div branching
directly (didn't exist as a dedicated file before this story). 14 new
REQ-204-named Vitest cases were added to `CellState.test.tsx` covering the
disclosure open/close/hover/focus/aria-live behavior, plus the realistic
combined-event-sequence case (`userEvent.click`) that exercises the actual
click/focus race the flag-separation fixes. 54/54 frontend tests green
(`npm run test`), `tsc -b` and `npm run lint` both clean. No backend files
touched, so no `dotnet test` run for this story. No new ADR — this is an
interaction-pattern change within the existing SCREEN-01a/REQ-204 scope,
not a new component boundary or structural choice; an architecture-reviewer
consideration during doc-sync confirmed no `COMP-xx` boundary is touched
(frontend-only, no new API surface or data flow).

**S-020 · Incorrect-guess animation (SCREEN-01a extension)**
Add a subtle shake + red flash to a cell when a submitted guess is
rejected — a literal, immediate "no match" cue, distinct from (not reusing)
the correct-guess badge-dock motion. Respects `prefers-reduced-motion`:
flash only, no shake. Update design-document.md §2/SCREEN-01a to record
this as a designed element before building it (per CLAUDE.md's rule
against undocumented animations).
*Accept:* Playwright/Vitest coverage confirms an incorrect guess triggers
the animation (or its reduced-motion fallback). *Deps:* S-015 (build
alongside the correct-guess animation work).
**Built as:** matches the plan, plus one bug found and fixed by a
code-reviewer pass. `CellState.tsx` gained a new `useShakeToken` hook
(alongside S-015's `useRevealToken`, same "transition, not mount" trigger
shape) applying `cell-state--shake` — separate CSS keyframes
(`cell-state-shake` translateX wiggle + `cell-state-incorrect-flash`
red-to-transparent) from the badge dock's, remounted via `key={shakeToken}`
so repeated rejections on the same cell restart the animation — whenever
`attemptCount` increases while `isCorrect` stays false, covering both
state 2 -> state 2 and state 2 -> state 3 transitions; the existing
`prefers-reduced-motion` media query overrides it to the flash only, no
shake, matching the badge dock's own fallback pattern. An
architecture-reviewer pass ran clean (no boundary violation, no ADR
needed — a self-contained interaction-pattern addition to the existing
`CellState` component using only already-defined design tokens, same
reasoning as S-015/S-019). A code-reviewer pass then found a real bug:
because `GridCell` only renders `CellState` once a cell has a guess (an
unattempted cell shows a plain "+" placeholder instead), a cell's very
first-ever rejected guess this session mounted `CellState` directly
already-incorrect rather than transitioning into that state from an
already-mounted render — indistinguishable, from inside `useShakeToken`
alone, from a page reload showing a cell someone else already attempted
(which correctly must never shake). This silently contradicted
design-document.md's "fires on every rejected guess" line and would have
failed the new Playwright assertion. Fixed with a new
`submittedThisSession` prop (`GridCell` derives it from the existing
`knownPlayerName != null` signal, already the marker for "this browser
session submitted this guess") that seeds `useShakeToken`'s initial state
correctly only for a first-mount rejection, leaving a real page-load mount
silent as before. `useRevealToken` (S-015's badge-dock reveal) has the
identical latent gap for a cell's first-ever *correct* guess —
deliberately left unfixed here, out of this story's scope, same as other
documented "acknowledged gap, not fixed this story" notes elsewhere in
this backlog (e.g. S-011/S-018). Regression coverage added at both levels:
`CellState.test.tsx` gained unit tests for the new prop (including that it
only seeds on a rejection, not a correct first mount) and a new
`GridScreen.test.tsx` integration test drives the real
null-guess -> rejected-guess transition end to end — the level at which
the bug actually lived, since `CellState`-only tests couldn't see it.
`play-grid.spec.ts` asserts `.cell-state--shake` is visible on both the
state 2 and state 3 rejection paths.

**S-021 · Post-login game-selection landing page (REQ-303 UX addition)**
Add a landing screen shown immediately after login/signup, before the
grid: a single tile for xG Grid (the only game in Tier 0 — no backend
"list games" endpoint needed, since Tier 0 only ever has one game; the
tile is client-side static, keyed off the existing `GameKey="xg-grid"`
constant already used elsewhere per COMP-05) that the player selects to
enter SCREEN-01. Update REQ-303's user story/acceptance criteria to
describe "open the app, select a game, see that game's current round"
rather than the grid appearing immediately, and update the existing
`play-grid.spec.ts` E2E flow (REQ-701/303/201/203/210) to add the
selection step it currently skips.
*Accept:* REQ303-named test: after login, the player lands on the
game-selection screen, not the grid; selecting xG Grid navigates to
SCREEN-01/`GET /rounds/current`. Existing S-010-era E2E flows updated to
select xG Grid before interacting with the grid, still passing. *Deps:*
S-010.
**Built as:** matches the plan, plus one deviation and one
code-reviewer-found follow-up. Deviation: the plan didn't call out a way
back to the game-selection screen once a player has moved past it, so a
"Games" button was added to the header nav (alongside the existing
"Grid"/"Leaderboard" links) as the natural round-trip — `App.tsx`'s
`Screen` union gained a `'game-select'` member, which is also now the
default post-login/post-logout screen instead of `'grid'`. Since Tier 0
has exactly one game, `App.tsx` routes any `onSelectGame` call straight to
`'grid'` regardless of the `gameKey` argument passed
(`GameSelectScreen`'s exported `XG_GRID_GAME_KEY` constant) — a
code-reviewer pass flagged the discarded argument as worth a comment
explaining that's deliberate Tier-0 behavior, not an oversight, so one was
added at the `App.tsx` call site. The same pass suggested a regression
test for the new "Games" nav round-trip (login -> select xG Grid -> click
"Games" -> back on the game-selection screen), added to
`tests/unit/App.test.tsx` alongside the other two REQ-303 cases (lands on
game-selection after login; selecting xG Grid navigates to the grid). An
architecture-reviewer pass ran clean: no boundary violation, no ADR
needed — pure frontend routing, no backend endpoint added or changed, and
`XG_GRID_GAME_KEY` is a frontend-only constant with no coupling to
`GridGameModule`'s backend `GameKey`.

**Left open, not scoped as stories this round:** a scheduled/proactive
cache pre-warming job (no evidence on-demand fetching is a real problem
yet — revisit if S-014's threshold bump makes grid generation struggle in
practice), and selectable color themes/dark mode (design-document.md
already tracks this as a deliberately unresolved open question — a
reversal of the light-only v1 direction deserves its own design session,
not a quick story).

**S-022 · Fix uniqueness formula's self-comparison bug (REQ-204/205)**
Real play-testing found that a lone or first correct guesser for a cell
scored "0% unique" / 0 points — backwards from the intent that being the
only correct answer should score maximally. `UniquenessCalculator.Calculate`
compared each guess against a population that included itself, which is
degenerate at low guesser counts. Excludes the guesser's own guess from
both sides of the ratio; see ADR-0020 for the full rationale (this reverses
a previously-recorded "not a bug" decision from S-011).
*Accept:* REQ204/205-named tests updated across
`UniquenessCalculatorTests.cs`, `RoundCloseServiceScoringTests.cs`, and
`CurrentRoundEndpointTests.cs` to assert the corrected values (a lone
correct guesser locks/estimates at 1.0/`MaxPointsPerCell`, not 0.0/0); a new
test covers the 3-guesser partial-sharing case previously only covered at
the unit level. *Deps:* S-011, S-018.
**Built as:** matches the plan; `UniquenessCalculator.Calculate`
(`XGArcade.Core.Scoring`) now short-circuits to `1.0` when there are zero
*other* correct guessers, else computes `1 - (othersWithSameAnswer /
otherCorrectGuessCount)` — both counts exclude the guesser's own guess.
`ScoringRules.PointsFromUniqueScore` itself is unchanged (the fix is
entirely upstream, in the uniqueness fraction it's given). Seven existing
tests across three files were updated to their corrected expected values,
one new API-level test
(`REQ204_CurrentRound_Get_OneOfTwoOtherCorrectGuessersSharesMyAnswer_ReturnsUniquePercentHalf`)
and one new round-close test
(`REQ205_CloseRoundAsync_TwoOfThreeCorrectGuessesShareAnAnswer_SharedPairLocksHalfAndDistinctLocksFull`)
were added to keep the "genuine partial uniqueness, not just the 0/1
extremes" case covered now that the two-distinct-answer case scores both
guessers at 100%. `requirements-document.md` (REQ-204/205 status notes,
glossary), `architecture-document.md` (COMP-04 status note), and
`implementation-document.md` (§6a pseudocode) all updated to describe the
corrected formula; new `docs/decisions/0020-uniqueness-formula-excludes-self-comparison.md`.
Backend test suite could not be executed in this environment (no dotnet SDK
available, same limitation S-018 recorded) — all math was hand-verified
against the corrected formula and the existing/updated test expectations
before committing.

**S-023 · Fix live-meta-disclosure toggle not closing on a second click (REQ-204/S-019)**
Real usage found that clicking the "live" reveal toggle a second time didn't
close the panel — it only closed once the mouse physically moved away.
Root cause: a real click leaves the pointer resting on the button (it never
moved), so `hovering` stayed `true` through the whole click and kept
`revealed` true via the `toggledOpen || hovering || keyboardFocused` OR,
regardless of `toggledOpen` flipping back to `false`.
*Accept:* a second click closes the panel immediately, without requiring
the mouse to also leave the button; hover's own peek-on-enter/close-on-leave
behavior still works afterward. *Deps:* S-019.
**Built as:** `CellState.tsx`'s `LiveMetaDisclosure` gained a `hoverSuppressed`
flag: when a click transitions `toggledOpen` from `true` to `false` while
still hovering, hover's contribution to `revealed` is suppressed until the
pointer actually leaves (`onMouseLeave` resets it), so a click-driven close
sticks even though the mouse hasn't moved. `revealed` is now `toggledOpen
|| (hovering && !hoverSuppressed) || keyboardFocused`. The existing
`CellState.test.tsx` test that had asserted the old (buggy) behavior —
closing only after both a second click *and* `unhover` — was rewritten to
assert the panel closes on the second click alone, then verifies hover
still peeks correctly on a later, fresh mouse enter. No REQ/design-document
change: the fix makes the implementation match what S-019's own acceptance
criteria and design-document.md already described ("a tap toggles a
persistent open/closed state"), not a new interaction design.
A code-reviewer pass caught the identical bug still present on the keyboard
path (worse: pressing Enter an odd number of times before tabbing away left
the panel stuck open with no visible way to notice) — `keyboardFocused` had
no `keyboardSuppressed` counterpart, so a keyboard/screen-reader user could
never close the panel via Enter/Space at all. Fixed the same way, with a
mirrored `keyboardSuppressed` flag reset on blur; `revealed` is now
`toggledOpen || (hovering && !hoverSuppressed) || (keyboardFocused &&
!keyboardSuppressed)`. Two new tests cover it: pressing Enter twice closes
the panel without needing to blur first, and — to confirm this didn't
regress the *intended* persistence — an odd number of Enter presses
followed by tabbing away leaves the panel open (mirroring a mouse click's
own persistence after the pointer leaves), not silently closed. 71/71
frontend tests green (`npm run test`).

**S-024 · Leaderboard auto-refresh polling (REQ-401/404)**
`LeaderboardScreen` only fetched once per mount, so an already-open
leaderboard tab went stale as other players' rounds closed and locked new
`FinalPoints` — the only way to see updated totals was to navigate away and
back. Added polling while the screen stays mounted.
*Accept:* REQ401/404-named test: the leaderboard re-fetches and updates its
displayed totals on an interval without the player navigating away, without
re-showing the loading state on each poll tick, and without a transient
poll failure replacing an already-displayed leaderboard with an error.
*Deps:* S-011.
**Built as:** `LeaderboardScreen.tsx` now runs its fetch through a shared
`load(showLoadingState)` function, called once immediately
(`showLoadingState: true`) and then self-reschedules via `setTimeout`
(`showLoadingState: false`, `REFRESH_INTERVAL_MS` = 15s) — never flips back
to the `loading` phase, and a non-401 error on a background tick never
overwrites a good `ready` state with an error message; a 401 still calls
`onAuthError` regardless of which tick it happened on. Explicitly out of
scope, by design: this only refreshes the existing locked-total ranking
(`SUM(FinalPoints)`) faster — it does not fold in unlocked/live points from
an in-progress round, which would contradict REQ-205/S-018's "provisional,
never a promise" rule for live values. A code-reviewer pass flagged two
gaps in the first version: background poll failures were swallowed with
zero trace (now logged via `console.error`, still without touching
`state`), and a plain `setInterval` doesn't guard against overlapping/
out-of-order responses if a request ever runs longer than the interval
(switched to self-rescheduling `setTimeout` — the next poll is only
scheduled after the previous one settles, in `.finally()`, so at most one
fetch is ever in flight). Two new tests added to `LeaderboardScreen.test.tsx`
using fake timers: one confirms a poll tick updates the displayed totals
without flashing "Loading…," the other confirms a failed poll tick leaves
an already-loaded leaderboard displayed rather than replacing it with an
error. Full frontend suite (71/71) green.

**Verified, not a new story: post-login landing-page routing.** A reported
concern that logging in should always land on the game-selection screen,
loading a game's round only on explicit selection, turned out to already be
correctly implemented by S-021 (merged the same day) — `App.tsx` initializes
`screen` to `'game-select'` unconditionally (not persisted/restored to a
previous screen), `GameSelectScreen` performs no round fetching of its own,
and `GridScreen` is only mounted once `screen === 'grid'`. No code change
made; confirmed via the existing `REQ-303` App.test.tsx coverage plus manual
reading of the mount/render logic.

**Proposed, not yet built — drafted 2026-07-12 in response to direct product
feedback, queued here rather than implemented in the same session as
S-022–024 above, per this repo's one-story-per-session/PR convention:**

**S-025 · Self-service account deletion (REQ-710)**
`DELETE /account` (or similar): a confirmation-gated, irreversible action a
logged-in player can trigger themselves. Anonymize (`UserId = NULL`) the
player's `Guess` rows rather than deleting them (preserves other players'
historical uniqueness/leaderboard accuracy — same rule
`CLAUDE.md`/ADR-none already states for account deletion generally); delete
the `User` row, `NotificationPreference`, and the credential via the auth
provider; the email becomes available for a new signup afterward.
*Accept:* REQ710-named test (unit): anonymization leaves no reversible link
from a `Guess` back to the deleted user. REQ710-named test (API): deletion
requires the confirmation step, deletes/anonymizes exactly the rows REQ-710
specifies, and a subsequent login attempt with the same credentials fails.
*Deps:* S-004 (auth), S-009 (Guess exists to anonymize).

**S-026 · Admin UI page + round control + user deletion (REQ-504/505/506)**
Builds the actual admin page S-012 deliberately deferred (REQ-501/502/503's
override review UI), plus two new non-Production-only admin capabilities:
ending the active round or adjusting its schedule on demand (REQ-505 — the
human-facing, admin-authenticated equivalent of REQ-806's E2E-only
force-close endpoint, plus a new "adjust end_time" action REQ-806 doesn't
cover), and deleting a user's account (REQ-506, reusing S-025's REQ-710
anonymization logic via an admin-triggered path rather than a second,
independently-written deletion implementation). Both new admin-only actions
must follow ADR-0006's existing fail-closed pattern (endpoint not registered
at all in Production, checked in `Program.cs` before routing — never guarded
only by an attribute), the same rule already governing REQ-806's
`/internal/test-data/*` endpoints.
*Accept:* REQ501/502/503-named UI tests: the existing override-review flow
works end-to-end from the new page, not just via direct API calls.
REQ505-named tests: ending/rescheduling a round works for an admin and is
absent (404, not just 403) in a Production-configured test host. REQ506-named
tests: an admin can delete another user's account (reusing REQ710's
anonymization contract) and this is likewise absent in Production. A
non-admin gets 403 from every underlying endpoint and no visible entry point
to the page. *Deps:* S-012 (admin API/authorization already exists), S-025
(REQ-710's anonymization logic, reused rather than duplicated), S-008
(REQ-806's existing round-close/force-close logic, extended rather than
replaced).

**S-027 · Leaderboard time-window resolutions (REQ-405)**
Add round/week/month/year resolution tabs to the leaderboard, sorted
ascending like the existing all-time total (ADR-0021). REQ-405's open
design questions were resolved 2026-07-12: **calendar-aligned** windows
(ISO week, calendar month starting the 1st, calendar year — not rolling
7/30/365-day windows), evaluated in **UTC** (matches every other timestamp
in this system); **locked rounds only** — an in-progress/unlocked round
never contributes to any window, the same rule REQ-401/404's all-time
total already follows; "round" resolution means the single most recently
*closed* round for the game (Tier 0 still has no past-round-browsing UI —
REQ-206's known gap, unaffected by this story). This story's own
implementation must include a REQ-607-aligned indexing plan for the four
new query shapes (`Round.EndTime` range + `Guess.FinalPoints` sum), not
just "add a WHERE clause" against the existing unbounded query.
*Accept:* REQ405-named tests: each of the four resolutions returns the
correct ascending-sorted ranking for a seeded set of rounds spanning
multiple weeks/months/years; a round still in progress never appears in
any window's totals; the "round" resolution always resolves to the most
recently closed round, never an arbitrary one. *Deps:* S-011 (locked
`FinalPoints`/leaderboard exist).

**S-030 · Enable Club × Club grid pairing (REQ-107)**
`CategoryPairingRules.IsAllowedPairing` already permits Club × Club (only
Country × Country is banned) but `GridGameModule.GenerateInstanceAsync` is
currently hardcoded to always generate rows=Country/columns=Club — a Tier 0
scope restriction in `MVP-SCOPE.md`, not a REQ-107 constraint. Closes that
gap using data already seeded, no new reference table or category type
required. Generalize row/column header selection so a grid's pairing can
independently be Country×Club or Club×Club (never Country×Country). Also
extend `RefreshCellFromLiveLookupAsync` (REQ-211's live-lookup fallback),
which currently only knows how to refresh a Country×Club cell — a Club×Club
cell missing cache would otherwise silently fail closed and regress the
ADR-0018 wrongly-rejected-guess fix for this new pairing. Update
`MVP-SCOPE.md`'s "Grid content" line to reflect the removed restriction
(already done as part of scoping this story).
*Accept:* REQ107-named test confirms Club×Club grids generate correctly
(still never Country×Country, still N unique rows/N unique columns per
REQ-102); REQ211-named test confirms a Club×Club cell missing cache also
gets the live-lookup fallback. *Deps:* S-007, ADR-0018 (S-011 follow-up).

**S-031 · Trophy category — individual awards only (REQ-108, ADR-0012)**
Pulled forward from Tier 1 (`MVP-SCOPE.md`, 2026-07-12) after two weeks of
real play made Country×Club feel repetitive. Deliberately scoped narrower
than REQ-108's full definition: v1 seeds exactly one trophy, **Ballon
d'Or**, into `TrophyDefinition` (`Name`, `WikidataQid` — resolved by hand,
same one-time manual pattern as Country/Club QIDs, ADR-0012). "Satisfies
this category" means the player has a `PlayerAttribute` (or override)
record of type `trophy` with that value; the query uses Wikidata's `P166`
("award received"), a comparably simple shape to the existing Country×Club
intersection query, not a bulk import. Builds on S-030's generalized
row/column header selection so Trophy can pair with Country or Club (never
Country×Country per REQ-107); with only one trophy value seeded, a
Trophy×Trophy grid can never satisfy REQ-102's N-unique-headers requirement
and so structurally never generates — no separate categorical ban needed
beyond the existing retry logic. Also extends REQ-211's live-lookup
fallback (`RefreshCellFromLiveLookupAsync`) to handle a Trophy-typed cell,
same reasoning as S-030's Club×Club extension. Team-competition trophies
(World Cup, Champions League) are explicitly out of scope for this story —
a distinct follow-up once individual awards are proven out, since they need
a genuinely different Wikidata query pattern (squad membership + tournament
result — no single property links a player directly to "won this
tournament").
*Accept:* REQ108-named tests: a Trophy×Country/Trophy×Club grid generates
correctly with Ballon d'Or as the only seeded trophy value; a guess is
scored correct only via a `PlayerAttribute`/`PlayerOverride` record of type
`trophy`; REQ211-named test confirms a Trophy cell missing cache also gets
the live-lookup fallback. *Deps:* S-007, S-030 (shares the generalized
header-selection and live-lookup-fallback work).

**S-032 · Autocomplete + `PlayerNameIndex` (REQ-207, ADR-0007)**
Pulled forward from Tier 1 by deliberate choice, 2026-07-12 — not because
the `MVP-SCOPE.md` trigger strictly fired (no unprompted "typing is
tedious" complaint has been recorded), but chosen anyway. Builds exactly
what ADR-0007 already specifies, no new architectural decision needed: a
new `PlayerNameIndex` table (name, aliases, birth year, primary
nationality/club for display) populated via a one-time bulk Wikidata query
for `P106` = association football player, refreshed manually/periodically
(start manual, per ADR-0007's own follow-up note — tighten only if names
are noticeably missing after transfer windows). Guess input's autocomplete
suggestions query `PlayerNameIndex` only, never `PlayerAttribute`/
`PlayerOverride` — preserving ADR-0007's boundary rule that a name
appearing in autocomplete implies nothing about its correctness for the
current cell. Explicitly out of scope: REQ-208's alias-matching and
fuzzy-typo-tolerance clauses for guess *scoring* (a player can still
free-type past the suggestion list, and that path is unchanged) — this
story is the suggestion-list UX only, not a change to how a submitted guess
is checked.
*Accept:* REQ207-named test confirms the autocomplete data source is
`PlayerNameIndex`, structurally distinct from `PlayerAttribute` (e.g. a
name present in the index with zero `PlayerAttribute` rows still
suggests); UI test: typing a partial name shows matching suggestions from
the bulk-imported index; Manual: spot-check that early/sparse grids don't
become trivially easy to solve via what does/doesn't autocomplete
(REQ-207's own manual test-level note). *Deps:* S-009 (guess submission/
name matching exists), S-006 (Wikidata client exists to extend for the
bulk query).

**S-033 · Show point value on the "incorrect, no attempts left" cell state (REQ-204)**
Frontend-only gap, flagged and left unfixed three times (originally around
S-011, again at S-028): `CellState.tsx`'s state 3 (SCREEN-01a's "Incorrect,
no attempts remaining" — both guesses wrong, cell locked) renders "no
attempts left" with no point value, unlike every
other locked state (live-correct shows an estimate, locked-correct and
round-closed both show "Y pts"). `design-document.md`'s SCREEN-01a mock
already shows this state as "no attempts left · 100 pts" (corrected during
S-028/ADR-0021) — the component itself was just never updated to match. The
value is a known constant under golf-scoring rules
(`ScoringRules.MaxPointsPerCell`), not a live computation, so this is a
pure rendering fix with no backend/API change.
*Accept:* REQ204-named Vitest test confirms the incorrect/no-attempts-left
state renders the point value alongside "no attempts left," matching
`design-document.md`'s existing mock; visually verified against a locked-
incorrect cell. *Deps:* none — `CellState.tsx` and `MaxPointsPerCell` both
already exist.

**S-034 · Paginate the global leaderboard endpoint (REQ-607)**
Closes the gap an architecture-reviewer pass flagged during S-011 and
deliberately left unfixed at the time: `GET /leagues/global/leaderboard`
(`XGArcade.Api.Leagues.LeaderboardEndpoints`) still returns every league
member in one unbounded response. Build it per `implementation-document.md`
§6's already-specified pagination shape (`cursor`/`pageSize` query params,
`ORDER BY totalPoints ASC` per ADR-0021, response includes the requesting
user's own rank/row even if it falls outside the current page — SCREEN-03's
sticky "your position" footer needs this without a second round-trip). No
`{leagueId}` route needed yet (custom leagues remain Tier 1/T-109) — this
paginates the existing global-only endpoint as it stands today.
*Accept:* REQ607-named tests: response is capped at `pageSize`; a second
page via `cursor` returns the next distinct slice with no overlap or gap;
the requesting user's own row is always present even when off-page;
existing REQ401/404-named tests updated for the now-paginated response
shape; `LeaderboardScreen.tsx` updated to consume pages (load-more or
equivalent — no new SCREEN-xx design needed, this is existing SCREEN-03
behavior catching up to a real data shape). *Deps:* S-011.

**S-028 · Golf-style (lowest-wins) scoring model (REQ-203/204/205/206/401/404, ADR-0021)**
Direct product feedback, immediately after S-022 shipped: the requested
scoring direction is the opposite of what S-011/S-022 built — a rarer/more-
unique correct answer should score FEWER points, and a player's (and the
leaderboard's) goal is to MINIMIZE their total, not maximize it. Confirmed
with two follow-up questions before implementation (both answered
explicitly, not assumed): an incorrect guess scores the max penalty
(`MaxPointsPerCell`, not 0 — 0 is now the *best* score, so a wrong guess
must never tie the best possible correct one), and an unanswered cell, for
any round a player participated in, is penalized the same as a wrong guess
("unanswered equals wrong guess after each round").
*Accept:* `ScoringRules.PointsFromUniqueScore` inverted; incorrect guesses
lock at `MaxPointsPerCell`; a round participant's unattempted cells are
penalized the same way at round close; `LeaderboardService` sorts
ascending. All existing REQ-204/205/401/404-named tests updated to the new
expected values; new tests cover the unanswered-cell materialization
specifically (a participant's missed cell, a non-participant's total
exemption, and idempotency across repeated round-close calls). *Deps:*
S-022 (ADR-0020's uniqueness formula, built on top of, not reverted).
**Built as:** matches the plan, with one structural addition flagged and
accepted up front rather than discovered afterward: penalizing unanswered
cells requires knowing every cell id for a round's grid instance, which
`Core.Scoring` has no existing way to ask for — ADR-0021 documents this as
a new `IGameModule.GetCellIdsAsync(instanceId)` method (implemented in
`GridGameModule` by reading the already-generated `GridInstance.Cells`),
reached the same ADR-0003-respecting way `GenerateInstanceAsync`/
`ScoreSubmissionAsync` already are — never a direct `GridCell` read from
`Core.Scoring`. `ScoreLockingService` gained `IRoundRepository`/
`IGameModuleResolver` dependencies (both already registered in `Program.cs`
for other consumers, so no DI wiring changes needed) and a new
`MaterializeUnansweredCellsAsync` step, run before locking: for each round
participant (≥1 `Guess` row in that round — a user who never opened the
round at all is exempt, confirmed explicitly rather than assumed), it
inserts a synthetic `Guess` row (`IsCorrect = false`, `AttemptCount = 0`,
`SubmittedName = ""` — distinguishing it from a real wrong guess in case
that distinction matters later) for each cell they never attempted.
Naturally idempotent: a second `LockRoundScoresAsync` call re-derives
"which cells are still missing" from what's actually persisted, so already-
materialized rows are excluded the second time with no separate guard.
`IGuessRepository` gained `AddRangeAsync` for the batch insert.
`ScoreCalculator.CalculateTotalPoints`/`GuessRepository
.GetTotalFinalPointsByUserIdsAsync` needed no logic change — both still
just sum `FinalPoints ?? 0`; the materialization step ensures that sum sees
real rows for previously-"free" unanswered cells before either ever runs.
Backend test suite could not be executed in this environment (no dotnet SDK
available, same limitation S-018/S-022 recorded) — every changed formula
and every new/updated test's expected value was hand-derived and
cross-checked against the corrected formulas before committing; an
architecture-reviewer and code-reviewer pass both ran against the diff
given its size, same as S-022. Architecture review ran clean (the new
`IRoundRepository`/`IGameModuleResolver` dependency on `ScoreLockingService`
mirrors `GuessSubmissionService`'s existing pattern exactly; confirmed no
other `IGameModule` implementer besides `GridGameModule`/`FakeGameModule`
exists that would fail to compile). Code review hand-verified every
formula/assertion as arithmetically correct and found two real gaps, both
fixed: `REQ206_CloseRoundAsync_UserWithNoGuessesInRoundAtAll
_NeverGetsAnyMaterializedGuesses` originally seeded a round with zero
guesses at all, so it passed trivially (`MaterializeUnansweredCellsAsync`
short-circuits on an empty participant set before considering anyone,
never actually exercising the exclusion logic it claimed to test) — fixed
by adding a real participant alongside the non-participant, so the
materialized-for-participant / nothing-for-non-participant contrast is
actually proven; and a stale comment in `FakeGameModule.cs` referencing a
nonexistent `ScoreLockingServiceTests` file, corrected to
`RoundCloseServiceScoringTests`. A speculative, non-blocking
concurrent-round-close race (two simultaneous `LockRoundScoresAsync` calls
for the same round could both compute the same "missing" set) was noted
and documented as a code comment rather than fixed, since no current
caller can trigger it. `requirements-document.md`
(REQ-203/204/205/206/401/404/405 all touched — glossary, status notes,
acceptance criteria), `architecture-document.md` (COMP-04 status note, the
leaderboard data-flow diagram's sort direction, ADR table), and
`implementation-document.md` (§6a pseudocode rewritten for the
materialization step and inverted formula, `IGameModule`'s interface
listing, REQ-607's pagination pseudocode's `ORDER BY` direction) all
updated to match. New `docs/decisions/0021-golf-style-lowest-wins-scoring.md`.

**S-029 · Navigation, uniqueness copy, mobile grid fit, guess-name display, and round-closing fixes (REQ-205/206/303, ADR-0022)**
Five separate pieces of direct product feedback from actually playing the
deployed app on a phone, bundled into one session per this repo's
precedent for a small batch of related polish/bugfixes (S-022/023/024):
(1) the header nav wrapped onto a second line on a narrow phone because
"Games"/"Grid"/"Leaderboard"/"Log out" were all separate buttons, when
"Games" and "Grid" already duplicated the existing game-selection landing
page (S-021); (2) "X% unique" read as backwards once paired with
ADR-0021's golf-style points (higher uniqueness = fewer points); (3) a
Tier 0 3×3 grid still needed horizontal scrolling on an ordinary phone,
caused by header label text width, not the touch-target floor; (4) a
guessed name showed exactly as typed (wrong casing for a correct guess,
and shown at all for a wrong one, which isn't useful information); (5) a
completed grid's points never reached the leaderboard in the deployed
environment — round-close had a real production trigger gap.
*Accept:* nav reduced to "Leaderboard"/"Log out" with the "xG Arcade" title
itself routing to game-select; `CellState.tsx` shows "N% of others guessed
this too" instead of "X% unique" (same underlying number, N = 1 -
uniqueScore); a Tier 0 3×3 grid fits a common phone viewport without
horizontal scroll; a correct guess shows `Player.FullName`, an incorrect
guess shows no name; `GridScreen` shows a live "~N pts estimated" running
total; `generate-round.yml`'s cron actually locks a round's score at close,
verified by new `RoundGenerationServiceTests` cases. *Deps:* S-011 (scoring/
leaderboard), S-018 (`LivePoints`), S-021 (game-selection landing page),
S-028 (golf-style scoring, for the copy fix's framing).
**Built as:** matches the plan. Backend: `IPlayerStoreRepository` gained
`GetPlayersByIdsAsync` (bulk); `GuessSubmissionResult`/`SubmitGuessResponse`/
`CurrentRoundGuessResponse` all gained `ResolvedPlayerName` (null unless
`IsCorrect`), resolved via `GuessSubmissionService`/`RoundEndpoints` calling
`IPlayerStoreRepository` directly — a plain by-ID lookup, not a new
matching path, so boundary rule 5 (autocomplete/correctness separation) is
unaffected. `IRoundRepository` gained `GetPreviousByGameKeyAsync`;
`RoundGenerationService` now takes `IRoundCloseService` and closes a
round's *predecessor* (never `latest` itself — see ADR-0022 for why
"latest" is structurally the wrong round to check) before deciding whether
to generate a successor, so `generate-round.yml`'s existing cron is now
also Tier 0's real round-closing trigger, not just the non-Production
`force-close-round` test-data endpoint. New
`docs/decisions/0022-round-closing-runs-inside-generation-job.md`; trade-
off recorded there, not fixed: any rounds already ended-but-never-closed
before this shipped need one extra cron cycle each to catch up, or a manual
`force-close-round` call. Frontend: `App.tsx`'s header nav simplified (the
title is now a button when authenticated); `CellState.tsx`'s
`formatOthersGuessedPercent` replaces `formatPercent`, and its two
incorrect-guess states no longer pass a name to `Row` (now optional) at
all; `GridScreen.tsx` replaced its `knownPlayerNames`-by-value map with a
`submittedThisSessionCellIds` set (S-020's shake cue only ever needed the
*session* signal, not a name — the name now comes straight from
`resolvedPlayerName`) and added a client-side-summed live total; `Grid.css`
gained a `max-width: 480px` media query wrapping header label text instead
of forcing it onto one uncapped-width line. Backend test suite could not
be executed in this environment (no `dotnet` SDK available, same
limitation prior stories recorded) — new/changed logic was hand-traced
against concrete round-chain timelines before committing, particularly
`RoundGenerationService`'s predecessor-closing branch (worked through by
hand: does "latest" ever point at the round that actually needs closing?
No — it's always one step ahead of it, which is exactly why
`GetPreviousByGameKeyAsync` exists rather than checking `latest.EndTime`
directly). Frontend suite run for real this time (73/73 green,
`npm run test`), `tsc -b` and `npm run lint` (`oxlint`) both clean —
`CellState.test.tsx`'s uniqueness-copy assertions and
`GridScreen.test.tsx`'s two guess-submission tests needed updating to match
the new wording/name-display behavior (an incorrect guess's mocked POST
response has no name to assert on anymore, so those tests now wait on the
attempt-count text landing instead). **Review pass (second commit):**
independent architecture-reviewer, code-reviewer, test-writer,
ui-implementer, and requirements-writer passes found the diff structurally
clean (no boundary violations, no ad-hoc design tokens) but fixed a real
REQ-206 contradiction in `requirements-document.md`, moved an inline
S-029 tag into a proper REQ-303 acceptance-criterion bullet, and closed two
coverage gaps — a missing test for `GridScreen`'s new live total, and a
missing idempotency test for `RoundGenerationService`'s predecessor-closing
call on a repeated (retried) invocation. Final frontend suite: **75/75
green**, `tsc -b`/`npm run lint` still clean. **CI fix (third commit):**
`ci.yml`'s real Playwright run against a live backend (not reachable from
this sandbox — no `dotnet` SDK, see prior stories' same limitation) caught
a real regression neither the frontend unit suite nor either review pass
had: `frontend/tests/e2e/play-grid.spec.ts` had two pre-existing assertions
(`REQ-701/303/201/203/210` and `REQ-210` test cases) that expected an
incorrect guess's raw as-typed text to remain visible in the cell — exactly
the behavior this story's own name-display fix intentionally removed.
Fixed by flipping both to `.not.toBeVisible()`, proving the new behavior
instead of the old one; the correct-guess assertion (`cell.getByText(seed
.correctPlayerName)`) needed no change since `resolvedPlayerName` and the
seed's exact-cased `correctPlayerName` are the same string. No product code
changed, test-only fix.

## Tier 1 backlog (unordered — each waits for its trigger in `MVP-SCOPE.md`)

T-101 API-Football fallback + full waterfall (ADR-0011, `ExternalApiUsage`) ·
~~T-102 guess-time live verification~~ (built, S-011 follow-up/ADR-0018) ·
~~T-103 autocomplete + `PlayerNameIndex`~~ (pulled forward, see S-032) ·
T-104 disambiguation UI (REQ-209) ·
~~T-105 Trophy category~~ (pulled forward as individual-awards-only v1, see
S-031 — automated ID resolution for team-competition trophies is T-105's
unclaimed remainder) ·
T-106 dev/prod split + sync (ADR-0006/0009, REQ-801-804) · T-107 backups +
alerting (REQ-901/902 — **bright line: before any non-self user**) ·
T-108 email confirmation + Resend (REQ-701-705) · T-109 custom leagues
(REQ-402-404) · T-110 legal docs finalized (**bright line: before public
launch**).
