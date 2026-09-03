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
>
> **Token efficiency (2026-08-17):** high priority alongside correctness —
> spend tokens on the work, not on re-deriving context a story already
> paid for. A well-scoped story already names its exact files, line
> numbers, and root causes from the investigation that produced it; when
> turning a story into a session prompt (`/orchestrate` or a direct
> session), hand the implementing agent those specifics directly instead
> of re-opening a broad codebase exploration the story text already
> closed. Keep a session's scope to exactly what its own story names —
> don't fold in adjacent "while we're in there" cleanup that pulls in
> files or context outside it (file a follow-up story instead). Prefer a
> targeted `Edit` over reading and rewriting a whole file. When a story's
> own investigation is incomplete or a root cause is still unconfirmed,
> say so in the story rather than sending an agent to re-discover it from
> scratch. This applies to every story above and below, not just the ones
> written on this date.

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

**Follow-up (2026-07-28, REQ-110):** three consecutive `warm-player-cache.yml`
runs produced byte-identical summaries ("2064 pairs checked, 1214 queried
live, 850 already valid"), diagnosed from the workflow's own CI logs. Most
of it is the already-accepted "below-threshold pairs re-queried every run"
gap from this entry, not new. But one run alone had 133 of 1214 live
queries (11%) end in a `WikidataClient` technical failure (WDQS timeout,
HTTP error, or parse error) silently swallowed and returned as an empty
match list — indistinguishable in `CacheWarmingResult` from a genuine
zero-match answer. REQ-110 amended to require the run summary to report a
technical-failure count (distinct from `PairsQueriedLive`) and list the
specific failing pairs, so an operator can tell "genuinely below
`MinValidAnswers`" apart from "worth re-running." Does not change the
accepted re-query gap, and does not change `WikidataClient`'s fail-open
contract for REQ-103/REQ-211 (ADR-0046) — cache-warming's own
summary/observability only. Not yet implemented — flagged for
`backend-implementer`.

**Resolved same day.** Implemented as described:
`CacheWarmingResult.PairsWithTechnicalFailure`/`FailingPairs`, threaded
via a new `onTechnicalFailure` callback on `IWikidataClient`/
`IWikidataLookupService`. See `docs/requirements-document.md`'s REQ-110
"Extended (2026-07-28)" text and `PlayerCacheWarmingServiceTests.cs` for
the shipped behavior and its regression tests.

**Further follow-up (2026-07-28, REQ-110):** the technical-failure
visibility work above diagnosed but didn't fix the two root causes of
"zero net cache expansion, byte-identical summaries." REQ-110 amended
with two more acceptance-criteria blocks, both flagged for
`backend-implementer`, neither implemented yet: (1) cache warming's
sync-path queries currently share round generation's 15s
`_queryTimeout` (`WikidataLookupOrigin.Sync`) even though nobody is
waiting synchronously on this unattended CLI job — a
cache-warming-specific, longer timeout (same class of fix as ADR-0046's
28s guess-time-fallback precedent, exact value left to
`backend-implementer`) plus a same-run retry on a technical failure,
without touching REQ-103's 15s or ADR-0046's 28s; (2) the 1207-of-1214
"genuinely below `MinValidAnswers`, not a failure" pairs from that same
diagnosis are re-queried on every run for zero benefit — REQ-110 now
requires a persisted "confirmed checked, genuinely low, as of this
reference-data/query-shape state" signal, explicitly required to be
respected (i.e. bypassed/reset) by REQ-111's stale-QID cleanup and
REQ-112/S-038's `purge-player-pool` so a purge-and-rewarm cycle stays a
real full re-check. **Flagged for a new ADR** (new persisted state, a
real "could have gone another way" choice on mechanism) — not decided
here, routed separately per the user's request.

**Resolved same day.** Implemented as described:
`WikidataQueryTimeoutTier.CacheWarming` (45s) plus
`PlayerCacheWarmingService.LookupWithSameRunRetryAsync` (2 attempts) for
part (1); the new `ConfirmedLowMatchPair` table
(`IPlayerStoreRepository.IsConfirmedLowAsync`/`RecordConfirmedLowAsync`),
invalidated by `StaleClubAttributeCleaner` and `purge-player-pool`, for
part (2). The flagged ADR is
`docs/decisions/0050-confirmed-low-match-pair-persistence.md` (corrected
2026-08-01: this note previously said `0049`, stale from before that ADR
was renumbered — see this file's own 2026-07-28 renumbering entry in
`docs/CHANGELOG.md`; no content change, only the reference).

**Further follow-up (2026-08-01, REQ-110) — the same-run retry from part
(1) above was itself a regression.** `warm-player-cache.yml` run #15 was
manually re-dispatched three times (2026-07-28 through 2026-08-01) and
every attempt got cancelled at the workflow's 90-minute CI ceiling,
never completing — on top of CI logs that had become unreadable
(thousands of per-pair `Warning`-level lines, some 15-20 line stack
traces). Root cause: the same-run retry doubled every technical failure's
cost (up to 2 × the 45s cache-warming timeout), and nothing persisted a
failure across runs, so the same pairs got retried at that doubled cost
on every future run forever. A specific, confirmed structural cause was
also found: `WikidataClient.BuildClubClubIntersectionQuery`'s plain join
on two independent P54 statement-path patterns could multiply result rows
by (statements at club A) × (statements at club B) per player — one real
case returned 250,000+ WDQS binding rows for two clubs with a large,
overlapping squad. See NOTES.md's 2026-08-01 entry for the full diagnosis
narrative. Flagged for a new ADR (removing an existing retry mechanism and
adding new persisted cross-run state are both "could have gone another
way" choices) — not decided here.

**Resolved same day.** Implemented as described: `LookupWithSameRunRetryAsync`/
`MaxAttemptsPerPair` removed from `PlayerCacheWarmingService` — each pair
attempted exactly once per run; new `PairLookupFailure` table
(`IPlayerStoreRepository.IsPersistentTechnicalFailureAsync`/
`RecordTechnicalFailureAsync`/`ClearTechnicalFailureAsync`), same
invalidation surface as `ConfirmedLowMatchPair`, so a pair failing 2
consecutive runs is skipped (no live query) on the third
(`CacheWarmingResult.PairsSkippedPersistentFailure`); `BuildClubClubIntersectionQuery`
now wraps each club's P54 match in its own `FILTER EXISTS { }` block
instead of a plain join; `WikidataClient`'s two per-pair failure logs
moved from `Warning` to `Debug`. The flagged ADR is
`docs/decisions/0052-pair-lookup-failure-persistence-and-club-club-query-fix.md`.

**Further follow-up (2026-08-01) — the first real runs under ADR-0052's
tracking exposed a missing recovery path.** `warm-player-cache.yml` runs
correctly identified 125 Club×Club pairs as persistent, structural
technical failures and stopped retrying them — but there was no way to
clear a `PairLookupFailure` marker without reaching for
`clean-stale-club-attributes`, whose scope is every pair touching a named
club on either side. Since the 125 stuck pairs collectively touched all
32 seeded clubs, using that tool would have wiped roughly 850 other
pairs' worth of good cached `PlayerAttribute`/`PlayerData` data just to
clear 125 broken failure markers. *Resolved same session:* added
`PairLookupFailureCleaner` (`XGArcade.Data.Seeding`) and its
`clear-pair-lookup-failures` CLI verb (`Program.cs`,
`.github/workflows/clear-pair-lookup-failures.yml`) — reads
`PairLookupFailure` directly for every row at/above
`PersistentFailureThreshold` and removes only those rows, touching no
other table. Same entity, narrower scope than ADR-0052's existing
invalidation surface — but ADR-0052 itself explicitly names
`StaleClubAttributeCleaner`/`purge-player-pool` as the *only* two paths and
says not to add a third without updating it, so this required amending
ADR-0052 in place (a dated status note), not a new ADR number.
`architecture-reviewer` caught the doc claiming otherwise before this was
considered done.

**Follow-up (2026-08-29, ADR-0089):** the "Ran out of candidates before
completing the grid" incident that opened this entry (2026-07-13) is now
fixed at its structural root, not just mitigated. This entry's own fixes
(wider reference pool, proactive cache warming, confirmed-low/technical-
failure tracking) reduced how often an unlucky row-header set exhausted
the column pool, but never removed the underlying fragility: every row
header was still forced to share one homogeneous category type, fixed for
the whole instance and never reconsidered. ADR-0089 replaces
`GridGenerationService.SelectPairing`'s per-instance pairing choice with
each row/column header independently picking its own category type from
one combined Country/Club/Trophy pool, so a data-sparse header of one type
no longer dooms the whole generation attempt when a different type would
have worked. `MinValidAnswers` (S-014's playtested value) is unaffected.
See `docs/decisions/0089-grid-per-header-category-mixing.md` for the full
decision and rejected alternatives, and REQ-107's own 2026-08-29 status
note for the requirements-level description.

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
~~T-104 disambiguation UI~~ (built, see S-067) ·
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
**Real gap found and flagged, not silently worked around (closed same day
— see follow-up below):** the backend's `MeResponse` DTO (`AuthDtos.cs`)
had no dedicated `isGuest` field — S-069 added `IsGuest` to the `User`
entity but never surfaced it on this response. The frontend derived guest
status as `email === null` instead (a correct signal at the time:
`AuthController.Guest` is the only path that ever creates a null-`Email`
row, and `AuthController.Claim`/`UserRepository.ClaimGuestAsync` always
set `Email` and clear `IsGuest` together), but a real `isGuest` boolean on
`MeResponse` was recommended as more robust/self-documenting than relying
on that invariant holding forever. Recommended as a small follow-up for
`backend-implementer`, not added in this story (out of its scope, and not
this agent's to add per the xG Arcade/game and delivery-agent boundaries).
*Accept:* Vitest coverage in `AuthScreen.test.tsx` (guest sign-in success/
failure) and `SettingsScreen.test.tsx` (claim section visibility, REQ-701
password-policy checks, success/400/401 handling) — exhaustive REQ717-named
frontend coverage remains `test-writer`'s to add, per this repo's
delivery-agent split. No Playwright E2E spec added/changed: no existing
spec asserts on `AuthScreen`/`SettingsScreen` behavior this story alters.

**Follow-up (2026-07-21, same day, REQ-717):** `backend-implementer` added
`MeResponse.IsGuest` (mirrors `User.IsGuest` directly), and this story's
own frontend was switched over to it — `CurrentUser.isGuest` in
`frontend/src/lib/types.ts`, consumed by `App.tsx`/`SettingsScreen.tsx` —
removing the `email === null` inference entirely. `test-writer` then added
the remaining REQ717-named coverage this story's *Accept* left open
(uniqueness-denominator counting, REQ-409's exact-`ClaimedAt` cutoff and
post-claim-only 5-round floor, explicit REQ-406/407/408 participation,
guess-attempt-limit parity, `DeleteAccount`'s guest-rejection branch, and
the header banner's show/hide/disappears-after-claim behavior in
`App.test.tsx`) across `AuthEndpointTests.cs`, `LeaderboardServiceTests.cs`,
`RoundCloseServiceScoringTests.cs`, `GuessSubmissionServiceTests.cs`, and
`App.test.tsx`. A `quality-architect` pass then gave
`GenerateUniqueGuestDisplayNameAsync` an optional `Random` seam (same
pattern `GridGameModule` already uses) so the collision-retry branch could
be tested deterministically, extracted `SupabaseAuthClient`'s duplicated
error-parsing into one shared helper, and merged a near-duplicate
guest-guess-seeding test helper into the existing one.

**S-071 · Guest-play captcha hardening (REQ-717's "Bot-check (captcha)"
addition, ADR-0037)**
A same-session follow-up to S-069/S-070: Supabase's own dashboard warns
that enabling Anonymous Sign-ins without a captcha invites abuse, and a
per-IP rate limit alone (S-069's `auth-guest` policy) is weaker against a
distributed/multi-IP scripted attacker than a captcha check. This story
adds Cloudflare Turnstile as a second, complementary layer scoped to
`POST /auth/guest` only — never `auth-signup`/`auth-login`.
*Deps:* S-069 (backend guest endpoint this wraps), S-070 (frontend guest
entry point this instruments).
**Built as:** Backend — `GuestRequest.CaptchaToken` threaded through
`ISupabaseAuthClient.SignInAnonymouslyAsync` to Supabase's
`gotrue_meta_security.captcha_token` field; a new
`SupabaseAuthResult.IsCaptchaRejection` signal (parsed from Supabase's
`error_code`/message on a failed anonymous sign-in) lets
`AuthController.Guest` return a distinct `"Captcha verification failed"`
(400) instead of the existing generic `"Guest sign-in failed"` (500).
Frontend — new `frontend/src/lib/turnstile.ts`, a promise-based wrapper
(`getTurnstileToken()`/`resetTurnstileWidget()`) that lazily loads
Cloudflare's script once, renders the invisible/managed widget (REQ-717's
recommended mode), and dedupes concurrent in-flight calls to the same
promise (race-condition fix from a same-day `quality-architect` pass,
`6f267a4`) rather than tearing down a still-awaited widget out from under
itself. `lib/api.ts`'s `playAsGuest()` now sends `{ captchaToken }` as
`POST /auth/guest`'s body; `AuthScreen.tsx`'s `handlePlayAsGuest` obtains a
token before ever calling `playAsGuest()`, and resets the widget only when
the caught error's `title === 'Captcha verification failed'` — any other
guest-sign-in failure shows the existing generic inline error with no
widget reset. `infra/README.md`/`deploy.yml` gained
`DEV_TURNSTILE_SITE_KEY`/`PROD_TURNSTILE_SITE_KEY`, wired into
`deploy-frontend`'s `VITE_TURNSTILE_SITE_KEY` the same way
`VITE_API_BASE_URL` already is. `SETUP.md` gained a new step 5 (enabling
Supabase's Anonymous Sign-ins toggle itself, a pre-existing undocumented
precondition this story's own doc-sync pass surfaced) and step 6 (the
Cloudflare Turnstile site + Supabase Auth captcha-settings manual setup).
No independent verification against a live Supabase project was possible
(no network access in this environment) — `gotrue_meta_security
.captcha_token` and its `error_code`/message shape on rejection are
recorded from documentation/training knowledge, flagged in
`SupabaseAuthClient`'s doc comments for manual verification before
production, the same caveat ADR-0036's own calls already carry.
*Accept:* REQ717-named captcha tests across `AuthEndpointTests.cs` (the
distinct 400 response, rate-limit rejection short-circuiting before the
captcha check ever runs, a scope regression proving
`IsCaptchaRejection` never fires for Login/Signup/Refresh/Claim) and a new
`SupabaseAuthClientCaptchaTests.cs` (`error_code` vs. message-substring
captcha-detection paths, against a real `SupabaseAuthClient` and a fake
HTTP handler — not just the stubbed `ISupabaseAuthClient` the endpoint
tests use); `turnstile.test.ts` (script-load-once, widget render/teardown,
reset-forces-fresh-render, concurrent-call deduping, script/Turnstile
error rejection, all against a fake `window.turnstile`) and
`AuthScreen.test.tsx`/`App.test.tsx` coverage (token sent in the request
body, the distinct rejection resets the widget and shows its detail text,
a generic failure does not reset the widget, a token-acquisition failure
never calls `POST /auth/guest` at all). 314 frontend tests total as of the
final commit (`6f267a4`). `quality-architect` passed the diff after the
one fix listed above (the `getTurnstileToken()` race condition); no
architecture/component-boundary change beyond ADR-0037's own scope, no new
ADR needed on top of it.

**S-072 · Guest account lifecycle cleanup (REQ-718, ADR-0038)**
REQ-718/ADR-0038 were drafted the same session (2026-07-25); this story is
the implementation both describe: delete an unclaimed guest at logout,
purge unclaimed guests after 30 days, purge inactive guests after 7 days —
all three reusing S-025's `IAccountDeletionService` unmodified, never a
second deletion path.
*Deps:* S-025 (`IAccountDeletionService`), S-069/S-070 (guest play,
`IsGuest`/`ClaimedAt`), S-008 (`/internal/generate-round`'s bearer-token
pattern this reuses for the new scheduled endpoint).
*Accept:* REQ718-named tests (unit: `LastActiveAt` set at creation and
updated on login/guest-creation/claim/guess-submission and on no other
request; the 30-day-unclaimed and 7-day-inactive selection queries select
exactly the rows their own definitions require, including a claimed
account never matching either regardless of age; API: logging out an
unclaimed guest deletes the account and a subsequent request with that
token is rejected, logging out a claimed account deletes nothing;
integration: the scheduled purge run against seeded
unclaimed/inactive/claimed/active rows purges only what the two rules
require) — added by `test-writer` in a follow-up pass, not written here.
**Built as:** `User` gained a third column, `LastActiveAt` (non-nullable
`DateTime`, migration `20260725120000_AddUserLastActiveAt` — added
nullable, backfilled from each row's own `CreatedAt` via raw SQL, then
tightened to `NOT NULL`, since a per-row backfill can't be expressed via
`AddColumn`'s single fixed `defaultValue`). Set inline at insert
(Signup/Guest, alongside `CreatedAt`), folded into
`UserRepository.ClaimGuestAsync`'s existing write (Claim), and updated via
a new `IUserRepository.UpdateLastActiveAtAsync` (Login, resolved by
`AuthProviderUserId`; a submitted guess in `GuessEndpoints`, updated for
every outcome — accepted, disambiguation, or rejected — since all still
mean the account genuinely engaged with an active round) — no `IsGuest`
branch in any of these four paths, per ADR-0038's explicit instruction.
`AuthController` gained `POST /auth/logout` ([Authorize]) — this system's
first backend logout call ever (REQ-715's logout was, until now, entirely
client-side): for an unclaimed guest, calls
`IAccountDeletionService.DeleteAccountAsync` and always responds `204`
regardless of outcome (best-effort; failures are logged, not surfaced).
New `XGArcade.Api.Auth.InternalGuestCleanupEndpoints` maps
`POST /internal/purge-guest-accounts` (bearer-token-protected) — two new
`IUserRepository` queries
(`GetUnclaimedGuestsOlderThanAsync`/`GetInactiveGuestsOlderThanAsync`)
select each rule's rows, deduped by `User.Id` before calling
`IAccountDeletionService.DeleteAccountAsync` once per account, returning a
small typed response with each rule's match count and the total deleted.
The existing `/internal/generate-round` bearer-token
constant-time-compare check was extracted from `InternalRoundEndpoints`
into a new, shared `XGArcade.Api.Internal.InternalJobAuthorization` static
helper, used by both endpoints, rather than hand-duplicated for the new
one. New `purge-guest-accounts.yml` (daily, 07:00 UTC — offset one hour
from `generate-round.yml`'s 06:00) follows that workflow's exact
curl/bearer-token/fail-on->=400 shape. Frontend: `lib/api.ts` gained
`logout(accessToken)` (POST, best-effort — a caller failure is expected to
be caught, not thrown further); `App.tsx`'s `handleLogout` captures the
current `accessToken` before clearing local state, then fires `logout()`
without awaiting it, so a slow/failing network call never delays or blocks
REQ-715's existing instant local logout (the `useCallback` now depends on
`accessToken`, since the token must be read before it's cleared).
`docs/legal/privacy-policy-draft.md` updated (new `LastActiveAt` tracking
disclosure, automatic guest-removal rules) per CLAUDE.md's legal-drafts
rule. **Not run against a live `dotnet build`/`dotnet test`** — no `.NET`
SDK available in the build environment; hand-traced against REQ-718's own
Given/When/Then and against every existing call site the new
`IUserRepository`/`AuthController` members touch. No new Wikidata QIDs
introduced. `test-writer` to add the REQ718-named unit/API/integration
coverage listed under *Accept* above in a follow-up pass; `architecture-
reviewer`/`quality-architect` review still pending as of this entry.

**S-073 · Admin guest/user metrics view; admin bulk force-clear guest
accounts (REQ-507/508)**
No pre-written story text exists for this one — unlike most entries above,
REQ-507 and REQ-508 were scoped and implemented directly in the same
session, and this entry is being added retroactively (by `doc-sync`) to
give them a backlog story the way every other implemented REQ has one.
REQ-507 (live admin-facing counts: total users, current guests, claimed
guests) and REQ-508 (an immediate, admin-triggered bulk delete of every
current guest account, with a dry-run count and a two-step confirm) were
both drafted and implemented together, and both explicitly reuse
`IAccountDeletionService` per ADR-0038's mandate that "any future admin
path" delete a guest account only through that service — no second
deletion path was written.
*Deps:* S-025 (`IAccountDeletionService`), S-026 (`AdminManagementEndpoints`
precedent this new file deliberately does *not* share environment-gating
with — see "Built as" below), S-069/S-070 (`IsGuest`/`ClaimedAt`), S-072
(`LastActiveAt`/the age-filtered purge queries this story's new query
deliberately does not reuse).
*Accept:* REQ507-named tests: the metrics endpoint returns a live
(non-cached) total user count, current guest count, and claimed-guest
count that always agree with the `IsGuest`/`ClaimedAt` invariant; a
non-admin caller gets 403. REQ508-named tests: the dry-run count endpoint
returns the exact current `IsGuest = true` count; the clear action deletes
every currently-matching guest via `IAccountDeletionService`, leaves
claimed accounts untouched, reports a per-account
Succeeded/NotFound/Failed outcome rather than one all-or-nothing result,
and remains reachable in a Production-configured test host (unlike
REQ-505/506); a non-admin caller gets 403 for both endpoints.
**Built as:** new `XGArcade.Api.Admin.AdminAccountsEndpoints`
(`GET /admin/accounts/metrics`, `GET /admin/accounts/guests/count`,
`POST /admin/accounts/guests/clear`), Admin policy, registered
unconditionally in `Program.cs` — including Production — unlike the
non-Production-only `AdminManagementEndpoints` (REQ-505/506, S-026), since
both REQs' own scope notes are explicit these act on real account data as
their stated purpose. Four new `IUserRepository` methods
(`CountUsersAsync`/`CountGuestsAsync`/`CountClaimedGuestsAsync`/
`GetAllGuestIdsAsync`), each a single query, no in-memory materialization.
`GetAllGuestIdsAsync` is a deliberately new, unfiltered query rather than a
relaxed form of S-072's `GetUnclaimedGuestsOlderThanAsync`/
`GetInactiveGuestsOlderThanAsync` — REQ-508 explicitly applies no age/
inactivity filter. The bulk clear endpoint calls
`IAccountDeletionService.DeleteAccountAsync` once per selected guest id
(sequentially, not `Task.WhenAll`, since all three metrics queries and the
delete loop share one request-scoped `DbContext`, and EF Core doesn't
support concurrent use of one context instance); a new
`AccountDeletionService.UserNotFoundErrorMessage` const (extracted, no
behavior change) lets the endpoint tell "no longer exists" apart from any
other failure without a second existence check, and a `Failed` outcome is
logged with the target user id. Backend tests:
`backend/tests/XGArcade.Data.Tests/UserRepositoryTests.cs` (extended) and
new `backend/tests/XGArcade.Api.Tests/AdminAccountsEndpointTests.cs` — not
independently run against a live `dotnet test` in this build environment
(no .NET SDK available); hand-traced against REQ-507/508's own
Given/When/Then instead. Frontend (`AccountMetricsSection`/
`GuestClearSection` in `AdminScreen.tsx`, SCREEN-04) was built in the same
session by `ui-implementer` and is not re-described here — see
`docs/design-document.md`'s SCREEN-04 "Accounts / guest-clear" subsection
and its own judgment-call list (its first draft referenced this story as
"S-076" by mistake; corrected to S-073 in this same doc-sync pass).
`architecture-reviewer`/`quality-architect` already reviewed this diff and
found no blocking issues; no new ADR was written — this reuses ADR-0038's
existing mandate and REQ-507/508's own already-accepted scope, not a new
structural decision.

**S-074 · Pre-login splash/landing screen (REQ-719)**
Direct product request (2026-07-25): the app landed straight on the
login/signup form for an unauthenticated visitor, with no landing page
at all — the product owner wanted something shown first, with an
explicit action into login, not the form itself as the first thing
anyone sees. Routed through `requirements-writer` first since no REQ
existed for this behavior.
*Deps:* none new — reads only the existing `accessToken` state and
`AuthScreen`/`handleLogout` already in `frontend/src/App.tsx`.
*Accept:* REQ719-named tests: an unauthenticated render (first visit,
reload, or a return from logout/account-deletion/a failed silent
refresh) shows the splash screen, not `AuthScreen`, every time — no
persisted "already seen it" flag; a single, unambiguous CTA on the
splash screen reaches `AuthScreen`; a successful login/signup from there
still lands on the game-selection screen exactly as REQ-303/S-021
already defines, unchanged.
**Built as:** new `frontend/src/splash/SplashScreen.tsx` (+`.css`,
`.test.tsx`) — an `<h1>` for "xG Arcade", a one-line tagline, and a
single primary button ("Log in or sign up"), styled entirely from
existing `docs/design-document.md` §2 tokens; no new color/typeface/
animation, and deliberately no logo/brand-mark image (out of scope for
this REQ, tracked as separate design work). `App.tsx` gained a new
`showAuthScreen` boolean (starts `false` every mount, reset to `false`
by `handleLogout` — which is also what account-deletion and a failed/
absent silent-refresh outcome funnel through — so all three land back
on the splash screen, never straight to `AuthScreen`) gating which of
`SplashScreen`/`AuthScreen` renders when there's no `accessToken`.
`data-testid="splash-screen"` was added since the header already renders
its own "xG Arcade" `<h1>` whenever unauthenticated, making a plain
role/name query for the splash screen's heading ambiguous. Existing E2E
helpers in `header-nav.spec.ts`/`play-grid.spec.ts` that assumed a fresh
`page.goto('/')` landed directly on `AuthScreen` were updated to click
through the new CTA first; a new `frontend/tests/e2e/splash-screen.spec.ts`
covers the full journey (splash → CTA → signup → logout → back to splash
→ log back in). `docs/design-document.md` flags `SplashScreen` alongside
`AuthScreen`/`GameSelectScreen` as another built-but-unspec'd screen (§7),
version 0.48 → 0.49. `architecture-reviewer`: pass, no ADR (pure frontend
render-state addition, no new component boundary or data flow).
`quality-architect`: pass — one stale comment in `App.tsx` (still said
"lands back on AuthScreen" after an account deletion) fixed to reference
the splash screen instead; an unrelated pre-existing flaky test
(`AdminScreen.test.tsx`'s REQ-507 case, order-dependent under a full
`npm run test` run) noted in `NOTES.md` rather than fixed here, since it
isn't a regression from this change. 341 Vitest tests pass, `tsc -b` and
`oxlint` clean; Playwright E2E not run in this sandbox (no `dotnet` to
build/run the backend the E2E tests require) — CI-only, per existing
project convention.

**S-075 · "Games" nav entry and URL-reflected navigation (REQ-720/721,
ADR-0039)**
Direct product request (2026-07-25): the product owner said more games
are coming soon and wanted a "Games" entry in the header nav listing them
(anticipating growth beyond xG Grid), and separately asked whether the
current screen could be reflected in the URL (`/` or `#`) so a refresh
doesn't always bounce back to the landing screen. Routed through
`requirements-writer` first since neither REQ existed; REQ-721's
implementation approach (hash vs. path, library vs. hand-rolled) was
routed through `architecture-reviewer` for a recommendation, then
recorded as ADR-0039 before implementation started.
*Deps:* REQ-719/S-074 (the splash screen REQ-721 must never let a URL
bypass), REQ-303/S-021 (post-login game-select landing, unchanged), S-029
(the nav simplification REQ-720 deliberately reverses), ADR-0030/REQ-712
(mobile hamburger collapse REQ-720's nested disclosure must not regress).
*Accept:* REQ720-named tests: "Games" toggles independently of REQ-712's
outer toggle and never itself navigates; selecting "xG Grid" reaches the
grid screen and closes both menus; `aria-current` while the grid screen is
showing; the "xG Arcade" title still reaches `GameSelectScreen` unchanged;
no nav-row wrap/overflow regression at any viewport. REQ721-named tests:
every one of the six `Screen` values gets a distinct, reload-restorable
hash; an unauthenticated reload always shows the splash screen regardless
of what URL was requested; a fresh login/signup always lands on
game-select regardless of the URL present beforehand; no browser
back/forward guarantee is made (explicitly out of scope).
**Built as:** `frontend/src/nav/HeaderNav.tsx`/`.css` gained a nested,
independently-toggled "Games" disclosure (its own `aria-expanded`/
`gamesOpen` state, cascading closed when the outer mobile menu closes)
listing "xG Grid"; `App.tsx` passes through `isGridCurrent`/`onSelectGrid`.
`App.tsx` gained `SCREEN_HASHES`/`HASH_TO_SCREEN`/`screenForHash()` (a
`Screen`-to-hash lookup table and its inverse) and a `navigateTo()` helper
now used at every navigation call site instead of raw `setScreen`; the
initial `screen` state reads `location.hash` only when an access token is
already present at mount (never for an unauthenticated visitor, so
REQ-719's splash gate can't be bypassed by a URL); `handleAuthenticated`
still calls `navigateTo('game-select')` unconditionally (REQ-303
unchanged); `handleLogout` clears `location.hash` rather than writing one.
No `react-router`, no `popstate`/`hashchange` listener — exactly ADR-0039's
hash-based, hand-rolled decision, chosen over path-based URLs because the
frontend's Azure Static Web App host has no SPA-fallback configured today
(and Playwright E2E, which runs against the Vite dev server, would never
have caught that gap), and over a routing library because REQ-721
explicitly excludes browser back/forward — the library's main value-add —
against a flat 6-value `Screen` union with no nesting or params.
`docs/design-document.md`'s SCREEN-07 was rewritten for the new nested
disclosure (also fixing a pre-existing gap where the "Leagues" nav entry
had never been documented there), version 0.49 → 0.50.
`architecture-reviewer`: pass, no drift, full ADR-0039 compliance
confirmed (no router dependency added, no `popstate`/`hashchange`
listener). `quality-architect`: pass; one medium finding (REQ-721's
`grid`/`leaderboard`/`admin` hashes had no test assertion) closed by
`test-writer` in a follow-up pass (353 → 359 Vitest tests). `tsc -b` and
`oxlint` clean throughout; Playwright E2E not run in this sandbox (no
`dotnet` available to run the backend the E2E tests require) — CI-only,
consistent with prior sessions.

## Epic 6 — xG Path (second game)

Design-only work (`docs/decisions/0040-0043`, `requirements-document.md`
REQ-1201-1206/REQ-410, `design-document.md` SCREEN-09/10) turned into a
concrete build sequence, same house rules as every epic above: one story
per session/PR, top to bottom, no story depends on a later one, every
story leaves the system deployable and testable. The first three stories
are shared-infrastructure refactors surfaced *by* planning xG Path but not
specific to it — they touch already-shipped xG Grid code, carry real
regression risk to it, and are ordered first deliberately so that risk is
validated before any new game logic is built on top of it.

**S-076 · Core.Scoring resolves a scoring strategy per GameKey (ADR-0040)**
Foundational refactor, no new game yet and no behavior change for xG Grid.
`ScoreLockingService.LockRoundScoresAsync` currently calls
`UniquenessCalculator.Calculate`/`ScoringRules.PointsFromUniqueScore`
directly for every correct guess, regardless of `GameKey`. Introduce
`IScoringStrategy` (computes `FinalUniquenessScore`/`FinalPoints` for a
cell's correct guesses) and `IScoringStrategyResolver` (resolves a
strategy by `GameKey`, mirroring `IGameModuleResolver`'s existing shape
exactly). Extract xG Grid's existing formula into
`UniquenessScoringStrategy` — a pure wrap of the current
calculator/rules calls, not a formula change. `ScoreLockingService` calls
the resolved strategy instead of the calculator/rules directly.
`MaterializeUnansweredCellsAsync`'s unanswered-cell penalty is unaffected
(runs before any strategy is consulted, stays strategy-agnostic).
*Accept:* every existing REQ-204/205-named test still passes unmodified
(this is an extraction, not a behavior change); a new test confirms
`IScoringStrategyResolver` resolves `"xg-grid"` to `UniquenessScoringStrategy`
and throws/fails loudly for an unregistered `GameKey` rather than
silently defaulting to it. *Deps:* S-011/S-018/S-022/S-028 (the existing
scoring code being extracted).

**S-077 · Guess attempt cap resolved per-cell via `IGameModule` (ADR-0041)**
Foundational refactor, no behavior change for xG Grid. `GuessRules
.MaxAttemptsPerCell` is a single `const int = 2`, read directly by
`GuessSubmissionService`, `LiveRoundContributionService`, and
`RoundEndpoints`. `IGameModule` gains a method resolving a given cell's
own max-attempts value (same resolution shape `GetCellIdsAsync` already
uses). All three call sites read through it instead of the constant.
`GridGameModule`'s implementation returns `2` unconditionally — identical
behavior to today. Once every call site is migrated,
`GuessRules.MaxAttemptsPerCell` is deleted outright, not left as unused
dead code.
*Accept:* every existing REQ-210-named test still passes unmodified; a
new test confirms no call site references a hardcoded `2` anymore (all
three resolve through `IGameModule`); confirm-by-inspection that
`GuessRules.MaxAttemptsPerCell` no longer exists in the codebase.
*Deps:* S-009/S-010 (the existing attempt-cap code being extracted). No
dependency on S-076 — different interface, either order works.

**S-078 · Global League leaderboard scoped per GameKey (ADR-0043/REQ-410)**
Independent of the rest of this epic — motivated by xG Path but not
blocked by it, and nothing else in this epic depends on it except S-087.
`GetGlobalLeaderboardAsync` and `IGuessRepository
.GetPerRoundFinalPointsByUserIdsAsync` gain a required `gameKey`
parameter (`GetPerRoundFinalPointsByUserIdsAsync`'s existing `Guess`-
`Round` join gains a `round.GameKey == gameKey` filter — no schema
change). `LeaderboardEndpoints`'s route passes `"xg-grid"` explicitly
today (behavior for the only current game is unchanged) rather than the
previous implicit "every round regardless of game" query.
*Accept:* every existing REQ-409-named test still passes, now supplying
`"xg-grid"` explicitly; a new test seeds rounds under two different
`GameKey`s and confirms a player's ranking/median only reflects the
requested game's rounds. *Deps:* S-060 (REQ-409's median ranking, the
method being changed).

**S-079 · `PlayerCareerStint` data model (ADR-0042)**
No consumer yet — this story only makes the data available; S-081 is its
first reader. New entity (`PlayerId`, `ClubName`, `StartYear`, `EndYear?`,
`SequenceOrder`, `AppearanceCount?`) alongside `PlayerAttribute`/
`PlayerAlias`/`PlayerOverride` in `XGArcade.Data` (COMP-06). Extend
`WikidataLookupService`'s existing `P54` query to also read the
`P580`/`P582`/`P1350` qualifiers already present in the statement it
already fetches (no new SPARQL query shape, no new external call) and
populate `PlayerCareerStint` rows alongside the existing `PlayerAttribute`
`"club"` rows from the same response. `AppearanceCount` is `null`, never
`0`, when `P1350` isn't present for that stint.
*Accept:* REQ103-adjacent test: a mocked Wikidata response with `P54`
qualifiers persists one ordered `PlayerCareerStint` row per stint,
`SequenceOrder` reflects chronological order regardless of the order
statements appear in the response, a stint missing `P1350` gets a null
(never zero) `AppearanceCount`; confirm-by-inspection that
`PlayerAttribute`'s existing `"club"` rows and REQ-101's candidate-matching
query are unchanged. *Deps:* S-006 (`WikidataLookupService`/`P54` query
being extended).

**S-080 · `Games.XGPath` module scaffold (`IGameModule` shell)**
Via `/new-game` (`game-scaffolder`), matching ADR-0002/0003's boundary
exactly: new `XGArcade.Games.XGPath` project, `GameKey = "xg-path"`,
registered in `IGameModuleResolver`. `GenerateInstanceAsync`/
`ScoreSubmissionAsync`/`GetCellIdsAsync`/the new per-cell attempt-cap
method (S-077) exist with minimal/stub implementations — enough to prove
the module is discoverable and the boundary compiles, not real game
logic yet (mirrors how S-001 scaffolded the whole platform "empty but
compiling"). No frontend change, no route exposing this game yet.
*Accept:* `IGameModuleResolver.Resolve("xg-path")` returns the new
module; a compiling `XGArcade.Games.XGPath.Tests` project exists (even if
near-empty); confirm no game-specific reference leaked into `Core.*`
(ADR-0003). *Deps:* S-077 (the `IGameModule` interface shape this scaffolds
against must be final first).

**S-081 · xG Path puzzle generation (REQ-1201/1202)**
`GenerateInstanceAsync` picks a configured count `N` (3-5) of distinct
eligible target players — REQ-1201's eligibility (≥3 documented,
chronologically-orderable `PlayerCareerStint` rows, at least one at a
seeded `ClubDefinition` club with ≥20 recorded appearances there or an
unknown appearance count (ADR-0047, added 2026-07-27), drawn from
REQ-112's existing player pool) — and persists a puzzle instance plus one
cell per puzzle. `GetCellIdsAsync` returns those cell ids.
*Accept:* REQ1201-named tests: a candidate with <3 stints, an
undeterminable stint order, no stint at a seeded club, or a seeded-club
stint with a known appearance count below 20 is never selected; a
seeded-club stint at exactly 20 or with an unknown count is still
eligible. REQ-112 pool membership is satisfied by construction, not a
runtime check — at the time this story was built, `Player` had no
`BirthYear`/`Gender` field to violate; `Player.BirthYear` was added later
by REQ-1207/S-082, for xG Path's own age clue, not for pool filtering, and
this eligibility check still does not read it — the restriction remains
enforced entirely upstream at Wikidata-query time (ADR-0025), the same
restriction `GridGameModule` already relies on; this is confirmed by
inspection, not a test case, the same scope-note precedent S-079's own
CHANGELOG entry used. REQ1202-named tests: exactly `N`
distinct-target puzzles are generated per instance; `Round.GameKey =
"xg-path"`/`GameInstanceId` wiring is unchanged from ADR-0003's existing
shape (no new Core-side reference). *Deps:* S-079 (career-stint data to
select against), S-080 (module scaffold).

**S-082 · xG Path clue reveal + guess submission (REQ-1203/1204/1205/1207)**
Backend only. Exposes a puzzle's clue sequence progressively — every one
of the target's `N` documented club stints, split across exactly 3
reveal turns (smallest turn first: `base = N div 3`, `remainder = N mod
3`, first `3 - remainder` turns get `base` clubs, last `remainder` turns
get `base + 1`; e.g. `N=10` → 3-3-4), each club in a turn carrying its
appearance count when known; then one bundled years clue for every
revealed club; then position, nationality, age, in that fixed order;
national team caps never appear — mirrors `GET /rounds/current`'s
per-cell reveal shape, clue-indexed rather than category-indexed.
`ScoreSubmissionAsync` resolves a guess via the existing
`PlayerNameIndex`/name-matching pipeline (ADR-0007, no new matching
infrastructure) and is correct iff the resolved candidate's `PlayerId`
equals the puzzle's target `PlayerId`. The attempt cap read through
S-077's `IGameModule` method returns a fixed `7` for every puzzle.
*Accept:* REQ1203-named tests: the 3-way club split for `N` at the
minimum (3), a non-multiple-of-3 value below 10, and a value at or above
10; appearance count present vs. unknown within a multi-club turn;
chronological order across and within turns; the bundled year-range
clue's content; the sequence halts immediately on a correct guess at
every possible point. REQ1204-named tests: correctness is a direct
`PlayerId` match, not a category check; a guess resolving to no candidate
is incorrect. REQ1205-named tests: the resolved attempt cap is 7 for every
puzzle, never a fixed `2`. *Deps:* S-081 (puzzle instances/cells to guess
against), S-077 (the per-cell attempt-cap mechanism).

**REQ-1207 folded in mid-session:** REQ-1203's position/nationality/age
clues turned out to depend on `Player` data (`Position`/`BirthYear`) that
didn't exist anywhere in the schema or the Wikidata sync pipeline. Rather
than fake that data, silently skip those clue types, or block this story on
a separate one, REQ-1207 (Wikidata P413/P569 sourcing, set once at player
creation) was drafted and built as part of S-082 itself — see REQ-1207's
own entry in `docs/requirements-document.md` for the full detail. Also
added `GET /path/current` (`XGArcade.Api.Path.PathEndpoints`, REQ-1203's
read path) and a shared `XGArcade.Core.Games.GameEntityNotFoundException`
base type (a quality-gate follow-up so `GuessEndpoints`, game-agnostic by
design, doesn't need compile-time knowledge of each game's own scoring-
exception type) — see ADR-0048 for the display-read pattern this endpoint
confirms.

**S-083 · xG Path scoring strategy (REQ-1206, ADR-0040)**
`ClueEfficiencyScoringStrategy` — `round(cluesUsed / maxCluesForThisPuzzle
* MaxPointsPerCell)` for a correct guess; a puzzle never solved before its
attempt cap is exhausted scores `MaxPointsPerCell` (the same
"unanswered/incorrect scores worst" convention as xG Grid, ADR-0021).
Registered against `"xg-path"` in `IScoringStrategyResolver` (S-076).
Computes no `FinalUniquenessScore` at all (null) — this game has no
uniqueness concept, per ADR-0040's own reasoning. Building this for real
also resolved ADR-0040's own deferred parameter-shape follow-up (new
ADR-0049): `IScoringStrategy.ScoreCorrectGuess` now takes the whole `Guess`
being scored (not just its `PlayerAnswerId`) plus a plain
`maxAttemptsForCell`, which `ScoreLockingService` resolves once per cell
(never once per guess) via the existing `IGameModule
.GetMaxAttemptsForCellAsync` (ADR-0041) and passes into whichever strategy
is resolved for the round's `GameKey`; `cluesUsed` is read directly off the
winning `Guess.AttemptCount`, no new column.
*Accept:* REQ1206-named tests: points formula across a range of
`cluesUsed`/`maxCluesForThisPuzzle` combinations; worst-case score when
never solved; `FinalUniquenessScore` is always null for this strategy;
confirm `ScoreLockingService` resolves this strategy (not
`UniquenessScoringStrategy`) for an `"xg-path"` round. *Deps:* S-076
(the resolver/interface), S-082 (guesses carrying enough information —
clues used at time of correct guess — for this strategy to compute from).

**S-084 · xG Path round scheduling (REQ-1202, ADR-0051) — done, 2026-07-28**
A second `RoundSchedulingOptions` instance for `GameKey = "xg-path"` (its
own configured `RoundDuration`, independent of xG Grid's), resolved via a
new `IRoundSchedulingOptionsResolver`/`RoundSchedulingOptionsResolver`
(`XGArcade.Core.Rounds`) mirroring `IScoringStrategyResolver`'s per-`GameKey`
shape, rather than a single directly-injected singleton.
`RoundGenerationService.GenerateNextRoundIfNeededAsync` now takes a leading
`gameKey` parameter. `POST /internal/generate-round` gained an optional
`gameKey` query parameter (default `"xg-grid"` for back-compat with any
caller that omits it), dispatching narrowly to either the existing
`GridTemplateResolver` or a new `PathTemplateResolver`
(`XGArcade.Api.Path`) to produce the round's opaque `TemplateId`; an
unrecognized `gameKey` returns 400 "Invalid gameKey" (validated up front,
same discipline as the existing `roundDurationHours` check — a
quality-gate follow-up correcting an initial 500). `GridSize` moved off
`RoundSchedulingOptions` onto `Games.XGGrid.GridGenerationOptions`
(xG-Grid-specific generation config, not a generic scheduling concern); a
new `Games.XGPath.PathGenerationOptions.PuzzleCount` (default 4) holds
xG Path's own equivalent. Architecture-reviewer was consulted on the
scheduled-job wiring choice before implementation, as this story's own
text called for — recommended extending the existing job rather than
adding a second scheduled invocation: `generate-round.yml`'s single daily
cron now generates both `GameKey`s' rounds, each with its own independent
3-attempt retry loop. See ADR-0051 for the full decision (four related
changes made together) and its own addendum to ADR-0027.
*Accept:* a scheduled run generates an "xg-path" round independent of
xG Grid's own round timing/duration; REQ-301's "one round ahead"
generation and REQ-302's round-lifecycle rules hold for `"xg-path"`
exactly as they do for `"xg-grid"`, proven by test
(`RoundGenerationServiceTests.cs`'s new two-`GameKey` coverage,
`RoundSchedulingOptionsResolverTests.cs`, and `RoundEndpointTests.cs`'s
end-to-end `POST /internal/generate-round?gameKey=xg-path` coverage,
omitted-`gameKey` regression, and unrecognized-`gameKey` 400), not by
inspection alone. *Deps:* S-081 (instance generation to actually
schedule).

**S-085 · Frontend: SCREEN-09 multi-tile game select (REQ-303/720) — done, 2026-08-01**
`GameSelectScreen.tsx` gains a second tile (xG Path), per SCREEN-09's
spec — same tile pattern, no imagery, tokens only. `App.tsx` routing
extended for a second game destination.
*Accept:* REQ720/303-adjacent UI test: both tiles render, in the same
order as `HeaderNav`'s "Games" list; selecting the xG Path tile navigates
to its own screen (S-086); the existing xG Grid tile/navigation is
unchanged. *Deps:* S-082 (a real xG Path experience must exist before
exposing an entry point to it in production).
**Built as:** matches the plan, plus two deliberate scope additions and
one honest placeholder. `GameSelectScreen.tsx` gained a second tile
(`XG_PATH_GAME_KEY = 'xg-path' as const`) — name + one-line description
("Guess the player from a revealed career"), tokens only, row wraps to
stacked at 480px, xG Grid first/xG Path second per SCREEN-09. Each tile's
`aria-label` pins the accessible name to just the game name, with
`aria-describedby` exposing the description as an accessible description
(quality-gate follow-up, `3829e0d`). `onSelectGame` is now typed as the
exact two-member literal union rather than bare `string`, and `App.tsx`
dispatches on it via a `switch` with a `never`-typed exhaustiveness check
(also a quality-gate follow-up) — a third game key added without a
matching case is now a compile error, not a silent no-op. Two additions
beyond the story's literal two-named-files: (1) `frontend/src/nav/
HeaderNav.tsx` gained a second "xG Path" entry (`isPathCurrent`/
`onSelectPath`, mirroring the existing `isGridCurrent`/`onSelectGrid`
pattern exactly) — not named in this story's text, but added deliberately
so this list and `GameSelectScreen`'s tile order stay in agreement, per
REQ-720's own "one entry per game xG Arcade currently hosts" acceptance
criterion (xG Path is a real, merged game as of S-082, so REQ-720's
"Tier 0: exactly one game" language was already stale). (2) `App.tsx`'s
new `'path'` screen (`#/path`) renders a minimal, honestly-labeled
placeholder (`.app__coming-soon`: "xG Path" / "Coming soon — this game
isn't playable yet.") rather than any real gameplay UI — SCREEN-10's
clue-reveal UI is S-086's separate, not-yet-built work, and this story's
own scope is only the entry point reaching it, same "advertised, not
half-built" honesty precedent as other placeholder screens in this
backlog. Two commits: `58a3ca2` (implementation + tests), `3829e0d`
(quality-gate fixes above). 7 new tests (`GameSelectScreen.test.tsx`,
`App.test.tsx`, `HeaderNav.test.tsx`), 389/389 Vitest passing, clean
`tsc -b`/`oxlint`. No backend changes; `architecture-reviewer` found no
boundary drift (follows the existing `grid`/`isGridCurrent`/`onSelectGrid`
pattern exactly, extended to a second game) and confirmed no ADR is
needed. E2E not run in-sandbox (needs a local stack, CI-only per
convention); `frontend/tests/e2e/header-nav.spec.ts` has a stale comment
("a disclosure listing xG Grid (Tier 0's only game)") flagged low-severity
by quality-architect and left untouched by this story — a cheap follow-up
if anyone is in that file next.

**S-086 · Frontend: SCREEN-10 xG Path puzzle screen (growing timeline) — done, 2026-08-01**
The validated clue-reveal UI: vertical timeline of clue nodes (settle-in
motion reusing the badge dock's character, `prefers-reduced-motion`
fallback per that same precedent), guess input pinned below, "Clue N of
M" counter in tabular figures, rejected-guess shake reused verbatim from
SCREEN-02, solved state showing the target player's photo (REQ-214,
falling back to the initials-avatar treatment already established) or
name, "Next puzzle" as an explicit action (never automatic), "Puzzle N of
M" header per SCREEN-10.
*Accept:* REQ1203/1204/1205/1206-adjacent UI tests: clues render in the
documented order/content; a correct guess at any point halts further
reveals; the clue counter reflects that puzzle's own cap, not a fixed
number; reduced-motion preference disables the slide/fade entirely (no
partial-motion state). *Deps:* S-082 (backend clue reveal/guess
endpoints), S-085 (entry point reaching this screen).
**Built as:** matches the plan overall, with two judgment calls flagged
rather than silently resolved. New `frontend/src/path/` module
(`PathScreen.tsx`, `PathTimeline.tsx`, `PathGuessInput.tsx` + CSS/tests)
mirroring `frontend/src/grid/`'s structure; `lib/types.ts`/`api.ts` gained
`CurrentPathResponse`/`fetchCurrentPath` mirroring the existing
`CurrentRoundResponse`/`fetchCurrentRound` pattern; `lib/pathRules.ts`
holds `MAX_CLUES_PER_PUZZLE` (7), kept separate from xG Grid's
`MAX_ATTEMPTS_PER_CELL`; `App.tsx` now renders the real `PathScreen`
where S-085 left a "coming soon" placeholder. Two deviations from the
story's literal text, both already documented where the code lives: (1)
the design doc's "falling back to the initials-avatar treatment already
established" language doesn't match what REQ-214 has ever actually done —
its no-photo case has always been plain text (name) plus a checkmark, no
avatar of any kind — so this reuses that actual plain-text-only fallback
instead of reintroducing a component this story was never asked to design
(see `docs/design-document.md`'s new SCREEN-10 status note for the
accurate description); (2) "Next puzzle" is shown once a puzzle is locked
at all
(solved *or* attempt-cap-exhausted), not only when solved, so a player
can't get stranded after using all 7 attempts unsuccessfully — the design
doc only described the solved case explicitly, also covered by that same
status note. Two commits: `18b1cc2` (implementation + tests), `928bd85`
(quality-gate fixes: `CategoryLabel`/`CategoryGlyph` relocated from
`frontend/src/grid/` to a new shared `frontend/src/components/` —
`architecture-reviewer` flagged `PathTimeline.tsx` reaching into a peer
game module's directory to import it, a deliberate scope addition beyond
the story's three literally-named files, same "keep the module boundary
honest" spirit as S-085's `HeaderNav.tsx` addition; plus a guess-submit
re-fetch-failure fix, an image-load-failure fallback for the solved-state
photo, dropping a redundant JS reduced-motion hook in favor of the
existing CSS-only pattern, a missing "locked-unsolved" test, and a
duplicate-React-key fix — see that commit's own message and
`docs/design-document.md`'s SCREEN-10 quality-gate-follow-up status note
for the re-fetch-failure edge case specifically). 408/408 Vitest passing,
clean `tsc -b`/`oxlint`. No backend changes (S-082 already merged it
separately); `architecture-reviewer` confirmed no ADR is needed — a
straightforward extension of the established `frontend/src/grid/`-as-
per-game-screen-module pattern, plus the `CategoryLabel` relocation
already covered above. E2E not run in-sandbox (needs a local stack,
CI-only per convention), consistent with S-085's own precedent.

**S-087 · Frontend: leaderboard game switcher (SCREEN-03, ADR-0043/REQ-410) —
done, 2026-08-02**
`LeaderboardScreen.tsx` gains the game-switcher tab row per SCREEN-03's
addition, wired to S-078's now-`gameKey`-scoped endpoint. Switching games
re-fetches whichever scope tab is currently selected without resetting it
to All-time.
*Accept:* REQ410-adjacent UI test: switching games re-queries the active
scope with the new `gameKey`; the selected scope tab is preserved across
a game switch. *Deps:* S-078 (backend), S-085 (a second game to switch
to — the switcher is meaningfully testable once one exists, even though
the backend change alone would technically support it earlier).
*Built as:* a genuinely full-stack story, not just frontend — S-078 had
already added `gameKey` to every `ILeaderboardService` method, but
`LeaderboardEndpoints.cs` (the Api/outer-composition layer) still
hardcoded `GridGameModule.XGGridGameKey` at every call site, so there was
no way for any client to actually request xG Path's ranking. This story's
backend half added an optional `gameKey` query parameter to the four
routes that read a specific game's data (`/leaderboard`, `/active-round`,
`/closed-rounds` list, `/window/{resolution}`) — omitted defaults to
xg-grid (preserves pre-S-087 behavior for any caller not yet updated),
and an unrecognized value 400s via the same inline validation
`InternalRoundEndpoints.cs` already established, kept in the Api layer
per ADR-0003 (`Core.Leagues` itself untouched). The single-round
`/closed-rounds/{roundId:guid}` route deliberately gained no `gameKey` —
it resolves by `roundId` alone, which already determines the round's
game. Frontend: a new game-tab row (`leaderboard-screen__game-tabs`,
xG Grid then xG Path, matching `HeaderNav`/`GameSelectScreen`'s existing
order and reusing their `XG_GRID_GAME_KEY`/`XG_PATH_GAME_KEY` constants
rather than duplicating them) sits above the existing scope-tab row;
selecting a game never resets `scope`, and every one of the four scopes'
fetch effects was extended with a `gameKey`-comparison ref alongside their
existing scope-comparison ref so a game switch re-fetches whichever scope
is active — same pattern already used for scope transitions, not a new
one. One deliberate addition beyond the story's literal text: switching
games while a specific past round is drilled into (`selectedRound` set)
now backs out to the round list, since a round belongs to exactly one
game and leaving a stale cross-game round detail on screen would be
misleading — not spelled out in the story, called out here as a judgment
call. REQ410-named tests added at both API level (two games' all-time
rankings independent; a player qualifying under one game absent from the
other's response; omitted-gameKey defaults to xg-grid; unrecognized
gameKey 400s; one smoke test on the closed-rounds route) and UI level
(game switch re-queries the active scope with the new gameKey; selected
scope tab survives a game switch) — the API-level cross-game test was
explicitly called out as "not yet addable" in S-078/REQ-410's own
acceptance criteria until this story's query param existed. 419/419
Vitest passing (416 pre-existing + 3 new), clean `tsc -b`/`oxlint`.
Backend could not be built or tested in-sandbox (`dotnet` not installed);
deferred to CI. No new ADR — ADR-0043's own Consequences section already
named this exact frontend work as a deferred follow-up, not a new
structural decision.

**S-088 · E2E coverage for the full xG Path game loop — done, 2026-08-02**
Playwright: generate an xG Path round (extending the non-Production
test-data endpoint, REQ-806, to cover `GameKey = "xg-path"`), solve a
puzzle across multiple clue reveals, confirm scoring locks correctly at
round close, confirm the puzzle's points appear correctly in its own
game-scoped leaderboard (S-087) and not blended with xG Grid's.
*Accept:* one full end-to-end spec covering generation → clue reveal →
guess → round close → leaderboard, run against the same local-stack E2E
setup `ci.yml` already uses for xG Grid. *Deps:* S-076 through S-087 (the
complete feature).
**Built as:** a genuinely new sibling endpoint, not a literal extension of
the existing one — `POST /internal/test-data/seed-guessable-path-round`
(`InternalRoundEndpoints.cs`, same file as `seed-guessable-round`), gated
by the same non-Production registration and repository-only write
discipline (ADR-0006 boundary rule 4). One deliberate deviation from the
story's own text worth naming: "extending the non-Production test-data
endpoint... to cover `GameKey = "xg-path"`" reads like a parameter added
to `seed-guessable-round` itself, but in practice `seed-guessable-round`
writes a `GridInstance`/`GridCell` directly and has no game-agnostic shape
to extend — a second, parallel endpoint against `IPathInstanceRepository`
(creating one `Player` with three career stints, one `PathInstance`/
`PathPuzzle`, and an active `Round`) was the only route available inside
this repo's own established pattern, minor but real scope drift from the
literal story text. Two new API tests in `RoundEndpointTests.cs`:
`REQ807_SeedGuessablePathRound_Post_CreatesAnActiveXgPathRoundWithOneGuessablePuzzle`
and `SeedGuessablePathRound_Post_IsNeverRegistered_WhenEnvironmentIsProduction`.
`frontend/tests/e2e/play-path.spec.ts` is the one continuous spec the
accept criterion asked for: signup → generation via the new seed endpoint
→ clue reveal (REQ-1203) → an intentionally wrong guess → the correct
guess (REQ-1204/1205) → round close (REQ-205) → the puzzle's points
showing correctly under xG Path's own game-scoped leaderboard tab and
explicitly absent from xG Grid's (REQ-410/ADR-0043, REQ-408). Three
commits: `c0bdd3a` (endpoint), `ae382e9` (E2E spec), `c8eb356`
(quality-gate fixes). `architecture-reviewer` passed with no boundary
concerns (same-shape ADR-0006 extension, no new component/data flow — no
new ADR needed). `quality-architect` passed after three findings were
fixed: the new endpoint's own comment banners mislabeled it as a REQ-806
extension (it's REQ-807's — REQ-806 is `force-close-round`), one test was
named with a non-standard `S088_` prefix instead of this repo's
REQ-prefixed convention, and the unique-test-player boilerplate (name tag
+ `Player`/`WikidataQid` creation) had been hand-copied a third time —
extracted into a shared `CreateUniqueTestPlayerAsync` helper used by all
three seed call sites. `docs/requirements-document.md`'s REQ-807 was
updated in the same `c8eb356` commit to document the new endpoint and
correct its stale "only grid/round content is seeded this way" line — see
that REQ's own status note for the full acceptance-criteria addition.

**S-089 · REQ-215: player-submitted answer suggestion — done, 2026-08-01**
Backend: new `PlayerSuggestion` entity/migration (player name, asserted
club(s), asserted nationality, submitting user id, originating
cell/category types, timestamp, pending/resolved state) and a submission
endpoint that enforces non-guest server-side (rejecting a guest's request
regardless of what the client UI shows) and never writes to
`PlayerAttribute`/`PlayerOverride`/`PlayerNameIndex` on submission — a
suggestion is stored pending only. Frontend: a suggestion entry point
appears after a guess is scored incorrect or a REQ-211 live lookup times
out (and only then — never on a correct guess or a resolved live lookup);
a guest sees it present-but-disabled with a registration prompt (REQ-717's
claim path); a non-guest gets the working form (requires at least one
club and a nationality, rejected with a validation error otherwise).
*Accept:* REQ215-adjacent tests at Unit (trigger scoping; submission
validation), API (guest rejected server-side even with a crafted direct
request; persisted suggestion has no `PlayerAttribute`/`PlayerOverride`/
`PlayerNameIndex` side effect; the originating guess's own stored outcome
is unchanged after submission, confirming the finalized no-retroactive-
rescoring decision), UI (guest present-but-disabled with copy; non-guest
enabled and can complete the form) levels, matching REQ-215's own Test
level line. *Deps:* REQ-211 (existing), REQ-717 (existing, for guest
detection).

**S-090 · REQ-509/510: admin suggestion review + Wikidata commit + manual search-and-add — done, 2026-08-08**
Backend: admin endpoints to list pending suggestions
(REQ-509), trigger a live Wikidata lookup by the suggestion's player name
(same intersection-query shape as REQ-103/REQ-211, timeout reported as
"lookup unavailable" rather than silently treated as no-match, per
ADR-0046), commit a reviewed suggestion through the existing
`PlayerOverride`/`PlayerAttribute` write path (never `PlayerNameIndex`,
per ADR-0007/ADR-0053), and reject; plus REQ-510's standalone manual
search-and-add variant of the identical fetch/commit flow, usable with no
suggestion record involved. Frontend: a new, dedicated admin Suggestions
screen/section — deliberately separate from REQ-503's existing
`AdminScreen.tsx` unverified-data queue, per ADR-0053's decision, not a
shared row shape or merged UI. *Accept:* REQ509/REQ510-adjacent tests at
Unit (fetched data presented for admin judgment, never auto-approved),
API (commit writes only through the override/attribute mechanism, never
`PlayerNameIndex`; reject writes nothing; both actions are
Admin-policy-gated and logged with `admin_id`/timestamp; REQ-510's path
requires no suggestion record before, during, or after), Integration
(Wikidata query mocked; a timeout is distinguished from a genuine
no-match), UI (admin) levels, matching REQ-509/REQ-510's own Test level
lines. *Deps:* S-089 (suggestions must exist to review, though REQ-510's
manual-add half has no dependency on S-089 itself), ADR-0053 (the new
separate-admin-view decision this story implements).
**Built as:** matches the plan closely, plus one new structural decision
and one bug found and fixed along the way. New
`backend/src/XGArcade.Api/Admin/AdminSuggestionEndpoints.cs` implements all
four suggestion-scoped endpoints (`GET /admin/suggestions`, `POST
/admin/suggestions/{id}/lookup`, `POST /admin/suggestions/{id}/commit`,
`POST /admin/suggestions/{id}/reject`) plus REQ-510's two standalone ones
(`POST /admin/player-search/lookup`, `POST /admin/player-search/commit`),
sharing a single fetch helper and a single commit helper across both REQs
rather than duplicating either, exactly as the plan intended. New
structural decision (flagged during the docs phase rather than decided
silently): the commit action doesn't route every confirmed field through
one uniform mechanism — nationality (single-valued) goes through
`PlayerOverride`, exactly like REQ-501's existing manual-override path,
but club(s) (multi-valued, per REQ-113's "ever played for, at any career
point") go through additive `PlayerAttribute` rows instead, one per
confirmed club not already effective for that player, so that confirming
one club can never mask another the way a `PlayerOverride`'s full-type
replacement (ADR-0015) would. Recorded in new ADR-0060 (`docs/decisions/
0060-suggestion-commit-write-path-split-by-cardinality.md`); see that ADR
for the full alternatives considered and accepted trade-offs (most notably:
a committed club `PlayerAttribute` row carries no audit trail of its own —
`PlayerSuggestion.ResolvedByAdminId`/`ResolvedAt` or a log line is the only
record of who confirmed it). Bug found and fixed mid-implementation
(`b8eee1b`, before merge): the new `IWikidataClient
.QueryPlayerCareerAndNationalityByNameAsync` originally gated club
detection on the SPARQL row's `?startTime` qualifier parsing successfully
(reusing `WikidataCareerStintEntry`, whose `StartYear` is non-nullable by
design for ADR-0054's xG Path stint log) — since not every real P54
club-membership statement carries a P580 start-time qualifier, this
silently dropped clubs with no recorded start date from the admin lookup's
result. Fixed by changing `WikidataPlayerCareerLookupResult.Clubs` to a
plain distinct-name list gated only on `?clubLabel` being bound; a
regression test pins a club with no `startTime` binding still appearing.
Frontend: `SuggestionsScreen.tsx`/`.css` (new), reachable via a "Player
suggestions" link added to `AdminScreen.tsx` — never merged into that
screen's existing unverified-data queue, per ADR-0053. Test coverage:
`AdminSuggestionEndpointTests.cs` (21 NUnit tests), `WikidataClientTests.cs`
extensions (including the bug-fix regression case), `SuggestionsScreen
.test.tsx` (9 tests), plus an `App.test.tsx` navigation test — 486/486
Vitest tests passing (independently verified); architecture review and
quality review both clean. **Backend caveat unchanged from S-089: `dotnet`
was unavailable in this build environment** — the backend half was
hand-traced against existing, already-verified patterns rather than
actually built or run; confirm in CI.

**S-091 · Frontend: xG Path guess autocomplete (REQ-207 extension) — done, 2026-08-01**
Pulled forward by deliberate product decision, 2026-08-01, immediately
after S-086 shipped without it (SCREEN-10's own spec named no autocomplete
requirement, so S-086 correctly left it out — this is new scope, not a
gap in that story). Wires `PathGuessInput.tsx` into the existing, fully
game-agnostic `GET /players/autocomplete` endpoint (REQ-207/ADR-0007) the
same way `GuessInput.tsx` already does for xG Grid — no backend change:
the endpoint queries `PlayerNameIndex` globally, with no `gameKey`/category
scoping to extend. Deliberately **not** paired with a disambiguation
picker (REQ-209): reviewed and rejected for xG Path specifically, since
`XGPathGameModule.ScoreSubmissionAsync` (REQ-1204, S-082) already resolves
correctness as "is the target player among the name-matched candidates,"
not "which specific candidate did the player mean" — unlike xG Grid, where
two different same-named players can each independently satisfy a cell's
two categories, an xG Path puzzle has exactly one correct target, so which
same-named candidate a picker would let the player choose never changes
the scored outcome. A picker here would be purely cosmetic, not a
correctness aid — out of scope for this story, not a deferred gap.
*Accept:* REQ207-adjacent UI test: typing 2+ characters in the xG Path
guess field surfaces suggestions from the same autocomplete endpoint xG
Grid uses; selecting a suggestion fills the field without submitting;
suggestions carry no `AttributeType`/category information that could leak
correctness (matching REQ-207's own "implies nothing about whether it is
correct" criterion — trivially satisfied here since xG Path's guess field
has no category to leak in the first place, but the suggestion list itself
must still be the shared, non-scored `PlayerNameIndex` source, never a
narrower path-specific list). *Deps:* S-086 (the guess input this wires
into must already exist).
**Built as:** matches the plan exactly, no deviations. `PathGuessInput.tsx`
now calls `fetchPlayerAutocomplete` (`lib/api.ts`, unchanged — already
game-agnostic) with the same constants `GuessInput.tsx` (xG Grid) uses:
2-character minimum, 275ms debounce, 8-suggestion limit, identical
keyboard-nav (arrow keys move the highlight, Enter selects the highlighted
suggestion, Escape dismisses without touching the typed text) and
combobox/listbox ARIA wiring, and the same graceful-failure behavior (a
rejected/failed fetch shows no suggestions and never blocks or errors the
guess form). New `accessToken` prop plumbed through from `PathScreen.tsx`
(the caller already held it for every other authenticated call). No
disambiguation picker (REQ-209) was added, confirming the story's own
scope call. No backend changes — `GET /players/autocomplete` was already
game-agnostic, querying `PlayerNameIndex` with no `gameKey`/category
scoping to extend. Two commits: `27ed880` (this backlog entry), `3dd0027`
(implementation + tests). 5 new REQ207-prefixed test cases plus the 6
pre-existing `PathGuessInput.test.tsx` tests updated to stub `fetch` and
pass `accessToken`, 416/416 Vitest passing, clean `tsc -b`/`oxlint`. Both
`architecture-reviewer` and `quality-architect` passed clean during the
quality gate — no boundary drift (pure reuse of an already-generic,
already-documented capability by a second consumer) and no fixes needed;
no new ADR.

**S-092 · xG Grid: widen player pool using xG Path's full-career data — dropped, 2026-08-03 (orchestrate run, same day it was queued)**
Raised directly by the product owner during a feedback session, and matched
a follow-up ADR-0054 already named on its own (2026-08-02): "this codebase's
whole player-data cache is built reactively/on-demand... rather than
proactively... [this] needs its own follow-up story and ADR, not bundled
into this one." Today `GridGameModule` still only ever reads
`PlayerAttribute`/live Wikidata country-club intersection queries — it never
reads `PlayerCareerStint` (the full-career data xG Path fetches directly per
ADR-0054/ADR-0055), even though a player who was already fetched for an xG
Path puzzle may already have exactly the club/country membership data a
grid cell needs, sitting unused. The ask: can a cache-miss in
`GridGameModule`'s existing lookup path check `PlayerCareerStint` first
(cheap, already-persisted) before falling back to a live Wikidata query —
while still only ever selecting from xG Grid's own existing seeded
club/country pool (`ClubDefinition`/`CountryDefinition`), not expanding
categories or changing what's eligible.
**Dropped before implementation — this exact idea is already forbidden by
ADR-0042 (2026-07-26, `docs/decisions/0042-player-career-stint-data-model.md`),
written 8 days before this story was queued and never checked against it.**
ADR-0042's Decision and "For AI agents" sections state, in terms that name
this precise scenario: "xG Grid's correctness path continues to read only
`PlayerAttribute`/`PlayerOverride` and must never be changed to read
`PlayerCareerStint`... If a task seems to need club dates/order/counts
inside xG Grid's own logic, stop and flag it — that's a sign the task is
misunderstood, not a sign these tables should merge." `PlayerCareerStint.cs`'s
own doc comment repeats this verbatim. `/orchestrate` ran `requirements-writer`
and `architecture-reviewer` independently before any code was written; both
confirmed the conflict and recommended escalation rather than a workaround:
(1) even setting the ADR aside, `PlayerCareerStint` has no nationality field
at all, so it can never resolve a Country×Club cell's nationality side, and
its `ClubName` is a free-text Wikidata label with no QID — no reliable exact
join to `ClubDefinition`, unlike `PlayerAttribute.AttributeValue` which is
written QID-first (the same "no lossy matching in the correctness path"
concern ADR-0007 already protects); (2) even a bare existence check from
`GridGameModule` would make xG Grid's live-lookup behavior implicitly
depend on xG Path's unrelated fetch/cache-warming history — a real
erosion of the "no automatic propagation between these tables" trade-off
ADR-0042 knowingly accepted, not just a literal-text violation. Escalated
to the product owner via `AskUserQuestion`; decision was to drop the story
rather than draft a new ADR superseding ADR-0042 or pursue a narrower
COMP-06-internal reconciliation variant (either of which would still need
its own future design pass, not a same-session fix). The underlying product
goal (a broader, proactively-built player dataset instead of patching
individual gaps) remains tracked by ADR-0054's own 2026-08-02 follow-up
note — any future attempt at this should start there, and must explicitly
address ADR-0042's boundary rather than reproduce this same conflict.
No code, REQ, or ADR changes were made. *Deps:* none — closed, not queued.
picked up whenever a session is available for the design pass.

**S-093 · xG Path: no-repeat target selection across rounds + admin cycle visibility — requirements + ADR landed 2026-08-03; backend, frontend, and tests all implemented 2026-08-03; quality-gate follow-ups (ADR-0058 amendment) addressed 2026-08-03**
Player feedback, 2026-08-02/03: as more familiar players get selected
(ADR-0056), the same targets are starting to repeat noticeably across
rounds. Today's `PickDistinct` (REQ-1202) only guarantees no repeat *within
one round instance* — nothing tracks or prevents a target reappearing
*across* rounds at all. The ask, in two parts: (1) a target should not be
selected again until every eligible player in the current pool has been
used once (a full "cycle"); (2) once a full cycle completes, an admin
should be able to see that in `AdminScreen.tsx` (the existing admin
surface, REQ-503/509/510 — no new screen needed) so they can take action
(e.g. widen the seeded club/country pool, revisit ADR-0056's familiarity
threshold). **Requirements pass run 2026-08-03** (`/orchestrate` via
`requirements-writer`): added REQ-1208 (no-repeat-until-cycled behavior)
and REQ-1209 (admin cycle-visibility panel), both `Status: Not yet
implemented — drafted only`. **ADR-0058 (2026-08-03)** resolves the two
open design questions: cycle-tracking state is xG Path's own data (new
table(s) in `XGArcade.Data`, scoped to `Games.XGPath` — never a flag on
the shared `Player` entity, per ADR-0042's precedent), and a cycle is
scored against the live, ADR-0056-familiarity-filtered pool
`GetEligiblePlayerIdsAsync`/`PickDistinct` already use (not the larger
structurally-eligible-only pool), with a tolerant
"remaining-unused-count-below-N" completion rule rather than requiring an
exact zero, to tolerate that pool's documented live instability. **Backend
implemented 2026-08-03** by `backend-implementer` — see REQ-1208/1209's own
status notes for the full shape: new entities `PathTargetCycle`/
`PathCycleTargetUsage` (migration `20260803140000_AddPathTargetCycle`), four
new `IPathInstanceRepository` methods, `XGPathGameModule.
GenerateInstanceAsync`'s cycle-aware selection/rollover logic, and the new
`GET /admin/xg-path/cycle` endpoint (`AdminXGPathEndpoints`). `dotnet` was
unavailable in the implementation sandbox — the migration's `Designer.cs`
and `XGArcadeDbContextModelSnapshot.cs` were hand-derived from the existing
`AddPathInstance`/latest-migration pattern, not machine-generated; this
still needs a real `dotnet ef migrations` / `dotnet build` verification in
CI before merge. **Frontend implemented 2026-08-03** by `ui-implementer`:
new `XGPathCycleSection` in `AdminScreen.tsx`, `fetchAdminXGPathCycle` in
`frontend/src/lib/api.ts`, and `AdminXGPathCycleState` in `frontend/src/lib/
types.ts` — reuses `AccountMetricsSection`'s exact fetch/gating pattern
(401 escalates, 403 hides, other error shows inline) and the existing
`admin-screen__metrics`/`admin-screen__empty` display classes, no new
tokens. `npm run build` (`tsc -b && vite build`), `npx tsc -b`, `npm run
lint` (oxlint), and the full Vitest suite (453 tests, including the
pre-existing `AdminScreen.test.tsx`) all pass unchanged. **Tests
implemented 2026-08-03** by `test-writer`: backend unit coverage
(`XGPathGameModuleTests.cs`, new `ManualTimeProvider.cs`) — usage recorded
per selection, exclusion within a cycle, rollover once remaining-unused
drops below N (including reselecting a just-used player), a stale usage
row from a dropped-out player never blocking rollover, and the
pre-existing REQ-1202 insufficient-pool abort left untouched by cycle
state; backend API coverage (`RoundEndpointTests.cs`, new
`AdminXGPathEndpointTests.cs`) — round generation across a rollover
boundary and `GET /admin/xg-path/cycle`'s persisted-state/no-data-yet/403/
401 cases plus its unconditional Production registration; frontend Vitest
coverage (`AdminScreen.test.tsx`) — full-field render, no-data-yet empty
state, and the 401/403/other-error handling pattern for
`XGPathCycleSection`. Frontend: 459/459 Vitest tests pass, verified in
this sandbox. Backend: `dotnet` is unavailable in this sandbox (same
constraint the prior two implementation commits noted) — these tests are
written and hand-traced against the actual implementation but not
compiled or run; still need a real `dotnet test` pass in CI before merge.
**Quality-gate follow-ups addressed 2026-08-03:** `architecture-reviewer`
flagged that `GET /admin/xg-path/cycle`'s `IGameModule` bypass wasn't
literally covered by ADR-0016/0048's stated scope (per-instance content,
not cross-instance bookkeeping state) — ADR-0058 amended to confirm this
is a deliberate extension, not a silent assumption, and to note
`AddInstanceWithCycleUsageAsync`'s bundled-write shape isn't the default
way to write multiple entities. `quality-architect`'s one nit (a comment
typo, `XGArcada` → `XGArcade`, in `frontend/src/lib/types.ts`) also fixed.
*Deps:* none blocking on other stories. Next: a real `dotnet build`/
`dotnet test` verification in CI (migration files and backend tests were
both hand-derived/hand-traced in a sandbox without `dotnet`), then merge.

**S-094 · xG Grid: guessed player's photo on a locked, final-incorrect cell
(REQ-216) — implemented 2026-08-03 (backend and frontend, same day)**
Direct product-owner sign-off this session, reversing a deliberate prior
decision (`CellState.tsx`'s states-2/3 comment) narrowly for the
locked-incorrect case only (state 3, and state 4's incorrect branch) —
state 2 (attempts remaining) is explicitly unaffected. REQ-216 records the
confirmed product scope and UI template (red border + REQ-214-style
photo/name display, graceful fallback to today's behavior when the guess
matched no real `PlayerNameIndex` candidate). The open architecture
question — how a wrong-but-real guessed player's photo is resolved, given
`PlayerNameIndex` carries no photo of its own (ADR-0007) — is now
**resolved**: `architecture-reviewer` + **ADR-0057** decided this is its
own distinct, lower-priority live-lookup trigger, separate from REQ-211 —
Wikidata-only (no API-Football fallback, unlike REQ-211/ADR-0011), fires
once at cell-lock time, fails silently to no-photo (never fail-closed-as-
incorrect) on timeout/no-match. **Amendment, same day:** the two no-photo
branches no longer fall back to "nothing" — direct product-owner sign-off
now calls for a new placeholder/dummy avatar graphic in both the
real-match-no-photo case (with name) and the no-match-at-all case (no
name); see REQ-216's 2026-08-03 status note for the full asymmetry-with-
REQ-214 discussion. This introduces a new visual element with no token in
`design-document.md` §2 yet — `ui-implementer` must add one before
building the frontend half. **Backend implemented 2026-08-03** by
`backend-implementer` — see REQ-216's own status note for the full shape
(`IGameModule.ResolveWrongGuessPlayerAsync`,
`WikidataClient.QueryPlayerPhotoByNameAsync`,
`IPlayerNameIndexRepository.FindByNormalizedNameAsync`, the two new
`Guess` columns, and the new response fields on both
`POST .../guesses` and `GET /rounds/current`). Note the placeholder-avatar
amendment above is a pure frontend/rendering decision — the backend only
ever exposes the same two nullable name/photo fields regardless of
whether the frontend renders "nothing" or a placeholder graphic for a
null photo, so this amendment required no backend change. **Frontend
implemented 2026-08-03** by `ui-implementer`: `design-document.md` §2's
"Placeholder avatar" token/component entry added first (per the flagged
blocker above), then `CellState.tsx`'s locked-incorrect branch (red border
via `Grid.tsx`/`Grid.css`'s new `.grid-table__cell--incorrect`, real photo
via the existing `CellPhoto` component reused as-is, the placeholder via a
new `CellPlaceholderAvatar`), `GridCell.tsx`'s prop passthrough, and
`lib/types.ts`'s two new `incorrectGuessMatchedPlayerName`/
`incorrectGuessMatchedPlayerPhotoUrl` fields. *Deps:* ADR-0057 (resolved
this session); REQ-211/ADR-0011 (existing, `WikidataClient` reused,
budget/fallback tier NOT shared); REQ-214 (existing, UI template).

**S-095 · Team-competition trophies for xG Grid Trophy category (REQ-108,
ADR-0061) — implemented 2026-08-09**
This is the deferred remainder S-031 explicitly called out as future work:
"Team-competition trophies (World Cup, Champions League) are explicitly
out of scope for this story — a distinct follow-up once individual awards
are proven out, since they need a genuinely different Wikidata query
pattern (squad membership + tournament result — no single property links a
player directly to 'won this tournament')." That follow-up trigger fired
here — no new product decision needed, this is the commitment S-031 already
made being fulfilled. ADR-0061 scopes the query design first: a
team-competition win is a join across three things (a player's `P1344`
"participant of" a tournament edition, the edition's `P3450` "sports season
of league or competition" linking it to the competition series, and the
edition's `P1346` "winner" matched against the target country via `P1532`
"country for sport" on the winner's national-team item, or directly against
the target club), not a single joining property the way individual-award
`P166` is. `TrophyDefinition.IsTeamTrophy` (added at S-031, unused until
now) drives dispatch between the two query shapes; no schema change needed.
As a judgment call during implementation, also resolved ADR-0035's own
outstanding follow-up note in the same story: `LookupAndPersistTrophyCountryAsync`
didn't yet honor `CountryDefinition.UsesCountryForSportProperty`, tracked as
follow-up "whenever the trophy pool grows enough to make the pairing
reachable" — since this story's own seeding is exactly what crosses that
threshold, fixing it here was scope, not creep (see ADR-0061's own
"ADR-0035 follow-up resolved in the same story" section).
*Accept:* REQ108/REQ114-named tests: a Trophy×Country/Trophy×Club grid
generates correctly and scores a guess correctly for both a team
competition and an individual award, including the national-team-country
(`UsesCountryForSportProperty`) branch on both query shapes; `ReferenceDataSeeder`
seeds exactly three trophies (Ballon d'Or, FIFA World Cup, UEFA Champions
League), idempotently. *Deps:* S-031 (extends its Trophy category
plumbing directly); ADR-0035/REQ-114 (reuses its `P1532` national-team
pattern on the winner side of the new join).

**Built as:** `IWikidataClient`/`WikidataClient` gained four new
intersection query methods — `QueryTeamTrophyCountryIntersectionAsync`,
`QueryTeamTrophyNationalTeamIntersectionAsync`,
`QueryTeamTrophyClubIntersectionAsync` (ADR-0061's own three), plus
`QueryTrophyNationalTeamIntersectionAsync` for the existing individual-award
path — a fourth method beyond ADR-0061's own list, a judgment call made
during implementation to fully close ADR-0035's follow-up note, documented
on ADR-0035's own updated follow-up entry rather than a second ADR, since
it's the same P27-vs-P1532 dispatch pattern ADR-0035 already established.
`WikidataLookupService.LookupAndPersistTrophyCountryAsync`/
`LookupAndPersistTrophyClubAsync` now dispatch on `TrophyDefinition
.IsTeamTrophy` (and, for Country, also on `CountryDefinition
.UsesCountryForSportProperty`). `GridGameModule`'s `CategoryCandidate`
gained `IsTeamTrophy` alongside the existing `UsesCountryForSportProperty`,
threaded from generation and guess-time resolution through to both
live-lookup dispatch call sites — this also closed the "REQ-114/ADR-0035
scope note" gap that had previously left the Country×Trophy call site
without `P1532` support at all. `ReferenceDataSeeder` seeds FIFA World Cup
(`Q19317`) and UEFA Champions League (`Q18756`) as `IsTeamTrophy = true`
rows alongside the existing Ballon d'Or, growing the seeded trophy pool
from one to three. **Confirmed, asserted-not-just-commented production
consequence:** this crosses `GridGameModule.SelectPairing`'s
`trophyCount >= size` feasibility check for the default `GridSize = 3` —
Country×Trophy and Club×Trophy are now REACHABLE and selectable in
production for the first time, not just mechanically wired up (Trophy×Trophy
still needs `trophyCount >= size * 2 = 6` and stays infeasible). Both new
QIDs are training-knowledge guesses, **not independently verified against
live Wikidata pages this session** (no network access from this sandbox,
same limitation as every prior QID in this codebase) — flagged in
`ReferenceDataSeeder`, `NOTES.md`, and test doc comments; a human must
verify both before this is relied on in production. Full NUnit test suite
added (REQ108/REQ114-named) across `WikidataClientTests.cs`,
`WikidataLookupServiceTests.cs`, `GridGameModuleTests.cs`, and
`ReferenceDataSeederTests.cs` — **not run in this sandbox** (`dotnet` SDK
unavailable); checked by careful manual review only, per `NOTES.md`'s
2026-08-09 entry — CI is the first real run. `docs/requirements-document.md`
(REQ-107/REQ-108 status notes), `docs/architecture-document.md` (§6.1,
boundary rule 1 discussion, COMP-05's cache-warming note),
`MVP-SCOPE.md`, and `docs/implementation-document.md` (data model,
`SelectPairing` narrative) all updated to describe team-competition
trophies as built and reachable, not deferred.

**S-096 · Admin-managed site-wide announcement banner (REQ-511,
COMP-13, ADR-0065) — implemented 2026-08-10**
Requested directly by the product owner alongside a separate admin
notification request for new player suggestions/incident reports
(REQ-215/509, REQ-903) — decomposed into three candidate stories via
`/orchestrate`; the product owner picked this one to build first, one
story per session per this file's own rule, queuing the other two below
rather than bundling. Scope confirmed via `AskUserQuestion` before
starting: a single admin-managed banner (no scheduling, no per-user
dismissal, no multiple concurrent banners — see REQ-511's own "Out of
scope" list), visible to every visitor including one with no session at
all.
*Accept:* REQ511-named tests: admin create/replace-not-insert,
activate/deactivate flip visibility while preserving the saved message,
blank-message and over-max-length rejection with no state change, public
read requires no authentication and returns a clear no-active-banner
state, write actions reject 401/403 under the existing `"Admin"` policy.
*Deps:* none — new `Core.Announcements` component (COMP-13), no
dependency on any existing admin feature beyond reusing the `"Admin"`
authorization policy.

**Built as:** Backend (`backend-implementer`): `AnnouncementBanner`
(`XGArcade.Data.Entities`), a true singleton table — `IAnnouncementBannerRepository`
never inserts a second row, see ADR-0065; `GET /announcement-banner`
(`XGArcade.Api.Announcements.AnnouncementBannerEndpoints`), unauthenticated,
same registration style as `GET /health`, always `200`; `PUT`/`activate`/
`deactivate`/admin `GET /admin/announcement-banner`
(`XGArcade.Api.Admin.AdminAnnouncementBannerEndpoints`), all
`"Admin"`-policy-gated, no new authorization policy introduced.
Frontend (`ui-implementer`): `frontend/src/components/AnnouncementBanner.tsx`
mounted at the very top of `App.tsx`, above `<header>` and outside every
auth-gated branch, fetched once on mount (no polling, per REQ-511's own
"no push/real-time delivery is required"); inline `AnnouncementBannerSection`
in `AdminScreen.tsx` (a judgment call — not a separate linked screen like
`SuggestionsScreen`, since a single message field plus two toggle buttons
didn't warrant its own nav hop), following `AccountMetricsSection`/
`XGPathCycleSection`'s existing 401/403/inline-error resilience pattern.
No new design token — reuses `.app__guest-banner`'s existing token
pairing; documented in `design-document.md` §7 since no SCREEN-xx spec
exists yet for either piece. Tests (`test-writer`): backend
`AnnouncementBannerRepositoryTests.cs`/`AnnouncementBannerEndpointTests.cs`
(singleton upsert-not-insert, 401/403/validation matrix, exactly-at-max-length
message equality — strengthened after quality-gate flagged the original
assertion as status-code-only) — hand-traced only, `dotnet` SDK
unavailable in this sandbox, not compiled or run; frontend
`AnnouncementBanner.test.tsx` plus `AdminScreen.test.tsx` extensions, and
an `App.test.tsx` `describe('App (REQ-511: announcement banner)')` block
(added after quality-gate flagged the original frontend tests as only
covering the component in isolation, not its three real App.tsx render
paths) asserting the banner renders identically for a fully logged-out
visitor, a guest, and a normal logged-in account. **Quality-gate run**
(`architecture-reviewer` + `quality-architect` in parallel): no boundary
violations (clean on ADR-0003 game boundary, ADR-0004 hosting-agnostic,
authorization reuse); two blocking test-coverage findings (the App.tsx
cross-render-path gap and the weak max-length assertion above) routed
back to `test-writer` and fixed; both reviewers separately flagged the
same doc gaps (missing COMP entry, missing ADR, missing CHANGELOG entry),
closed in this same story via `/new-adr` (ADR-0065) and this doc pass.
Verified: 529/529 Vitest tests pass, `tsc -b` and `oxlint` clean, all
confirmed independently in this sandbox; backend suite deferred to CI
(`dotnet` unavailable here, same recurring constraint as S-095 and
every other recent backend story). `docs/requirements-document.md`
(REQ-511, new), `docs/architecture-document.md` (COMP-13, new; §10 ADR
table), `docs/design-document.md` (§7, new REQ-511 open-question entry),
`MVP-SCOPE.md` (Tier 1 pulled-forward entry), and `docs/decisions/0065-
site-wide-announcement-banner-shape.md` (new) all added/updated in this
story.

**S-097 · Admin notification badge for pending player suggestions
(REQ-215/509/512) — implemented 2026-08-10**
Decomposed alongside S-096 above but explicitly deferred to its own
session per this file's one-story-per-PR rule. Low-risk relative to
S-098 below: the pending-suggestions data already exists (REQ-509's `GET
/admin/suggestions`), so this was a count/badge read against data
already being fetched for `SuggestionsScreen.tsx`, not a new data
source. `requirements-writer` drafted REQ-512 first per this repo's
usual "no REQ, no code" workflow.
*Accept:* REQ512-named tests: a positive pending count renders
`Player suggestions (N)`; a zero count renders no badge (button text
alone, not a `(0)` suffix); the count refreshes after navigating away to
resolve a suggestion and back, with no polling; a non-admin/guest never
sees a count (401 escalates via the existing `onAuthError`, 403 leaves
the badge absent without erroring the page).
*Deps:* none — reuses REQ-509's existing `GET /admin/suggestions`
endpoint and existing `fetchPendingSuggestions()` API client function;
no new backend endpoint or data source.

**Built as:** Frontend only (`ui-implementer`) — no backend change, since
this reuses REQ-509's existing endpoint end to end. A new
`PlayerSuggestionsEntry` component in `frontend/src/admin/AdminScreen.tsx`
wraps the existing "Player suggestions" button, fetching on mount via the
already-existing `fetchPendingSuggestions()` and rendering the count as
plain text (`Player suggestions (N)`), the same convention
`UnverifiedDataSection`'s `Unverified data (N)` heading already uses in
this file — deliberately not a colored pill/badge, since
`design-document.md` §2 has no token for one and this avoids an ad-hoc
value per CLAUDE.md's token rule (no `docs/design-document.md` change
needed as a result). After a quality-gate finding, the component was
corrected to distinguish 401 (escalates via `onAuthError`, matching
every other admin section) from 403 (badge silently absent, button still
usable — a non-admin case that should never happen from an already-gated
screen but is handled the same defensive way as the rest of the file)
from any other failure (surfaced inline via a `loadError` state, not
swallowed as "zero pending" — the one failure mode this badge can't
afford). Fetch-on-load only, no polling: `App.tsx`'s screen ternary
already unmounts/remounts `AdminScreen` around a visit to
`SuggestionsScreen`, so returning from resolving a suggestion naturally
re-triggers the fetch with no extra plumbing. Tests (`test-writer`): 7
new tests across `AdminScreen.test.tsx` (badge presence/absence,
401/403/error-state handling in isolation) and `App.test.tsx` (one
end-to-end navigation-round-trip test proving the remount-triggers-
refetch claim, not just asserting it in a comment). **Quality-gate run**
(`architecture-reviewer` + `quality-architect`): no new
component/boundary/data-flow — REQ-509's existing endpoint and
authorization policy are reused as-is, no ADR needed; one blocking
finding (the missing 401/403/other-error distinction above) routed back
and fixed. Full frontend suite (536/536) passing, `tsc -b`/oxlint clean;
backend untouched by this story since it reuses REQ-509's existing
endpoint. `docs/requirements-document.md` (REQ-512, new) and this
backlog entry updated in this story; `docs/architecture-document.md` and
`docs/design-document.md` confirmed unchanged (no new component,
boundary, data flow, or design token introduced).

**S-098 · Admin notification for new in-app incident reports
(REQ-904, ADR-0066) — implemented 2026-08-10**
Decomposed alongside S-096/S-097 above, explicitly deferred, then picked
up this session. Needed a genuinely new capability, not just a badge:
REQ-903/ADR-0064 deliberately keeps no in-app record of a created
incident ("no in-app moderation/review queue"), so there was no existing
data source to badge against, unlike S-097's reuse of REQ-509's existing
endpoint. Confirmed directly with the product owner (2026-08-10, via
`AskUserQuestion`) that the intended approach is for the admin UI/backend
to poll GitHub's Issues API for open issues labeled `user-reported`,
rather than adding a lightweight in-app persistence table that would
encroach on ADR-0064's existing "no review queue" boundary — that answer
was treated as settled scope going in, not re-litigated. `requirements-
writer` drafted REQ-904 first per this repo's usual "no REQ, no code"
workflow.
*Accept:* REQ904-named tests: a positive open-issue count renders a count
next to "Incident reports"; a zero count renders no badge (absence, not
`(0)`, same convention as REQ-512); a GitHub-poll failure renders a
distinct failure/unknown state, never a false zero; repeated admin
requests within the cache TTL do not each trigger a new GitHub call, a
request after the TTL expires does; 401 escalates via `onAuthError`, 403
hides the section.
*Deps:* REQ-903/ADR-0064 (the existing `IGitHubIssueClient`/PAT this
story extends, not replaces).

**Built as:** Backend (`backend-implementer`) + frontend (`ui-implementer`).
`IGitHubIssueClient` gained `ListOpenIssuesByLabelAsync` (same
`GitHubIssueClient`, same PAT, no scope widening — GitHub's fine-grained
`Issues: write` scope already covers reading issues on that repo). A new
`ICachedIncidentIssueSummaryProvider`/`CachedIncidentIssueSummaryProvider`
(`XGArcade.Core.IncidentReporting`) is the only caller of that method: a
single shared `IMemoryCache` entry, default 60s TTL (`GitHub:
IncidentReportCacheTtlSeconds`), that re-serves the last successfully-
polled result on a GitHub failure rather than immediately flipping a
working admin UI to an error state, and only returns an explicit
"unavailable" result if no successful poll has ever happened. This is the
first use of `Microsoft.Extensions.Caching.Memory` anywhere in this
codebase — added as a direct `XGArcade.Core.csproj` package reference
(pinned to 10.0.10 to satisfy a transitive floor `XGArcade.Data`'s EF
Core reference already imposes), not a new third-party dependency. `GET
/admin/incident-reports` (new file, `XGArcade.Api.Admin
.AdminIncidentReportEndpoints`), same `"Admin"` policy every other admin
endpoint uses, always `200` with `{available, openCount, issues}` — no
new authorization policy introduced. Frontend: a new `IncidentReportsEntry`
section in `AdminScreen.tsx`, placed directly after `PlayerSuggestionsEntry`
(S-097's sibling badge), fetching once on load (no polling), rendering
the count's absence rather than `(0)` at zero, a distinct inline message
for the `available: false` failure state, and the same 401/403 handling
S-097 established. A new `.admin-screen__link` class styles the "view on
GitHub" link-out — tokens only (`--color-text-primary`,
`--touch-target-min`), no new color/typeface/animation introduced, so
`docs/design-document.md` was confirmed unchanged, same judgment call
S-097 made for its own badge. Full quality-gate run
(`architecture-reviewer` + `quality-architect`): no boundary violations
(the cache is confirmed as the only caller `GET /admin/incident-reports`
is allowed to use, and `IGitHubIssueClient` remains the only class that
calls GitHub's REST API); ADR-0066 added for the caching/polling decision
since it introduces a genuinely new "live outbound read triggered by an
admin page load" shape this codebase hadn't had before. Tests never call
the real GitHub API (`FakeGitHubIssueClient`, extended for the new
method). Backend 1375/1375, frontend 543/543 passing.
`docs/requirements-document.md` (REQ-904, new), `docs/decisions/0066-
admin-github-issue-polling-cache.md` (new), `docs/architecture-document.md`
(COMP-12 extended, §10 ADR table), and this backlog entry all
updated/added in this story.

## Epic 7 — Technical debt remediation (`CODEBASE_ANALYSIS.md` follow-up)

Source: `CODEBASE_ANALYSIS.md` (2026-08-10), a static/behavioral/security
scan of the codebase. Unlike Epics 0–6, this epic isn't part of the Tier 0
build sequence — it doesn't gate or get gated by feature work, and its
stories don't depend on each other except where noted. **Every story here
is a pure refactor: no behavior change, no new REQ IDs.** Acceptance
criteria are "the existing test suite (named after its current REQ IDs)
passes unchanged," not new REQ-tagged tests. If a story's own execution
turns up a structural choice that could reasonably have gone another way
(per `CLAUDE.md`'s ADR test), add an ADR as part of that story — don't
pre-empt it here. Work these in any order/parallel; each is scoped to one
PR/session.

**S-099 · Patch high-severity `undici` transitive dependency**
`npm audit` (frontend) reports a High-severity advisory chain on `undici`
7.28.0, pulled in transitively via `jsdom` (a Vitest devDependency —
test-only, never shipped to production, but a trivial fix). Run `npm
audit fix` in `frontend/` (or bump the resolved version manually if
`audit fix` doesn't land on a clean one) and confirm nothing else shifts
unexpectedly in `package-lock.json`.
*Accept:* `npm audit` reports 0 vulnerabilities; `npm run test` and `npm
run test:e2e` pass with the same pass count as before the bump.
*Deps:* none.

**S-100 · WikidataClient: extract intersection-query spec table (infra + first 3 pairs)**
`WikidataClient.cs` (2,034 lines, the repo's highest-churn file after
`Program.cs`) has 9 near-identical `Query*IntersectionAsync` methods and
10 near-identical `Build*Query` methods, one pair per `(CategoryType,
CategoryType)` combination. Introduce a spec table — a
`(CategoryType, CategoryType)`-keyed structure holding each pair's SPARQL
clause template and timeout tier (`WikidataQueryTimeoutTier`) — and a
single shared driver that centralizes the `WikidataQid.IsValid` guard
(currently duplicated per-method) before building/running the query.
Migrate the 3 non-trophy pairs first: `QueryCountryClubIntersectionAsync`,
`QueryNationalTeamClubIntersectionAsync`, `QueryClubClubIntersectionAsync`.
Keep existing public method signatures as thin wrappers over the driver
so `GridGameModule`/`XGPathGameModule` call sites need no changes.
*Accept:* for each of the 3 migrated pairs, a test asserts the
spec-table-generated SPARQL string is byte-for-byte identical to the
pre-refactor output (not just "non-null") — this is the regression net,
since `dotnet test` wasn't runnable in the analysis environment that
proposed this refactor and must be relied on here instead; full
`WikidataClientTests.cs` suite passes unchanged.
*Deps:* none.

**S-101 · WikidataClient: migrate remaining 6 trophy-pair queries onto the spec table**
Extend S-100's spec table to the 6 trophy-related pairs:
`QueryTrophyCountryIntersectionAsync`, `QueryTrophyClubIntersectionAsync`,
`QueryTeamTrophyCountryIntersectionAsync`,
`QueryTeamTrophyNationalTeamIntersectionAsync`,
`QueryTeamTrophyClubIntersectionAsync`,
`QueryTrophyNationalTeamIntersectionAsync`. Once every pair goes through
the shared driver, delete the now-dead standalone `Build*Query` methods.
*Accept:* same byte-for-byte SPARQL diff verification as S-100 for all 6
remaining pairs; full test suite green; `WikidataClient.cs`'s line count
drop reported in the PR description.
*Deps:* S-100.

**S-102 · Decompose Program.cs composition root**
`Program.cs` (1,245 lines) is the single most-changed file in the repo's
history — every feature commit tends to touch it, because DI wiring, JWT/
Supabase auth config, CLI-verb dispatch (`--all-clubs` and friends), and
Minimal-API endpoint mapping (26 `app.Map*`/`app.Use*` calls) all live in
one file. Split it into focused extension-method groups (e.g. an
auth-setup group, a CLI-verb-dispatch group, an endpoint-mapping group),
called from a slimmed-down `Program.cs`. Pure reorganization.
*Accept:* `Program.cs` reduced to a thin composition root; full
`XGArcade.Api.Tests` (`WebApplicationFactory`-based) suite passes
unchanged — this is the suite most sensitive to composition-root
reshuffling; manual local smoke check that `/health`, auth, and at least
one CLI verb still work.
*Deps:* none.

**S-103 · Continue AdminScreen.tsx God-Component extraction**
`AdminScreen.tsx` (1,432 lines, 16 `useState`, 4 `useEffect`) is already
mid-refactor (`#167` extracted the shared `useAdminSectionFetch` hook).
Continue that direction: extract the remaining self-contained sections
(announcement banner, incident reports, player suggestions, etc.) into
their own components, each owning its own local state instead of sharing
`AdminScreen`'s. `AdminScreen.tsx` itself becomes a thin
layout/composition component.
*Accept:* `AdminScreen.tsx` line count and `useState` count substantially
reduced; `AdminScreen.test.tsx` (or its post-split equivalent) passes with
no behavior change; no new color/typeface/animation introduced (confirm
against `docs/design-document.md` §2, same as every other frontend story).
*Deps:* none.

**S-104 · Reduce GridGameModule.cs nesting/complexity**
`GridGameModule.cs` (983 lines, 23 methods) has the deepest control-flow
nesting of any hand-written file in the repo. Flatten the deepest-nested
branches into named private methods / early-returns without changing
generation or scoring behavior.
*Accept:* full `GridGameModuleTests.cs` suite passes unchanged; nesting
measurably reduced from the analysis's baseline (lines at ≥5 indent
levels).
*Deps:* none.

**S-105 · Relocate the longest inline rationale comments to their ADRs (optional, low priority)**
`CODEBASE_ANALYSIS.md` §2 confirmed the codebase's highest comment-ratio
files (`Grid.css`, `CellState.css`, `types.ts`, `turnstile.ts`) are
genuinely substantive, not noise — but some of the longest multi-paragraph
comments duplicate rationale that already lives in an ADR. Where that's
true, trim the inline comment to a pointer + one-line summary instead of
the full history. Skip any comment that is the *only* place its rationale
is recorded — this story must not cause a net loss of documented
rationale, only de-duplicate it.
*Accept:* comment ratio drops for the specific files touched;
`quality-architect` review confirms nothing load-bearing was cut (every
trim points at an ADR/doc section that still contains the full
explanation).
*Deps:* none.

## Epic 8 — Technical debt remediation, round 2 (`CODEBASE_ANALYSIS.md` follow-up)

Source: `CODEBASE_ANALYSIS.md`'s 2026-08-11 revision, written after Epic 7
(S-099–S-105) fully landed and a fresh sweep — extended to a top-10 list
since security and the original hotspots were already settled ground —
found the next batch. Same house rules as Epic 7: independent of the Tier
0 build sequence, **every story here is a pure refactor/doc-sync — no
behavior change, no new REQ IDs** (S-110 is docs-only). Acceptance
criteria are "existing tests/docs pass or match reality," not new
REQ-tagged tests. Work in any order/parallel unless a story says
otherwise; each is scoped to one PR/session. The report's own P4
"watch-only" items (#7–10: large test files, `LeaderboardScreen.tsx`,
`AuthController.cs`, `SuggestionsScreen.tsx`) deliberately have **no**
story here — per this epic's own doctrine, low-churn/not-yet-a-problem
files get left alone until something else touches them, not turned into
busywork.

**S-106 · Split PlayerStoreRepository.cs, part 1 (Player/PlayerData/PlayerAttribute/PlayerAlias)**
`PlayerStoreRepository.cs`/`IPlayerStoreRepository.cs` (772/482 lines, 44
methods) is the clearest Single-Responsibility violation left in the
codebase — confirmed an outlier by comparing method counts across every
repository in `backend/src/XGArcade.Data/Repositories/` (next-highest is
16). Split out the first four concerns into their own repository/interface
pairs: `IPlayerRepository` (core CRUD: `GetPlayerByWikidataQidAsync`,
`GetPlayerByIdAsync`, `GetPlayersByIdsAsync`, `AddPlayerAsync`,
`GetOrCreatePlayersByWikidataQidAsync`,
`GetPlayersByNormalizedFullNameAsync`), `IPlayerDataRepository`
(unverified/approve/remove), `IPlayerAttributeRepository`, and
`IPlayerAliasRepository`. Register each separately in
`CompositionRoot/ServiceRegistration.cs`. Check call sites first
(`GridGameModule.cs`, `XGPathGameModule.cs`, `DataSync` services, admin
endpoints) — a caller needing multiple concerns takes multiple injected
repositories rather than one wide one; don't build a facade unless call
sites show a real need for one.
*Accept:* existing `PlayerStoreRepositoryTests.cs` (1,401 lines) coverage
for these four concerns moves/renames to match the new boundaries rather
than being rewritten — structural-only change, no behavior change, no new
REQ IDs. Add an ADR (per `CLAUDE.md`'s own "could reasonably have gone
another way" test — splitting one repository into several is exactly that
kind of choice).
*Deps:* none.

**S-107 · Split PlayerStoreRepository.cs, part 2 (Override/photo+position backfill/CareerStint/data-quality tracking)**
Continues S-106 (independent of it — no shared new infrastructure between
the two halves, unlike `WikidataClient.cs`'s spec table, so this can run
before, after, or in parallel with S-106). Split out the remaining five
concerns: `IPlayerOverrideRepository`, a photo/position/birth-year backfill
repository, `IPlayerCareerStintRepository`, and a data-quality-tracking
repository for the confirmed-low/technical-failure methods
(`IsConfirmedLowAsync`/`RecordConfirmedLowAsync`/
`IsPersistentTechnicalFailureAsync`/etc.). Once both halves land, delete
the original `PlayerStoreRepository.cs`/`IPlayerStoreRepository.cs`.
*Accept:* same as S-106 — tests move/rename, not rewritten; no behavior
change; extend S-106's ADR (or add a second one) if the split boundaries
here raise a new structural question S-106 didn't.
*Deps:* none (can run independent of S-106, but both must land before
either deletes the original files).
**Built as:** matches the plan exactly — S-106 (#177) had already merged by
the time this story started, so the original `PlayerStoreRepository.cs`/
`IPlayerStoreRepository.cs` (469/290 lines, the remaining 21 methods) were
split into `IPlayerOverrideRepository`/`PlayerOverrideRepository`
(`PlayerOverride` CRUD + `HasEffectiveAttributeAsync`),
`IPlayerBackfillRepository`/`PlayerBackfillRepository` (photo/position/
birth-year backfill cursors, plus the `PlayerPositionBirthYearUpdate`
record), `IPlayerCareerStintRepository`/`PlayerCareerStintRepository`
(`PlayerCareerStint`), and `IPlayerDataQualityRepository`/
`PlayerDataQualityRepository` (`ConfirmedLowMatchPair`/`PairLookupFailure`
tracking plus `GetUnseededClubCandidatesAsync`, and the
`UnseededClubCandidate` record) — same one-interface-per-file convention
S-106 established, each registered independently in
`CompositionRoot/ServiceRegistration.cs`, no facade. Every call site
(`GridGameModule`, `PlayerCacheWarmingService`, `XGPathGameModule`,
`WikidataLookupService`, `PlayerCareerStintRefreshService`,
`PlayerCareerPrefetchService`, `ClubGapAuditService`,
`PlayerPhotoBackfillService`, `PlayerPositionBirthYearBackfillService`,
`CliVerbDispatcher`'s seven hand-built CLI verbs, and the admin/round/path
API endpoints) was rewired to depend only on the narrower interface(s) it
actually calls — `GridGameModule` needed both `IPlayerOverrideRepository`
and `IPlayerDataQualityRepository`, `PlayerCacheWarmingService` needed only
`IPlayerDataQualityRepository`, every `PlayerCareerStint`-only caller
(`XGPathGameModule`, `WikidataLookupService`,
`PlayerCareerStintRefreshService`, `PlayerCareerPrefetchService`,
`PathEndpoints`, `InternalRoundEndpoints`) needed only
`IPlayerCareerStintRepository`. `IPlayerStoreRepository`/
`PlayerStoreRepository.cs` are now deleted — COMP-06 is eight
independently-registered repositories. Existing `PlayerStoreRepositoryTests.cs`
(885 lines) coverage for these five concerns moved/renamed into
`PlayerOverrideRepositoryTests.cs`/`PlayerBackfillRepositoryTests.cs`/
`PlayerCareerStintRepositoryTests.cs`/`PlayerDataQualityRepositoryTests.cs`
— test bodies/assertions unchanged, structural move only. No new
structural question came up, so this extended ADR-0067 (S-106's own ADR)
with an "S-107 update" section rather than adding a second ADR. One
pre-existing gap flagged, not fixed (out of this story's pure-refactor
scope): `IsConfirmedLowAsync`/`RecordConfirmedLowAsync`/
`IsPersistentTechnicalFailureAsync`/`RecordTechnicalFailureAsync`/
`ClearTechnicalFailureAsync` have no direct repository-level test — they
were, and remain, exercised only indirectly (through the real repository)
by `GridGameModuleTests.cs`/`PlayerCacheWarmingServiceTests.cs`; this gap
predates the split and isn't new. Backend build/test suite could not be
run in this sandbox (`dotnet` unavailable; apt's `dotnet-sdk-10.0` package
this project's `net10.0` target needs 404s from this environment's Ubuntu
mirror for the `noble-updates`/`noble-security` pool paths specifically —
confirmed by installing `dotnet-sdk-8.0` successfully from the plain
`noble` pool instead, then hitting `NETSDK1045`/`NU1202` against `net10.0`/
EF Core 10 packages) — verified instead by an exhaustive grep sweep for
every remaining `IPlayerStoreRepository`/`PlayerStoreRepository`
occurrence across `backend/src`/`backend/tests` (confirming none is a live
declaration/instantiation, only historical comments) and by hand-checking
every rewired constructor's parameter order against each call site;
relying on CI's `dotnet build`/`dotnet test` for final confirmation.

**S-108 · Backfill tests for AdminScreen.tsx's extracted components, batch 1**
S-103's "pure mechanical extraction" correctly left `AdminScreen.test.tsx`
as the only test coverage for what's now 10+ implementation files. Add
dedicated test files for the first 5: `PlayerSuggestionsEntry.tsx`,
`IncidentReportsEntry.tsx`, `AnnouncementBannerSection.tsx`,
`AccountMetricsSection.tsx`, `XGPathCycleSection.tsx`.
*Accept:* each new `<Component>.test.tsx` passes, covering that
component's own props/state/rendering in isolation; existing
`AdminScreen.test.tsx` still passes unchanged (or is deliberately trimmed
in the same PR with an explicit note on what moved where); no behavior
change.
*Deps:* none.
**Built as:** matches the plan — added the 5 named test files, each
rendering its component directly (not through `AdminScreen`) and stubbing
only the fetch routes that component itself calls. `AdminScreen.test.tsx`
was left unchanged, the story's explicit alternative to trimming — its
existing REQ-507/508/511/512/904/1209 assertions against the full tree
still pass unmodified and now overlap with, rather than being replaced by,
the new isolated coverage. Full frontend suite (`npm run test`, 34 files/
581 tests) and `tsc -b`/`oxlint` all green.

**S-109 · Backfill tests for AdminScreen.tsx's extracted components, batch 2**
Same as S-108, for the remaining components: `UnverifiedDataSection.tsx`,
`GuestClearSection.tsx`, `RoundControlSection.tsx`,
`UserDeletionSection.tsx`, and the shared `useAdminSectionFetch.ts` hook
(used by 5 of the 9 extracted components — give it its own test file too
if it doesn't already effectively have one via the batch-1/S-108
components' tests).
*Accept:* same as S-108.
*Deps:* none — independent of S-108, not sequential.

**S-110 · Sync architecture-document.md/implementation-document.md with the S-102 CompositionRoot split (docs-only)**
`docs/implementation-document.md` §4's folder-structure block still reads
`/XGArcade.Api -> Controllers, DTOs, Program.cs`, and both that doc and
`docs/architecture-document.md` describe auth wiring, admin authorization,
and scoring-strategy registration as happening "in `Program.cs`" in
several places — stale since S-102 (2026-08-11) moved that logic to
`backend/src/XGArcade.Api/CompositionRoot/{AuthSetup,CliVerbDispatcher,
EndpointMapping,ServiceRegistration,WikidataHttpClientConfiguration}.cs`.
Run `doc-sync` directly against S-102's diff (PR #172) rather than against
new work — there is none here.
*Accept:* grep both docs for `Program.cs` afterward; every remaining hit
is either still accurate (e.g. "`Program.cs` calls the `CompositionRoot`
extension methods") or corrected. No code changes in this story.
*Deps:* none.

**S-111 · Split frontend/src/lib/api.ts by domain**
Flagged P3 in the *original* 2026-08-10 analysis, never one of Epic 7's 7
stories — still the largest unaddressed hand-written frontend file (1,057
lines, 51 exports, ~47 similarly-shaped fetch-wrapper functions). Split
into domain files mirroring the backend's own `CompositionRoot` precedent:
`auth.ts` (signup/login/guest/claim/refresh/logout/delete-account),
`rounds.ts` (current round/path, guess submission, suggestions),
`leaderboard.ts` (all `fetch*Leaderboard*` variants), `admin.ts` (if any
admin-specific calls live here rather than being colocated with their
admin components already), `path.ts`. Keep `ApiError`/`describeError`
and any genuinely shared helpers in a slimmed-down `api.ts` or a new
`apiClient.ts` the domain files import from.
*Accept:* every existing call site's import path updates; no behavior
change; existing frontend test suite passes unchanged.
*Deps:* none.
**Built as:** matches the plan's four named files plus a shared foundation
file and three domain files the story text didn't spell out by name but
whose functions didn't fit any of the four. `frontend/src/lib/apiClient.ts`
holds `ApiError`/`throwApiError`/`describeError`/`API_BASE_URL` — every
other file imports from it, nothing imports from it circularly.
`auth.ts` (signup/login/playAsGuest/claimAccount/refreshAccessToken/
deleteAccount/logout/fetchMe/updateDisplayName), `rounds.ts`
(fetchCurrentRound/submitGuess/submitSuggestion/fetchPlayerAutocomplete —
the latter two are shared verbatim by both xG Grid and xG Path call sites,
so they live here rather than being duplicated into path.ts), `path.ts`
(fetchCurrentPath only — the one genuinely xG Path-specific endpoint),
`leaderboard.ts` (all five `fetch*Leaderboard*`/`fetchClosedRounds`
variants plus the `WindowResolution` type), and `admin.ts` (the 19
remaining admin-only functions with no separate home: player-data
verification, round control, user deletion, account metrics/guest-clear,
xG Path cycle read, and the suggestion-review workflow) match the plan
exactly. Three additional domain files the original story text didn't
name, because their functions matched none of the five: `leagues.ts`
(createLeague/joinLeague/fetchMyLeagues — no admin/auth/round tie),
`announcements.ts` (the public `fetchAnnouncementBanner` plus its four
admin CRUD siblings — kept together as one banner-feature domain rather
than splitting the public read into admin.ts, since it isn't admin-only),
and `incidents.ts` (public `reportIncident` plus admin
`fetchAdminIncidentReports` — same "keep the feature's public and admin
sides together" reasoning as announcements.ts). The original
`frontend/src/lib/api.ts` (1,057 lines) is deleted; every one of its 28
call sites was updated to import `ApiError`/`describeError` from
`apiClient.ts` and each function from its new domain file. Doc comments in
`frontend/src/lib/types.ts` and `SuggestionEntry.test.tsx` that named
`lib/api.ts` by path were updated to point at the function's new file;
historical dated implementation notes in `requirements-document.md` and
ADR-0037 that mention the old path were left alone as historical record,
not the current source of truth. `npx tsc -b`, `npx oxlint`, and
`npx vitest run` (34 files/581 tests) all pass unchanged — no test
bodies/assertions were touched, only import paths.
*Deps:* none.

**S-112 · Restructure CliVerbDispatcher.cs from one method into a verb registry**
S-102 moved the CLI-verb dispatch logic out of `Program.cs` but didn't
restructure it — `TryHandleAsync` is a single 649-line method handling
every verb (`--all-clubs` and its siblings —10+ CLI-triggered workflows
exist under `.github/workflows/`) sequentially. Same shape as
`WikidataClient.cs`'s S-100/S-101 fix: replace the sequential if/else body
with a lookup table (`Dictionary<string, Func<...>>` or similar) mapping
each `--verb` string to its own named private method, populated once.
*Accept:* pure refactor, no behavior change — whatever coverage exists per
verb today (integration tests, or manual verification per the relevant
`.github/workflows/*.yml` file) exercises the same verb the same way
before and after. No new REQ IDs.
*Deps:* none.

**S-113 · Decide and document CompositionRoot/*.cs's testing strategy**
No dedicated unit tests exist for `AuthSetup.cs`/`CliVerbDispatcher.cs`/
`EndpointMapping.cs`/`ServiceRegistration.cs` (confirmed: no
`AuthSetupTests.cs` etc. anywhere in `backend/tests/`) — coverage is
entirely indirect via `XGArcade.Api.Tests`'s `WebApplicationFactory`
integration suite. This may be the right call (composition-root code is
often better integration-tested than unit-tested), but it happened by
default when S-102 was scoped as a pure move, not as a deliberate
decision. Either (a) confirm integration-only coverage is intentional and
state so explicitly in `docs/coding-guidelines.md`, or (b) add focused
unit tests for real conditional logic worth isolating (e.g.
`AuthSetup.cs`'s `useLocalE2EAuth` branch).
*Accept:* if (b), new tests pass; either way, `docs/coding-guidelines.md`
gains a stated convention so this doesn't get re-litigated per new
composition-root file.
*Deps:* none.

**S-114 · Extract shared DB-context/logger-factory builder in CliVerbDispatcher.cs**
S-112's `quality-architect` review flagged that, unlike `WikidataClient.cs`'s
S-100/S-101 driver (which eliminated real query-building duplication), the
10 verb handlers in `CliVerbDispatcher.cs` still copy-paste the same
`ConfigurationBuilder`/`GetConnectionString("Database")`/
`DbContextOptionsBuilder<XGArcadeDbContext>`/(where used)
`LoggerFactory.Create` boilerplate — deliberately left alone in S-112 to
keep that story a pure, minimal-diff refactor. Extract a shared private
helper (e.g. `BuildDbContext()` returning a configured
`XGArcadeDbContext`, and `BuildLoggerFactory()` for the handlers that need
one) and have each handler call it instead of repeating the four-line
setup inline.
*Accept:* pure refactor, no behavior change — every handler still resolves
`ConnectionStrings:Database` the same way and throws the same
`InvalidOperationException` message when it's missing; full test suite
(and CI, since `dotnet` isn't available in this sandbox) passes unchanged.
*Deps:* S-112.

## Epic 9 — Technical debt remediation, round 3 (`CODE_HEALTH_ASSESSMENT.md` follow-up)

Source: `CODE_HEALTH_ASSESSMENT.md` (2026-08-11), a CodeScene/SonarQube-style
numeric (1.0-10.0) health assessment covering every backend/frontend/infra
module — a complementary lens to `CODEBASE_ANALYSIS.md`'s priority-list
format (Epics 7-8), not a replacement for it. Same house rules as Epics 7-8:
independent of the Tier 0 build sequence, **every story here is a pure
refactor/doc-sync — no behavior change, no new REQ IDs**. This epic is also
where the `code-health-auditor` agent (`.claude/agents/code-health-auditor.md`,
`docs/ai/agent-migration-plan.md` §8) was introduced to own future sweeps
of this kind, so they don't depend on an ad hoc main-session pass each time.
Two findings from `CODE_HEALTH_ASSESSMENT.md` were checked against
`CODEBASE_ANALYSIS.md`/Epic 7-8 before being written up here and turned out
to already be fully addressed — **not** duplicated into new stories:
`CliVerbDispatcher.cs`'s DI-bootstrap duplication (fixed by S-114's
`BuildDbContext()`/`BuildLoggerFactory()` extraction — the fresh sweep's
own agent had flagged variable-naming differences between handlers, not
actual unextracted duplication) and `GridGameModule.cs`'s nesting (fixed by
Epic 7's S-104 — this epic's S-119 addresses a different axis, responsibility
count/SRP, which S-104 was never scoped to fix).

**S-115 · Comprehensive code health assessment (`CODE_HEALTH_ASSESSMENT.md`)**
Produced a CodeScene/SonarQube-style 1.0-10.0 score for every backend
project, the frontend, and infra, plus a component-level breakdown mapped
to `architecture-document.md`'s own COMP-xx IDs — a numeric-scoring
complement to `CODEBASE_ANALYSIS.md`'s priority-list format, not a
duplicate of it (cross-checked against Epic 7/8's completed stories before
finalizing, per this epic's own intro note above).
*Accept:* `CODE_HEALTH_ASSESSMENT.md` exists at repo root with the
requested report structure (Executive Summary, Score Breakdown by Module,
Score Breakdown by Component/Layer, Priority Refactoring Targets).
*Deps:* none.
**Built as:** matches the plan — overall system score 6.4/10, with
`XGArcade.Core`/`XGArcade.Data`/`XGArcade.Api`/`infra` scoring 7.8-8.3 and
three concentrated hotspots below 5.0 (`WikidataClient.cs` 2.5,
`GridGameModule.cs` 4.5, and the frontend's missing shared data-fetching
abstraction contributing to a 5.5 frontend score). Findings verified via
parallel `Explore` agent deep-reads of each project plus direct git-churn
analysis (`git log --format=format: --name-only | sort | uniq -c | sort
-rn`), not estimated.

**S-116 · Slim `docs/architecture-document.md` §5 (Components) to current-state-only**
§5's Components table and ~600 lines of trailing "**COMP-XX status (DATE,
S-xxx):**" prose had accreted an unbounded, dated narrative history since
the project began — one table cell alone (COMP-05) was 14,718 characters —
even though every change it described is already fully recorded in its
cited ADR. This is the same failure mode `CODE_HEALTH_ASSESSMENT.md`
flagged in code (`WikidataClient.cs`, `GridGameModule.cs`: incremental
growth with no periodic consolidation), just in prose, in the one document
`CLAUDE.md` tells every session to read before touching a component
boundary.
*Accept:* every component's row describes current state only (no "as of
DATE" framing); a new evolution-reference subsection points each component
to its ADR trail in one line instead of re-narrating it; no boundary rule,
REQ reference, or ADR pointer is lost (verified by grepping the old text
for everything it pointed at and confirming the new text still points at
the same places); any internal cross-reference to the removed prose (e.g.
"see the COMP-X status note above") is fixed to point at where the fact
now lives; frontmatter `version`/`last_updated` bumped; `docs/CHANGELOG.md`
gets an entry.
*Deps:* none.
**Built as:** matches the plan. §5 went from 629 lines/~88,400 characters
to 125 lines/~13,000 characters (the whole document: 166,389 → 97,598
characters, a 41% reduction). §6 (data flows, 862 lines) was checked
(`awk '{print length, NR}'` for the single-mega-line signature, plus a
manual read of its opening ~140 lines) and found free of the same pattern
except a smaller ~135-line pocket of "**COMP-03 status (DATE, S-xxx):**"
prose at the very start of §6.1 — deliberately left alone rather than
rushed in the same pass, and tracked as S-123 below. Four dangling
cross-references in §6 that pointed at the removed §5 prose (lines
originally referencing "the COMP-01/COMP-03/COMP-04/COMP-11 status note
above/below") were found via grep and fixed to point at the new §5 table
rows or restated inline instead. Version bumped 0.96 → 0.97 (both
frontmatter and the in-body version line, which had already drifted from
the frontmatter before this story — also fixed).

**S-117 · Add the `code-health-auditor` agent**
No agent owned "run a periodic whole-codebase health sweep and turn
findings into a tracked epic" — `CODEBASE_ANALYSIS.md`'s Epic 7/8 sweeps
happened via ad hoc main-session work, not a defined, invokable
responsibility, the same "orphaned responsibility" shape
`docs/ai/agent-migration-plan.md` §2 (F-2) already identified for
refactoring and test architecture before `quality-architect` absorbed
them. Deliberately a new agent, not folded into `quality-architect`, since
that agent's process explicitly starts from "review the diff (or named
code)" — a different-shaped, different-triggered task from "score the
whole tree."
*Accept:* `.claude/agents/code-health-auditor.md` exists with the standard
frontmatter (`name`/`description`/`tools`); `CLAUDE.md`'s agent table,
`.claude/README.md`'s agent table/org description/a new workflow section,
and `docs/ai/agent-migration-plan.md` (org chart §4.1, ownership matrix
§4.3, a new dated §8 addendum) are all updated in the same change per
`docs/ai/agent-migration-plan.md`'s own governance rule.
*Deps:* none.
**Built as:** matches the plan. The agent applies small, mechanical,
same-session-verifiable fixes itself (step 2 of its process) but hands
anything nontrivial or cross-boundary to the existing delivery agents via
a new backlog epic — deliberately not a second refactor-execution path
alongside `quality-architect`'s.

**S-118 · WikidataClient.cs: migrate remaining query methods onto the shared HTTP/timeout/retry driver**
S-100/S-101 (Epic 7) built a shared spec-table-driven HTTP/timeout/retry
path for the 9 `CategoryType`-intersection queries specifically. Several
query methods added afterward by later ADRs — `QueryPlayerCareerStintsByQidsAsync`/
`QueryPlayerPoolByNationalityAsync`/`QueryPlayerPoolBirthYearAsync`
(ADR-0054/ADR-0055), `QueryPlayerPositionsAndBirthYearsByQidsAsync`/
`QuerySitelinkCountsByQidsAsync` (ADR-0056), and
`QueryPlayerCareerAndNationalityByNameAsync` (ADR-0053) — were never
migrated onto that driver and still hand-roll their own HTTP
send/timeout-CTS/catch blocks. Extend the shared driver to cover these too.
*Accept:* same regression-net style as S-100/S-101 — for each migrated
method, a test asserts behavior (request shape, timeout tier, error
handling) is unchanged, not just non-null; full `WikidataClientTests.cs`
suite passes unchanged; line count reduction reported in the PR
description.
*Deps:* none (extends S-100/S-101's driver without reopening them).
**Built as (2026-08-11):** matches the plan — new private `RunThrowingQueryAsync<T>`
added as `RunIntersectionQueryAsync`'s throw-always sibling; all 6 named
methods became thin wrappers over it, byte-for-byte-identical request/error
behavior (12 new regression tests). `WikidataClient.cs`: 1,815 → 1,778
lines. Full detail: `docs/CHANGELOG.md`, 2026-08-11 entry.

**S-119 · GridGameModule.cs: split by responsibility (generation / name-matching / live-lookup dispatch)**
S-104 (Epic 7) flattened `GridGameModule.cs`'s nesting (25→3 deep-indent
lines) but didn't reduce its responsibility count — it remains 1,039
lines/26 methods/13 constructor-injected dependencies implementing grid
generation, three-stage name matching, disambiguation building, live-lookup
dispatch, and DTO construction all in one `IGameModule` implementation, a
different axis from what S-104 was scoped to fix. Split into
`GridGenerationService`, `GridNameMatcher`, and a live-lookup dispatcher,
composed behind the existing thin `IGameModule` adapter — the same shape
ADR-0067 already proved for `PlayerStoreRepository`.
*Accept:* `GridGameModuleTests.cs` coverage moves/renames to match the new
class boundaries rather than being rewritten — structural-only change, no
behavior change; `IGameModule`'s public contract unchanged. Add an ADR
(per `CLAUDE.md`'s own "could reasonably have gone another way" test —
this is exactly that kind of structural split, same test S-106's ADR-0067
already applied).
*Deps:* none.
**Built as (2026-08-11, ADR-0068):** matches the plan — `GridGameModule.cs`
(1,039 lines) split into `GridGenerationService`/`GridNameMatcher`/
`GridLiveLookupDispatcher` behind narrow interfaces, composed by
`GridGameModule` itself (now ~160 lines, still implementing `IGameModule`
directly since that contract has real external callers). Test coverage
moved 1:1 (2,345 lines/90 methods), confirmed via mechanical method-name
diff. Full detail: `docs/CHANGELOG.md`, 2026-08-11 entry; ADR-0068.

**S-120 · Frontend: extract a shared `useAuthedFetch`/`usePaginatedFetch` hook**
Confirmed via review: multiple screens (`LeaderboardScreen.tsx` most
visibly, but the pattern repeats elsewhere) each hand-roll their own
`phase: 'loading'|'error'|'ready'` union, cancellation-flag, and
`handleAuthError`/401-escalation logic independently — unlike the
backend's consistent repository/service pattern, the frontend has no
shared data-fetching abstraction at all.
*Accept:* new hook covers at minimum the common loading/error/cancel/401
shape; first migration (the lowest-risk screen, not necessarily
`LeaderboardScreen.tsx` — see S-121) proves it out with existing tests
passing unchanged; further screens migrate in follow-up stories, not all
in one PR.
*Deps:* none.
**Built as (2026-08-16):** matches the plan — `frontend/src/lib/useAuthedFetch.ts`
added (promoted from the prior `AdminScreen`-local `useAdminSectionFetch`),
first migrated into `LeaguesScreen.tsx`. `docs/coding-guidelines.md`
updated to describe the promoted hook. Full detail: `docs/CHANGELOG.md`,
2026-08-16 entries (S-120).

**S-121 · LeaderboardScreen.tsx: split into per-scope components**
`LeaderboardScreen.tsx` (1,129 lines) implements 4 independent state
machines (all-time/live/past-rounds/windowed scopes) with 4 near-identical
fetch/poll/cancel effects and 4 near-duplicated `handleLoadMore*`
functions. Split into `AllTimeLeaderboard`/`LiveLeaderboard`/
`PastRoundsLeaderboard`/`WindowedLeaderboard`, each built on S-120's shared
hook. *Note:* `CODEBASE_ANALYSIS.md`'s prior revision explicitly marked
this file "watch-only, no action" on low-churn grounds — this story
revisits that call because S-120's hook migration touches the file anyway,
making a combined pass cheaper than two separate ones, not because the
churn-based reasoning was wrong.
*Accept:* `LeaderboardScreen.test.tsx` coverage moves to per-component test
files, not rewritten; no visual/behavior change; `oxlint`/`tsc -b`/`vitest`
clean.
*Deps:* S-120.
**Built as (2026-08-16):** matches the plan — split into `AllTimeLeaderboard.tsx`/
`LiveLeaderboard.tsx`/`PastRoundsLeaderboard.tsx`/`WindowedLeaderboard.tsx`
plus shared `LeaderboardRowsList.tsx`; none of the four scopes' fetch
shapes actually fit S-120's hook (each has its own poll/pagination
lifecycle), so each kept a small local `handleAuthError` helper instead —
confirmed consistent with S-120's own scoped hook description.
`LeaderboardScreen.tsx` is now a 261-line always-mounted orchestrator
gating visibility via an `active` prop. `CODEBASE_ANALYSIS.md`'s prior
"watch-only" call on this file was marked superseded in place. Full
detail: `docs/CHANGELOG.md`, 2026-08-16 entry (S-121).

**S-122 · Add direct repository tests for `PlayerDataQualityRepository`**
Acknowledged, not-yet-fixed gap from S-107's own "Built as" note (Epic 8):
`IsConfirmedLowAsync`/`RecordConfirmedLowAsync`/`IsPersistentTechnicalFailureAsync`/
`RecordTechnicalFailureAsync`/`ClearTechnicalFailureAsync` have no direct
repository-level test, only indirect coverage via
`GridGameModuleTests`/`PlayerCacheWarmingServiceTests`.
*Accept:* new/extended `PlayerDataQualityRepositoryTests.cs` directly
exercises each of the five methods; no behavior change.
*Deps:* none.
**Built as (2026-08-16):** matches the plan — pure test addition to
`PlayerDataQualityRepositoryTests.cs`, no production code touched, no docs
needed updating (confirmed against each doc's `update_when` trigger).

**S-123 · Slim `docs/architecture-document.md` §6.1's remaining status-note pocket**
S-116 above slimmed §5 but deliberately left §6 alone after confirming it
was mostly clean — except a ~135-line pocket of "**COMP-03 status (DATE,
S-xxx):**" prose at the very start of §6.1 (Grid generation flow, roughly
the paragraphs between the section heading and the actual flow diagram),
the same accretion pattern as §5 at smaller scale. Apply the same
current-state-only treatment S-116 used.
*Accept:* same as S-116 — current-state only, ADR pointers preserved, no
dangling cross-references, frontmatter bumped, CHANGELOG entry added.
Check the rest of §6 (§6.2 onward) for the same pattern while in there,
but don't rewrite what's still accurate just because you're in the file —
S-116's own conservatism principle applies here too.
*Deps:* none.
**Built as:** matches the plan, plus two more genuine pockets found while
scanning the rest of §6 (`code-health-auditor`, per S-117's ownership of
documentation-bloat remediation): §6.3 (Data sync flow, 4 stacked status
paragraphs) and §6.8 (Account deletion flow, 4 stacked "addition"
paragraphs), both given the same treatment. §6.2/§6.4/§6.6/§6.9/§6.10 were
checked and correctly left alone. Whole document: 1,254 lines/98,350
characters → 1,174 lines/93,196 characters (6.4%/5.2% reduction); zero
ADR/REQ/COMP pointers lost, verified by diffing the reference set of each
rewritten section before/after, not just spot-checked. Quality gate
(`architecture-reviewer` + `quality-architect`, run independently and in
parallel) both caught the same two pre-existing stale identifiers carried
forward unexamined into the new prose — `GridGameModule.SelectPairing`
(actually on `GridGenerationService`) and `IPlayerStoreRepository`
(replaced by `IPlayerDataRepository`/`IPlayerOverrideRepository` under
ADR-0067) — both corrected in the same PR since the paragraphs were
already being rewritten. Two further, genuinely out-of-scope doc/code
mismatches surfaced by the same review (§6.2c's stale ADR-0052 citation,
§6.10's stale `XGArcade.Testing` naming — neither line touched by this
diff) were deliberately not fixed here and are tracked as S-125/S-126
below instead, per this story's own "don't rewrite what's still accurate
just because you're in the file" scope limit.

**S-124 · WikidataClient.cs: migrate `QueryPlayerPhotosByQidsAsync`/`QueryPlayerPhotoByNameAsync` onto the shared throwing driver**
S-118's `quality-architect` review pass found that these two methods
(REQ-214 backfill/S-045, ADR-0057) hand-roll the identical throw-on-failure
HTTP send/timeout-CTS/catch shape `RunThrowingQueryAsync` (added by S-118)
now centralizes for six other methods, but were outside S-118's scoped
method list (`docs/backlog.md`'s S-118 entry named exactly six methods) so
were left as-is rather than pulled in opportunistically. `RunThrowingQueryAsync`'s
own doc comment in `WikidataClient.cs` now names both as not-yet-migrated so
this gap isn't silently rediscovered.
*Accept:* same regression-net style as S-118 — byte-for-byte SPARQL/request
assertions and exact timeout/exception-message assertions for both methods,
not just non-null; full `WikidataClientTests.cs` suite passes unchanged;
line count reduction reported in the PR description.
*Deps:* S-118 (extends its driver without reopening it).
**Built as (2026-08-18, `code-health-auditor` sweep):** matches the plan —
both methods are now thin wrappers over `RunThrowingQueryAsync`; exact
timeout/exception-message text preserved byte-for-byte (existing tests
only asserted exception type, so two new byte-for-byte SPARQL tests and
two exact-message timeout tests were added per method, same precedent as
S-118's own regression tests). `WikidataClient.cs`: 1,820 → 1,775 lines.
Hand-traced against `RunThrowingQueryAsync`'s existing six call sites, not
run via `dotnet test` (no SDK in this sandbox — same caveat as S-118).

**S-125 · architecture-document.md §6.2c: fix stale ADR-0052 citation**
S-123's quality gate (`architecture-reviewer`) found `docs/architecture
-document.md`'s §6.2c cites ADR-0052 ("boundary rule 5 and ADR-0052 both
apply") for player suggestions, but ADR-0052 is actually about cache-
warming's technical-failure persistence (`PairLookupFailure`/
`ConfirmedLowMatchPair`) — unrelated. The correct citation is ADR-0053
(player-suggestion admin view), which §5's own COMP-06 row already cites
correctly for `PlayerSuggestion`/`PlayerSuggestionClub`. Predates S-123;
that story's line wasn't touched by S-123's diff so was left alone per its
own scope limit.
*Accept:* §6.2c's citation corrected to ADR-0053; no other content change;
frontmatter bumped; CHANGELOG entry added.
*Deps:* none.
**Built as (2026-08-18, `code-health-auditor` sweep):** matches the plan,
plus one adjacent stale claim found in the same two paragraphs while
fixing the citation: §6.2c's heading and body both still said REQ-509/510's
admin commit half was "not yet built" (S-090) — it shipped 2026-08-08,
well before this note was written, confirmed via `docs/backlog.md`'s own
S-090 entry and `AdminSuggestionEndpoints.cs`'s git history. Corrected in
the same edit rather than deferred, since it was the same sentence already
being touched for the ADR fix, not a separate rewrite.

**S-126 · architecture-document.md §6.10: fix stale `XGArcade.Testing` naming**
S-123's quality gate (`quality-architect`) found `docs/architecture
-document.md` §6.10 still names the pre-split `XGArcade.Testing`/COMP-09
component, while S-123's own §6.1 rewrite (and §5's COMP-09 row) already
use the current name, `Testing.SeedManager`. Same document, two names for
the same component. Predates S-123; that section wasn't touched by S-123's
diff so was left alone per its own scope limit.
*Accept:* §6.10 updated to `Testing.SeedManager` (COMP-09), consistent
with §5 and §6.1; no other content change; frontmatter bumped; CHANGELOG
entry added.
*Deps:* none.
**Built as (2026-08-18, `code-health-auditor` sweep):** matches the plan —
§6.10's lone remaining `XGArcade.Testing` reference corrected to
`Testing.SeedManager`; grepped the whole document afterward to confirm no
other instance remained.

**S-127 · Widen `PlayerCareerPrefetchService` to also sweep seeded clubs (ADR-0069)**
ADR-0055 deliberately scoped `PlayerCareerPrefetchService`'s candidate pool
to already-seeded countries only, flagging widening it as needing "a fresh
product decision." That decision has now been made: a player from an
unseeded country who played for a seeded club is invisible to both
`warm-player-cache`'s pairwise sweep and `prefetch-player-careers`'s
nationality-only sweep — a structural gap, not fixable by seeding one more
club. New `IWikidataClient.QueryPlayerPoolByClubAsync` (P54's full
statement path, `p:P54`/`ps:P54`, excluding deprecated rank — never the
truthy `wdt:P54` shortcut, same non-negotiable rule as every other
P54-involving query in this codebase), a symmetric club sweep added to
`PlayerCareerPrefetchService.PrefetchAsync` alongside the existing country
sweep (same invocation, same CLI verb, no new workflow), and
`PlayerCareerPrefetchResult` extended with `ClubsProcessed`/`ClubsFailed`.
*Accept:* new query's SPARQL shape asserted byte-for-byte (same precedent
as `WikidataClientTests.cs`'s existing per-method exact-query tests);
`PrefetchAsync`'s club loop covered success/per-club-failure-isolated/null-QID-skipped,
mirroring the existing country-loop test cases; `CliVerbDispatcher.cs`'s
console summary and `prefetch-player-careers.yml`'s header comment updated
to mention clubs, not just countries; ADR-0069 records the decision and
the P54 full-statement-path constraint; REQ-110/architecture-document.md's
COMP-07 row updated to describe the widened scope.
*Deps:* ADR-0055 (extends, does not supersede).
**Built as (2026-08-17):** matches the plan. Full detail: `docs/CHANGELOG.md`,
2026-08-17 entry (S-127); ADR-0069.

**S-128 · Feature-flag REQ-211's guess-time live-lookup fallback (ADR-0070)**
The product owner wants to test whether S-127's proactively-built cache is
complete enough on its own without removing REQ-211's guess-time live
Wikidata fallback outright — ADR-0018's own history shows removing it blind
is exactly how a real correctness bug got reported before. New
`GridLiveLookupOptions` (`Enabled`, default `true`), config-bound via
`GridLiveLookup:Enabled`/env var `GridLiveLookup__Enabled`, same pattern as
`RoundScheduling:RoundDurationHours`. `GridGameModule.ScoreSubmissionAsync`
checks the flag immediately before its existing `PlayerNameIndex` gate —
when disabled, an unresolved guess returns immediately, skipping both
`IPlayerNameIndexRepository.ExistsByNormalizedNameAsync` and
`IGridLiveLookupDispatcher.TryRefreshCellAsync`, and fails closed exactly
as it would have before REQ-211 existed (no new `ScoreResult` shape, no new
outcome). REQ-103's grid-generation-time live lookup
(`GridGenerationService.GetMatchCountAsync`) is a separate call path
through the same shared `IGridLiveLookupDispatcher` and is deliberately
untouched.
*Accept:* `GridGameModuleTests` covers `Enabled = false` (never calls
`ExistsByNormalizedNameAsync`/`TryRefreshCellAsync`, verified via
call-counting spies wrapping the real dependencies, matching this
codebase's existing spy pattern) and `Enabled = false` with cached data
already answering the guess (unaffected); every existing REQ-211 test keeps
passing unchanged since the default is `true`; ADR-0070 records the "flag,
not removal" reasoning; REQ-211 gets a status note (not a supersession —
the fallback still exists in full); architecture-document.md's COMP-05
guess-scoring narrative notes the fallback is now conditional.
*Deps:* ADR-0018, ADR-0046 (both describe the fallback this flag gates,
neither is superseded), REQ-509/510 (remediation path while testing with
the flag off).
**Built as (2026-08-17):** matches the plan; ADR-0070. See the follow-up
below for a deploy-time gap found and fixed the same day.
**Follow-up (2026-08-17):** ADR-0070's own Consequences section claimed
flipping `GridLiveLookup:Enabled` needs "no redeploy of code" — true at the
app-config level, but the flag was never actually wired into the deployed
dev Container App at all: `infra/bicep/modules/backend-container-app.bicep`
only forwards explicitly-declared params into the container's `env` block
(same pattern `RoundScheduling__RoundDurationHours` already uses), and
`gridLiveLookupEnabled` was missing from that list — so the deployed
backend always ran with the flag's `true` default regardless of intent.
Fixed by adding `gridLiveLookupEnabled` (default `true`, no behavior
change) through `main.bicep` → `backend-container-app.bicep`'s
`GridLiveLookup__Enabled` env entry, mirroring `roundDurationHours` exactly
— the same "edit the bicep default, push to main, deploy.yml redeploys
with no image change" pattern that param already establishes. `infra/README.md`
checked, not updated — it doesn't document `roundDurationHours` at this
level of detail either, so no drift introduced.

**S-129 · `CommitPlayerDataResponse` reports what actually changed, not just what was requested (backend half)**
The product owner wants to be certain, after approving a suggestion, that
a row was actually added to the DB — not just shown their own confirmed
values echoed back, which was indistinguishable from a no-op (e.g. every
asserted club already an effective `PlayerAttribute`). On the frontend,
`SuggestionsScreen.tsx`'s main approval flow (`PendingSuggestionRow`/
`handleRowDone`) currently shows no confirmation at all on commit, making
the gap worse than it sounds. `CommitPlayerDataResponse` redesigned to
`(Guid PlayerId, bool PlayerCreated, string? Nationality, bool
NationalityWritten, IReadOnlyList<string> ClubsAdded, IReadOnlyList<string>
ClubsAlreadyEffective)` — `CommitPlayerDataAsync` (`AdminSuggestionEndpoints.cs`)
already computed all of this internally and previously discarded it.
**Quality-gate correction (same day):** `PlayerCreated` was first computed
via a separate `GetPlayerByWikidataQidAsync` pre-read before
`GetOrCreatePlayersByWikidataQidAsync`'s own upsert — a real race against
concurrent callers of the same batched method (REQ-211's guess-time
fallback, `PlayerCareerPrefetchService`'s sweep, a second admin commit),
and `GetOrCreatePlayersByWikidataQidAsync` itself had no
`DbUpdateException`/unique-violation handling at all, unlike this
codebase's other get-or-create paths. Fixed by bringing
`GetOrCreatePlayersByWikidataQidAsync` in line with
`LeagueRepository.GetOrCreateGlobalLeagueAsync`/`PathInstanceRepository
.GetOrCreateCycleStateAsync`'s existing catch-detach-refetch precedent, and
changing its return type to `IReadOnlyDictionary<string,
PlayerCreationResult>` (`PlayerCreationResult(Player Player, bool
WasCreated)`) so `WasCreated` is computed atomically at the point of
insert — `CommitPlayerDataAsync` now reads `PlayerCreated` off that signal
directly, no separate pre-read. No `ValidateCommitRequest` behavior and no
write-path routing (ADR-0060) changed — only what the response reports
about writes that already happened.
*Accept:* both `/admin/suggestions/{id}/commit` and
`/admin/player-search/commit` return the new shape;
`AdminSuggestionEndpointTests.cs` updated for the new field names plus new
cases — brand-new player (`PlayerCreated=true`), a repeat commit with an
already-effective club (`ClubsAlreadyEffective`, not `ClubsAdded`,
`PlayerCreated=false`), a nationality-only commit against an existing
override (`NationalityWritten=true` via the update branch), and a genuine
full no-op (`PlayerCreated=false`, `NationalityWritten=false`,
`ClubsAdded=[]` together, unambiguous); ADR-0060 gets a 2026-08-17 status
note explaining the response-shape change without reopening the write-path
decision; REQ-509/REQ-510 get a status note that their acceptance criteria
were silent on response shape. `PlayerRepositoryTests.cs` updated for the
new `PlayerCreationResult` shape plus `WasCreated` assertions on every
existing case; the `DbUpdateException`/re-fetch-winner branch itself is
documented as untestable against the InMemory provider (same precedent as
`UserRepositoryTests.cs`'s identical note on `UserRepository.AddAsync`),
not manually verified against real Postgres in this sandbox (no Docker
daemon available) — flagged for verification before treated as fully
confirmed. Frontend consumption (making `SuggestionsScreen.tsx` actually
display the new fields) is an explicit follow-up story, not part of this
one.
*Deps:* ADR-0060, REQ-509/510 (S-090).
**Frontend half (2026-08-17, same story number):** `CommitPlayerDataResult`
(`frontend/src/lib/types.ts`) updated to the new field names
(`playerCreated`/`nationalityWritten`/`clubsAdded`/`clubsAlreadyEffective`).
`PlayerReviewPanel`'s `handleCommit` (`SuggestionsScreen.tsx`) now captures
the actual commit response instead of discarding it, and threads it through
`onDone`'s new `result?: CommitPlayerDataResult` parameter to both callers.
A shared `describeCommitResult` helper turns the response into a plain-
language summary (new-player/nationality/clubs-added, with a genuine no-op
called out plainly as "No changes — this data was already up to date."),
used by both flows — `PendingSuggestionRow`'s approval flow, which
previously showed no confirmation at all (now lifted into
`SuggestionsScreen`'s own `confirmation` state, rendered above the pending
list since the row itself unmounts on every commit), and
`ManualSearchSection`'s flow, which previously showed only the generic
"Player data committed." string. No new CSS — reuses the existing
`.suggestions-screen__confirmation` class already defined for
`ManualSearchSection`. `SuggestionsScreen.tsx` is an admin-only utility
screen (REQ-509/510/ADR-0053) with no `SCREEN-xxx` entry in
`docs/design-document.md`, so nothing there needed updating.
*Accept:* `SuggestionsScreen.test.tsx` updated for the new response shape
in every existing commit-mock, plus new cases: a suggestion-approval commit
that adds a new club renders the specific summary (not just row removal), a
genuine no-op commit renders "No changes — this data was already up to
date.", and the manual-search flow's commit no longer renders the generic
string. `npm run test` (582/582 passed), `npx tsc -b` (clean), and
`npx oxlint` (clean) all run for real in this sandbox.
*Deps:* backend half above, ADR-0060, REQ-509/510 (S-090).

## Epic 10 — CI/CD workflow cleanup

Scoped from a live audit of all 20 `.github/workflows/*.yml` files against
their actual GitHub Actions run history (`actions_list`/`list_workflow_runs`,
2026-08-17), not just their file contents. Two genuinely broken workflows
were found — `backup-database.yml` has failed **40/40** of its last 40
scheduled runs, and `prefetch-player-careers.yml` has failed 4 of its last
6 — which is the concrete substance behind "always failing." Everything
else below is scoped from the same audit, not guessed.

**S-130 · Delete every Tier 1 dev/prod-split workflow that has never succeeded — clean slate, re-add when Tier 1 actually needs it**
Product decision (2026-08-17, explicit): no patch-and-keep for these — if
a Tier 1 workflow has zero runs or has never once gone green, delete it
outright rather than fixing/guarding it, since none of Tier 1's real prod
environment exists yet for it to act on anyway. Re-adding a thin
`workflow_dispatch` wrapper later, once Tier 1 actually starts, is cheap;
carrying dead/red entries in the Actions tab in the meantime is not.
Supersedes this epic's original S-130 (which proposed patching
`backup-database.yml` with an early-exit guard) and resolves S-133's
"decide" framing outright — delete, don't debate. Five workflows meet the
bar, confirmed against `list_workflow_runs`, not assumed:
- `backup-database.yml` — **40/40 scheduled runs failed** (targets
  `PROD_*` secrets that don't exist yet).
- `promote-dev-to-prod.yml` — **0 runs ever**.
- `sync-players.yml` — **0 runs ever** (own header comment already says it
  needs rewriting, not just re-enabling, once Tier 1's API-Football
  integration lands — "T-101" — so today's file gives no head start).
- `sync-prod-to-dev.yml` — **0 runs ever**.
- `promote-dev-to-prod-dry-run.yml` — technically ran twice and succeeded
  both times, so it doesn't meet the "never succeeded" bar on its own, but
  its entire purpose (an early-warning drift check before someone runs the
  real promote) is moot once `promote-dev-to-prod.yml` is gone — delete it
  alongside its target rather than leave an orphaned dry-run for a
  workflow that no longer exists.
Keep the underlying scripts (`infra/scripts/sync-prod-to-dev.sh`,
`infra/scripts/promote-dev-to-prod.sh`, and whatever `sync-players.yml`
currently wraps) runnable by hand/CLI — same "delete the workflow wrapper,
keep the capability" pattern S-132 already applies to the one-off
maintenance tools — so nothing is actually lost, only the always-red or
never-triggered Actions-tab entries.
*Accept:* all 5 `.yml` files deleted; `infra/README.md` updated to drop
references to the deleted workflows and gains a short note (near the Tier 1
section) that dev/prod-split automation was deliberately removed until
Tier 1 creates a real prod environment, with a pointer to the kept
scripts for manual use in the meantime; `MVP-SCOPE.md`'s Tier 1 section
gets the same pointer so a future Tier-1 session isn't surprised these
are gone; REQ-901 (backup) gets a status note that its automation was
removed pending Tier 1, not that the requirement itself changed.
CHANGELOG entry naming all 5 removed workflows and why.
*Deps:* none.

**S-131 · Diagnose `prefetch-player-careers.yml`'s 4/6 recent failures**
Runs 1, 3, 4, and 6 (of 6 total) failed; only run 2 (2026-08-02) and the
job that's currently `in_progress` as of this audit succeeded outright. The
two most recent failures (2026-08-17, both same day as `1e7cb99` "Give
warm-player-cache/prefetch-player-careers headroom for a cold rebuild
(#203)") suggest this may already be addressed by that just-merged fix, but
that's unverified — the fix landed same-day as the last observed failure,
not confirmably after it. Re-run the workflow post-merge and confirm green
before treating this as resolved; if it still fails, get the actual job
logs (`get_workflow_run_logs_url`) rather than assuming timeout is still
the cause.
*Accept:* a manually-triggered post-#203 run completes `success`; if not,
a new story is filed with the actual failure captured from logs rather
than reopening this one indefinitely. `NOTES.md` gets an entry either way
(closes the incident) since this pattern (long-running Wikidata sweep jobs
timing out as the pool grows) has recurred at least twice now (`purge-player-pool`'s
own 2026-08-17 timeout fix, `1e7cb99`) and is worth a standing note for
future sweep jobs added the same way.
*Deps:* none (informational/verification only — #203 already merged).

**S-132 · Remove one-off maintenance workflows that already served their purpose**
Seven workflows are `workflow_dispatch`-only, never scheduled, and their
own header comments describe them as one-time recovery/backfill/cleanup
tools for an incident that's already resolved — confirmed against run
history, each has run only 2-6 times total, always in a tight cluster
around the incident that motivated it, with no runs since:
`audit-club-gaps.yml` (2 runs, last 2026-08-10), `backfill-player-photos.yml`
(3 runs, last 2026-07-18), `backfill-player-position-birthyear.yml` (6 runs,
last 2026-08-10), `clean-duplicate-career-stints.yml` (3 runs, last
2026-08-10), `clean-stale-club-attributes.yml` (2 runs, last 2026-07-17),
`clear-pair-lookup-failures.yml` (5 runs, last 2026-08-02), and
`verify-wikidata-player-data.yml` (3 runs, last 2026-07-20). Remove the
`.yml` workflow files (they clutter the Actions tab and each one's
"is this still needed" question has to be re-asked by every future agent
that reads this repo) but **keep the underlying CLI verbs and services**
(`PlayerPhotoBackfillService`, `PlayerPositionBirthYearBackfillService`,
`DuplicateCareerStintCleaner`, `StaleClubAttributeCleaner`,
`PairLookupFailureCleaner`, the `verify-wikidata-player-data`/`audit-club-gaps`
verbs) runnable via `dotnet run -- <verb>` — every one of these tools is
idempotent and may legitimately be needed again for a future, different
incident of the same shape, per each tool's own doc comment. Removing only
the workflow wrapper (not the capability) matches this codebase's existing
convention of CLI-verb-first, workflow-as-thin-wrapper (see
`implementation-document.md` §6's CLI-verb pattern section). `purge-player-pool.yml`
is explicitly **not** in this list — it ran again today (2026-08-17, a real
pool rebuild), it's an actively-reused recovery tool with a required
confirmation phrase, not a one-time-incident artifact.
*Accept:* the 7 `.yml` files are deleted; each verb still runs via
`dotnet run -- <verb-name>` locally/in a throwaway manual `workflow_dispatch`
re-add if ever needed (documented as such in each service's own doc
comment, which already exists); `infra/README.md`'s any references to
these workflows by name are updated or removed; CHANGELOG entry naming
which workflows were removed and why (matches the "removing something
non-obvious" documentation bar this repo already holds itself to).
*Deps:* none.

**S-133 · Superseded by S-130 — decision made, not left open**
Originally framed this as an open product decision (keep-dormant vs.
remove the three never-triggered Tier-1-pending workflows). Resolved
2026-08-17: product owner chose "clean slate" outright — delete every
Tier 1 workflow that's unused or has failed, no debate, re-add later if
Tier 1 needs it. Folded into S-130 (which now covers all 5 affected
workflows, not just these 3, since the same reasoning extends to
`backup-database.yml` and `promote-dev-to-prod-dry-run.yml` too). Kept as
a numbered entry rather than deleted outright, matching this backlog's own
S-092 precedent (a dropped/superseded story keeps its number and a short
explanation rather than being silently removed or having its number
reused).
*Deps:* superseded by S-130.

**S-134 · Workflow naming audit — rename `warm-player-cache.yml` → `warm-grid-cache.yml`, no other renames needed**
Explicit audit of every workflow name that survives S-130/S-132's
deletions against a verb-object, kebab-case, unambiguous-scope bar. Most
already read clearly on their own — `ci`, `deploy`,
`import-player-name-index`, `purge-guest-accounts`, `purge-player-pool` —
and renaming any of them for its own sake would just create diff noise and
break muscle-memory/external references (`infra/README.md`, dashboards)
for no reader benefit. Two real gaps found, both the same shape as the
`generate-round.yml` split (Epic 11): a name that doesn't say which game it
serves. `generate-round.yml`'s fix is the split itself (S-136), not a
separate rename. `warm-player-cache.yml` needs an actual rename: it fills
`PlayerAttribute` (xG Grid's category-pairing answer cache) only — it does
not touch `PlayerCareerStint`, which is xG Path's `prefetch-player-careers.yml`.
"Warm cache" is the right verb (this genuinely is a correctness-cache
warming operation, confirmed against `PlayerCacheWarmingService`'s own
behavior — not a raw player-roster import), but "player cache" doesn't say
*which* cache or *which* game, unlike its Path counterpart. Rename to
**`warm-grid-cache.yml`**, giving the two per-game data-prep jobs matching,
scoped names (`warm-grid-cache.yml` / `prefetch-player-careers.yml`) the
same way `generate-grid-round.yml`/`generate-path-round.yml` will. Do
**not** invent one shared name for both jobs — they build genuinely
different tables for genuinely different correctness models (ADR-0042
deliberately keeps `PlayerAttribute`/xG Grid and `PlayerCareerStint`/xG
Path unreadable from each other's side), so a shared name would misrepresent
that boundary, not just relabel it. With S-130 removing the entire Tier 1
dev/prod-split/backup family outright, there's nothing left in that group
to weigh a name against.
*Accept:* `.github/workflows/warm-player-cache.yml` renamed to
`warm-grid-cache.yml`, content unchanged; a full-repo sweep (not just the
file itself) updates every reference to the old filename by name —
`PlayerCacheWarmingService`'s own doc comment, `NOTES.md`, `infra/README.md`,
`architecture-document.md`, and any other workflow's own comments that
name it (e.g. `purge-guest-accounts.yml`'s cron-offset note references
other jobs by name) — a grep for the literal string `warm-player-cache`
across the repo returns zero hits once done; CHANGELOG entry.
*Deps:* S-130, S-132 (audit the post-cleanup set, not the pre-cleanup one).

**S-153 · `prefetch-player-careers.yml`: give re-runs a skip-shortcut for previously-failed country/club/batch fetches (mirror ADR-0052's `PairLookupFailure` pattern)**
Closes S-131. Confirmed against real run history, not assumed: run #6
(2026-08-17T08:09:25Z, `workflow_dispatch` on commit `1e7cb99` itself — the
exact #203 headroom fix, verified via `head_sha`) is the manually-triggered
post-#203 run S-131 asked for. It did **not** complete `success`, but it
also did **not** time out — it finished in 43 minutes (08:09→08:52), well
inside the new 240-minute cap, so #203's fix worked as intended. The
residual failure is a different, already-understood class: 8 countries
(United Kingdom, Argentina, Germany, Ivory Coast, France, Brazil, Czech
Republic, United States of America) and 1 club (Lille) failed their
player-pool fetch, and 26 career-fetch batches failed, all transient
Wikidata `502 Bad Gateway` (plus one truncated-response JSON parse error)
— 132,226 players touched / 20,287 stints added from what succeeded before
the job's designed "keep going, fail loud at the end" contract exited it
nonzero. Same flakiness class the workflow's own 2026-08-17 header comment
already documents for run #5 (37 batches, 2 countries) — not a new bug.
The real gap: unlike `warm-player-cache.yml` (`ConfirmedLowMatchPair`/
`PairLookupFailure`, ADR-0050/ADR-0052), `PlayerCareerPrefetchService` has
no persisted record of which country/club pool fetches or which
career-fetch batches failed last run, so every re-run repeats the FULL
country+club sweep from scratch to pick up the ~35 units that actually
failed — a 43-90 minute retry to fix single-digit-percent flakiness, every
time.
*Accept:* a new table mirroring `PairLookupFailure`'s exact shape (composite
key scoped to prefetch's own units — country/club identifier for pool-fetch
failures, batch key for career-fetch batch failures — `ConsecutiveFailureCount`,
`LastFailedAt`; same read/write-only-through-repository-method discipline,
same "not self-expiring, cleared by `purge-player-pool`" invariant, same
prod/dev sync exclusion per ADR-0009) lets `PlayerCareerPrefetchService`
skip a country/club/batch that succeeded on the immediately-prior run and
retry only what failed, so a re-run's cost scales with the flakiness delta,
not the full pool; a REQ###-named test proves a unit that failed once then
succeeded on retry is retried (not skipped), and `NOTES.md` gets an entry
recording this story's own before/after re-run cost once it's verified for
real. `docs/implementation-document.md` §5 gains the new table entry
alongside `PairLookupFailure`'s existing one.
*Deps:* none (S-131's diagnosis is this story's own investigation, above).

## Epic 11 — Round generation: per-game workflows, human-readable round IDs

**S-135 · Add a human-readable per-`GameKey` round number, surfaced in place of the raw GUID**
`Round.Id` is a `Guid` and is exposed as such end-to-end (API DTOs, URLs,
and — confirmed live — `RoundControlSection.tsx:70`'s admin panel, which
renders literally `Round {activeRound.round.roundId} · ends {endTime}`,
the one place today where a raw GUID appears as visible text to a human).
There is no existing round-number concept to build on — `frontend/src/lib/types.ts`
has a comment stating exactly this ("no round-number field anywhere in
this data... never a fabricated 'round #N'"), written when that was still
true. Add `Round.SequenceNumber` (int, unique per `GameKey`, assigned at
creation time — e.g. `MAX(SequenceNumber) + 1` scoped to the new round's
`GameKey`, computed inside `RoundGenerationService`'s existing
create-transaction so it can't race against itself the same way its
idempotency check already can't). Backfill existing rows by `StartTime`
order per `GameKey` in the same migration. Add `sequenceNumber` to every
round-shaped DTO (`CurrentRoundResponse`, `CurrentPathResponse`,
`ClosedRoundSummary`, `GenerateRoundResponse`, `AdminRound`) alongside the
existing `roundId`, which **stays as the real PK/FK for every internal
wiring path** (URLs, guess/suggestion submission, leaderboard lookups) —
this is a display label only, never a replacement identifier, so no
routing/foreign-key code changes. `RoundControlSection.tsx` and any other
GUID-rendering spot switch to `"Grid Round #{sequenceNumber}"` /
`"Path Round #{sequenceNumber}"` phrasing.
*Accept:* migration backfills every historical row with a correct,
gapless-per-`GameKey` sequence; a new REQ###-named test proves two rounds
of different `GameKey`s can share the same `SequenceNumber` (they're
independent counters, matching `IRoundSchedulingOptionsResolver`'s existing
per-`GameKey` independence) while two same-`GameKey` rounds never collide;
`RoundControlSection.tsx` no longer renders a raw GUID anywhere (its own
existing test updated); design-document.md/requirements-document.md gain
this as a new REQ under §the round-scheduling section since it's new
user-facing behavior, not a REQ-301/303 amendment; ADR added (new
persisted concept + migration + backfill — "could reasonably have gone
another way" per `CLAUDE.md`'s own bar, e.g. a formatted code like
`GRID-2026-08-17-01` was considered and rejected in favor of a plain
integer for simplicity, record that in the ADR).
*Deps:* none.

**S-136 · Split `generate-round.yml` into `generate-grid-round.yml` and `generate-path-round.yml`**
Today one job/one cron (`0 6 * * *`) calls a shared bash function twice,
once per `GameKey`, deliberately chosen over a matrix or a second cron
entry specifically to avoid re-deriving ADR-0027's `RoundDuration >=
cron's max gap` safety invariant a second time (ADR-0051's own reasoning).
The user-facing ask is explicit: separate workflows per game. Splitting is
safe now for a different reason than it would have been at ADR-0051's
time — `RoundSchedulingOptions` is already fully per-`GameKey` and
independent (`IRoundSchedulingOptionsResolver`), and the shared
`/internal/generate-round` endpoint already takes `gameKey` as a first-class
parameter with no other game-specific branching outside the
`templateId` switch — so nothing server-side needs to change. Each new
workflow gets its own `on.schedule` cron and must independently satisfy
ADR-0027's invariant against **its own** `GameKey`'s configured
`RoundDurationHours` (currently both default to a value ADR-0027 already
validated for the shared 24h-gap cron; re-verify each independently rather
than assuming the old shared proof still holds once they can diverge).
Each workflow's own `workflow_dispatch.round_duration_hours` input now
only affects its own `GameKey`, fixing today's coupled behavior where
supplying it during a manual dispatch silently applied to both games.
*Accept:* `generate-grid-round.yml`/`generate-path-round.yml` each retry
3x/backoff independently (same shape as today, just not sharing a job);
a REQ###-named test or documented manual verification confirms a manual
dispatch of one never affects the other's round; new ADR (extending, not
superseding, ADR-0027/ADR-0051 — records why the shared-cron reasoning no
longer applies once schedules can diverge safely) with the "For AI agents"
guardrail carried forward: any future divergence in `RoundDurationHours`
between the two games must re-check each workflow's own cron against
ADR-0027's invariant independently. `infra/README.md` and any dashboard/
alert referencing `generate-round.yml` by name updated.
*Deps:* none (independent of S-135 — the GUID/sequence-number work and the
workflow split touch different layers).

## Epic 12 — xG Path player-pool eligibility overhaul

Current xG Path eligibility (`XGPathGameModule.GetEligiblePlayerIdsAsync`/
`IsEligible`, ADR-0045/ADR-0047) requires **≥3 total career-stint rows**
(not 3 *eligible* clubs) plus **≥1 stint at a `ClubDefinition`-seeded club**
with ≥20 recorded appearances (or unknown). The birth-year floor (1939) is
enforced far upstream, at Wikidata SPARQL query time in `WikidataClient`,
shared with xG Grid's own player pool — it cannot be changed there without
also narrowing xG Grid's pool, which is out of scope. B-team/reserve clubs
are not filtered anywhere today; national-team filtering exists
(`PathCareerStintFilter`, regex on `"national"` + `"team"`) but is
proven inconsistent (catches "Catalonia national football team," misses
"Basque Country regional football team" purely because it doesn't say
"national").

**S-137 · xG Path: add a `BirthYear >= 1975` eligibility filter, additive to (not replacing) xG Grid's shared 1939 pool floor**
`Player.BirthYear` already exists and is populated by the REQ-1207 backfill
(`backfill-player-position-birthyear.yml`, S-132 removes the *workflow* but
the backfill service and its data stay), so this needs no new data
pipeline — just a new check in `XGPathGameModule`'s eligibility pipeline
(alongside `IsEligible`, not inside `PathCareerStintFilter`, since this is
a player-level fact, not a stint-level one). **Decision, made here rather
than left open:** a candidate with `BirthYear == null` is excluded (not
included), fail-closed — matching this codebase's established fail-closed
convention (ADR-0070, REQ-211's fallback) over silently admitting a player
xG Path can't actually verify meets the new bar. File S-141 as the explicit
follow-up to sweep any remaining null-`BirthYear` rows so this exclusion
shrinks the pool as little as possible over time, rather than trying to
solve both in one story.
*Accept:* `PathCareerStintFilterTests.cs`/`XGPathGameModuleTests.cs` gain
cases for `BirthYear == 1975` (included, boundary), `1974` (excluded),
`null` (excluded); new ADR superseding ADR-0045 on this one point (records
why 1975 lives as an xG-Path-only additive filter rather than a shared
SPARQL-level change, and the fail-closed null decision); REQ update noting
the new floor and that it is intentionally independent of xG Grid's 1939
floor.
*Deps:* none.

**S-138 · xG Path: require ≥2 distinct `ClubDefinition`-seeded clubs, replacing ADR-0047's single-club threshold**
Current rule (ADR-0047): ≥1 stint at a seeded club with ≥20 appearances (or
unknown), plus a separate, club-blind ≥3-total-stints structural check
(ADR-0045). The new rule is explicitly about *eligible* clubs specifically:
≥2 distinct clubs from the same `ClubDefinition`/`GetClubsAsync()` list xG
Grid already uses. **Decision, made here:** keep ADR-0047's ≥20-appearance
(or-unknown) quality bar, but apply it to **both** required seeded clubs,
not just one — dropping it entirely would let a genuine one-cameo-appearance
stint count as one of the two required clubs, undermining exactly the
answer-quality concern ADR-0047 was written to address. The pre-existing
≥3-total-stints check (ADR-0045) can be dropped once this lands, since
≥2 *seeded* stints is a strictly more specific requirement that makes the
old, weaker, club-blind count check redundant.
*Accept:* `PathCareerStintFilterTests.cs` cases: exactly 2 qualifying
seeded clubs (included), 1 seeded + 1 non-seeded (excluded), 2 seeded but
one below the appearance threshold (excluded), 2 seeded both above
threshold with extra non-seeded stints mixed in (included, extra stints
ignored). New ADR superseding ADR-0045 (drops the ≥3-stint rule) and
ADR-0047 (raises 1-club to 2-club, keeps the appearance bar) — explicitly
call out this is a deliberate narrowing of the eligible pool and needs the
same "does the pool stay big enough" verification S-141 performs before
this is trusted in production.
*Deps:* S-141 should run immediately after this merges, not independently
scheduled — sequence them in the same session/PR if practical.

**S-139 · xG Path: add a B-team/reserve-team exclusion to `PathCareerStintFilter`**
No B-team concept exists anywhere in the schema — `ClubDefinition` has no
type/tier field, and no B-team clubs are seeded there, so a stint at e.g.
"Real Madrid Castilla," "Barcelona Atlètic," or "Manchester United U21"
passes every existing check unfiltered (it just never counts toward
S-138's seeded-club requirement, but it can still surface as a raw clue-
reveal club name, which is the actual bug this closes). Follow the same
pattern already proven (twice, with two follow-up bug fixes) for national
teams in `PathCareerStintFilter`: a conservative label-matching regex
(candidates: `\b(reserve|reserves|B|II|U1[7-9]|U2[0-3]|castilla|atl[eè]tic)\b`-shaped,
final pattern to be verified against real seeded-club-derived stint data,
not guessed from this list alone) plus explicit test cases for every known
false-positive risk (e.g. a real club whose *proper name* happens to
contain "II," "reserve," or similar — check the current 30-club
`ReferenceDataSeeder.cs` list for any such collision before finalizing the
pattern). **Explicitly document, in the ADR, that this will not be
perfect on day one** — the national-team filter's own history
(ADR-0059→ADR-0063, and the Catalonia/Basque inconsistency found by this
epic's own investigation) shows label-pattern filters for free-text
Wikidata club names get refined iteratively as real false positives/
negatives surface, not solved once.
*Accept:* filter excludes stint rows whose `ClubName` matches the pattern
from both clue-reveal (`PathClueSequenceBuilder`) and S-138's eligibility
check; new ADR (same shape as ADR-0059/0063) records the pattern, its
known limitations, and the false-positive check against the current seeded
club list; test file gets a case per seeded club confirming none of them
are accidentally caught by the new pattern.
*Deps:* S-138 (the B-team exclusion changes which clubs count as
"seeded-club stints" for eligibility, so land it after S-138's redefinition
lands, not before, to avoid re-deriving the eligibility tests twice).

**S-140 · Fix `PathCareerStintFilter`'s inconsistent regional/national-team matching**
Found during this epic's investigation, independent of the 1975/2-club
work above: the current `\bnational\b.*\bteam\b` regex excludes "Catalonia
national football team" but not "Basque Country regional football team" —
a real inconsistency (both are non-club representative sides that should
be excluded on the same principle) that exists purely because of which
word the two labels happen to use, not a deliberate distinction. Broaden
the pattern to also catch `"regional"` + `"team"`/`"representative"`
phrasing. Keep the existing doc-comment discipline of stating plainly what
the filter does and does not prove (no real FIFA-affiliation signal, purely
label-wording) — don't repeat the ADR-0059-era overclaim that got
corrected once already.
*Accept:* new test case locks in "Basque Country regional football team"
as excluded; existing "Catalonia national football team" case still
passes; doc comment reviewed against ADR-0063's correction and not
re-overclaiming. No ADR needed — this is a bug fix to an already-ADR'd
filter's implementation, not a new eligibility-model decision (same bar
S-140's sibling stories in Epic 9 already used for filter bug fixes).
*Deps:* none — can land independently and immediately, before or after
S-137–139.

**S-141 · Re-verify xG Path's eligible-pool size after S-137–140 land, reset target-cycle tracking**
Four eligibility-narrowing changes landing together (1975 floor, 2-seeded-
club requirement, B-team exclusion, broadened regional exclusion) could
plausibly shrink the eligible pool enough to matter for ADR-0058's
target-cycle no-repeat tracking (`PathTargetCycle`) — a smaller pool cycles
back to repeat targets sooner. This is a manual verification + operational
step, not new product code: run `prefetch-player-careers`/`warm-player-cache`
against the post-change filters, query the actual resulting eligible-pool
count (mirroring `audit-club-gaps`'s empirically-grounded approach rather
than guessing), and record the before/after count in `NOTES.md`. If the
pool shrinks below a size where `PathTargetCycle`'s cycle length starts
producing noticeably-repetitive rounds, that's a signal to bring in more
seeded clubs (same `audit-club-gaps` tool, still kept per S-132) rather
than relaxing S-137–140's rules — flag to the product owner rather than
deciding unilaterally which direction to correct in.
*Accept:* `NOTES.md` entry with the actual before/after eligible-player
count against real (dev) data; if the pool drops by more than roughly
half, an explicit escalation note to the product owner rather than silent
acceptance.
*Deps:* S-137, S-138, S-139, S-140 (all must land first — this verifies
their combined effect, not each one's in isolation).

## Epic 13 — Autocomplete: threshold verification + cold-start latency

**S-142 · Document REQ-207's already-implemented 2-character autocomplete threshold**
Investigation confirmed both games and the backend already agree on a
2-character minimum before autocomplete suggestions are fetched/shown:
`frontend/src/grid/GuessInput.tsx` (`MIN_QUERY_LENGTH = 2`),
`frontend/src/path/PathGuessInput.tsx` (same constant, same 150ms debounce,
explicitly mirrors `GuessInput.tsx` per its own comment), and
`backend/src/XGArcade.Api/Players/PlayerAutocompleteEndpoints.cs`
(`MinQueryLength = 2`, enforced server-side independent of the frontend).
**No code change is needed for the "autocomplete after two letters"
request — it already ships everywhere.** The only real gap: REQ-207 in
`requirements-document.md` never states the specific threshold value, so
this fact currently lives only in scattered code comments, not the
requirement itself, which is a real doc-vs-code traceability gap worth
closing on its own.
*Accept:* REQ-207 gains an explicit acceptance-criterion line stating the
2-character minimum, citing all three enforcement points found above so a
future change to any one of them is a REQ violation, not just an
inconsistency between files. CHANGELOG entry noting this was verified
already-correct, not newly built — avoids a future session re-doing this
investigation from scratch.
*Deps:* none.

**S-151 · Warm the database connection/query path on game-screen load, not just the container process**
Reported as "autocomplete feels slow to trigger the first time." Root
cause confirmed, not assumed: the backend Container App is provisioned
with `minReplicas: 0` (`infra/bicep/modules/backend-container-app.bicep:60`)
— it scales to zero on idle and cold-starts on the next request.
`App.tsx:167` already fetches `/health` on app load specifically to wake
the container before the player reaches any game screen, but
`/health` (`EndpointMapping.cs:39`) is `Results.Ok(new { status = "healthy" })`
— a static response with **no DB access**. So the existing warm-up wakes
the ASP.NET Core process but never opens a Postgres connection or compiles
the EF Core query shape `PlayerAutocompleteEndpoints`/
`IPlayerNameIndexRepository` actually use — that cost still lands entirely
on the player's first keystroke. Fix: fire a real DB-touching warm-up call
(a cheap, throwaway `PlayerNameIndex` lookup — e.g. a 1-character query
run server-side only, never exposed to the `MinQueryLength = 2` client
contract, or a dedicated `/players/autocomplete/warmup` endpoint that runs
the same repository call with an empty/trivial filter) alongside the
existing `/health` ping when `GridScreen.tsx`/`PathScreen.tsx` mount —
game-screen load, not app load, so it only fires for players who actually
reach a game, not every app visit (e.g. someone who only opens Settings).
*Accept:* a REQ###-named test (new or extending REQ-207) proves the
warm-up call exercises the real `IPlayerNameIndexRepository` path (not a
mock) so a genuine cold Postgres connection gets opened by it, not by the
player's first real query; the warm-up request is fire-and-forget from the
frontend (never blocks game-screen render, never surfaces an error to the
player if it fails — same "best-effort, no UI impact" shape as the
existing `/health` check's own failure handling); manual verification
against the deployed dev environment (Container Apps' actual scale-to-zero
behavior can't be reproduced in the local/CI stack, which never scales to
zero) confirms perceived first-keystroke latency drops, recorded in
`NOTES.md` with a before/after feel since this can't be asserted by an
automated test. No ADR needed — this is a performance fix within an
already-documented boundary (COMP-10), not a structural change.
*Deps:* none.

## Epic 14 — xG Path: suggestion/correction reporting

**CANCELLED (2026-08-18):** product decision — xG Path does not get a
suggestion/correction entry point. S-143 (the ADR, design-only) already
merged and stays as historical record of the boundary decision it made;
S-144/145/146 (the actual submission route, admin-review wiring, frontend
entry point, and doc sync) are cancelled and will not be built. The
nullable per-game context fields S-143's migration added to
`PlayerSuggestion` stay in place unused (harmless, no reason to churn a
merged migration for a cancelled feature) rather than being rolled back.
This unblocks S-152 (Epic 16), whose "wait for Epics 10-15" sequencing
existed only to avoid building the purge verb against a schema that was
still going to change underneath it — with S-144-146 cancelled, there's no
further schema change coming from this epic.

`PlayerSuggestion`/`SuggestionEndpoints.cs` today are structurally coupled
to xG Grid: `PlayerSuggestion.CellId` (no FK, explicitly flagged in its own
doc comment as a v1 simplification coupling this table to `GridCell`),
`RowCategoryType`/`ColCategoryType` denormalized onto the entity, and the
submission route (`POST /rounds/{roundId}/cells/{cellId}/suggestions`)
resolves category types via `IGameModule.GetCellCategoryTypesAsync`, which
`XGPathGameModule` deliberately implements as a hard
`NotSupportedException` today, with its own comment stating plainly
`SuggestionEndpoints` has no real caller for xG Path in production. This
is a documented, deliberate gap (`requirements-document.md` REQ-215 already
notes "xG Path ever does grow a suggestion entry point, not fixed now"),
not an oversight — closing it is a real boundary decision, not a small
patch.

**S-143 · ADR: generalize `PlayerSuggestion`'s submission context off of `CellId`/row-col category types**
xG Path has no cell/category-pairing concept to plug into the existing
shape — it has a single target player revealed via progressive clue turns
(`PathClueTurn`), so "report a correction" there means "this target
player's asserted nationality/club is wrong," not "this cell's category
pairing is wrong." Recommend mirroring this exact codebase's own
established precedent for cross-game context (ADR-0003: `Round` references
games only via opaque `GameKey`/`GameInstanceId`, never a game-specific FK)
rather than inventing a new pattern: add `GameKey` + a nullable, per-game
opaque context (keep `CellId`/`RowCategoryType`/`ColCategoryType` as
xG-Grid-only-when-populated fields, add `PathInstanceId` as the xG-Path-only
equivalent) rather than a single polymorphic blob column, matching how
`Guess.CellId`'s own "accepted v1 simplification, revisit for a second
game" note from ADR-0003's original entry anticipated exactly this moment.
Write the ADR before any code lands — this is squarely the kind of boundary
change `CLAUDE.md`'s "xG Arcade/game boundary" convention requires stopping
and flagging for, not a routine feature addition.
*Accept:* ADR records the chosen shape, the rejected alternatives (a single
JSON context blob; a fully separate `PathSuggestion` table — rejected
because it'd duplicate `SubmittingUserId`/`Status`/`ResolvedAt`/admin-review
plumbing Epic 15 also depends on being unified across both games), and an
explicit note that `AssertedClubs`/`AssertedNationality`'s existing shape
already generalizes fine (a suggestion is always "this player's true
data is X," regardless of which game surfaced the report).
*Deps:* none (design-only story).

**S-144 · CANCELLED — Backend: xG Path suggestion submission + admin review**
Implements S-143's chosen shape: migration adding the new nullable
context field(s) to `PlayerSuggestion`, a new submission route scoped to
xG Path (either a new `POST /path/rounds/{roundId}/suggestions` mirroring
the existing route's shape, or widening the existing route to branch on
`GameKey` — S-143 should settle which), and replacing
`XGPathGameModule.GetCellCategoryTypesAsync`'s current
`NotSupportedException` with real behavior only if S-143's shape still
calls that method for xG Path at all (it may not, if the new route bypasses
it entirely — confirm against S-143's ADR before assuming this method
needs touching). `AdminSuggestionEndpoints.cs`'s existing review/commit/
reject flow needs no game-specific change if S-143's shape keeps
`PlayerName`/`AssertedNationality`/`AssertedClubs` as the single
game-agnostic payload admins review — verify this holds before adding any
xG-Path-specific admin UI branching.
*Accept:* `SuggestionEndpointTests.cs` gains xG-Path submission coverage
(equivalent structure to existing xG-Grid tests); `AdminSuggestionEndpointTests.cs`
confirms a xG-Path-originated suggestion reviews/commits/rejects through
the exact same admin flow with no special-casing; REQ-215 updated from
"xG Grid only" to cover both games, with a status note explaining the
context-field generalization.
*Deps:* S-143.

**S-145 · CANCELLED — Frontend: xG Path "report a correction" entry point**
Add the equivalent of xG Grid's existing suggestion-report UI trigger
(locate and mirror whichever component currently opens the report flow
from a grid cell/guess result) to `PathScreen.tsx`/`PathGuessInput.tsx` or
the clue-reveal UI, wired to S-144's new endpoint. Same review-before-submit
discipline the existing Grid flow already has (REQ-215's existing
acceptance criteria — editable fields, not auto-submitted).
*Accept:* `PathScreen.test.tsx` gains coverage for opening/submitting/
canceling the report flow, mirroring the existing Grid suggestion
component's test shape; no new design tokens introduced (reuse existing
suggestion-flow styling per `design-document.md` §2's token-reuse rule).
*Deps:* S-144.

**S-146 · CANCELLED — Doc sync: REQ-215/architecture-document.md reflect xG Path suggestion support**
Moot: S-144/145 (the behavior this doc sync would describe) are cancelled,
so REQ-215's "not fixed now" status note about xG Path stays accurate as
written and needs no update.
*Deps:* S-143, S-144, S-145.

## Epic 15 — Settings: a user's own suggestion history

**CANCELLED (2026-08-18):** product decision — no suggestion-history
feature in Settings. S-147/148/149/150 below are cancelled and will not
be built; no code or schema exists for this epic today, so cancelling it
leaves nothing to roll back. This is one of the two epics S-152 (Epic 16)
was waiting to merge before proceeding — with it cancelled instead, S-152
is unblocked.

No REQ, repository method, or UI pattern for this exists today — confirmed
by investigation, not assumed. `PlayerSuggestion.SubmittingUserId` is
already captured on every suggestion (submitted, no FK, matching
`Guess.UserId`'s deletion-safe precedent), so the data needed already
exists; only the query/mutation/UI surface is missing.
`IPlayerSuggestionRepository` today exposes exactly `AddAsync`/
`GetPendingAsync` (Pending-only, admin-wide)/`GetByIdAsync`/`ResolveAsync` —
no per-user filter, no query for `Committed`/`Rejected` rows, no
delete/dismiss mutation anywhere in the codebase to copy from (checked
`IncidentReport` — no local persistence at all, not a usable precedent).

**S-147 · CANCELLED — Backend: `GET /me/suggestions` — a user's own suggestion history, all statuses**
New repository method (`GetBySubmittingUserIdAsync(userId)`, no status
filter — unlike admin's `GetPendingAsync`, a user should see their
`Pending`/`Committed`/`Rejected` suggestions all at once) and a new
authenticated (non-admin — any logged-in, non-guest user viewing their own
data) endpoint. Response shape: player name, asserted data, status,
submitted/resolved timestamps — deliberately **no denial-reason field**,
since none exists on the entity today and REQ-215's original submission
flow never collected one either (matches the codebase's existing "reject
has no reason" precedent on the admin side — don't invent asymmetry
between what an admin sees and what the submitter sees).
*Accept:* new REQ (REQ-511, next free ID in that block) with Given/When/Then
covering all three statuses appearing, a guest user rejected 403 (matches
REQ-215's own guest-rejection precedent), and a user never seeing another
user's suggestions (authorization test, same bar as every other
`/me/*`-shaped endpoint in this codebase); repository test coverage
mirroring `PlayerSuggestionRepository`'s existing test shape.
*Deps:* none (pure additive read, no schema change).

**S-148 · CANCELLED — Backend: let a user clear their own resolved (confirmed/denied) suggestions**
No soft-delete/dismiss/clear concept exists anywhere in this codebase
today — this establishes the first one, so keep it narrow and reversible-
in-spirit: add a nullable `ClearedByUserAt` timestamp column (not a hard
delete) so a cleared row disappears from the user's own `/me/suggestions`
view but the admin audit trail (`ResolvedByAdminId`/`ResolvedAt`,
`GetPendingAsync`'s own scope) is completely untouched — `GetPendingAsync`
already only ever selects `Status == Pending`, so this new column can never
affect it regardless. **Only `Committed`/`Rejected` (i.e., already-resolved)
rows may be cleared** — a `Pending` suggestion has nothing to "clear," it's
still awaiting review, so reject with 409 (same conflict-shape precedent as
the admin endpoints' existing 409-on-already-resolved) if a user tries to
clear a still-pending one. New endpoint, same auth model as S-147
(submitter-only — 403 if the authenticated user doesn't own the row, 404
if it doesn't exist, matching the existing admin-endpoint error-shape
conventions).
*Accept:* new REQ (REQ-512) covering: clearing a `Committed` row hides it
from `/me/suggestions` but not from any admin view; clearing a `Pending`
row 409s; clearing another user's row 403s; a cleared row's underlying
`PlayerSuggestion`/`PlayerSuggestionClub` rows are never deleted (data
integrity/audit-trail test). New ADR — first soft-delete/dismiss pattern
in the codebase, "could reasonably have gone another way" (hard delete of
resolved rows was considered and rejected specifically because it would
destroy the admin's `ResolvedByAdminId` audit trail for no benefit — record
that reasoning).
*Deps:* S-147 (same entity/endpoint family, land together or immediately
after).

**S-149 · CANCELLED — Frontend: "My suggestions" section in `SettingsScreen.tsx`**
`SettingsScreen.tsx` currently has no list/history UI at all — every
existing section is a single form or link. Add a new section (after the
existing admin-link/appearance sections, before account deletion, matching
the screen's existing top-to-bottom ordering convention of least-to-most
destructive) listing the user's suggestions from S-147, reusing
`SuggestionsScreen.tsx`'s `PendingSuggestionRow` visual pattern as the
closest existing precedent rather than inventing new list styling.
**User-facing status wording is "Confirmed"/"Denied"** (matching the
product ask's own words) even though the backend enum stays
`Committed`/`Rejected` — this is a presentation-layer mapping only, not a
backend rename, avoiding unnecessary churn to an already-shipped enum
(`PlayerSuggestionStatus`) with no functional reason to change. Each
`Committed`/`Rejected` row gets a "Clear" button wired to S-148; `Pending`
rows show no clear action (nothing to clear yet, per S-148's own rule).
*Accept:* `SettingsScreen.test.tsx` gains coverage for all three statuses
rendering with correct user-facing labels, the clear action removing a
row from the list (optimistic or refetch, matching this codebase's
existing `SuggestionsScreen.tsx` commit-confirmation pattern rather than
inventing a new state-management shape), and a `Pending` row rendering
with no clear button. No new design tokens (reuse existing list/row
styling per `design-document.md` §2).
*Deps:* S-147, S-148.

**S-150 · CANCELLED — Doc sync: REQ-511/512, architecture-document.md, design-document.md for the Settings suggestion-history feature**
Moot: S-147/148/149 (the behavior this doc sync would describe) are
cancelled, so REQ-511/512 are never drafted and no doc sync is needed.
*Deps:* S-147, S-148, S-149.

## Epic 16 — Pre-launch clean slate

**S-152 · `purge-game-history` CLI verb + confirmation-gated workflow: wipe all rounds, guesses, grids, and paths**
Deletes every historical `Round`, `GridInstance`(+`GridCell`), `PathInstance`
(+`PathPuzzle`), `PathTargetCycle`, and `PathCycleTargetUsage` row so the
platform starts with zero game history once this whole overhaul (Epics
10-15) is done. Follows `purge-player-pool.yml`'s exact precedent: a plain
CLI verb (`dotnet run -- purge-game-history`), never an HTTP endpoint (same
ADR-0024 long-running-bulk-delete-is-a-CLI-verb reasoning), a
`workflow_dispatch`-only workflow requiring the same typed exact-phrase
confirmation input `purge-player-pool.yml` already uses, and — learn from
`purge-player-pool.yml`'s own very recent incident (2026-08-17,
`18640c9`) — a verb-scoped extended `Database.SetCommandTimeout` from the
start, not discovered the hard way after this table has grown.
**Two things confirmed against `XGArcadeDbContext.cs`'s actual FK
configuration, not assumed:**
1. Deleting `Round` **cascades to `Guess`** (`Guess.RoundId`,
   `DeleteBehavior.Cascade`) **and also to `PlayerSuggestion`**
   (`PlayerSuggestion.RoundId`, same cascade) — the second cascade isn't
   something the "rounds, guesses, grids, paths" request named explicitly.
   **Decision, made here rather than left as a silent side effect:**
   include it — a pre-launch clean slate should also clear suggestion-review
   history accumulated during this overhaul's own testing, not just
   gameplay data, and there's no product reason to keep pre-launch test
   suggestions once the game itself resets. If that's wrong, this story's
   own explicit callout is exactly what makes it easy to catch and change
   before running it for real, rather than discovering it after the fact.
2. `Round.GameInstanceId` is deliberately unconstrained (ADR-0003) and
   `PathTargetCycle`/`PathCycleTargetUsage` have no FK to `Round`/
   `PathInstance` at all (`PlayerId`+`CycleNumber`-keyed only) — none of
   this is reachable via cascade from `Round`/`GridInstance`/`PathInstance`
   deletes, so the verb must delete these two tables explicitly in the same
   transaction, not assume a cascade covers them. Skipping this would leave
   xG Path's ADR-0058 cycle state referencing a cycle whose actual rounds
   no longer exist — a real, silent correctness gap for the *next* round
   generated post-wipe, not just leftover clutter.
**Explicitly out of scope — never touched by this verb:** `User`,
`League`/`LeagueMembership` (a fresh global league already exists per
REQ-401's invariant; wiping it would break that), and every player-data
table (`Player`, `PlayerData`, `PlayerAttribute`, `PlayerOverride`,
`PlayerAlias`, `PlayerCareerStint`, `PlayerNameIndex`) plus every reference
table (`ClubDefinition`, `CountryDefinition`, `TrophyDefinition`,
`GridTemplate`, `PathTemplate`) — this whole overhaul exists to build that
data *up*, and this story is explicitly about resetting *game history*,
never the player database underneath it. If a genuinely full reset is ever
wanted later, that's `purge-player-pool.yml`'s job, run separately and
deliberately, not folded into this one.
**Sequencing: land and run this LAST**, after Epics 10-15 are all either
merged or (as of 2026-08-18) formally cancelled, not before and not
mid-overhaul. Reasons, not just preference: Epic 11 (S-135) adds
`Round.SequenceNumber` with a backfill for existing rows — running the
wipe first makes that backfill work pointless (every row it'd backfill
gets deleted anyway) and running it last means the fresh post-launch
rounds start a clean `SequenceNumber` sequence from 1 with no backfill
math involved at all. Epic 14/15 (S-143-150) were going to change
`PlayerSuggestion`'s `RoundId`/`CellId` coupling further — building this
verb against the *final* post-overhaul schema avoids writing it twice.
**Epic 14/15 are now cancelled** (S-143's ADR-0076 stays merged as-is;
S-144-150 will not be built — see each epic's cancellation note above), so
this gate is satisfied: Epics 10-13 are merged, and 14/15 have no further
schema change coming. Building this verb now, against the current schema,
is the correct read of this section's own intent.
*Accept:* new `IRoundRepository`/equivalent method(s) performing the
delete in one transaction (mirroring `PlayerRepository`'s existing
cascade-delete pattern for `purge-player-pool`); the workflow's
`confirmation` input must exactly match a fixed phrase (e.g. `"reset all
game history"`, distinct from `purge-player-pool.yml`'s own phrase so the
two can never be confused/fat-fingered) or the job fails before touching
the DB; a REQ###-named test (repository-level, against the InMemory
provider per this codebase's existing precedent for bulk-delete verbs)
proves `League`/`LeagueMembership`/`Player`/every reference table are
provably untouched — row counts before/after, not just "no exception
thrown"; `NOTES.md` gets an entry recording the actual row counts wiped the
first time this runs for real, same as `purge-player-pool.yml`'s own
incident-log discipline. No ADR needed — this is a self-contained
operational tool with no REQ/ADR-level decision behind it, matching
`purge-player-pool.yml`'s and the other maintenance verbs' own precedent.
*Deps:* none to build; **do not run for real before Epics 10-15 are merged**
(see Sequencing above) — `TODO.md`'s pre-launch checklist gets the actual
"run it" step so this isn't only a capability sitting unused.

**Built 2026-08-18 (code/tests/workflow only — NOT run for real, per this
story's own sequencing gate; `TODO.md`'s pre-launch checklist item is the
actual "run it" trigger).** Two deliberate deviations from this story's own
*Accept* text, both for concrete reasons:
- The delete logic lives in a new static `GameHistoryPurger`
  (`backend/src/XGArcade.Data/Seeding/GameHistoryPurger.cs`), not an
  `IRoundRepository` method — that interface is Round-scoped only, and this
  is a 7-table cross-cutting delete; matches the existing
  `PathTargetCycleResetter`/`StaleClubAttributeCleaner`/
  `PairLookupFailureCleaner`/`DuplicateCareerStintCleaner` precedent in the
  same folder (each its own standalone static class, not shoehorned into an
  unrelated repository interface).
- Tests (`backend/tests/XGArcade.Data.Tests/GameHistoryPurgerTests.cs`) are
  NOT REQ###-named — this story's own text says "No REQ/ADR-level decision
  behind it," and the existing REQ-less-maintenance-tool precedent in the
  same test folder (`UserDisplayNameBackfillerTests`,
  `PlayerNormalizedFullNameBackfillerTests`, `PlayerNameIndexWordBackfillerTests`,
  `PlayerAliasNormalizedAliasBackfillerTests`) omits the REQ### prefix
  entirely rather than inventing one.

Also: `GameHistoryPurger` does NOT rely on EF Core's configured
`DeleteBehavior.Cascade` at runtime for `Guess`/`PlayerSuggestion`(+
`PlayerSuggestionClub`)/`GridCell`/`PathPuzzle` — that cascade only fires
for entities already tracked in the context (client cascade) or via a real
relational database's own `ON DELETE CASCADE` (production Npgsql only).
The InMemory provider this story's own acceptance criteria require testing
against has neither, so every table is loaded and removed explicitly; see
that class's own doc comment for the full reasoning. One `SaveChangesAsync`
call for the whole purge (not one per table) is what actually satisfies the
"one transaction" criterion — no explicit `BeginTransactionAsync` needed
(and one would break the InMemory-provider tests, which don't support real
transactions).

Files: `backend/src/XGArcade.Data/Seeding/GameHistoryPurger.cs` (new),
`backend/src/XGArcade.Api/CompositionRoot/CliVerbDispatcher.cs` (new
`purge-game-history` verb), `backend/tests/XGArcade.Data.Tests/GameHistoryPurgerTests.cs`
(new), `.github/workflows/purge-game-history.yml` (new). No .NET SDK was
available in the sandbox this was built in — code was hand-traced against
concrete scenarios, not compiled/run; `dotnet build`/`dotnet test` must run
in CI before this is considered verified.

## Epic 17 — Technical debt remediation, round 4 (`CODE_HEALTH_ASSESSMENT.md` follow-up, 2026-08-18 sweep)

Source: `CODE_HEALTH_ASSESSMENT.md` (2026-08-18 revision) and
`CODEBASE_ANALYSIS.md` (2026-08-18 revision), the `code-health-auditor`
agent's periodic sweep. Same house rules as Epics 7-9: independent of the
Tier 0 build sequence, **every story here is a pure refactor/doc-sync/
test-addition — no behavior change, no new REQ IDs**. Before writing this
epic, every finding was cross-checked against Epic 9 (`docs/backlog.md`
S-115–S-129) and `CODEBASE_ANALYSIS.md`'s prior top-10 list to avoid
re-flagging completed work — the majority of both had already shipped
since the 2026-08-11 sweep (`GridGameModule.cs` split/ADR-0068,
`LeaderboardScreen.tsx` split, `useAuthedFetch` hook, `WikidataClient.cs`'s
remaining query methods migrated onto the shared driver, `PlayerStoreRepository`
split/ADR-0067, `frontend/src/lib/api.ts` split, `CliVerbDispatcher.cs`
restructured to a verb registry, `CompositionRoot` testing strategy
decided, `architecture-document.md` §5/§6 slimmed). This sweep found and
fixed three small items Epic 9 had explicitly deferred (S-124/S-125/S-126
— see their own "Built as" notes above, all closed in this sweep, not
carried into this epic) and identified the items below as the next real
gaps, prioritized by complexity × churn per this agent's own mandate, not
score alone.

**S-154 · `XGPathGameModule.cs`: extract the eligibility pipeline into a dedicated service**
`CODE_HEALTH_ASSESSMENT.md`'s 2026-08-11 revision flagged this file (then
423 lines) as "already shows the same multi-concern-method pattern XGGrid
took at this size — a clear pre-emptive refactor candidate." That
prediction has materialized: the file is now 557 lines (+32%) with 8
commits since (S-137/138/139/141, the highest churn count of any
non-generated backend file this sweep found besides `CliVerbDispatcher.cs`,
whose growth is healthy verb-registry breadth, not concern-mixing — see
this epic's intro and `CODE_HEALTH_ASSESSMENT.md` §4 for why the two read
differently despite similar churn). `GetEligiblePlayerIdsAsync`/`IsEligible`
(~150 lines together) form a genuinely separable "eligibility pipeline"
concern — candidate narrowing, stint sanitization, three structural checks,
birth-year filtering, familiarity filtering — distinct from
`GenerateInstanceAsync`'s own orchestration concern (template lookup,
cycle rollover, selection, persistence) and from `ScoreSubmissionAsync`'s
scoring concern. Extract into `IPathEligibilityService`/`PathEligibilityService`,
composed behind `XGPathGameModule` (now a thinner `IGameModule` adapter),
mirroring ADR-0068's `GridGameModule` split precedent exactly — same
"no facade, `IGameModule` keeps its real external callers" shape.
*Accept:* `XGPathGameModuleTests.cs` coverage moves/renames to match the
new class boundary rather than being rewritten — structural-only change,
no behavior change; `IGameModule`'s public contract unchanged; the
fetch→sanitize→eligibility-check ordering invariant (REQ-1203, locked by
S-139's own comment block) is preserved byte-for-byte, not just
behaviorally — that comment block moves with the code it documents. Add
an ADR per `CLAUDE.md`'s own "could reasonably have gone another way"
test, same bar ADR-0068 already applied to the equivalent xG Grid split.
*Deps:* none.
**Built as (2026-08-22):** matches the plan exactly — `GetEligiblePlayerIdsAsync`/
`IsEligible` and their four supporting constants extracted verbatim into
new `IPathEligibilityService`/`PathEligibilityService.cs` (363 lines),
registered independently (`AddScoped`, `ServiceRegistration.cs`,
immediately before `AddScoped<IGameModule, XGPathGameModule>()`).
`XGPathGameModule.cs` went from 632 → 291 lines; it keeps implementing
`IGameModule` directly (no facade) and still owns target-selection/cycling,
clue reveal, and scoring. `IPlayerRepository` is now injected on both
classes — a small, deliberate duplication accepted for the same reason
ADR-0068 accepted it for `IGridInstanceRepository` (see ADR-0082's
Consequences section). `XGPathGameModuleTests.cs` (1,493 lines/50 methods)
split 1:1: 26 eligibility-rule tests moved/renamed into new
`PathEligibilityServiceTests.cs` (reshaped to assert directly on the
returned id list rather than the original's indirect
`PathGenerationException` proxy, the same allowance ADR-0068 used for its
own REQ-211 tests); the remaining 24 adapter-orchestration tests stayed in
`XGPathGameModuleTests.cs`, whose `BuildModule` now composes a real
`PathEligibilityService`. The REQ-1203 fetch→sanitize→collapse→eligible-check
ordering invariant comment moved byte-for-byte, verified by diff against
the pre-refactor file. New ADR-0082 records the decision (including the
two alternatives considered and rejected: leaving it as one class, and
splitting the Player-level floors out further). `docs/architecture-document.md`
(v1.10, COMP-11 row) and `docs/requirements-document.md` (v1.95, REQ-1201/
1203 test-level references) synced in the same iteration — see
`docs/CHANGELOG.md`'s 2026-08-22 entry for the full doc-sync detail.
Reviewed by `architecture-reviewer`/`quality-architect` before the ADR was
written (commit `490186a`); no `dotnet` SDK available in this sandbox, so
the split was verified by direct diff/hand-trace, not a local test run —
must be (and was) verified green in CI before merge.

**S-155 · `WikidataClient.cs`: split query-building/parsing helpers out of the client class — SHIPPED**
`CODE_HEALTH_ASSESSMENT.md`'s 2026-08-11 revision scored this file 2.5/10
and recommended splitting into `SparqlQueryBuilder`/`SparqlQueryRunner`/
`SparqlResponseParsers`. S-118/S-124 (Epic 9, both complete as of this
sweep) fixed the specific defect that made it a 2.5 — 9+ near-duplicated
HTTP-handling blocks, now consolidated behind two shared drivers
(`RunIntersectionQueryAsync`/`RunThrowingQueryAsync`) — so the duplication-
driven urgency is gone. What remains is a breadth/SRP concern only: the
file (1,775 lines) still holds every `Build*Query`/`Parse*Bindings` helper
for all ~15 query methods alongside the two drivers and the client's own
public surface. Lower priority than S-154 above (no duplication risk, no
recent defect history, moderate churn) — included because the original
recommendation's second half was never done, not because it's urgent.
Split `Build*Query`/`Parse*` static methods into `SparqlQueryBuilders`/
`SparqlResponseParsers` (both stateless, dependency-free, trivially
testable in isolation per the original recommendation's own reasoning);
`WikidataClient` keeps the two `Run*` drivers and its public
`IWikidataClient` methods, now thinner wrappers delegating to the moved
helpers.
*Accept:* every existing `WikidataClientTests.cs` case passes unchanged
(pure move, no behavior change); if any `Build*`/`Parse*` method is tested
indirectly only (via the client's public methods), that indirect coverage
is preserved, not weakened; line count reduction of `WikidataClient.cs`
reported in the PR description. Judgment call, flag for
`architecture-reviewer`: whether the moved helpers land in the same
`XGArcade.DataSync/Wikidata/` folder as new files or a `Wikidata/Sparql/`
subfolder — this story doesn't decide that, since it "could reasonably go
another way" and isn't load-bearing for the refactor's value.
**Built as:** `WikidataClient.cs` 1,775 → 782 lines (a 993-line/56%
reduction). Two new files, both landing flat in the existing
`XGArcade.DataSync/Wikidata/` folder rather than a new `Wikidata/Sparql/`
subfolder — `architecture-reviewer` resolved the judgment call above in
favor of flat, matching the `IntersectionQuerySpecs.cs` precedent already
in that same folder from S-100/S-101, with no other DataSync subfolder
convention to justify a new one: `SparqlQueryBuilders.cs` (456 lines,
every `Build*Query` static helper, plus the three builder-only constants
`MaleWikidataQid`/`DateOfBirthCutoff`/`NationalTeamClassWikidataQid` moved
as `internal const`) and `SparqlResponseParsers.cs` (592 lines, every
`Parse*Bindings`/`ParseBindings` static helper plus the
`SparqlResponse`/`SparqlResults`/`SparqlValue` JSON-shape records).
`WikidataClient.cs` now holds only its constructor/fields, the two `Run*`
drivers (`RunIntersectionQueryAsync`/`RunThrowingQueryAsync`), the private
`QueryIntersectionAsync` dispatcher, and its public `IWikidataClient`
methods as thin wrappers delegating to the moved helpers — a pure move,
zero behavior change. No `WikidataClientTests.cs` changes needed; every
case passes through the same unchanged public surface.
`architecture-reviewer` confirmed no boundary violations (entirely
internal to COMP-07/`XGArcade.DataSync`, doesn't touch ADR-0003's
Core/game boundary or any other architectural boundary) and that this is
file organization, not a structural decision — no ADR needed. No `dotnet`
SDK available in the sandbox that built this; the test suite could not be
run locally and must be verified green in CI before merge.
*Deps:* none.

**S-156 · Add direct tests for `AdminScreen.tsx`'s remaining untested subcomponents — SHIPPED, 2026-08-22**
`CODEBASE_ANALYSIS.md`'s 2026-08-11 revision (#2, P2) flagged 9 extracted
`AdminScreen.tsx` subcomponents with zero dedicated tests. Since then,
5 gained their own test file (`AccountMetricsSection`, `AnnouncementBannerSection`,
`IncidentReportsEntry`, `PlayerSuggestionsEntry`, `XGPathCycleSection`) —
confirmed via direct file listing this sweep. Four remain covered only
indirectly through `AdminScreen.test.tsx` (1,332 lines, itself evidence of
the gap): `GuestClearSection.tsx`, `RoundControlSection.tsx`,
`UnverifiedDataSection.tsx`, `UserDeletionSection.tsx`.
*Accept:* each of the four gains its own `*.test.tsx` file covering its
own render/interaction/error paths (mirroring the shape the 5 already-split
files use); `AdminScreen.test.tsx` is trimmed of any case now redundant
with the new dedicated files (not left duplicated); full `npm run test`
suite passes with the same or greater total assertion count, not fewer.
*Deps:* none.
**Built as:** matches the plan — added `GuestClearSection.test.tsx`,
`RoundControlSection.test.tsx`, `UnverifiedDataSection.test.tsx`, and
`UserDeletionSection.test.tsx`, each rendering its component directly
(not through `AdminScreen`) and stubbing only the fetch routes that
component itself calls, mirroring the S-108 batch-1 shape.
`AdminScreen.test.tsx` was trimmed of the now-redundant per-subcomponent
render/interaction/error-path cases the new files own, but keeps its own
composition/wiring coverage (fetch-on-mount, real — not mocked —
`onRefresh` round-tripping through `UnverifiedDataSection` and
`RoundControlSection`, and the activeRound-gated show/hide of
`RoundControlSection`+`UserDeletionSection`); a quality-architect review
of the first pass found one composition case (`RoundControlSection`'s
real-`onRefresh` coverage) had been dropped rather than migrated, fixed in
a follow-up commit restoring it. Full frontend suite (`npm run test`):
613 → 646 tests (44 files), all passing; `tsc -b`/`oxlint` clean. Pure
test-coverage addition — no production code, REQ, component boundary, or
data-model change; `architecture-reviewer` and `quality-architect` both
reviewed and passed the diff.

**S-157 · Continue `useAuthedFetch` migration to `GridScreen.tsx`/`PathScreen.tsx`/`AdminScreen.tsx`**
S-120 (Epic 9) added the shared hook and migrated one screen
(`LeaguesScreen.tsx`), explicitly scoping further migration to "follow-up
stories, not all in one PR." Confirmed via grep this sweep:
`GridScreen.tsx`, `PathScreen.tsx`, and `AdminScreen.tsx` still hand-roll
their own `phase: 'loading'|...` fetch-on-mount state independently and
match the hook's covered shape (simple fetch-on-mount, not the leaderboard
scopes' poll/pagination lifecycles S-121 found didn't fit). Migrate one or
more of these three, whichever proves lowest-risk first — this story
doesn't mandate all three in one PR, following S-120's own precedent.
*Accept:* each migrated screen's existing tests pass unchanged (no
visual/behavior change); `oxlint`/`tsc -b`/`vitest` clean.
*Deps:* S-120 (already complete).
**Built as (2026-08-22):** migrated `AdminScreen.tsx` only, onto two
independent `useAuthedFetch` instances (one per endpoint), preserving each
endpoint's own refetch granularity and the pre-existing page-wide-403 and
probe-swallows-to-null behaviors unchanged. `GridScreen.tsx`/`PathScreen.tsx`
were evaluated and ruled out for this pass: both need to mutate their
fetched state after a guess submission, which `useAuthedFetch` doesn't
support since it exposes no setter. One test-coverage gap found and closed
along the way (the active-round probe's swallow-non-401/403/404-to-null
boundary was previously untested). Full detail: `docs/CHANGELOG.md`,
2026-08-22 entry (S-157).

**S-158 · Extract `App.tsx`'s auth-session lifecycle into a `useSession()` hook**
Carried over from `CODEBASE_ANALYSIS.md`'s original (2026-08-10) Quick Win
list — never done. `App.tsx` (603 lines) still mixes routing, dialog
state, and ~150 lines of self-contained auth-session lifecycle
(`handleAuthenticated`/`handleLogout`/`attemptSilentRefresh`/the `fetchMe`
effect) in one component, confirmed unchanged this sweep (`grep -n
useSession` returns nothing in `App.tsx`).
*Accept:* `App.test.tsx` (and the broader `tests/unit/App.test.tsx` suite)
passes unchanged — pure extraction, no behavior change; `App.tsx`'s line
count reduction reported in the PR description.
*Deps:* none.
**Built as (2026-08-22):** extracted verbatim into `frontend/src/lib/
useSession.ts` — `accessToken`/`currentUser` state, `isGuest`,
`handleAuthenticated`, `handleLogout`, `attemptSilentRefresh`, and the
`fetchMe` effect, mirroring `useThemePreference`'s (`frontend/src/lib/
theme.ts`) hook-module style. `App.tsx` (649 → 529 lines) keeps only
routing/dialog state; `handleLogout`'s three inline routing side effects
(reset `screen`, hide `AuthScreen`, clear the hash) became an `onLoggedOut`
callback `useSession` invokes at the same point in its own sequence.
`handleAuthenticated`'s `navigateTo('game-select')` call similarly stayed
in App.tsx (routing, not session state) — `useSession`'s own
`handleAuthenticated` now only does the token-storage/state half, and
App.tsx's `AuthScreen onAuthenticated` prop calls it followed by
`navigateTo('game-select')`, same order as before. One real bug caught by
the test suite during this extraction, not present in the final code: the
first version passed `onLoggedOut` as an unmemoized inline closure, which
gave `useSession`'s `handleLogout` (and, through its dependency array, the
`fetchMe` effect) a new identity on every `App` render — re-fetching
`/auth/me` far more often than intended and clobbering local `currentUser`
updates like the account-claim flow's `onAccountClaimed`. Fixed by wrapping
that callback in `useCallback([])` in `App.tsx`. `ACCESS_TOKEN_STORAGE_KEY`
is now exported from `useSession.ts` (App.tsx still reads it directly for
the `screen` initializer and the mount-only hash-sync effect, both routing
concerns). Full frontend suite (647/647, including `tests/unit/
App.test.tsx`'s 12) passes unchanged; `oxlint`/`tsc -b` both clean.

**Watch-only (no story, low churn/not yet a problem):**
- `frontend/src/admin/SuggestionsScreen.tsx` (697 lines, now the largest
  frontend file post-S-129): inspected this sweep and found genuinely
  cohesive — one feature (REQ-509/510), two entry points sharing one
  `PlayerReviewPanel` component deliberately, not four unrelated concerns
  bundled the way pre-split `LeaderboardScreen.tsx` was. Has its own
  `SuggestionsScreen.test.tsx` (466 lines, a healthy ~0.67x ratio, unlike
  the `AdminScreen.tsx` pattern above). Low churn (fresh from S-129,
  2026-08-17). No action; re-check if it keeps growing.
- `WikidataClientTests.cs` (3,973 lines, now the single largest file in
  the repo): growing for legitimate regression-proof reasons (S-118/S-124/
  S-127 each added real byte-for-byte assertions). A future split-by-
  query-family pass would help navigability but isn't urgent — same
  judgment `CODEBASE_ANALYSIS.md`'s 2026-08-11 revision already made and
  still holds.
- `docs/backlog.md` (6,627 lines) and `docs/CHANGELOG.md` (7,732 lines):
  both inherently high-churn, append-only-by-design working documents
  (every story/every doc change adds one entry) — not the same accretion
  failure mode `architecture-document.md`'s COMP-xx cells had, since each
  entry here is a distinct, dated, atomic record rather than a single
  cell being repeatedly rewritten in place. No action.
- `CliVerbDispatcher.cs` (736 lines, 12 commits since June — the highest
  raw churn count of any backend source file this sweep found): confirmed
  healthy despite the churn — a verb-registry pattern (S-112) where every
  new CLI operation is a new, independent, consistently-shaped entry
  (`Verbs` dictionary + one handler method reusing `BuildDbContext()`/
  `BuildLoggerFactory()`, S-114's fix still holding, zero duplication
  found on inspection). This is the "high churn but well-shaped" case
  CodeScene's own methodology distinguishes from a real hotspot — the
  size is proportional to the number of distinct operations (14 verbs),
  not concern-mixing. No action.

## Epic 18 — Cache warming: eliminate live pairwise Wikidata queries via local derivation

Origin: a real `warm-grid-cache.yml` run (2026-08-18) logged `199 of the
199 queried-live pairs hit a technical failure` — a 100% failure rate on
the club×club combinatorial-join query shape, concentrated entirely on
large historic clubs (Manchester City, Bayern Munich, Real Madrid, PSG,
Barcelona, ...). Investigation traced this to `PlayerCacheWarmingService`
issuing a live pairwise SPARQL intersection query per Country×Club/
Club×Club pair even though `prefetch-player-careers` (ADR-0055/ADR-0069)
already sweeps the exact same seeded reference data unpaired — the fix is
to let the two jobs' data actually compose instead of running fully
independent live-query paths against the same reference tables.

**S-159 · `PlayerCareerPrefetchService` populates `PlayerAttribute` from its existing pool sweeps — SHIPPED**
Every player in a country's/club's full pool sweep satisfies that
nationality/club attribute by construction of the pool query's own WHERE
clause — no extra Wikidata call needed to know it. Writing that fact as a
`PlayerAttribute` (paired with a `PlayerData` row, `Source="wikidata"`/
`Confidence="verified"`) lets `PlayerCacheWarmingService`'s existing local
`CountPlayersWithBothAttributesAsync` pre-check become the *complete*
answer once both sides of a pair have been swept — the live pairwise query
is then never issued for that pair at all, including the exact
combinatorial big-club joins that were failing 100% of the time.
`PlayerCacheWarmingService.cs` itself needed no code change; its existing
`cachedCount >= MinValidAnswers` check just starts being right more often.
REQ-110 (extended), ADR-0077 (deliberate narrow reversal of ADR-0001's
incremental-only `PlayerAttribute` principle, scoped to the seeded-
reference subset).
**Built as:** matches the plan, plus two quality-gate correction rounds
worth knowing about if this pattern gets reused elsewhere — (1) the first
implementation pass omitted the paired `PlayerData` write that every other
automated Wikidata-derived `PlayerAttribute` write in this codebase
includes (`WikidataLookupService.QueueAttribute`'s established shape,
required for REQ-502's source/confidence traceability) — caught by both
`architecture-reviewer` and `quality-architect` independently, fixed; (2)
the fix for a separate club-name-sourcing concern initially routed the
club attribute value through `clubNameByClubQid` (the QID→name map
`PlayerCareerStint.ClubName` uses) to match that write's own sourcing —
which was itself wrong and reverted after a follow-up architecture-review
pass: the club attribute value must come from `club.Name` directly, since
that's what `PlayerCacheWarmingService`'s own join key is sourced from,
not from a QID map built for an unrelated purpose (resolving an arbitrary
per-stint club QID pulled from a player's full Wikidata career, not
identifying the specific `ClubDefinition` row this loop is sweeping). See
ADR-0077's "Correction (2026-08-18)" section for the full reasoning.
Never actually compiled in the sandbox that built it (no `dotnet` SDK
available there) — first CI run on the branch is the real verification.
*Deps:* none (ADR-0055/ADR-0069 already shipped).

**S-160 · `warm-grid-cache`: mark a swept-but-genuinely-low pair `ConfirmedLowMatchPair` without a live round-trip — SHIPPED**
S-159's own follow-up, flagged in ADR-0077's Consequences section rather
than solved there. Once a pair's both sides have been fully swept by
`prefetch-player-careers`, `PlayerCacheWarmingService`'s local
`CountPlayersWithBothAttributesAsync` count is not just a cache hint, it's
the *true, final* count for that pair — so a pair that's fully swept but
still below `MinValidAnswers` is already known to be genuinely low without
needing a live Wikidata round-trip to confirm it (today's code doesn't
know this: `PlayerCacheWarmingService.WarmAsync`'s `else` branch still
issues a live query for `cachedCount < MinValidAnswers` regardless of
whether both sides are fully swept, then persists `ConfirmedLowMatchPair`
based on *that* query's result). Needs a way for `PlayerCacheWarmingService`
to know "both sides of this pair are fully swept" (e.g. a per-country/
per-club "swept" marker `PlayerCareerPrefetchService` sets on success, or
inferring it from `ICategoryValueRepository`'s seeded-country/seeded-club
membership once `prefetch-player-careers` has run at least once
end-to-end) and, when true, write `ConfirmedLowMatchPair` directly from
the local count instead of issuing a live query at all. Scope carefully:
this is a genuinely new signal (today `ConfirmedLowMatchPair` only ever
gets written as a side effect of a live query that came back low, per
REQ-110's own "persisted confirmed-low signal" extension) — likely needs
its own ADR per `CLAUDE.md`'s "could reasonably have gone another way"
test, not a silent extension of ADR-0077.
*Deps:* S-159.
**Built as:** `CountryDefinition`/`ClubDefinition.PlayerPoolSweptAt`
(`DateTime?`), the per-row "swept" marker option this entry's own note
above named — see ADR-0078 for the full decision, including why the
alternative signals considered (a single shared timestamp, a new pair
table, an implicit global flag) were rejected. `PlayerCacheWarmingService
.WarmAsync` checks both sides before `IsConfirmedLowAsync`/
`IsPersistentTechnicalFailureAsync`/the live-query chain and calls
`RecordConfirmedLowAsync` directly when both are swept — a new
`PairsConfirmedLowFromSweep` counter makes it visible in the run summary.
Both invalidation sites ADR-0078 requires are wired:
`StaleClubAttributeCleaner` (REQ-111) and `purge-player-pool`
(REQ-112/S-038) now also clear `PlayerPoolSweptAt`. Never actually compiled
in the sandbox that built it (no `dotnet` SDK available there); the
migration and Designer/snapshot files were hand-written by mirroring
`20260817120000_AddRoundSequenceNumber`'s exact shape — first CI run on the
branch is the real verification.
**Quality-gate follow-up, non-blocking (2026-08-18):** `architecture-reviewer`/
`quality-architect` passed this cleanly but flagged three small,
explicitly-non-blocking items worth a future look, not their own story:
(1) `CliVerbDispatcher.HandlePurgePlayerPoolAsync`'s five bulk statements
(the pre-existing three plus this story's two new `PlayerPoolSweptAt`
resets) run as separate, non-transactional operations — a crash mid-purge
could leave `PlayerPoolSweptAt` stale relative to already-deleted
`PlayerAttribute` data; reordering the resets to run *before* the
`Players` delete (or wrapping the whole verb in a transaction) would make
a crash fail safe instead of stale-trusting, same incident class
ADR-0078 exists to prevent, but this pattern predates this story and
isn't new drift; (2) `UpdateCountrySweptAtAsync`/`UpdateClubSweptAtAsync`
silently no-op if the row is gone — fine today (nothing deletes
`CountryDefinition`/`ClubDefinition` rows), a `LogWarning` would help if
that ever changes; (3) `RecordConfirmedLowAsync`'s tracked load-then-save
runs on every `WarmAsync` run for an already-swept-and-low pair forever
(cheaper than a live query, but not as cheap as `IsConfirmedLowAsync`'s
`AsNoTracking` read) — negligible at Tier 0's ~15-club scale, worth
revisiting only if the reference-data pool grows substantially.

## Epic 19 — xG Path clue-content data-quality bugs (post-Epic-12 QA)

Origin: a 2026-08-18 user QA pass over freshly-generated xG Path rounds
(the first rounds built under Epic 12's tightened eligibility rules)
surfaced three independent clue-content defects via screenshots. None of
the three are duplicates of anything Epic 12 itself already tracked.

**S-161 · xG Path: add a `Player.Position != null` eligibility requirement, additive to ADR-0073's `BirthYear` floor — SHIPPED**
A puzzle for a French, 1980-born target rendered "Position: not available"
— `Player.BirthYear`/`Player.Nationality` were populated but `Position`
was null. `Player.Position` staying null forever for a subset of rows is
already-documented, deliberate REQ-1207 behavior (a data gap, not a code
bug) — but nothing currently stops a `Position == null` candidate from
being SELECTED as a target in the first place, unlike `BirthYear`, which
ADR-0073/S-137 already excludes on `null` (fail-closed). This story closes
that gap the same way, for the same reason: exclude `Position == null`
candidates from the eligible pool rather than let a preventable "not
available" surface on a puzzle screen at all.
*Accept:* `XGPathGameModuleTests.cs` gains a case for `Position == null`
(excluded) alongside the existing `BirthYear` boundary cases, using the
same fixture shape; new ADR (mirrors ADR-0073's shape almost exactly —
additive, `Player`-level, fail-closed on null); REQ-1201 status note
extending the eligibility rule, following the S-137/S-138 status-note
precedent (existing acceptance-criteria bullets are not rewritten, a new
dated note is appended, same as those two stories did).
*Deps:* none — same "additive `Player`-level check, alongside not inside
`IsEligible`" pattern ADR-0073 already established, applied to a second
field.

**S-162 · xG Path: collapse adjacent same-club `PlayerCareerStint` rows for clue display — SHIPPED**
A puzzle for a target matching Divock Origi's real career shape rendered
three consecutive "Lille" entries back to back — `PlayerClueSequenceBuilder`
renders every stint row it's given with no notion that two (or three)
ADJACENT rows (nothing else in between, chronologically) at the identical
`ClubName` might be one real "chapter" of a career split across multiple
Wikidata statements (a squad-list renewal, a sell-then-loan-back, etc.).
This reads as broken/duplicated data regardless of the administrative
reason behind the split. **This is NOT the same fix as
`DuplicateCareerStintCleaner`/ADR-0063** — that class proves two DB rows
are the same real-world stint and deletes one; ADR-0063 explicitly forbids
merging two rows with different, both-populated `AppearanceCount` values
(exactly the Lille shape here: "40 apps" / "33 apps" / a third unknown-apps
row), on the reasoning that they could be a genuine loan-and-return. A
DB-level merge is therefore not safe here. What IS safe: a **read-time,
display-only** collapse (no DB write, no deletion, reversible by
construction) of stint rows that are ADJACENT in chronological sequence
AND share the identical `ClubName` — nothing else could have happened in
between them regardless of why Wikidata recorded them as separate
statements, so collapsing them into one displayed entry (earliest
`StartYear`, latest `EndYear`, summed `AppearanceCount` when all inputs
are known, `null` if any input is unknown) cannot be "wrong" the way a DB
merge could be.
**The real complexity, and why this isn't a same-day fix:** this collapse
would run in `PathCareerStintFilter`, chained alongside
`ExcludeNationalTeams`/`ExcludeBTeams` — but `XGPathGameModule.IsEligible`'s
`MinDocumentedStintCount` (>= 3) floor exists SPECIFICALLY so
`PathClueSequenceBuilder.SplitIntoTurns` always has >= 3 rows to split
across its 3 fixed club-reveal turns (see that constant's own doc comment
and the "INVARIANT" comment on `GetEligiblePlayerIdsAsync`). A new collapse
step shrinks the row count the same way `ExcludeNationalTeams`/
`ExcludeBTeams` already do — so it MUST be applied, identically, at both
the eligibility call site and the clue-building call site, in the same
position in the filter chain, exactly like those two existing filters —
never only at one. Landing it at only the display call site would silently
reopen the exact "eligible with < 3 real stints, empty first club-reveal
turn" bug class S-138's quality-gate review already found and fixed once
(ADR-0074). This needs its own ADR (a new heuristic, same class as
ADR-0075's B-team pattern) plus test coverage in BOTH
`PathCareerStintFilterTests.cs` (the collapse function in isolation:
2 adjacent same-club rows merge, 3 adjacent same-club rows merge, a
same-club pair with something else in between does NOT merge, all-known
`AppearanceCount`s sum, and — CORRECTED, this earlier draft of this entry
contradicted itself here — an unknown `AppearanceCount` on ANY merged
segment makes the merged total `null`, NOT the known value alone.
Deliberately NOT `DuplicateCareerStintCleaner`'s null-tolerant
single-value-propagation rule (ADR-0063): that class is proving two rows
are the literal same real stint, where "unknown" plausibly means "the
other row already told us the true count." This collapse is different —
it's merging rows that may be genuinely separate real registrations for
one continuous, uninterrupted club chapter, where appearance counts are
additive; silently treating an unknown segment as contributing zero
(by only showing the known segment's count) would understate a real
total, so the honest choice is to show no count at all for the merged
entry, same as any other stint with an unrecorded `AppearanceCount`
already renders today — not to fabricate a partial sum) AND
`XGPathGameModuleTests.cs` (a candidate whose raw row count is >= 3 but
whose POST-COLLAPSE distinct-chapter count is < 3 must be excluded, not
just displayed differently) — i.e. real invariant-preserving work, not a
one-file display tweak. No `dotnet` SDK or database access in this
sandbox to verify against; do this in a session where compiling/running
the test suite is possible, not by hand-writing it blind the way this bug
report's own investigation found several past xG Path filters were forced
to (see ADR-0075's "For AI agents" section on unverified regexes as the
precedent for what NOT to repeat blind twice in the same file).
*Accept:* see test coverage above; new ADR; REQ-1203 status note.
*Deps:* none structurally, but should land in a session with real
`dotnet test` access given the invariant risk described above.

**S-163 · xG Path: infer and label loan spells on club-reveal clues — SHIPPED**
A puzzle matching David Beckham's real career rendered "Manchester United"
and "Preston North End" together in the same club-reveal turn — real-world,
Preston was a loan spell chronologically NESTED inside the Man Utd stint
(1994-95, inside 1992-2003), not a sequential "next club." Nothing in
`PlayerCareerStint`/`ClubDefinition` records loan-vs-permanent status or a
parent-club relationship at all (confirmed: no such field exists —
ADR-0042 is the data-model ADR and does not mention one). Two options were
considered: (a) a real schema addition sourced from Wikidata's P1642 "on
loan from" property — real Tier 1/2-shaped scope per `MVP-SCOPE.md`, out
of reach from this sandbox (no wikidata.org access) regardless; (b) a
heuristic inferred purely from date-range containment (a stint whose
`[StartYear, EndYear]` is fully contained within a DIFFERENT club's
concurrent range is PROBABLY a loan) — the same class of imprecise,
iteratively-refined heuristic `PathCareerStintFilter`'s
`NationalTeamPattern`/`BTeamPattern` already are, same false-positive/
negative risk profile, same "needs its own ADR" bar ADR-0075 sets.
**Decision (explicit product request, 2026-08-19): build (b), accepting
the inference-accuracy trade-off as a deliberate experiment ("test out")
rather than a load-bearing correctness claim** — this is presentation-only
(no eligibility/scoring impact) and reversible (a single boolean flowing
through, easy to strip back out if the false-positive rate turns out
unacceptable in practice).
**The exact interface contract** (both backend and frontend implement
against this, independently, in parallel):
- New method in `PathCareerStintFilter.cs`:
  `public static bool IsInferredLoan(PlayerCareerStint stint, IReadOnlyList<PlayerCareerStint> allStints)`
  — true when `allStints` contains a DIFFERENT-`ClubName` stint whose
  range fully contains `stint`'s: `stint.EndYear is not null &&
  (other.StartYear <= stint.StartYear && (other.EndYear is null ||
  other.EndYear >= stint.EndYear))`. Conservative on purpose: a stint with
  a null `EndYear` (still ongoing) is never itself flagged as contained —
  the `stint.EndYear is not null` guard must gate the WHOLE expression
  (an ongoing stint can't be "inside" anything yet, regardless of what
  else is going on), not just the second branch of the inner `||` (a
  guard placed there would short-circuit to `true` whenever `other` is
  also ongoing, without ever consulting `stint.EndYear`) — but CAN be the
  containing stint for an earlier-ended one.
- `PathClubClue` (`PathClueTurn.cs`) gains a third field:
  `bool IsLoan = false` (default keeps every existing positional
  `new PathClubClue(name, count)` call site — tests included — compiling
  unchanged).
- `PathClueSequenceBuilder.BuildSequence` passes `IsLoan:
  PathCareerStintFilter.IsInferredLoan(s, stintsChronological)` when
  building each turn's `PathClubClue`s (it already has the full
  `stintsChronological` list in scope).
- `PathClubClueResponse` (`PathEndpoints.cs`) and its `ToTurnResponse`
  mapping gain the same `bool IsLoan` field, propagated straight through.
- Frontend `PathClubClue` (`frontend/src/lib/types.ts`) gains `isLoan:
  boolean`; `PathTimeline.tsx`'s `ClubReveal` rendering (around its
  existing `club.appearanceCount != null &&` conditional span) renders a
  small "(loan)" text qualifier next to the club name when `club.isLoan`
  is true — reuse an existing muted/secondary text token from
  `design-document.md` §2, do not introduce a new color/weight for this.
**Accept:** `PathCareerStintFilterTests.cs` cases for `IsInferredLoan` —
fully contained (true), partial overlap only (false), no overlap (false),
identical range different club (edge case — document the chosen behavior,
don't leave it unspecified), an ongoing (`EndYear: null`) stint as the
candidate being tested (false, per the conservative rule above), an
ongoing stint as the containing stint for an earlier-ended one (true).
`PathClueSequenceBuilderTests.cs` updated/extended for `IsLoan` wiring.
A `PathTimelineTests` (Vitest) case renders a loan-flagged club clue and
asserts the qualifier appears. New ADR (mirrors ADR-0075's shape: Context/
Decision/Alternatives/Consequences/For-AI-agents, explicit "not verified
against live Wikidata or production data" disclosure, explicit "this is a
deliberate experiment, expected to need iteration" framing matching how
this session's own investigation found `NationalTeamPattern`/`BTeamPattern`
needed follow-up corrections). REQ-1203 status note (new dated addendum,
same style as the national-team/B-team ones already in that REQ).
*Deps:* none. Backend (`XGPathGameModule`/`PathCareerStintFilter`/
`PathEndpoints.cs`) and frontend (`types.ts`/`PathTimeline.tsx`) can be
built in parallel against the interface contract above without waiting on
each other.

## Epic 20 — Cross-game player experience

**S-164 · REQ-1210: round-completion animation with current points and a leaderboard link — SHIPPED, 2026-08-22**
A completion animation, generic across every game xG Arcade hosts (xG
Grid, xG Path today, and any future game — written against the shared
`Round`/cell model, ADR-0003, not either game's own internals), shown
once a player's own guessing activity locks the last cell available to
them in a round: shows a current-points value for that round (xG Grid's
existing "~N pts estimated" provisional wording, REQ-204/213; xG Path's
plain, non-provisional "N pts" wording, REQ-1206 — no new scoring path,
no new wording convention) and a link straight to that round's
leaderboard for that specific game, live-scoped (REQ-407) if the round
hasn't closed yet at the moment the link is activated or closed-scoped
(REQ-408) if it has. Frontend-only, no backend/`IGameModule` change: a
new game-agnostic `frontend/src/lib/roundCompletion.ts`
(`computeRoundCompletion`/`useCompletionTransition`, the latter firing
only on an in-session `false → true` transition — deliberately never on
first mount, so reloading or re-navigating into an already-complete round
does not replay it) and `frontend/src/components/RoundCompletionBanner.tsx`,
each consumed by `GridScreen.tsx`/`PathScreen.tsx` via a small
per-game `toCompletableItem` mapping function. The leaderboard link is
threaded as in-memory navigation state through `App.tsx`'s existing
hash-based screen-switch mechanism (new `leaderboardInitial` state +
`handleViewRoundLeaderboard`) into new optional `initial*` props on
`LeaderboardScreen`/`PastRoundsLeaderboard` — not a URL route; see
ADR-0083 for why this doesn't trigger ADR-0039's own "add react-router"
follow-up. *Accept:* `roundCompletion.test.ts` (completion/points-sum
logic and the transition hook in isolation), `RoundCompletionBanner.test.tsx`,
`GridScreen.test.tsx`/`PathScreen.test.tsx` (banner appears only once
every available cell/puzzle is locked, shows the correct
game-appropriate wording and points, "View leaderboard" navigates with
the correct target), `LeaderboardScreen.test.tsx`/`PastRoundsLeaderboard.test.tsx`
(seeded `initial*` props land the screen directly on the right
game/scope/round), `play-path.spec.ts` (E2E, updated for the new banner
in the completion flow). *Deps:* none — REQ-204/205/206, REQ-1206,
REQ-407/408 are all pre-existing. **Open product question, not yet
resolved:** whether the animation should replay on every subsequent
revisit of an already-complete round rather than only the first time —
recorded in `requirements-document.md` §7 pending a product decision;
`useCompletionTransition`'s in-session-only behavior is ADR-0083's
conservative default, not a resolution of that question.

## Epic 21 — Technical debt remediation, round 5 (`CODE_HEALTH_ASSESSMENT.md`/`CODEBASE_ANALYSIS.md` follow-up, 2026-08-22 sweep)

Source: `CODEBASE_ANALYSIS.md` and `CODE_HEALTH_ASSESSMENT.md` (both
2026-08-22 revision), the `code-health-auditor` agent's periodic sweep.
Same house rules as Epics 7/9/17: independent of the Tier 0 build
sequence, **every story here is a pure refactor/doc-sync — no behavior
change, no new REQ IDs**. Before writing this epic, every Epic 17 story
(S-154–S-158) was verified actually shipped against `git log`/current code
(all 5 confirmed merged — `IPathEligibilityService`/`PathEligibilityService`,
`SparqlQueryBuilders`/`SparqlResponseParsers`, the 4 `AdminScreen.tsx`
subcomponent test files, `AdminScreen.tsx`'s `useAuthedFetch` migration,
and `useSession.ts`), closing out Epic 17 entirely. One doc-drift found and
fixed directly in this sweep (not re-proposed as a story): S-154's own
backlog entry was missing its "Built as" note despite the work having
fully shipped (ADR-0082, CHANGELOG, architecture/requirements docs all
already synced) — backfilled above from those existing sources. This
sweep's one new finding, below, was identified by cross-referencing git
churn against every backend/frontend module, per this agent's own
complexity × churn mandate.

**S-165 · `PlayerCareerPrefetchService.cs`: extract the shared country/club sweep shape**
`PlayerCareerPrefetchService.cs` (408 lines) is the highest-churn file in
`XGArcade.DataSync` (8 commits since 2026-08-11 — S-127 added the club
sweep loop as a deliberate mirror of the pre-existing country loop per
ADR-0069, and every subsequent xG data-quality story, REQ-110/ADR-0077/
ADR-0078/S-159/S-160, touched both loops symmetrically, growing the
duplication each time rather than shrinking it). The two `foreach` loops in
`PrefetchAsync` (country loop, lines ~125-192; club loop, lines ~198-279)
are ~90 lines each and near-identical in shape: skip on null QID, fetch a
pool with a try/catch that logs-and-continues on `WikidataQueryException`,
mark the row "swept" via `UpdateCountrySweptAtAsync`/`UpdateClubSweptAtAsync`
(ADR-0078), skip an empty pool, look up already-known attribute-holder
player ids, chunk the pool into `FetchAndPersistBatchAsync` calls, and log
a running-totals line — the same shape this repo has already flagged and
fixed twice before at this size (`WikidataClient.cs`'s per-query-method
HTTP duplication, Epic 7; `GridGameModule.cs`'s multi-concern methods,
Epic 9). Extract a shared private helper (e.g.
`SweepPoolAsync(IReadOnlyList<CategoryValueDefinition> rows, Func<Guid,DateTime,CancellationToken,Task> markSweptAsync, Func<CategoryValueDefinition,CancellationToken,Task<IReadOnlyList<WikidataNameIndexEntry>>> fetchPoolAsync, string attributeType, Func<CategoryValueDefinition,string> attributeValueSelector, string logLabel, ...)` or an equivalent small delegate/record-based parameterization) that both loops call, preserving every existing behavioral nuance verbatim — in particular the club loop's deliberate `club.Name` (not `clubNameByClubQid`) sourcing for its attribute value (see the existing 2026-08-18 quality-gate-fix comment on that loop for why the two loops are NOT simply interchangeable on this one point) and each loop's own distinct log-message wording/failure-list variable. This is exactly the kind of "duplicated shape repeated per near-identical block" pattern this agent's own mandate calls out explicitly — not just large-file busywork.
*Accept:* `PlayerCareerPrefetchServiceTests.cs` (563 lines) passes
unchanged — pure structural refactor, no behavior change; both loops'
existing per-nuance test coverage (the `club.Name`-vs-`clubNameByClubQid`
distinction in particular) still passes without modification, confirming
the extraction preserved it; net line-count reduction reported in the PR
description. Flag for `architecture-reviewer`/`quality-architect`: whether
the shared helper takes a small parameter record/delegate bundle or is
instead expressed as two thin wrapper methods calling one shared private
core — this "could reasonably go another way" the same way S-155's
flat-vs-subfolder judgment call did; this story doesn't decide it. No
`dotnet` SDK in this sandbox — implement and verify in a session with real
`dotnet test` access, per this repo's standing constraint for
`XGArcade.DataSync` changes.
*Deps:* none.
**Built as (2026-08-23):** matches the plan, with the "flag for
architecture-reviewer/quality-architect" judgment call resolved as two thin
wrapper methods over one shared core, not a delegate/record bundle passed
directly to callers — `SweepCountriesAsync`/`SweepClubsAsync` each supply
their own fetch call, mark-swept write, and log wording to a shared private
generic `SweepAsync<TRow>`, which itself delegates the byte-identical
fetch-batch/dedup tail to `SweepPoolAsync`. The club sweep's `club.Name`
(never `clubNameByClubQid`) attribute-value sourcing carried over verbatim
via `SweepClubsAsync`'s own `getName` selector. `PlayerCareerPrefetchService.cs`
408 → 404 lines; `PlayerCareerPrefetchServiceTests.cs` byte-unchanged, full
backend suite (1,616 tests) green. `architecture-reviewer`/`quality-architect`
both passed with zero findings; per `architecture-reviewer` the
wrapper-vs-bundle choice is private-method-shape territory below ADR
granularity, so no ADR. Full detail: `docs/CHANGELOG.md`, 2026-08-23 entry
(S-165).

**Watch-only (no story, low churn or not yet a problem):**
- `backend/src/XGArcade.Games.XGPath/PathCareerStintFilter.cs` (544 lines,
  7 commits): grew from S-138 through S-163 as each new xG Path
  data-quality bug (national-team leakage, B-team leakage, adjacent-same-club
  duplication, inferred loans) added its own independently-testable static
  method. Inspected this sweep and found genuinely cohesive — one
  responsibility ("read-time filters/transforms over an already-fetched
  `PlayerCareerStint` list"), four short methods (the actual code is well
  under 100 lines; the rest is this codebase's established
  heavy-inline-rationale documentation style, the same convention
  `IWikidataClient.cs`/`NationalTeamPattern` already established and this
  sweep re-confirmed is deliberate, not noise). Each method already has its
  own dedicated, narrow test coverage in `PathCareerStintFilterTests.cs`.
  Not the same accretion failure mode as pre-split `XGPathGameModule.cs` —
  no single method is doing multiple things, and splitting the file further
  (one file per filter) would fragment a concern nobody actually changes
  independently, the same reasoning ADR-0082 gave for not splitting
  `PathEligibilityService` further. Re-check if it keeps growing at this
  rate — the next 1-2 xG Path data-quality bugs are the point to
  reconsider, not now.
- `backend/src/XGArcade.DataSync/Wikidata/WikidataClient.cs` (782 lines,
  post-S-155 split): defect-risk driver resolved (Epic 9) and breadth
  driver resolved (S-155) — now holds only its constructor/fields, the two
  `Run*` drivers, and ~15 thin `IWikidataClient` wrapper methods delegating
  to `SparqlQueryBuilders`/`SparqlResponseParsers`. No action.
- `backend/tests/XGArcade.DataSync.Tests/Wikidata/WikidataClientTests.cs`
  (3,973 lines): unchanged this sweep, same judgment as Epic 17 — growing
  for legitimate regression-proof reasons, navigability-only concern.
- `backend/src/XGArcade.Api/CompositionRoot/CliVerbDispatcher.cs` (769
  lines): unchanged since the 2026-08-18 revision (confirmed via
  `git log --since`) — the verb-registry pattern is still holding with zero
  new duplication. No action.
- `nanoid@<3.3.18` (`npm audit`, dev-dependency-only, transitive via
  `vite`): unchanged since 2026-08-18 — still a real advisory, still
  dev-tooling-only (never bundled into the shipped frontend), still
  Dependabot's routine-drift lane per `CLAUDE.md`, not fixed directly here.
- `docs/backlog.md`/`docs/CHANGELOG.md`: unchanged reasoning from Epic 17
  — both are append-only-by-design working logs, not the accretion failure
  mode this agent watches for. No action.

---

## Epic 22 — Technical debt remediation, round 6 (`CODE_HEALTH_ASSESSMENT.md`/`CODEBASE_ANALYSIS.md` follow-up, 2026-08-23 sweep)

Source: `CODE_HEALTH_ASSESSMENT.md`/`CODEBASE_ANALYSIS.md` (2026-08-23
revision), the `code-health-auditor` agent's periodic sweep. Same house
rules as Epics 7/9/17/21: independent of the Tier 0 build sequence, **every
story here is a pure refactor/doc-sync — no behavior change, no new REQ
IDs**. This pass was explicitly scoped wider than Epic 21's single finding
— every module still below ~9.0 in the 2026-08-22 `CODE_HEALTH_ASSESSMENT.md`
was re-read, and backend/frontend/infra were each searched for the same
"duplicated near-identical block"/"weak-coverage hotspot"/"boundary smell"
patterns this lineage has already caught and fixed multiple times, not just
re-verifying the single 2026-08-22 finding (S-165, still open, unchanged,
not re-described here). Before writing this epic, S-165 and every Epic 21
watch-only item were re-confirmed still accurate against current
`git log`/code — no drift found. Four new findings surfaced, below.
`npm run test` (647/647, 44 files), `tsc -b`, and `oxlint` all ran live and
clean this pass (existing `node_modules/`, no reinstall needed);
`npm audit` unchanged (`nanoid@<3.3.18`, dev-only, still Dependabot's lane).
No `dotnet` SDK in this sandbox, confirmed again — every backend-touching
story below needs a session with real `dotnet test` access.

**S-166 · `PlayerCacheWarmingService.cs`: extract the shared Country×Club/Club×Club sweep shape**
`PlayerCacheWarmingService.WarmAsync` (`backend/src/XGArcade.Games.XGGrid/`,
388 lines, 4 commits) is almost entirely two nested loops that duplicate the
exact same 5-branch decision tree end to end: the Country×Club loop (lines
~169-269) and the Club×Club loop (lines ~271-349) each (1) read the cached
count via `CountPlayersWithBothAttributesAsync`, (2) short-circuit
already-valid, (3) short-circuit "confirmed low from a fully-swept pool"
(ADR-0078/S-160), (4) short-circuit a previously-confirmed-low pair, (5)
short-circuit a persistent (2+ run) technical failure, and only then (6) run
a live Wikidata lookup, record/clear the technical-failure marker, and
persist a fresh confirmed-low marker if still below threshold — then log a
per-pair debug line and call the shared `LogProgressCheckpoint`. The only
real differences between the two loops are which two `AttributeType`/name
pairs get passed to each repository call and which `IWikidataLookupService`
method is invoked (`LookupAndPersistAsync` vs.
`LookupAndPersistClubClubAsync`) — this is the same "duplicated shape
repeated per near-identical block" pattern this report has already caught
and fixed at this size four times (`WikidataClient.cs` HTTP handling, Epic
7; `GridGameModule.cs` multi-concern methods, Epic 9; `PlayerCareerPrefetchService.cs`'s
own country/club sweep loops, Epic 21 S-165 — this is the third occurrence
of exactly that same country/club-pair shape in this codebase, not a
coincidence). Extract a shared private helper (e.g.
`SweepPairsAsync<TLeft, TRight>(IReadOnlyList<(TLeft, TRight)> pairs, string attributeTypeA, Func<TLeft,string> nameA, string attributeTypeB, Func<TRight,string> nameB, Func<TLeft,TRight,CancellationToken,Task<IReadOnlyList<Player>>> lookupAsync, ...)`
or an equivalent small delegate/record-based parameterization) that both
loops call, preserving every counter (`pairsQueriedLive`,
`pairsAlreadyValid`, `pairsSkippedConfirmedLow`,
`pairsSkippedPersistentFailure`, `pairsConfirmedLowFromSweep`,
`pairsWithTechnicalFailure`, `failingPairs`) and every log line's wording
verbatim — this method's own return type (`CacheWarmingResult`) and the
`LogProgressCheckpoint`/`ProgressLogInterval` cadence are unaffected either
way. Flag for `architecture-reviewer`/`quality-architect`: exactly how to
parameterize the "which two repository calls" difference (generic
delegates vs. a small strategy record) is a "could reasonably go another
way" call, same as S-165's own flagged judgment call — this story doesn't
decide it, and whichever shape is picked should probably match S-165's
(implement together or reference each other, since both stories touch the
same "sweep-a-pool-of-pairs" shape one directory apart).
*Accept:* `PlayerCacheWarmingServiceTests.cs` (775 lines) passes unchanged
— pure structural refactor, no behavior change; net line-count reduction
reported in the PR description. No `dotnet` SDK in this sandbox — implement
and verify in a session with real `dotnet test` access.
*Deps:* none (may be sequenced alongside S-165 since both touch the same
duplication pattern, but neither blocks the other).
**Built as (2026-08-23):** matches the plan, with the "flag for
architecture-reviewer/quality-architect" judgment call resolved the same
way S-165 resolved its own: two thin wrapper methods
(`SweepCountryClubPairsAsync`/`SweepClubClubPairsAsync`) over one shared
private generic `SweepPairsAsync<TLeft, TRight>`, each supplying its own
attribute-type/name selectors, `IWikidataLookupService` method, and log
wording via delegates — not a delegate/record bundle passed directly to
callers. Checked S-165's landed code first, per this story's own flag;
`PlayerCareerPrefetchService.cs` turned out to live in a different
project (`XGArcade.DataSync`, not "one directory over" as this story's
text put it — a `quality-architect` correction, no effect on the fix).
`PlayerCacheWarmingService.cs` 388 → 367 lines; `PlayerCacheWarmingServiceTests.cs`
unchanged, full backend suite (1,616 tests) green. `architecture-reviewer`/
`quality-architect` both passed with zero blocking findings; two
non-blocking nits applied (a stale comment reference, `new
SweepPairsOutcome()` over a bare `default`). No ADR — same private-method-
shape reasoning as S-165. Full detail: `docs/CHANGELOG.md`, 2026-08-23
entry (S-166).

**S-167 · `CliVerbDispatcher.cs`: extract the shared Wikidata-client bootstrap**
`CliVerbDispatcher.cs` (769 lines) is the single highest-churn file in the
entire repository (13 commits) and was re-checked this pass at a finer
grain than the 2026-08-18/2026-08-22 revisions used — those confirmed the
*verb-registry* shape (the `Verbs` dictionary + one handler per verb,
S-112) is holding with no new duplication, which is still true and not
being re-litigated here. This is a narrower, different finding one level
down: five of the fourteen handlers
(`HandleWarmPlayerCacheAsync`, `HandleImportPlayerNameIndexAsync`,
`HandleBackfillPlayerPhotosAsync`,
`HandleBackfillPlayerPositionBirthYearAsync`,
`HandlePrefetchPlayerCareersAsync`) each repeat the same three-statement
Wikidata-client bootstrap inline: construct a bare `HttpClient`, call
`WikidataHttpClientConfiguration.Configure` on it, then `new WikidataClient(...)`
passing a `logger:` from the handler's own `BuildLoggerFactory()` (and, for
two of the five, an explicit `queryTimeout: TimeSpan.FromSeconds(60)` with
its own multi-paragraph inline justification, which must be preserved
per-call-site, not centralized away). S-114 already extracted
`BuildDbContext()`/`BuildLoggerFactory()` as shared helpers for the
boilerplate one level higher — this is the same kind of extraction for the
one boilerplate block S-114 didn't reach. Extract a private
`BuildWikidataClient(ILoggerFactory loggerFactory, TimeSpan? queryTimeout = null)`
helper (mirroring `BuildDbContext`/`BuildLoggerFactory`'s own existing
shape and doc-comment style) that the five handlers call instead of
constructing the `HttpClient`/`WikidataClient` pair by hand each time;
`HandleAuditClubGapsAsync`/`HandleVerifyWikidataPlayerDataAsync` and the
rest, which don't construct a `WikidataClient` at all, are untouched.
*Accept:* no dedicated `CliVerbDispatcherTests.cs` exists (S-113 recorded
this file as deliberately integration-tested, not a coverage gap to
re-open) — accept criterion is that every other backend test suite passes
unchanged and a hand-traced read of each of the five handlers confirms the
extracted helper reproduces each call site's exact prior arguments
(including the two `queryTimeout: 60s` overrides). No `dotnet` SDK in this
sandbox — implement and verify in a session with real `dotnet test`/manual
CLI-verb smoke-test access, per this file's own operational nature (these
verbs back real GitHub Actions workflows — see each handler's own doc
comment for which).
*Deps:* none.
**Built as:** matches the plan exactly — `BuildWikidataClient(ILoggerFactory
loggerFactory, TimeSpan? queryTimeout = null)`, same shape/placement as
`BuildDbContext()`/`BuildLoggerFactory()`, called from all 5 named handlers
with the two `queryTimeout: 60s` overrides and their justification comments
preserved verbatim. A real `dotnet` SDK was installed in-session (10.0.111
via apt, matching this repo's `net10.0` target) rather than deferred —
full backend suite ran green (1616/1616, 6 projects), and the hand-traced
argument check was done twice independently (`backend-implementer` during
implementation, `architecture-reviewer`/`quality-architect` during the
quality gate). One small addition beyond the plan: a doc-comment sentence
on `BuildWikidataClient` explaining why its `HttpClient` is deliberately
left undisposed (each caller is a one-shot CLI process) — a
`quality-architect` review finding, non-blocking, added directly.

**S-168 · `frontend/src/lib/*.ts`: extract a shared authenticated-fetch helper**
Every domain file under `frontend/src/lib/` that talks to the backend
(`admin.ts` 18 call sites, `auth.ts` 9, `announcements.ts` 5,
`leaderboard.ts` 5, `rounds.ts` 4, `leagues.ts` 3, `incidents.ts` 2,
`path.ts` 1 — 47 total across 8 files) hand-rolls the same shape at every
single call site: build a headers object (`Authorization: Bearer
${accessToken}`, plus `'Content-Type': 'application/json'` for a body-
carrying request), call `fetch(...)`, check `!response.ok` and call the
already-shared `throwApiError(response)` (`lib/apiClient.ts`) if so, then
`(await response.json()) as SomeResponseType`. `apiClient.ts`'s own header
comment already frames itself as "the shared fetch foundation every domain
file imports from" — but today it only centralizes the *error*-handling
half (`throwApiError`/`ApiError`/`describeError`); the *request*-building
half (headers, method, body-serialization, the `fetch`+`ok`-check+`json()`
sequence itself) is still repeated by hand at all 47 call sites. This is
the same "duplicated HTTP-handling shape repeated per method" pattern this
lineage caught in `WikidataClient.cs` (Epic 7) and is now catching for the
third and fourth time this pass (S-166/S-167 above) on the backend side —
this is its frontend-`lib/`-layer equivalent, not yet caught because no
single file in `lib/` was ever the single largest outlier (each file is a
reasonable size on its own; the duplication is only visible aggregated
across all 8). Deliberately NOT the same fix as `useAuthedFetch.ts`
(S-107/Epic 17) — that hook is React-component-scoped (owns loading/error
*state*) and was explicitly evaluated and rejected for `GridScreen.tsx`/
`PathScreen.tsx` for exactly that reason (see Epic 17's own note); these 47
call sites are plain, non-hook async functions callable from anywhere,
including outside components, so the right extraction is a small
`apiRequest<T>(accessToken, path, init?)`-shaped function added to
`apiClient.ts` itself (or a sibling), not a hook. Scope this story to
`apiClient.ts` plus the 8 domain files above; do not fold in
`useAuthedFetch.ts` or its own call sites.
*Accept:* `npm run test` (647/647 across 44 files), `tsc -b`, and `oxlint`
all pass unchanged — every existing test that exercises these functions
does so via a mocked `fetch` at each component's boundary (there are no
dedicated `admin.test.ts`/`auth.test.ts`/etc. files today, confirmed this
pass), so this is verifiable in a normal frontend session, unlike
S-166/S-167. Flag for `ui-implementer`/`quality-architect`: whether the
shared helper takes a discriminated `'GET'|'POST'|'PUT'|'DELETE'`-plus-body
shape or stays closer to a thin `fetch` wrapper taking a raw `RequestInit`
is a "could reasonably go another way" call this story doesn't decide —
also confirm each of the 47 call sites' specific status-code special-casing
(several treat a bare `404` as data, not an error — e.g.
`fetchActiveAdminRound`/`deleteUserByEmail` — and must keep doing so
through whatever shared helper is chosen, never silently routed through
`throwApiError`).
*Deps:* none.
**Built as (2026-08-23):** matches the plan, with the "flag for
ui-implementer/quality-architect" judgment call resolved as a thin
`fetch` wrapper taking a raw `RequestInit` — `apiRequest<T>(accessToken:
string | null, path: string, init?: RequestInit): Promise<T>` — not a
discriminated `'GET'|'POST'|'PUT'|'DELETE'`-plus-body shape. All 47 call
sites' status-code special-casing, including the 404-as-data idioms in
`fetchActiveAdminRound`/`deleteUserByEmail`/`fetchCurrentRound`/
`fetchAdminAnnouncementBanner`/`fetchCurrentPath`, is preserved verbatim
via a catch-and-branch-on-`error.status` wrapper around `apiRequest`
rather than a status-code allowlist inside the helper itself.
`useAuthedFetch.ts` and `rounds.ts`'s `warmUpAutocomplete` were left on
their existing abstractions per the story's own scoping. One fix-now
issue caught mid-pass by `quality-architect`: an earlier draft's blanket
try/catch around `response.json()` would have swallowed real parse
failures on all ~40 typed call sites, not just the 4 genuinely-204 ones —
corrected to an explicit `response.status === 204` check before parsing.
`frontend/src/lib/apiClient.ts` is now 102 lines; `npm run test -- --run`
647/647 across 44 files, `npx tsc -b` clean, `npm run lint` (oxlint)
clean; no test files added or changed, matching the story's own
acceptance criteria. `architecture-reviewer` found no module-boundary
violation and no ADR warranted (equivalent in kind to S-111's original
`apiClient.ts` split, which also had no ADR). Full detail:
`docs/CHANGELOG.md`, 2026-08-23 entry (S-168).

**S-169 · `GridScreen.tsx`/`PathScreen.tsx`: extract the shared round-fetch/load-state hook**
`GridScreen.tsx` and `PathScreen.tsx` (529/357 lines, 5 commits each) both
independently define: (1) an identically-shaped `LoadState` union
(`'loading' | 'empty' | 'error' | 'ready'`, with the same
`roundEndTime`-computed-once-at-fetch-time field on the `'ready'` case —
both files' own comments cross-reference each other's identical
`lib/roundTime.ts` convention), (2) a mount `useEffect` that fetches the
current round/path, guards on a `cancelled` flag, treats a 401 `ApiError`
as `onAuthError()`, and otherwise sets the `'error'` state from
`describeError` — byte-for-byte the same control flow, differing only in
which `fetchCurrentX` function is called, (3) a second, independent
fire-and-forget `warmUpAutocomplete(accessToken)` mount effect, identical
in both files, and (4) `handleViewCompletedRoundLeaderboard`, which
`PathScreen.tsx`'s own comment states outright "mirrors GridScreen.tsx's
`handleViewCompletedRoundLeaderboard` exactly." This is the frontend
equivalent of the same duplicated-per-near-identical-case pattern S-166/
S-167 catch on the backend this pass — previously not flagged because
Epic 17's own frontend pass was scoped to `AdminScreen.tsx`/`App.tsx`
specifically, and `GridScreen.tsx`/`PathScreen.tsx` were only evaluated
(and correctly rejected) for `useAuthedFetch.ts`, a different, narrower
question than whether the two screens duplicate each other. Extract a
shared hook (e.g. `useRoundFetch<TRound extends { endTime: string }>(accessToken, fetchFn, onAuthError)`
returning `{ state, refetch }` with the same `LoadState` shape, generic
over the round/path response type) covering points (1)-(2) at minimum;
evaluate during implementation whether (3) and (4) are worth folding in too
or are better left as each screen's own thin wrapper around the shared
piece — this "how much to fold into the shared hook vs. leave
screen-specific" question is exactly a "could reasonably go another way"
call, flagged for `ui-implementer`/`architecture-reviewer` rather than
decided here. `GridScreen.tsx`'s own `applyScoredGuess`/
`handleSubmitGuess`/`handleResolveDisambiguation` (the mutate-fetched-state
logic that ruled out `useAuthedFetch.ts`) and `PathScreen.tsx`'s
`puzzleIndex`/`refetchWarning` state are untouched by this story — this is
scoped only to the fetch-and-load-state machinery, not the two screens'
genuinely different guess-submission flows.
*Accept:* `npm run test` (647/647 across 44 files, including
`GridScreen.test.tsx`/`PathScreen.test.tsx` unchanged), `tsc -b`, and
`oxlint` all pass unchanged — pure structural refactor, no behavior change.
*Deps:* none.
**Built as (2026-08-23):** matches the plan, with the "how much to fold
into the shared hook vs. leave screen-specific" judgment call resolved as:
folded in `checkRoundStillLive(roundId)` (the shared re-fetch-and-compare
core of `handleViewCompletedRoundLeaderboard`'s live-vs-past check,
REQ-1210/ADR-0083) since it reuses the exact same fetch shape as the mount
effect — but kept it read-only, deliberately never calling `setState`,
matching the pre-extraction code exactly (`GridScreen.test.tsx`'s "past"-
scope 404 re-check would otherwise blank out the just-completed round
mid-click). Left `warmUpAutocomplete` out of the hook entirely — it's a
separate, unrelated `useAutocompleteWarmup(accessToken)` export in the
same new file rather than folded into `useRoundFetch` itself, since it
never touches `TRound`/`state`. Each screen keeps its own thin
`handleViewCompletedRoundLeaderboard` wrapper owning
`checkingLeaderboardTarget` and its own `gameKey`; `GridScreen.tsx`'s
`applyScoredGuess`/`handleSubmitGuess`/`handleResolveDisambiguation` and
`PathScreen.tsx`'s `puzzleIndex`/`refetchWarning` were untouched beyond
reading `state`/`setState` from the hook. One incidental fix needed to
land the extraction: two new `react-hooks/exhaustive-deps` warnings
appeared once `setState` came from a custom hook instead of a literal
`useState` call (oxlint no longer recognized it as stable) — fixed by
adding `setState` to the affected `useCallback` dependency arrays.
New `frontend/src/lib/useRoundFetch.ts` is 138 lines; `npm run test`
647/647 across 44 files (including `GridScreen.test.tsx`/
`PathScreen.test.tsx` unchanged), `npx tsc -b` clean, `npm run lint`
(oxlint) clean. No dedicated `useRoundFetch.test.ts` added, matching
`useAuthedFetch.ts`'s own precedent of no dedicated lib-hook test file.
Full detail: `docs/CHANGELOG.md`, 2026-08-23 entry (S-169).

**Watch-only (no story, low churn or not yet a problem):**
- `infra/scripts/sync-prod-to-dev.sh`/`promote-dev-to-prod.sh` (83/85
  lines): genuinely near-duplicate outside their already-shared
  `lib/game-data-tables.sh` allowlist (ADR-0006/0009) — the dry-run/
  confirmation-prompt/`pg_dump`+`pg_restore`/FK-safety-restore sequence is
  the same shape in both, differing only in which `*_DATABASE_URL` is
  source vs. target, the echoed direction wording, and the confirmation
  phrase (`sync` vs. `promote to prod`). Deliberately NOT written up as a
  story this pass: both scripts' own header comments frame the asymmetry
  (a stronger, more explicit confirmation phrase for the prod-writing
  direction) as a deliberate safety property, and unifying them behind a
  shared `--direction` flag would trade that per-script hard-coded safety
  for a single shared code path where a copy-paste/flag-typo mistake could
  point the wrong way — genuinely a design call, not a pure mechanical
  win, and low churn (1 commit each in this repository's history). Only
  worth a story if `architecture-reviewer` judges the safety trade-off
  acceptable; not decided here.
- A handful of small, low-churn frontend presentational components have no
  dedicated test file (`components/CategoryLabel.tsx`, `components/Logo.tsx`,
  `nav/GuestLogoutConfirm.tsx`, `path/PathScoringExplainer.tsx`,
  `leaderboard/LeaderboardRowsList.tsx`) — each is exercised indirectly via
  its parent screen's own test file (same convention already established
  for `PathCareerStintFilter.cs`'s siblings and confirmed fine there).
  Checked this pass and found genuinely low-risk: all five are small
  (<200 lines), presentational, and low-churn. Not a story.
- `backend/src/XGArcade.DataSync/Wikidata/WikidataLookupService.cs` (406
  lines): re-inspected this pass given its role as `PlayerCacheWarmingService`'s
  own dependency — already properly deduplicated via its own shared
  `PersistMatchesAsync`/`QueueAttribute`/`PersistCareerStintsAsync` helpers
  (no per-call-site repetition the way S-166's file has). No action.
- `backend/src/XGArcade.Api/CompositionRoot/ServiceRegistration.cs` (308
  lines, 5 commits): re-inspected this pass — a growing but cohesive DI
  registration list, one line per service/option, zero duplication. No
  action.
- `backend/src/XGArcade.Api/Path/PathEndpoints.cs` (324 lines, 6 commits):
  re-inspected this pass — one endpoint, heavily and specifically
  documented (mirrors `RoundEndpoints.cs`'s established shape per
  ADR-0016/ADR-0048), no duplication. No action.
- Every Epic 21 watch-only item (large test files, `WikidataClient.cs`
  post-S-155, `nanoid@<3.3.18`, `docs/backlog.md`/`docs/CHANGELOG.md`
  themselves) re-confirmed unchanged this pass — not repeated verbatim
  here, see Epic 21 above.

---

## Epic 23 — Technical debt remediation, round 7 (`CODE_HEALTH_ASSESSMENT.md`/`CODEBASE_ANALYSIS.md` follow-up, 2026-08-23 sweep)

Source: a same-day follow-up to the 2026-08-23 sweep that filed Epic 22
(S-166–S-169). Same house rules as Epics 7/9/17/21/22: independent of the
Tier 0 build sequence, **every story here is a pure refactor/doc-sync — no
behavior change, no new REQ IDs**. Before writing this epic, every Epic 22
story (S-166–S-169) was re-verified against current `git log`/code: all
four confirmed merged (`c037af8`/`ed0acf2` S-169, `359bc93`/`ae10cf3` S-168,
`f40316c`/`72aae59` S-166, `87d44c2`/`435809c` S-167), and `PlayerCacheWarmingService.cs`
(388→367 lines), `PlayerCareerPrefetchService.cs` (408→404 lines),
`frontend/src/lib/apiClient.ts` (new, 102 lines)/`useRoundFetch.ts` (new,
138 lines) all match their respective stories' plans on disk. Two findings
from that verification pass, below (S-170/S-171); every other candidate
this pass investigated is either already covered by an existing story or
explicitly declined — see "Investigated and declined" below.

**S-170 · Remove two unused `ILogger<T>` constructor parameters in `XGArcade.Games.XGGrid`**
`GridGameModule.cs` (line 26, `ILogger<GridGameModule> logger`) and
`GridLiveLookupDispatcher.cs` (line 16, `ILogger<GridLiveLookupDispatcher> logger`)
each carry a constructor-injected `logger` parameter that is never read
anywhere in either file (confirmed via full-file grep: the identifier
`logger` appears exactly once in each file — the parameter declaration
itself — and the compiler already flags both with `CS9113: Parameter
'logger' is unread`). Both are leftovers from S-119's split of the
original `GridGameModule` into this adapter plus
`IGridGenerationService`/`IGridNameMatcher`/`IGridLiveLookupDispatcher` —
whatever logging either class originally did evidently moved to one of
the split-out classes, and the parameter was never removed from the two
that kept it. Delete both unused parameters (and the now-unused
`Microsoft.Extensions.Logging` `using` in each file, if nothing else in
that file still needs it) and update each constructor's callers/DI
registration accordingly.
*Accept:* both `CS9113` warnings are gone from a clean build; full backend
suite (`GridGameModuleTests.cs`/`GridLiveLookupDispatcherTests.cs`-family)
passes unchanged — pure structural removal, no behavior change, since
neither parameter was ever read. No `dotnet` SDK in this sandbox —
implement and verify in a session with real `dotnet build`/`dotnet test`
access.
*Deps:* none.
**Built as:** matches the plan exactly. Removed the unused `ILogger<GridGameModule> logger`
and `ILogger<GridLiveLookupDispatcher> logger` primary-constructor
parameters (and the now-unused `Microsoft.Extensions.Logging` `using` in
both files — neither class references `Microsoft.Extensions.Logging`
anywhere else). Neither call site needed a DI registration change: both
are registered via `builder.Services.AddScoped<...>()` in
`ServiceRegistration.cs`, which resolves constructor arguments
automatically. Three test files construct these classes directly and
needed the trailing `NullLogger<...>.Instance` argument dropped:
`GridGameModuleTests.cs` (two call sites), `GridGenerationServiceTests.cs`,
and `GridLiveLookupDispatcherTests.cs` — the last of these also lost its
now-unused `using Microsoft.Extensions.Logging.Abstractions;`, since
`GridLiveLookupDispatcher` was the only class it built with a logger
in that file; `GridGameModuleTests.cs`/`GridGenerationServiceTests.cs` kept
theirs, since both still build `GridGenerationService`/`GridNameMatcher`
with a `NullLogger` there. This sandbox still has no `dotnet` SDK and no
network path to install one (the egress proxy denies
`builds.dotnet.microsoft.com`), so the `CS9113`-gone/full-suite-green
acceptance criteria above are unverified by this session — confirmed
instead by a full-file grep of both changed source files (the `logger`
identifier no longer appears at all) and by re-reading every call site
this change touched.

**S-171 · Backfill missing "Built as" notes for S-168/S-169 in `docs/backlog.md`**
S-168 (`frontend/src/lib/*.ts`'s shared `apiRequest<T>` helper) and S-169
(`GridScreen.tsx`/`PathScreen.tsx`'s shared `useRoundFetch` hook) are both
confirmed shipped — merged (`359bc93`/`ae10cf3` and `c037af8`/`ed0acf2`
respectively), matching their own story text on disk (`apiClient.ts`
102 lines, `useRoundFetch.ts` 138 lines, both files present at their
stated paths), and both already have full "what shipped" detail recorded
in `docs/CHANGELOG.md`'s 2026-08-23 entries — but unlike S-165/S-166/S-167
in this same epic lineage (all three of which gained a "Built as" note the
same pass they shipped), neither S-168 nor S-169's own `docs/backlog.md`
entry was ever updated with one. This is the same doc-drift pattern this
lineage has caught and fixed directly on sight before (S-154 in Epic 21,
S-165's own predecessor state) — here it's large/structured enough
(two entries, cross-referencing specific commit shas and line-count deltas)
to be worth a tracked story rather than an inline fix. Add a "**Built
as:**" paragraph to each of S-168 and S-169, in this epic-lineage's
established style (see S-165/S-166/S-167 immediately above them, or
S-154 in Epic 21, for the shape: what matched the plan, what judgment
call was resolved and how, final line counts, test-suite result, and a
pointer to the `docs/CHANGELOG.md` entry for full detail) — sourced from
the existing 2026-08-23 `docs/CHANGELOG.md` entries and current code, not
re-investigated from scratch.
*Accept:* doc-only change — no tests to run; `docs/backlog.md`'s two new
notes are checked against current code (file paths, line counts, hook/
helper signatures) and against `docs/CHANGELOG.md`'s existing entries for
consistency before being written.
*Deps:* none.

**Investigated and declined this pass (not written up as stories):**
- `backend/src/XGArcade.Api/CompositionRoot/CliVerbDispatcher.cs`'s
  `TryHandleAsync`/`Verbs` dispatch mechanism (still 773 lines, now 12
  commits by `git log --follow`, still the single highest-churn file in
  the repo): re-read specifically to check whether the dispatch logic
  itself — not the verb-registry *shape*, already re-confirmed healthy in
  Epic 21/22 — has direct unit coverage or only indirect coverage via full
  CLI integration tests. It has only indirect coverage, but that is a
  **deliberate, already-documented decision**, not a gap: `TryHandleAsync`
  is two lines (`Verbs.TryGetValue` plus an `await`), and
  `docs/coding-guidelines.md`'s "Composition-root testing (S-113)" section
  states explicitly, by name, that `CliVerbDispatcher.cs`'s verb-dispatch
  table "has no comparable logic today — don't add unit tests for them
  speculatively," with the criterion for revisiting being real new
  conditional logic in the dispatch path itself (the way `AuthSetup.cs`'s
  `IsLocalE2EAuth` earned its own test file). No such logic has been added
  since that decision — the 12 commits are all new verb *handlers* being
  registered in the dictionary (S-114's shared bootstrap helpers, S-167's
  `BuildWikidataClient` extraction), not changes to the two-line dispatch
  itself. Writing `CliVerbDispatcherTests.cs` now would be exactly the
  speculative unit test the guideline already warns against. Not a story.
- `backend/src/XGArcade.Api/Auth/AuthController.cs` (773 lines): re-checked
  git churn directly (`git log --oneline --follow` on this exact path) —
  still 1 commit, matching every prior sweep's finding back to 2026-08-18.
  High line count with churn this low is exactly this report's own
  "watch, don't act" signal (low complexity × low churn is not a hotspot),
  and CODEBASE_ANALYSIS.md's #9 already tracks it as such. Not a story —
  confirming a prior finding still holds is not a new finding.
- `backend/tests/XGArcade.DataSync.Tests/Wikidata/WikidataClientTests.cs`
  (3,973 lines, still the largest file in the repo including tests):
  re-checked git churn — still 4 commits, unchanged since Epic 21/22.
  Same "growing for legitimate regression-proof reasons, navigability-only
  concern" judgment as every prior sweep. Not a story.
- `infra/scripts/sync-prod-to-dev.sh`/`promote-dev-to-prod.sh`: not
  re-investigated beyond confirming Epic 22's own watch-only entry is
  unchanged — this has been explicitly and repeatedly judged NOT a
  finding (the confirmation-phrase asymmetry is a deliberate safety
  property per each script's own header comment), and per this sweep's own
  standing instruction, is left alone rather than re-litigated every pass.

**Watch-only (no story, low churn or not yet a problem):**
- All of Epic 22's watch-only items (`PathCareerStintFilter.cs`,
  `WikidataClient.cs` post-S-155, `WikidataClientTests.cs`,
  `CliVerbDispatcher.cs`'s verb-registry shape, `nanoid@<3.3.18`,
  `docs/backlog.md`/`docs/CHANGELOG.md` themselves) re-confirmed unchanged
  this pass — not repeated verbatim here, see Epic 22 above. `infra/`'s
  `sync-prod-to-dev.sh`/`promote-dev-to-prod.sh` and `docs/`'s own
  accretion check (from `CODE_HEALTH_ASSESSMENT.md`'s 2026-08-23 revision)
  likewise re-confirmed clean — both currently sit at 8.0/10 with nothing
  above watch-only pulling them lower, not a backlog gap.
- `CODE_HEALTH_ASSESSMENT.md`'s own most recent revision predates this
  epic's S-165/S-166/S-167/S-168/S-169 (all five merged after that
  revision was written, confirmed via `git log`) — its score table and
  per-module notes for `XGArcade.DataSync`/`XGArcade.Games.XGGrid`/
  `frontend/` are now stale relative to current code. Not fixed in this
  pass (out of this pass's scope — see this session's own task framing);
  flagged here so the next full sweep picks it up rather than re-derives
  it as a "new" finding.

---

## Epic 24 — Technical debt remediation, round 8 (deep two-part sweep: module-deepening past the duplicated-block/god-file/churn lens, plus a first dead-code hunt)

Source: a deliberately deeper, two-part `code-health-auditor` investigation
(2026-08-23, same day as Epic 22/23 but a separate, wider-scoped pass),
explicitly going past the duplicated-shape/god-file/churn heuristics
already applied five times over (Epics 7/9/17/21/22) and past ADR-0084's
new per-diff subset of the same heuristics. Part 1 looked for genuinely
new dimensions (test depth, error-handling quality, naming, doc accuracy,
infra fragility) in the four modules `CODE_HEALTH_ASSESSMENT.md`'s
2026-08-23 revision tied at 8.0/10 (`XGArcade.DataSync`,
`XGArcade.Games.XGGrid`, `infra/`, `docs/` — note that revision itself
predates Epic 22/23's five merged stories and is now stale, a known,
already-flagged gap, see Epic 23's own watch-only list; not re-derived
here). Part 2 hunted for genuinely dead/unused code — a lens no prior
sweep in this lineage has applied. Before writing anything below, every
Epic 22/23 story was re-verified against current `git log`/code
(S-166/S-167/S-168/S-169 all confirmed merged and matching their own
"Built as" notes; S-170/S-171 confirmed still open/unimplemented at the
time this investigation ran, exactly as Epic 23 left them — not touched
here, per this pass's explicit instruction). A live `dotnet build`
(dotnet 10.0.111 available in this session) reproduced only the two
already-tracked `CS9113` warnings (S-170) and nothing new. **Both S-170
and S-171 have since merged** (PRs #247/#248, same day) — Epic 23 is now
fully closed; this note is left as-is rather than rewritten, since it
accurately describes this investigation's own starting state.

**Findings that turned out clean — recorded so the next sweep doesn't
re-derive them:**
- `XGArcade.DataSync`'s error-handling (`PlayerCareerPrefetchService.cs`,
  `PlayerPhotoBackfillService.cs`, `PlayerPositionBirthYearBackfillService.cs`,
  `PlayerFamiliarityService.cs`, `WikidataClient.cs`): every `catch` is
  narrow (`WikidataQueryException`, or `Exception ex when (ex is
  HttpRequestException or JsonException)`), logged with context, and each
  swallow-vs-throw choice is justified inline against
  `coding-guidelines.md`'s own "external-client error contracts" rule — no
  broad `catch (Exception)` swallow found anywhere in the module. Test
  depth spot-checked on the two files this pass's own git-log/churn lens
  would flag first (`PlayerCareerPrefetchServiceTests.cs`,
  `PlayerCacheWarmingServiceTests.cs`): both have dedicated cases for the
  swallow/throw/technical-failure branches, not just the happy path.
- `docs/architecture-document.md`'s COMP-07 claim that "the later
  by-QID/by-nationality/by-club/familiarity query methods... still
  hand-roll their own HTTP handling" is now **false** — verified against
  current `WikidataClient.cs`: every one of those methods
  (`QueryPlayerPoolByNationalityAsync`, `QueryPlayerPoolByClubAsync`,
  `QueryPlayerPhotosByQidsAsync`, `QueryPlayerPositionsAndBirthYearsByQidsAsync`,
  `QuerySitelinkCountsByQidsAsync`, etc.) is already a thin wrapper over
  the shared `RunThrowingQueryAsync` driver plus `SparqlQueryBuilders`/
  `SparqlResponseParsers` (S-118/S-124/S-155) — this is a genuine doc-drift
  finding, not a code finding; see S-172 below.
- `frontend/src/lib/*.ts` post-S-168 split: every exported symbol across
  all 27 `lib/*.ts` files was grepped for import sites elsewhere in the
  tree. The handful with zero external references (`DeleteUserResult`,
  `RoundCompletionResult`, `ResolvedTheme`, `CategoryType`,
  `PlayerDataApprovalResult`, `PlayerDataRemovalResult`,
  `ClearGuestAccountOutcome`, `AdminIncidentReportIssue`,
  `CurrentPathPuzzle`, `UseAuthedFetchOptions`, `UseAuthedFetchResult`,
  `UseRoundFetchResult`, `UseSessionResult`) all turned out to be exported
  types used only as an inferred return/parameter type of an exported
  function or hook in the same file — normal TypeScript practice, not dead
  code. No orphaned export, no orphaned component/screen found (every
  `.tsx` file under `frontend/src` is imported from at least one other
  file besides its own test).
- CLI verbs with no GitHub Actions trigger (`audit-club-gaps`,
  `backfill-player-position-birthyear`, `clean-duplicate-career-stints`,
  `clean-stale-club-attributes`, `clear-pair-lookup-failures`,
  `reset-path-target-cycle`, `verify-wikidata-player-data`): all seven
  are deliberately manual, workflow-wrapper-removed-but-verb-kept per
  S-132's own explicit "may legitimately be needed again" reasoning — not
  dead code, re-confirmed against each handler's own doc comment.
- `HandleVerifyWikidataPlayerDataAsync`'s bare `ExecuteUpdateAsync` (a
  `coding-guidelines.md` EF Core exception on its face): already carries
  its own inline justification citing the exact same established
  exception `purge-player-pool`'s `ExecuteDeleteAsync` uses (standalone
  operational CLI verb, never exercised by the InMemory-provider unit
  tests that rule protects) — not a violation, already documented.
- REQ-211's live-lookup gate: `CLAUDE.md`'s standing rule ("only trigger a
  live lookup when the guess matched a real `PlayerNameIndex` candidate")
  looked, on a first read of `GridLiveLookupDispatcher.cs` alone, like it
  might not be implemented — the gate actually lives one layer up, in
  `GridGameModule.cs`'s own `ScoreSubmissionAsync`
  (`playerNameIndexRepository.ExistsByNormalizedNameAsync` check, S-032,
  2026-07-17), whose own comment notes explicitly that "the 'Tier 1, not
  built' gap this comment used to describe is closed." False alarm,
  recorded so a future sweep doesn't re-open it without checking the
  caller first.
- No orphaned Tier 1 API-Football/`ExternalApiUsage` scaffolding exists in
  `backend/src` — every reference is a comment describing the deferred
  Tier 1 plan, not dead placeholder code.
- ADR-0032's supersession of ADR-0029's fallback-specific carve-out is
  fully landed in code (`WikidataLookupService.ConfidenceFor` maps both
  `WikidataLookupOrigin` values to `"verified"`) — no dead branch left
  behind.
- `docs/`'s own accretion (bloat) lens was already checked and cleared by
  the 2026-08-23 `CODE_HEALTH_ASSESSMENT.md` revision (`design-document.md`'s
  largest cells judged proportionate WCAG-math, not narrated history) —
  not re-litigated here per this pass's own explicit "don't relitigate a
  settled design point" instruction; Part 1 of this pass looked for
  *accuracy* drift instead (see S-172), a different question.

**S-172 · `docs/architecture-document.md` COMP-07: fix the stale "still hand-roll their own HTTP handling" claim**
The COMP-07 row's last sentence ("The 9 `CategoryType`-intersection
queries route through one shared spec-table-driven HTTP/timeout/retry
path (S-100/S-101); the later by-QID/by-nationality/by-club/familiarity
query methods above were added afterward and still hand-roll their own
HTTP handling — an open item, see `docs/backlog.md` Epic 9.") describes a
state that stopped being true across three separate stories
(S-118/S-124/Epic 9, then S-155/Epic 17) — every one of those methods is
now a thin wrapper over the shared `RunThrowingQueryAsync` driver plus
`SparqlQueryBuilders.cs`/`SparqlResponseParsers.cs`, confirmed by direct
read of current `WikidataClient.cs`. Rewrite the sentence to describe the
current, fully-centralized state (all query methods, both the 9
intersection queries and the by-QID/nationality/club/familiarity ones,
share one HTTP/timeout/retry path via `RunIntersectionQueryAsync`/
`RunThrowingQueryAsync`) and drop the stale `docs/backlog.md` Epic 9
pointer (Epic 9 is fully closed, confirmed via `CODEBASE_ANALYSIS.md`'s
own closeout note). Grep the surrounding COMP-07 row and §3's ADR-cross-
reference table (COMP-07 row) for anything else the old sentence's pointer
protected before rewriting, so nothing else depending on that phrasing
goes dangling.
*Accept:* doc-only change — the corrected sentence is checked against
current `WikidataClient.cs`'s actual method list before being written
(every `Query*Async` method traced to a shared driver, not asserted from
memory); no REQ/ADR reference is dropped, only the stale "open item"
framing.
*Deps:* none.
*Built as:* `docs/architecture-document.md`'s COMP-07 row rewritten
(2026-08-23) — verified every by-QID/by-nationality/by-club/familiarity
method in `backend/src/XGArcade.DataSync/Wikidata/WikidataClient.cs`
(`QueryPlayerPoolByNationalityAsync`, `QueryPlayerPoolByClubAsync`,
`QueryPlayerPhotosByQidsAsync`,
`QueryPlayerPositionsAndBirthYearsByQidsAsync`,
`QuerySitelinkCountsByQidsAsync`, etc.) is a thin wrapper over
`RunThrowingQueryAsync`, alongside the 9 intersection queries' own
`RunIntersectionQueryAsync` path, both driven by
`SparqlQueryBuilders.cs`/`SparqlResponseParsers.cs`. Replaced the stale
"still hand-roll their own HTTP handling ... Epic 9" sentence with a
description of the current fully-centralized state; checked §3's ADR
cross-reference table (COMP-07 row) and the rest of the COMP-07 row for
anything else depending on the old phrasing — nothing else referenced it.
No REQ/ADR/code change; frontmatter `version`/`last_updated` bumped.

**S-173 · Reconcile `infra/bicep/main.parameters.json` with `infra/README.md`/`SETUP.md`'s "does not exist yet" claim**
`infra/README.md` states, explicitly and twice-reinforced ("**Prod**:
`main.parameters.json`. **Does not exist yet.** Created at Tier 1's bright
line (a real user besides you)..."), that this file doesn't exist in Tier
0. It does — `infra/bicep/main.parameters.json` is present on disk right
now, with real (if generic-template) content (`environmentTag: "prod"`,
`location: "swedencentral"`, `minReplicas: 0`), and is referenced by
nothing: not `deploy.yml` (confirmed by grep — only
`main.parameters.dev.json` is ever passed to `az deployment group
create`), not any other workflow, not any script. `docs/review-2026-07-07.md`'s
own history shows a `main.parameters.json`/`main.parameters.nonprod.json`
pair existed side-by-side from the very first scaffold, before the
nonprod→dev rename (`docs/CHANGELOG.md`, 2026-07-07) and before Epic 10's
2026-08-17 "clean slate" product decision (S-130) that explicitly deleted
five other never-triggered/always-red Tier 1 prod-facing workflow files
(`backup-database.yml`, `promote-dev-to-prod.yml`, `sync-players.yml`,
`sync-prod-to-dev.yml`, `promote-dev-to-prod-dry-run.yml`) on the
reasoning "if a Tier 1 workflow has zero runs... delete it outright...
re-adding a thin wrapper later is cheap." This parameters file is the same
shape of leftover Epic 10 already decided the "delete now, cheap to re-add
at Tier 1" pattern for — it just wasn't caught in that pass because it's a
config file, not a workflow. This is a **"could reasonably have gone
another way" call, not decided here**: either (a) delete the file now, to
match `infra/README.md`'s own stated Tier 0 reality and Epic 10's
precedent, re-added trivially at Tier 1's bright line same as the deleted
workflows; or (b) keep it as a harmless pre-scaffolded template and fix
`infra/README.md`/`SETUP.md`'s wording instead. Flag for
`architecture-reviewer` to pick one; `doc-sync` executes whichever is
chosen (file deletion, or doc wording fix — either is a same-session,
low-risk change once decided).
*Accept:* whichever option is chosen, `infra/README.md`'s "does not exist
yet" claim and the actual repo state agree afterward; `SETUP.md`'s §7
prod-deploy snippet (which already correctly says "Tier 1 — skip for
MVP") is unaffected either way.
*Deps:* none.
*Built as:* Option (a) — deleted `infra/bicep/main.parameters.json`
(`architecture-reviewer` call, 2026-08-23). Reasoning: it's unreferenced
dead config, the exact same shape as the five Tier 1 workflow files Epic
10/S-130 already decided to delete rather than patch, and keeping a
generic-template file with real-looking values (`environmentTag: "prod"`)
sitting around unreferenced risks someone assuming it's live/authoritative
and deploying against it by hand. Re-adding it at Tier 1's bright line is
cheap, same as the deleted workflows. With the file gone,
`infra/README.md`'s and `SETUP.md`'s existing "does not exist yet" wording
is now simply true — no doc wording change was needed. Verified
`SETUP.md`'s §7 prod-deploy snippet (already labeled "Tier 1 — skip for
MVP") is untouched; it still names `main.parameters.json` as the file to
create when Tier 1 starts, which is correct forward-looking guidance, not
a claim about current repo state.

**S-174 · Add a Bicep template-validation step to CI, before the real `deploy.yml` deployment**
`deploy.yml`'s only interaction with `infra/bicep/main.bicep` is the real
`az deployment group create` call against the live dev resource group
(line ~99) — grepped `ci.yml`/`deploy.yml` directly: no `az bicep build`,
no `az deployment group validate`, no `what-if` step exists anywhere in
either workflow. A syntax error, a broken module reference, or a
parameter-name mismatch between `main.bicep` and its two `.parameters*.json`
files is therefore only ever caught at actual deploy time against the
live dev Container App/Static Web App — the same class of "verified only
when it's expensive to fail" gap `CLAUDE.md`'s own "Getting started" §
step 4 warns about for the *initial* deploy pipeline, just resurfaced for
every *subsequent* change to `infra/bicep/`. Add a validation step (`az
deployment group validate` or `--what-if`, whichever this repo's existing
`az` CLI usage in `deploy.yml` already assumes is available) that runs
against every PR touching `infra/bicep/**`, before merge — not as part of
the real deploy job itself, so a validation failure blocks the PR rather
than red-lighting a live deploy run.
*Accept:* a deliberately-broken test Bicep file (e.g. a typo'd module
path) is confirmed to fail the new validation step locally/in a scratch
branch before this lands for real; the real `deploy.yml` deploy step
itself is unchanged, still the actual source of truth. No `az` CLI in
this investigation's own sandbox — implement and verify in a session with
real Azure CLI/credentials access, per this file's own operational
nature.
*Deps:* none.
*Built as:* new dedicated `.github/workflows/validate-bicep.yml`, triggered
on `pull_request` scoped to `paths: infra/bicep/**` (rather than a
conditional step bolted onto `ci.yml`/`deploy.yml`), so it only ever runs
when there's actually Bicep to check and never touches the real deploy
job. Two layers: (1) `az bicep build --file infra/bicep/main.bicep` — a
pure local compile needing no Azure login at all, which directly catches
the "typo'd module path" failure mode named in this story's own
description; (2) `az deployment group validate` against the real dev
resource group (dev's actual secrets, same parameter shape as
`deploy.yml`'s `deploy-infra` step) to also catch parameter-name mismatches
and anything only visible once Azure actually looks at the template —
`containerImage`/`registryUsername` use inert placeholders since ARM
validation never pulls the image. `deploy.yml`'s `deploy-infra` job is
byte-for-byte unchanged. This investigation's own sandbox still has no
`az` CLI (confirmed: `az: command not found`), and GitHub doesn't register
a brand-new `pull_request`-triggered workflow until it exists on the
default branch (confirmed directly: a scratch PR opened before merging
produced zero workflow runs) — so verification happened via a scratch PR
(#254) against `main`, immediately after PR #253 merged this workflow.
Results: layer (1) fully verified working both ways — [a run with a
deliberately typo'd module path](https://github.com/johanpearson/xG-Arcade/actions/runs/32648442659)
failed with the expected `BCP091: Could not find file` before ever
reaching Azure login, and [a run with the path fixed](https://github.com/johanpearson/xG-Arcade/actions/runs/32648861622)
passed the compile step. Layer (2) is implemented correctly but is
currently blocked by an Azure AD (Entra ID) configuration gap, not a code
bug: that second run's Azure OIDC login itself failed with `AADSTS700213:
No matching federated identity record found for presented assertion
subject 'repo:johanpearson/xG-Arcade:pull_request'` — the federated
credential on the `AZURE_CLIENT_ID` app registration only trusts
`deploy.yml`'s push-to-`main` OIDC subject, not the `pull_request` subject
a PR-triggered OIDC login presents. **Resolved (2026-08-23, same day, in
the Azure Portal, outside this repo — no code change):** the user added a
federated credential to the `AZURE_CLIENT_ID` app registration via the
"GitHub Actions deploying Azure resources" scenario, entity type **Pull
request**. First attempt still failed with the identical `AADSTS700213`
error on two separate runs several minutes apart (ruling out propagation
delay) — root cause turned out to be that Azure's wizard had
auto-generated an **ID-qualified** subject
(`repo:johanpearson@32451746/xG-Arcade@1293474861:pull_request`,
embedding the numeric org/repo IDs) rather than the **plain name-based**
subject GitHub's OIDC token actually presents
(`repo:johanpearson/xG-Arcade:pull_request`) — the two formats don't
match even though both are theoretically valid, and this repo's tokens use
the plain form (confirmed: `deploy.yml`'s existing working credential is
also plain-form). Fixed by using the Subject identifier's "Edit
(optional)" override to set it to the plain form manually. Verified via a
second scratch PR (#258, closed without merging):
[this run](https://github.com/johanpearson/xG-Arcade/actions/runs/32654655099/job/97231420214)
shows all three real steps green — `az bicep build`, `Azure login (OIDC)`,
and `az deployment group validate`. **S-174 is now fully verified
end-to-end against real Azure; both layers of `validate-bicep.yml` are
confirmed working.** Also discovered along the way: GitHub silently skips
a paths-filtered `pull_request` check on a push that makes the watched
path byte-identical to the base branch again (a plain revert produced zero
runs) — worth knowing if a future "why didn't this check re-run"
investigation hits the same thing. And a general note for anyone adding a
GitHub Actions federated credential in Azure AD in the future: always use
"Edit (optional)" to check/set the plain name-based subject explicitly
rather than trusting the wizard's auto-generated value, unless the repo
has deliberately opted into GitHub's immutable-ID subject claims.

**S-175 · Extract a shared composite GitHub Action for the repeated "checkout, setup-dotnet, run a CLI verb, connect to dev DB" workflow shape**
Six standalone `workflow_dispatch`(-plus-cron) workflow files
(`backfill-player-photos.yml`, `import-player-name-index.yml`,
`prefetch-player-careers.yml`, `purge-game-history.yml`,
`purge-player-pool.yml`, `warm-grid-cache.yml`) plus two jobs inside
`ci.yml` and `deploy.yml` (`migrate-and-seed`) — 8 sites total, confirmed
by direct grep of `actions/checkout@v7`/`actions/setup-dotnet@v6`
(`dotnet-version: "10.0.x"`)/`dotnet run --project backend/src/XGArcade.Api
-- <verb>`/`ConnectionStrings__Database: ${{ secrets.DEV_DATABASE_CONNECTION_STRING }}`
— all repeat the identical 4-step shape, differing only in which CLI verb
runs and each workflow's own `timeout-minutes` value. This is the same
"duplicated shape repeated per near-identical block" pattern this lineage
has now caught seven times across backend/frontend (see
`CODE_HEALTH_ASSESSMENT.md`'s revision history) — never previously looked
for in `infra/`, which per this pass's own brief had "the least deep
scrutiny of any so far." Extract a composite action (e.g.
`.github/actions/run-cli-verb/action.yml`, taking `verb` as a required
input) that each of the 8 call sites invokes instead of hand-rolling the
4 steps. Flag for `architecture-reviewer`: composite action vs. a
`workflow_call` reusable workflow is a "could reasonably have gone another
way" call (a composite action keeps each `.yml` file's own `on:`/cron/
`timeout-minutes` fully independent, matching ADR-0072's own per-workflow-
independence reasoning; a reusable workflow would centralize more but
needs explicit secrets-passing, a bigger behavioral surface to get exactly
right) — this story doesn't decide it.
*Accept:* every one of the 8 call sites produces an identical Actions-tab
run (same steps, same env, same secret) before and after, confirmed by a
manual `workflow_dispatch` smoke-test run of at least one converted
workflow (e.g. `purge-player-pool.yml`, the smallest) in a session with
real GitHub Actions access — not verifiable in this investigation's own
sandbox.
*Deps:* none (may be sequenced with S-176 below, since both touch
`.github/workflows/`, but neither blocks the other).

*Built as (2026-08-23):* a **composite action**
(`.github/actions/run-cli-verb/action.yml`, `verb`/`arg`/`connection-string`/
`attempts` inputs), not a `workflow_call` reusable workflow — see ADR-0085
for the full reasoning (keeps each caller's `on:`/cron/`timeout-minutes`
independent, per ADR-0072's precedent; a reusable workflow's nested-run
Actions-tab shape would have risked failing this story's own "identical run"
bar). Converted **7** of the listed 8 sites — `ci.yml`'s "Migrate + seed
local database" step was examined and found not to actually match this
shape (different, non-secret connection string; shares its job's checkout/
setup-dotnet with unrelated frontend/Playwright steps rather than owning a
standalone block) and was deliberately left unconverted, with a comment at
the site explaining why. Each of the 7 real call sites keeps its own
`actions/checkout@v7` step before invoking the composite action — a local
composite action's own `action.yml` must already be resolvable from a
checked-out workspace, so checkout cannot be folded into the composite
action itself without becoming circular; this is a genuine GitHub Actions
constraint, not leftover duplication. `warm-grid-cache.yml`'s existing
2-attempt retry/`::warning::`/`::error::` shape is reproduced byte-for-byte
via the composite action's `attempts: '2'` input; the other 6 real sites use
the default `attempts: '1'` (single run, no synthetic annotations, output
unchanged from before). **Not verified in this sandbox:** the *Accept*
criterion's manual `workflow_dispatch` smoke-test of a converted workflow
(e.g. `purge-player-pool.yml`) against real GitHub Actions — this sandbox
has no path to trigger a real dispatch; needs a human or a session with real
GitHub Actions access to run that smoke-test and confirm the Actions-tab run
is identical before/after.

**S-176 · Deduplicate the byte-identical `generate_round` retry-with-backoff bash function in `generate-grid-round.yml`/`generate-path-round.yml`**
S-136/ADR-0072 deliberately split a single shared `generate-round.yml`
into two independent per-`GameKey` workflow files so their cron/
`RoundDurationHours` coupling could diverge — a real, already-decided
structural call, not being relitigated here. What that split did *not* do
is dedupe the actual bash logic each file's "Trigger round generation"
step runs: the entire `generate_round()` function (URL construction,
3-attempt/30s-60s-backoff retry loop, `::warning`/`::error` annotations)
is byte-for-byte identical in both files (confirmed via direct diff),
differing only in the final `generate_round "xg-grid"` vs.
`generate_round "xg-path"` call. ~35 lines of real retry/backoff logic,
not boilerplate — a bug fixed in one file's copy (the kind of fix this
function has already needed once, per its own "Bug-bundle follow-up
(2026-07-27)" comment) has no mechanism to keep the other file's copy in
sync. Extract into a composite action (e.g.
`.github/actions/trigger-round-generation/action.yml`, taking `game-key`
and `round-duration-hours` as inputs) both workflows call — this is
compatible with ADR-0072's own decoupling intent (each workflow's `on:`
trigger, cron, and `round_duration_hours` input stay fully independent;
only the executed retry-loop body is shared) not a re-coupling of the two
games' scheduling. Flag for `architecture-reviewer` to confirm that
reading before implementation, since ADR-0072 is the ADR this story's fix
sits right next to.
*Accept:* both `generate-grid-round.yml` and `generate-path-round.yml`
produce identical retry/backoff behavior (3 attempts, 30s/60s backoff,
same `::warning`/`::error` annotation wording) before and after, confirmed
by a manual `workflow_dispatch` smoke-test of at least one in a session
with real GitHub Actions access — not verifiable in this investigation's
own sandbox.
*Deps:* none (may be sequenced with S-175, neither blocks the other).

*Built as (2026-08-23):* extracted a **composite action**
(`.github/actions/trigger-round-generation/action.yml`, `game-key`
(required), `round-duration-hours`/`backend-hostname`/`internal-job-token`
inputs), reproducing the original `generate_round()` function's retry loop
byte-for-byte (3 attempts, 30s/60s backoff via `sleep $((attempt * 30))`,
identical `[$game_key] Attempt $attempt/$max_attempts` and
`::warning`/`::error` annotation wording — confirmed by diffing the old
inline function bodies against the new action's `run:` block, only
variable names changed from `${{ inputs.* }}`/`${{ secrets.* }}`
interpolation to `env:`-mapped shell variables, which composite actions
require since they can't read the calling workflow's own `inputs`/
`secrets` context directly). `architecture-reviewer` confirmed this
reading before implementation (see prompt/response captured in this
session): a composite action defines no `on:`/trigger surface of its own,
so it cannot reintroduce the `workflow_dispatch` coupling ADR-0072 fixed —
each workflow file's `on.schedule` cron and its own
`workflow_dispatch.round_duration_hours` input definition are completely
untouched, only the retry-loop bash body is now shared. Both
`generate-grid-round.yml` and `generate-path-round.yml` gained one
`actions/checkout@v7` step before the composite-action call (same
GitHub Actions constraint as S-175/ADR-0085: a local composite action's
own `action.yml` must be resolvable from a checked-out workspace).
Updated ADR-0072's "Consequences" and "For AI agents" sections to record
that the duplication trade-off it originally accepted is now resolved by
this composite action, and to note that composite actions are explicitly
outside its "no shared/reusable workflow or matrix" prohibition — see
ADR-0085 for the general composite-action-vs-reusable-workflow reasoning
this mirrors. **Not verified in this sandbox:** the *Accept* criterion's
manual `workflow_dispatch` smoke-test against real GitHub Actions — this
sandbox has no path to trigger a real dispatch; needs a human or a session
with real GitHub Actions access to dispatch both workflows and confirm the
Actions-tab run (steps, annotation wording, attempt/backoff timing) is
unchanged from before this refactor.

**Watch-only / declined (no story):**
- `purge-guest-accounts.yml`'s single-attempt curl+status-check shape is a
  simpler cousin of S-176's `generate_round` retry function (no backoff
  loop, no multi-attempt logic) — genuinely a different, smaller shape,
  not the same duplicated block. Left out of S-176's scope deliberately;
  revisit only if a third near-identical retry-loop workflow appears.
- `infra/scripts/sync-prod-to-dev.sh`/`promote-dev-to-prod.sh`: re-confirmed
  unchanged from every prior sweep's watch-only verdict — not
  re-investigated beyond that confirmation, per this lineage's own
  standing "don't relitigate a settled finding" discipline.
- `LeaderboardScreen.tsx`'s documented line count in
  `docs/implementation-document.md` (261 lines, from S-121) is now stale
  (actual: 310 lines, +19% via untracked incremental growth) — but the
  file's described role ("thin orchestrator: header, game-key switcher,
  scope tab bar, the scoring explainer modal") still matches current code
  on inspection; this is stale-number drift, not a factual error the way
  S-172's COMP-07 claim was. Not written up as a story — busywork against
  an already-accurate description; revisit only if the file's actual
  responsibilities drift, not just its line count.
- `docs/`'s own accretion/bloat lens: re-confirmed clean, not re-litigated
  (see "Findings that turned out clean" above).

## Epic 25 — Admin UX regrouping, player stats, and avatar uploads (2026-08-24 planning session)

Product owner asked directly for three things in one session: the admin
page (`AdminScreen.tsx`) feels scattered as one long scroll and should be
grouped into a navigable set of sections; players should be able to see
their own and other players' stats (best score, average score, rounds
played); and players should be able to upload a profile avatar, gated
behind admin approval so nothing inappropriate goes live. REQ-411/516/517/722
added to `docs/requirements-document.md` v2.02 in the same session — see
those entries for full acceptance criteria; each story below only repeats
enough to scope the session, not the full REQ text.

**S-177 · Admin screen grouped sub-navigation (REQ-516)**
Replace `AdminScreen.tsx`'s current single vertical stack of independent
sections with a grouped nav (tabs or equivalent): **Users**
(`AccountMetricsSection`, `UserDeletionSection`, `GuestClearSection` — plus
a slot reserved for S-183's avatar moderation section, added in a later
story, not this one), **Grid** (`UnverifiedDataSection`,
`PlayerSuggestionsEntry`, `RoundControlSection`), **Path**
(`XGPathCycleSection`), **Announcements** (`AnnouncementBannerSection`),
**Issues** (`IncidentReportsEntry`). Pure frontend layout change — every
section keeps its own independent `useAuthedFetch` instance and gating
exactly as today (in particular the page-level `rowsHidden ||
activeRoundHidden` access-denied check and the Production-only hiding of
`RoundControlSection`/`UserDeletionSection` behind `activeRound !== null`
are both unchanged, just now inside a specific group rather than always
rendered). No new endpoints.
*Accept:* selecting a group shows only that group's sections; switching
groups does not re-fetch a section that already loaded data this page
visit; a non-admin still sees the page-level access-denied message before
any group renders; `RoundControlSection`/`UserDeletionSection` are still
entirely absent from the DOM in Production, now within the "Users"/"Grid"
groups rather than always-rendered.
*Deps:* none.

*Built as (2026-08-24):* a persistent `role="tablist"` tab bar (5 tabs,
"Users" default), each group rendered as an always-mounted
`.admin-screen__group` wrapper toggled via the `hidden` attribute rather
than conditional rendering — the same "always mounted, active-controlled"
pattern `LeaderboardScreen.tsx`'s scope tabs already established — so no
section's fetch is ever re-triggered by a group switch.
`RoundControlSection`/`UserDeletionSection` keep their real
`activeRound !== null` conditional nested inside the "Grid"/"Users" groups
respectively, still fully unmounting in Production. One naming deviation
from this story's own text above: there is no separate `GuestClearSection`
component to place in the "Users" group — REQ-508's guest-clear UI was
already composed inside `AccountMetricsSection` before this story, and
stays that way (not split out), so "Users" renders `AccountMetricsSection`
(alone) plus the Production-gated `UserDeletionSection`, and a reserved,
not-yet-built slot for S-183's avatar moderation section. Architecture
review: PASS, no boundary change, no ADR needed (pure frontend layout).
Quality review: PASS, no blocking findings. `docs/design-document.md`
SCREEN-04 updated to describe the grouped nav in the same session
(`doc-sync`).

**S-178 · Backend player stats aggregate endpoint (REQ-411)**
`GET /users/{userId}/stats?gameKey=` returning, scoped to one `GameKey`:
rounds played, best single round `FinalPoints`, average `FinalPoints`,
and current all-time rank (omitted below REQ-409's 5-round minimum).
Reuse `LeaderboardService`/`IGuessRepository`'s existing per-round-total
and qualifying-round queries (REQ-408/409) rather than a new aggregate
path — this is a read composed from data already computed for the
leaderboard, not a new scoring concept. 401 with no session; 404 for a
nonexistent `userId`; both the caller's own id and another player's id
return the same shape (REQ-411 sets no privacy toggle).
*Accept:* unit tests (`REQ411_*`) cover the zero-qualifying-rounds "no
rounds played" shape (not `0`-filled) and the omitted-below-minimum rank;
API tests cover 401/404 and that both self and other-player lookups
return identical shapes.
*Deps:* none.

*Built as (2026-08-24):* `GET /users/{userId}/stats?gameKey=`
(`UserEndpoints.cs`, `MapUserEndpoints`) plus a new
`ILeaderboardService.GetUserStatsAsync`. `ValidateGameKey`/
`ResolveRequestingUserAsync` (previously `private` on
`LeaderboardEndpoints`) were made `internal` and reused rather than
duplicated — one gameKey allowlist and one auth-resolution path for the
whole Api layer. Rank is computed from `GetRankedMembersAsync`, a private
helper extracted from `GetGlobalLeaderboardAsync` with no behavior change
to that method, so a player's rank here can never drift from the
leaderboard's own ranking. **Guest/claimed-account bug found and fixed
mid-implementation:** the first version reused
`IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync` as-is, but that
query's REQ-717/ADR-0036 guest-exclusion clause was unconditional, so a
guest account (and a claimed account's pre-claim rounds) always came back
as "zero rounds played" even with 5+ genuinely qualifying rounds —
contradicting REQ-411's own "Out of scope" text, which carves out only the
*rank* figure from guest-eligibility rules. Fixed by adding an
`applyGuestEligibilityRules` parameter (default `true`, preserving
`GetRankedMembersAsync`'s existing ranking call and its pre-existing tests
unchanged); `GetUserStatsAsync` passes `false` for rounds-played/best/
average while Rank still goes through the unchanged, guest-excluding path.
Covered by two follow-up tests (guest-inclusion, claimed-account
pre-claim-rounds). Final file list: `UserEndpoints.cs` (new),
`ILeaderboardService.cs`/`LeaderboardService.cs`, `IGuessRepository.cs`/
`GuessRepository.cs`, `LeaderboardEndpoints.cs` (visibility only),
`EndpointMapping.cs`; tests in `UserEndpointTests.cs` (new) and
`LeaderboardServiceTests.cs`. Architecture review: PASS, no ADR needed —
the `applyGuestEligibilityRules` parameter and the `internal` visibility
changes are both narrow, spec-driven extensions of already-decided
boundaries, not new structural decisions. doc-sync: REQ-411 status note,
`architecture-document.md` COMP-02/§5.3/§6.2a updated.

**S-179 · Frontend player stats/profile screen (REQ-411)**
A stats/profile screen consuming S-178's endpoint: an entry point to view
your own stats (from Settings or header nav, matching REQ-712/713's
existing nav patterns), and a way to view another player's stats by
selecting their `DisplayName` on the leaderboard (`LeaderboardScreen.tsx`'s
rows currently render `DisplayName` as plain text — this story makes it a
navigation target). Reuse the leaderboard's existing game-key switcher
pattern for the per-`GameKey` scoping REQ-411 requires. Zero-rounds-played
renders a distinct empty state, not a blank or `0`-filled screen.
*Accept:* UI tests cover own-stats entry point, other-player navigation
from a leaderboard row, the zero-rounds empty state, and per-game
switching.
*Deps:* S-178.

*Built as (2026-08-24):* a new `UserStatsScreen.tsx`/`.css`/`.test.tsx`
(`frontend/src/users/`, SCREEN-13) — one component for both "own stats"
and "another player's stats," since it has no own-vs-other concept beyond
the `userId`/`displayName` props it's handed, consuming S-178's `GET
/users/{userId}/stats?gameKey=` via a new `fetchUserStats`
(`frontend/src/lib/userStats.ts`) and `UserStatsResponse`
(`frontend/src/lib/types.ts`). Reuses `LeaderboardScreen.tsx`'s existing
`XG_GRID_GAME_KEY`/`XG_PATH_GAME_KEY` tab pattern for per-`GameKey`
scoping. Own-stats entry point: an unconditional "My stats" link/section
on `SettingsScreen.tsx` (not admin-gated, styled like the existing
admin-link section). Other-player entry point: `LeaderboardRowsList.tsx`'s
main-list row display names became `<button>` nav targets via a new
optional `onSelectPlayer(userId, displayName)` prop, threaded through
`AllTimeLeaderboard`/`LiveLeaderboard`/`PastRoundsLeaderboard`/
`WindowedLeaderboard` and `LeaderboardScreen.tsx` up to `App.tsx`.
Judgement call, documented inline in `LeaderboardRowsList.tsx`: the
requesting user's own row, when already visible in a loaded page, is
clickable too, for list consistency (a partial list where only some names
are clickable would read as broken) — the pinned "you" footer row (REQ-607)
stays plain text since Settings already covers that destination. `App.tsx`
gained a `'stats'` value on its existing hand-rolled `Screen` union plus a
`#/stats` hash entry (ADR-0039 pattern), an in-memory
`statsTarget`/`statsReturnScreen` seed (same pattern
`leaderboardInitial`/`LeaderboardRoundTarget` already established,
ADR-0083), and `handleOpenOwnStats`/`handleSelectPlayerStats` handlers so
"Back" returns to whichever screen (Settings or Leaderboard) the player
actually came from. `docs/design-document.md` gained SCREEN-13 plus short
SCREEN-03/SCREEN-08 addenda (written alongside the implementation, v0.78).
Full test coverage: `UserStatsScreen.test.tsx`,
`LeaderboardRowsList.test.tsx` (new), extensions to
`SettingsScreen.test.tsx`/`LeaderboardScreen.test.tsx`, and 3 new
`App.test.tsx` routing cases (own-stats-from-Settings-and-Back,
other-player-from-leaderboard-and-Back-to-leaderboard,
reload-restore-fallback-to-own-stats). 681 frontend tests passing, 0
failures; `npx tsc -b` and lint both clean. No backend changes — S-178's
backend is untouched by this story. Architecture review: PASS, no ADR
needed (narrow, spec-driven extension of already-decided patterns —
ADR-0039 hash routing, ADR-0083 nav-seed). Quality review: pass, after one
follow-up round closing a test gap. doc-sync: REQ-411 status note
(`requirements-document.md`) updated from "backend only" to fully
implemented; `docs/implementation-document.md` §4 gained a new `/users`
folder entry plus short additions to `/settings`, `/leaderboard`, and
`/lib` recording the new files this story added.
`docs/architecture-document.md` checked and left unchanged — pure frontend
consuming an already-documented COMP-02 endpoint, no component/data-flow
boundary change per its own `update_when` trigger.

**S-180 · ADR + backend avatar upload pipeline (REQ-722)**
Write the ADR REQ-722 flags (Supabase Storage vs. Azure Blob — product
direction from the 2026-08-24 planning session is Supabase Storage, to
avoid adding Azure-specific code to `Core`/`Api` per ADR-0004; the ADR
should record this choice and reasoning, not relitigate it) before or
alongside implementation. Add an `AvatarSubmission` entity (mirroring
`PlayerSuggestion`'s submit/review/decide shape — status `Pending`/
`Approved`/`Rejected`, submitting `UserId`, image reference, timestamps)
and an `IAvatarStorage` abstraction implemented against Supabase Storage,
kept out of `XGArcade.Core`/`XGArcade.Api` proper the same way every other
hosting-specific concern is (ADR-0004). `POST /users/me/avatar`: creates
or replaces the caller's `Pending` submission (never two pending rows for
one player); a prior `Approved` avatar stays visible to other players
until the new submission is itself approved. Size/type limits enforced,
specifics left to implementation.
*Accept:* the ADR is written and referenced from REQ-722's own "Needs an
ADR" note; unit tests cover replace-not-duplicate for a second pending
upload and that approving a new submission supersedes an older `Approved`
one; API tests cover 401 and the size/type rejection.
*Deps:* none (may run before or alongside S-177/S-178, touches neither).

*Built as (2026-08-24):* `AvatarSubmission` (`XGArcade.Data.Entities`,
`Pending`/`Approved`/`Rejected`, no FK to `User` — mirrors
`PlayerSuggestion.SubmittingUserId`'s existing no-FK reasoning) plus
`IAvatarSubmissionRepository`/`AvatarSubmissionRepository` and a migration.
`IAvatarStorage` (`XGArcade.Core/Storage/IAvatarStorage.cs`) is the
upload/best-effort-delete contract; its concrete implementation,
`SupabaseAvatarStorage`, was placed in a **new project**,
`XGArcade.Storage` — deliberately not copying
`XGArcade.Core.Auth.SupabaseAuthClient`'s existing in-`Core` placement, a
stricter application of ADR-0004's hosting-agnostic boundary than that
pre-existing precedent, per `architecture-reviewer`'s specific note on the
diff. `POST /users/me/avatar` (`XGArcade.Api.Avatars.AvatarEndpoints`)
enforces a 5 MB cap and `image/jpeg`/`image/png`/`image/webp` only — no
`image/gif`/`image/svg+xml`, SVG excluded deliberately since it can carry
executable content — and replaces rather than duplicates an existing
`Pending` submission, best-effort deleting the superseded image. The full
decision (provider choice, client placement, alternatives considered) is
recorded in ADR-0087. Architecture review: PASS, no blocking findings
(the two doc gaps it flagged — `architecture-document.md` component entry
and this note — closed by `doc-sync` the same session). Quality review:
PASS, no blocking findings. `dotnet test`: 1673 passed, 0 failed, verified
via a real SDK in-sandbox. REQ-517's admin approve/reject (S-181) and
REQ-722's frontend (S-182) remain separate, not-yet-built stories.

**S-181 · Backend admin avatar moderation endpoints (REQ-517)**
`GET /admin/avatar-submissions` (pending only, oldest first, image preview
reference + submitter `DisplayName` + submission time — mirrors REQ-509's
existing pending-suggestion ordering), plus approve/reject actions under
the existing `"Admin"` policy. Approving supersedes any prior `Approved`
row for that player; rejecting leaves a prior `Approved` avatar untouched;
acting twice on an already-decided submission is rejected with a clear
error, not a silent success (submissions are terminal once decided, per
REQ-517's "Out of scope"). No reason/comment field on rejection.
*Accept:* API tests cover the pending-only oldest-first ordering, 401/403
under the Admin policy, and the reject-a-second-decision-on-the-same-row
error case.
*Deps:* S-180 (needs `AvatarSubmission` to exist).

*Built as (2026-08-24):* `IAvatarSubmissionRepository` gained
`GetByIdAsync`/`GetAllPendingAsync` (oldest-first, mirrors
`IPlayerSuggestionRepository.GetPendingAsync()`) and race-safe
`ApproveAsync`/`RejectAsync` (both re-check `Status==Pending` inside the
same tracked load before writing, mirroring
`PlayerSuggestionRepository.ResolveAsync`'s bool-return race guard);
`ApproveAsync` additionally looks up and deletes any prior `Approved` row
for the same `SubmittingUserId` in the same `SaveChangesAsync`, following
`CreateOrReplacePendingAsync`'s existing "replace, don't invent a new
status" precedent rather than adding a `Superseded` enum member.
`IAvatarStorage` gained `GetPreviewUrlAsync` (ADR-0087's own anticipated
Follow-up, not a new ADR) — implemented in `SupabaseAvatarStorage` as a
5-minute signed URL via Supabase Storage's `POST /storage/v1/object/sign/
{bucket}/{path}`, and as a deterministic placeholder in
`LocalE2EAvatarStorage`/`FakeAvatarStorage` for the local-e2e/test paths.
New file `XGArcade.Api.Admin.AdminAvatarEndpoints` (`GET
/admin/avatar-submissions`, `POST .../approve`, `POST .../reject`)
mirrors `AdminSuggestionEndpoints`'s list/act-by-id/409-on-already-
resolved shape, registered in `EndpointMapping.cs` next to
`MapAvatarEndpoints`. Response DTO (`PendingAvatarSubmissionResponse`)
exposes the resolved `ImagePreviewUrl`, never `ImageStorageKey`. Built
without a local `dotnet` SDK in-sandbox — hand-traced against
`AdminSuggestionEndpointTests`/`AvatarEndpointTests`'s existing patterns,
not locally run; a CI verification run is required before this is
considered done.

**S-182 · Frontend avatar upload UI in Settings (REQ-722)**
A "My avatar" section in `SettingsScreen.tsx`, alongside REQ-714's
existing display-name edit: upload control, and a display of whichever of
the four states applies (none / pending / approved / rejected) with an
image preview for pending/rejected. A rejected status never hides a
separately-existing approved avatar from an earlier submission.
*Accept:* UI tests cover each of the four states rendering distinctly,
and that uploading while pending replaces rather than queues a second
submission (matching S-180's backend behavior).
*Deps:* S-180.

*Built as (2026-08-24):* the "My avatar" section (`SettingsScreen.tsx`,
SCREEN-08 addendum — see `design-document.md`, v0.78 → v0.79, added before
this merge) needed two backend read endpoints that didn't exist yet, built
in the same story: `GET /users/me/avatar` (three independent
Pending/Rejected/Approved summaries — never one mutually-exclusive status,
per REQ-722's own "a `Rejected` status does not remove or affect a
separately-existing `Approved` avatar" clause) and `GET
/users/me/avatar/{id}/image` (owner-only byte stream; 404, never 403, for
unknown/not-owned/underlying-storage-missing rows alike, so existence is
never leaked). Backend support added: `IAvatarStorage.DownloadAsync`
(owner-scoped, streams bytes through the backend per ADR-0013 — a second,
narrower mediation shape alongside S-181's admin-facing
`GetPreviewUrlAsync`, reconciled in ADR-0087's Consequences section) and
`IAvatarSubmissionRepository.GetLatestRejectedAsync`.
`IAvatarSubmissionRepository.GetByIdAsync` was added independently by both
this story and S-181 with an identical signature (each needed "fetch by
id, any status, let the caller enforce its own authorization rule" for its
own handler); deduped to one copy during the merge with `origin/main`,
now shared by S-181's admin approve/reject handlers and this story's image
endpoint. Test coverage: roughly 27 new tests added across this story's
commits (16 backend `[Test]` cases across `AvatarEndpointTests.cs`,
`AvatarSubmissionRepositoryTests.cs`, and `SupabaseAvatarStorageTests.cs`,
plus 11 frontend `it(...)` cases in `SettingsScreen.test.tsx` covering all
four avatar states). Architecture review: PASS — flagged that `S-181`
landed in parallel on `origin/main` with an overlapping `GetByIdAsync`
addition and the `IAvatarStorage`/ADR-0087 doc updates, requiring a real
merge-conflict reconciliation (not just a rebase) rather than a clean
fast-forward, and separately asked for the ADR-0087 addendum reconciling
`DownloadAsync` against S-181's `GetPreviewUrlAsync` (added, see that
ADR). Quality review: conditional pass, two must-fix items, both since
fixed — (1) the `ClaimsPrincipal` → `IUserRepository` → 401 caller-identity
block, now duplicated a third time across this file's three handlers,
extracted into a shared `ResolveCurrentUserAsync` helper (pure mechanical
extraction, no behavior change); (2) two test-coverage gaps on the new
image endpoint — 401 with no bearer token, and 404 specifically for the
"owned row, but the underlying storage object is gone" branch, distinct
from the already-covered unknown-id/not-owned-id 404 cases. Built without
a local `dotnet` SDK in-sandbox; confirmed via a real CI run (`ci.yml`,
`workflow_dispatch`) on the final commit — backend, frontend unit, and E2E
jobs all green.

**S-183 · Frontend admin avatar moderation section (REQ-517)**
An avatar moderation section consuming S-181's endpoints, with image
previews and approve/reject actions, slotted into S-177's "Users" admin
nav group (the group reserved a slot for exactly this). Pending-count
badge next to the section's own heading (this section renders inline, not
behind a separate nav entry), mirroring REQ-512's existing pending-count
convention.
*Accept:* UI tests cover the pending queue rendering with previews,
approve/reject removing a row from the list, and the pending-count badge
matching the number of rows returned; confirm the section renders inside
the "Users" group, not as a standalone top-level section.
*Deps:* S-177, S-181.

*Built as (2026-08-24):* a new `AvatarModerationSection.tsx`
(`frontend/src/admin/`) consumes S-181's three endpoints via three new
`frontend/src/lib/admin.ts` functions and a new `PendingAvatarSubmission`
type (`frontend/src/lib/types.ts`), rendered unconditionally (this
endpoint is registered in every environment) inline in `AdminScreen.tsx`'s
"Users" group, immediately below `AccountMetricsSection`. Each pending
row shows an image preview, the submitter's display name (falling back to
"a deleted user" per REQ-710, matching `SuggestionsScreen`'s existing
convention), and the submission time, oldest first. Approve/reject are
per-row actions with per-row action/error state; a `409` (already
resolved by another admin) renders a distinct "Already resolved" message
plus a "Refresh list" action rather than a generic error, mirroring
`SuggestionsScreen`'s `PlayerReviewPanel` conflict handling. The
pending-count badge is an "Avatar moderation (N)" heading badge —
mirroring `UnverifiedDataSection`'s inline heading-badge convention
rather than `PlayerSuggestionsEntry`'s button-label badge, since this
section has no separate click-through entry point — omitting the "(N)"
suffix at zero per REQ-512's convention. No new visual token: the only
new CSS is a 64px rounded image thumbnail reusing existing spacing/color
tokens. Verified with `AvatarModerationSection.test.tsx` (8 tests) and an
extension of `AdminScreen.test.tsx` confirming the section renders only
inside the "Users" group. 689/689 frontend tests passing; `tsc -b` and
lint both clean; a `ci.yml` `workflow_dispatch` run (backend/frontend/E2E,
run #616) passed in full. `architecture-reviewer`: PASS, no ADR needed.
`quality-architect`: PASS, after one wording-drift finding (this story's
own "nav entry" phrasing, and REQ-517's matching bullet, corrected in a
separate commit to describe the heading-badge convention actually
shipped).

**Also required, same iteration as S-180-183 land (not a separate story,
per CLAUDE.md's legal-docs rule):** update `docs/legal/*.md` — user-
uploaded images are a new category of collected data, and the privacy
policy draft must reflect it before this ships to anyone but the product
owner.

**S-184 · Other-players avatar view + Settings profile header (REQ-722)**
Close REQ-722's last unbuilt criterion, "No avatar / rejected state, as
seen by other players" — flagged in S-182's own status note as having "no
assigned story yet." Add a read surface that renders another player's
`Approved` avatar, or a placeholder when none exists, and never a
`Pending`/`Rejected` image — wired into `UserStatsScreen.tsx` (S-179),
which today renders no avatar at all. Also add a small profile header to
`SettingsScreen.tsx` (avatar + display name, self-view) since Settings
already resolves the caller's own approved-avatar image for the existing
"My avatar" section.
*Accept:* API tests cover 200-with-image-bytes for a caller requesting a
*different* user's avatar, 404 when the target has no `Approved` avatar
(never uploaded, or only `Pending`/`Rejected`), and 401 unauthenticated. UI
tests cover the placeholder rendering when no avatar exists and that the
component works identically for both a viewer's own id and another
player's.
*Deps:* S-180, S-181, S-182 (needs `AvatarSubmission`, the approval flow,
and the existing avatar object-URL plumbing to exist).

*Built as (2026-08-25):* `GET /users/{userId}/avatar/image`
(`XGArcade.Api.Avatars.AvatarEndpoints`) — the deliberate opposite of the
owner-only `GET /users/me/avatar/{id}/image`: the caller is only verified
as logged-in (401 if not, via the existing `ResolveCurrentUserAsync`
helper), never compared against `{userId}`. Reuses
`IAvatarSubmissionRepository.GetApprovedAsync`/`IAvatarStorage.DownloadAsync`
as-is — no new repository or storage logic, since both were already
generic on `submittingUserId` (only `GetApprovedAsync`'s doc comment was
stale, describing it as "the caller's own"; corrected in the same story).
Collapses "never uploaded," "only `Pending`," and "only `Rejected`" into
the same 404, matching REQ-722's own single no-avatar-state framing. 3 new
backend tests in `AvatarEndpointTests.cs` (200 cross-user fetch, 404
no-approved-avatar covering both the no-submissions and
rejected-only cases, 401 unauthenticated).

Frontend: a new shared `PlayerAvatar.tsx`/`.css`
(`frontend/src/components/`) — a small circular thumbnail for *any*
`userId` (own or another player's, no own-vs-other concept, mirroring
`UserStatsScreen.tsx`'s own pattern), fetching via a new
`fetchUserAvatarImageObjectUrl` (`lib/avatar.ts`) and degrading quietly to
a placeholder silhouette on any failure (a 404, or any other error) — no
visible error, no broken-image icon. Wired into `UserStatsScreen.tsx`'s
header next to the "{DisplayName}'s stats" heading, closing REQ-722's last
open criterion. A new profile header on `SettingsScreen.tsx` (first thing
under the "Settings" heading, above the guest-claim section) shows the
account's own avatar and display name as plain text — not editable, the
existing "Display name" section stays the only place that changes —
reusing the `approvedImageUrl` this screen already resolves for its
existing "Currently visible to other players" status row, rather than
mounting `PlayerAvatar` and re-fetching the same image a second time
through the cross-user endpoint.

Quality-review fix pass (same iteration): the placeholder silhouette SVG
had been copy-pasted a third time (`CellState.tsx`'s pre-existing
`CellPlaceholderAvatar`, REQ-216; `PlayerAvatar.tsx`; `SettingsScreen.tsx`'s
new `ProfileAvatarPreview`) — extracted into a shared
`PersonSilhouetteIcon.tsx` (`frontend/src/components/`) per
`docs/coding-guidelines.md`'s rule-of-three, each call site keeping its own
wrapper/className/sizing/`aria-hidden` treatment unchanged.
`SettingsScreen.css`'s new `.settings-screen__profile-name` 16px font-size
was flagged and resolved as reuse of this same file's existing 16px
precedent, not a fabricated token — this codebase has no font-size/
type-scale token in `design-document.md` §2 to swap to instead.
`IAvatarSubmissionRepository.GetApprovedAsync`'s stale "caller's own" doc
comment corrected to generic. `docs/design-document.md` updated in the same
iteration (§2 placeholder-shape reuse note, SCREEN-13 and SCREEN-08
sections, v0.79 → v0.80). 711/711 frontend tests passing (verified
in-sandbox with a real `npm run test` run); `npx tsc -b` and `npm run lint`
both clean. Backend built without a local `dotnet` SDK in-sandbox —
hand-traced against `AvatarEndpointTests.cs`'s existing patterns, not
locally run; a CI verification run is still needed before this is
considered fully done. `architecture-reviewer`: PASS, no ADR needed — this
is the exact follow-up ADR-0087's own "Consequences" section already
anticipated (COMP-14 already names `DownloadAsync` as the canonical shape
for "any future avatar-viewing surface," explicitly citing this case).

**S-185 · Guest exclusion from display-name/avatar editing + pencil-icon
Settings redesign (REQ-714/717/722)**
By direct product decision (johan.pearson, this Settings-redesign
session), a guest account (`User.IsGuest = true`) can no longer edit its
display name or upload an avatar until it claims the account (`POST
/auth/claim`, REQ-717) — reversing REQ-714's and REQ-722's prior
unrestricted-guest scope and REQ-717's own "no guest-specific edit path"
statement. `requirements-writer` amended REQ-714/717/722 with dated status
notes and new guest-exclusion acceptance criteria ahead of this story's
implementation.
*Accept:* API tests cover a `403` with claim-account guidance on both
`PUT /auth/display-name` and `POST /users/me/avatar` for a guest caller,
unaffected for a claimed/non-guest caller (REQ-714/722's own "Test level"
sections). UI: the profile-header edit pencil is never rendered for a
guest, with a muted claim-first hint shown in its place.
*Deps:* S-180, S-181, S-182, S-184 (needs `POST /users/me/avatar`, `PUT
/auth/display-name`, and S-184's profile header to exist).

*Built as (2026-08-25):* Backend — `AuthController.UpdateDisplayName`
(`PUT /auth/display-name`) and `AvatarEndpoints`'s `POST
/users/me/avatar` each gained a server-side `if (user.IsGuest)` check
returning a 403 with claim-first guidance, checked before either
handler's own cheaper local validation (length bound / file size-type
limit) — same plain `IsGuest` gate REQ-215's `SuggestionEndpoints.cs` and
REQ-903's `IncidentEndpoints.cs` already use for their own
guest-exclusion paths. `AvatarEndpoints.cs`'s top-of-file/handler comments
(previously "no guest exclusion here, re-verified against the REQ text")
rewritten to describe the reversal. New tests:
`REQ714_UpdateDisplayName_Returns403_WhenCallerIsGuest`
(`AuthEndpointTests.cs`), `REQ722_PostAvatar_Returns403_WhenCallerIsGuest`
(`AvatarEndpointTests.cs`); the avatar suite's prior guest-allowed 201 test
updated to match the reversed behavior.

Quality-review fix pass (same iteration): this diff's two new `if
(user.IsGuest) { return <403> }` sites became the 4th/5th near-identical
occurrence in the API (alongside `SuggestionEndpoints.cs`'s REQ-215 and
`IncidentEndpoints.cs`'s REQ-903 checks) — extracted into a shared
`backend/src/XGArcade.Api/Auth/GuestRejectionProblem.cs`
(`GuestRejectionResult.Problem` for the minimal-API/`IResult` sites,
`ControllerBase.GuestRejectionProblem` extension for the one MVC/
`IActionResult` site), per `docs/coding-guidelines.md`'s rule-of-three-not-
five. Each of the 4 call sites keeps its own title/detail wording; only
the `(title, detail) → 403 Problem` plumbing is shared. No response shape,
status code, or copy changed at any site, so none of the 4 files' existing
tests needed updates.

Frontend — `frontend/src/settings/SettingsScreen.tsx` (SCREEN-08)
restructured: the always-visible "Display name" (S-058) and "My avatar"
(S-182) sections are removed as standalone sections and merged into one
panel, toggled by a new pencil (`EditPencilIcon`) button
(`aria-label="Edit profile"`, `aria-expanded`) on S-184's profile header —
hidden entirely when `isGuest`, with a muted "Claim your account to edit
your name or avatar." hint shown in its place. Same underlying
forms/handlers/state (`handleDisplayNameSubmit`/`handleAvatarSubmit`)
untouched, only relocated behind a new `editPanelOpen` toggle.
Quality-review fix pass (same iteration): closing the panel previously
left `error`/`saved`/`avatarError`/`avatarSaved` untouched — only the two
submit handlers ever reset them — so "submit → close → reopen" re-showed
the previous submission's stale success/error banner with no new action
taken; fixed via a new `handleToggleEditPanel`, which clears both forms'
success/error state (and any selected-but-unsubmitted avatar file) only
when the panel transitions closed, not open. New tests:
`REQ714_SettingsScreen_TogglesEditPanel_OnPencilClick`,
`REQ714_SettingsScreen_EditButton_MeetsTouchTargetMin`,
`REQ714_SettingsScreen_HidesEditButton_WhenGuest`,
`REQ722_SettingsScreen_HidesEditButton_WhenGuest`,
`REQ714_SettingsScreen_ClearsSuccessMessage_OnPanelReopen`,
`REQ722_SettingsScreen_ClearsSuccessMessage_OnPanelReopen`. 720/720
frontend tests passing (verified in-sandbox with a real `npm run test`
run); `npx tsc -b` and `npm run lint` both clean. Backend built without a
local `dotnet` SDK in-sandbox — hand-traced against
`AuthEndpointTests.cs`'s/`AvatarEndpointTests.cs`'s existing patterns, not
locally run; a CI verification run is still needed before this is
considered fully done. `architecture-reviewer`: PASS, no ADR needed —
reuses the existing plain `IsGuest` gate pattern REQ-215/REQ-903 already
established, not a new structural mechanism. `quality-architect`: PASS
after the two fix passes above (403-check duplication extraction,
stale-panel-state bug) — separately suggested (non-blocking) extracting a
`ProfileEditPanel` sub-component from `SettingsScreen.tsx`; noted as a
future call, not done here. `docs/design-document.md`'s SCREEN-08 section
updated in the same iteration (v0.80 → v0.81) describing the pencil-icon
panel and guest gating.
`quality-architect`: PASS, after the fix pass above.

## Epic 26 — Supabase free-tier egress remediation (2026-08-25 incident)

The Supabase org backing this project (free tier) is over its 5GB/billing-
cycle egress quota (6.40GB used, 128%) and faces Fair Use Policy
restrictions from 2026-09-24 if it stays over. Storage buckets are
confirmed not the cause (Storage Size 0/1GB) — this is Postgres/API
egress from GitHub Actions CLI jobs. Root cause, confirmed via GitHub
Actions run history and source reading:
`PlayerCareerPrefetchService` (backs `prefetch-player-careers.yml`) had no
skip-already-processed shortcut, unlike every sibling bulk Wikidata job —
every dispatch unconditionally re-swept every seeded country and club
from scratch, including a full `GetPlayerAttributesAsync`/
`GetCareerStintsByPlayerIdsAsync` dedup read-back against Supabase
Postgres before writing anything. A player-pool purge on 2026-08-17 was
followed by 9 manual re-dispatches in ~36 hours (chasing transient
Wikidata/WDQS failures under the job's fail-loud-at-end contract), and one
successful run alone persisted 193,382 players / 527,252 stints — the
most likely explanation for the ~1.3GB single-day egress spike visible in
the Supabase usage dashboard around 2026-08-18. Staying on the free tier
is the highest priority right now; this is bug/reliability-hardening work
on already-shipped jobs, not new feature scope.

**S-186 · Stop repeated full-pool Wikidata sweeps + cache avatar images (REQ-110, REQ-722)**
Four fixes in one story/PR: (1) give `PlayerCareerPrefetchService` a
freshness-based skip using the `CountryDefinition`/
`ClubDefinition.PlayerPoolSweptAt` timestamps ADR-0078 already stamps —
mirroring `PlayerCacheWarmingService`'s existing
confirmed-low-from-sweep short-circuit; (2) add a GitHub Actions
`concurrency:` group (`cancel-in-progress: false`) to all 4 bulk Wikidata
workflows so a burst of manual re-dispatches can never stack overlapping
full sweeps against Supabase again; (3) narrow the dedup read-back
queries' column projection, if low-risk given shared callers — otherwise
skip and say so; (4a) add `Cache-Control`/`ETag` response headers to the
two avatar image-streaming endpoints (`AvatarEndpoints.cs`), which
currently stream Supabase Storage bytes through the backend with zero
caching, for a companion frontend fix (`PlayerAvatar.tsx`) to rely on.
*Accept:* a re-run of `prefetch-player-careers` against an already-swept
country/club calls neither `fetchPoolAsync` nor the dedup repositories
again (`REQ110_*` tests); the existing `PlayerPoolSweptAt` invalidation
contract (`purge-player-pool`, `StaleClubAttributeCleaner`) still forces a
real re-sweep after this change; all 4 workflow files gain a concurrency
group; both avatar image endpoints return a `private` `Cache-Control`
with a 1-day-plus `max-age` and an `ETag`, never `public` (both are
authorization-gated per request).
*Deps:* none — extends REQ-110's existing cache-warming/prefetch
machinery and REQ-722's existing avatar-serving endpoints, no new REQ.

*Built as (2026-08-25):* Fix #1 — `PlayerCareerPrefetchService.SweepAsync`
gained a `getSweptAt` check ahead of `fetchPoolAsync`: a row whose
`PlayerPoolSweptAt` is already non-null is skipped entirely (no live
Wikidata call, no `markSweptAsync` re-write, and — since
`GetPlayerAttributesAsync`/`GetCareerStintsByPlayerIdsAsync` only ever run
inside `SweepPoolAsync`, reached only after a live fetch — no dedup
read-back either). **Freshness-policy decision (flagged for ADR-0088):**
"ever successfully swept" is sufficient, no staleness window — matches
this data's own volatility (a Wikidata career history rarely changes
retroactively) and mirrors ADR-0078's own precedent for the sibling
`warm-grid-cache` job. The existing invalidation contract
(`purge-player-pool`'s `ExecuteUpdateAsync` reset, `StaleClubAttributeCleaner`)
is unchanged and still forces a real re-sweep — verified explicitly by a
new test that mutates `PlayerPoolSweptAt` back to `null` mid-test and
confirms a second `PrefetchAsync` call queries Wikidata again.
`PlayerCareerPrefetchResult` gained `CountriesSkipped`/`ClubsSkipped`
(defaulted, backward compatible), surfaced in `prefetch-player-careers`'s
own CLI summary line. Five new `REQ110_*` tests in
`PlayerCareerPrefetchServiceTests.cs` cover the skip itself (both country
and club), that a skip doesn't re-write the timestamp, that a null
`PlayerPoolSweptAt` is never skipped, and the invalidation round-trip.

Fix #2 — `concurrency: { group: ${{ github.workflow }}, cancel-in-progress:
false }` added to `prefetch-player-careers.yml`, `warm-grid-cache.yml`,
`import-player-name-index.yml`, `backfill-player-photos.yml`; the first
file's own stale "no skip-already-processed shortcut" incident comment
updated to describe fix #1.

Fix #3 — **skipped.** Both `IPlayerAttributeRepository
.GetPlayerAttributesAsync` and `IPlayerCareerStintRepository
.GetCareerStintsByPlayerIdsAsync` are shared with other callers
(`WikidataLookupService` for the former; `PathEligibilityService`,
`PathEndpoints`, `WikidataLookupService`, and
`PlayerCareerStintRefreshService` for the latter), so narrowing either
would mean adding new interface methods/DTOs rather than reshaping an
existing shared contract — real, non-trivial scope for a secondary
optimization. Fix #1 already eliminates the dominant cost (the dedup
read-back only runs at all for a genuinely new or re-invalidated
country/club now, not on every re-dispatch), which is what actually
matters for the free-tier goal; narrowing the projection for that
remaining, much smaller case was judged not worth the added surface
area and risk in this story. Left as a follow-up if egress is still a
concern after fix #1/#2 land.

Fix #4a — `Cache-Control: private, max-age=86400` plus an `ETag` added to
both `GET /users/me/avatar/{submissionId}/image` and `GET
/users/{userId}/avatar/image` via `Results.Stream`'s `entityTag`
parameter (also gets conditional-GET/304 handling for free) and a manual
`httpContext.Response.Headers.CacheControl` assignment. Both stay
`private` — never `public` — since both are authorization-gated per
request (a `submissionId` owned by a different player 404s; the userId
endpoint requires a valid bearer token), so a shared/CDN cache serving
either response to a different caller would be an authorization bypass.
The owner-only endpoint's `ETag` is the `submissionId` itself (permanently
immutable, per REQ-722/ADR-0087's replace-not-mutate model); the
userId-keyed endpoint's `ETag` is the *current* Approved submission's own
`Id`, since that URL's content can change when a newer avatar is approved.
Two new tests (`REQ722_AvatarImage_Get_SetsPrivateCacheControlAndETag`,
`REQ722_GetUserAvatarImage_SetsPrivateCacheControlAndETag`) in
`AvatarEndpointTests.cs`.

Testing: no local `dotnet` SDK available in-sandbox (`which dotnet`
confirmed absent) — all new/changed backend code was hand-traced against
existing test patterns and the InMemory-provider repository behavior, not
locally run; a CI verification run (`ci.yml` `workflow_dispatch`) is
needed before this is considered fully done. `docs/decisions/0088-*.md`
(the freshness-policy decision above) to be added by the orchestrator in
the docs-sync pass, not by this story's implementation itself.

**S-187 · Rotating bounded re-sweep + fix duplicate-stint artifact on end-date completion (REQ-110, REQ-1203)**
Follow-up to S-186, from a design discussion with the product owner
identifying two small, independent gaps ADR-0088's "ever swept, skip
forever" fix left behind. Two pieces in one story/PR:

Piece 1 — `PrefetchAsync` gains an optional `maxEntitiesToResweep`
parameter (`null` default preserves ADR-0088's exact unbounded-skip
behavior unchanged). A non-null N additionally re-sweeps up to N
already-swept entities (oldest `PlayerPoolSweptAt` first) on top of
every never-swept entity (always swept, uncapped) — so a player
transferring into an already-swept country's/club's pool eventually
gets noticed again, in small bounded batches, without reintroducing
the unbounded re-sweep cost ADR-0088 fixed. `SplitResweepBudget`
divides one top-level budget across `SweepCountriesAsync`'s/
`SweepClubsAsync`'s separate calls (ceiling half to countries — 49
seeded vs ~15 clubs — so N=2 gives the product owner's own stated
default of 1 country + 1 club per run). New weekly
`resweep-player-careers.yml` workflow (Sunday 05:15 UTC, staggered
after `warm-grid-cache.yml`'s 04:30 UTC slot) calls the same
`prefetch-player-careers` CLI verb with a small bounded argument
(default 2) — kept separate from `prefetch-player-careers.yml` since
a single workflow can't parameterize per-cron-entry inputs; that
file's own `workflow_dispatch` trigger is unchanged (still
unbounded, the explicit "sweep everything not-yet-done" escape
hatch). The CLI verb itself switches from S-112's "exact-match, extra
tokens silently fall through" shape to a "prefix-match, validate and
throw" shape for its own new optional argument.

Piece 2 — `PlayerCareerStintRefreshService.BuildNewStintsByPlayerId`
(shared by the per-target refresh and the bulk prefetch sweep,
including piece 1's new rotation) deduped a freshly-fetched stint
against stored rows on the full `(ClubName, StartYear, EndYear,
AppearanceCount)` tuple. When a player transfers away from a club,
Wikidata eventually fills in a previously-null `EndYear` on what was
stored as an ongoing stint — the next fetch's non-null `EndYear` no
longer matched the stored null, so it was inserted as a SECOND row: a
duplicate-looking entry in xG Path's clue-reveal timeline for one
real stint. Narrowed the matching key to `(PlayerId, ClubName,
StartYear)` only — a match on that narrower key now either no-ops
(identical) or queues a completion (existing row's `EndYear`/
`AppearanceCount` overwritten with the fetched values) via a new
`IPlayerCareerStintRepository.UpdateCareerStintCompletionsAsync`,
rather than inserting a new row. Deliberately narrow: only completes
an already-correct row's own end-of-stint fields, never revisits a
stored `StartYear`/`ClubName` — a scoped, accepted exception to this
file's "additive only, never wipe-and-replace" contract (referenced
from ADR-0054's Consequences section), not a reversal of it.
`WikidataLookupService.PersistCareerStintsAsync` (xG Grid's own
guess-time byproduct write path, not routed through
`BuildNewStintsByPlayerId`) was originally left untouched by this
story's first commit — see the "Built as" follow-up note below for why
that turned out to be an undocumented gap, closed in a same-story
follow-up commit rather than left as-is.

*Accept:* piece 1 — `maxEntitiesToResweep: null` behaves exactly like
before this story (regression coverage); a never-swept entity is
always included regardless of budget size; with N already-swept
entities eligible, only the N oldest-`PlayerPoolSweptAt` ones get
re-swept (live Wikidata call + dedup read-back proven via persisted
players), the rest stay skipped; N=2 splits into 1 country + 1 club.
Piece 2 — an existing stored stint with `EndYear = null` gets its
`EndYear` updated in place (not duplicated) when a fetch returns the
same club/start-year with a real `EndYear`; a genuinely new club/
start-year still inserts as a new row; an identical re-fetch remains
a no-op; `UpdateCareerStintCompletionsAsync` never touches
`SequenceOrder` and silently no-ops on a missing `stintId`.

*Deps:* extends S-186/ADR-0088's existing freshness-skip and
`PlayerCareerStintRefreshService`/`PlayerCareerPrefetchService`'s
existing reconciliation logic — no new REQ; uses REQ-110 (piece 1)
and REQ-1203 (piece 2, career-stint clue-reveal correctness).

*Built as (2026-08-29):* as described above. Two commits: piece 1
(`PlayerCareerPrefetchService`'s `SplitResweepBudget`/`SweepAsync`
changes, `IPlayerCareerPrefetchService`/CLI dispatcher/new workflow,
`PlayerCareerPrefetchServiceTests.cs`), then piece 2
(`PlayerCareerStintRefreshService.BuildNewStintsByPlayerId`'s
narrowed key + `CareerStintReconciliation` return shape,
`IPlayerCareerStintRepository`/`PlayerCareerStintRepository`'s new
`UpdateCareerStintCompletionsAsync`, `PlayerCareerStintRepositoryTests.cs`,
`PlayerCareerStintRefreshServiceTests.cs`, and a small comment
correction in `DuplicateCareerStintCleaner.cs` — its own full-tuple
match is unaffected, now described as strictly more conservative than
the live write path's narrower key).

*Built as, follow-up (2026-08-29, commit `85924af`):* `architecture-reviewer`'s
full-diff review flagged piece 2 as an undocumented gap — it fixed the
duplicate-stint bug at 2 of the 3 reconciliation call sites
(`PlayerCareerStintRefreshService.BuildNewStintsByPlayerId` and, via it,
`PlayerCareerPrefetchService`) but left `WikidataLookupService.PersistCareerStintsAsync`
(xG Grid's own REQ-103 generation-time / REQ-211 guess-time byproduct
writer, and the most frequently invoked of the three) with its own
stale full-tuple dedup. Closed by extracting the shared per-candidate
no-op/insert/complete decision into a new internal
`CareerStintReconciler.Reconcile` primitive (`CareerStintReconciler.cs`)
used by all three call sites — the three callers' differing input
shapes (`WikidataCareerStintEntry` vs. `CareerStintQualifiers`) meant
only this narrower per-candidate decision could be shared, not the
whole reconciliation loop. Same commit also addressed a
`quality-architect` finding by adding a direct unit test on
`BuildNewStintsByPlayerId` itself,
`REQ1203_BuildNewStintsByPlayerId_IdenticalRefetchInput_ReturnsTrueNoOp`,
proving an identical re-fetch queues zero writes (a true no-op), not
just an unchanged end state after a write — requiring a new
`InternalsVisibleTo` grant for `XGArcade.DataSync.Tests`. Trivial
hardening in the same commit: `PlayerCareerPrefetchService`'s resweep
selection also requires a non-null `WikidataQid`, matching the main
loop's own skip.

Testing: no local `dotnet` SDK available in-sandbox — all new/changed
backend code was hand-traced against existing test patterns and the
InMemory-provider repository behavior, not locally run; a CI
verification run (`ci.yml` `workflow_dispatch`) is needed before this
is considered fully done. Both pieces got their own ADR in the
orchestrator's docs-sync pass, not written by this story's
implementation itself: ADR-0090 (piece 1, rotating bounded re-sweep)
and ADR-0091 (piece 2, career-stint completion's narrow exception to
the "additive only" contract, covering all three reconciliation call
sites after the follow-up commit above).

**S-188 · Date-filtered recent-transfer sweep, a third freshness mechanism (REQ-110)**
Follow-up to S-186/S-187: ADR-0090's rotation is deliberately slow (a
full cycle is on the order of a season) — fine for general drift, but
useless for reflecting a transfer that just happened around a
transfer-window deadline day. Adds a cheap, targeted, DATE-FILTERED
SPARQL query per seeded club instead of waiting out the rotation: two
new query builders, `BuildRecentClubArrivalsQuery` (`pq:P580` "joined
since" `FILTER`, mandatory bind) and `BuildRecentClubDeparturesQuery`
(`pq:P582` "departure recorded since" `FILTER`, mandatory bind;
`?startTime` OPTIONAL), both using the full `p:P54`/`ps:P54` statement
path (never the truthy `wdt:P54` shortcut — the `pq:P580`/`pq:P582`
qualifiers this query reads don't even exist under it). WDQS filters by
date server-side, so the result set is naturally bounded by real
transfer activity per club, not squad size — cheap even run often.

New `IWikidataClient.QueryRecentClubTransfersAsync(clubQid, clubName,
sinceUtc, ct)` runs both queries per club and merges the results into
one `RecentClubTransferLookupResult` (`StintsByQid` +
`PlayerNamesByQid` — the latter captured here since, unlike every other
career-stint query, there is no earlier pool-query pass that already
knows an arriving player's name). `clubName` is caller-supplied
(`ClubDefinition.Name`), never derived from a Wikidata label — this
query never projects `?club`/`?clubLabel` at all, since the caller
already knows exactly which club it's iterating.

New `RecentTransferSweepService`/`IRecentTransferSweepService`, one per
seeded `ClubDefinition`: an arrival get-or-creates the `Player`
(`GetOrCreatePlayersByWikidataQidAsync`, same precedent as
`PlayerCareerPrefetchService.FetchAndPersistBatchAsync`) and reconciles
via `PlayerCareerStintRefreshService.BuildNewStintsByPlayerId` — reused
verbatim, not reimplemented, so an arrival that matches no existing
`(ClubName, StartYear)` row inserts, and a departure that matches an
existing row completes it in place via `CareerStintReconciler.Reconcile`
(`UpdateCareerStintCompletionsAsync`, ADR-0091), never duplicating.
**Deliberate scope boundary:** this service only ever writes
`PlayerCareerStint` (xG Path's own byproduct data) — it does NOT write
`PlayerAttribute`/`PlayerData` (xG Grid's own guess-correctness answer
key, ADR-0007) and does NOT touch
`CountryDefinition`/`ClubDefinition.PlayerPoolSweptAt` at all (writing
that column here would incorrectly tell ADR-0088's skip-forever check
that this club's FULL pool was re-verified, when only a narrow
recent-activity slice actually was). A freshly-transferred player
therefore becomes visible to xG Path sooner, but does not become a
valid xG Grid guess answer for that club any sooner than ADR-0090's own
rotation (or a full `prefetch-player-careers` run) would make them —
flagged here for the product owner/orchestrator as a candidate
follow-up, not addressed by this story.

New CLI verb `sweep-recent-transfers [lookbackDays]` (optional single
argument, default 30 days — a full typical transfer window's worth of
overlap for an operator dispatching once around a deadline day, per
the "prefix-match, validate and throw" shape ADR-0090/S-187 already
moved `prefetch-player-careers` onto). New workflow
`sweep-recent-transfers.yml`, `workflow_dispatch` ONLY — deliberately
no cron, even though the per-run cost is small (~15 clubs x 2 queries =
~30 small WDQS-filtered SPARQL queries, plus a handful of Postgres
reads/writes for whatever a real transfer window actually produced):
this is a brand-new, unproven query shape (never run against the real
`query.wikidata.org` endpoint from this sandbox), following
`prefetch-player-careers.yml`'s own bootstrapping precedent (manual-only
until a real run's cost/behavior is confirmed, cron added later once
proven); and the underlying product need is inherently event-driven
(around a ~4-6-week transfer window, twice a year), so a 365-day/year
cron would mostly find nothing — unnecessary operational surface for no
added freshness benefit outside those windows. Standard
`concurrency: { group: ${{ github.workflow }}, cancel-in-progress: false }`
guard, same as every other bulk Wikidata workflow.

**Cutoff strategy:** `lookbackDays` (a CLI-supplied window, cutoff =
`DateTime.UtcNow.AddDays(-lookbackDays)`) was chosen over "since this
club's own `PlayerPoolSweptAt`" — the latter would tie this
mechanism's freshness window to ADR-0090's own rotation state (which
can be up to ~15 weeks stale for a club not yet due), making the
result set's size and the mechanism's actual behavior unpredictable
and non-obvious to reason about; a fixed, operator-chosen day count is
simpler to reason about and more directly useful for someone
dispatching this specifically because a deadline day is approaching,
regardless of how recently the rotation last touched any given club.

*Accept:* the arrivals/departures query builders' generated SPARQL
(full statement path, correct `FILTER` clause per direction, no
`ORDER BY`/`LIMIT`/`OFFSET`) is covered byte-for-byte; an arrival for a
brand-new player creates the `Player` row and inserts a stint; a
departure completing an existing ongoing stint updates it in place
(never duplicates) via the shared `CareerStintReconciler` machinery; an
identical re-fetch is a true no-op; `lookbackDays`/club name/QID are
threaded correctly per seeded club; one club's failure doesn't stop the
rest but still fails the run at the end (idempotent re-run); this
service never writes `PlayerPoolSweptAt`.

*Deps:* reuses S-187/ADR-0091's `CareerStintReconciler`/
`BuildNewStintsByPlayerId` machinery and `IPlayerCareerStintRepository`
unchanged — no new REQ; uses REQ-110.

*Built as (2026-08-29):* as described above —
`SparqlQueryBuilders.BuildRecentClubArrivalsQuery`/
`BuildRecentClubDeparturesQuery`,
`SparqlResponseParsers.ParseRecentClubTransferBindings` (+
`RecentClubTransferParseResult`), `IWikidataClient`/`WikidataClient
.QueryRecentClubTransfersAsync` (+ new `RecentClubTransferLookupResult`
public record), `IRecentTransferSweepService`/`RecentTransferSweepService`,
`CliVerbDispatcher.HandleSweepRecentTransfersAsync`,
`sweep-recent-transfers.yml`. All four `IWikidataClient` fakes
(`XGArcade.DataSync.Tests`, `XGArcade.Games.XGGrid.Tests`,
`AdminSuggestionEndpointTests.cs`, `AdminEndpointTests.cs`) updated with
the new interface member. Tests: `WikidataClientTests.cs`
(`S188_QueryRecentClubTransfersAsync_*`, 12 cases covering SPARQL
shape, response parsing/merging, and the error contract) and new
`RecentTransferSweepServiceTests.cs` (10 cases covering arrival/
departure/no-op reconciliation, cutoff threading, partial failure, and
the `PlayerPoolSweptAt` scope boundary).

Testing: no local `dotnet` SDK available in-sandbox — all new/changed
backend code was hand-traced against existing test patterns and the
InMemory-provider repository behavior, not locally run; a CI
verification run (`ci.yml` `workflow_dispatch`) is needed before this
is considered fully done. ADR-0092 (this story's cadence/cutoff
decisions above) to be added by the orchestrator in the docs-sync pass,
not written by this story's implementation itself.

*Built as, follow-up (2026-08-29, commit `0bcc10d`):* `quality-architect`
flagged the 22 new test methods across `RecentTransferSweepServiceTests.cs`
(10 cases) and `WikidataClientTests.cs` (12 cases, `S188_`-prefixed) as
violating `docs/coding-guidelines.md`'s `REQ###_MethodUnderTest_ExpectedOutcome`
naming convention — a pure rename to `REQ110_`, matching the established
sibling precedent (`REQ110_PrefetchAsync_*`), no test body/assertion/
behavior changed. Both `architecture-reviewer` and `quality-architect`
returned PASS on the full diff. See ADR-0092 for the Grid-vs-Path
freshness-asymmetry trade-off this story deliberately leaves open, rather
than re-explaining it here.

**S-189 · Close the Grid-vs-Path freshness asymmetry: recent-transfer sweep also writes PlayerAttribute (REQ-110)**
Follow-up to S-188/ADR-0092, explicitly requested by the product owner.
ADR-0092 deliberately left `RecentTransferSweepService` writing only
`PlayerCareerStint` (xG Path), never `PlayerAttribute`/`PlayerData` (xG
Grid's answer key), flagging a possible `ConfirmedLowMatchPair`/
`PairLookupFailure` invalidation risk as the reason. A closer trace this
story found the real risk much smaller than ADR-0092's original, coarser
read: `GridGenerationService`'s candidate-validity check
(`CountPlayersWithBothAttributesAsync`) and guess-correctness checking
(`PlayerOverrideRepository.HasEffectiveAttributeAsync`) both read
`PlayerAttribute`/`PlayerOverride` live, never `ConfirmedLowMatchPair`/
`PairLookupFailure` — so a fresh `PlayerAttribute` write is picked up
correctly and immediately by both. `ConfirmedLowMatchPair` itself is
consulted only inside `PlayerCacheWarmingService.WarmAsync`'s own
maintenance heuristic, and only as a secondary check after
`cachedCount >= MinValidAnswers` is already checked first — so a stale
`ConfirmedLowMatchPair` row is a missed opportunity for `warm-grid-cache`
to discover more matches sooner, never a live wrong-answer risk.
**Correction to ADR-0092's own trace, found while re-verifying it for this
story:** `PairLookupFailure` is not maintenance-only the way
`ConfirmedLowMatchPair` is — `GridLiveLookupDispatcher.TryRefreshCellAsync`
(REQ-211's guess-time live-lookup fallback) also consults
`IsPersistentTechnicalFailureAsync` directly, a real live path ADR-0092's
"never consult...at all" framing didn't account for. Clearing a stale
`PairLookupFailure` row there is still never a correctness risk (ADR-0046:
a live-lookup failure always fails closed as "unknown," consuming no guess
attempt, never becomes a wrong "incorrect" verdict) — but it can mean a
guess against that pair pays a live Wikidata round trip (and its ~28s
timeout, if the underlying failure was genuinely structural) that an
un-cleared marker would have short-circuited, a latency trade-off, not a
correctness one, and self-healing (the next `PlayerCacheWarmingService` run
that still fails re-records the marker). This nuance belongs in ADR-0093's
own text, not smoothed over.

Two pieces:

Piece 1 — extends `RecentTransferSweepService`'s existing arrival-persistence
path (no second Wikidata query, no second sweep loop) to also write a
`PlayerAttribute`+`PlayerData` row for `(player, "club", clubName)` on a
genuinely new arrival, mirroring `PlayerCareerPrefetchService
.FetchAndPersistBatchAsync`'s REQ-110-follow-up attribute-write shape
exactly: same dedup pattern (`IPlayerAttributeRepository
.GetPlayerAttributesAsync` queried once per distinct club value, a
`HashSet`-backed "already has it" filter), same `WikidataDataSource`/
`VerifiedConfidence` ("wikidata"/"verified") constants, a `PlayerData` row
paired with each new `PlayerAttribute`, and reuse of
`WikidataLookupService.ClubAttributeType` (already `internal` for exactly
this kind of reuse) rather than a second copy of the "club" string. Sourced
from `reconciliation.NewStintsByPlayerId` — `BuildNewStintsByPlayerId`'s own
arrival-only output — so a departure (which only ever appears in
`CompletionsByStintId`) naturally contributes zero attribute writes with no
extra branching; Grid's "ever played for this club" answer semantics mean a
player who left is still correctly a valid answer forever, so nothing about
departures changes.

Piece 2 — targeted invalidation: when a new `PlayerAttribute` is written for
player P against `(club, newClubName)`, deletes any `ConfirmedLowMatchPair`/
`PairLookupFailure` row pairing `newClubName` against every OTHER attribute
value P already has (queried via the existing
`IPlayerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync`, no new
lookup method needed) — bounded by however many attributes one player has (a
handful at most), not a club-wide sweep. `StaleClubAttributeCleaner` (the
only existing precedent) only supports "delete every row involving this
club," too broad for this narrower per-pair need, and lives in
`XGArcade.Data.Seeding` querying `XGArcadeDbContext` directly rather than
through `IPlayerDataQualityRepository` — so a new, narrower repository
method was added: `IPlayerDataQualityRepository.ClearMatchPairAsync`, which
checks BOTH possible stored orderings (unlike every sibling method on that
interface, which relies on a single fixed ordering because their only
caller, `PlayerCacheWarmingService.SweepPairsAsync`, always passes one
stable ordering per sweep type) — this caller has no way to know which side
a Club x Club pair (whose order depends on `ClubDefinition`'s seed-list
position) was originally recorded under.

*Accept:* an arrival creates BOTH the `PlayerCareerStint` (S-188's existing
behavior, unchanged) AND a new `PlayerAttribute`+`PlayerData` row; a
duplicate arrival (player already has the attribute) writes no second
`PlayerAttribute`/`PlayerData` row; the targeted invalidation deletes only
the specific `ConfirmedLowMatchPair`/`PairLookupFailure` rows pairing the
new club against the player's OTHER existing attributes, leaving unrelated
pairs (a different player; a different club/nationality combination not
involving this player) untouched; a departure never writes or removes a
`PlayerAttribute`; `RecentTransferSweepService` still never touches
`PlayerPoolSweptAt` (S-188's own boundary, unchanged).

*Deps:* extends S-188/ADR-0092's `RecentTransferSweepService` and reuses
`IPlayerAttributeRepository.GetPlayerAttributesAsync`/
`GetPlayerAttributesByPlayerIdsAsync`/`AddPlayerAttributesBatchAsync`,
`IPlayerDataRepository.AddPlayerDataBatchAsync`, and
`WikidataLookupService.ClubAttributeType` unchanged — no new REQ; uses
REQ-110 (same accepted imperfect-fit tag ADR-0092's own "REQ-110 tag"
section already flagged for this whole story lineage).

*Built as (2026-08-29):* as described above —
`RecentTransferSweepService.PersistNewArrivalAttributesAsync`/
`PersistClubAttributesForArrivalsAsync` (new), `RecentTransferSweepResult`
gained `AttributesAdded`, `IPlayerDataQualityRepository`/
`PlayerDataQualityRepository` gained `ClearMatchPairAsync`,
`CliVerbDispatcher.HandleSweepRecentTransfersAsync` wires the three new
repository dependencies (`IPlayerAttributeRepository`/`IPlayerDataRepository`/
`IPlayerDataQualityRepository`, all already DI-registered for other call
sites) and its CLI summary line, `IRecentTransferSweepService.cs`/
`sweep-recent-transfers.yml` doc comments updated to describe the new
Grid-answer-key-freshness scope (no verb/class/workflow rename — "recent
transfer sweep" already reads generically enough to cover both). Four new
tests in `RecentTransferSweepServiceTests.cs` (arrival writes the paired
attribute; duplicate-attribute dedup; departure-alone never writes/removes
an attribute; targeted invalidation clears the exact pair and leaves
unrelated pairs untouched) plus six new direct `ClearMatchPairAsync` tests
in `PlayerDataQualityRepositoryTests.cs` (both stored orderings, both
tables cleared in one call, no-op when nothing matches, unrelated pairs
survive).

Testing: no local `dotnet` SDK available in-sandbox — all new/changed
backend code was hand-traced against existing test patterns and the
InMemory-provider repository behavior, not locally run; a CI verification
run (`ci.yml` `workflow_dispatch`) is needed before this is considered
fully done. ADR-0093 (correcting ADR-0092's own stated caution per the
more precise trace above) to be added by the orchestrator in the docs-sync
pass, not written by this story's implementation itself.

*Built as, follow-up (2026-08-29, commit `df62417`):* `quality-architect`
flagged `RecentTransferSweepService.cs`'s comment as falsely claiming its
`WikidataDataSource`/`VerifiedConfidence` constants reused
`WikidataLookupService`'s own definitions, when they were actually a third
independent literal-string copy (`WikidataLookupService`'s
`WikidataSource`/`VerifiedConfidence` were `private`, so no other file
could reference them). Fixed by making both `internal` on
`WikidataLookupService` (same pattern already used for
`ClubAttributeType`/`NationalityAttributeType`) and having both
`RecentTransferSweepService` and `PlayerCareerPrefetchService` reference
them directly instead of declaring their own copies — a mechanical,
behavior-preserving consolidation (values were already identical literal
strings everywhere), no test changes needed. Both `architecture-reviewer`
and `quality-architect` returned PASS on the full diff after this fix. See
`docs/decisions/0093-recent-transfer-sweep-writes-playerattribute.md` for
the Grid-vs-Path freshness-asymmetry correction this story makes to
ADR-0092, rather than re-explaining it here.

**S-190 · xG Predict: requirements, data-source/scoring ADRs, and module scaffold (REQ-1301-1305)**
New game, requested directly by the product owner (not a Tier 1/2 pull
without a trigger — this is the platform's third game, scoped from a
product conversation, same category of deliberate decision as S-031/S-063/
S-070/S-089/S-097/S-098's own precedent for pulling a feature forward by
explicit request rather than a trigger firing).

xG Predict: a match-outcome prediction game. A round is 5 matches drawn
from an upcoming Premier League gameweek, selected for the tightest
kickoff-time clustering available; players predict each match's final
score before kickoff; the whole round locks at the first of the 5
matches' kickoff (closes the "predict the rest after seeing an early
result" exploit); each match grades three independent components (1X2
outcome, home goals, away goals) once its real result is confirmed,
asynchronously, sometime after the round has already locked — a genuinely
new lifecycle shape distinct from xG Grid/xG Path's round-close scoring.

*Accept:* REQ-1301-1305 exist in `docs/requirements-document.md` §4.14 in
Given/When/Then form; ADR-0094 (API-Football fixtures/results as the data
source — free tier confirmed sufficient, new precondition independent of
xG Grid's own Tier 1 API-Football trigger, ToS scoping note distinct from
ADR-0008's narrower player-data-caching review) and ADR-0095 (xG Predict's
conventional higher-is-better scoring, a named, product-confirmed
exception to ADR-0021's platform-wide golf-style convention — the product
owner was asked directly and chose to break consistency here since that's
how the prediction genre already works) both exist; `XGArcade.Games.XGPredict`
exists as a real project, registered as `IGameModule` under GameKey
`"xg-predict"`, every method stubbed (`NotImplementedException` for
REQ-1301-1305's not-yet-built logic, `NotSupportedException`/`null` for
REQ-215/216 which don't apply, mirroring `XGPathGameModule`'s own
precedents for both) — no round generation, prediction submission, or
scoring is actually implemented by this story.

*Deps:* none — this is the first story for a new game, same starting shape
as xG Path's own S-079/S-080.

*Explicitly out of scope, queued as follow-up stories, not bundled here*
(same "one story per session/PR" discipline this backlog already follows
throughout): the round/match/prediction entity shape (`XGPredictGameModule`'s
own doc comment and COMP-15's architecture-document.md row flag this as
needing its own ADR, mirroring ADR-0045's xG Path entity-shape ADR, before
`GenerateInstanceAsync` can be implemented — deliberately not decided by
this story); the API-Football fixtures client itself (ADR-0094 describes
it, `DataSync.Clients` doesn't have it yet); `IScoringStrategy`'s new
`LowerIsBetter` member and the xG Predict scoring strategy (ADR-0095);
`LeaderboardService`'s three `OrderBy` call sites migrating to per-`GameKey`
sort direction (ADR-0095); round scheduling config for `"xg-predict"`
(mirrors ADR-0051's per-`GameKey` resolver, not yet registered); frontend
work; the postponed/abandoned-match voiding default (REQ-1305, proposed
but not yet confirmed by the product owner — §7).

*Built as (2026-08-30):* as described above. `requirements-writer` drafted
REQ-1301-1305 (and, after the scoring-direction question was put to the
product owner directly mid-session, revised REQ-1304 from an initial
golf-style translation to the confirmed higher-is-better version), the
orchestrating session wrote ADR-0094/ADR-0095 directly plus a partial-
supersede cross-reference on ADR-0021 and a status note on REQ-404
(REQ text itself not rewritten in place, per this document's ID-stability
convention), and `game-scaffolder` built the module/project/DI-registration
scaffold and COMP-15's architecture-document.md entry, flagging the
entity-shape gap back rather than inventing one.

Testing: no local `dotnet` SDK available in-sandbox — `game-scaffolder`
verified the new/changed C# files by hand (usings, types, `IGameModule`
signatures) and the `.sln` file's structural integrity programmatically,
but could not run `dotnet build`/`dotnet test` locally; a CI verification
run (`ci.yml` `workflow_dispatch`) is needed before this is considered
fully done, same recurring constraint as every other recent backend story
in this file.

**S-191 · API-Football fixtures/results client for xG Predict (REQ-1301, REQ-1305, ADR-0094)**
A new isolated client in `DataSync.Clients` (COMP-07), `IApiFootballClient`/
`ApiFootballClient`, two capabilities — fetch an upcoming Premier League
gameweek's full fixture list (`GetUpcomingGameweekFixturesAsync`, REQ-1301),
and look up a specific fixture's current status/final score
(`GetFixtureResultAsync`, REQ-1305). API key read from configuration/
environment, never committed (`ApiFootballApiKey`, mirroring
`GitHubIncidentReportToken`'s nullable/fail-closed-per-call shape rather
than a startup throw). No `ExternalApiUsage` budget-gating (ADR-0094 says
this game's usage doesn't need it). Client only — explicitly no round
generation, no 5-match tightest-clustering selection, no prediction
submission, no grading logic; those remain separate follow-up stories.

*Accept:* `IApiFootballClient` exists in `DataSync.Clients`, isolated the
same way `WikidataClient` already is (no other component calls
API-Football directly — verified by `architecture-reviewer`); a matching
`.Tests` project (added to the existing `XGArcade.DataSync.Tests` project,
not a new csproj) with unit tests against `FakeHttpMessageHandler`,
mirroring `WikidataClientTests.cs`'s/`GitHubIssueClientTests.cs`'s pattern.

*Deps:* S-190 (COMP-15 module scaffold; this story doesn't touch it).

*Explicitly out of scope, queued as follow-up:* round generation/5-match
selection (REQ-1301's remaining half), prediction submission (REQ-1302),
round lock (REQ-1303), scoring (REQ-1304, needs ADR-0095's
`IScoringStrategy` work too), grading job/trigger (REQ-1305's remaining
half — "what triggers the grading process" is still an open architecture/
implementation decision per REQ-1305's own text), frontend work.

*Built as (2026-08-30):* `backend-implementer` built the client — the
two-HTTP-call flow for the fixture list (`GET fixtures/rounds?...
current=true` then `GET fixtures?...round=...`), the throw-on-technical-
failure contract (never swallow — REQ-1301's caller needs to distinguish
"API unreachable" from "genuinely fewer than 5 fixtures"), the
status-code-to-outcome mapping for `GetFixtureResultAsync` (`FT`/`AET`/
`PEN`/`AWD`/`WO`→Finished, `PST`/`CANC`/`ABD`→PostponedOrAbandoned,
everything else→NotYetConfirmed), and that the API-Football v3 JSON
schema/status-code list is drawn from documentation/training knowledge,
not a live fetch (this sandbox has no egress to api-football.com — same
posture ADR-0094 itself already took) — flagged explicitly in code
comments as unverified, needing a human check before real reliance, same
convention as this repo's unverified-QID entries elsewhere. Note
`architecture-reviewer` and `quality-architect` both returned PASS on the
diff, with two non-blocking, deliberately-deferred test-coverage gaps
noted for a future pass if anyone picks them up (an untested
`FormatException` branch in date parsing, and an untested blank/missing-
status-code branch in `GetFixtureResultAsync`) and one non-blocking
test-architecture note (a second verbatim copy of a network-failure fake
handler across two test projects — not yet a third occurrence, so not
extracted per this repo's rule-of-three convention).

Testing: no local `dotnet` SDK available in-sandbox — hand-traced against
`GitHubIssueClient`/`GitHubIssueClientTests.cs` and `WikidataClient`/
`WikidataClientTests.cs`, and against `FakeHttpMessageHandler`'s actual
constructor/factory signatures (confirmed to match, not guessed) by both
the implementer and the quality-architect review pass; a CI verification
run (`ci.yml` `workflow_dispatch`) is needed before this is considered
fully done, same recurring constraint as every other recent backend story
in this file — the orchestrating session will trigger this next.

**S-192 · xG Predict: round generation + prediction submission/lock (REQ-1301/1302/1303, ADR-0096)**
Direct continuation of S-190/S-191, run through `/orchestrate` end to end
(intake → scope check → ADR → delegation → quality gate → doc sync →
CI verification). Closes the entity-shape gap S-190 deliberately flagged
back rather than invented, and implements the two `IGameModule` methods
S-191 explicitly queued as follow-up: `GenerateInstanceAsync` (REQ-1301 —
select 5 matches from an upcoming gameweek via S-191's `IApiFootballClient`,
preferring the tightest kickoff-time clustering) and `ScoreSubmissionAsync`
(REQ-1302/1303 — store/update a two-integer prediction per match, reject
after the whole-round lock at the first match's kickoff). Deliberately
does NOT implement REQ-1304 (scoring — needs ADR-0095's `IScoringStrategy`
work too) or REQ-1305's grading job, and deliberately does NOT wire
`"xg-predict"` into `InternalRoundEndpoints`'s gameKey switch,
`GuessSubmissionService`, or any `RoundSchedulingOptions`/`IScoringStrategy`
registration — same "flag the follow-up, don't quietly pull it forward"
discipline S-191 itself used, mirroring ADR-0051's own precedent for
deferred scheduling-config wiring.

*Accept:* ADR-0096 (`docs/decisions/0096-xg-predict-entity-shape-and-submission-boundary.md`)
exists and decides the round/match/prediction entity shape, following
ADR-0045's (xG Path's) precedent; `XGPredictGameModule.GenerateInstanceAsync`/
`ScoreSubmissionAsync`/`GetCellIdsAsync` are real, tested implementations
(`GetMaxAttemptsForCellAsync` stays a stub — not this story's decision to
make); `architecture-reviewer` and `quality-architect` both ran clean after
one fix round; `docs/requirements-document.md` §4.14 and
`docs/architecture-document.md`'s COMP-15 row/§6.11 reflect what's actually
built vs. still design-only (REQ-1304/1305/1306 unaffected, still
design-only).

*Deps:* S-190 (COMP-15 module scaffold), S-191 (`IApiFootballClient`).

*Explicitly out of scope, queued as follow-up:* REQ-1304 (scoring, needs
ADR-0095's `IScoringStrategy` work), REQ-1305's grading job/trigger,
REQ-1306 (explicit confirm-and-lock action), the real HTTP submission
endpoint and its wiring into `InternalRoundEndpoints`/`GuessSubmissionService`
(architecture-document.md §6.11 itself still flags the endpoint shape as an
open question — reuse the existing guess endpoint with a new submission
variant, or a dedicated `xg-predict`-only endpoint — not decided by this
story), `RoundSchedulingOptions`/`IScoringStrategy` registration for
`"xg-predict"`, frontend work.

*Built as (2026-08-30):* the orchestrating session wrote ADR-0096 directly
(same precedent as ADR-0094/ADR-0095's own S-190 authorship), deciding:
new entities `PredictTemplate`/`PredictInstance`/`PredictMatch`/
`PredictMatchPrediction` (the last a separate top-level table, not owned by
`PredictMatch`, since predictions accumulate per-user over time the same
reason `Guess` is a top-level table rather than an owned collection of
`Round`); a new Core-owned `PredictionSubmission` DTO alongside
`GuessSubmission`/`ScoreResult`; and an explicitly-flagged, deliberate
compromise on `ScoreSubmissionAsync`'s return contract (`ScoreResult
{ IsCorrect = false }` on a successful store means "not yet graded," never
"wrong" — left for the future submission-endpoint story to resolve
properly, not solved here). `backend-implementer` built the entities,
hand-written migration (`20260830120000_AddPredictInstance`, +
`.Designer.cs`, `XGArcadeDbContextModelSnapshot.cs` updated to match —
same no-`dotnet`-SDK hand-verification constraint as every other recent
backend story), `IPredictInstanceRepository`/`PredictInstanceRepository`,
the module implementation (sliding-window tightest-kickoff-clustering
selection — the minimum-span k-subset of a sorted sequence is always
contiguous, so this beats enumerating every C(n,5) subset), DI
registration, and unit tests covering REQ-1301/1302/1303's own "Test
level" acceptance criteria in full, including REQ-1303's specific
exploit-prevention case (a match whose own kickoff hasn't happened yet is
still rejected once the round-level lock from the first match's kickoff
has passed) — called out by `quality-architect` as "the highest-value test
in the diff and it's done right."

Quality gate (`architecture-reviewer` + `quality-architect`, run in
parallel) found two real, non-blocking-but-worth-fixing issues on the
first pass, both fixed in a follow-up commit: (1) `PredictMatchPrediction`'s
timestamp field was named `CreatedAt` but silently overwritten on every
resubmission, misleadingly implying `Guess.CreatedAt`'s write-once
semantics — renamed to `SubmittedAt`; (2) `PredictScoringException`
derived directly from `Exception` (a comment incorrectly attributed this
to ADR-0096, which never actually decided it) and conflated two different
failure modes — split into `PredictScoringException` (now correctly
derives from `Core.Games.GameEntityNotFoundException`, matching
`PathScoringException`'s/`GuessScoringException`'s actual precedent, for
the two "not found" cases) and a new `PredictInvalidSubmissionException`
for the negative-goal-count validation case. ADR-0096 was amended the same
day to make this exception-hierarchy decision explicit rather than leaving
it an unremarked implementation detail.

Testing: no local `dotnet` SDK available in-sandbox — hand-verified by the
implementer (brace/paren balance, migration/snapshot byte-identity,
cross-referenced signatures) and by both quality-gate reviewers reading
the actual diff; a CI verification run (`ci.yml` `workflow_dispatch`) is
needed before this is considered fully done — the orchestrating session
runs this next, same recurring constraint as S-191 and every other recent
backend story in this file.

**S-193 · xG Predict scoring strategy + per-`GameKey` leaderboard sort direction (REQ-1304, ADR-0095)**
Direct continuation of S-190/S-192, closing the `IScoringStrategy` gap
ADR-0095 (S-190) and S-192 both explicitly deferred. Gives
`IScoringStrategy` a `LowerIsBetter` member (`true`, unchanged, for
`UniquenessScoringStrategy`/`ClueEfficiencyScoringStrategy`) and adds
`XGPredictScoringStrategy` (`LowerIsBetter = false` — ADR-0095's one named
exception), registered against `"xg-predict"` in `ServiceRegistration.cs`.
Migrates `LeaderboardService`'s three plain-total `OrderBy(TotalPoints)`
scopes (`GetActiveRoundLeaderboardAsync`/`GetClosedRoundLeaderboardAsync`/
`GetWindowedLeaderboardAsync`) to resolve ascending/descending per
`GameKey` via the existing `IScoringStrategyResolver`, exactly the
mechanism ADR-0095 Decision §3 specified.

*Accept:* `IScoringStrategy.LowerIsBetter` exists and is `true` for both
existing strategies; `XGPredictScoringStrategy` exists, `LowerIsBetter`
is `false`, and `ScorePrediction` implements REQ-1304's three independent
components (outcome/home-goals/away-goals, each independently awarding
`ScoringRules.PredictPointsPerComponent`), unit-tested directly (the 8
match/no-match combinations plus an exact-scoreline case, per REQ-1304's
own Test level); `ScoreCorrectGuess` — the actual `IScoringStrategy`
interface member — throws `NotSupportedException` rather than
implementing anything, since it is architecturally unreachable for this
`GameKey` (ADR-0096: xG Predict never writes `Guess` rows); the three
named `LeaderboardService` scopes resolve sort direction per `GameKey` and
each has a passing `ADR0095_`-prefixed descending-sort test;
`architecture-reviewer` and `quality-architect` both ran clean after one
quality-gate fix round; `docs/requirements-document.md`,
`docs/architecture-document.md`, and `docs/decisions/0095-xg-predict-scoring-direction-exception.md`
reflect what's actually built vs. still open.

*Deps:* S-190 (COMP-15 module scaffold, ADR-0095 itself), S-192
(`XGPredictGameModule`/`ScoreSubmissionAsync`, establishing predictions
never become `Guess` rows — the reason `ScoreCorrectGuess` is unreachable
here).

*Explicitly out of scope, queued as follow-up:* REQ-1305 (asynchronous
grading job/trigger — the actual production caller for `ScorePrediction`,
still nonexistent), REQ-1306 (confirm-and-lock action), the real HTTP
submission/grading wiring and `RoundSchedulingOptions` registration for
`"xg-predict"` (still deliberately deferred, unchanged from S-192's own
scope note), and — surfaced by this story's own quality gate, not fixed
here — REQ-1304's acceptance-criteria text claiming the Global League
all-time ranking (REQ-401/409/410 — `GetGlobalLeaderboardAsync`/
`GetRankedMembersAsync`, median-per-round) also sorts `"xg-predict"`
descending. ADR-0095's Decision §3 only ever named the three plain-total
scopes above, not this one, and this story built exactly what was named.
`GetRankedMembersAsync`'s `OrderBy(m => m.Median)` remains unconditionally
ascending for every `GameKey`, currently latent (no `"xg-predict"` round
exists in production — round generation isn't wired yet), but a real,
undecided gap that must be resolved (either extend the migration to that
fourth call site, or narrow REQ-1304's text to match) before REQ-1305/1306
make `"xg-predict"` rounds real. Tracked here, not silently fixed or
silently ignored.

*Built as (2026-08-30):* `backend-implementer` built the core
implementation — `IScoringStrategy.LowerIsBetter`, `XGPredictScoringStrategy`
(`ScoreCorrectGuess` throwing `NotSupportedException`, `ScorePrediction`
implementing the formula), `ScoringRules.PredictPointsPerComponent`, the DI
registration, and the `LeaderboardService` migration across all three named
scopes, plus `XGPredictScoringStrategyTests` and updated
`LeaderboardServiceTests` (new constructor dependency, three new
`ADR0095_`-prefixed descending-sort cases). `architecture-reviewer` PASSed
with one non-blocking note: `ScoreCorrectGuess` throwing rather than
implementing anything is a real, if currently-unreachable, awkward fit for
`IScoringStrategy`'s existing shape — flagged as a standing item for
whichever story builds REQ-1305's grading job to either confirm
`ScorePrediction` as this `GameKey`'s permanent second entry point (via a
short ADR note) or revisit `IScoringStrategy`'s shape itself, per
ADR-0040's own follow-up precedent; not a blocker here since a fourth game
hasn't yet forced the question. `quality-architect` required one blocking
fix before passing — a rule-of-three duplication: the same
ternary-`OrderBy`/`ThenBy`/`Select` ranking shape had been written
independently at all three `LeaderboardService` call sites — extracted
into a shared private helper, `RankByTotalPoints`, in a follow-up commit.
`quality-architect` also found and flagged (rather than fixed) the
median-ranking scope gap listed above. The orchestrating session wrote
ADR-0095's own Follow-up amendment directly, recording both findings and
what shipped, before this story's doc-sync pass.

Testing: no local `dotnet` SDK available in-sandbox — hand-verified by the
implementer and by both quality-gate reviewers reading the actual diff; a
CI verification run (`ci.yml` `workflow_dispatch`) is needed before this
is considered fully done — the orchestrating session runs this next, same
recurring constraint as S-191/S-192 and every other recent backend story
in this file.

**S-194 · Close S-193's median-ranking scope gap in `GetRankedMembersAsync` (REQ-1304, ADR-0095)**
Small, direct follow-up to S-193, closing the one scope gap that story's
own quality gate surfaced (and flagged, not fixed) and queued as a backlog
follow-up: `LeaderboardService.GetRankedMembersAsync` — the median-based
global ranking behind `GetGlobalLeaderboardAsync`/`GetUserStatsAsync`'s
`Rank` (REQ-409/410) — still sorted unconditionally ascending after S-193
migrated the other three `OrderBy(TotalPoints)`-shaped scopes. Resolves
`IScoringStrategy.LowerIsBetter` per `GameKey` here too, the same
mechanism, so REQ-1304's acceptance text (which always claimed all four
scopes) is now fully accurate with no remaining gap.

*Accept:* all four `LeaderboardService` ranking scopes
(`GetActiveRoundLeaderboardAsync`/`GetClosedRoundLeaderboardAsync`/
`GetWindowedLeaderboardAsync`/`GetRankedMembersAsync`) now resolve sort
direction per `GameKey` via `IScoringStrategyResolver`; two new
`ADR0095_`-prefixed tests added to `LeaderboardServiceTests` covering
`GetRankedMembersAsync`'s descending-sort case; `RankByTotalPoints` (S-193's
shared helper) deliberately not reused here, given the shape mismatch
between its `(int TotalPoints, List<LeaderboardEntry>)` and
`GetRankedMembersAsync`'s `(double Median, raw ranked tuple list)`; both
`architecture-reviewer` and `quality-architect` PASSed with no blocking
code findings, their only note being that the docs tracking this exact gap
(now closed) hadn't been updated yet.

*Deps:* S-193 (the `IScoringStrategy.LowerIsBetter`/`IScoringStrategyResolver`
mechanism this story reuses, and the gap it flagged).

*Explicitly out of scope:* REQ-1305 (asynchronous grading job/trigger),
REQ-1306 (confirm-and-lock action), and the real HTTP submission/grading
wiring for `"xg-predict"` — all unchanged and unaffected by this story,
same as S-193's own scope note.

*Built as (2026-08-30):* `backend-implementer` extended
`GetRankedMembersAsync` with its own `OrderBy`/`OrderByDescending` branch
resolving `scoringStrategyResolver.Resolve(gameKey).LowerIsBetter`, kept
separate from `RankByTotalPoints` rather than forced into it given the
tuple/return-type mismatch, plus two new `ADR0095_`-prefixed tests.
`architecture-reviewer` and `quality-architect` both PASSed with no
blocking code findings. `doc-sync` then updated
`docs/requirements-document.md` (REQ-1304's and REQ-409's status notes),
`docs/architecture-document.md` (COMP-02's row, §5.3's ADR-evolution table,
and the xG Predict data-flow diagram in §6), this backlog entry, and
`docs/decisions/0095-xg-predict-scoring-direction-exception.md`'s
amendment (written directly by the orchestrating session ahead of this
pass) to record the gap as closed.

Testing: no local `dotnet` SDK available in-sandbox — hand-verified by the
implementer and by both quality-gate reviewers reading the actual diff; a
CI verification run (`ci.yml` `workflow_dispatch`) is needed before this is
considered fully done — the orchestrating session triggers CI next, same
recurring constraint as S-191/S-192/S-193 and every other recent backend
story in this file.

**S-195 · xG Predict asynchronous per-match grading (REQ-1305, ADR-0097)**
Direct continuation of S-190 through S-194, closing REQ-1305's own "Needs
an ADR" section — the last of REQ-1301-1305's structural gaps — and
implementing the grading leg itself. Run through `/orchestrate` end to
end (intake → scope check → ADR → delegation → quality gate → doc sync →
CI verification). Resolves REQ-1305's two deliberately-deferred structural
questions: what triggers grading (a new hourly scheduled job/endpoint,
mirroring ADR-0072's per-`GameKey` workflow shape, not folded into
`generate-grid-round.yml`/`generate-path-round.yml`), and how a
locked-but-ungraded round's `Closed` status/leaderboard participation
interact (fully decoupled — a round can close with matches still
`Pending`, and the leaderboard shows a partial, always-growing total,
never a withheld one). Also settles a third question the first two forced:
where grading results are persisted and read from, since `Guess`/
`IGuessRepository` don't apply (ADR-0096 already established xG Predict
never writes `Guess` rows).

*Accept:* ADR-0097
(`docs/decisions/0097-xg-predict-async-grading-trigger-and-partial-round-state.md`)
exists and decides the trigger, entity/read-path shape, idempotency
mechanism, and `Closed`/leaderboard interaction; `IPredictGradingService`/
`PredictGradingService` (`XGArcade.Games.XGPredict`) is a real, tested
implementation that fetches each ready match's result via
`IApiFootballClient.GetFixtureResultAsync`, grades every prediction via
`XGPredictScoringStrategy.ScorePrediction` (REQ-1304), voids postponed/
abandoned matches, leaves not-yet-confirmed matches for retry, and is
idempotent by construction (`GradingStatus == Pending` is the only query
predicate); `PredictMatch` gains `GradingStatus`/`ActualHomeGoals`/
`ActualAwayGoals` and `PredictMatchPrediction` gains `FinalPoints` via a
real migration (`20260830130000_AddPredictMatchGrading`, with matching
`.Designer.cs`/`XGArcadeDbContextModelSnapshot.cs` updates); a new
bearer-token-gated `POST /internal/grade-predict-matches` endpoint exists,
registered unconditionally like `/internal/generate-round`; a new
`.github/workflows/grade-predict-matches.yml` polls it hourly plus
`workflow_dispatch`; `architecture-reviewer` and `quality-architect` both
PASSed with only non-blocking notes (below); `docs/requirements-document.md`,
`docs/architecture-document.md`, and `docs/implementation-document.md`
reflect what's actually built vs. still deferred (REQ-1306 unaffected,
still design-only).

*Deps:* S-190 (COMP-15 module scaffold), S-191 (`IApiFootballClient`/
`GetFixtureResultAsync`), S-192 (`PredictMatch`/`PredictMatchPrediction`
entities, `IPredictInstanceRepository`), S-193 (`XGPredictScoringStrategy`/
`ScorePrediction`, REQ-1304's formula).

*Explicitly out of scope, queued as follow-up (per ADR-0097's own text):*
`ILeaderboardService`/`LeaderboardEndpoints` wiring of the new
`GetTotalPointsByInstanceIdAsync` for `"xg-predict"` round totals — a real,
separate piece of work with its own design questions (e.g. how
`GetClosedRoundsAsync`'s `ClosedAt`-gated browsing interacts with a round
whose total is still growing), not decided by ADR-0097 and not started
here. Also unaffected and unchanged, same as every prior xG Predict
story's own scope note: `RoundSchedulingOptions`/round-generation wiring
for `"xg-predict"`, and REQ-1306 (confirm-and-lock action). Two small,
non-blocking quality-gate notes, not worth their own story: (1)
`ServiceRegistration.cs` registers `XGPredictScoringStrategy` twice (once
concrete, once via `IScoringStrategy`) instead of once resolved two ways —
cosmetic, harmless since the class is stateless; (2)
`InternalPredictGradingEndpointTests.cs` doesn't swap in a fake
`IApiFootballClient` via `WebApplicationFactory` (the way
`AdminSuggestionEndpointTests.cs` does for `IWikidataClient`), so no test
exercises the endpoint returning non-zero graded/voided counts end-to-end
— service-level tests already cover the actual grading logic thoroughly,
so this is a coverage nicety, not a real gap.

*Built as (2026-08-30):* the orchestrating session wrote ADR-0097 directly
(same precedent as ADR-0094/ADR-0095/ADR-0096's own authorship), deciding
the trigger (a third, purpose-built hourly workflow, not a variation of
the existing two per-`GameKey` round-generation workflows), the entity/
read-path shape (`PredictMatchGradingStatus` enum as the sole idempotency
source of truth, nullable `FinalPoints` mirroring `Guess.FinalPoints`, no
materialized "missing prediction" row — a deliberate, permanent difference
from `MaterializeUnansweredCellsAsync`'s ADR-0021 pattern, since
higher-is-better scoring means "no row" and "0 points" already coincide),
and the `Closed`/leaderboard decoupling (not a gap to patch — the direct,
intended consequence of `LockRoundScoresAsync` already being a no-op for
an xG Predict round, verified by reading the code rather than assumed).
`backend-implementer` built `IPredictGradingService`/`PredictGradingService`,
the five new `IPredictInstanceRepository` methods
(`GetMatchesReadyForGradingAsync`/`GetPredictionsForMatchAsync`/
`GradeMatchAsync`/`VoidMatchAsync`/`GetTotalPointsByInstanceIdAsync`), the
new entity columns and hand-written migration (same no-`dotnet`-SDK
hand-verification constraint as every other recent backend story —
brace/paren balance, migration/snapshot byte-identity, cross-referenced
signatures), the `/internal/grade-predict-matches` endpoint, the
`grade-predict-matches.yml` workflow, and full NUnit coverage
(`PredictGradingServiceTests`, extended `PredictInstanceRepositoryTests`,
`InternalPredictGradingEndpointTests`) — the confirmed/not-yet-confirmed/
postponed/idempotent-second-run cases REQ-1305's own "Test level" text
calls for.

`architecture-reviewer` and `quality-architect` (run in parallel) both
PASSed this diff with only the two non-blocking notes listed above — no
blocking findings, no fix round required, a change from S-192/S-193's own
one-fix-round pattern.

Testing: no local `dotnet` SDK available in-sandbox — hand-verified by the
implementer and by both quality-gate reviewers reading the actual diff; a
CI verification run (`ci.yml` `workflow_dispatch`) is needed before this is
considered fully done — the orchestrating session triggers CI next, same
recurring constraint as S-191 through S-194 and every other recent backend
story in this file.

**S-196 · Wire `"xg-predict"` into per-`GameKey` round scheduling (REQ-1301, ADR-0051, ADR-0072)**
Direct continuation of S-190/S-192/S-193, closing the last gap those
stories explicitly deferred: `RoundSchedulingOptions` registration for
`"xg-predict"` and wiring it into `InternalRoundEndpoints`'s `gameKey`
switch (S-193 already closed the sibling `IScoringStrategy` gap). Run
through `/orchestrate` end to end (intake → scope check → ADR
re-derivation → delegation → quality gate → doc sync → CI verification),
mirroring ADR-0051/ADR-0072's existing `"xg-grid"`/`"xg-path"` pattern
exactly rather than inventing a new design.

*Accept:* a new `PredictGenerationOptions` (`Games.XGPredict`, `MatchCount`
default 5) and `PredictTemplateResolver`
(`XGArcade.Api.Predict.GetOrCreateByMatchCountAsync`) mirror
`GridGenerationOptions`/`PathGenerationOptions` and
`GridTemplateResolver`/`PathTemplateResolver` exactly; `IPredictInstanceRepository`/
`PredictInstanceRepository` gained `GetTemplateByMatchCountAsync`/
`AddTemplateAsync` against the existing `PredictTemplates` table (no new
migration needed); `InternalRoundEndpoints`'s `gameKey` switch has a third
arm for `"xg-predict"`, its up-front validation widened to allow it, and
its exception filter widened to catch `PredictGenerationException`;
`LeaderboardEndpoints.ValidateGameKey`'s allow-list includes
`"xg-predict"`; `ServiceRegistration.cs` registers `PredictGenerationOptions`
and a third `RoundSchedulingOptions` instance (`RoundScheduling:XGPredict:RoundDurationHours`,
default 48h, new `appsettings.json` key); a new
`.github/workflows/generate-predict-round.yml` is a third fully
independent per-`GameKey` round-generation workflow (daily cron, own
`workflow_dispatch.round_duration_hours` input, reusing the existing
`.github/actions/trigger-round-generation` composite action) —
`generate-grid-round.yml`/`generate-path-round.yml` untouched, and no
shared/matrix workflow reintroduced (ADR-0072's explicit prohibition).

*Deps:* S-190 (COMP-15 module scaffold), S-192 (`XGPredictGameModule.GenerateInstanceAsync`,
the caller this story finally makes reachable), S-193 (`IScoringStrategy`
registration for `"xg-predict"`, the sibling gap this story's own
`RoundSchedulingOptions` registration completes).

*Explicitly out of scope, unaffected by this story:* REQ-1302/1303
prediction *submission* — `GuessSubmissionService` is still not wired to
`XGPredictGameModule.ScoreSubmissionAsync`, and no real HTTP submission
endpoint exists yet; REQ-1304's scoring formula (already built, S-193);
REQ-1305 (asynchronous grading job/trigger); REQ-1306 (confirm-and-lock
action); frontend work. Only round *generation* scheduling is wired by
this story.

*Built as (2026-08-30):* the orchestrating session re-derived both ADRs'
own "if a third game is added" Follow-up notes directly (not delegated —
see the dated amendments in `docs/decisions/0051-per-gamekey-round-scheduling.md`
and `docs/decisions/0072-split-generate-round-workflow-per-gamekey.md`),
concluding the existing three-armed-switch/independent-workflow-file
pattern still holds unchanged; no new ADR was needed. `backend-implementer`
then built the six mechanical pieces described above, plus test coverage:
`RoundSchedulingOptionsResolverTests` (extended to all three `GameKey`s),
new `PredictTemplateResolverTests`, new `PredictInstanceRepositoryTests`
coverage for the two new repository methods, new `REQ1301_`-prefixed
`RoundEndpointTests` API-level coverage (round generation via
`gameKey=xg-predict`, end to end, including the too-few-fixtures abort
path), and a new `LeaderboardEndpointTests` allow-list case.

Quality gate (`architecture-reviewer` + `quality-architect`, run in
parallel): `architecture-reviewer` PASSed clean — no boundary violations
against `docs/architecture-document.md`, ADR-0003, ADR-0006, ADR-0051, or
ADR-0072. `quality-architect` found no production-code issues but flagged
two new test methods missing the `REQ1301_` naming prefix this repo's
`docs/coding-guidelines.md` requires (the same file already establishes
the correct convention one game over, for `"xg-path"`) — fixed in a
same-session follow-up commit — plus three doc gaps (a stale REQ-1301
status note, a missing CHANGELOG entry, and an ADR-amendment claim about a
backlog follow-up that didn't exist yet), all closed by this same doc-sync
pass, including adding the small follow-up item below so ADR-0072's
amendment's claim is accurate.

Testing: no local `dotnet` SDK available in-sandbox — hand-verified by the
implementer (brace/paren balance, cross-referenced signatures) and by both
quality-gate reviewers reading the actual diff; a CI verification run
(`ci.yml` `workflow_dispatch`) is needed before this is considered fully
done — the orchestrating session triggers CI next, same recurring
constraint as S-191/S-192/S-193/S-194 and every other recent backend story
in this file.

**Follow-up (flagged 2026-08-30, S-196, ADR-0072's amendment): tune `"xg-predict"`'s
`RoundDuration` default toward its real gameweek cadence.** REQ-1301 draws
its 5 matches from "an upcoming Premier League gameweek," which occurs
roughly weekly in the real world — `RoundScheduling:XGPredict:RoundDurationHours`
currently defaults to 48h, the same value as `"xg-grid"`/`"xg-path"`,
chosen only for consistency with the existing pattern, not because 48h is
actually the right cadence for a weekly-occurring gameweek. The daily
generation cron (`generate-predict-round.yml`) is *safe* either way
(idempotent, no-op on days no new round is due), so this is a product-tuning
question, not a correctness bug — not decided or silently assumed either
way by S-196, tracked here as an explicit open item. Trigger: once real
xG Predict rounds are actually generating in production (needs S-196 plus
a live API-Football key, MVP-SCOPE.md's xG-Predict-specific precondition)
and the 48h default is observed to produce awkwardly-timed or overlapping
rounds against real Premier League gameweek scheduling.

**S-197 · xG Predict round/prediction screen: submission, round lock, and confirm-and-lock endpoints + frontend (REQ-1301/1302/1303/1306, ADR-0098)**
Direct continuation of S-190 through S-196, closing the last gap those
stories explicitly deferred: REQ-1302/1303 prediction submission had no
real HTTP endpoint, and REQ-1306 (confirm-and-lock) had no code at all.
Run through `/orchestrate` end to end (intake → scope check → ADR
scaffold → delegation → quality gate → doc sync → CI verification).

*Accept:* a new `XGArcade.Api.Predict.PredictEndpoints`
(`GET /predict/current`, `POST /predict/matches/{matchId}/predictions`,
`POST /predict/confirm`) calls
`IGameModuleResolver.Resolve("xg-predict").ScoreSubmissionAsync` directly —
never through `Guess`/`IGuessSubmissionService` (ADR-0096 already ruled
that shape out); a new `PredictPlayerLock` entity (composite-keyed on
`(PredictInstanceId, UserId)`, migration
`20260831090000_AddPredictPlayerLock`) backs two new
`IPredictInstanceRepository` methods, `IsPlayerLockedAsync`/
`LockPlayerPredictionsAsync`, implementing REQ-1306's per-player lock,
checked in the API endpoint before `ScoreSubmissionAsync` is ever called
(not inside `XGPredictGameModule` — see ADR-0098); `PredictInstance`
gained a `[NotMapped]` computed `LockInstant` property, extracted after
`Matches.Min(m => m.KickoffUtc)` was independently re-derived at three
call sites (a quality-gate fix, no migration). On the frontend, a new
`frontend/src/predict/` module (`PredictScreen.tsx`, `PredictMatchInput.tsx`,
`PredictConfirmDialog.tsx`, SCREEN-14) shows the round's 5-match slate at
once, each match its own card with a per-match "Save" button, a
round-wide-lock notice (REQ-1303) and a per-player-lock notice (REQ-1306)
each shown independently, and a confirm dialog reusing
`GuestLogoutConfirm.tsx`'s exact structural/accessibility pattern.
`GameSelectScreen`/`HeaderNav` gained a third tile/nav entry for xG
Predict, kept in agreement per SCREEN-09. Deliberately does NOT wire
`RoundCompletionBanner`/REQ-1210 (confirmed inapplicable to this game,
per §4.14's own note).

*Deps:* S-190 (COMP-15 module scaffold), S-192
(`XGPredictGameModule.ScoreSubmissionAsync`, the method this story finally
gives a real caller), S-196 (round generation reachable end to end, so a
round exists for this screen to show).

*Explicitly out of scope, unaffected by this story:* `ILeaderboardService`/
`LeaderboardEndpoints` wiring of `GetTotalPointsByInstanceIdAsync` for
`"xg-predict"` round totals (pre-existing gap from S-195/S-196); a
`GameKey` allow-list on `GuessEndpoints`/`GuessSubmissionService` (flagged
as a risk in ADR-0098's Consequences section, not fixed — see the
follow-up below); REQ-710 account-deletion wiring for
`PredictPlayerLock`/`PredictMatchPrediction` (flagged in
`XGArcadeDbContext.cs`'s own comment on `PredictPlayerLock`'s
`OnModelCreating` registration, not fixed — see the follow-up below); a
Playwright E2E spec for xG Predict (no `play-predict.spec.ts` exists yet,
unlike `play-grid.spec.ts`/`play-path.spec.ts`).

*Built as (2026-08-31):* `backend-implementer` built the endpoint file,
`PredictPlayerLock` entity/migration, and the two new repository methods;
`ui-implementer` built the frontend screen against SCREEN-14 (added to
`docs/design-document.md` by this same story, version 0.84). A same-session
quality-gate follow-up commit extracted `PredictInstance.LockInstant` (the
round-lock formula had been independently re-derived at three call sites)
and fixed stale comments. New backend tests: `PredictEndpointTests.cs`
(`REQ1301_`/`REQ1302_`/`REQ1303_`/`REQ1306_`-prefixed, end to end against
all three endpoints, including the full REQ-1306 confirm-lock lifecycle)
and extended `PredictInstanceRepositoryTests` coverage for
`GetPredictionsForInstanceAndUserAsync`. New frontend tests:
`PredictScreen.test.tsx`, `PredictMatchInput` coverage inline in it,
`PredictConfirmDialog.test.tsx`, plus updated `GameSelectScreen.test.tsx`/
`HeaderNav.test.tsx`/`App.test.tsx` for the third game tile/nav entry.

Quality gate (`architecture-reviewer` + `quality-architect`, run in
parallel): `architecture-reviewer` PASSed clean — no boundary violations
against `docs/architecture-document.md`, ADR-0003, ADR-0006, or ADR-0096,
and confirmed ADR-0098 was the right call for a new structural decision
(lock-check placement, lock storage shape) rather than a silent choice.
`quality-architect` found the `LockInstant`-formula duplication and two
stale comments (fixed same session, see the `df22345` commit) and flagged
the `GuessEndpoints`/`GameKey`-allow-list risk and the `PredictPlayerLock`
REQ-710 gap as backlog-worthy rather than blocking — both now tracked
below.

Testing: no local `dotnet`/`npm` available in-sandbox for the full
suite — CI verification (`ci.yml` `workflow_dispatch`) confirmed green
(backend, frontend unit, E2E) before this story was considered done, same
recurring constraint as every other recent story in this file.

**Follow-up (flagged 2026-08-31, S-197, ADR-0098's Consequences section): add a `GameKey` allow-list to `GuessEndpoints`/`GuessSubmissionService`.**
ADR-0098's Decision §1 (REQ-1306's lock check lives in `PredictEndpoints`,
not `XGPredictGameModule`) depends on `GuessSubmissionService` never
becoming a second, unguarded path into
`XGPredictGameModule.ScoreSubmissionAsync`. Today `GuessEndpoints`'
`POST /rounds/{roundId}/cells/{cellId}/guesses` has no `GameKey`
allow-list at all — it is safe only because
`XGPredictGameModule.GetMaxAttemptsForCellAsync` still throws
`NotImplementedException` for `"xg-predict"`, so `GuessSubmissionService`
never actually reaches `ScoreSubmissionAsync` through that route. That
safety is incidental, not structural. Trigger: whoever implements
`GetMaxAttemptsForCellAsync` for xG Predict must, in the same story, either
add an explicit `GameKey` guard to `GuessEndpoints`/`GuessSubmissionService`
or move REQ-1306's lock check somewhere both paths pass through — do not
implement that method without addressing this.

**Follow-up (flagged 2026-08-31, S-197, `XGArcadeDbContext.cs`'s own comment on `PredictPlayerLock`): wire REQ-710 account-deletion handling for `PredictPlayerLock`/`PredictMatchPrediction`.**
`AccountDeletionService` does not reference either table today.
`PredictMatchPrediction.UserId` is nullable and shaped to mirror
`Guess.UserId`'s anonymize-in-place path, but nothing calls that path yet
for this table specifically. `PredictPlayerLock.UserId` is *not* nullable
(it is half of the table's composite primary key), so the usual
"anonymize by setting `UserId = NULL`" approach is structurally
unavailable for it — the only viable path is a hard delete of the row on
account deletion, expected to be safe once wired (a lock row is a flag,
not a scoring row the way `Guess` is, so nothing else depends on it
surviving). Trigger: before xG Predict is considered feature-complete for
a real user base, or sooner if a REQ-710 compliance review is scheduled.

---

**S-198 · xG Predict as a third leaderboard game tab (SCREEN-03 frontend generalization) (REQ-404, REQ-1304, ADR-0095)**
`ui-implementer` generalizes SCREEN-03's leaderboard screen
(`frontend/src/leaderboard/`) from a two-game (xG Grid/xG Path) switcher to
three, closing the gap the 2026-08-30 `LeaderboardService`
per-`GameKey` sort-direction work (REQ-401/404's status note, ADR-0095)
left on the frontend side. `LeaderboardScreen.tsx`'s `GameKey` union and
`GAME_TABS` array widen to include `xg-predict` (importing the existing
`XG_PREDICT_GAME_KEY` constant from `GameSelectScreen.tsx` rather than
redefining it); the "Lowest total wins" subtitle now reads per-`GameKey`
("Highest total wins" for xG Predict, unchanged everywhere else); and the
`(ⓘ)` scoring-explainer branch, previously a two-way Grid/Path ternary
that would have incorrectly shown Path's explainer for Predict, gained a
third branch and a new component, `frontend/src/predict/
PredictScoringExplainer.tsx`, describing REQ-1304's three independent
scoring components (outcome/home-goals/away-goals) and explicitly stating
xG Predict is higher-is-better, unlike its two siblings.
`LeaderboardRowsList.tsx` and each of the four per-scope components
(`AllTimeLeaderboard`/`LiveLeaderboard`/`PastRoundsLeaderboard`/
`WindowedLeaderboard`) needed no change — confirmed by reading each and
grepping for `XG_GRID_GAME_KEY`/`XG_PATH_GAME_KEY`, none independently
hardcode a two-game assumption; they already render whatever `rows`/`rank`
the API response carries, without re-sorting client-side, which is what
makes ADR-0095's per-`GameKey` sort direction safe to consume as-is.

*Accept:* xG Predict is selectable as a third leaderboard game tab,
same order as `GameSelectScreen`'s tiles/`HeaderNav`'s "Games" list;
selecting it re-fetches whichever scope tab is active scoped to
`gameKey=xg-predict` and shows "Highest total wins"; the `(ⓘ)` entry point
shows `PredictScoringExplainer`'s content, not Grid's or Path's, when that
tab is active; a mocked descending-order API response for `xg-predict`
renders in that exact order/rank, proving the frontend doesn't assume
ascending sort. New/updated Vitest coverage in `LeaderboardScreen.test.tsx`
and `AllTimeLeaderboard.test.tsx`, REQ-404-referencing.

**Deliberately ships ahead of a backend gap, not fully functional end to
end — do not read this story as closing that gap:** `LeaderboardService`
still totals every scope from `Guess.FinalPoints` via `IGuessRepository`,
and xG Predict never writes `Guess` rows (ADR-0096 — predictions live in
`PredictMatchPrediction`, totaled via the separate
`GetTotalPointsByInstanceIdAsync` repository method already built by S-195).
Wiring that method into `LeaderboardService`/`LeaderboardEndpoints` for
`"xg-predict"` remains the still-open backend follow-up S-193/S-195/S-197
already flagged (search those entries' own "Explicitly out of scope"
notes for `GetTotalPointsByInstanceIdAsync`) — this story does not touch
that. Net effect: the xG Predict leaderboard tab calls the real endpoints
successfully but renders empty (REQ-404's zero-guess exclusion filters out
every xG Predict player, since none have `Guess` rows) until that backend
story lands. Flagged inline via a doc comment on `LeaderboardScreen.tsx`'s
`GameKey` type, in this backlog entry, and in `docs/CHANGELOG.md` — not
silently implied to be fully wired.

*Non-blocking follow-up surfaced by this story's own review, not addressed
here:* `frontend/src/users/UserStatsScreen.tsx` (SCREEN-13) has the same
two-game (xG Grid/xG Path) hardcoded switcher and its own "Lowest total
wins" note (`docs/design-document.md` ~line 3043-3107) that this story
deliberately left untouched — a different screen/story, not part of
SCREEN-03's generalization. Trigger: whenever SCREEN-13 is next touched,
or before xG Predict's stats need to appear there.

*Built as (2026-08-31):* `ui-implementer` widened `GameKey`/`GAME_TABS`,
added the per-`GameKey` subtitle branch and the three-way explainer
ternary in `LeaderboardScreen.tsx`, and built
`frontend/src/predict/PredictScoringExplainer.tsx`/`.css` following
`PathScoringExplainer.tsx`'s exact shell pattern (own focus-management/
Escape-to-close, not extracted into a shared hook — same "not yet three
of the same shape" reasoning that file's own comment already gives).
Added a display-only `PREDICT_POINTS_PER_COMPONENT` constant to
`frontend/src/lib/scoringRules.ts`, mirroring
`ScoringRules.PredictPointsPerComponent` the same way `MAX_POINTS_PER_CELL`
already mirrors its own backend constant, never for enforcement.

Testing: `npm run test` (Vitest) run locally in-sandbox (frontend-only
change, no backend touched) — 756/756 passed, including 6 new tests; `tsc
-b` and `npm run lint` both clean. No CI-trigger fallback needed for this
change.

---

## Epic 13 — xG Predict gap closure

S-190 through S-198 shipped every planned xG Predict story, but several
explicitly flagged "not fixed here, tracked as follow-up" gaps accumulated
along the way rather than being silently closed or silently ignored (see
each story's own "Explicitly out of scope" note). This epic closes them.
Ordered so no story here depends on a later one; S-199/S-200/S-201 are
independent of each other and of everything below them.

**S-199 · Wire `"xg-predict"` round totals into `LeaderboardService` (REQ-404, REQ-411, ADR-0096; needs a new ADR)**
Closes the gap S-193/S-195/S-197/S-198 each flagged and left open: every
`LeaderboardService` scope (`GetRankedMembersAsync`/`GetUserStatsAsync` via
`GetPerRoundFinalPointsByUserIdsAsync`; `GetActiveRoundLeaderboardAsync` via
`ILiveRoundContributionService`; `GetClosedRoundLeaderboardAsync`/
`GetWindowedLeaderboardAsync` via `GetTotalFinalPointsByRoundIdAsync`/
`GetTotalFinalPointsByRoundIdsAsync`) sources totals from `IGuessRepository`
only. `"xg-predict"` predictions never write `Guess` rows (ADR-0096), so
every one of these scopes silently returns zero xG Predict participants —
not just the all-time tab S-198 wired the frontend for.
`IPredictInstanceRepository.GetTotalPointsByInstanceIdAsync` (built in
S-195, `backend/src/XGArcade.Data/Repositories/IPredictInstanceRepository.cs:108`)
already computes the per-round-per-user totals needed; nothing currently
calls it from `LeaderboardService`
(`backend/src/XGArcade.Core/Leagues/LeaderboardService.cs`).
*Accept:* every scope above returns correct `"xg-predict"` totals/ranks
once real xg-predict rounds exist, verified by extending
`LeaderboardServiceTests` with `"xg-predict"`-scoped cases per scope (mirror
the existing `ADR0095_`-prefixed sort-direction tests' structure) and a new
`LeaderboardEndpointTests`/`UserEndpoints`-facing API test. Because this
touches every ranking scope's data source rather than one call site, and
because there is a real design choice in *how* (a `GameKey`-branching
if/else per scope vs. a small `IRoundScoreSource`-style abstraction
resolved the same way `IScoringStrategyResolver` already is), write a new
ADR before implementing — follow `IScoringStrategyResolver`'s existing
per-`GameKey` resolution pattern if it fits cleanly; don't introduce a
heavier abstraction than the two implementations (`Guess`-backed,
`PredictMatchPrediction`-backed) actually need. Live-round scope
(`GetActiveRoundLeaderboardAsync`) needs its own look: `ILiveRoundContributionService`
computes *in-progress* per-cell contribution, a concept `PredictMatchPrediction`
doesn't have in the same shape (predictions score only once matches are
graded, per ADR-0097) — confirm whether "live" xG Predict leaderboard
should show partial/graded-so-far totals or simply exclude xG Predict
rounds until closed, and record that as part of the new ADR rather than
guessing silently.
*Deps:* none (S-195/S-197/S-198 already merged).

*Built as (2026-08-31):* `backend-implementer` wrote ADR-0100 (accepted
before implementation, per this story's own instruction) and implemented
exactly what it specifies — new `Core.Scoring.IRoundScoreSource`/
`IRoundScoreSourceResolver` (`backend/src/XGArcade.Core/Scoring/`);
`GuessRoundScoreSource` (zero-behavior-change pass-through for
`"xg-grid"`/`"xg-path"`, registered twice) and `PredictRoundScoreSource`
(`backend/src/XGArcade.Games.XGPredict/PredictRoundScoreSource.cs`,
wrapping `IPredictInstanceRepository` only, never `IRoundRepository`/
`IUserRepository`); a new `IPredictInstanceRepository.GetParticipantUserIdsByInstanceIdAsync`
(participation, not points); `IRoundRepository.GetClosedIdsWithinWindowAsync`
widened from ids-only to full `Round` rows. `LeaderboardService`'s four
scopes now resolve `roundScoreSourceResolver.Resolve(gameKey)` instead of
injecting `IGuessRepository`/`ILiveRoundContributionService` directly —
those two are no longer constructor dependencies of `LeaderboardService`
itself. Composition-root wiring in `ServiceRegistration.cs` builds the
resolver's `GameKey -> IRoundScoreSource` dictionary directly (not a second
multi-registration of `IRoundScoreSource`, since the interface carries no
`GameKey` property of its own). New tests: `LeaderboardServiceTests`
(`ADR0100_`-prefixed cases, a hand-rolled `FakeRoundScoreSource` proving
resolver routing without this test project referencing Games.XGPredict),
`PredictRoundScoreSourceTests` (`XGArcade.Games.XGPredict.Tests`, real
InMemory-backed `PredictInstanceRepository`, no fakes), and one new
`LeaderboardEndpointTests` case proving a closed `"xg-predict"` round's
graded total is visible end to end through the real composition root.
Built without a local `dotnet` SDK in this sandbox — hand-traced, not
locally run; CI verification via `ci.yml`'s `workflow_dispatch` is required
before this is considered done, same recurring constraint as other recent
backend stories in this log.

**S-200 · Add a `GameKey` allow-list to `GuessEndpoints`/`GuessSubmissionService` (ADR-0098's Consequences section)**
Security follow-up flagged in S-197: ADR-0098 relies on REQ-1306's
confirm-and-lock check living only in `PredictEndpoints`, which only holds
because `XGPredictGameModule.GetMaxAttemptsForCellAsync`
(`backend/src/XGArcade.Games.XGPredict/XGPredictGameModule.cs:166`) still
throws `NotImplementedException`, keeping
`POST /rounds/{roundId}/cells/{cellId}/guesses`
(`backend/src/XGArcade.Api/Guesses/GuessEndpoints.cs:15`) from ever
reaching `XGPredictGameModule.ScoreSubmissionAsync` through
`GuessSubmissionService`/`IGameModuleResolver`. Still confirmed true as of
S-198 — this is incidental safety, not structural. Fix now, ahead of
whoever next touches `GetMaxAttemptsForCellAsync`, rather than continuing
to carry it as a landmine.
*Accept:* `GuessEndpoints`/`GuessSubmissionService` reject `"xg-predict"`
explicitly (a clear 4xx, not a fallthrough to `ScoreSubmissionAsync`)
regardless of `GetMaxAttemptsForCellAsync`'s implementation state; a new
`GuessSubmissionServiceTests`/`GuessEndpointTests` case proves a
`"xg-predict"` round's guess submission is rejected even if
`GetMaxAttemptsForCellAsync` were made to return a value. Update ADR-0098's
Consequences section to mark this risk closed.
*Deps:* none.

*Built as (2026-08-31):* `backend-implementer` added a new
`GuessSubmissionOutcome.GameNotSupported` value
(`backend/src/XGArcade.Core/Scoring/GuessSubmissionResult.cs`) and a new
`GuessSubmissionAllowedGameKeys` type
(`backend/src/XGArcade.Core/Scoring/GuessSubmissionAllowedGameKeys.cs`) — an
explicit allow-list, not a `"xg-predict"` deny-list, so `Core.Scoring` still
never references `Games.XGPredict` (ADR-0003), following the same
composition-root-supplied-`GameKey` shape already established by
`GuessRoundScoreSource`/`IRoundScoreSourceResolver` (ADR-0100). `GuessSubmissionService`
now takes this as a constructor dependency and checks `round.GameKey`
against it immediately after resolving the `Round`, before
`IGameModuleResolver.Resolve`/`GetMaxAttemptsForCellAsync`/
`ScoreSubmissionAsync` are ever reached — unconditional on
`GetMaxAttemptsForCellAsync`'s implementation state for any game.
`ServiceRegistration.cs` registers it as `{GridGameModule.XGGridGameKey,
XGPathGameModule.XGPathGameKey}`. `GuessEndpoints` maps the new outcome to a
400 (`"Game not supported"`) — a 400, not a 409, since nothing about the
round's state is in conflict. New tests: two `GuessSubmissionServiceTests`
cases (a `"xg-predict"` round rejected even though the fake game module is
rigged to succeed if it were ever called, proving the guard is structural;
and the mirror-image case confirming an allow-listed `GameKey` still reaches
the game module normally) and one `GuessEndpointTests` case proving the real
composition-root-wired endpoint returns 400 for a bare `"xg-predict"` round
with no backing game-instance data at all. ADR-0098's Consequences section
updated to mark the flagged risk closed. Built without a local `dotnet` SDK
in this sandbox — hand-traced, not locally run; CI verification via
`ci.yml`'s `workflow_dispatch` is required before this is considered done,
same recurring constraint as other recent backend stories in this log.

**S-201 · Wire REQ-710 account-deletion handling for `PredictPlayerLock`/`PredictMatchPrediction` (REQ-710)**
Flagged in S-197 via `XGArcadeDbContext.cs`'s own comment on
`PredictPlayerLock`'s `OnModelCreating` registration
(`backend/src/XGArcade.Data/XGArcadeDbContext.cs:372`).
`AccountDeletionService` (`backend/src/XGArcade.Core/Auth/AccountDeletionService.cs`)
currently anonymizes `Guess` rows only and doesn't reference either xG
Predict table.
*Accept:* `PredictMatchPrediction.UserId` is anonymized (`UserId = NULL`)
the same way `Guess.UserId` already is on account deletion — never
hard-deleted, for the same historical-scoring-integrity reason REQ-710
gives for `Guess`. `PredictPlayerLock` rows for the deleted user are
hard-deleted instead: its `UserId` is non-nullable (half the composite
primary key), so anonymize-in-place is structurally unavailable, and a
lock row is a flag rather than a scoring row, so nothing depends on it
surviving (this reasoning was already recorded in S-197's follow-up note —
implement it, don't re-derive it). New `AccountDeletionServiceTests` cases
covering both tables.
*Deps:* none.

**S-202 · Generalize `UserStatsScreen` (SCREEN-13) to a third xG Predict game tab (REQ-411, REQ-1304)**
Frontend counterpart to S-198, explicitly left untouched by that story.
`frontend/src/users/UserStatsScreen.tsx`'s `GAME_TABS` array is still
hardcoded to `XG_GRID_GAME_KEY`/`XG_PATH_GAME_KEY` only, and its own
"lowest total wins" copy (see `docs/design-document.md` ~line 3043-3107)
needs the same per-`GameKey` branch `LeaderboardScreen.tsx` already gained
in S-198. Follow that story's exact pattern: import
`XG_PREDICT_GAME_KEY` from `GameSelectScreen.tsx` rather than redefining
it, add the third tab, and branch the "highest/lowest total wins" copy per
`GameKey` the same way.
*Accept:* xG Predict is selectable as a third tab on both "own stats" and
"another player's stats" views; copy reads "Highest total wins" for xG
Predict; new/updated Vitest coverage in `UserStatsScreen.test.tsx`.
Requires S-199 to be merged first — otherwise this ships the same
"renders empty" gap S-198 explicitly flagged and this epic exists to close,
not repeat.
*Deps:* S-199.

*Built as (2026-09-02):* `frontend/src/users/UserStatsScreen.tsx`'s
`GAME_TABS` widened to three entries (imports `XG_PREDICT_GAME_KEY` from
`GameSelectScreen.tsx`, same as `LeaderboardScreen.tsx`'s S-198 pattern);
the hardcoded "Lowest total wins" subtitle replaced with an exhaustive-
switch `subtitleForGameKey` helper (xG Grid/xG Path → "Lowest total wins",
xG Predict → "Highest total wins"), mirroring `LeaderboardScreen.tsx`'s own
helper of the same name. Unlike S-198's LeaderboardScreen tab, this one
ships with no "renders empty" gap — `GET /users/{userId}/stats` was
already fully wired for `"xg-predict"` by S-199/ADR-0100, confirmed by
both `architecture-reviewer` and `quality-architect` review passes (both
PASS, no findings) before this story was considered done. New/updated
Vitest coverage in `UserStatsScreen.test.tsx` (tab-list assertion, a
re-fetch-scoped-to-xg-predict test, three subtitle-branch tests). No REQ/
architecture-doc changes and no new ADR needed — same same-shaped-
extension precedent S-198 already established. `docs/design-document.md`'s
SCREEN-13 mock/copy updated to match (plus an incidental alignment fix to
the widened tab row). Verified locally: 768/768 Vitest passing, `tsc -b`
clean, lint clean (only pre-existing unrelated warnings) — no backend
touched, so no CI-trigger fallback needed.

**S-203 · Playwright E2E coverage for xG Predict (`play-predict.spec.ts`)**
No E2E spec exists for xG Predict, unlike `frontend/tests/e2e/play-grid.spec.ts`/
`play-path.spec.ts` — flagged as out of scope by S-197.
*Accept:* a new `frontend/tests/e2e/play-predict.spec.ts` mirrors the
existing two specs' structure (test-data seed/reset via
`/internal/test-data/*`, sign in, generate/seed a round) and covers, end to
end: viewing the round's 5-match slate, submitting a per-match prediction,
the round-wide-lock and per-player-lock notices (REQ-1303/1306), confirm-
and-lock, and — once S-199 is merged — the resulting score appearing on the
leaderboard. If run before S-199 merges, scope the leaderboard assertion
out and file it as this spec's own follow-up rather than blocking on it.
*Deps:* S-197 (already merged), S-199 (for the full leaderboard
assertion — see above).

*Built as (2026-09-02):* `frontend/tests/e2e/play-predict.spec.ts` added,
mirroring `play-grid.spec.ts`/`play-path.spec.ts`'s structure exactly
(serial mode, `clearAnyExistingActivePredictRound`/`seedPredictRound`
helpers, real-signup-through-the-UI flow). Since S-199 was already merged
by the time this story ran, the leaderboard assertion was included rather
than scoped out — one continuous test file covers REQ-1301 (5-match
slate), REQ-1302 (submission), REQ-1303 (round-wide lock notice via a new
`firstKickoffMinutesFromNow` seeding knob), REQ-1306 (per-player
confirm-and-lock, including cancel), and REQ-1304/1305/410 (a graded
prediction's score reaching the xG Predict leaderboard tab). Required two
new non-Production-only `/internal/test-data/*` endpoints in
`InternalRoundEndpoints.cs` — `seed-guessable-predict-round` and
`grade-predict-match/{matchId}` (bypasses `IFootballDataClient`/
`PredictGradingService`, calling `IPredictInstanceRepository.GradeMatchAsync`
directly, the same "no deterministic way to make a real external fixture
finish with a specific score" reasoning the seed endpoints already use to
bypass real generation logic) — plus a `CreateSequencedRoundAsync` helper
extracted from all three `seed-guessable-*` endpoints on this diff's third
occurrence (rule-of-three). `architecture-reviewer` found no boundary/ADR
gap (same-shaped extension of the already-ADR-0006-covered test-data
pattern to a third game). `quality-architect` found and fixed two issues
(missing not-found handling on `grade-predict-match`; the rule-of-three
duplication now extracted) and flagged a missing API test gap, filled by
7 new `RoundEndpointTests` cases (happy path, the lock-instant knob in
both directions, not-found, and the ADR-0006 Production-gate for both
endpoints). Closes the E2E gap REQ-1302/REQ-1303's own status notes in
`docs/requirements-document.md` had flagged. Built without a local
`dotnet` SDK in this sandbox — hand-traced, not locally run; CI
verification via `ci.yml`'s `workflow_dispatch` is required before this is
considered done, same recurring constraint as other recent backend
stories in this log.

**S-204 · Fix `"xg-predict"` round generation's incompatibility with irregular (e.g. midweek) gameweek spacing (needs ADR-0102)**
**Correction (2026-08-31): this story previously proposed defaulting
`RoundScheduling:XGPredict:RoundDurationHours` to 168h as a product-tuning
call. That was wrong, not just imprecise — flagged by the product owner
(real Premier League gameweeks are not always 7 days apart; midweek
rounds are routine around cup replays, European-competition weeks, and
rearranged fixtures) and confirmed by reading the actual generation code.
Rewritten below as a real fix, not a config tweak.**

Root cause, traced through the two files involved:
`RoundGenerationService.GenerateNextRoundIfNeededAsync`
(`backend/src/XGArcade.Core/Rounds/RoundGenerationService.cs`) chains
rounds strictly back-to-back and periodically — round N+1's `StartTime` is
always exactly round N's `EndTime` (`= StartTime + RoundDuration`), and
generation only fires once round N has itself started. This is a fixed
period, by construction, regardless of GameKey.
`XGPredictGameModule.GenerateInstanceAsync`
(`backend/src/XGArcade.Games.XGPredict/XGPredictGameModule.cs:46`) calls
`IFootballDataClient.GetUpcomingGameweekFixturesAsync` fresh on every
call, with **no tracking of which matchday a previous round already
used** — that client method itself is correctly real-world-driven (it
walks forward from the current matchday to the next one where every fixture
is still in the future, `FootballDataClient.cs`'s own comment on why), but
nothing stops two different Round-generation calls from resolving to the
same matchday, or a Round-generation call from landing after a
fully-in-the-future matchday has already slipped by.

Combined, no fixed `RoundDuration` value is safe: too long, and a
midweek gameweek's fixtures kick off before the chain gets around to
generating that round — the matchday is silently skipped, never played,
not merely delayed (`GetUpcomingGameweekFixturesAsync` has already moved
past it by the time generation runs). Too short, and the chain generates a
new round before the real upcoming matchday has changed — since there is
no dedup, this creates a duplicate `PredictInstance`/`Round` for the exact
same real matches. 48h (today's default, unchanged since S-196) and 168h
(this story's own original, wrong proposal) both fail for different real
gameweek spacings; there is no constant that doesn't.

*Accept:* a new ADR (ADR-0102 — 0100/0101 are taken by S-199/S-201) decides
how `"xg-predict"` round generation should actually be triggered so it
tracks real matchday changes instead of elapsed time — options to weigh,
not a foregone conclusion: (a) `XGPredictGameModule` records which
matchday/fixture set it already used (e.g. on `PredictInstance` or a new
field) and `GenerateInstanceAsync` returns "no new round due" when the
next upcoming matchday is unchanged from the latest existing instance,
requiring `RoundGenerationService`/`IRoundGenerationService` to gain a way
to no-op generation for a GameKey without treating that as a failure; or
(b) a GameKey-specific generation path for `"xg-predict"` outside the
shared periodic chain entirely, if forcing this into the existing
one-round-ahead/fixed-`RoundDuration` shape (built for xg-grid/xg-path's
arbitrary, non-real-world cadence) turns out to be the wrong fit rather
than a clean extension of it. Whichever shape is chosen, add test coverage
proving a midweek matchday (two real gameweeks close together) is neither
skipped nor duplicated, and that the ordinary weekly case still produces
exactly one round per gameweek. Update `RoundSchedulingOptionsResolverTests`'
`"xg-predict"` case and ADR-0072's amendment section to reflect whatever
`RoundDuration`'s role becomes once this lands (possibly unused for
`"xg-predict"` specifically, if option (b) above is chosen).
*Deps:* none.

**S-205 · ADR note: confirm `ScorePrediction` as `XGPredictScoringStrategy`'s permanent second entry point**
S-193's `architecture-reviewer` flagged `ScoreCorrectGuess` throwing
`NotSupportedException` for `"xg-predict"` as an awkward, if currently
unreachable, fit for `IScoringStrategy`'s shape, and left a standing item:
either confirm `ScorePrediction` as this `GameKey`'s permanent second entry
point via a short ADR note, or revisit `IScoringStrategy`'s shape itself
(ADR-0040's own follow-up precedent). No fourth game exists yet to force
the question, and `IScoringStrategy` has exactly two real implementations
today — reshaping the interface now would be speculative, not something
the codebase's current needs justify (see this file's own "don't pull
Tier 1 items forward" discipline, applied here to interface design rather
than features). Resolve it the cheap way: document the decision, don't
refactor working code with no second caller yet.
*Accept:* a short amendment to `docs/decisions/0095-xg-predict-scoring-direction-exception.md`
(or a new ADR if the reviewers judge the existing one doesn't fit)
recording that `ScoreCorrectGuess`/`ScorePrediction` is the deliberate,
permanent shape for `IScoringStrategy` until a third game actually needs a
third method shape — closing the standing item rather than leaving it open
indefinitely.
*Deps:* none.

**S-206 · Swap xG Predict's data source from API-Football to football-data.org (REQ-1301, REQ-1305, ADR-0099)**
Production incident, not a planned story: the first real deploy with a
configured API-Football key (2026-08-31) hit
`/internal/generate-round?gameKey=xg-predict` returning 500,
`"API-Football returned no current round name — check the
ApiFootball:LeagueId/Season configuration."` League ID and season were
confirmed correct against the account's own dashboard; the actual cause,
confirmed via api-football.com's own support chatbot, is that **API-Football's
free plan does not include the current season at all** — only a rolling
2-4-season historical window. ADR-0094's free-tier-sufficiency judgment was
never verified live (egress to api-football.com has been blocked from this
sandbox since before ADR-0094 shipped) and turned out to be wrong. See
ADR-0099 for the full reasoning and alternatives considered (paid
API-Football plan, pausing xG Predict, Sportmonks — ruled out, no Premier
League on its free tier — and football-data.org, chosen).

Replaced `DataSync.ApiFootball.ApiFootballClient`/`IApiFootballClient`
entirely with `DataSync.FootballData.FootballDataClient`/
`IFootballDataClient` (same two-method shape, same narrow/point-in-time
posture) — `XGPredictGameModule.GenerateInstanceAsync` and
`PredictGradingService.GradeReadyMatchesAsync` now depend on the new
client; `ServiceRegistration.AddFootballDataServices` replaces
`AddApiFootballServices`. Config simplifies as a direct consequence:
football-data.org's `GET /v4/competitions/{code}` response carries
`currentSeason.currentMatchday` directly, so `FootballDataOptions` needs
only a competition code (`"PL"`), never ADR-0094's separately-computed
season year. Infra: `apiFootballApiKey`/`API_FOOTBALL_API_KEY` replaced by
`footballDataApiKey`/`FOOTBALL_DATA_API_KEY` throughout
`deploy.yml`/`main.bicep`/`backend-container-app.bicep` — the same
conditional non-empty-secret pattern (Azure Container Apps rejects an
empty-string `secrets` entry, a bug found and fixed the same day this key
was first wired) carries over unchanged.

*Accept:* `dotnet test` passes with the new `FootballDataClientTests`
(replacing `ApiFootballClientTests`) covering the happy path, missing-
matchday/missing-currentSeason config errors, non-success HTTP status,
malformed JSON, network failure, missing required fixture fields, the
API-token header/URL, an unconfigured-token no-request guard, and
`GetFixtureResultAsync`'s FINISHED/AWARDED→Finished,
POSTPONED/CANCELLED/SUSPENDED→PostponedOrAbandoned,
SCHEDULED/IN_PLAY/PAUSED→NotYetConfirmed status mapping plus a 404→throws
case (football-data.org's real "unknown fixture" shape, unlike
API-Football's 200-with-empty-array). `XGPredictGameModuleTests`/
`PredictGradingServiceTests`/`RoundEndpointTests` pass unmodified in
behavior, using `FakeFootballDataClient` in place of the deleted
`FakeApiFootballClient`.

*Not addressed by this story, tracked in `TODO.md`:* football-data.org's
actual terms of service have not been read from this sandbox (egress
blocked, same as api-football.com) — only secondhand summaries via web
search, which were inconsistent about free-tier commercial-use terms. A
human with real network access must confirm before public launch. The
required "Football data provided by the Football-Data.org API" frontend
attribution is also not yet added.

*Built as (2026-08-31):* new `backend/src/XGArcade.DataSync/FootballData/`
(7 files, replacing the deleted `ApiFootball/` folder of the same shape);
`XGPredictGameModule.cs`/`PredictGradingService.cs`/`ServiceRegistration.cs`/
`appsettings.json` updated; `FakeFootballDataClient.cs` (Games.XGPredict.Tests,
replacing `FakeApiFootballClient.cs`) and the duplicated inline fake in
`RoundEndpointTests.cs` renamed/updated in place; `FootballDataClientTests.cs`
(DataSync.Tests, replacing `ApiFootballClientTests.cs`) rewritten against
football-data.org's v4 endpoint shapes. `infra/bicep/main.bicep`/
`backend-container-app.bicep`/`.github/workflows/deploy.yml`/
`infra/README.md` updated. New ADR-0099; ADR-0094 marked superseded (its
Decision items 1-2 specifically). `MVP-SCOPE.md`, `SETUP.md` (§4 reverted to
its original xG-Grid-Tier-1-only scope; new §4a for football-data.org),
`TODO.md`, `docs/requirements-document.md`, `docs/architecture-document.md`,
`docs/implementation-document.md` updated for the xG-Predict-specific
mentions only — every API-Football mention describing xG Grid's separate,
still-dormant Tier 1 fallback (ADR-0011/ADR-0012/ADR-0008) is untouched and
still accurate.

*Deps:* S-190 through S-198 (everything this replaces the data-source
layer under).

Built without a local `dotnet`/`az`/`bicep` SDK in this sandbox (this
repo's own standing constraint) — hand-traced against the deleted
`ApiFootballClientTests.cs`'s own coverage shape, not locally run. CI
verification via `ci.yml`'s `workflow_dispatch` is required before this is
considered done, same recurring constraint as every other recent backend
story in this log.

**Follow-up fix, same day (2026-08-31):** the first real round generated
against a working key was itself wrong — `currentSeason.currentMatchday`
returned the just-finished weekend's gameweek, locked (REQ-1303) before
any player could see it, since football-data.org can keep `currentMatchday`
pointing at a just-concluded gameweek for a while before advancing.
`FootballDataClient.GetUpcomingGameweekFixturesAsync` now takes a
`TimeProvider` and advances through a bounded lookahead
(`MaxMatchdayLookahead = 4`) until every fixture in a candidate matchday
has a still-future kickoff, rejecting a matchday with even one
already-started fixture. Five new `FootballDataClientTests` cases; see
ADR-0099's Decision item 3 status update and REQ-1301's own status note
for the full incident. No ToS/infra impact — pure client-side correctness
fix within the same file set S-206 already touched.

## Epic 27 — xG Connect

**Status (2026-09-02): promoted from Tier 2 to Tier 0** — see
`MVP-SCOPE.md`'s Tier 0 section. **S-207 is clear to start.** Full
requirements: `docs/requirements-document.md` §4.15 (REQ-1401-1411).
Architecture: `docs/architecture-document.md` COMP-16/COMP-17 (both
currently "proposed, not yet assigned" — S-207 is what resolves that).

**S-207 · ADR: xG Connect structural decisions**
Resolve the two open questions §4.15's component-boundary note flags:
(a) does Friends/Challenges become its own Core component (COMP-16)
separate from the game module (COMP-17), or one component; (b) does xG
Connect's pairwise, on-demand match fit the existing `Round`/`League`
model (COMP-02/COMP-03, ADR-0003) or need a new first-class concept. Use
`/new-adr`. Update `architecture-document.md`'s COMP-16/17 rows to reflect
the decision (drop "proposed"/"not yet assigned" once real). No
application code — this story is pure design and unblocks every story
below.
*Accept:* ADR merged; architecture-document.md matches it.
*Deps:* none (blocked only by the Tier 2 promotion gate above).

**S-208 · Data model & migrations**
Per S-207's decision, scaffold EF Core entities + migrations for:
`Friendship`/`FriendRequest` (REQ-1401), `Challenge` (REQ-1402),
`MatchmakingOptIn` (REQ-1403), `ConnectMatch` + target picks
(REQ-1404/1405), `ConnectChainStep` (REQ-1406/1407), `ConnectChatMessage`
(REQ-1410). Repositories only, no business logic yet, per
`coding-guidelines.md`.
*Accept:* migration applies cleanly; repository unit tests for basic CRUD.
*Deps:* S-207.

**S-209 · Friends list (REQ-1401)**
Send/accept/decline friend request endpoints + service logic.
*Accept:* `REQ1401_...`-named tests covering every Given/When/Then in
REQ-1401 (duplicate-pending rejection both directions, already-friends
rejection, self-request rejection, decline-then-resend).
*Deps:* S-208.

*Built as (2026-09-02):* `IFriendService`/`FriendService`
(`XGArcade.Core.Social`) plus `XGArcade.Api.Social.FriendEndpoints`
(`POST /friends/requests`, `.../{id}/accept`, `.../{id}/decline`,
`GET /friends/requests/pending`, `GET /friends`), matching the plan exactly.
Full `REQ1401_...`-named coverage in `FriendServiceTests.cs`/
`FriendEndpointTests.cs`. A same-diff quality-gate finding also extracted a
shared `RequestingUserResolver` helper (`XGArcade.Api.Auth`), deduplicating
four near-identical copies across `LeaderboardEndpoints.cs`/
`LeagueEndpoints.cs`/`FriendEndpoints.cs`/`AvatarEndpoints.cs`
(ADR-0084 rule-of-three) — mechanical cleanup, not part of REQ-1401 itself,
no ADR needed. REQ-1402/1403 (challenges, matchmaking) not started — S-210.

**S-210 · Direct challenge + random matchmaking (REQ-1402/1403)** — Built,
2026-09-02.
Challenge send/accept/decline (requires an existing friendship); random
matchmaking opt-in pool + 12-hour pairing sweep job. Both paths resolve
into a new `ConnectMatch`. `IChallengeService`/`ChallengeService` and
`IMatchmakingService`/`MatchmakingService` (`XGArcade.Core.Social`)
implement the send/accept/decline and opt-in logic; per ADR-0103 neither
ever writes a `ConnectMatch` row itself — `XGArcade.Api.Social.
ChallengeEndpoints`' accept handler and the new
`XGArcade.Api.Social.MatchmakingSweepService` do that orchestration in
`XGArcade.Api` instead. **Deliberately does NOT mirror
`sweep-recent-transfers.yml`'s CLI-verb pattern** despite this story's own
original wording — that pattern is reserved (ADR-0024) for long-running,
multiple-live-external-API-call work, which this fast, bounded,
pure-in-database sweep is not; it uses the bearer-token `/internal/*`
+ hourly-cron pattern instead (same shape as
`grade-predict-matches.yml`/`purge-guest-accounts.yml`) — see
`sweep-matchmaking-pairings.yml`'s own header comment for the full
reasoning.
*Accept:* `REQ1402_...`/`REQ1403_...`-named tests, including 12h expiry
with no pairing and no player double-booked into two matches from one
pairing event.
*Deps:* S-209.

**S-211 · Target-pick selection + trivial-pair rejection (REQ-1404)**
Independent, mutually-invisible target-pick endpoint; free resubmission
before the match officially starts; the direct-already-connected
rejection check once both picks are in. This is the first place the live
per-player career-overlap check gets built for xG Connect — reuse the
existing guess-time live-lookup pattern (ADR-0010/0011) against Wikidata
rather than inventing a new data path, and extract it as a shared
helper/service, since S-213 needs the identical check per chain step.
*Accept:* `REQ1404_...`-named tests, including the trivial-pair rejection
(and that the first player's pick survives a rejected second pick) and
free pre-lock resubmission.
*Deps:* S-210.

*Built as (2026-09-02):* `XGArcade.Games.XGConnect` scaffolded (COMP-17,
`XGConnectGameModule`, `GameKey = "xg-connect"`; only `PurgeUserDataAsync`
meaningfully implemented, every round-generation-shaped method throws
`NotSupportedException` per the `XGPredictGameModule` precedent), then
`IConnectTargetPickService`/`ConnectTargetPickService` implements REQ-1404
exactly per plan, plus a new shared `IPlayerCareerOverlapService`/
`PlayerCareerOverlapService` (deliberately player-ID-generic, not
`ConnectTargetPick`-shaped, so S-213 can reuse it unchanged) for the
direct-connection check, via `POST /matches/{matchId}/target-pick`
(`XGArcade.Api.Connect.ConnectMatchEndpoints`). Full `REQ1404_...`-named
coverage in `ConnectTargetPickServiceTests.cs`/
`PlayerCareerOverlapServiceTests.cs`/`ConnectMatchEndpointTests.cs`. Two
same-branch follow-up fixes: an architecture-review pass had
`PlayerCareerOverlapService` delegate to the shared
`IPlayerCareerStintRefreshService` (`XGArcade.DataSync`, ADR-0054, which
gained a `throwOnFailure` opt-in for this) instead of forking its
fetch/persist logic; a quality-review pass extracted a third verbatim
`FixedTimeProvider` copy into a new shared `XGArcade.TestSupport` project
(rule-of-three cleanup, unrelated to REQ-1404 itself). No new ADR — the
`PlayerCareerOverlapService` placement was reviewed and judged a
straightforward application of `Games.XGPath`'s existing
`DataSync`-dependency precedent, not a new structural decision.

**S-212 · Match start, 6-hour timer, resolution scaffolding (REQ-1405)**
Match officially starts once both picks are locked; independent per-player
6-hour deadline; forfeit-on-timeout sweep job; resolution waits for both
players' terminal state but doesn't wait out an unused remainder of the
window once both are reached.
*Accept:* `REQ1405_...`-named tests per its Given/When/Then.
*Deps:* S-211.

*Built as (2026-09-03):* `IConnectMatchLifecycleService`/
`ConnectMatchLifecycleService` (`XGArcade.Games.XGConnect`).
`StartMatchIfBothPicksLockedAsync` re-confirms via
`IConnectMatchRepository.GetTargetPicksForMatchAsync` that both target
picks are locked, then transitions `ConnectMatch.Status` to `Active` with
`StartedAt = now` and `DeadlineUtc = StartedAt + 6h` — called from
`ConnectTargetPickService.SubmitTargetPickAsync`'s completing-pick branch,
right after `LockTargetPicksForMatchAsync`, so the match starts the
instant the second target pick locks. `RunForfeitSweepAsync` finds
`Active` matches past `DeadlineUtc`
(`IConnectMatchRepository.GetActiveMatchesPastDeadlineAsync`), marks each
not-yet-terminal player slot as timed out independently and idempotently
via two new nullable `ConnectMatch` columns, `PlayerATimedOutAt`/
`PlayerBTimedOutAt` (slot-based rather than `UserId`-keyed, since
`PlayerAUserId`/`PlayerBUserId` go null after REQ-710 anonymization), added
by a new hand-authored migration
`20260903120000_AddConnectMatchTimeoutTracking` — and, if both slots are
terminal after that same pass, resolves the match immediately to
`ConnectMatchOutcome.Draw` in that same sweep call. Deliberately only
handles the both-timed-out case; REQ-1409's mixed-outcome resolution (one
player times out while the other legitimately completes a chain or busts)
is explicitly out of scope, reserved for S-213/S-214 since none of
REQ-1406-1409's chain-step submission/bust/scoring logic exists yet. The
sweep's only trigger is a new bearer-token-gated endpoint, `POST
/internal/sweep-connect-forfeits`
(`XGArcade.Api.Connect.InternalConnectForfeitSweepEndpoints`, mirroring
`InternalMatchmakingSweepEndpoints.cs` exactly), called hourly by a new
`.github/workflows/sweep-connect-forfeits.yml` (same curl+retry-loop shape
as `sweep-matchmaking-pairings.yml`; hourly for the same
"up-to-1h-late has no correctness impact at this granularity" reasoning,
scaled to a 6h window instead of 12h). Full `REQ1405_...`-named coverage in
`ConnectMatchRepositoryTests.cs` (extended), a new
`ConnectMatchLifecycleServiceTests.cs`, `ConnectTargetPickServiceTests.cs`
(extended), and a new `InternalConnectForfeitSweepEndpointTests.cs`.
Quality gate found no boundary violations and no ADR needed — both the
slot-based timeout tracking and the both-timeout-resolves-to-Draw behavior
are direct, requirement-mandated implementations of already-accepted
REQ-1405/REQ-1409 text, not new structural decisions.

**S-213 · Incremental chain submission + live per-step validation (REQ-1406)**
Chain-step submission endpoint reusing S-211's career-overlap helper;
candidate search wired to the existing broad `PlayerNameIndex` (COMP-10),
never the curated club/country reference tables (mirrors REQ-207's
autocomplete/correctness separation, ADR-0007); chain-closing detection
against the OTHER target pick, not the one the chain started from.
*Accept:* `REQ1406_...`-named tests: valid overlapping-time step accepted,
non-overlapping-period rejection, never-played-for-that-club rejection,
closing-step detection, candidate search returns players outside the
curated reference tables.
*Deps:* S-212.

*Built as (2026-09-03):* `IPlayerCareerOverlapService` gained
`HaveOverlapAtClubAsync` (shares its fetch-once/live-refresh plumbing with
the existing `HaveSharedClubOverlapAsync` via a new private
`LoadBothPlayersStintsAsync` helper — no behavior change to the existing
method). New `IConnectChainStepService`/`ConnectChainStepService`
implements REQ-1406's per-step submission: resolves the candidate name via
the same COMP-06 `IPlayerRepository.GetPlayersByNormalizedFullNameAsync`
path `GridNameMatcher` uses (never `PlayerNameIndex`/COMP-10, per
ADR-0007), runs the claimed-club check, and — only once that passes —
checks chain-closing against the OTHER target pick via the existing,
unmodified `HaveSharedClubOverlapAsync`. `ConnectChainStep` gained a
`ClosesChain` column (migration
`20260903130000_AddConnectChainStepClosesChain`). Exposed as `POST
/matches/{matchId}/chain-steps`
(`XGArcade.Api.Connect.ConnectChainStepEndpoints`), mirroring
`GuessEndpoints`'s "a wrong answer is a normal 200, not an error" shape —
only match-not-found/not-a-participant/not-active/chain-already-complete
and live-lookup-unavailable get non-200 statuses. This story does NOT
enforce a cap on invalid attempts per position (REQ-1407/S-214's job).
Full `REQ1406_...`-named coverage in `PlayerCareerOverlapServiceTests.cs`,
new `ConnectChainStepServiceTests.cs`, new
`ConnectChainStepEndpointTests.cs`, and one addition to
`PlayerAutocompleteEndpointTests.cs` proving candidate search stays on the
existing broad search with zero `ClubDefinition`/`CountryDefinition` rows
seeded. No new ADR — same `Games.XGPath`-precedent reasoning S-211's own
entry already covers for `PlayerCareerOverlapService`'s placement.

**S-214 · Penalty/bust rule, scoring, match resolution (REQ-1407/1408/1409)**
Two-strikes-per-step tracking (independent per chain position), scoring
formula (connections + accumulated penalties, min 1), win/draw/forfeit
resolution once both players reach a terminal state.
*Accept:* `REQ1407_...`/`REQ1408_...`/`REQ1409_...`-named tests per their
Given/When/Then.
*Deps:* S-213.

*Built as (2026-09-03):* `ConnectChainStepService.SubmitChainStepAsync`
(`XGArcade.Games.XGConnect`) enforces REQ-1407 inline with its existing
per-step validation — a second, consecutive failure at the same chain
position calls a new, idempotent `IConnectMatchRepository.
MarkPlayerBustedAsync` (mirroring `MarkPlayerTimedOutAsync`'s own `??=`
semantics, new nullable `ConnectMatch.PlayerABustedAt`/`PlayerBBustedAt`
columns) and returns a new `SubmitChainStepOutcome.Busted` (`200 OK`,
`SubmitChainStepResponse.Busted: true`), distinct from an ordinary
`InvalidStep`; a new `AlreadyForfeited` precondition (`409 Conflict`)
rejects any submission from a caller whose own slot already busted or
timed out — closing a real pre-existing gap, since `ConnectMatch.Status`
only flips to `Resolved` once BOTH players are terminal, so such a player
could otherwise keep submitting steps for as long as the opponent hadn't
finished. New `IConnectScoringService`/`ConnectScoringService` (pure,
stateless) implements REQ-1408: `score = Math.Max(1, validStepCount +
firstAttemptFailureCount)`. `ConnectMatchLifecycleService` gained
`TryResolveMatchIfBothTerminalAsync`, implementing REQ-1409: a shared
private `ResolveIfBothTerminalAsync` helper (used by both this new method
and `RunForfeitSweepAsync`) converges all three terminal paths (timeout,
bust, chain completion — the latter detected via a new shared
`ConnectChainStepExtensions.HasClosedChain()` extension) into a
resolution decision — both-completed compares scores (lower wins, equal
draws), one-completed-one-forfeited is an outright win for the completer
with no minimum score, both-forfeited is always a draw; called from
`ConnectChainStepService` right after a bust or a chain-close, so
resolution is never deferred to a later pass. `RunForfeitSweepAsync`'s own
sweep loop was corrected in the same story: it previously marked BOTH
slots timed-out unconditionally once the shared 6h deadline passed, which
was wrong once bust/completion existed as terminal paths — it now checks
each slot's already-terminal state first, which is what makes the
mixed-outcome case (one player times out while the other already
busted/completed) resolve correctly. `ConnectMatch.PlayerAScore`/
`PlayerBScore` are persisted in the same `ResolveMatchAsync` write as
`Outcome`/`ResolvedAt`, null for a forfeiting player. New migration
`20260903140000_AddConnectMatchBustAndScoreTracking` adds the four new
columns. Full `REQ1407_...`/`REQ1408_...`/`REQ1409_...`-named test
coverage across `ConnectChainStepServiceTests.cs`,
`ConnectChainStepEndpointTests.cs`, `ConnectMatchLifecycleServiceTests.cs`,
`ConnectMatchRepositoryTests.cs`, and a new `ConnectScoringServiceTests.cs`.
Quality gate found one duplication issue (the chain-completion check was
being re-derived at multiple call sites), fixed by extracting
`ConnectChainStepExtensions.HasClosedChain()` in a same-story follow-up
commit (no behavior change). No new ADR — same "straightforward,
requirement-mandated implementation of already-accepted REQ text"
reasoning S-211/S-212/S-213's own entries above already used for this
component, confirmed by `architecture-reviewer` against this story's own
diff.

**S-215 · In-match chat (REQ-1410)**
Send/read chat scoped to one match; participant-only access; chat
persists and stays readable after the match ends.
*Accept:* `REQ1410_...`-named tests.
*Deps:* S-212 (does not need S-213/S-214's chain-scoring logic).

*Built as (2026-09-03):* New `IConnectChatService`/`ConnectChatService`
(`XGArcade.Games.XGConnect`) layers send/read on top of the existing S-208
`IConnectChatMessageRepository` and `IConnectMatchRepository`
(participant check only). Deliberately does not gate on
`ConnectMatch.Status` — REQ-1410's Given/When/Then never makes match
status a precondition for sending or reading, and one clause explicitly
requires chat to remain readable once a match has resolved. Exposed as
`POST`/`GET /matches/{matchId}/chat-messages`
(`XGArcade.Api.Connect.ConnectChatEndpoints`), same thin-endpoint pattern
as `ConnectChainStepEndpoints`/`ConnectMatchEndpoints` — `MatchNotFound`
→ 404, `NotAParticipant` → 403 Problem. Also closed the REQ-710
anonymization gap S-208/S-214's own doc comments had flagged: new
`IConnectChatMessageRepository.AnonymizeSenderAsync` (load-then-save,
mirrors `ConnectMatchRepository.AnonymizeUserDataAsync`'s own shape) is
now injected into and called from `XGConnectGameModule.PurgeUserDataAsync`
alongside the existing `IConnectMatchRepository.AnonymizeUserDataAsync`
call, so a deleted user's `ConnectChatMessage.SenderUserId` rows are
anonymized too, not just `ConnectMatch`/`ConnectTargetPick`/
`ConnectChainStep`. No schema migration — `ConnectChatMessage` already
existed (S-208). No new ADR — same "straightforward, requirement-mandated
implementation of already-accepted REQ text" reasoning S-211 through
S-214's own entries already used for this component. A follow-up commit
the same story (`d895c1a`, 2026-09-03) satisfied this story's
`REQ1410_...`-named tests accept criterion: `ConnectChatServiceTests.cs`
and `ConnectChatEndpointTests.cs`, plus `REQ710_...`-named coverage of
`AnonymizeSenderAsync` in an extended `ConnectChatMessageRepositoryTests.cs`
and `XGConnectGameModuleTests.cs`. Two further same-story quality-gate
follow-ups (2026-09-03): a pure refactor (`5b535c2`, no behavior change)
extracted the four-times-duplicated match-lookup/participant-check shape
(across `ConnectTargetPickService`, `ConnectChainStepService`, and both
`ConnectChatService` methods) into a new
`ConnectMatchAccessExtensions.ResolveParticipantMatchAsync`, mirroring
`ConnectChainStepExtensions.cs`'s own placement/naming convention from
S-214's rule-of-three extraction; and a real behavior addition (`a142c43`,
test coverage in `71dc730`) rejects a null/empty/whitespace-only
`MessageText`, or one over `MaxMessageLength = 1000` trimmed characters,
with a `400` Problem response, and trims the message before persisting —
not required by REQ-1410's own Given/When/Then, but bringing this endpoint
in line with the blank/max-length validation convention every other
free-text endpoint already applies (`GuessEndpoints`,
`AdminAnnouncementBannerEndpoints`, `LeagueEndpoints`). No new ADR for
either follow-up — the extraction is behavior-preserving (same reasoning
as `ConnectChainStepExtensions.HasClosedChain()` needing none in S-214),
and the validation addition enforces an already-established convention
rather than making a new structural choice.

**S-216 · Notification indicator, backend (REQ-1411)**
Aggregate endpoint for the current user: pending friend requests +
pending challenges + matches awaiting their own next move (no target pick
submitted yet, or an in-progress, non-terminal chain). Excludes an
unpaired matchmaking opt-in (nothing actionable yet).
*Accept:* `REQ1411_...`-named tests: combined presence across all three
categories, zero once every contributing item resolves, unpaired opt-in
excluded.
*Deps:* S-209, S-210, S-212 (needs all three pending-item sources to exist).

*Built as (2026-09-03):* `GET /notifications/summary`
(`XGArcade.Api.Notifications.NotificationEndpoints`), aggregating
`IFriendService.GetPendingFriendRequestsAsync`,
`IChallengeService.GetPendingChallengesAsync`, and a new
`IConnectMatchLifecycleService.GetMatchesAwaitingActionAsync`
(`XGArcade.Games.XGConnect`) — the last layered on a new
`IConnectMatchRepository.GetOpenMatchesForUserAsync` (participant + `Status
!= Resolved` candidate set) plus the same per-slot bust/timeout/
`ConnectChainStepExtensions.HasClosedChain` terminal check
`ConnectMatchLifecycleService`'s forfeit-sweep/resolution methods already
use, evaluated for the caller's own slot only (the other participant's
terminal state does not affect whether a match is still "awaiting my
move"). Response (`NotificationSummaryResponse`) carries per-category
counts plus a combined `HasPending` flag. Full `REQ1411_...`-named test
coverage (`NotificationEndpointTests.cs`, plus extended
`ConnectMatchLifecycleServiceTests.cs`/`ConnectMatchRepositoryTests.cs`)
landed in a same-story `test-writer` follow-up commit (`cc93715`,
2026-09-03), not included in the commit above.

**S-217 · Frontend: friends/challenges/matchmaking screens**
New `design-document.md` SCREEN entries (via the `frontend-design`
skill/`ui-implementer`) for the friends list, send/accept/decline UI,
challenge flow, matchmaking opt-in, and the header-nav notification badge
from S-216 (exact visual treatment — count vs. presence dot — is a design
decision here, deliberately left open by REQ-1411 itself).
*Accept:* Vitest coverage; manual browser check per `CLAUDE.md`'s
UI-testing rule.
*Deps:* S-216.

*Built as (2026-09-03):* New `docs/design-document.md` SCREEN-15 "Friends &
Challenges" (via `frontend-design`/`ui-implementer`), with updates to
SCREEN-07 (header-nav badge, resolving REQ-1411's count-vs-presence-dot
decision in favor of a count) and SCREEN-13 (a "Send friend request" entry
point, since no user-search-by-name endpoint exists — friending always
starts from a player already visible somewhere, e.g. a leaderboard row).
New `frontend/src/social/FriendsScreen.tsx`, reached from a new "Friends"
`HeaderNav` entry, with three tabs: `FriendsTab` (friends list, pending
incoming requests, and — per SCREEN-15's own framing that the friends list
is where you'd challenge a friend — a "Challenge" button per friend,
REQ-1402), `ChallengesTab` (pending incoming challenges, accept/decline;
accepting only shows an honest "Match started!" acknowledgment banner,
never navigating into gameplay, which stays S-218's separate scope), and
`MatchmakingTab` (REQ-1403's one-shot opt-in — no listing endpoint exists,
so its "in the pool until…" status is session-local only, not fetched).
`SendFriendRequestAction` is reused from both `FriendsTab`'s incoming-
request rows and the new `UserStatsScreen` entry point above. New
`frontend/src/lib/{friends,challenges,matchmaking,notifications}.ts`
(typed fetch wrappers) and `useNotificationSummary.ts` (a 15s
self-rescheduling poll of `GET /notifications/summary`, mirroring
`AllTimeLeaderboard.tsx`'s own poll shape, mounted once in `App()` so
`HeaderNav`'s badge stays current regardless of which screen is showing).
The badge itself resolved REQ-1411's deliberately-open design question as
a combined count (not a presence dot) rendered inline as "Friends (N)",
omitted entirely at 0 — the same inline "(N)" convention `AdminScreen`'s
existing pending-count sections already use. Two same-story quality-gate
follow-up commits
(`7a9db40`, `52a7ee7`, 2026-09-03, no behavior change): the first
extracted two shapes duplicated past the rule-of-three threshold
(ADR-0084) — `useSubmitAction.ts` (submit/error/onAuthError, mirroring
`useAuthedFetch.ts`'s mount-fetch shape for the user-triggered-submit
case; five call sites) and `FetchListSection.tsx` (loading/error/empty/
list render shape, scoped to `/social`'s own CSS classes; three call
sites), both with direct unit coverage added alongside the existing
per-component tests; the second fixed a copy-paste component-id slip in a
code comment (`useSubmitAction.ts` cited COMP-06 instead of COMP-16). No
new ADR — same "straightforward, requirement-mandated implementation of
already-accepted REQ text" reasoning S-211 through S-216's own entries
already used for this component, confirmed by `architecture-reviewer`
against this story's own diff, which also confirmed the count-vs-dot
badge decision and the no-user-search "start from a player's stats page"
decision are both correctly scoped as `design-document.md`-level
decisions, not structural ones.

**S-218 · Frontend: match/gameplay screen**
Target-pick selection UI, chain-builder UI (candidate search, club claim,
live validation feedback, penalty/bust states), match resolution screen,
in-match chat UI.
*Accept:* Vitest + Playwright E2E covering a full match happy path
(challenge → both picks → chain to completion → resolution); manual
browser check.
*Deps:* S-214, S-215, S-217.

**Backend read-side prep (2026-09-03):** while preparing this story's
handoff, found every existing xG Connect endpoint (`ConnectMatchEndpoints`/
`ConnectChainStepEndpoints`/`ConnectChatEndpoints`) was write-only — there
was no way for this screen to read a match's current state, or even
discover which `matchId`s belong to the caller. Closed that gap first, as
a natural read-side extension of already-built REQ-1404/1405/1406/1409/
1411 behavior (no new REQ/ADR): `GET /matches` and `GET /matches/{matchId}`
(`XGArcade.Api.Connect.ConnectMatchQueryEndpoints`, backed by a new
`IConnectMatchQueryService`/`ConnectMatchQueryService` in
`XGArcade.Games.XGConnect`). See each of those REQs' own "Read-side
addendum" status notes in `requirements-document.md` §4.15 for the exact
shapes.

**Built as (2026-09-03):** new `docs/design-document.md` SCREEN-16 "xG
Connect match/gameplay" — a fourth "Matches" tab on `FriendsScreen.tsx`
(SCREEN-15), not a new top-level header-nav entry or App-level Screen/hash
route (a match has no deep-linking requirement in REQ-1404-1411, so the
drill-down between the matches list and one match's detail is
component-local state). New `frontend/src/connect/`: `MatchesTab.tsx`
(list, from `GET /matches`), `MatchScreen.tsx` (single-match container,
polling `GET /matches/{matchId}` every 15s while unresolved, driving
whichever sub-screen matches `status`), `TargetPickPanel.tsx` (REQ-1404,
including the trivially-connected-rejection retry flow), `ChainBuilder.tsx`
(REQ-1406/1407, incremental submission with live feedback and the
two-strikes/bust terminal state), `ChainStepsList.tsx` (shared chain render,
used by `ChainBuilder` and `MatchResolution.tsx`), `MatchResolution.tsx`
(REQ-1408/1409), `MatchChat.tsx` (REQ-1410, 15s-polled, gated on nothing),
and `PlayerSearchField.tsx` (the shared debounced autocomplete input behind
both the target-pick and chain-step candidate searches, REQ-1406's own
search-pattern precedent — built shared from the start since, unlike
`GuessInput.tsx`/`PathGuessInput.tsx`, both call sites live in this same
new feature area). New `frontend/src/lib/connectMatches.ts` (typed fetch
wrappers for all six xG Connect endpoints) and six new response types in
`frontend/src/lib/types.ts`. `shortUserId.ts` gained
`shortUserIdOrDeleted()`, a nullable-safe wrapper — this screen's
`opponentUserId`/`senderUserId` fields go null post-REQ-710 anonymization,
unlike SCREEN-15's own response shapes. `ChallengesTab`'s post-accept
banner and `MatchmakingTab`'s opted-in status both gained a "View your
matches" link switching to the new tab, closing the gap S-217's own entry
explicitly left open. See each affected REQ's own "Status note (S-218 —
frontend built)" in `requirements-document.md` §4.15 for exact behavior,
and SCREEN-16 itself for the three deliberately-flagged limitations (no
live countdown timer, a 15s-polled rather than live-pushed match/opponent
state, no invalid-attempt history in the chain view — only the current
valid chain). Vitest: 42 new tests across the eight new `connect/`
components plus updates to `FriendsScreen.test.tsx`/`ChallengesTab.test.tsx`/
`MatchmakingTab.test.tsx` for the new tab/links — full suite (868 tests)
green, `tsc -b` clean, `oxlint` clean (pre-existing, unrelated warnings
only). Manual browser check (2026-09-03): real Chromium (not jsdom) against
the Vite dev server with mocked API responses (no local backend available
in this sandbox — see this doc's own dotnet/Docker-unavailability
precedent), driving the full golden path — matches list → target-pick
search/submit → simulated match-start → chain-step submission → chat send
→ (separately) challenge-accept banner → "View your matches" link →
resolved-match summary with a completed chain — all rendered correctly
against the token system, screenshots reviewed. **Playwright E2E against a
real backend is explicitly NOT included in this pass** — `test-writer` owns
that per this story's own handoff instructions; the UI was built with
stable, role/label-queryable controls for that purpose (see each
component's own accessible labels: "Target player name," "Candidate player
name," "Claimed shared club," "Chat message," "View match," "Set target
pick," "Submit connector," "Send message").

**E2E coverage landed (2026-09-03, `test-writer`):**
`frontend/tests/e2e/play-connect.spec.ts` — one continuous playthrough
(REQ-1402/1404/1405/1406/1408/1409/1410) covering exactly this story's own
accept criterion: challenge send/accept -> both target picks -> chain to
completion (both players) -> resolution, plus bonus in-match chat coverage
given the same fixture. This is the first spec in this repo needing TWO
independent, simultaneously authenticated Playwright sessions
(`browser.newContext()` per player) rather than one — every other spec in
this directory drives a single-player round. Friending (REQ-1401) is seeded
directly via the real API (already has its own full FriendServiceTests.cs/
FriendEndpointTests.cs coverage) rather than driven through the UI's
stats-page-only entry point, which would need an unrelated round/leaderboard
detour first; challenge send/accept and everything gameplay-related is
driven through the real UI. A new environment-gated test-data endpoint,
`POST /internal/test-data/seed-connect-players`
(`XGArcade.Api.Connect.InternalConnectTestDataEndpoints`, same
non-Production-only pattern as `InternalRoundEndpoints`'s three
seed-guessable-*-round endpoints), seeds two `PlayerCareerStint`-backed
target players that are NOT trivially connected plus one connector that
closes either target's one-step chain — deterministic and hermetic, no live
Wikidata reachability needed.

**Real bug found and flagged, not silently fixed here:** `TargetPickPanel.tsx`
submits `/players/autocomplete`'s (COMP-10, `PlayerNameIndex`) own
suggestion `playerId` as `POST /matches/{matchId}/target-pick`'s
`targetPlayerId`, but `ConnectTargetPickService`/`PlayerCareerOverlapService`
resolve that id against `PlayerCareerStint`/`Player` (COMP-06) — a different,
unreconciled id space per `PlayerNameIndex.PlayerId`'s own doc comment
(ADR-0007). For real, Wikidata-imported players these ids will practically
always differ, so a target pick selected via the real autocomplete UI does
not reliably resolve to the intended player's own career data today. The new
seed endpoint works around this (for its own test-only players only) by
seeding a `PlayerNameIndex` row with `PlayerId` deliberately set equal to the
real `Player.Id`, documented prominently in that endpoint's own top-of-file
comment as a workaround, not a fix. **Needs a real follow-up story** —
likely resolving the target pick by name server-side, mirroring
`ConnectChainStepService.SubmitChainStepAsync`'s own
`IPlayerRepository.GetPlayersByNormalizedFullNameAsync` pattern, rather than
trusting a client-supplied id from a different component's id space.
API-level coverage for the new seed endpoint itself lives in
`InternalConnectTestDataEndpointTests.cs` (`XGArcade.Api.Tests`). Not run
locally against a live backend — this sandbox has neither a `dotnet` SDK nor
a reachable Docker daemon (`docker info` fails: no daemon socket) — `tsc -b`
and `oxlint` are clean and the full Vitest suite (868 tests) still passes;
a `ci.yml` `workflow_dispatch` run is needed to verify the Playwright spec
and the two new backend tests for real.

**Backend half of that bug fixed (2026-09-03, `backend-implementer`):**
`POST /matches/{matchId}/target-pick` now takes `{ targetPlayerName: string }`
instead of `{ targetPlayerId: Guid }`, resolved server-side inside
`ConnectTargetPickService.SubmitTargetPickAsync` via
`IPlayerRepository.GetPlayersByNormalizedFullNameAsync` — the exact
follow-up this section flagged, mirroring `ConnectChainStepService`'s own
`candidatePlayerName` resolution (lowest-`Id`-wins on a same-name
collision, same deliberate simplification). New
`SubmitTargetPickOutcome.TargetPlayerNotFound` maps to a 404 problem-details
response. See REQ-1404's own "Bug fix" status note in
`requirements-document.md` for the full detail.
`ConnectTargetPickServiceTests.cs`/`ConnectMatchEndpointTests.cs` updated to
seed real `Player` rows and submit names, plus new tests for
`TargetPlayerNotFound` and the collision case.
`InternalConnectTestDataEndpoints.cs`'s own seed endpoint is UNCHANGED for
now — it still seeds the `PlayerNameIndex`-alignment workaround, because
**the frontend half is still outstanding**: `TargetPickPanel.tsx`/
`frontend/src/lib/connectMatches.ts` still submit the autocomplete
suggestion's `playerId`, which no longer matches this endpoint's new
request shape at all (it will fail every real submission until updated) —
that frontend update is the very next task. No `dotnet` SDK in this
sandbox; hand-traced against the existing service/endpoint tests' own
assertions rather than run — a `ci.yml` `workflow_dispatch` run is needed
to confirm.

**Frontend half fixed (2026-09-03, `ui-implementer`):** `TargetPickPanel.tsx`
now submits the selected autocomplete suggestion's `name`, not its
`playerId` — same precedent `ChainBuilder.tsx`'s candidate search already
used, which never had this bug. `submitConnectTargetPick`
(`frontend/src/lib/connectMatches.ts`) takes `targetPlayerName` and sends
`{ targetPlayerName }`; no dedicated frontend request type existed to
rename (the body was always constructed inline). The panel's new 404
"Target player not found" case is handled inline exactly like the existing
409 trivially-connected rejection: shows the server's own detail text,
clears the field, does not call `onSubmitted`. No control names changed
("Target player name," "Set target pick") so `test-writer`'s
`play-connect.spec.ts` selectors are unaffected. `TargetPickPanel.test.tsx`
updated (new request-shape assertion plus a new not-found test case). Full
suite green: `tsc -b` clean, `oxlint` clean (same pre-existing unrelated
warnings only), Vitest 67 files / 869 tests passed.

**Re-verified post-fix (2026-09-03, `test-writer`):** confirmed
`play-connect.spec.ts`'s target-pick step (type into the search field,
click the matching `role="option"`, click "Set target pick") needed zero
interaction changes — the fix is internal to what gets submitted (name,
not id), not how the control is driven, and `PlayerSearchField.tsx`'s
control roles/labels are unchanged. Only comments needed updating: this
section's own earlier "workaround, not a fix" language, and matching
language in `play-connect.spec.ts` and
`InternalConnectTestDataEndpoints.cs`'s top-of-file/inline comments, no
longer described a live bug now that both halves are fixed. Updated all
three to say plainly that `PlayerNameIndex` seeding there is still needed
(so `/players/autocomplete` has a suggestion for the UI's
required-selection step to work through) but the `PlayerId` value on those
rows is no longer load-bearing, since resolution is now by name. Did not
touch `ConnectTargetPickServiceTests.cs`/`ConnectMatchEndpointTests.cs`/
`TargetPickPanel.test.tsx` — those already cover the new contract per the
two entries directly above. No `dotnet` SDK or reachable Docker daemon in
this sandbox (same precedent as above): `tsc -b` clean, `oxlint` clean, the
two touched Vitest files (`TargetPickPanel.test.tsx`, 7/7) pass, and
`npx playwright test play-connect.spec.ts --list` parses/type-checks the
spec correctly — the spec itself was not executed against a real backend.

**Real bug caught by the E2E spec's first CI run (2026-09-03,
`ui-implementer`):** `play-connect.spec.ts`'s first actual run against a
real backend failed at `getByText('Awaiting target picks')` right after
User B accepts a challenge and clicks "View your matches" — the match
User B just got paired into wasn't in the list. Root cause:
`FriendsScreen.tsx` mounted all four tab panels at once, toggling
visibility with `hidden={activeTab !== value}`, so none of them unmount
when their tab isn't active — a deliberate, correct convention for
Friends/Challenges/Matchmaking (their data doesn't change out from under
them while hidden), but wrong for `MatchesTab`, which uses
`useAuthedFetch` (fetch-on-mount only, no refetch-on-visibility). Since
`MatchesTab` mounts the instant `FriendsScreen` first mounts — typically
before any match exists — its `GET /matches` response was captured once,
empty, and never refreshed; the later "View your matches" click (just
`setActiveTab('matches')`) landed on that same stale, already-mounted
component. **Fixed** by making the Matches tab's content
(`selectedMatchId ? <MatchScreen> : <MatchesTab>`) truly conditionally
rendered — mounted only while `activeTab === 'matches'` — instead of kept
alive under a `hidden` div, so it remounts and refetches every time the
user switches to it. `handleViewMatches` needed no change: its
`setSelectedMatchId(null); setActiveTab('matches');` batch into one
render, and on that render `MatchesTab` mounts fresh and fetches, exactly
as needed. Friends/Challenges/Matchmaking's own mount-once/`hidden`
behavior is untouched — this is a deliberate exception for Matches only,
documented in `design-document.md` SCREEN-16 (version 0.89). New
regression test in `FriendsScreen.test.tsx` returns a different match
list on the second `GET /matches` call and asserts the second render
actually reflects it after switching away and back — the exact case that
slipped through the first time, since the earlier "Matches tab" test only
re-verified first-mount behavior. Full suite green: `tsc -b` clean,
`oxlint` clean (same pre-existing unrelated warnings only), Vitest 67
files / 870 tests passed (one new regression test added to
`FriendsScreen.test.tsx`, bringing it from 4 to 5).

**Real bug caught by the E2E spec's second CI run (2026-09-03,
`test-writer`) — this one is in the spec, not the product:** CI run #2
(after the Matches-tab staleness fix above) failed at
`submitTargetPick`'s own internal `expect(page.getByText('Your target:
${name}')).toBeVisible()` assertion. Diagnosis: that text only exists in
`TargetPickPanel.tsx`'s `myTargetPick?.locked` branch, but
`ConnectTargetPickService.SubmitTargetPickAsync` only flips `locked` true
on BOTH rows atomically, together, on the SECOND (completing) submission —
never individually for the first submitter. So neither player's browser
ever actually renders that branch: the first submitter (User A) stays on
the still-unlocked form (`myTargetPick` set but not locked), which shows
"Current pick: `<name>` — you can change it until your opponent also
picks." instead; the second/completing submitter (User B) has their match
flip to `Active` in the very same request, so their own post-submit
refetch already swaps `MatchScreen.tsx` over to `ChainBuilder` before
`TargetPickPanel` would ever re-render into its locked state. The
`locked` branch's "Your target: ..."/"Waiting for your opponent to lock in
their target pick…" text is therefore unreachable in the browser for
either player today — arguably dead code in `TargetPickPanel.tsx`, but
harmless/unreachable rather than incorrect, and left alone here (a
`quality-architect`/`ui-implementer` call, not this spec's). **Fixed**:
removed the shared `submitTargetPick` helper's internal post-submit
assertion entirely (it can't be correct for both call sites, since the two
players' post-submit UI genuinely differs) and asserts what each player's
screen actually shows at its own call site instead — User A now checks for
`` `Current pick: ${name}` `` (confirmed via `TargetPickPanel.tsx` that
Playwright's `getByText` concatenates a matched element's own text nodes,
including ones split across an inline `<strong>`, so a plain substring
across that boundary matches correctly with no regex needed) plus the
"you can change it until your opponent also picks." text; User B's
existing "Build your chain" assertion (already correct) was left
unchanged. Re-checked every other text assertion in the spec
(`MatchesTab.tsx`/`MatchResolution.tsx`/`ChainBuilder.tsx`/
`MatchChat.tsx`) directly against each component's own source rather than
by inference — no other assertion rides on the same
locked-branch-is-unreachable misunderstanding. No `dotnet` SDK or
reachable Docker daemon in this sandbox: `npx playwright test --list` and
`tsc --noEmit` both clean; the spec was not executed against a real
backend — a `ci.yml` `workflow_dispatch` run is needed to confirm this
run #2 failure is actually resolved.
