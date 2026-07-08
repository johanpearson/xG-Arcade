# CLAUDE.md

This file is read automatically by Claude Code at the start of a session.
It tells the agent what this project is, which documents govern it, and
what to do before and after each unit of work.

## Project summary

**xG Arcade** (working title — placeholder name, find-and-replace when a real
name is chosen): a multi-game platform owning user accounts, leagues, the
round scheduling engine, and the scoring/uniqueness engine. It has no
football-specific logic itself.

**xG Grid**: the first game hosted on the xG Arcade. An NxN grid where
players combine two categories (e.g. country × club) to guess a matching
player, scored on answer uniqueness. Lives entirely in
`XGArcade.Games.XGGrid`, behind the shared `IGameModule` interface — see
`docs/architecture-document.md` §5 and `docs/decisions/0003-generic-round-game-reference.md`
for the specific rule that keeps the xG Arcade from depending on xG Grid internals.

Built as a modular monolith (C# / ASP.NET Core backend, TypeScript / React
frontend) so a second game can be added later without changing the xG Arcade core.

## Document map

All governing documents live under `docs/`. Each has YAML frontmatter with
`status`, `version`, `related_docs`, and `update_when` — read the
frontmatter first to decide if a doc is relevant to your current task.

| Document | Answers | Read it when |
|---|---|---|
| `MVP-SCOPE.md` | WHAT to actually build right now vs. later (Tier 0/1/2) | **Read this first, every session, before any other doc below** — it's the real build order |
| `docs/backlog.md` | The ordered story list (S-001…S-013) implementing Tier 0 incrementally | Starting any implementation session — pick the next unfinished story, one story per session/PR |
| `docs/requirements-document.md` | WHAT the system must do (REQ-xxx) | Implementing or testing any user-facing behavior |
| `docs/architecture-document.md` | WHY it's structured this way (COMP-xxx) | Adding/changing a component, module boundary, or data flow |
| `docs/implementation-document.md` | HOW it's concretely built | Writing code: tech stack, data model, folder layout, algorithms |
| `docs/design-document.md` | What it should LOOK/FEEL like (SCREEN-xxx, tokens) | Building or changing any frontend screen or component |
| `docs/decisions/*.md` | WHY a specific structural choice was made, permanently | Before doing anything that might contradict an existing decision |
| `docs/CHANGELOG.md` | WHAT changed recently, doc-wise | Start of every session, to catch up |
| `infra/README.md` | HOW to deploy and what secrets are needed | Touching anything under `/infra` or `/.github/workflows` |
| `SETUP.md` | Step-by-step external account setup (MVP: GitHub/Azure/Supabase only; Resend/API-Football are Tier 1) | Doing initial account/secrets setup, human-facing, not agent-facing |
| `docs/legal/*.md` | Draft privacy policy / terms — NOT legally reviewed | Any change touching data collection, retention, or third-party sharing must be reflected here too |
| `TODO.md` | Consolidated getting-started and pre-launch checklist | Check off items as completed; add new ones rather than letting action items live only in conversation |
| `docs/coding-guidelines.md` | Code style, error handling, testing patterns | Writing any backend or frontend code |
| `NOTES.md` | Informal running context/gotchas, not formal decisions | Something surprising is discovered during development worth remembering later |
| `docs/review-2026-07-07.md` | Point-in-time critical review of every file, with fixes applied | Revisit after real code exists to check unverified findings (agent usefulness, coding guidelines accuracy) |
| `docs/review-2026-07-07-design.md` | Design/plan review: 8 fixed flaws + what was judged and deliberately left alone | Before relitigating a settled design point; revisit after S-013 |

**Reading order for a typical task:** `MVP-SCOPE.md` (is this even in
scope right now?) → CHANGELOG → the relevant REQ section →
architecture doc if it touches a boundary → implementation doc for
concrete details → relevant ADRs if something feels like it contradicts a
past decision.

## Workflow: before starting work

1. Read `docs/CHANGELOG.md` to see what changed since your last session.
2. Identify which REQ-xxx (and, if relevant, COMP-xxx) IDs the task relates
   to. If none exist yet, that's a signal a requirement is missing —
   propose one rather than implementing undocumented behavior.
3. Check `docs/decisions/` for any ADR relevant to the area you're touching.

## Workflow: after finishing a unit of work

Always do this before considering a task done — do not treat documentation
updates as optional cleanup:

1. **Tests**: new/changed behavior has tests named with its REQ-xxx (see
   naming convention in `implementation-document.md` §7). Run the full
   suite, not just new tests.
2. **Requirements doc**: if behavior or acceptance criteria changed, update
   `docs/requirements-document.md` in the same iteration. Never renumber
   existing REQ IDs; mark superseded ones `Status: Deprecated`.
3. **Architecture doc**: if a component boundary, responsibility, or data
   flow changed, update `docs/architecture-document.md`.
4. **New structural decision**: if you made a choice that could reasonably
   have gone another way (library swap, boundary change, data strategy
   change), add an ADR using `docs/decisions/0000-template.md`. Do not
   silently make architecturally significant decisions without one.
5. **CHANGELOG**: append one line to `docs/CHANGELOG.md` naming which docs
   you touched and why.
6. **Frontmatter**: bump `last_updated` (and `version` if the change is
   substantial) in any doc you edited.

If you're not sure whether a change is "significant enough" for an ADR: if
reverting it would require someone to understand *why* the original choice
was made, it needs an ADR.

## Git and PR conventions

- **Commit messages**: reference the REQ/ADR/COMP ID a change relates to
  where one exists, e.g. `Add cell-level guess attempt limit (REQ-210)`.
  Not every commit needs this (a typo fix doesn't), but any commit
  implementing or changing a requirement does.
- **Branch naming**: `feature/req-###-short-description` or
  `fix/short-description` — descriptive enough that the branch list is
  useful without opening each one.
- **PR descriptions** should state: which REQ(s) it implements or changes,
  whether `docs/` were updated to match (or explicitly why not), and
  whether tests were added. A PR that changes behavior without touching
  any doc is a signal to double back, not something to wave through.
- Claude Code's native git/PR capabilities handle the actual git operations
  — there's no dedicated subagent for this, since wrapping a built-in
  capability in a persona would add a layer without adding value. These
  conventions are what keep it consistent with everything else here.

## Conventions

- Requirement IDs: `REQ-xxx`, never renumbered, referenced in test names as
  `REQ###_MethodUnderTest_ExpectedBehavior`.
- Architecture component IDs: `COMP-xxx`, referenced in code comments at
  module boundaries where non-obvious.
- ADR IDs: `NNNN`, sequential, never reused, superseding ADRs reference the
  one they replace.
- Backend tests: NUnit. API tests: NUnit + WebApplicationFactory. Frontend
  unit: Vitest. E2E: Playwright.
- No secrets in source control; configuration via environment variables.
- Code style, error handling, and testing patterns: `docs/coding-guidelines.md`
  — `code-reviewer` checks against it specifically.
- **xG Arcade/game boundary:** never add a game-specific foreign key or type
  reference to a `XGArcade.Core` entity (e.g. `Round`, `League`, `User`).
  Games reference Core through `IGameModule`; Core references games only via
  opaque `GameKey`/`GameInstanceId` pairs. See ADR-0003. If a task seems to
  require breaking this, stop and flag it rather than adding the reference.
- **Hosting-agnostic application code:** never add Azure-specific (or any
  cloud-provider-specific) code to `XGArcade.Api` or `XGArcade.Core` — the
  backend must remain a plain container. Hosting configuration belongs only
  in `/infra` and environment variables. See ADR-0004.
- **Frontend visual consistency:** never introduce a color, typeface, or
  animation not defined in `docs/design-document.md` §2. If a screen needs
  something the token system doesn't cover, update that document first
  rather than adding an ad-hoc value in code.
- **Email sending boundary:** auth emails (confirmation, password reset) are
  Supabase Auth's responsibility via custom SMTP — never send them from
  `XGArcade.Core` code. Product notification emails (round results) go
  through `Core.Notifications` calling Resend's API directly, never through
  Supabase Auth or an auth hook. See ADR-0005.
- **Test-data isolation:** the `/internal/test-data/*` endpoints
  (`XGArcade.Testing`, COMP-09) must only be registered when
  `ASPNETCORE_ENVIRONMENT != Production`, checked in `Program.cs` before
  routing — never guarded only by an attribute. They must create/reset data
  only by calling other components' normal write paths, never a direct
  table write. See ADR-0006.
- **Environment sync boundary:** any sync between prod and dev, in either
  direction, must go through the shared allowlist in
  `infra/scripts/lib/game-data-tables.sh` — never a table-specific copy in
  either `sync-prod-to-dev.sh` or `promote-dev-to-prod.sh` individually.
  This allowlist never includes `User`, `NotificationPreference`, `League`,
  `LeagueMembership`, `Guess`, `Round`, `GridInstance`, or `GridCell` —
  results and customer data are never eligible to sync, regardless of
  direction. See ADR-0006 and ADR-0009.
- **Autocomplete/correctness separation:** autocomplete and name matching
  query only `PlayerNameIndex` (COMP-10); correctness-checking a submitted
  guess queries only `PlayerAttribute`/`PlayerOverride` (COMP-06). Never
  merge these two paths — doing so leaks answer validity through
  autocomplete. See ADR-0007.
- **Build order follows `MVP-SCOPE.md`, not the full design docs:** a
  REQ/ADR existing and looking "ready" is not permission to build it if
  it's Tier 1/2. If a task seems to need Tier 1/2 complexity to make
  Tier 0 work, stop and flag it rather than quietly pulling it forward.
- **Guess-time live lookups are narrow and never deferred:** only trigger
  a live lookup (REQ-211) when the guess matched a real
  `PlayerNameIndex` candidate with no existing `PlayerAttribute` data —
  never for a name that matched nothing there. Always try Wikidata first;
  API-Football is a fallback only, never the first call (ADR-0011).
  Persist the result immediately in the same request, never batched.
  Only check `ExternalApiUsage`'s reserved threshold on the API-Football
  fallback path and fail closed (incorrect) if it's exhausted — never
  skip that check to "just answer the guess."
- **Framework versions:** check `implementation-document.md` §1 for the
  currently-verified stable versions before scaffolding — don't default to
  whatever a training-data memory suggests, since these dates matter.
  Dependabot (`.github/dependabot.yml`) handles routine minor/patch drift;
  major version changes need a deliberate doc update, not just a merge.
- **Account deletion never hard-deletes `Guess` rows:** anonymize
  (`UserId = NULL`) instead — hard-deleting would corrupt other players'
  historical uniqueness scores and leaderboard totals. See REQ-710.
- **New external data sources need a terms-of-service check first:** before
  wiring up any new provider in `DataSync.Clients`, read its terms for
  caching/retention restrictions the same way ADR-0008 did for
  API-Football. Don't assume a new source is fine by analogy.
- **`docs/legal/*.md` are drafts, not final:** if a change affects what
  data is collected, how long it's kept, or which third parties see it,
  update the relevant legal draft in the same iteration — don't let it
  silently drift out of sync with what the system actually does.

## Getting started (first session — no code exists yet)

**Read `MVP-SCOPE.md` first, before this section or `implementation-document.md`**
— then work through `docs/backlog.md` story by story (S-001 onward); it
turns the scoping below into a concrete, ordered, testable sequence.
The requirements/architecture/implementation docs describe the full,
long-term system — they are not a claim that all of it should be built
now. `MVP-SCOPE.md` is the actual build order: Tier 0 only, first. Do not
build a Tier 1/2 item (API-Football as a fallback source, guess-time live
verification, autocomplete, dev/prod split, custom leagues, etc.) just
because its REQ/ADR already exists and looks ready — that's the exact
trap `MVP-SCOPE.md` exists to prevent. **Note Wikidata is Tier 0, not
Tier 1** — it's the primary data source from the start (full historical
correctness via a small, hand-curated club list), with API-Football added
later as a Tier 1 fallback/expansion.

Scaffold, in order, checking off against `implementation-document.md` §3-4
but scoped down to what Tier 0 actually needs (e.g. skip
`XGArcade.DataSync`'s API-Football client, skip
`XGArcade.Games.XGGrid`'s Trophy-category logic, skip the dev-environment
Bicep parameters entirely):

1. `backend/XGArcade.sln` with the Tier 0 subset of projects
   (`XGArcade.Api`, `XGArcade.Core`, `XGArcade.Games.XGGrid`,
   `XGArcade.Data`, and their `.Tests` counterparts — `XGArcade.DataSync`
   can start as a thin Wikidata-only client, not the full
   Wikidata+API-Football waterfall version)
2. `backend/Dockerfile` — must produce the plain container `deploy.yml` and
   `infra/bicep/modules/backend-container-app.bicep` expect (listens on
   port 8080; see that Bicep module's `targetPort`)
3. `frontend/` — Vite + React + TypeScript scaffold, with `npm run test`
   (Vitest) and `npm run test:e2e` (Playwright) wired up so `ci.yml` passes
   on an empty project before any real feature is added
4. A trivial end-to-end slice (e.g. a health-check endpoint + a frontend
   page that calls it) to prove `ci.yml` and `deploy.yml` both work before
   building real features on top
5. Only after that: start on Tier 0's actual feature work (Core domain +
   data model, scoped to `MVP-SCOPE.md`, not the full data model in
   `implementation-document.md` §5)

Do not skip straight to feature work before step 4 passes — an untested
deploy pipeline discovered late is more expensive to debug than one
verified early with almost no code in it.

## Commands

- Backend tests: `dotnet test`
- Frontend unit tests: `npm run test` (Vitest)
- E2E tests: `npm run test:e2e` (Playwright)
- Full local run: see `implementation-document.md` §9 for suggested build order

## Subagents available (`.claude/agents/`)

See `.claude/README.md` for the human-facing guide to all of these,
including full development/testing/new-game/design workflows.

| Agent | Use for |
|---|---|
| `test-writer` | Turning a REQ's acceptance criteria into NUnit/Playwright/Vitest tests with correct naming |
| `doc-sync` | Reviewing a diff and updating requirements/architecture/implementation docs + CHANGELOG to match |
| `architecture-reviewer` | Checking new code against `architecture-document.md` boundaries before merging, and flagging when an ADR is needed |
| `game-scaffolder` | Adding a new game module with the correct `IGameModule` boundaries and matching doc stubs |
| `ui-implementer` | Building/changing frontend screens against `design-document.md`'s token system and the `frontend-design` skill |
| `requirements-writer` | Drafting new requirements or reviewing existing ones for testability/consistency, in the established Given/When/Then format |
| `code-reviewer` | General code quality/refactor review against `docs/coding-guidelines.md` — distinct from `architecture-reviewer`'s structural boundary checks |

## Slash commands available (`.claude/commands/`)

| Command | Use for |
|---|---|
| `/update-docs` | Runs the "after finishing work" doc-update workflow above against the current diff |
| `/new-adr` | Scaffolds a new ADR file from the template and asks for the missing sections |
| `/new-game` | Scaffolds a new game via `game-scaffolder` |
| `/test` | Resets dev test data and runs the full test suite (unit + API + E2E) |

## Definition of done

A task is not done until: tests pass, relevant docs are updated (or
explicitly confirmed as not needing changes), CHANGELOG has an entry if any
doc changed, and any new structural decision has an ADR.

## See also

- `README.md` — human entry point to the whole repo
- `.claude/README.md` — how to actually use the agents/commands above,
  with full workflows for development, testing, adding a game, and design work
- `TODO.md` — consolidated getting-started and pre-launch checklist
