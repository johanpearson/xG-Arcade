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

**S-017 · Enforce display-name uniqueness (REQ-401/701)**
Add a case-insensitive uniqueness constraint on `User.DisplayName` (DB
unique index + a clear signup-time error, not a generic failure) — spaces
remain allowed, this only closes the uniqueness gap, not a username-style
format change. Update REQ-701's acceptance criteria to state the
uniqueness requirement explicitly.
*Accept:* REQ701-named test: signup with an already-used display name (any
casing) is rejected with a clear error; existing display names unaffected.
*Deps:* S-011 (DisplayName exists).

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

**Left open, not scoped as stories this round:** a scheduled/proactive
cache pre-warming job (no evidence on-demand fetching is a real problem
yet — revisit if S-014's threshold bump makes grid generation struggle in
practice), and selectable color themes/dark mode (design-document.md
already tracks this as a deliberately unresolved open question — a
reversal of the light-only v1 direction deserves its own design session,
not a quick story).

## Tier 1 backlog (unordered — each waits for its trigger in `MVP-SCOPE.md`)

T-101 API-Football fallback + full waterfall (ADR-0011, `ExternalApiUsage`) ·
T-102 guess-time live verification (REQ-211) · T-103 autocomplete +
`PlayerNameIndex` (REQ-207, ADR-0007) · T-104 disambiguation UI (REQ-209) ·
T-105 Trophy category + automated ID resolution (REQ-108, ADR-0012) ·
T-106 dev/prod split + sync (ADR-0006/0009, REQ-801-804) · T-107 backups +
alerting (REQ-901/902 — **bright line: before any non-self user**) ·
T-108 email confirmation + Resend (REQ-701-705) · T-109 custom leagues
(REQ-402-404) · T-110 legal docs finalized (**bright line: before public
launch**).
