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
**Built as:** `DELETE /auth/account` (`AuthController.DeleteAccount`,
`[Authorize]`), confirmation-gated by re-verifying the caller's current
password against Supabase Auth (`SignInWithPasswordAsync`, same call
`Login` uses) rather than a bare confirmation flag — a 401 on a wrong
password, before anything is touched. The reusable anonymize/delete logic
is new `IAccountDeletionService`/`AccountDeletionService`
(`XGArcade.Core.Auth`), deliberately identified by local `User.Id` (not a
JWT or password) so S-026's admin-triggered deletion can call the same path
rather than a second implementation, per this story's own watch-out. Order:
anonymize `Guess` rows (`IGuessRepository.AnonymizeByUserIdAsync`) → remove
`LeagueMembership` rows (new `ILeagueRepository.RemoveMembershipsByUserIdAsync`
— explicit, not left to a DB cascade, since this codebase's tests run
against EF Core's InMemory provider which doesn't enforce real Postgres FK
cascades) → delete the local `User` row (new `IUserRepository.DeleteAsync`)
→ delete the Supabase Auth identity last (new
`ISupabaseAuthClient.DeleteUserAsync`). `NotificationPreference` deletion is
a no-op: that table doesn't exist yet in Tier 0 (Resend/notification
preferences are Tier 1, `MVP-SCOPE.md`). Deleting the Supabase identity
needed a new, genuinely privileged secret (`Supabase:ServiceRoleKey` —
Supabase's Admin API rejects the existing anon key) — new
`docs/decisions/0026-service-role-key-for-account-deletion.md` covers why,
threaded through `infra/bicep`, `deploy.yml`, `infra/README.md`, `SETUP.md`,
and `MVP-SCOPE.md`'s precondition list in the same change, same precedent
ADR-0013 set. All new repository writes (`AnonymizeByUserIdAsync`,
`RemoveMembershipsByUserIdAsync`, `DeleteAsync`) go through the EF Core
change tracker (load-then-`SaveChangesAsync`), not `ExecuteUpdateAsync`/
`ExecuteDeleteAsync`, for the same InMemory-provider-compatibility reason.

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
**Built as:** matches the plan, plus one testability seam and one
consolidation done during code review. `GridGameModule.GenerateInstanceAsync`
gained a new `SelectPairing` step: countries/clubs are read into a common
`CategoryCandidate(Name, WikidataQid)` shape, then `SelectPairing` decides
Country×Club vs. Club×Club per instance — a coin flip
(`GridGameModule`'s optional `Random? random` constructor param, defaulting
to `Random.Shared`, added purely so tests can pin the outcome without DI
needing to register a `Random`) whenever the seeded reference data can
support both (Club×Club needs `2 × Size` distinct clubs, since REQ-102 bars
a value on both axes), else a deterministic fallback to whichever single
pairing is feasible; both infeasible still throws `GridGenerationException`,
same as before this story. `PickColumnHeadersAsync` was generalized to
`PickHeadersAsync` (works over either pairing, not just Country rows ×
Club columns) and `RefreshCellFromLiveLookupAsync` (REQ-211) now resolves
a cell's row/column values back into `CategoryCandidate`s
(`ResolveCandidateAsync`) and dispatches through a new shared
`LookupLiveMatchesAsync` helper — also used by generation-time
`GetMatchCountAsync` — rather than each call site independently deciding
which `IWikidataLookupService` method a pairing maps to; a code-reviewer
pass caught the first-draft version duplicating that dispatch logic across
both call sites and it was collapsed into the one helper before merge.
`IWikidataClient.QueryClubClubIntersectionAsync` and
`IWikidataLookupService.LookupAndPersistClubClubAsync` were added alongside
the existing Country×Club methods, sharing `WikidataClient`'s underlying
SPARQL-running logic (its warning log now names the query kind
alongside the two QIDs, restoring debuggability lost when that logic was
shared). `CategoryPairingRules.IsAllowedPairing` itself needed no code
change — Club×Club was already permitted, only Country×Country is banned.
Full REQ107/REQ211-named test coverage added in `GridGameModuleTests.cs`
plus new DataSync-level coverage for both new Wikidata methods in
`WikidataClientTests.cs`/`WikidataLookupServiceTests.cs`, including the
random-coin-flip branch specifically
(`REQ107_GenerateInstanceAsync_BothPairingsFeasible_CoinFlipsBetweenCountryClubAndClubClub`).
`docs/architecture-document.md` §6.1/6.2, `docs/requirements-document.md`
(REQ-107/REQ-211 status notes), and `docs/implementation-document.md`
(GridCell/grid-generation/guess-scoring pseudocode notes) updated to
describe per-instance pairing selection instead of a fixed Country-rows/
Club-columns assumption.

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

**Built as (2026-07-17, backend only — a frontend agent is wiring the UI
against this same contract in parallel):** `PlayerNameIndex` entity/table
(`PlayerNameIndexEntries`, keyed by `PlayerId`, `HasIndex(NormalizedName)`)
plus `IPlayerNameIndexRepository`/`PlayerNameIndexRepository`
(`SearchByPrefixAsync`, `UpsertManyAsync`) in `XGArcade.Data` — a
deliberately separate interface from `IPlayerStoreRepository`/COMP-06, never
merged, per ADR-0007/boundary rule 5. `GET /players/autocomplete?query=&limit=`
(`XGArcade.Api.Players.PlayerAutocompleteEndpoints`, bearer-token
authenticated, response `{ playerId, name, birthYear?, nationality? }[]`):
a query under 2 characters (trimmed) returns `[]` without querying the
repository; `limit` defaults to 10, clamped server-side to 25 regardless of
what's requested. `WikidataClient.QueryPlayerPoolPageAsync` is the new bulk,
paginated (5,000 rows/page, loop until an empty page) `P106`=`Q937857`
query, same male-only/born-1939-or-later filter as the intersection
queries, deliberately no `P54`. `PlayerNameIndexImporter` (the
`import-player-name-index` CLI verb, ADR-0024, `import-player-name-index.yml`
workflow_dispatch-only) drives the page loop and upserts.

Two deviations from how this story was originally scoped, both forced by
the existing project-reference graph rather than a judgment call:
- `PlayerNameIndexImporter` lives in `XGArcade.DataSync.Wikidata`
  (alongside `WikidataLookupService`), not `XGArcade.Data/Seeding` alongside
  `ReferenceDataSeeder`/`StaleClubAttributeCleaner` — `XGArcade.Data` has no
  project reference to `XGArcade.DataSync` (only the reverse), so a class
  needing both `IWikidataClient` and `IPlayerNameIndexRepository` cannot
  live in `XGArcade.Data` without a circular project reference, which the
  build simply refuses.
- `PlayerNameIndex` has no `WikidataQid` column (matching the entity sketch
  exactly), so `PlayerNameIndexImporter` derives `PlayerId` as a
  deterministic hash of the QID (MD5's 16 bytes mapped onto a `Guid`) rather
  than a fresh `Guid.NewGuid()` per run — otherwise every re-import would
  duplicate every player's row instead of correcting it in place. Flagged
  for `architecture-reviewer`/`quality-architect` review as a judgment call
  made under ambiguity in the entity spec, not a pre-approved design.

Backend test suite run in full this session (`dotnet` SDK installed fresh
in this sandbox via `apt-get install dotnet-sdk-10.0`, per NOTES.md's
documented fix): 361/361 passed across all five backend test projects (up
from 328/328 pre-S-032; +33 new tests: `PlayerNameIndexRepositoryTests`,
`WikidataClientTests`' new `QueryPlayerPoolPageAsync` coverage,
`PlayerNameIndexImporterTests`, `PlayerAutocompleteEndpointTests`). A real
EF Core migration (`AddPlayerNameIndex`) was generated via `dotnet ef
migrations add`, not hand-written. New Wikidata QIDs: none — this story
reuses `Q937857`/`Q6581097` (already in use elsewhere in `WikidataClient`),
no new QID introduced.

**Bug follow-up (2026-07-18): the pagination strategy above was wrong in
production — replaced with birth-year slicing + fail-loud.** Every real
run of `import-player-name-index.yml` upserted 0 rows and exited 0: the
paged query's `ORDER BY ?player` over the entire unfiltered pool forced
WDQS to sort hundreds of thousands of items per page, hitting WDQS's hard
~60s *server-side* timeout on every page (PR #77's 60s client-timeout bump
couldn't help — the server cap binds first), and the swallow-to-`[]`
client contract made the importer read the first timed-out page as
end-of-data. The S-032 quality review's "silent truncation ambiguity"
finding turned out to be the 100% case. Fix:
`WikidataClient.QueryPlayerPoolBirthYearAsync` replaces
`QueryPlayerPoolPageAsync` — one bounded one-year `P569`-window query per
birth year (1939 → current year, no `ORDER BY`/`LIMIT`/`OFFSET`/subquery),
throwing `WikidataQueryException` on failure so an empty year is
distinguishable from a failed query; `PlayerNameIndexImporter` iterates
the years, retries a failed slice (3 attempts, backoff), finishes the
remaining years, then throws (red workflow) if any slice failed — the
"partial import is an accepted trade-off" paragraph is reversed. `P18`
photo fetching and the `PhotoUrl` column were dropped
(`RemovePlayerNameIndexPhotoUrl` migration) — the autocomplete contract
never exposed a photo. The intersection queries' never-throw contract and
the autocomplete endpoint/repository are untouched. Backend suite after
the change: 365/365 across all five test projects. See
`implementation-document.md` §6a and NOTES.md 2026-07-18; recorded as a
bug fix within COMP-07's existing responsibility, no ADR (same precedent
as S-042's truthy-P54 fix).

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
**Built as (2026-07-14):** reported directly by a player on the deployed
app (screenshot: a locked-incorrect Barcelona × Marseille cell showing no
point value, and the header's running total reading "~0 pts estimated"
despite the wrong guess) — the exact gap this story already described,
finally implemented, plus one connected bug this story's own scope didn't
originally cover. Added `frontend/src/lib/scoringRules.ts` exporting
`MAX_POINTS_PER_CELL = 100`, mirroring `ScoringRules.MaxPointsPerCell` the
same way `guessRules.ts`'s `MAX_ATTEMPTS_PER_CELL` already mirrors its
backend counterpart — display only, never enforcement. Also fixed, same
root cause: `GridScreen.tsx`'s REQ-206 running total only ever summed
correct guesses' `LivePoints`, silently excluding locked-incorrect cells
entirely, so a wrong guess looked like it contributed nothing (reading as
the *best* possible score under golf rules) instead of the guaranteed
`MaxPointsPerCell` worst case — now included in the same sum.
**Simplified further, same feedback round:** the first version rendered
"no attempts left · 100 pts"; direct follow-up feedback judged the
qualifier redundant once the points value itself said "this cell is
done" — dropped in favor of `CellState.tsx`'s state-3 branch matching a
correct cell's own minimal "✕/✓ + points" structure exactly (just
"100 pts"). State 4's incorrect outcome, previously left alone, was
brought in line the same way instead of staying inconsistent once both
states used the same frontend-known constant rather than needing a
`FinalPoints` value from the API (still no live path to exercise it,
S-011 scope gap — but nothing stops the styling from matching regardless).
The same feedback also asked for REQ-213's explainer (SCREEN-06) to state
the attempt count, that a wrong guess and an unanswered cell lock at the
same maximum score, and the player-pool restriction (REQ-112/ADR-0025,
male footballers born 1939+) — none of which were previously documented
anywhere player-facing; added as three more paragraphs, see REQ-213.
Tests: CellState/GridScreen/ScoringExplainer Vitest suites updated/
extended (88 frontend tests pass, `tsc -b --noEmit` clean), one E2E
assertion updated by hand (no live backend in this environment to run it
against). Visually verified against the exact reported scenario at a real
narrow viewport, both the simplified cell and the expanded explainer.

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
**Built as:** matches the plan closely. Backend: `cursor`/`pageSize` query
params (default `pageSize` 50, max 100; `cursor` defaults to 0, last-seen
global rank), a negative `cursor` or an out-of-range `pageSize` → 400,
an out-of-range-but-valid `cursor` (stale, from a since-shrunk league) →
empty page rather than an error. `LeaderboardEntry`/`LeaderboardRowResponse`
gained an explicit `Rank` field (1-based, global, not page-local) since the
frontend previously derived rank from array index, which breaks once a
page can start mid-list. `LeaderboardService` still composes the full
membership list and ranks/slices in memory rather than pushing `ORDER
BY`/`LIMIT` to the database — an accepted MVP-scale tradeoff (bounds the
response, not the query), matching `implementation-document.md` §6's own
note that the cursor-shaped contract, not the storage strategy, is what
must not need to change later. Tests: `LeaderboardServiceTests.cs`
(Core-level, REQ401/404 updated + new REQ607 cases) and new
`LeaderboardEndpointTests.cs` (API-level: query-param validation 400s,
boundary values `pageSize`=1/100, response-shape/cursor round-trip).
Frontend: `LeaderboardScreen.tsx` gained a "Load more" button appending
subsequent pages and a pinned "you" footer for when the requesting user's
row is off the currently-loaded page(s); the existing 15s poll now
refreshes only page 1. One bug found and fixed during the quality gate,
not part of the original spec: a player whose rank crosses the
page-1/page-2 boundary between poll ticks could appear twice (once in the
fresh page-1 response, once still in the stale trailing rows from an
earlier "Load more") — fixed by de-duplicating the stale trailing rows
against the fresh page-1 response's user IDs before merging. Tests:
`LeaderboardScreen.test.tsx` extended (load-more behavior, poll-only-
refreshes-page-1, the you-footer, and the page-1-reorder dedup regression).
Full backend suite run (`dotnet test`, SDK installed mid-session per
`NOTES.md`'s documented fix — the first sandbox session to actually verify
this, not just hand-trace it): 328/328 passed across all five backend test
projects, no regressions. Frontend suite (`npm run test`, `tsc -b`, lint)
also passes clean (96/96 tests). Architecture-reviewer pass: no boundary/
data-flow change, no ADR needed — the pagination shape was already fully
pre-specified in `implementation-document.md` §6 before this story.

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

**S-035 · Bound grid generation's wall-clock time (REQ-101, ADR-0023)**
Incident-driven, not pre-planned: three consecutive manual `generate-round.yml`
dispatches on 2026-07-12/13 each failed differently (two opaque HTTP 500s,
fixed separately by REQ-301's problem-details catch-all; an unrelated
deploy-race 503; and a genuine HTTP 504 after Azure's ingress killed the
connection at 240s of real elapsed time). The Container App's own log
showed the actual cause of the last one: `PickHeadersAsync` had chained
enough live Wikidata lookups to run over 4 minutes, since
`GridGenerationOptions.MaxAttempts` (500) never meaningfully bounds
wall-clock time — the reference-data pool is far smaller than 500, so
`MaxAttempts` alone can't fire before external infrastructure does. Add a
`MaxDuration` wall-clock deadline, checked alongside the existing
pool-exhausted/`MaxAttempts` checks, so generation always resolves —
success or a clean, logged `GridGenerationException` — well under any
known infrastructure timeout.
*Accept:* REQ101-named test confirms a `GridGenerationException` naming
the configured `MaxDuration` when it's exceeded, deterministically (a
`ManualTimeProvider` test double advances a fake clock from within the
fake Wikidata lookup service's own call hook, no real waiting); existing
`MaxAttempts`-exhaustion test still passes unchanged; `GridGenerationOptions`
default-values test extended to cover the new field. *Deps:* S-007, S-030
(the fix has to land against S-030's generalized `PickHeadersAsync`, not
the pre-S-030 `PickColumnHeadersAsync` it replaced).
**Built as:** matches the plan, with one significant scope cut found
*during* implementation, not before it: a bounded-concurrency candidate
search (`Task.WhenAll` over a small batch of candidates instead of one at a
time) was the other half of the original plan, meant to actually raise the
odds of a cold-cache generation succeeding, not just fail it faster.
Implemented, then reverted before commit on realizing
`PlayerStoreRepository`/`CategoryValueRepository`/`WikidataLookupService`
all share one request-scoped `XGArcadeDbContext` — concurrent use of a
single `DbContext` instance isn't safe in EF Core, and the bug would have
passed every test against the InMemory provider while throwing against
real Npgsql in production. Reverted to the safe, deadline-only version;
the concurrency piece is recorded as ADR-0023's explicit follow-up
(needs `IDbContextFactory`-based per-call contexts, plus a concurrency
limit chosen against ADR-0011's Wikidata query-time-throttle budget, not
picked arbitrarily), not silently dropped. `PickHeadersAsync` gained a
`_timeProvider`-read deadline check and `LogInformation`/`LogDebug`/
`LogWarning` calls (candidates tried/accepted/rejected, abort reason) via
the already-injected `ILogger<GridGameModule>` — no new logging boundary,
same component already owned this logging. `GridGameModule` gained an
optional `TimeProvider? timeProvider = null` constructor parameter
(defaults to `TimeProvider.System`, same optional-param idiom as `Random?
random` from S-030), resolved automatically via DI the same way
`RoundGenerationService`'s `TimeProvider` already is (`Program.cs`'s
existing `AddSingleton(TimeProvider.System)`). New
`ManualTimeProvider` test double (`XGArcade.Games.XGGrid.Tests`) and a new
`onCalled` hook on the existing `FakeWikidataLookupService`, so a test can
advance simulated time from inside a simulated live-lookup call without
any real waiting.
`docs/requirements-document.md` (REQ-101 acceptance criteria and status
note), `docs/implementation-document.md` (§6a's pseudocode-vs-actual note),
and `docs/decisions/0023-grid-generation-wall-clock-deadline.md` (new)
updated to match. No `architecture-document.md` change — this stays
entirely within COMP-05's existing responsibility, no boundary moved.

**S-036 · Proactive player-attribute cache warming + wider reference pool (REQ-110)**
Direct continuation of S-035, same incident: `MaxDuration` made a failed
generation attempt fail fast and cleanly instead of hanging, but a fast
`GridGenerationException: "Ran out of candidates before completing the
grid."` on the very next real dispatch (2026-07-13) showed the deeper
problem `MaxDuration` alone was never going to fix — `MinValidAnswers=5`
(S-014) combined with only 15 reference clubs means a lot of real
country/club pairs, especially smaller-market countries, genuinely don't
have 5+ shared historical players. No amount of retrying fixes an
unlucky-but-real data gap. This is exactly the risk S-011's backlog entry
predicted and deliberately deferred ("a scheduled/proactive cache
pre-warming job ... revisit if S-014's threshold bump makes grid
generation struggle in practice").
Two parts, both requested together: (1) a proactive cache-warming job that
checks every reference Country×Club and Club×Club pair ahead of time
instead of only ever discovering a pair's real match count as a side
effect of a live generation attempt; (2) a materially wider reference pool
(more countries, more clubs) so more row-header picks have a realistic
chance of clearing `MinValidAnswers` at all.
*Accept:* REQ110-named tests confirm every pair gets checked, an
already-valid pair is skipped (not re-queried), and a below-threshold pair
is re-queried (documented as a known gap, not a bug — see REQ-110's own
acceptance criteria for why). *Deps:* S-007, S-030, S-035.
**Built as:** matches the plan, with the execution-model choice being the
one real design decision made along the way. `PlayerCacheWarmingService`
(`XGArcade.Games.XGGrid`) does the actual iteration — deliberately
sequential (same `XGArcadeDbContext`-sharing constraint `PickHeadersAsync`
already has to respect, see S-035's own note) and deliberately **not**
exposed as an HTTP endpoint. An endpoint would hit the identical ~240s
ingress wall S-035 just fixed round generation against, since this job can
run for a genuinely long time (every reference pair, each up to a real
~15-27s live Wikidata call) — and a fire-and-forget background task inside
the deployed app isn't safe either, since this Container App scales to
zero (`minReplicas: 0`, NOTES.md 2026-07-09) and a scale-down mid-run would
silently lose all progress with nothing persisted to resume from. Instead
it's a second `dotnet run --` CLI verb in `Program.cs` (`warm-player-cache`,
built the same way the existing `migrate-and-seed` verb is: constructs its
own `XGArcadeDbContext`/repositories/`WikidataClient` directly rather than
spinning up the full DI container), triggered manually via a new
`warm-player-cache.yml` workflow (`workflow_dispatch` only, no recurring
schedule — this is meant to run after a reference-data change, not on a
fixed cadence). Idempotent: skips any pair already at or above
`MinValidAnswers` (fast, cache-only), but does **not** skip a pair cached
*below* that threshold, since there's no persisted signal distinguishing
"never checked" from "checked, genuinely low" — accepted as a known first-pass
gap (documented in both REQ-110 and the service's own doc comment), not
attempted to fix with a new tracking table this round.
`ReferenceDataSeeder.cs` widened from 20/15 to 45/21 countries/clubs — the
25 added countries and 6 added clubs use well-known, stable Wikidata QIDs
from training knowledge, **not independently verified against a live
Wikidata endpoint** (this sandbox's network policy blocks wikidata.org,
same limitation NOTES.md already records for Supabase/JWKS verification).
A wrong QID here is self-limiting, not dangerous — `WikidataClient`'s
SPARQL queries against a nonexistent/mismatched QID just return zero
bindings, indistinguishable from a real "no shared players" result — and
`PlayerCacheWarmingService`'s own run will surface any entry that
consistently resolves zero matches against everything it's tried against,
which is the practical way to catch a bad one. Flagged for spot-checking,
not blocking on it given the graceful-failure property.
**Review pass:** an independent architecture-reviewer pass agreed
`PlayerCacheWarmingService` living in COMP-05 is fine (no boundary
change — reads reference data through `ICategoryValueRepository` and
persists through `IWikidataLookupService`/`IPlayerStoreRepository`
exactly like generation already does), but flagged two real gaps the
original pass had missed:
1. `Program.cs`'s CLI verb hand-duplicated the real
   `AddHttpClient<IWikidataClient, WikidataClient>` registration's
   `BaseAddress`/`User-Agent`, flagged only by a "kept in sync manually"
   comment — a bug-prone pattern (the same risk already existed for
   `migrate-and-seed`'s duplicated `DbContextOptionsBuilder`, but this
   extended it to a second, larger surface). Fixed by extracting a shared
   `ConfigureWikidataHttpClient` local function both the DI registration
   and the CLI verb now call — the two can no longer silently drift.
2. The execution-model decision (CLI verb, not an endpoint or background
   task) *is* architecturally significant per this repo's own ADR bar,
   closely related to ADR-0023, and was only recorded as scattered prose
   (this entry, the service's doc comment, REQ-110's status note) rather
   than an indexed ADR. The "judged sufficient without one" call in the
   original draft of this entry was wrong — added
   `docs/decisions/0024-cache-warming-runs-as-a-cli-verb.md`, plus a
   one-line COMP-05 status note and both this ADR and the
   previously-unlisted ADR-0023 added to `architecture-document.md`'s §10
   table (ADR-0023 itself already existed from S-035 but was never added
   to that table — a pre-existing gap, fixed here opportunistically).

**S-037 · Fix wrong club QIDs from S-036; wider club pool; stale-cache recovery tool (REQ-109)**
Direct follow-up requested after S-036 shipped: the user manually checked
S-036's new club QIDs against live Wikidata pages (this sandbox can't —
network policy blocks `wikidata.org`) and found 4 of the 6 were wrong —
Napoli, AS Roma, Sevilla, Porto. Each wrong QID happened to be some
*other* real Wikidata entity, so `WikidataClient`'s SPARQL queries against
them didn't error or return empty (S-036's own doc comment predicted
"self-limiting, not dangerous... just return zero bindings" — wrong for
these 4), they silently returned real-but-wrong player data persisted
under the intended club's name. See NOTES.md's 2026-07-13 entry for the
full incident writeup.
*Accept:* the 4 QIDs corrected in `ReferenceDataSeeder.cs`; 11 further
clubs added with QIDs the user verified directly, not guessed;
`ReferenceDataSeeder.SeedAsync` corrects an existing row's `WikidataQid`
in place (not just skips duplicates by name — needed or the QID fix
would silently do nothing against an already-seeded database); a new
tool purges whatever got persisted under a club's name while its QID was
wrong, and a REQ109-named regression test proves it: seed a club with
data shaped like it came from a wrong QID, confirm cleaning it leaves
zero cached matches, not a lingering silent match against the unrelated
entity's data. *Deps:* S-005, S-036.
**Built as:** matches the plan. `ReferenceDataSeeder.SeedAsync` reworked
from "skip if a row with this name exists" to "look up by name, update
`WikidataQid` in place if found, else insert" — same by-`Name` idempotency
check, now correcting instead of only preventing duplicates. New
`StaleClubAttributeCleaner` (`XGArcade.Data.Seeding`, same static-class-
plus-`XGArcadeDbContext` shape as `PlayerNormalizedFullNameBackfiller`/
`UserDisplayNameBackfiller`/`LeagueMembershipBackfiller`) deletes every
`PlayerAttribute`/`PlayerData` row for a given set of club names.
Deliberately **not** wired into `migrate-and-seed`'s automatic,
safe-to-run-forever chain the other backfillers share — unlike those,
there's no way to tell a wrong-QID-sourced row from a correct one after
the fact (both look like an ordinary `PlayerAttribute(club="Napoli")`
row), so leaving this running on every deploy would eventually delete
freshly-fetched *correct* data too. Instead it's a fourth `dotnet run --`
CLI verb (`clean-stale-club-attributes "<comma-separated names>"` — one
argument, comma-separated, not one shell arg per name, so a name
containing a space like "AS Roma" survives a GitHub Actions
`workflow_dispatch` text input without any shell quoting risk), triggered
manually via a new `clean-stale-club-attributes.yml` workflow, run once
per correction, always *before* the next `warm-player-cache` run (running
it after would wipe the fresh correct data too, same reasoning). Reference
pool: 21→32 clubs (RB Leipzig, Bayer Leverkusen, Marseille, Lyon, Monaco,
Lille, Lazio, Valencia, Real Sociedad, Newcastle United, West Ham United).
`docs/architecture-document.md` was checked and found not needing a
change — this stays within COMP-06 (Data.PlayerStore)'s existing
responsibility, no boundary change. `docs/requirements-document.md` gained
**REQ-111** (added by a `requirements-writer` review pass, after a
`code-reviewer` pass flagged that `StaleClubAttributeCleaner`'s
cache-purge/recovery behavior was being filed under REQ-109 by association
rather than covered by its own requirement) — REQ-109's "resolved once,
verified" language covers the `ReferenceDataSeeder.SeedAsync` in-place
correction itself, but not purging the derived `PlayerAttribute`/
`PlayerData` cache once a QID is corrected, which is what REQ-111 now
covers. `docs/implementation-document.md` §6 also gained a paragraph on
this CLI-verb pattern (`doc-sync` review pass).

**S-038 · Restrict player pool to male, born in 1939 or later (REQ-112, ADR-0025)**
User-identified scope issue: the player pool sourced from Wikidata had no
gender or era restriction, so a grid could surface a female footballer or
an unfamiliar early-20th-century name a player has no realistic way to
reason their way to. *Accept:* both `WikidataClient` SPARQL query builders
always include `wdt:P21 wd:Q6581097` (male) and `wdt:P569 ?dateOfBirth`
with a `FILTER` requiring it on/after a fixed `1939-01-01T00:00:00Z`
cutoff; a new `purge-player-pool "delete all player data"` CLI verb +
workflow (gated behind an exact confirmation phrase, same extra-friction
pattern `promote-dev-to-prod.sh` already uses) deletes the entire cached
player pool (`Player`, cascading through `PlayerData`/`PlayerOverride`/
`PlayerAttribute`/`PlayerAlias`) since neither property was ever recorded
on already-cached rows and can't be selectively corrected the way S-037's
per-club fix could; reference tables and account/game-history tables
(`User`/`League`/`Round`/`GridInstance`/`GridCell`/`Guess`) are untouched.
*Deps:* S-006 (`WikidataClient`), S-036/S-037 (the CLI-verb pattern this
reuses).
**Built as:** first implemented with a rolling `TimeProvider`-driven
"latest 100 years" cutoff, then corrected to a fixed `1939-01-01` date per
the user's follow-up — see ADR-0025 for the full reasoning (fixed vs.
rolling cutoff, date-of-birth vs. career-span filtering, full-purge vs.
selective-fix, and the confirmation-phrase safety gate). The fixed date
removed the need for any `TimeProvider`/clock dependency on
`WikidataClient` at all. New tests in `WikidataClientTests.cs` assert the
sent SPARQL query contains the male triple and a date-of-birth cutoff of
exactly `1939-01-01T00:00:00Z`, for both query builders. Operational
sequence after merge: (1) deploy ships the new filters, (2) trigger
`purge-player-pool.yml` once with confirmation phrase `delete all player
data`, (3) trigger `warm-player-cache.yml` to repopulate under the new
filters. `docs/requirements-document.md` gained **REQ-112**;
`docs/architecture-document.md` needs no change (no boundary/component
change — same COMP-06/COMP-07 responsibility, just a stricter query);
`docs/implementation-document.md` §6a's sample SPARQL query and rules list
updated, plus a new §6 paragraph on the `purge-player-pool` CLI verb.

**S-039 · Account/settings page — delete-account UI only (REQ-710)**
Scope gap found while implementing S-025: `DELETE /auth/account` exists and
is fully tested, but no frontend code was ever written to call it — S-025's
own acceptance criteria was backend-only (unit + API tests), so
"self-service" account deletion currently has no way for a real player to
actually trigger it. There is also no `SCREEN-xx` for an account/settings
page anywhere in `design-document.md`. Deliberately scoped narrow: this
story is the delete-account flow only, not a general profile/settings page
(no display-name editing, no future notification-preference UI) — avoids
building speculative UI ahead of an actual need, same discipline
`MVP-SCOPE.md` applies elsewhere. No separate SCREEN-05 design pass first;
define the layout (a simple settings entry point + password-confirmation
dialog + an explicit irreversibility warning, matching what
`AuthController.DeleteAccount` already requires) inline within this story,
using only tokens already defined in `design-document.md` §2, and add the
resulting mock to that doc as part of this same change — same pattern
S-016/S-017/S-018 used for additions too small to warrant a dedicated
design session.
*Accept:* REQ710-named UI test: an authenticated player can reach the
delete-account flow from the app's existing navigation, is required to
re-enter their current password (matching the API's existing
re-verification requirement — a wrong password shows an error and deletes
nothing), sees an explicit irreversible-action warning before confirming,
and is signed out and returned to the login/landing screen on success.
Wrong-password and cancel paths leave the account untouched (Vitest,
mocked fetch). *Deps:* S-025 (the endpoint this calls).
**Built as:** matches the plan, no deviations. The header's existing nav is
the "settings entry point" — a plain "Delete account" link next to
"Leaderboard"/"Log out", not a general profile/settings page (none added).
It opens `DeleteAccountScreen` (new SCREEN-05, `docs/design-document.md`
§3, added in this same change per the plan above): an explicit,
unambiguous irreversibility warning, then a current-password field
re-verified server-side exactly as `AuthController.DeleteAccount` already
enforces — a wrong password shows an inline error and deletes nothing, no
bare confirmation checkbox. On success there's no account left to show
anything else on, so the flow signs the user out and returns to the
login/landing screen, same effect `App.tsx`'s existing `handleLogout`
already produces. New `deleteAccount(accessToken, password)`
(`frontend/src/lib/api.ts`) calls `DELETE /auth/account`, returning `void`
on the 204 the endpoint sends on success; `DeleteAccountScreen`
(`frontend/src/auth/`) is styled entirely from existing §2 tokens
(`accent-red` for the warning and the destructive confirm button — both
already pass the text-contrast floor as-is, no new token needed). `App.tsx`
gained a `'delete-account'` `Screen` member; the screen's
`onAccountDeleted` and `onAuthError` props both point at the existing
`handleLogout`, since a successful deletion and an expired/invalid JWT
both resolve to the same "sign out, land on `AuthScreen`" outcome.
Distinguishing a wrong-password 401 (show inline error, keep the session)
from a JWT-invalid 401 (sign out via `onAuthError`, same as every other
authenticated screen) is done by checking `ApiError.title !== 'Incorrect
password'` — `AuthController.DeleteAccount`'s own confirmation-failure
response is the only 401 path that sets that specific title, so this needed
no new response field. REQ-710's status heading (requalified to "Partially
implemented" by this story's own scoping change, #49) is restored to
"Implemented" now that the player-facing entry point exists.

**S-040 · Collapse cell content to icon+points at rest; fix mobile header crush; polish desktop grid layout (REQ-204, SCREEN-01/01a)**
Direct product feedback from two screenshots (deployed app on a phone, and
on a wide/"desktop site" viewport) found two real problems, both traced to
actual code before scoping this story — see REQ-204's status note and
SCREEN-01's new status note in `design-document.md` for the full diagnosis.
(1) **Mobile header crush:** `Grid.css`'s `.grid-table__row-header`
`max-width: 88px` mobile cap isn't actually enforced, since the table uses
browser auto-layout — a wide cell (full player name + badge + checkmark +
"live" text) in the same row squeezes the header column far below that cap,
and `overflow-wrap: anywhere` then breaks mid-word, rendering a country
name one character per line. (2) **Desktop layout:** the grid reads as
small and stuck top-left within `.app`'s existing `max-width: 900px` cap,
with a lot of unused surrounding space — never actually art-directed past
mobile.

This story fixes the root cause behind (1), not just the symptom: redesign
SCREEN-01a states 1 and 4 (the only two states that show a player name) to
show only their checkmark/✕ + points at rest, on every screen size, not
mobile-only — extends S-019's existing tap/hover/focus toggle
(`LiveMetaDisclosure`) to also gate the name, rather than adding a second
interaction pattern. State 1 (correct, round active): at rest, show the
live dot + "live" + the live point estimate (moves from revealed-only to
always-visible); reveal shows the name alongside the existing %/round-end
text (unchanged wording, just now paired with the name). State 4 (round
closed, correct outcome): currently has **no reveal toggle at all** — add
one, reusing the same mechanism as state 1; at rest shows ✓ + `FinalPoints`
+ "final"; revealed shows the name + the existing %-breakdown text. State 2
(incorrect, one attempt remaining) already shows no name and no points,
and stays that way — it isn't locked, so no point value applies there,
today or after S-033. State 3 (incorrect, no attempts remaining) already
shows no name and, once S-033 ships, will also show points at rest. Both
states are unaffected by this story — no change needed. Shrinking typical
cell
content this way is expected to substantially fix (1) as a side effect,
but this must be verified against a real narrow viewport as part of this
story's acceptance criteria, not assumed; if header crushing still occurs,
`grid-table` needs `table-layout: fixed` (or an equivalent explicit
column-width strategy) so header `max-width`/`min-width` is actually
respected regardless of other cells' content.

Also polishes (2): spacing/cell-sizing adjustments so the existing
single-column layout doesn't look like a mobile layout simply stretched
onto a wide screen. **Explicitly out of scope:** `design-document.md`
SCREEN-01's desktop side-panel variant (grid + a "your progress" panel)
was never built and remains a known, separately-tracked gap — deferred to
its own future story, not folded into this one.

`design-document.md` SCREEN-01/01a must be updated to reflect the new
at-rest/revealed content split for states 1 and 4 *before* implementation
(per `CLAUDE.md`'s rule against undocumented UI changes) — same "design it,
then build it" discipline S-019/S-020 followed, not a follow-up cleanup.
*Accept:* REQ204-named test: state 1 at rest shows no player name, only the
live dot/"live" text and the live point estimate; tapping/hovering/
focusing reveals the name alongside the existing %/round-end text.
REQ204-named test: state 4 at rest shows no player name, only the
checkmark/`FinalPoints`/"final"; tapping reveals the name alongside the
existing %-breakdown text (new toggle behavior — state 4 has none today).
Manual/visual verification against a real narrow (≤480px) viewport: row/
column header text wraps onto readable words/phrases, never single
characters. Manual/visual verification on a wide viewport: the grid no
longer reads as cramped/stuck top-left with excess unused space around it.
*Deps:* S-019 (the toggle mechanism this extends), S-033 (state 3's
point-value fix, so every locked state is consistent about showing points
at rest).
**Built as:** matches the plan for the name-gating behavior in both states,
plus one deviation the acceptance criteria's own "verify, don't assume"
clause anticipated: shrinking cell content did **not**, on its own, fix the
mobile header crush. Root-causing past the symptom found the real bug —
`Grid.css`'s `.grid-table__row-header` `max-width: 88px` was never actually
enforced under the browser's default `table-layout: auto`, which sizes a
column from the *widest cell content anywhere in that column* (a live/
correct cell's name + badges + checkmark + "live" text), not from the
header's own `max-width`; `overflow-wrap: anywhere` then broke the
oversized header text mid-word regardless of how narrow the header's own
content was. Fixed with `table-layout: fixed` plus an explicit
`<colgroup>`/`<col>` (`Grid.tsx`, ≤480px breakpoint in `Grid.css`), which
makes the row-header column's width genuinely sourced from its own `<col>`
element rather than any cell's content — plus stacking the flag/badge above
the header text, rather than beside it (`Grid.css`), so the name gets the
full column width to wrap on rather than sharing it with the glyph. A
second, unrelated pre-existing CSS bug was found and fixed along the way,
only visible because of this story's own change: `.cell-state__reveal-toggle`
(`CellState.css`) reset `font: inherit`, a shorthand that also silently
resets `font-size` to the browser's ~16px default rather than
`.cell-state__meta`'s intended 11px/10px — harmless while the button only
ever held a dot and the word "live," but produced bad text wrapping once
state 1's live point estimate became always-visible at rest. State 1's
toggle was renamed in place (`LiveMetaDisclosure` → `useRevealDisclosure` +
`RevealToggle`, `CellState.tsx`) so state 4 could reuse the same hook/markup
rather than duplicating it. Desktop breakpoint chosen: `@media (min-width:
960px)` — widens `.app`'s `max-width` (900px → 1200px, `App.css`) and grid
cell/header sizing (44px → 64px touch targets, more padding, `Grid.css`/
`GridScreen.css`); still explicitly not the SCREEN-01 side-panel variant.
`design-document.md` SCREEN-01a's state 1/state 4 mocks were updated (0.16 →
0.17) before the component code changed, per the plan's own design-first
requirement. Tests: `CellState.test.tsx` gained the two REQ204-named tests
the acceptance criteria specified, plus two more covering edge cases found
during review (no live point estimate yet in state 1; state 4 with neither
`uniquePercent` nor `finalPoints` present) — all 88 frontend tests pass,
`tsc -b --noEmit` clean. A `code-reviewer` pass on the diff found no other
issues.

**S-041 · Drop live/final distinction from cells; click-to-reveal player; add scoring explainer (REQ-204/212/213, SCREEN-01/01a)**
Direct product feedback on S-040's result: the live/final distinction it
still preserved (a pulsing dot, the word "live," the "~N pts estimated"
qualifier, and a tap/hover/focus toggle revealing a %-breakdown +
round-end-time line) was itself judged unnecessary noise once shrunk down —
a player doesn't need any of that per cell to know their score, just the
number. Three changes, scoped together since they replace each other:

1. **Cell display, further simplified (REQ-204):** states 1 and 4 (the only
   two showing a checkmark for a correct guess) now render identically in
   structure at rest — checkmark plus a **points** value only, never a
   percent, never both, and with no dot/icon/"~"/"estimated"/"final"
   qualifier distinguishing a still-live estimate from a locked score. A
   player cannot tell from the cell alone whether the shown value could
   still change — see (3). This supersedes (not deletes — REQ-204 marks the
   old bullets `Superseded 2026-07-14`) the "always as text, never
   icon-only" live-dot rule and the S-019/S-040 tap-or-hover/focus
   disclosure of the %-breakdown/round-end text.
2. **Click/tap reveals the guessed player (REQ-212, new):** the per-cell
   disclosure toggle S-019 built and S-040 extended is gone — in its place,
   clicking/tapping anywhere on a locked+correct cell reveals the guessed
   player's name and badge dock; clicking/tapping again hides it. Click/tap
   only, on every device — no separate hover-only or focus-only peek (a
   deliberate simplification from S-019's three-way click/hover/focus
   toggle, chosen directly with the product owner). A locked+incorrect cell
   (state 2/3) is never a click target for this and still shows no name at
   all, ever (unchanged, REQ-303/S-029). Mechanically, this moves the
   reveal control from a small in-cell button (`CellState.tsx`'s own
   focusable toggle) to the whole cell, owned by `GridCell.tsx` — which
   also resolves a pre-existing awkwardness (`GridCell.tsx`'s locked branch
   rendered a non-interactive `<div role="group">` specifically to avoid
   nesting `CellState`'s own button inside a disabled one; now that
   `CellState` has no button of its own, a locked+correct cell can just be
   a real `<button>`).
3. **General scoring/live-updates explainer (REQ-213, new):** a new entry
   point in the grid screen's header, next to the round/timer indicator
   (SCREEN-01's "Round #14 ⏱ 1d 4h"), opens a general explanation covering
   what a live estimate means and that it can change before round close,
   what a locked/final value means once the round closes, and — in general
   terms, not the exact formula — that xG Arcade scores like golf overall
   and a less-commonly-guessed answer scores better (ADR-0021). This is
   where the content the old per-cell disclosure used to carry now lives,
   once, instead of repeated cell by cell. Never cell-specific — valid
   regardless of which cells the player has or hasn't attempted.

`design-document.md` SCREEN-01a's state 1/4 mocks and a new explainer mock
must be updated *before* the component code changes, per the usual
design-then-build discipline S-019/S-020/S-040 already followed.

*Accept:* REQ204-named test: state 1 and state 4 at rest render identically
in structure (checkmark + points, no live indicator, no percent).
REQ212-named tests: clicking/tapping a locked+correct cell reveals the
player name + badge dock and toggles closed on a second click/tap; keyboard
activation (Enter/Space) produces the same toggle; `aria-expanded` reflects
state; a locked+incorrect cell is not a click target and never reveals a
name. REQ213-named test: the explainer opens from the header entry point,
contains text covering all three required content points, and closing it
doesn't discard in-progress state (e.g. an open guess-input sheet).
Manual/visual verification at a narrow and wide viewport that the
simplified cells and new explainer both read cleanly, not just that tests
pass. *Deps:* S-040 (the toggle/mechanism this replaces), S-019 (ditto,
transitively).
**Built as:** matches the plan for all three changes, plus two real bugs
found and fixed along the way, neither anticipated in the acceptance
criteria. (1) Manual browser verification of REQ-212's reveal (required by
this story, not just tests passing) found a revealed player name could
collapse to zero visible width in a narrow cell: `.cell-state__name`'s
`overflow: hidden`/`text-overflow: ellipsis`/`white-space: nowrap` gives a
flex item an *automatic* minimum size of 0, and its `flex-shrink: 0`
siblings (flag, club badge, checkmark) never yield space — so once
revealed content overflowed a narrow cell's line, the entire deficit landed
on the name, rendering it invisible even though it was correct in the DOM.
Fixed by wrapping normally instead (`overflow-wrap: anywhere`, matching
`.cell-state__meta`'s existing pattern; `CellState.css`). (2) A
`code-reviewer` pass on this story's diff found `design-document.md`
SCREEN-06's entry, as first written, falsely claimed the explainer "returns
focus to the entry point on close" as something `GuessInput` already did —
neither modal actually did, at the time. Fixed by implementing real focus
management in `ScoringExplainer.tsx` (moves focus to its close button on
mount, restores the previously-focused element on unmount) and correcting
the doc to describe `GuessInput`'s actual, unchanged behavior instead of a
false comparison, plus giving the explainer's backdrop an explicit
`z-index: 20` (above `GuessInput`'s `z-index: 10`) rather than relying on
DOM order for correct stacking when both are open at once. Mechanically,
`GridCell.tsx` now owns `revealed` state and renders a locked+correct cell
as a real `<button>` (replacing the old non-interactive
`<div role="group">`), since `CellState.tsx` no longer owns a toggle of its
own to nest one inside. Tests: `CellState.test.tsx`, `GridCell.test.tsx`,
`GridScreen.test.tsx` rewritten/extended; new `ScoringExplainer.test.tsx`
added. 85/85 Vitest tests pass, `tsc -b --noEmit` clean.
`frontend/tests/e2e/play-grid.spec.ts` had two assertions updated by hand
to match the new at-rest cell content but was logic-reviewed only, not
executed (no live backend available in this environment) — a known gap,
not a passing confirmation, until it's run against a real deployment.

**S-042 · Fix truthy `wdt:P54` dropping historical clubs; all-clubs stale-cache recovery (REQ-113, REQ-111)**
Incident-driven bugfix, orchestrated as a bug rather than a planned story
(entry added retroactively, same as the S-033/S-035/S-037 precedent): a
genuinely correct guess (Sandro Tonali × AC Milan) scored incorrect. Both
`WikidataClient` intersection builders matched clubs via Wikidata's truthy
`wdt:P54` shortcut — a best-rank-only view, so a preferred-ranked *current*
club silently suppressed every normal-rank historical club, reducing "ever
played for" to "currently plays for." See NOTES.md's 2026-07-17 entry for
the full incident writeup and operator recovery order.
*Accept:* both builders match P54 via the full statement path
(`p:P54`/`ps:P54`) excluding only `wikibase:DeprecatedRank`, with two
distinct statement variables in the club-club builder; REQ113-named
query-shape tests prove the sent SPARQL for both; a recovery path exists
for the fact that every seeded club's cached data was incomplete at once
(re-warming alone can't fix partial pairs — the warming service skips
pairs already at `MinValidAnswers`). *Deps:* S-006, S-030, S-036, S-037.
**Built as:** query fix exactly as above (`WikidataClient.cs`, REQ-113 —
new requirement pinning the ever-played-for semantics that previously only
existed as an aside in REQ-109). Recovery extends S-037's existing
mechanism rather than adding a new one: `clean-stale-club-attributes`
gains an `--all-clubs` mode (`StaleClubAttributeCleaner.
CleanAllSeededClubsAsync`, REQ-111 extended) resolving every club name
from `ClubDefinition` at runtime — hand-typing ~32 names is exactly the
typo surface where one misspelled club silently stays stale. Fails loudly
on an empty `ClubDefinition` table; the named form now rejects any
`-`-prefixed token (a mistyped `--all-club` must never pass as a club name
that "removed 0 rows" successfully — guard lives in `Program.cs`'s
argument handling, no unit-test seam today, verified manually). Two
REQ113 tests in `WikidataClientTests.cs`, four REQ111 tests in
`StaleClubAttributeCleanerTests.cs`. No ADR — `architecture-reviewer` and
`quality-architect` concurred this restores already-documented semantics
(bug fix), conditional on `implementation-document.md` §6a being updated
to the statement-path query, which was done in the same pass.
`docs/architecture-document.md` checked, no change (COMP-07-internal query
shape + COMP-06-internal tooling; no boundary or data-flow change). Open
follow-up: the Tonali "Tottenham" attribution needs manual live-Wikidata
verification (genuine transfer vs. S-037-class wrong QID).

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
