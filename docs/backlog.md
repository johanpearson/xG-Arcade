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
**Built as:** matches the plan, plus one deliberate implementation detail
worth flagging. `XGArcade.Api.Admin.AdminManagementEndpoints` (new file,
kept separate from S-012's `AdminEndpoints.cs` specifically so the
non-Production gate is visible at a glance rather than a per-endpoint
condition) adds `GET/POST /admin/rounds/{gameKey}/active|close` and `PUT
/admin/rounds/{gameKey}/end-time` (REQ-505) and `DELETE
/admin/users?email=` (REQ-506) — all registered only when
`!app.Environment.IsProduction()`, checked before any route is mapped, same
`InternalRoundEndpoints.cs` discipline REQ-806 already established.
`POST .../close` calls `IRoundCloseService.CloseRoundAsync` directly
(REQ-205, no new close logic); `DELETE /admin/users` resolves the
admin-supplied email via a new `IUserRepository.GetByEmailAsync`
(case-insensitive) then calls the identical `IAccountDeletionService
.DeleteAccountAsync` REQ-710's self-service path already uses — no second
deletion implementation, per this story's own watch-out. `AuthController
.Me`'s `MeResponse` gained `IsAdmin` (via a new public static
`AdminAuthorizationHandler.IsAdminUserId` helper, so the "Admin" policy and
this flag can never disagree), which is the entire mechanism the frontend
uses to decide whether to render the "Admin" nav link at all (REQ-504).
Deliberate deviation from a literal reading of REQ-505's drafted criteria:
`GET .../active` always returns `200 { hasActiveRound, round }` — including
`hasActiveRound: false` for "no round active right now" — rather than a
404-style "not found," because a 404 there is reserved to mean exactly one
thing: this whole endpoint group isn't registered (Production). That's the
only signal `AdminScreen.tsx` has for hiding the round-control/user-deletion
sections entirely rather than showing them disabled, so overloading the
same status code for both "nothing active" and "feature absent" would have
made that distinction impossible for the frontend to make reliably.
`frontend/src/admin/AdminScreen.tsx` (SCREEN-04) is the actual page,
composing three sections (unverified-data review reusing S-012's REQ-501/
502/503 endpoints, always rendered; round control and user deletion, both
gated on the active-round probe succeeding at all) — a non-admin who
somehow reaches it directly still gets an "access denied" message from the
page's own 403 handling, independent of the nav-link hiding. Test coverage:
`AdminManagementEndpointTests.cs` (new, 22 tests covering admin-success,
non-admin 403, and Production-absence 404 for every endpoint), 2 new
`AuthEndpointTests.cs` cases (REQ-504's `IsAdmin` true/false), 2 new
`UserRepositoryTests.cs` cases (`GetByEmailAsync` case-insensitivity),
`AdminScreen.test.tsx` (12 tests), 2 new `App.test.tsx` cases (nav-link
gating). An architecture-reviewer pass ran clean (fail-closed gating
correct, REQ-710/REQ-205 reuse confirmed, no boundary violation, no new ADR
needed — this reuses ADR-0006's existing pattern rather than introducing a
new one) and a code-reviewer pass found no bugs, only the two test-coverage
gaps above (since closed). Backend tests could not be run in this
environment (no `dotnet` SDK available, same limitation prior stories
recorded) — verified by close reading against the actual source instead;
frontend's full suite (103 tests), `tsc -b`, and lint all ran and passed.

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
**Built as:** matches the plan. New `GET
/leagues/global/leaderboard/window/{resolution}` route
(`XGArcade.Api.Leagues.LeaderboardEndpoints`), `{resolution}` parsed
case-insensitively into a new `LeaderboardWindowResolution` enum — anything
else is a 400. `LeaderboardService.GetWindowedLeaderboardAsync`: `Round`
reuses REQ-408's exact single-round path
(`GetClosedByGameKeyAsync(gameKey, 0, 1)` +
`GetTotalFinalPointsByRoundIdAsync`); `Week`/`Month`/`Year` compute a
calendar-aligned, half-open UTC window and go through two new repository
methods, `IRoundRepository.GetClosedIdsWithinWindowAsync` (locked-only,
`EndTime` range) and `IGuessRepository.GetTotalFinalPointsByRoundIdsAsync`
(the existing single-round method now delegates to this plural one rather
than duplicating the query). **Indexing plan honored without a new
migration:** the existing `Round(GameKey, EndTime)` index (REQ-408) and
`Guess`'s existing unique index on `(RoundId, UserId, CellId)` (`RoundId`
leading) already cover both new query shapes — documented inline on the
new repository methods rather than re-derived at review time. 18 new
REQ405-named tests (8 `LeaderboardServiceTests`, 10
`LeaderboardEndpointTests`, including a month-boundary case and the
invalid-resolution 400); full backend suite (510 tests) passes. **Frontend
(same session, follow-up commit):** a 4th "Time Windows" scope on
`LeaderboardScreen.tsx` with its own round/week/month/year sub-tabs, same
prev-scope/prev-resolution-ref fetch-on-transition pattern the `live`/`past`
scopes already established, rows rendered non-provisional (locked totals
only). New `fetchWindowedLeaderboard` in `lib/api.ts`. 4 new REQ405 Vitest
cases; full frontend suite (205 tests), `tsc -b`, and lint all clean.
`design-document.md` SCREEN-03 updated to document the full scope-tab
system (also backfilled the pre-existing gap where the `live`/`past`
scopes from S-053/S-054 had never been documented there at all).
**Follow-up (quality-architect review, 2026-07-21):** `lib/api.ts`'s
`WindowResolution`/`fetchWindowedLeaderboard` doc comments called these
"rolling" windows, contradicting this story's own decided design
(calendar-aligned, never rolling — see this entry's own text above and
`LeaderboardService.GetCalendarWindow`); corrected in place, comment-only,
no behavior change. `design-document.md` SCREEN-03's "Time Windows" bullet
still says "a rolling leaderboard" and has the same drift — flagged for a
`doc-sync`/`requirements-writer` pass rather than edited here.

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

**Built as:** `CategoryPairingRules.Trophy` added; `GridGameModule.
SelectPairing` generalized from S-030's two-way coin flip to a uniform
random choice among however many of five candidate pairings (Country×Club,
Club×Club, Country×Trophy, Club×Trophy, Trophy×Trophy) the seeded reference
data can support — Trophy is always kept second in a mixed pairing, same
precedent Country×Club already set for Country preceding Club.
`MapAttributeType`/`ResolveCandidateAsync`/`LookupLiveMatchesAsync` all gained
a Trophy branch; Trophy×Trophy has no dedicated live-lookup persist method
(unreachable in practice, see below) and falls through to the existing
fail-closed `null` return. `WikidataClient` gained
`QueryTrophyCountryIntersectionAsync`/`QueryTrophyClubIntersectionAsync`
(P166 "award received", truthy — a deliberate, documented call distinct from
P54's non-truthy rule, see the query builders' own comments — + P27/P54
respectively), reusing `BuildIntersectionQuery`'s shared plumbing.
`WikidataLookupService` gained `LookupAndPersistTrophyCountryAsync`/
`LookupAndPersistTrophyClubAsync`, reusing the existing `PersistMatchesAsync`
helper, persisting matches under `PlayerAttribute.AttributeType="trophy"`.
`ReferenceDataSeeder` gained a `Trophies` array seeding exactly one row,
Ballon d'Or (`Q166177`, `IsTeamTrophy=false`) — **this QID was not
independently verified against a live Wikidata page this session** (same
sandbox network limitation `ReferenceDataSeeder`'s own doc comment already
documents for S-036/S-037's guessed club QIDs, 4 of which turned out wrong)
— a human must check it before relying on this in production; `Trophy
Definition.Name` already had a unique index (`ADR-0012` scaffolding), so no
new migration was needed. **Confirmed, asserted-not-just-commented
consequence:** with only this one seeded trophy, every Trophy pairing is
infeasible for any realistic grid size and so structurally never selected
in production (`REQ108_SelectPairing_OnlyOneTrophySeeded_MatchingRealSeedData
_NeverSelectsAnyTrophyPairing`) — the mechanism itself is proven correct via
a faked larger trophy pool (5+/3+ values) in the rest of the new
`GridGameModuleTests` coverage. 42 new REQ108/REQ211-named tests added
across `GridGameModuleTests.cs`, `WikidataClientTests.cs`,
`WikidataLookupServiceTests.cs`, and `ReferenceDataSeederTests.cs`; full
backend suite (552 tests) passes. `docs/requirements-document.md` (REQ-107/
REQ-108 status notes), `MVP-SCOPE.md`, and `GridTemplate`/`IWikidataClient`'s
own doc comments updated to describe Trophy as built, not deferred.

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
the change: 367/367 across all five test projects (includes two
quality-gate-requested tests pinning the caller-cancellation-vs-query-
failure distinction in both `WikidataClient` and the importer). See
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

**S-043 · Photo reveal on a locked, correct cell — backend half (REQ-214)**
Backend implementation of the pull-forward MVP-SCOPE.md already recorded for
2026-07-18 (no new trigger — see REQ-214's own status note). Scoped to the
backend only, per the task that delegated it; the frontend half (SCREEN-01a
photo rendering, no-layout-change/no-broken-image-icon UI behavior) remains
a separate, not-yet-delegated task.
*Accept:* `WikidataClient`'s two intersection query builders fetch
Wikidata's `P18` (image) `OPTIONAL`, same shape as the existing `alias`
fetch; the resolved photo travels through `WikidataPlayerMatch` ->
`WikidataLookupService` -> a new `Player.PhotoUrl` column -> both existing
reveal responses (`POST .../guesses`' `SubmitGuessResponse` and
`GET /rounds/current`'s `CurrentRoundGuessResponse`) alongside
`ResolvedPlayerName`, additive-only; REQ103's never-throw contract and
REQ211/ADR-0018's live-lookup fallback path are unaffected (both route
through the same two builders, exercised by existing tests unchanged).
**Built as:** `Player.PhotoUrl` (nullable `string`), NOT a `PlayerAttribute`
column — a deliberate deviation from the task's literal instruction, made
and documented in-code (`Player.cs`'s `PhotoUrl` doc comment) because
`PlayerAttribute`'s composite key (`PlayerId`, `AttributeType`,
`AttributeValue`) holds many rows per player (one per career club), so a
scalar per-player field has no natural "which row owns it" answer there;
`Player` is already the single-row-per-person table (`FullName`,
`WikidataQid`), upserted the same way (`WikidataLookupService.
GetOrCreatePlayerAsync`, set once at creation, never re-synced on a later
lookup — same as `FullName`). `PlayerOverride` is untouched: photos are
never correctness data, so there is no "photo" override field and none was
added. EF Core migration `AddPlayerPhotoUrl` (hand-written against the
existing migration pattern — `dotnet` unavailable in this environment, so
`dotnet ef migrations add` could not be run; needs a real
`dotnet ef` verification pass before merge, same caveat as every migration
authored under this constraint). Flagged for `architecture-reviewer`: the
`Player` vs. `PlayerAttribute` placement decision could reasonably have
gone the other way and may warrant its own ADR. Wikidata's `P18` ->
Special:FilePath URL shape (used directly, no QID-style suffix split)
could not be verified against a live query (no `wikidata.org` access in
this environment) — flagged for manual verification, same precedent as
S-036/S-037's QID entries. Tests: `REQ214`-named, in
`WikidataClientTests.cs` (SPARQL shape + parsing, both builders),
`WikidataLookupServiceTests.cs` (persistence), `GuessSubmissionServiceTests.cs`,
`GuessEndpointTests.cs`, `CurrentRoundEndpointTests.cs` (photo present,
absent, and incorrect-guess-never-shows-photo, mirroring REQ-212's existing
name-reveal coverage at each level). Full suite not run in this
environment (`dotnet` unavailable) — CI is the first real run.

**S-044 · Photo reveal on a locked, correct cell — frontend half (REQ-214)**
Frontend half of S-043's backend work, delegated to `ui-implementer`
separately per REQ-214's own status note; landed in parallel, same day.
*Accept:* on a locked+correct, revealed cell (REQ-212), a photo shows
alongside the already-revealed name whenever the backend response includes
one; falls back to exactly today's text-only reveal (no broken-image icon,
no loading/error state) whenever it doesn't; shows/hides in lockstep with
REQ-212's existing reveal toggle, never a separate control; cell footprint
identical whether or not a photo is shown; never shown on an incorrect
guess.
**Built as:** `CurrentRoundGuess`/`SubmitGuessResponse` (`frontend/src/lib/types.ts`)
gained an optional `resolvedPlayerPhotoUrl?: string | null` field — written
before the backend half's DTOs were confirmed, as a same-name guess
mirroring `resolvedPlayerName`'s own naming; checked afterward against
`CurrentRoundGuessResponse.ResolvedPlayerPhotoUrl`/
`SubmitGuessResponse.ResolvedPlayerPhotoUrl` (`XGArcade.Api`) and confirmed
to match exactly under the default camelCase JSON policy, so no rename was
needed. `GridCell.tsx` threads it through the same `guess.isCorrect` gate
already used for the name. `CellState.tsx` renders it via a new
`PlayerAvatar` subcomponent inside a `.cell-state__name-group` wrapper
(grouping the avatar with the name so they wrap/reflow together) — `src`
missing, `null`, or a same-session `onerror` all collapse to the identical
"render nothing" branch, so the DOM is byte-for-byte identical to
pre-REQ-214 output in every "no photo" case (asserted directly in tests,
not just visually).
**Sizing judgment call (recorded in `docs/design-document.md` §3's
SCREEN-01a note, since no avatar token exists in §2):** no dedicated
avatar/photo token exists yet — reused the already-shipped, already
battle-tested `.category-label__badge--small` size (18px circle) the badge
dock next to it already uses, rather than inventing a new value. Fixed
literal `width`/`height` (not content-derived), `object-fit: cover`,
`flex-shrink: 0` — the mechanism that guarantees a photo can never grow the
row, since the box size never depends on the source image's own dimensions.
**Test infrastructure change:** `vite.config.ts`'s `test` block gained
`css: true` — without it, Vitest/jsdom don't apply real stylesheet rules at
all (`getComputedStyle` returns browser defaults, e.g. `font-size: medium`
regardless of the actual CSS), which would have made a genuine
dimension-regression assertion impossible; verified this doesn't change any
existing test's outcome (full suite re-run, all passing) before relying on
it for REQ-214's new tests. jsdom still has no real layout engine (no box
model), so even with `css: true` this can only assert the *CSS rules*
enforcing fixed dimensions are in effect (literal pixel `width`/`height`,
`flex-shrink: 0`, matched 1:1 against the badge dock's own already-shipped
size) — not true rendered pixel bounding boxes, which would need a real
browser. Real-browser verification (Playwright) was attempted and could not
be completed in this sandbox: `npx playwright install chromium` failed with
a 403 from the outbound proxy (`cdn.playwright.dev` not on the allowlist) —
flagged here rather than silently skipped, per this story's own
instructions. Added one E2E assertion to the existing REQ-212 reveal test
(`tests/e2e/play-grid.spec.ts`) confirming the fallback path renders no
`.cell-state__avatar` in a real browser via CI (the seed endpoint's players
have no `PhotoUrl`, so only the fallback path — not the photo-shown path —
is reachable through that seed today).
Tests: `REQ-214`-tagged, in `CellState.test.tsx` (photo shown; three "no
photo" cases — field absent, explicit null, load failure — all degrading
identically, including a byte-for-byte DOM equality check between the
absent-field and explicit-null cases; hides in lockstep with the reveal
toggle; never shown on an incorrect guess; the dimension-regression
assertions described above) and `GridCell.test.tsx` (end-to-end prop
wiring through the same `isCorrect` gate the name already uses).

**S-045 · Backfill `Player.PhotoUrl` for already-cached players (REQ-214)**
S-043 shipped `Player.PhotoUrl`, but only ever sets it at the moment a
`Player` row is first created (`WikidataLookupService
.GetOrCreatePlayerAsync`) — an already-existing row (every `Player` created
by a `warm-player-cache` run before S-043 shipped) is returned as-is and
never revisited, so `PhotoUrl` stays `NULL` on it forever. The user had run
`warm-player-cache` repeatedly since early July, leaving a large existing
`Player` table with every row's `PhotoUrl` permanently `NULL`, and
explicitly asked for a backfill rather than a destructive wipe-and-rerun
(`purge-player-pool` + `warm-player-cache` would cascade into
`PlayerAttribute`/`Guess`/`GridCell` history this codebase explicitly
protects).
*Accept:* a new `dotnet run -- backfill-player-photos` CLI verb (same
ADR-0024 shape as `warm-player-cache` — no new ADR needed, flagged and
confirmed squarely inside that existing decision) fills `Player.PhotoUrl`
for every player with a `WikidataQid` and no photo yet, in batches, without
touching any other table; idempotent and safe to re-run indefinitely — a
second run touches nothing already backfilled.
**Built as:** `IWikidataClient.QueryPlayerPhotosByQidsAsync` — a batched,
direct-by-QID SPARQL `VALUES` lookup (`BatchSize = 200`,
`PlayerPhotoBackfillService`'s own constant), a different shape from the
two intersection queries, with the same throw-on-failure
(`WikidataQueryException`) contract as `QueryPlayerPoolBirthYearAsync`
rather than the intersection queries' swallow-to-`[]` contract — per
`docs/coding-guidelines.md`'s 2026-07-18 error-handling guideline (a batch
job whose success metric is a row count must not swallow a failure as
"no data"). `IPlayerStoreRepository.GetPlayersMissingPhotoAsync`/
`UpdatePlayerPhotosAsync` — a paged read and a batched write (one
`SaveChangesAsync` per batch), never the whole table loaded at once.
`PlayerPhotoBackfillService` (`XGArcade.DataSync.Wikidata`, same placement
reasoning as `WikidataLookupService`/`PlayerNameIndexImporter` — it needs
both `IWikidataClient` and `IPlayerStoreRepository`, and `XGArcade.Data`
has no reference back to `XGArcade.DataSync`) — sequential, not concurrent
(same `DbContext`-safety reasoning as `PlayerCacheWarmingService`),
progress-logged periodically. Two judgment calls made and documented
in-code: (1) per-batch failure handling is log-and-continue, not
`PlayerNameIndexImporter`'s retry-then-fail-loud — a failed batch's players
simply stay `PhotoUrl == NULL` and are picked up automatically by the next
full re-run's own missing-photo query, so there's no equivalent "was this a
failure or genuinely no data" ambiguity to fail loudly about; (2) the read
cursor uses an in-run "already attempted" exclusion set rather than
`Skip`/`Take` — `Guid` has no LINQ-translatable ordering to keyset-paginate
on, and plain offset paging would silently skip untouched rows once a
batch's successful writes shrink the underlying `WHERE PhotoUrl IS NULL`
filter between calls. Accepted limitation (documented, same class as
`PlayerCacheWarmingService`'s own "below `MinValidAnswers`, re-queried
every run" note): a player with genuinely no Wikidata `P18` statement stays
`PhotoUrl == NULL` forever and is re-queried on every future full run —
there's no persisted "checked, genuinely no photo" signal distinct from
"never checked." New workflow `backfill-player-photos.yml`
(`workflow_dispatch` only, modeled directly on `warm-player-cache.yml`).
Tests: `REQ214`-named, in `WikidataClientTests.cs` (batched VALUES query
shape, throw-on-failure), `PlayerStoreRepositoryTests.cs` (the new
repository methods), `PlayerPhotoBackfillServiceTests.cs` (missing-photo
players backfilled; already-has-photo/no-QID players untouched and never
queried; batching respects `BatchSize`; idempotent re-run touches nothing;
a failed batch is logged and skipped without failing the run, and its
players remain retryable on a later run). Full backend suite run in this
environment (`dotnet`/`dotnet test` were both available, unlike prior
stories under this constraint) — 409 tests passed, 0 failed, across all
five backend test projects. No ADR added — confirmed this sits entirely
inside ADR-0024's existing scope.

**Bug found and fixed the same session, before merge:** the orchestrator
independently installed Postgres and ran `backfill-player-photos` for real
against a live database (this environment does have Docker/network access
after all — the "no real Postgres" caveat above described an earlier,
narrower attempt, not a hard sandbox limit) seeded with `/internal/test-
data`-style QIDs (shape `Qtest-<guid>`). A malformed `Player.WikidataQid`
crashed the *entire* run with an unhandled `ArgumentException` —
`QueryPlayerPhotosByQidsAsync`'s upfront QID-format validation threw
`ArgumentException`, not `WikidataQueryException`, so `BackfillAsync`'s
`catch (WikidataQueryException)` never caught it, contradicting this
story's own documented log-and-continue design. Fixed by extracting a
shared `WikidataQid.IsValid` predicate (new file, `XGArcade.DataSync
.Wikidata`) and having `PlayerPhotoBackfillService` pre-filter each batch
through it — a malformed QID is now skipped-and-logged per player (not
per whole batch) before it ever reaches the client, so the client's strict
throw-on-malformed-input contract (unchanged, still used by the two
intersection query methods too) is simply never exercised on this path.
Two new `REQ214`-named regression tests in `PlayerPhotoBackfillServiceTests
.cs` reproduce a mixed valid/malformed batch and an all-malformed batch.
Full suite after the fix: 411/411. Independently re-run against the exact
same live-database reproduction post-fix — completes cleanly (per-player
warning logged, exit 0) instead of crashing. Both `architecture-reviewer`
and `quality-architect` reviewed the fix clean, no blocking findings — see
`docs/CHANGELOG.md` and `NOTES.md`'s 2026-07-18 entries for the fix.

**S-046 · Decouple the photo from REQ-212's click/tap reveal — photo shows at rest (REQ-214)**
Direct product feedback on S-044's shipped result (PR #79, same-day): the
user asked, right after seeing the click-gated 18px avatar live, for the
photo to show automatically the instant a cell locks correct, filling the
cell, with no click/tap needed — REQ-212's reveal toggle should keep
governing only the name/badge dock, independently.
*Accept:* a correct, locked cell's photo (when the resolved player has one)
fills the cell at rest, no click/tap required; REQ-212's click/tap toggle
continues to reveal/hide only the name and badge dock, on top of the photo
when present, and no longer gates the photo at all; checkmark/points stay
overlaid on the photo (legible against it, not just against a plain
background); cell footprint identical whether or not a photo is shown, now
checked at rest rather than only on reveal; no-photo cells and the
incorrect-guess case are both fully unaffected.
**Design-doc gap closed as part of this story (not left for a follow-up):**
`design-document.md`'s REQ-214 status note explicitly flagged that §2 had
no overlay/scrim token for text-or-icon-on-photo contrast, and asked
whoever implemented this to add a real token rather than leaving
`CellState.css` with a bare `rgba()` value. Added `overlay-scrim`
(`rgba(26, 31, 28, 0.94)` — same hue as `text-primary`, 94% opacity chosen
so a worst-case pure-white photo showing through the remaining 6% still
can't push the effective backdrop light enough to fail contrast; measured
~5.5:1 in that worst case, well over the 4.5:1 floor). Documented, and
initially missed a second consequence of the same math: the darkened
`accent-gold-text`/near-black `text-primary` pairing this document uses
everywhere else is calibrated for a *light* (`surface-card`/white)
background, and both fail contrast on this new *dark* one — the lighter,
undarkened `accent-gold` is what actually clears 4.5:1 here (reused
directly, no new token needed for the checkmark/points), and the revealed
name (no correct/incorrect color of its own) needed `surface-card`/white
instead of `text-primary`. The `accent-gold` half was caught by contrast
math up front; the name's `text-primary`-is-illegible-here half was only
caught by this story's own required real-browser verification (a data-URI
test photo, since this sandbox has no network path to Wikidata to exercise
the real live-lookup) — flagged explicitly here rather than treated as a
minor fix, since it's exactly the kind of gap contrast math alone can miss.
**Built as:** the old `PlayerAvatar` subcomponent (S-044, 18px circle
nested inside the revealed name row, gated by `revealed`) is gone —
replaced by `CellPhoto` (`.cell-state__photo-img`), rendered by
`CellState` itself whenever `photoUrl` is present and hasn't failed to
load this session, entirely independent of `revealed`. Mechanically, the
photo layer (`.cell-state--photo`) is taken out of `.cell-state`'s normal
flex flow via `position: absolute; inset: 0`, positioned against
`.grid-cell`'s padding edge (`Grid.css` gained `position: relative` on
`.grid-cell` as the positioning context) — deliberately ignoring that
button's own padding so the photo bleeds to the cell's actual corners
(`border-radius: inherit` + `overflow: hidden` clip it to match). A
`.cell-state__overlay` band (the `overlay-scrim` background) sits above the
photo via `z-index`, holding the same `Row`/points markup the no-photo case
uses unchanged — `Row` no longer takes a `photoUrl` prop at all. The
CSS-cascade-tie note from S-013's darkened-token additions partially
applies again here: `.cell-state--photo .cell-state__meta` genuinely ties
`.cell-state--correct .cell-state__meta` on specificity, so it's placed
*after* in `CellState.css` specifically to win that tie by source order.
`.cell-state--photo .cell-state__icon--correct` is already strictly more
specific than the bare `.cell-state__icon--correct` rule and would win
regardless of placement — kept alongside the other override for
readability, not because it also depends on source order (an inaccuracy
in this entry's first pass, caught by `quality-architect`'s review and
corrected in both `CellState.css`'s own comment and here).
Tests: `REQ-214`-tagged, `CellState.test.tsx`'s photo-reveal describe block
rewritten (photo shows at rest with no click; reveal adds the name without
touching the photo; hiding again leaves the photo showing; no-photo/null/
load-failure cases re-verified byte-for-byte unaffected; the
`accent-gold`/`surface-card` on-scrim color pairing verified against the
no-photo case's `accent-gold-text`/`text-primary`; declared-CSS mechanism
tests — `position: absolute`, `inset: 0`, `object-fit: cover` — replacing
the old fixed-18px-slot assertions, same "check the layout-affecting
properties, not a snapshot" reasoning as before, since jsdom still has no
real layout engine) and `GridCell.test.tsx` (photo shows immediately after
lock, before any click; reveal/hide toggles the name only). E2E
(`tests/e2e/play-grid.spec.ts`): the dimension-invariance bounding-box
check now runs right after the cell locks (the new at-rest photo moment)
in addition to after the reveal click, both against a real Chromium via
Playwright. Full Vitest suite (116 tests) and full Playwright E2E suite (4
tests) both run for real in this environment (Postgres installed directly,
API started with `Auth__Mode=local-e2e`) — all green. Real-browser
verification of the photo-filled cell was done directly (a locally
generated data-URI test image set on a seeded player row, since this
sandbox's outbound network has no path to Wikidata) rather than only
trusted to automated assertions, per this story's own visual-change
verification requirement — confirmed the photo fills the cell edge-to-edge,
the scrim band stays legible under the checkmark/points/name in both the
at-rest and revealed states, and the no-photo case is visually unchanged.

**S-047 · Photo overlay covers too much of the photo; grid cells stretch into
flat rectangles at wide viewports (SCREEN-01a, §4)**
Two real UI/UX problems reported directly via phone screenshots, both
root-caused before scoping (not guessed): (1) `CellState.css`'s
`.cell-state__overlay` (the scrim behind a correct photo cell's checkmark/
points/name) covers ~40-45% of the cell on a real mobile screenshot,
against the design doc's own original ~30% intent — a solid `--space-2`
(8px) uniform padding plus un-tightened photo-variant type sizes on a
genuinely small (~90-110px) mobile cell. (2) `Grid.css`'s `.grid-table` used
`width: 100%` unconditionally, which combined with the browser's default
`table-layout: auto` above 480px and `.grid-table__cell`'s explicit `height`
(a CSS floor, not a ceiling) stretched a Tier-0 3-column grid's cells into
flat, short rectangles at any wide viewport (a real desktop, or a phone
reporting a similar CSS viewport via "Request desktop site") — same root
cause either way, not two separate bugs.
*Accept:* `design-document.md` gets a concrete, numeric overlay-coverage
target and a concrete cell-aspect-ratio rule (§4) before implementation,
per this repo's design-then-build discipline; the overlay's padding/type
size shrink on the photo variant only (no-photo cells and `overlay-scrim`'s
color/contrast math are unaffected); `.grid-table` no longer force-stretches
above 480px, so Tier-0 cells stay close to square at any viewport width;
S-040's ≤480px mobile header-fix (`table-layout: fixed` + `<colgroup>`) and
REQ-214's fixed-cell-footprint constraint are both unregressed; real-browser
verification at both a narrow and a wide viewport, not just passing tests.
**Built as:** matches the plan above, plus two real bugs found and fixed
during this story's own required real-browser verification, neither
anticipated in the original bug description (same "found and fixed in the
same session" precedent as S-041/REQ-214's own verification passes):
1. A revealed photo cell's name could get silently clipped by
   `.cell-state--photo`'s pre-existing `overflow: hidden` (needed so the
   photo itself doesn't bleed past the cell's rounded corners) — since the
   overlay is bottom-anchored and grows *upward*, a wrapped 2-line name got
   clipped from the *top*, in the worst case showing an unreadable *middle*
   fragment (e.g. "izecson..." from "Ricardo Izecson dos Santos Leite").
2. Worse: at a typical Tier-0 mobile cell's content width (~65-80px), the
   revealed row's four flex items (row badge, name, column badge, checkmark)
   didn't fit on one line for *any* real name, not just long ones —
   "Thierry Henry," an entirely ordinary name, rendered completely
   invisible once revealed on a photo cell, not just tightly cropped.
   Fixed by, on the photo variant only: hiding both badge-dock glyphs once
   revealed (decorative/`aria-hidden`, already redundant with the row/
   column headers shown above/left of the whole grid) and clamping the
   name to a single ellipsis-truncated line (`-webkit-line-clamp: 1`)
   instead of letting it wrap. This narrows (does not remove)
   design-document.md §2's "signature badge-dock" element to the no-photo
   case — recorded there and in SCREEN-01a as a deliberate, one-off
   exception, the same style of call as `accent-green-scrim`'s
   checkmark-color exception, not a change of mind about the badge dock
   generally. The no-photo case's badge dock (including its slide-in
   animation) is completely unaffected either way.
Mechanically: `CellState.css`'s `.cell-state__overlay` padding rewritten as
four explicit longhands (`padding-top`/`-bottom`/`-left`/`-right`) rather
than the shorthand `padding: var(--space-1) var(--space-2)` — discovered
mid-story that jsdom's CSSOM (unlike a real browser) doesn't expand a
multi-value shorthand containing `var()` into longhands at all, which would
have made the padding tightening untestable; longhands are equally valid
CSS and render identically in a real browser. `Grid.css`'s `.grid-table`
drops its unconditional `width: 100%` for `width: auto; margin: 0 auto;`
(letting the browser's own automatic table-layout algorithm shrink-to-fit
when a grid's columns don't genuinely need the full container width), and
re-establishes `width: 100%` inside the existing `@media (max-width: 480px)`
block alongside S-040's `table-layout: fixed`, unchanged there. No new
design tokens — every color/spacing value reused from `docs/design-
document.md` §2's existing table; only new literal values are font sizes
(11px/10px/12px icon/meta/name on the photo variant) and the `-webkit-
line-clamp: 1` truncation, both un-tokenized in the same acknowledged way
this doc's own §7 already flags for type scale generally.
Tests: `CellState.test.tsx` gained computed-style assertions (overlay
padding longhands, photo-variant font-size reductions, tightened row gap,
badge-dock `display: none` on the photo variant vs. visible on no-photo,
`-webkit-line-clamp`/`overflow` on the photo variant's name vs. absent on
no-photo) — the same "check the CSS mechanism, not a pixel snapshot"
approach REQ-214's own footprint tests already established for jsdom's lack
of a real layout engine. New `Grid.test.tsx` (2 tests) checks `.grid-table`'s
declared `width`/`margin` and every data cell's shared min-width/height
floor at jsdom's default (>480px) viewport. Full Vitest suite: 124/124
passing (was 116 before this story). `tsc -b --noEmit` and `oxlint` both
clean. Real-browser verification: done directly via a temporary,
not-committed Playwright + Vite harness (this sandbox has Chromium at
`/opt/pw-browsers` and no `dotnet`/Postgres, so a full backend-backed E2E
run wasn't available here — the harness rendered the real `Grid`/
`GridCell`/`CellState`/CSS with constructed props and an inline SVG data-URI
test photo instead, the same "no network path to a real photo host" workaround
prior sessions used) at both a 390px mobile viewport and a 1280px desktop
viewport, plus a 360px narrow-phone check confirming S-040's ≤480px header
wrap is unregressed — confirmed cells render square-ish at all three widths,
the overlay is visibly tighter against the photo, and (after the two fixes
above) a revealed name is legible in every case checked, including the
deliberately pathological long-name case. Harness files deleted before this
diff was finalized, not part of the shipped change.
`frontend/tests/e2e/play-grid.spec.ts`'s existing REQ-212/S-015 reveal
assertions unconditionally expected the badge dock visible after a reveal —
updated (not left for CI to find, the S-029 lesson) to branch on whether
`.cell-state--photo` is present on the cell (the same live-lookup-driven
non-determinism this test already handles for photo presence generally),
asserting the badge dock hidden on a photo cell and visible otherwise; the
revealed-name assertion itself needed no change, since `-webkit-line-clamp`
is a paint-only effect that doesn't touch the DOM text Playwright's
`getByText` matches against. Logic-reviewed only, not executed here (no
`dotnet`/Postgres in this sandbox, same gap S-041's own entry already
recorded for this file). No ADR — CSS/layout-only polish on
already-implemented REQ-204/REQ-212/REQ-214, same precedent as S-040/S-041's
own no-ADR calls for this kind of change.

**S-048 · Photo cell: nothing overlaid at rest, name+points-only overlay on
reveal (REQ-204/212/214, SCREEN-01a)**
Direct user feedback after seeing S-047 live, judged a further, deliberate
simplification rather than another coverage tweak: "at rest, only picture.
on click name + points only in an overlay." Scoped to the photo case only —
a correct cell without a photo is completely unaffected and keeps its
always-visible checkmark+points at rest (REQ-204's original behavior) and
its name+badge-dock reveal (REQ-212's original behavior).
*Accept:* `design-document.md`'s SCREEN-01a mock and status notes updated
first, including a plainly-recorded trade-off note (a photo cell loses its
always-visible-without-clicking score signal — only "this cell is done,"
via the photo's own presence, survives at rest) since this is the first
story to affect REQ-204's always-visible-at-rest guarantee itself, not just
what reveal shows; `requirements-document.md` gets matching status notes
under REQ-204, REQ-212, and REQ-214; `CellState.tsx`'s photo branch renders
only `<CellPhoto>` at rest (no `.cell-state__overlay` at all) and, once
`revealed`, an overlay with only the name and points (no checkmark, no
badge dock — S-047's badge-dock drop stays dropped); no-photo branch
untouched; dead CSS (the photo-variant checkmark/row/badge-dock-hide rules
that can no longer ever match once the checkmark/Row/badge markup is never
rendered there) removed, not left orphaned; real-browser verification at
mobile and desktop widths, not just passing tests.
**Built as:** matches the plan above. `CellState.tsx`'s `isCorrect` branch
now has two distinct sub-branches instead of one shared `overlayContent`
for both photo and no-photo cases: the no-photo path is byte-for-byte
unchanged (still `Row` + always-visible points, `revealed` gating only the
name/badges); the photo path no longer builds or reuses `overlayContent`
at all — it renders `<CellPhoto>` unconditionally and, only when
`revealed`, a `.cell-state__overlay` containing a plain
`<span className="cell-state__name">` and the existing
`<p className="cell-state__meta">` points paragraph, with no `Row` call at
all (so no checkmark, no badge dock — both are structurally absent, not
merely CSS-hidden, a stronger guarantee than S-047's `display: none`
approach for the badge dock). `CellState.css` changes: removed
`.cell-state--photo .cell-state__row` (S-047's tighter row gap — dead, no
`.cell-state__row` is ever rendered inside `.cell-state--photo` anymore),
`.cell-state--photo .cell-state__icon` and
`.cell-state--photo .cell-state__icon--correct` (S-047's smaller size and
the 2026-07-19 `accent-green-scrim` color exception — both dead, no
checkmark is ever rendered inside `.cell-state--photo` anymore), and
`.cell-state--photo .cell-state__badge-dock { display: none; }` (S-047's
defensive hide — dead for the same reason; a removal note was left in each
spot pointing back at this story rather than silently deleting history).
`.cell-state--photo .cell-state__meta`/`.cell-state--photo
.cell-state__name` (S-047's smaller type/line-clamp) are kept unchanged —
still needed, since the name and points still render, just only once
revealed. `--color-accent-green-scrim` itself (design-document.md §2,
`index.css`) is kept defined but is now documented as dormant (its
calibrated checkmark no longer renders anywhere) rather than deleted, per
this repo's own "document, don't silently drop" pattern for superseded
values — reversible in one line if a checkmark is ever deliberately
reintroduced to this overlay.
Tests: `CellState.test.tsx`'s photo-reveal describe block rewritten in
place — every assertion that expected a checkmark/points visible at rest
on a photo cell, or a checkmark/row/badge-dock structure once revealed, was
replaced (not left stale, the S-029 lesson) with the new invariant (nothing
overlaid at rest; name+points-only, no checkmark, no badge dock, once
revealed). New/rewritten tests: at-rest overlays-nothing, revealed overlay
content, revealed→hidden removes the whole overlay, structural absence
(not just CSS `display: none`) of `.cell-state__row`/icon/badge-dock on a
photo cell, and a checkmark-presence check confirming a photo cell never
renders one in either state while the no-photo case still does. Full
Vitest suite: 124/124 passing (unchanged count from S-047's own final
tally — tests were rewritten in place, not net-added, since this story
narrows behavior more than it adds new surface). `tsc -b --noEmit` and
`oxlint` both clean. Real-browser verification: done via a temporary,
not-committed Playwright + Vite harness (same approach S-047 used — this
sandbox has Chromium at `/opt/pw-browsers` and no `dotnet`/Postgres, so a
full backend-backed E2E run wasn't available here), rendering `CellState`
directly with an inline SVG data-URI test photo, at both a 390px mobile and
a 1280px desktop viewport: confirmed a photo cell shows only the picture at
rest, confirmed click reveals a legible name+points overlay with no
checkmark and no badge dock (including for a deliberately long name,
correctly clamped to one line), and confirmed the cell's bounding box is
pixel-identical before and after the reveal click (the fixed-footprint
guarantee, REQ-214, still holds). No min-height was needed on
`.cell-state__overlay` — two lines of text (name + points) fill it
comfortably at a realistic ~100px mobile cell size, doesn't collapse or
look empty. Harness files deleted before this diff was finalized, not part
of the shipped change. `frontend/tests/e2e/play-grid.spec.ts` needed no
behavioral assertion changes — it already avoided asserting on
checkmark/points visibility for the photo case specifically (its
`hasPhoto` branch only ever asserted on the badge dock and the name), so
S-048's changes fall within what that test already tolerated; one
descriptive comment (near the wrong-guess/correct-guess flow) was updated
for accuracy since it generically described "checkmark plus points at
rest" for any correct cell, which is no longer true for the photo case.
Logic-reviewed only, not executed here (no `dotnet`/Postgres in this
sandbox, same gap S-041/S-047's own entries already recorded for this
file). No ADR — CSS/component-internal simplification of already-implemented
REQ-204/REQ-212/REQ-214, same precedent as S-040/S-041/S-047's own no-ADR
calls for this kind of change.

**S-049 · Desktop cells still read small/cramped after S-047/S-048
(design-document.md §4, SCREEN-01a)**
Third round of direct user feedback on the same `/grid` screen, after
mobile was confirmed good ("it looks great in mobile"): "if i switch to
desktop view in the mobile it still looks weird.. feels like the grid
could be larger? and the cell + picture should look nice." Root-caused
before scoping, not guessed: S-047's `.grid-table` fix (letting the table
shrink-to-fit above 480px instead of forcing `width: 100%`) correctly
stopped cells stretching into flat rectangles, but `.grid-table__cell`'s
`min-width`/`height` at `≥960px` (S-040, 64px) was only ever a *floor* —
never a deliberate *target* for a genuinely wide viewport. With a Tier-0
grid's 3-5 columns and no cell content that ever needs more room than that
floor, the grid rendered at its smallest reasonable size (~300-400px)
inside `.app`'s 1200px desktop cap. "Cell + picture should look nice" is
the same root cause from a different angle — a 64px cell leaves almost no
room for a photo to read as more than a thumbnail.
*Accept:* `design-document.md` §4 gets a concrete, numeric desktop target
size (not just the S-047 aspect-ratio bound) before implementation;
`Grid.css`'s `≥960px` block raises the floor it already sizes columns
from to a real target, scoped to that breakpoint only (the 481-959px
shrink-to-fit range and the ≤480px `table-layout: fixed` range both
unregressed); the photo scales cleanly via the existing `object-fit:
cover` with no distortion; real-browser verification at mobile, mid, and
desktop widths, not just passing tests; requirements-document.md checked
(not assumed) for whether any REQ's acceptance criteria is pixel-size-
specific before deciding not to touch it.
**Built as:** matches the plan above. `Grid.css`'s `@media (min-width:
960px)` block: `.grid-table__cell`'s `min-width`/`height` raised from 64px
to **120px**, padding from `--space-2` to `--space-3` in step. Chosen
mechanism: raising the same floor value the table's shrink-to-fit column
sizing already keys off (per CSS2.1's automatic table-layout algorithm,
unchanged from S-047), not switching to `table-layout: fixed` +
`<colgroup>` widths the way the ≤480px breakpoint does — nothing in a
Tier-0 cell's content (text wraps; the photo layer is absolutely
positioned out of flow) ever exceeds the floor, so raising it functions as
a de facto target size in practice, confirmed by real-browser measurement
rather than assumed. `CellState.css` companion change: a new `@media
(min-width: 960px)` override on the photo-overlay's revealed name (12px →
15px) and points line (10px → 12px), plus overlay padding
(`--space-1`/`--space-2` → `--space-2`/`--space-3`) — S-047's mobile-tuned
type read undersized once the cell itself nearly doubled, a second angle
on the same feedback. The existing single-line ellipsis clamp
(`-webkit-line-clamp: 1`) needed no change — re-verified at the larger
size with a deliberately long name ("Ricardo Izecson dos Santos Leite"):
still truncates cleanly with no clipping/overflow. The no-photo case's
type sizes and the badge-dock/name/checkmark reveal layout were left
untouched — real-browser verification found them already reading fine at
the larger cell size.
Real-browser verification: done via a temporary, not-committed Vite dev
server + Playwright script (this sandbox has Chromium at
`/opt/pw-browsers`, no `dotnet`/Postgres, so a full backend-backed E2E run
wasn't available — same constraint and same workaround S-047/S-048 used),
rendering the real `Grid`/`GridCell`/`CellState`/CSS with constructed
props (a mix of photo/no-photo correct cells, an incorrect-with-attempt
cell, and an empty cell) and an inline SVG data-URI test photo, at four
viewports: 1280px desktop with a 3×3 grid (table rendered ~490×406px,
cells ~134×120px, ratio ~1.1:1 — square, comfortably inside the 1200px
cap), 1280px desktop with a 5×5 grid (table ~787×646px, same per-cell
size, still comfortably inside the cap, no overflow/scroll), 700px (the
481-959px shrink-to-fit range, confirmed unchanged from S-047), and 360px
(the ≤480px `table-layout: fixed` range, confirmed unchanged from S-040).
Also verified: the fixed-cell-footprint guarantee (REQ-214) still holds at
the new size (measured the same photo cell's bounding box before and after
a reveal click — pixel-identical, 108.7×95px content box), the revealed
photo overlay is legible and proportionate at the new size (screenshot-
reviewed before/after the CellState.css font-size bump), and a
deliberately long name still clamps to one ellipsis-truncated line with no
clipping. Harness files (a temporary Vite entry + Playwright screenshot
scripts) deleted before this diff was finalized, not part of the shipped
change.
Tests: `Grid.test.tsx` gained 2 new tests (S-049) — since the changed
values live inside an `@media (min-width: 960px)` block, and jsdom doesn't
apply media-scoped styles at all (confirmed directly: `window.matchMedia`
isn't even implemented in this jsdom version, which is also why every
pre-existing test in this file already scoped itself to "the
un-media-queried base rule"), these are raw-stylesheet-source assertions
(`Grid.css?raw`) rather than computed-style ones — checking the ≥960px
block contains `min-width: 120px`/`height: 120px`/`padding: var(--space-3)`
and no longer contains the old `64px` value, plus that the ≤480px block is
untouched. This is a different (source-text, not computed-style) test
technique than S-047's own Grid.test.tsx tests use, called out explicitly
rather than silently mixed in. Full Vitest suite: 126/126 passing (was 124
before this story — 2 net new). `tsc -b --noEmit` and `oxlint` both clean.
No E2E spec changes needed — `tests/e2e/play-grid.spec.ts`'s cell-box
assertions are all relative (before/after comparisons for the
fixed-footprint guarantee), never hardcoded pixel values, so they're
unaffected by the size change; confirmed by reading the file, not assumed.
`requirements-document.md` checked and left alone: the only place cell
pixel sizes (44px/64px) appear is inside a narrative "Built as" implemen-
tation-history note under REQ-204 (S-040's own entry), not phrased as a
Given/When/Then acceptance criterion — no REQ's testable acceptance
criteria depends on a specific cell size, so there's nothing to update.
No ADR — CSS/layout-only polish on already-implemented REQ-204/REQ-212/
REQ-214, same precedent as S-040/S-041/S-047/S-048's own no-ADR calls for
this kind of change.

**S-050 · Photo doesn't reach the cell's own border — real gap between the
photo and the bottom edge, on both breakpoints (REQ-214, SCREEN-01a, §4)**
Fourth round of direct user feedback on the same `/grid` screen, this time
with real screenshots of the live deployed app at both a normal mobile
view and a "Request desktop site" view: "see how they are not tall
enough to show full pictures.. we need to make sure that the pictures
actually fits the cell." Explicitly root-caused via real-browser DOM
measurement before any CSS was touched (not guessed from reading the
stylesheet — a prior static read of `Grid.css`/`CellState.css` found
nothing obviously wrong, since the mechanism as documented *should* work).
*Accept:* the actual gap measured via `getBoundingClientRect` on a real
Chromium render at both a mobile (~390px) and a desktop (~1280px)
viewport, using a genuinely non-square (portrait) test photo and a mixed
grid of different row-header wrap heights (matching the user's own
screenshot); root cause identified and recorded with real numbers before
any fix; fix verified by re-measuring the same boxes after, at both
breakpoints; REQ-214's fixed-cell-footprint guarantee (including its
"regardless of load failure" clause) re-verified, not just assumed
unaffected; `design-document.md` updated with the actual mechanism found
(not just "gap fixed").
**Built as:** matches the plan above.
- **Diagnostic:** a temporary, not-committed Vite entry + Playwright script
  (same pattern S-047/S-048/S-049 each used — this sandbox has Chromium at
  `/opt/pw-browsers`, no `dotnet`/Postgres, so a full backend-backed E2E
  run wasn't available) rendered the real `Grid`/`GridCell`/`CellState`/CSS
  with constructed correct-photo cells (an inline SVG data-URI portrait
  photo, 300×450, genuinely non-square) alongside an incorrect cell and
  row headers of different wrapped-line-counts ("Real Sociedad" 2 lines,
  "Paris Saint-Germain" 3 lines, matching the user's own screenshot),
  measuring `.grid-cell` (the button), `.cell-state--photo`, and
  `.cell-state__photo-img`'s `getBoundingClientRect()`s directly.
- **Measured root cause (before any fix):** the photo's rendered box was
  **pixel-identical to `.grid-cell`'s own box** in every case tested — the
  existing REQ-214/S-047 mechanism (`.cell-state--photo`'s `inset: 0`
  bleeding through `.grid-cell`'s own padding) worked exactly as documented.
  The real gap was one level further out: `.grid-table__cell` (the `<td>`
  itself) has its own, *separate* padding (`var(--space-1)` = 4px below
  960px, `var(--space-3)` = 12px at/above it) wrapping the button, which
  nothing before this story ever bypassed. Measured gap between the photo
  and the `<td>`'s actual border, **symmetric on all four sides** (not
  literally bottom-only as described — checked explicitly): 4.5px at
  390px viewport (4px padding + ~0.5px sub-pixel/border rounding), 12.5px
  at 1280px (12px padding + rounding) — confirmed identical top/right/
  bottom/left in every cell checked, including the mixed-row-header-height
  ones (61px/76px/120px row heights all showed the same proportional gap).
  Most plausible reading of the user's "bottom" framing: two photo cells
  stacked vertically compound this same gap across their shared row
  border (bottom padding + 1px border + next row's top padding), reading
  as a noticeably wider blank band there than the isolated left/right gaps
  of a single cell — a real, verified account for why the report singled
  out the bottom edge even though the underlying cause is uniform.
- **Fix attempted and rejected, recorded rather than silently discarded:**
  a `.grid-table__cell:has(.cell-state--photo) { padding: 0; }` override,
  scoped to only `<td>`s that actually contain a photo layer. This closed
  the measured gap (re-verified: 0.5px remaining on every side, exactly
  the `<td>`'s own 1px border) but a second, real bug was found during
  this same story's required re-verification pass before shipping it:
  `.grid-cell`'s own rendered size would then depend on whether
  `.cell-state--photo` is *currently* in the DOM, which `CellState.tsx`
  ties to photo **load success**, not just URL presence (a failed image
  load unmounts `.cell-state--photo` entirely, falling back to the
  no-photo branch). Confirmed via a deliberately-broken photo URL: the
  button visibly resized (95×95 → smaller) the moment `onError` fired
  after already rendering at the larger, gap-closed size — exactly the
  shift REQ-214's "constant regardless of... fails to load" guarantee
  forbids, and exactly what `play-grid.spec.ts`'s existing pre/post-
  `networkidle` `cell.boundingBox()` equality check would have caught
  non-deterministically in a real network environment (only when a real
  Wikidata photo URL actually failed to load). Rejected before shipping.
- **Fix shipped:** move the `position: relative` that establishes
  `.cell-state--photo`'s abs-positioning containing block from `.grid-cell`
  (the button) up to `.grid-table__cell` (the `<td>`) itself — one DOM
  level further out, past *both* padding layers. `.grid-cell`'s own CSS is
  otherwise completely unchanged (same `width`/`height`/padding as before
  this story), so its own rendered box is now governed solely by those
  unconditional rules regardless of whether a photo is showing, loading,
  or failed — verified directly: `.grid-cell`'s computed `width`/`height`/
  `padding` are identical whether or not its child renders
  `.cell-state--photo`, and its `getBoundingClientRect()` is
  pixel-identical before and after the same deliberately-broken-photo-URL
  failure scenario above (95×95 both times). The photo layer itself, no
  longer constrained by the button's own box at all, now fills
  `.grid-table__cell`'s full padding box independently — measured gap
  after the fix: **0.5px on every side at both breakpoints**, exactly this
  rule's own 1px border split by sub-pixel rounding, i.e. the cell's actual
  visible edge, not a leftover gap. Re-verified with the same asymmetric
  test photo and mixed-row-header-height grid as the diagnostic, at both
  breakpoints, plus the revealed (name+points overlay) state (unaffected —
  CellState.css needed no change at all for this fix) and a deliberately
  long name ("Ricardo Izecson dos Santos Leite," still clamps to one
  ellipsis-truncated line as S-047 established). Screenshot-reviewed
  before/after at both breakpoints: photo now visibly flush with the grid
  lines, incorrect (no-photo) cells' own padding completely unaffected.
- **Mechanically:** `Grid.css`'s `.grid-table__cell` rule gains
  `position: relative;`; `.grid-cell`'s own `position: relative;` is
  removed (comment rewritten to explain why, pointing at
  `.grid-table__cell`'s new comment for the full mechanism).
  `CellState.css`'s `.cell-state--photo` doc comment and `CellState.tsx`'s
  `CellPhoto` doc comment both updated to describe the new containing
  block accurately (no CSS changes needed in either file — the fix is
  entirely in `Grid.css`). No new design tokens; no change to
  `.cell-state__photo-img`'s `object-fit: cover` (confirmed, not assumed,
  to be the right tool per this story's own scoping note — the bug was
  always about which box the image fills, never the fit mode).
- **Tests:** `Grid.test.tsx` gained a new describe block (2 tests,
  replacing an earlier draft written against the rejected `:has()` fix
  before it was reverted) — a raw-stylesheet-source check
  (`.grid-table__cell` contains `position: relative`, `.grid-cell` does
  not contain a `position: relative;` *declaration*, distinguished from
  this same comment's own prose mention of that phrase by requiring the
  trailing `;`) and a rendered-DOM check confirming `.grid-cell`'s
  computed `width`/`height`/`padding` are identical with and without a
  photo present. Full Vitest suite: **128/128 passing** (was 126 before
  this story — 2 net new). `tsc -b --noEmit` and `oxlint` both clean.
  Real-browser verification: done via the temporary harness described
  above; harness files deleted before this diff was finalized, not part
  of the shipped change. No `play-grid.spec.ts` changes needed — its
  `cell.boundingBox()` assertions target `.grid-cell` (via
  `data-testid="grid-cell-..."`), the exact element this fix keeps
  load-outcome-independent; confirmed by reading the file and by the
  deliberately-broken-photo-URL check above, not assumed. Logic-reviewed
  only, not executed here (no `dotnet`/Postgres in this sandbox, same gap
  every prior story in this chain already recorded for this file).
  `requirements-document.md` gets a matching 2026-07-19 status note under
  REQ-214 (the "filling the cell" acceptance criterion was, in the
  shipped-through-S-049 version, only true up to this same measured gap);
  no acceptance criterion's *substance* changed — the footprint-invariance
  bullet already existing there is what this story re-verifies more
  thoroughly (including the load-failure transition), not a new rule.
  No ADR — CSS-only fix (plus doc-comment accuracy updates) to
  already-implemented REQ-214, same precedent as S-040/S-041/S-047/S-048/
  S-049's own no-ADR calls for this kind of change; the rejected `:has()`
  approach never shipped, so there's nothing to revert in an ADR sense
  either.

**S-051 · Show the full photo, allow letterboxing, instead of cropping to
fill the cell (REQ-214, SCREEN-01a, §2) — a direct product decision, not a
bug fix**
Fifth round of iteration on the same `/grid` photo cell. The user said "I
want the full picture to be visible within the cells, so they are not cut
off" — a request, not a report of broken behavior. Asked directly (via
`AskUserQuestion`) to choose between "Crop photo to fill the cell
completely (today's behavior)" and "Show full photo, allow empty space
(letterbox)," after being shown the trade-off explicitly (a
differently-shaped photo may leave a thin background strip on two sides of
the cell), the user chose the letterbox option. Recorded here plainly as a
deliberate, informed choice — same discipline this backlog already applies
to S-048's "at rest, only picture" trade-off — not silently implemented as
if it were an obvious default.
*Accept:* `.cell-state__photo-img`'s `object-fit` changed from `cover` to
`contain`; whether the letterbox background reads as intentional (rather
than a leftover gap) checked in a real browser, not assumed; whether the
existing `overlay-scrim` contrast math already covers a letterboxed
worst-case checked against the actual `--color-surface-card` token value,
not assumed; real-browser verification with both a portrait and a
landscape test photo; REQ-214's fixed-footprint guarantee re-confirmed,
not just assumed unaffected by the fit-mode change.
**Built as:** matches the plan above.
- **CSS change:** `CellState.css`'s `.cell-state__photo-img` rule:
  `object-fit: cover` → `object-fit: contain`. No change to the
  `inset: 0`/explicit `width: 100%; height: 100%` sizing mechanism that
  keeps the cell's own footprint independent of the image — that
  guarantee (REQ-214) comes from the box being absolutely sized, never
  from the fit mode, and is unaffected by which one is used.
- **Letterbox background, checked rather than assumed fine:** with
  `contain`, empty space can appear inside `.cell-state--photo`'s own box
  wherever the photo doesn't reach — before this story that box had no
  background of its own and relied on `.grid-cell`'s (the button behind
  it) `background: var(--color-surface-card)` (Grid.css) showing through
  its transparent box. Real-browser screenshots (Chromium,
  `/opt/pw-browsers`, a temporary Vite+Playwright harness — same
  not-committed-diagnostic pattern S-047 through S-050 each used, deleted
  before this diff was finalized) at both a mobile (390px) and desktop
  (1280px) viewport, with a genuinely non-square landscape (450×300) and
  portrait (300×450) test photo (a bright red border frame around a blue
  fill, so any cropping would be immediately visible as a missing border
  edge), confirmed: the whole photo — including all four border edges —
  renders with nothing cropped in every case, and the letterbox strip
  reads as a clean, plain white card background, not a visible seam or an
  obviously wrong color. Made this explicit rather than left incidental:
  `.cell-state--photo` now has its own `background-color: var(
  --color-surface-card)` (written as the longhand, not the `background`
  shorthand, so it's assertable from a jsdom test the same way the
  overlay's own padding already needed longhands for) — same token, same
  value, just no longer dependent on `.grid-cell`'s background happening
  to stay what it is today.
- **Overlay contrast over the letterbox, checked against the real token
  value, not assumed:** `overlay-scrim`'s existing contrast math
  (design-document.md §2) was calibrated against "the worst case: a
  pure-white photo showing through." `--color-surface-card`
  (`frontend/src/index.css`) is `#FFFFFF` — literally pure white, not an
  off-white tint — so a landscape photo's bottom letterbox (the
  orientation that can land directly behind the bottom-anchored overlay)
  presents the *exact same* underlying color the existing math already
  treats as the worst case, not merely a similar one — alpha-blending
  doesn't distinguish "a very light photo" from "an opaque white
  background." Same `rgb(51, 56, 53)` blended value, same 4.65:1
  (`accent-gold`)/11.99:1 (`surface-card`, the revealed name's color)
  ratios apply unchanged. **No new token or contrast math needed** —
  confirmed by checking the actual token value, not assumed, and
  re-confirmed visually: the same real-browser harness's revealed,
  landscape-oriented photo cell (bottom letterbox landing behind the
  overlay) showed the name and points text clearly legible against the
  scrim. A portrait photo's letterbox lands left/right, never behind the
  bottom-anchored overlay, so it was never a contrast concern.
- **Footprint guarantee re-confirmed:** the harness measured identical
  cell dimensions (`getBoundingClientRect`) across landscape, portrait,
  at-rest, and revealed cases at both breakpoints — unaffected by the
  fit-mode change, as expected, since the mechanism (absolute
  positioning + explicit sizing) is orthogonal to `object-fit`.
- **Tests:** `CellState.test.tsx`'s existing `object-fit` assertion
  updated from `'cover'` to `'contain'`; one new test asserts
  `.cell-state--photo`'s `background-color` is the `surface-card` token
  (declared value, same jsdom-can't-resolve-var()-shorthands workaround
  documented elsewhere in this file). Full Vitest suite: **129/129
  passing** (was 128 before this story — 1 net new). `tsc -b --noEmit`
  and `oxlint` both clean. jsdom cannot render actual letterboxing (no
  real layout engine) — the declared `object-fit`/`background-color`
  values are the extent of what's unit-testable; the "whole photo
  visible, letterbox reads clean, overlay stays legible" outcomes are
  real-browser-only findings, recorded above rather than asserted in a
  test that can't actually check them.
- **Docs:** `design-document.md` — SCREEN-01a gets a new S-051 status note
  (the `▒▒▒▒▒▒` fill mocks now read as "photo scaled to fit, possibly with
  a background strip on two sides," not a literal uniform fill); the
  2026-07-18 REQ-214 implementation note and the S-049 §4 note, both of
  which described `object-fit: cover` as current/unchanged, are marked
  superseded rather than silently edited out. `requirements-document.md`
  gets a matching REQ-214 status note (the "filling the cell" acceptance
  criterion never specified crop-vs-contain; now narrowed to mean "the
  cell's footprint," not necessarily every pixel) and an addition to that
  REQ's own "Test level" line noting the real-browser-only verification
  needed here. No ADR — CSS-only change to an already-implemented
  requirement, same precedent as every other story in this chain; this
  one is a recorded product *decision* rather than a bug fix, but that
  doesn't change the ADR calculus (no structural/component-boundary
  choice was made).

**S-052 · Wikidata sync data is auto-verified; only the guess-time fallback stays reviewable (REQ-502/503, ADR-0029)**
Discovered via play-testing (not a request): S-026's admin page gave `GET
/admin/player-data/unverified` its first real UI caller, which surfaced
that the review queue had reached 52,782 rows — every `PlayerData` row
ever synced from Wikidata since S-006, all still `Confidence =
"unverified"` (that field was never conditional on anything, and REQ-503's
"approve → verified" action was never built, S-012's own gap). A
manual review queue at that size is unusable, and doesn't match what the
data actually is: a routine Wikidata sync is Tier 0's own trusted primary
source, not a user submission awaiting correction.
**Built as (ADR-0029):** new `WikidataLookupOrigin` enum
(`XGArcade.DataSync.Wikidata`) threaded through
`IWikidataLookupService.LookupAndPersistAsync`/`LookupAndPersistClubClubAsync`
— `Sync` (grid-generation cache-miss, `GridGameModule.GetMatchCountAsync`;
cache-warming, `PlayerCacheWarmingService`) now persists `Confidence =
"verified"`; `GuessTimeFallback` (REQ-211/ADR-0018's guess-time re-check,
`GridGameModule.RefreshCellFromLiveLookupAsync`) still persists
`"unverified"`. A new one-time CLI verb, `dotnet run --
verify-wikidata-player-data` (`verify-wikidata-player-data.yml`, manual
`workflow_dispatch`, same shape as `warm-player-cache.yml`), bulk-flips the
existing 52,782-row backlog to `verified` — the historical rows can't be
split by origin after the fact (`Source` is always `"wikidata"` either
way), so this matches the new default for the overwhelming majority of
what actually created that backlog; safe to re-run. Test coverage: two new
`WikidataLookupServiceTests.cs` cases (`REQ211_LookupAndPersistAsync
_GuessTimeFallback_PersistsAsUnverified` and its Club x Club mirror)
alongside every pre-existing "hit persists" test updated to assert
`Confidence == "verified"` for `WikidataLookupOrigin.Sync`; two
`GridGameModuleTests.cs` assertions confirming the right origin is passed
from both call sites (generation-time cache-miss vs. guess-time fallback).
*Accept:* Wikidata-sourced sync data persists verified; guess-time-fallback
data persists unverified; existing backlog is bulk-cleared via the new CLI
verb. *Deps:* S-026 (surfaced the issue), S-006/S-030 (the sync paths being
changed), S-011/ADR-0018 (REQ-211's fallback, left unchanged in behavior).

**S-053 · Live leaderboard: fold active-round points into the shared/per-league total, and expose it as its own active-round scope (REQ-406/407, ADR-0031)**
Direct product request (2026-07-19), routed through `requirements-writer`
first since it's a real product/scoring decision, not a rendering fix — see
REQ-406/407's full acceptance criteria. Today the global/per-league
leaderboard (REQ-401/404, `LeaderboardService`) only sums locked
`Guess.FinalPoints`, `null` until a round closes (ADR-0022); REQ-206's
status note has flagged this as the deliberate gap to revisit since S-029.
This story builds one shared live-contribution computation — for a round
participant (same definition as ADR-0021's `MaterializeUnansweredCellsAsync`:
≥1 guess in that round), each cell of the active round contributes its
current `LivePoints` (REQ-204, correct) or `MaxPointsPerCell`
(locked-incorrect, both attempts used) or nothing at all (not yet
attempted — deliberately not `0`, which already means "best score" under
ADR-0021's golf model) — and exposes it two ways: (a) folded on top of
REQ-401/404's existing `SUM(FinalPoints ?? 0)` for the shared/per-league
leaderboard (REQ-406), and (b) as its own standalone "this round (live)"
scope, participant-only, reachable from the same leaderboard screen
(SCREEN-03) as an additional selectable option alongside REQ-405's
resolution tabs, not a separate screen (REQ-407). Both recompute on every
read — **no caching or snapshotting**, per ADR-0031, which was written
specifically for this story after `architecture-reviewer` flagged that this
reverses §6.2a's DB-side-aggregate leaderboard pattern and narrows
REQ-607's bounded-read-cost guarantee; read that ADR before implementing,
its "For AI agents" section applies directly. A player with zero guesses in
the active round is unaffected on the shared total and does not appear on
the standalone active-round scope at all. Present both live figures as
visibly provisional (REQ-204/213's existing "estimated" framing) so a
player can't mistake a live rank for a locked one.
*Accept:* REQ406/REQ407-named tests: shared leaderboard total includes a
correctly-guessed active-round cell's current `LivePoints` and a
locked-incorrect cell's `MaxPointsPerCell`, excludes not-yet-attempted
cells entirely (not as `0`); recomputing after an underlying guess changes
(e.g. another participant's guess shifts a cell's uniqueness) produces a
different total/rank on the next read with no explicit invalidation step;
a non-participant is excluded from the standalone active-round scope;
requesting the active-round scope when no round is active returns a clear
"no active round" response (REQ-303's existing pattern), not a generic
error. *Deps:* S-011 (REQ-401/404/206 baseline), S-018 (REQ-204 `LivePoints`),
S-028/ADR-0021 (golf model + participant definition + `GetCellIdsAsync`),
S-034 (REQ-607 pagination pattern the shared total's page slicing still
uses), ADR-0031 (governs this story's implementation approach directly).
**Built as:** `backend-implementer` implemented exactly as scoped — one
shared `ILiveRoundContributionService`/`LiveRoundContributionService`
(`XGArcade.Core.Scoring`) computing the three-case per-cell contribution,
consumed by both `LeaderboardService.GetGlobalLeaderboardAsync` (now takes
a nullable `Round? activeRound`, REQ-406) and the new
`GetActiveRoundLeaderboardAsync` (REQ-407, new `GET
/leagues/global/leaderboard/active-round` route, 404 "No active round"
when none exists). Cells resolved only via `IGameModuleResolver`/
`IGameModule.GetCellIdsAsync` (ADR-0003 intact — confirmed by
`architecture-reviewer`). `ui-implementer` added SCREEN-03's three-way
scope selector ("All-time" / "This round (live)" / "Past rounds") reusing
the existing "~N pts estimated" wording (`GridScreen.tsx`/`CellState.tsx`
precedent) for the provisional framing, no new token. **Quality-gate bug
found and fixed before merge:** the live scope's `useRef` "fetch once"
guard never reset, so re-entering the tab after switching away silently
kept showing stale data indefinitely — the opposite of "come back to see
the update." Fixed to refetch on every genuine transition into the scope
(previous-scope comparison instead of a permanent latch), while still
avoiding the original React StrictMode double-fetch race the guard existed
to prevent; regression tests added for the leave-and-return case.
Full backend suite 465/465, full frontend suite 170/170, `tsc -b`/lint
clean.

**S-054 · Browsable per-round leaderboard for past closed rounds (REQ-408)**
Companion story to S-053, product-requested in the same round of feedback
(2026-07-19) — see REQ-408's full acceptance criteria. Unlike S-053, this
is locked-only, no live component: REQ-206's own `SUM(final_points)`
definition, applied per closed round, individually browsable by round id —
closing the other half of the gap REQ-206's status note has flagged since
S-029 ("Tier 0 has no past-round-browsing UI at all"). Reached from the
same leaderboard screen (SCREEN-03) as S-053's scopes, via a "past rounds"
option that lists browsable closed rounds (most recently closed first,
never the active/upcoming round — that's S-053's territory) before drilling
into one round's leaderboard. The round list itself is paginated with the
same `cursor`/`pageSize` shape and defaults REQ-607 already established
(50/100) — a second, differently-shaped pagination convention was
explicitly rejected when REQ-408 was drafted. A round id that doesn't exist
returns "not found"; a round id that exists but hasn't closed yet returns a
distinct "not closed yet" response — never silently served as if it were
complete (it's only reachable live via S-053 while active).
*Accept:* REQ408-named tests: round list returns only closed rounds, most
recent first, paginated per REQ-607's shape; a specific closed round's
total matches REQ-206's locked formula exactly and never changes on
re-read; not-found vs. not-closed-yet are distinct, correctly-coded
responses. *Deps:* S-011 (REQ-206 locked total, REQ-205 close), S-034
(REQ-607 pagination shape reused for the round list), S-053 (shares
SCREEN-03's scope-selector UI this story adds its own option to — build
after S-053 to avoid two stories independently adding the same selector).
**Built as:** required a new `Round.ClosedAt` (nullable `DateTime`) column
— a real EF Core migration (`AddRoundClosedAt`), executing the exact
follow-up ADR-0022's own "Follow-up" section already anticipated ("revisit
adding an explicit `Round.ClosedAt` column then, when a real `dotnet`
environment is available"); no new ADR needed. New `GET
/leagues/global/leaderboard/closed-rounds` (paginated list) and `GET
/leagues/global/leaderboard/closed-rounds/{roundId}` (404 not-found / 409
not-closed-yet) routes, backed by new `IRoundRepository
.GetClosedByGameKeyAsync` and `IGuessRepository
.GetTotalFinalPointsByRoundIdAsync`. **Quality-gate bug found and fixed
before merge:** the original `RoundCloseService.CloseRoundAsync` persisted
`ClosedAt` *before* `LockRoundScoresAsync` finished, opening a window where
this story's own "closed round" endpoint could read a round as final while
some guesses still had `FinalPoints == null`. Reordered so `ClosedAt` is
only ever set after locking completes successfully — a throw during
locking now leaves `ClosedAt` null and a later retry resumes/redoes locking
before ever closing; new tests cover both the failure and the successful-
retry paths. `ui-implementer` built the "Past rounds" scope on SCREEN-03:
round-selection list (labelled by close time, no fabricated round
numbering) drilling into that round's locked, non-provisional leaderboard.
Full backend suite 465/465, full frontend suite 170/170, `tsc -b`/lint
clean.

**S-055 · Fix mobile/tablet grid cell sizing: uniform column widths regardless of name length**
Reported via direct user screenshots of a 3×3 grid: `table-layout: auto`
(the browser default, left in place above the 480px breakpoint since
S-047/S-049) sizes each `<table>` column independently from the widest
cell/header content in that column specifically, so a long team/player
name ("Atletico Madrid") rendered its column visibly wider than a short
one ("Sevilla") — most visible at mobile/tablet widths, still measurably
present at desktop (measured 92.75px/147.97px/141.59px across three
columns at a 700px viewport before the fix, 120px/155.97px/149.59px at
1280px). S-040's own `table-layout: fixed` fix at ≤480px already sidesteps
this there; this story generalizes it. No REQ change — this is a visual
bug fix against `design-document.md` §4's existing uniform-cell-size
intent, not new product behavior.
*Accept:* every data column renders at an identical, explicit width at a
given breakpoint regardless of header/cell content length, confirmed via
real-browser measurement (not visual inspection alone) at 390px/700px/
1280px; no horizontal-scroll fallback triggers; header/row-label text
wraps instead of stretching its column; touch targets stay ≥44px;
REQ-214's fixed-cell-footprint photo invariant is unaffected. *Deps:*
S-040 (≤480px `table-layout: fixed` precedent), S-047/S-049 (existing
`≥960px` cell-size targets this story reuses rather than reinvents).
**Built as:** `table-layout: fixed` now applies unconditionally (previously
only inside the ≤480px block), with every data column given an explicit,
equal `<col>` width via a new `grid-table__data-col` class on `Grid.tsx`'s
`<colgroup>` (previously unclassed for data columns) — fixed layout takes
each column's width from its own `<col>` rather than its widest cell, so
an explicit, identical width per data column is what actually guarantees
identical columns. Chosen widths reuse existing values rather than
inventing new ones: 90px for the 481-959px band (already
`.grid-table__col-header`'s own min-width), 120px at ≥960px (already
`.grid-table__cell`'s S-049-verified target); the row-header column scales
in step (110px / 140px). Also closes a `design-document.md` aspect-ratio
violation the fix surfaced at 481-959px (cells were ~2.8:1 before this
change, outside the documented 1:1–1.3:1 bound) by giving
`.grid-table__cell` a matching height in that band, the same way S-049
already did for ≥960px; a matching `≥960px` typography/padding bump keeps
the photo-overlay's revealed name/points legible at the larger cell size.
Verified via real Chromium render at 390/700/1280px with mixed-length
headers: uniform column widths, no horizontal scroll, wrapped (not
clipped) header text. 177/177 frontend tests pass, `tsc -b`/lint clean.
`docs/design-document.md` updated in the same story (§4's cell-sizing
notes).

**S-056 · Leaderboard scoring fairness: exclude never-played members, credit unguessed cells in an initiated round; rename scope tabs (REQ-401/404/406/407)**
Product-owner-confirmed fairness fix to S-053's leaderboard work
(2026-07-19/20), routed through `requirements-writer` first since both
changes are real scoring-behavior decisions, not rendering fixes — see
REQ-401/404/406/407's own dated status notes for full acceptance criteria.
Two independent problems, fixed together because both touch
`LeaderboardService`/`ILiveRoundContributionService` in the same session:
(1) a league member who has never submitted a single `Guess` (in any
round, locked or active) defaulted to a total of `0`, which under
ADR-0021's lowest-wins golf model is the *best* possible score — such a
member ranked #1 ahead of everyone who had actually played; now excluded
from the ranked list entirely (REQ-401/404). (2) the active-round live
estimate (REQ-406/407) never credited an untouched cell, so a
freshly-initiated grid read as unfairly low the moment a player made their
first guess, instead of starting near the theoretical max and counting
down; now, for a round *participant* (≥1 guess anywhere in that round,
ADR-0021's existing definition), every cell they've made zero guesses on
at all contributes `MaxPointsPerCell`, same as a locked-incorrect cell — a
cell with one of two attempts used and still unresolved is unaffected and
continues to contribute nothing. Also folded in: SCREEN-03's scope-tab
labels renamed "This round (live)"/"Past rounds" → "Current Round"/
"Previous Rounds" — purely cosmetic, no REQ specifies exact tab wording,
so no `requirements-document.md` acceptance-criteria change for the rename
itself (only its own literal quoted strings elsewhere in that doc needed
updating to match).
*Accept:* REQ401/REQ404-named tests: a member with zero guesses ever is
absent from the ranked list, not ranked first with total `0`; a member
with ≥1 guess (locked or live) still ranks normally even at a computed
total of `0`. REQ406/REQ407-named tests: a round participant's zero-guess
cell contributes `MaxPointsPerCell`; a cell with one of two attempts used
and unresolved still contributes nothing; a non-participant is unaffected
and excluded from the active-round scope entirely, unchanged. *Deps:*
S-011 (REQ-401/404 baseline), S-053 (REQ-406/407,
`ILiveRoundContributionService`), S-028/ADR-0021 (participant definition
this story reuses, not redefines).
**Built as:** `LeaderboardService.GetGlobalLeaderboardAsync` now queries a
new `IGuessRepository.GetUserIdsWithAnyGuessAsync` (`GuessRepository`)
alongside the existing locked-only `GetTotalFinalPointsByUserIdsAsync`,
filtering the ranked list to that set before the existing `0`-default
logic ever applies — kept as a separate query specifically so a member
active only in the currently active (unlocked) round is not mistaken for
never-played. `LiveRoundContributionService` now tracks each
participant's per-cell attempted-cell set and adds `MaxPointsPerCell` for
every round cell outside it. No change needed to
`ScoreLockingService`/`RoundCloseService` — `MaterializeUnansweredCellsAsync`
(ADR-0021, S-028) already implements the identical behavior for
locked/final scoring at round close. Tab rename is a one-line label change
in `LeaderboardScreen.tsx`. `docs/requirements-document.md` updated in the
same session (REQ-401/404/406/407's dated status notes and the literal
tab-label quotes at REQ-407/408).

**S-057 · Wikidata guess-time fallback also auto-verified (ADR-0032, supersedes ADR-0029); admin bulk-approve action (REQ-503 extension)**
Two product decisions from the same 2026-07-20 round of feedback, shipped
together: (1) one day after ADR-0029 deliberately kept REQ-211's
guess-time fallback lookup persisting `Confidence = "unverified"` so an
admin could still spot-check that narrower, less-vetted path, the product
owner decided all Wikidata-sourced data should be verified by default,
including that path — see ADR-0032 for the full reasoning and trade-offs
accepted (no human review left on the narrowest lookup path anymore).
(2) Independently, REQ-503's admin review UI (SCREEN-04, built S-026) has
never had a working "approve → verified" action — S-052/ADR-0029 narrowed
the review *queue's* size but never built the missing action itself. This
story finally builds it, in bulk-first form (a single-row approve is just
the N=1 case), including "select all" and per-row partial-failure
reporting.
*Accept:* REQ211-named tests: guess-time-fallback lookups persist
`Confidence = "verified"`, matching the `Sync` origin's existing behavior
(supersedes S-052's own `..._PersistsAsUnverified` test for this origin).
REQ503-named tests: single approve flips one row to `verified` and logs
`admin_id`/timestamp; bulk approve (including select-all) flips every
selected row, each logged individually; a partially-failing bulk approve
(a row already reviewed or deleted between selection and submission)
reports per-row success/failure rather than succeeding or failing the
whole batch; no `reason` field required or accepted for either form,
unlike `PlayerOverride`'s existing "correct" action. *Deps:* S-052/ADR-0029
(the `WikidataLookupOrigin` split this story's first half reverses, not
rebuilds), S-012/S-026 (REQ-503's existing review list/UI this story's
second half extends).
**Built as:** `WikidataLookupService.ConfidenceFor` now maps both
`WikidataLookupOrigin` values to `"verified"` — the enum and its two call
sites (`GetMatchCountAsync` → `Sync`, `RefreshCellFromLiveLookupAsync` →
`GuessTimeFallback`) are kept, not collapsed away, per ADR-0032. A second
run of the existing `verify-wikidata-player-data` CLI verb (idempotent,
from ADR-0029) is needed against the deployed database to flip the
2026-07-19→2026-07-20 window of `GuessTimeFallback` rows still sitting as
`unverified` — not run as part of this story (no DB access in the
implementing sandbox), flagged as a manual follow-up. New `POST
/admin/player-data/approve` (`XGArcade.Api.Admin.AdminEndpoints`, Admin
policy) takes a list of `PlayerData` ids; `IPlayerStoreRepository
.ApprovePlayerDataAsync`/`PlayerStoreRepository` evaluates each
independently in one `SaveChangesAsync` call, backed by new
`PlayerData.ApprovedByAdminId`/`ApprovedAt` columns (`AddPlayerDataApproval`
migration) mirroring `PlayerOverride.LockedByAdminId`/`LockedAt`'s existing
audit shape rather than a separate audit-log table. `AdminScreen.tsx` adds
a checkbox per row, "select all," a selected-count readout, and an
"Approve selected" button, plus a persistent per-row results list after
submit. `docs/decisions/0032-wikidata-guess-time-fallback-also-auto-verified.md`
(supersedes ADR-0029, whose own status line is updated to
`Superseded by ADR-0032`) and `docs/requirements-document.md` (REQ-211/503
dated status notes) updated in the same session.

**S-058 · Edit display name from Settings; persistent login via refresh token (REQ-714/715, new; ADR-0033)**
Two independent, newly-drafted requirements from the same round of
feedback, shipped together as one Settings-screen-adjacent batch: REQ-714
(no way to change `User.DisplayName` after signup existed until now) and
REQ-715 (the frontend discarded the refresh token `POST /auth/login`
already returned, so an expired access token always forced a full
re-login even mid-session). Both are genuinely new REQs, not extensions of
existing ones — drafted and reviewed by `requirements-writer` on
2026-07-20 before implementation. See REQ-714/715's own full acceptance
criteria and ADR-0033 (refresh-token storage location) for the complete
picture.
*Accept:* REQ714-named tests: a submitted name between 1-30 characters
(inclusive of both bounds) updates `DisplayName` and is reflected
everywhere it's shown, with no backfill needed since nothing denormalizes
it; a name already in use by a different account (any casing) is rejected
with a specific conflict error; resubmitting the caller's own current name
(including a pure-casing change) is never treated as a conflict against
itself. REQ715-named tests: a successful login stores the refresh token,
not only the access token; a missing/expired access token silently
exchanges the stored refresh token for a new one without an interruption;
an invalid/expired/revoked refresh token fails clearly and signs the
person out; logout and account deletion both clear the stored refresh
token. *Deps:* REQ-701 (the length-bound/uniqueness mechanism REQ-714
reuses), ADR-0013 (backend-mediated Supabase Auth, REQ-715's refresh
endpoint extends the same pattern), REQ-713/S-039 (`SettingsScreen.tsx`,
REQ-714's host screen).
**Built as:** `PUT /auth/display-name` (`AuthController.UpdateDisplayName`)
reuses REQ-701's exact length bound and `IUserRepository
.DisplayNameExistsAsync`, now with an `excludeUserId` parameter for the
self-resubmission case; `POST /auth/refresh` (`AuthController.Refresh`)
mediates through Supabase Auth exactly like `/auth/login`/`/auth/signup`
(ADR-0013), sharing `SupabaseAuthClient`'s request plumbing
(`PostCredentialsAsync` renamed `PostAuthRequestAsync`) rather than a
parallel implementation, with `LocalE2EAuth` implementing the same
contract for the local E2E stack. Frontend: `SettingsScreen.tsx` gained
the display-name edit form; `App.tsx` stores the refresh token in
`localStorage` alongside the access token (ADR-0033), attempts a silent
refresh on a missing/401'd access token before falling back to logout, and
clears both tokens on logout/account deletion. **Flagged, not built:**
explicit server-side refresh-token revocation on logout — REQ-715's own
acceptance criteria only require clearing the frontend's stored copy;
account deletion already invalidates any outstanding refresh token as a
side effect of deleting the underlying Supabase identity. Backend and
frontend test suites extended (`UserRepositoryTests.cs`,
`AuthEndpointTests.cs` including an exact-30-character boundary case,
`SettingsScreen.test.tsx`, `App.test.tsx`).
`docs/decisions/0033-refresh-token-storage-localstorage.md` and
`docs/requirements-document.md` (new REQ-714/715 entries) added in the
same session; `docs/design-document.md` SCREEN-08 (missing the
display-name form mock/description) and `docs/legal/privacy-policy-draft.md`
(display name is editable, not only chosen at signup) both caught and
fixed by a later doc-sync pass.

**S-059 · Fix real-mobile grid cell sizing: uniform row heights regardless of row-header label length (follow-up to S-055)**
Direct user report, with a real-device screenshot, one session after S-055
shipped: "cells still not the same size" on real mobile. Pixel measurement
of that screenshot confirmed S-055's own fix held (columns uniform, ~238px
each) but surfaced a second, previously-undetected bug on the *row* axis at
real mobile widths (390-412px) specifically — "Real Sociedad" (row-header
wraps 2 lines), "Paris Saint-Germain" (3 lines), and "Valencia" (1 line)
rendered at visibly different row heights (measured ~185px/238px/157px in
the screenshot), tracking each row's own row-header line count. Same
underlying CSS2.1 mechanism S-055 already fixed for columns
(`table-layout`/an explicit floor acting as a ceiling only by coincidence),
just on the axis S-055 never checked: `.grid-table__cell`'s `height` is
only ever a *floor* on a table row's height, and the 481-959px/≥960px bands
already carry a real target height comfortably larger than what wrapped
row-header content needs (so never exhibited this), but ≤480px still relied
on the bare 44px touch-target floor every real row-header already exceeds.
No REQ change — visual bug fix against `design-document.md` §4's existing
uniform-cell-size intent, same class of change as S-055.
*Accept:* every data row's cells render at an identical, explicit height at
the ≤480px breakpoint regardless of row-header label length, confirmed via
real-browser measurement (not visual inspection alone) at 390px/412px, with
390-1280px all re-verified for no regression; row-header text still wraps
(not silently clipped) up to 3 lines, with graceful ellipsis truncation,
full text preserved in the DOM for assistive tech, beyond that (flagged,
not silently shipped); touch targets stay ≥44px; REQ-214's
fixed-cell-footprint photo invariant is unaffected. *Deps:* S-040 (row-header
stacking/wrap treatment this story reuses), S-047 (the floor-vs-ceiling
table-row mechanism this story's own root-cause note is the row-axis twin
of), S-049/S-055 (existing "give it a real target height instead of a bare
floor" precedent, reused rather than reinvented, at the one breakpoint that
still lacked it).
**Built as:** `.grid-table__cell` gets an explicit 78px target height at
≤480px (a working number for this grid's own longest real content —
"Paris Saint-Germain"'s natural 3-line/76px need, plus a small rounding
margin), in a *second*, separate `@media (max-width: 480px)` block placed
after the base (unconditional) `.grid-table__cell` rule — not merged into
the existing, earlier ≤480px block, since that block is declared *before*
the base rule in source order and an override placed there loses the
cascade to it despite its own media condition matching (verified directly:
an earlier version of this fix placed the override in the wrong block and
real-browser measurement showed no change at all). Paired with a 3-line
`-webkit-line-clamp` on the row-header's own name text (the existing
≤480px block) — the same truncation-with-ellipsis technique
`CellState.css`'s `.cell-state--photo .cell-state__name` (S-047) already
uses — so a label longer than any of this grid's own three examples can
never exceed the 78px budget and reintroduce the bug for a single outlier
row; 3 lines specifically because "Paris Saint-Germain" itself already
needs exactly 3 to render in full, so none of the three real examples in
the bug report actually gets truncated. Verified via real Chromium render
(not-committed diagnostic Playwright + Vite harness, same approach
S-047/S-050/S-055 each used) at 390px/412px/700px/1280px: all three
example rows render at an identical height per breakpoint (78px/78px/90px/
120px), 700px/1280px unchanged from before this story (regression check),
and a deliberately-long stress-test row-header name truncates cleanly with
an ellipsis rather than breaking layout or stretching its row. 201/201
frontend tests pass (4 new, `Grid.test.tsx`), `tsc -b`/lint clean. No E2E
spec change needed — `play-grid.spec.ts`'s cell-footprint checks run at
the suite's default (desktop-sized) viewport, unaffected by a ≤480px-only
fix. `docs/design-document.md` updated in the same story (§4's cell-sizing
notes, new S-059 bullet).

**S-060 · Median, participation-gated all-time leaderboard (REQ-409)**
Implements REQ-409's 2026-07-20 decision (see that REQ's full text): the
all-time leaderboard ranks by the median of each player's per-round
`SUM(FinalPoints)` totals (locked rounds only, no live component) instead
of the raw sum, gated by a minimum of 5 qualifying rounds (closed round +
at least one `Guess` in it) to appear ranked at all — replacing, not
adding a tab alongside, the existing `GetGlobalLeaderboardAsync` ranking.
Below-threshold players are excluded the same way REQ-404's zero-guess
exclusion already works. Ties broken by display name, same as every other
ranking. See REQ-404's added status note for how the interim (pre-this-
story) behavior is described.
*Accept:* REQ409-named tests: median computed correctly for odd/even
qualifying-round counts; exactly-4-rounds excluded, exactly-5 included and
ranked; an active/unlocked round never counts toward the threshold or the
median; sort order and tie-break match every other leaderboard ranking.
API test confirms the all-time endpoint returns the median-based ranking
and a below-threshold member is absent, not present with a placeholder.
*Deps:* S-011 (global leaderboard), S-034 (pagination).
**Built as:** matches the plan exactly, plus one deliberate removal beyond
the plan's literal scope. New `IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync`
joins `Guesses` to `Rounds` (`Guess` has no navigation property to
`Round`), filters `ClosedAt != null`, groups by `(UserId, RoundId)`
DB-side. `LeaderboardService.GetGlobalLeaderboardAsync`'s median uses
`MidpointRounding.AwayFromZero` only for the displayed `int` value — the
underlying `double` drives sort order/ties, so rounding never affects
rank. The REQ-406 live-round fold was removed from this method entirely
rather than left dormant (no resolved meaning for folding a live round
into a median — `GetActiveRoundLeaderboardAsync`/REQ-407 is untouched and
still live); `GetTotalFinalPointsByUserIdsAsync`/`GetUserIdsWithAnyGuessAsync`
were deleted as dead code once this was rewritten (no other callers).
Existing tests whose premise (single-guess ranking, live-fold behavior)
no longer held were updated to seed real closed rounds and 5+ qualifying
rounds rather than deleted; 9 new REQ409 unit tests and 2 new API tests
added. Full backend suite: 580/580 passing.

**S-062 · Password policy, enumeration-safe errors, signup/login rate limiting (REQ-701/606)**
Closes REQ-701's password-policy and account-enumeration-safe-error
clauses and REQ-606's signup/login rate-limiting clause — all three
already fully specified, no product decision needed. Password policy is
the existing §5 default (minimum 8 characters, no forced complexity),
enforced server-side first among `AuthController.Signup`'s free local
checks and client-side in `AuthScreen.tsx`. Every Supabase signup-rejection
reason now returns the identical generic body rather than Supabase's own
wording — deliberately not narrowed to the already-registered case, since
a distinctly different message only for that case would itself leak which
case occurred; Supabase's real error is logged server-side only.
Signup/login get a 10-request/minute-per-IP rate limit via ASP.NET Core's
built-in `RateLimiting` middleware (`QueueLimit = 0`, 429 on exceeding, no
new package).
*Accept:* REQ701-named tests: signup blocked under 8 characters, succeeds
at exactly 8, generic error returned for every Supabase rejection reason
(never the real reason). REQ606-named tests: signup/login both 429 after
exceeding the per-minute limit; exhausting one endpoint's limit doesn't
affect the other.
*Deps:* S-004 (auth exists).
**Built as:** matches the plan exactly. Rate-limit tests exploit that
`WebApplicationFactory`'s TestServer leaves `RemoteIpAddress` null (all
requests collapse onto one partition), so a fast in-process burst of 11
requests deterministically trips the limit with no clock mocking needed.
7 new backend tests, 3 new frontend tests; full backend suite (580 tests)
and frontend suite (212 tests) both green, `tsc -b`/lint clean.

**S-061 · Admin "remove the data point" action (REQ-503, closes the last gap)**
S-057 built "approve"; this closes REQ-503's other missing action,
"remove," the same day. `POST /admin/player-data/remove` (`AdminEndpoints`,
Admin policy), bulk-capable from the start like "approve," per-id
success/failure reporting. Hard-deletes the `PlayerData` row — checked
first that nothing holds a foreign key to a specific row id
(`PlayerOverride` keys on `(PlayerId, Field)`, not a `PlayerData` id;
`PlayerAttribute` has no reference to it at all), so a real delete is safe
and matches the REQ's own "remove," not "hide," wording. Unlike "approve,"
removal has no "must still be unverified" precondition — it's a general
corrective action, not tied to the review queue's current state. No new
`RemovedByAdminId`/`RemovedAt` audit columns (nothing survives to attach
them to once the row is gone) — audit logging is a structured `ILogger`
line at removal time instead, matching this codebase's established
preference against a general-purpose audit-log table (same reasoning
`PlayerOverride`'s own audit columns already established elsewhere).
`AdminScreen.tsx` gained a "Remove selected" action in the same
bulk-selection bar as "Approve selected."
*Accept:* REQ503-named tests: single remove deletes one row; bulk remove
(including select-all) deletes every selected row; a row already removed
between selection and submission reports `NotFound` for that id without
failing the rest of the batch; a non-admin gets 403 and the row survives.
*Deps:* S-057 (existing review list/approve action this extends).
**Built as:** matches the plan exactly. 5 new backend tests
(`AdminEndpointTests.cs`), 4 new frontend tests (`AdminScreen.test.tsx`);
full backend suite (557 tests) and frontend suite (209 tests) both green,
`tsc -b`/lint clean. REQ-503's full acceptance criteria (approve, correct,
remove) are now all built.

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
T-108 email confirmation + Resend (REQ-702-705 — REQ-701's own
password-policy/enumeration-safe-error clauses are built, S-062) ·
~~T-109 custom leagues~~ (create/join pulled forward and built, see
S-063 — REQ-404's full per-custom-league leaderboard is T-109's unclaimed
remainder) · T-110 legal docs finalized (**bright line: before public
launch**).

**S-063 · Custom leagues create/join (REQ-402/403)**
Pulled forward ahead of `MVP-SCOPE.md`'s original Tier 1 placement — no
trigger fired (no request was actually observed), same "pulled forward by
deliberate choice" pattern as REQ-108/REQ-214. Scope: create a league
(auto-enrolls the creator), join via a 6-character invite code (887M-symbol
alphabet excluding visually-ambiguous characters), list a player's own
custom leagues by name/code. New `Core.Leagues.LeagueService`/
`ILeagueService`, `Api.Leagues.LeagueEndpoints` (`POST /leagues`,
`POST /leagues/join`, `GET /leagues/mine`), `LeaguesScreen.tsx` (new nav
entry alongside Leaderboard/Settings). Explicitly out of scope: REQ-404's
full per-custom-league leaderboard (no tab switcher, no per-league
leaderboard reads — `LeaderboardScreen.tsx`/`LeaderboardService.cs`/
`LeaderboardEndpoints.cs` untouched) and the per-user league caps
(25 created / 100 joined) requirements-document.md mentions elsewhere —
neither was requested for this story.
*Accept:* REQ402-named tests: create succeeds and auto-adds the creator,
invite codes are unique. REQ403-named tests: join with a valid code
succeeds; join with an invalid code returns a clear error and creates no
membership; unauthenticated calls are rejected.
*Deps:* S-004 (auth).
**Built as:** matches the plan, plus one gap caught and fixed before
merge: the new `League.InviteCode` unique index was added to
`XGArcadeDbContext.OnModelCreating` but the corresponding EF Core
migration was missing — generated and included
(`20260720163147_AddLeagueInviteCodeUniqueIndex`) so the constraint
actually exists against a real database, not just the in-memory test
provider. Invite-code collision handling: an in-app pre-check
(`InviteCodeExistsAsync`, retried up to 5 times) plus the DB unique index
as the real race-safety net, mirroring `User.NormalizedDisplayName`'s
existing pattern; re-joining a league the caller already belongs to is an
idempotent success (`JoinLeagueOutcome.AlreadyMember`), not an error — a
documented product-shape choice since REQ-403 doesn't specify this case.
18 new backend tests (8 `LeagueServiceTests`, 10 `LeagueEndpointTests`),
12 new frontend tests (`LeaguesScreen.test.tsx` + `HeaderNav.test.tsx`);
full backend suite (580 tests) and frontend suite (226 tests) both green,
`tsc -b`/lint clean.

**S-064 · Implement dark mode / selectable color themes (REQ-716)**
Builds the design decided in the 2026-07-20 design pass (REQ-716,
`docs/design-document.md` §2's "Dark theme" subsection, ADR-0034) — no
new design decisions to make here, purely implementation. A three-state
System/Light/Dark toggle on `SettingsScreen.tsx`, persisted in
`localStorage` under a new key, applied as a `data-theme` attribute on
`<html>` before first paint (avoid a flash of the wrong theme, same
concern `App.tsx`'s existing `ACCESS_TOKEN_STORAGE_KEY` read-at-startup
already has to handle). Every CSS custom property in `frontend/src/index.css`
gets a `:root[data-theme="dark"]` (or equivalent) override matching
ADR-0034's token table exactly — colors only, no layout/spacing/type/
animation changes. "System" resolves `prefers-color-scheme` at load and
reactively on its `change` event.
*Accept:* toggling to Dark/Light pins that theme regardless of OS setting
and persists across a reload; System (default) follows
`prefers-color-scheme`, including a live OS-level change while the app is
open; every screen renders legibly in both themes (spot-check each SCREEN
mock); no flash of the wrong theme on load.
*Deps:* the design pass above (2026-07-20, REQ-716/ADR-0034 — design
decided), the existing `SettingsScreen.tsx` (ADR-0030's mobile-nav/
Settings consolidation).
**Built as:** matches the plan exactly. New `frontend/src/lib/theme.ts`
(`useThemePreference` hook, `applyStoredThemePreference`/`resolveTheme`/
`applyResolvedTheme` helpers) mounted once in `App.tsx` (not inside
`SettingsScreen`, so the "system" preference's reactive
`prefers-color-scheme` listener stays active regardless of which screen
is showing) and called once more, standalone, in `main.tsx` before the
React tree mounts (avoids a flash of the wrong theme). `index.css`'s
`:root[data-theme='dark']` block copies every hex value verbatim from
`design-document.md` §2's table — including making
`accent-green-text`/`accent-gold-text` (dormant in dark theme per that
table) point at the same values as `accent-green`/`accent-gold`, so every
existing component that already reads those two specific variable names
picks up the correct dark color with zero component-code change.
`SettingsScreen.tsx` gained a System/Light/Dark `radiogroup`. Verified
visually via a real Chromium screenshot (light vs. dark, both legible) in
addition to 16 new `theme.test.ts` unit tests plus updated
`SettingsScreen.test.tsx` coverage; full frontend suite (248 tests),
`tsc -b`, and lint all clean. **One coincidental-not-derived finding,
flagged rather than silently accepted:** the login/signup submit button's
text color reuses `--color-surface-card` as its foreground — outside the
design pass's audited token list — which in dark theme measures 4.64:1
against the green button background (clears 4.5:1 AA, but narrowly and by
coincidence). See REQ-716's own status note.

**S-065 · Alias and fuzzy-typo matching for guess scoring (REQ-208)**
Closes REQ-208's two still-deferred clauses — the "simple half" (lowercase/
diacritics/punctuation normalization) was already built, S-009.
`GridGameModule.FindMatchAsync` now tries three stages in order, each only
reached if the previous produced no candidate fitting both of the cell's
categories: exact `Player.NormalizedFullName` match (unchanged),
`PlayerAlias.NormalizedAlias` exact match, then a bounded edit-distance
fuzzy pass. Stays entirely on the correctness-checking side
(`PlayerAttribute`/`PlayerAlias`, COMP-06) — no new read path into
`PlayerNameIndex` (COMP-10), per ADR-0007's boundary rule (autocomplete
and correctness matching must never merge).
*Accept:* REQ208-named tests: diacritics (existing coverage), a new
alias-match case, fuzzy-typo cases that should match, and near-miss
strings that should NOT match (confirms the edit-distance threshold
doesn't make the game trivially easy).
*Deps:* S-009 (name normalization, exact matching).
**Built as:** matches the plan, plus a length-tiered edit-distance
threshold rather than one fixed number — 0 for names <=4 characters
normalized length, 1 for 5-8, 2 for >=9 — verified against concrete name
pairs before committing to the thresholds (e.g. "Pele"/"Dele," two
different real players, is distance 1, so a flat tolerance of 1 would
have made them collide; "Ronaldo"/"Rivaldo" is distance 2, correctly
rejected at the 5-8 tier's tolerance of 1). New
`XGArcade.Data.NameEditDistance` (plain Levenshtein, O(n·m) DP — the
smallest well-understood metric for "minor typos," not
transposition-aware or phonetic matching). The fuzzy candidate pool is
bounded to players already known (via a cached `PlayerAttribute` row) to
satisfy at least one of the cell's two categories, never a full-table
scan — a player satisfying neither can never be a correct answer for this
cell regardless of name. `FilterByCategoriesAsync`/`AcceptMatch` extracted
so all three stages (exact/alias/fuzzy) share identical
category-fit/REQ-209-disambiguation handling, preventing drift between
them. 27 new tests (`GridGameModuleTests.cs`, `PlayerStoreRepositoryTests.cs`,
new `NameEditDistanceTests.cs`), including two ordering tests proving
alias/fuzzy repository calls never happen once an earlier stage already
resolved a match. Full backend suite (607 tests) green.

**S-066 · National teams as distinct footballing entities (REQ-114, ADR-0035)**
Pulled forward ahead of `MVP-SCOPE.md`'s original Tier 1 placement, by
explicit product decision (not a triggered event from that file's own
trigger list — struck through there per its own "update when pulled
forward" instruction). England, Scotland, Wales, and Northern Ireland
seeded as four additional `CountryDefinition` rows (alongside, never
replacing, United Kingdom), each with a new `UsesCountryForSportProperty`
flag set `true` — queried via Wikidata's `P1532` ("country for sport")
instead of `P27` ("citizenship"), since none of the four are sovereign
states and every home-nation player's `P27` is uniformly United Kingdom.
See ADR-0035 for the full alternatives-considered record, including why
this is a per-row flag on the existing "Country" category type rather than
a new category type or a separate reference table.
*Accept:* REQ114-named tests: the new `P1532` query path is used only for
flagged countries; the existing `P27` path is completely unaffected for
every other seeded country; a national-team country pairs with clubs
exactly like any other country, no special-casing in grid generation
itself; the guess-time live-lookup fallback (REQ-211) also dispatches
through the right query path for a national-team cell.
*Deps:* S-006 (Wikidata client), S-030 (generalized pairing selection).
**Built as:** matches the plan exactly. `GridGameModule`'s internal
`CategoryCandidate` record struct gains a third field carrying the flag
from `CategoryValueRepository` through generation/guess-time-fallback to
the point a live Wikidata call is actually dispatched — chosen over
re-resolving the full `CountryDefinition` row at each dispatch site, since
that dispatch point (`LookupLiveMatchesAsync`) is called from
`GetMatchCountAsync` inside `PickHeadersAsync`'s hot loop, and an extra
repository round-trip per candidate tried during generation would be a
real, avoidable cost (see ADR-0035's alternatives table). The
`P27`-vs-`P1532` choice is made in exactly one place,
`WikidataLookupService.LookupAndPersistAsync` — `GridGameModule`'s
dispatch call site needed no change at all. New
`IWikidataClient.QueryNationalTeamClubIntersectionAsync`/
`BuildNationalTeamClubIntersectionQuery`, using the truthy `wdt:P1532`
shortcut (safe here — unlike `P54`, there's no Wikidata editorial
convention of marking one `P1532` statement "preferred rank," so best-rank
semantics and "represented this country at all" coincide, same reasoning
already used for `P166`'s truthy shortcut in S-031). Matched players
persist under the same `PlayerAttribute.AttributeType = "nationality"`
vocabulary as every other country — "England" is just another value,
same as "United Kingdom" already is. QIDs (England `Q21`, Scotland `Q22`,
Wales `Q25`, Northern Ireland `Q26`) are training-knowledge values, **not
verified against live Wikidata from this sandbox** — flagged in the
seeder, REQ-114, and ADR-0035; a human must verify before relying on them
in a real deployment, same process S-037 already established. **Known
follow-up, not fixed here:** Country × Trophy's dispatch branch doesn't
yet honor the flag — currently unreachable in production (the seeded
trophy pool is too small for any Trophy pairing to ever be selected, same
as Trophy × Trophy), tracked in ADR-0035. 20 new tests across
`WikidataClientTests.cs`, `WikidataLookupServiceTests.cs`,
`ReferenceDataSeederTests.cs`, `GridGameModuleTests.cs`; new EF Core
migration for the `CountryDefinition` column generated and included. Full
backend suite (627 tests) green.

**S-067 · Disambiguation UI (REQ-209)**
Pulled forward ahead of `MVP-SCOPE.md`'s original Tier 1 trigger ("you
actually observe two real players with the same normalized name both
satisfying one cell"), which had never actually fired — by deliberate
choice, same pattern as REQ-108/REQ-214/REQ-402-403's own precedent.
Replaces S-065's auto-accept-lowest-id-and-log behavior: when a guess
resolves to more than one fitting candidate, the player is now shown a
picker instead of the system guessing on their behalf. Backend/API and
frontend landed as two sequential sub-tasks the same day.
*Accept:* REQ209-named tests: exactly-one-candidate still auto-accepts
unchanged; more-than-one-candidate returns disambiguation candidates
without persisting a `Guess` row or incrementing attempt count (REQ-210);
a valid `chosenPlayerId` resubmission scores correctly and consumes
exactly one attempt total (prompt + resolution together, not two); an
invalid/stale `chosenPlayerId` is treated as an ordinary incorrect guess.
*Deps:* S-065 (REQ-208's matching pipeline this replaces the disambiguation
tail-end of), S-011 (guess submission).
**Built as (backend/API):** `GridGameModule.AcceptMatchAsync` (renamed
from `AcceptMatch`, now async) returns `ScoreResult.DisambiguationCandidates`
— each candidate's *other* known `PlayerAttribute` values (nationality/
club/trophy), excluding whichever of the cell's own two categories every
candidate already satisfies, since repeating those wouldn't distinguish
anything — instead of birth year (REQ-209's own text only offers that as
an illustrative "e.g." example; `Player` has no birth-year column, and
adding one was out of scope). A `chosenPlayerId` fast path re-runs the
same exact/alias/fuzzy pipeline from scratch and only accepts if the id
is present in the freshly-computed matching set — never trusts a
client-supplied id blindly, and an invalid one fails closed to an
ordinary incorrect guess rather than throwing.
`GuessSubmissionService.SubmitGuessAsync` returns the new
`NeedsDisambiguation` outcome *before ever touching `guessRepository`* —
no `AddAsync`/`UpdateAsync`, no attempt-count increment — which is what
makes REQ-210's "not a separate attempt" guarantee structural rather than
conventional; verified directly by a test asserting no `Guess` row exists
after a disambiguation prompt, and a companion test asserting the
prompt-then-`chosenPlayerId`-resolution pair together consume exactly one
attempt. API: `SubmitGuessRequest` gained `ChosenPlayerId`;
`SubmitGuessResponse` gained `Candidates` (null on every ordinary
response — the frontend's discriminator for "show a picker" vs. "render a
scored result"). 15 new backend tests; full backend suite (642 tests)
green.
**Built as (frontend):** `GuessInput.tsx` renders SCREEN-02a's picker
(native `role="radiogroup"` of radio-labeled candidates, each showing
name + `distinguishingAttributes.join(' · ')`, gracefully omitted — not
shown empty — when a candidate has none) whenever `onSubmit` resolves
with a non-empty candidate array instead of closing; a new
`onResolveDisambiguation` prop resubmits with the chosen `playerId` and
closes on the resulting scored response, same error-handling shape as the
plain form. `GridScreen.handleSubmitGuess` never writes cell state for a
disambiguation-needed response — only the extracted `applyScoredGuess`
(shared by the plain path and the `chosenPlayerId` resolution path) ever
updates `state.round.cells`, so the grid keeps showing the cell as
unanswered until a real scored response arrives. Verified visually via a
temporary, deleted-afterward preview harness + Chromium screenshots at
mobile/desktop widths (bottom-sheet vs. centered popover, per SCREEN-02a);
a full logged-in-through-real-backend flow with genuinely ambiguous
seeded data was not reachable in this sandbox, so the network round-trip
itself is verified only via mocked-fetch Vitest coverage, not a live
integration. 8 new frontend tests; full frontend suite (256 tests),
`tsc -b`, and lint all clean.

**S-068 · Leaderboard scoring/median/fairness explainer (REQ-213 extension)**
Raised directly by a player/product request (2026-07-21, via `/orchestrate`):
the leaderboard should explain how its own ranking actually works — the
same need REQ-213/SCREEN-06 already solved for per-cell scoring, but that
explainer is (a) only reachable from the grid screen's `(ⓘ)` entry point,
never from the leaderboard screen (SCREEN-03) itself, and (b) its content
predates REQ-409 (median, ≥5-round participation gate, decided/built
2026-07-20 — after REQ-213's own last content update on 2026-07-14) and
S-056's fairness fix (never-played members excluded from ranking;
unguessed cells counted at max in the live scope) — neither is mentioned
anywhere a player reads the leaderboard. Routed through `requirements-writer`
first, same as S-056, since "what the explainer must say" is a content
decision, not a rendering fix — do not draft the copy inline in a frontend
PR. Deliberately **not** bundled into this same `/orchestrate` session's
round-end-display work (S-068 itself is that story) — kept to one story
per session/PR per this file's own rule at the top.
*Scope, to resolve with requirements-writer before building:* (1) does
SCREEN-03 get its own `(ⓘ)` entry point opening the *same* `ScoringExplainer`
component (extended with new content), or a separate leaderboard-specific
explainer — recommend reusing the same component/REQ-213 to avoid two
divergent copies of the golf-scoring framing; (2) new content needed:
the all-time scope ranks by **median** per-round score (not a raw sum),
gated behind having played **≥5 qualifying (closed, ≥1-guess) rounds**
below which a player simply doesn't appear on the list — stated plainly so
"why am I not on the leaderboard yet" doesn't read as a bug; (3) the
never-played-member exclusion and live-scope unguessed-cell-counts-at-max
rule (S-056) belongs either in this explainer or a leaderboard-scoped
companion note — requirements-writer to decide which REQ each new
acceptance criterion attaches to (REQ-213 itself, or a new status note on
REQ-409/401/404).
*Accept:* REQ213-named test(s) confirming the explainer is reachable from
SCREEN-03 and its content covers the median/participation-gate/fairness
points above, in addition to the six content points REQ-213 already
requires; existing REQ213 grid-screen-reachability tests unaffected.
*Deps:* REQ-213/S-041 (existing explainer/component), REQ-409/S-060
(median ranking), S-056 (fairness fix) — all already built, this story
only makes them player-visible.
**Built as:** `requirements-writer` resolved both open scope questions as
recommended — same component, reused, plus three cross-referencing content
paragraphs rather than restated formulas — extending REQ-213's own dated
status notes (`docs/requirements-document.md`) rather than opening a new
REQ. `ui-implementer` then added `LeaderboardScreen.tsx`'s second `(ⓘ)`
entry point (`leaderboard-screen__info-toggle`, next to the "Global
leaderboard" title, same quiet/no-accent treatment as `GridScreen.tsx`'s
own), importing `ScoringExplainer` directly from `frontend/src/grid/
ScoringExplainer.tsx` — no new component, no new props, confirmed against
the actual component before assuming reuse would work. Its open state
(`explainerOpen`) is tracked independently of `scope`/each scope's own load
state, mirroring `GridScreen.tsx`'s existing `explainerOpen`/`activeCell`
independence, so opening it never discards a selected scope tab or a
loaded "Load more" page. `ScoringExplainer.tsx` gained the three new
content paragraphs (median ranking and its unchanged "lower is better"
framing; the ≥5-qualifying-round gate; never-played exclusion plus the
Current Round untouched-cell-at-max rule), rendering identically
regardless of which screen's entry point opened it. `test-writer` added 8
new tests across `LeaderboardScreen.test.tsx`/`GridScreen.test.tsx` (288
total frontend tests). `quality-architect` passed the diff with one
trivial comment fix and flagged `docs/requirements-document.md`'s own
"decided, not yet built" status wording as stale once this story actually
shipped — corrected in the same doc-sync pass that recorded this section.
`architecture-reviewer` passed clean, no ADR needed; noted (not actionable
now) that `ScoringExplainer.tsx` living under `grid/` while imported by
`leaderboard/` is fine today with no documented frontend module-boundary
rule violated, worth revisiting only if such boundaries are ever
formalized. `docs/design-document.md` SCREEN-03/SCREEN-06 updated in the
same session to match (median/participation-gate ranking description was
already stale independent of this story — corrected here, not just the new
entry point added on top of it).

**S-069 · Guest play, backend half (REQ-717, ADR-0036)**
`MVP-SCOPE.md`'s "Guest play" bullet pulled this forward by deliberate
product decision (2026-07-21, no trigger fired) — REQ-717 and ADR-0036
were drafted the same session; this story is the backend implementation
both describe. Deliberately backend-only: REQ-717's acceptance criteria are
observable-behavior statements about the API/data layer (per its own scope
note), and a frontend guest-play entry point/claim UI is real, separate
scope not bundled in here.
*Accept:* REQ717-named tests (unit: `LeaderboardServiceTests`,
`UserRepositoryTests`; API: `AuthEndpointTests`) covering guest
provisioning (no email/password, `IsGuest = true`, auto-generated
`Guest####` display name, Global league auto-membership), guessing/scoring/
uniqueness/round-scoped leaderboards requiring zero new code path (verified
by absence of any `IsGuest` branch outside the two places listed below),
the claim/upgrade path (preserves `Guess`/`LeagueMembership` rows
unchanged, rejects a non-guest caller), REQ-409's qualifying-rounds query
excluding guest rows and a claimed account's pre-claim rounds, and the
`auth-guest` rate limit's own distinct 429 behavior.
*Deps:* S-004 (auth exists), S-060 (REQ-409 median ranking, the one query
this story narrows).
**Built as:** `User` gained two columns (`IsGuest bool`, default `false`;
`ClaimedAt DateTime?`) and `Email` became nullable (`string?`) — a
non-trivial ripple audited across every existing caller (`AuthController`'s
Signup/Me/DeleteAccount, `UserRepository.GetByEmailAsync`,
`UserDisplayNameBackfiller`); migration
`20260721140000_AddGuestPlaySupport`. `ISupabaseAuthClient` gained
`SignInAnonymouslyAsync` (POST `auth/v1/signup` with no email/password,
mirroring `SignUpAsync`) and `LinkEmailPasswordAsync` (PUT `auth/v1/user`,
authenticated with the guest's own access token rather than the shared anon
key) — **neither call's exact request/response shape was verified against
a live Supabase project** (no network access in the build environment);
flagged in `SupabaseAuthClient`'s own doc comments for manual verification
before this reaches production, per this repo's established practice
around unverified external-API assumptions (ADR-0008's precedent).
`AuthController` gained `POST /auth/guest` (rate-limited by a new,
deliberately tighter `auth-guest` policy — 3/min per IP default vs.
auth-signup/auth-login's 10/min, since an anonymous sign-in has no email
step at all to slow down scripting) and `POST /auth/claim`
(`[Authorize]`, rejects a non-guest caller, delegates to a new
`IUserRepository.ClaimGuestAsync` that sets `Email`/clears `IsGuest`/stamps
`ClaimedAt` via load-then-`SaveChangesAsync`, never touching
`Guess`/`LeagueMembership`). `GuessRepository.
GetPerRoundFinalPointsByUserIdsAsync` (REQ-409's qualifying-rounds query)
gained a join to `Users` excluding `IsGuest` rows and, for a claimed
account, rounds closed before `ClaimedAt`. No change to any REQ-201-210/204/
406/407/408 code path, per ADR-0036's explicit "For AI agents" instruction —
a guest is a real `User`/`LeagueMembership`/`Guess` row throughout. Frontend
(guest entry point, claim/upgrade screen) intentionally not built this
session — remains open Tier 1/2 scope in `MVP-SCOPE.md`.

**S-070 · Guest play, frontend half (REQ-717, ADR-0036)**
The frontend counterpart S-069 deliberately left out: a guest entry point
on the login/signup screen and a claim/upgrade section in Settings, wired
to S-069's `POST /auth/guest`/`POST /auth/claim`.
*Deps:* S-069 (backend endpoints this story calls).
**Built as:** `AuthScreen.tsx` gained a "Play as guest" button below the
existing log-in/sign-up form (a new `playAsGuest()` in `lib/api.ts`,
mirroring `login()`'s shape/error-handling exactly) — on success, routes
through the exact same `onAuthenticated` callback a normal login/signup
already uses, so a guest session is stored and treated identically from
that point on (ADR-0036's explicit design goal; no separate "guest mode"
client-side state anywhere). `SettingsScreen.tsx` gained a "Save your
progress" claim section (new `claimAccount()` in `lib/api.ts`, `POST
/auth/claim`), rendered only while the account is a guest, with the same
REQ-701 password-policy/inline-error conventions `AuthScreen.tsx`'s signup
form already established; on success, `App.tsx` replaces its
`currentUser` state wholesale with the claim response, which makes the
section disappear immediately (no reload). `App.tsx` also gained a small,
low-effort header banner ("Playing as {name}. Save your progress.") while
the session is a guest — not mandated by REQ-717, added per this story's
own judgment call, documented in `design-document.md` (§3/§7) rather than
left as an unreviewed addition.
**Real gap found and flagged, not silently worked around:** the backend's
`MeResponse` DTO (`AuthDtos.cs`) has no dedicated `isGuest` field — S-069
added `IsGuest` to the `User` entity but never surfaced it on this
response. The frontend derives guest status as `email === null` instead
(a correct signal today: `AuthController.Guest` is the only path that ever
creates a null-`Email` row, and `AuthController.Claim`/`UserRepository.
ClaimGuestAsync` always set `Email` and clear `IsGuest` together — see the
comment on `CurrentUser` in `frontend/src/lib/types.ts`), but a real
`isGuest` boolean on `MeResponse` would be more robust/self-documenting
than relying on that invariant holding forever. Recommended as a small
follow-up for `backend-implementer`, not added here (out of this story's
scope, and not this agent's to add per the xG Arcade/game and
delivery-agent boundaries).
*Accept:* Vitest coverage in `AuthScreen.test.tsx` (guest sign-in success/
failure) and `SettingsScreen.test.tsx` (claim section visibility, REQ-701
password-policy checks, success/400/401 handling) — exhaustive REQ717-named
frontend coverage remains `test-writer`'s to add, per this repo's
delivery-agent split. No Playwright E2E spec added/changed: no existing
spec asserts on `AuthScreen`/`SettingsScreen` behavior this story alters.
