# Using Claude Code on this project

This is the guide for a human sitting down to actually use Claude Code on
xG Arcade — what exists, when it kicks in, and how the common workflows
(building, testing, adding a game, working on design) actually go. `CLAUDE.md`
at the repo root is the file Claude Code reads automatically for project
context; this file is for you. The full organization design — who owns
what, and why it's shaped this way — is `docs/ai/agent-migration-plan.md`.

## How the organization works

The agents form a small engineering organization rather than a flat
toolbox:

- **The main session is the orchestrator.** It coordinates: intake,
  scoping against `MVP-SCOPE.md`, decomposition, delegating to the agents
  below, running the quality gate, and validating the definition of done.
  Type `/orchestrate` with a story/feature/bug to run that workflow
  explicitly — it's the default way to start a unit of work.
- **Delivery agents implement**: `backend-implementer`, `ui-implementer`,
  `game-scaffolder`, `test-writer`.
- **Protection agents guard the system**: `architecture-reviewer`
  (structure/boundaries), `quality-architect` (maintainability, code
  quality, refactoring, test architecture).
- **Product and stewardship**: `requirements-writer` (REQs),
  `doc-sync` (docs/CHANGELOG honesty).

Every responsibility has exactly one owner — the matrix is in
`docs/ai/agent-migration-plan.md` §4.3.

## What's here

**Subagents** (`.claude/agents/*.md`) — Claude Code invokes these
automatically when your request matches what the agent's description says
it's for. You don't need to explicitly summon them by name, though you can
("use the test-writer agent to cover REQ-210") if you want to be sure.

| Agent | Kicks in when you... | What it actually does |
|---|---|---|
| `backend-implementer` | ...ask for a backend feature, fix, or data-work change | Implements C#/ASP.NET Core work following this repo's established patterns (repository/DTO/problem-details rules, EF Core InMemory-provider traps, CLI-verb job pattern) and reports honestly what the sandbox could and couldn't verify |
| `ui-implementer` | ...build or change any frontend screen/component | Enforces `design-document.md`'s tokens and the `frontend-design` skill's constraints before writing UI code; keeps E2E specs in step with behavior changes |
| `game-scaffolder` | ...say "add a new game" or similar | Scaffolds a new `IGameModule` implementation with the correct boundaries, plus matching requirements/architecture doc stubs |
| `test-writer` | ...ask for tests, or finish implementing a REQ | Turns a requirement's Given/When/Then into NUnit/Playwright/Vitest tests, named `REQ###_...`, reusing the shared test infrastructure |
| `architecture-reviewer` | ...make a change touching more than one component, or add a dependency | Checks the diff against `architecture-document.md`'s boundary rules, flags drift, recommends whether it's a bug or needs an ADR |
| `quality-architect` | ...finish non-trivial code, ask for a review or refactor, or notice test setup getting copy-pasted | Three modes: reviews against `docs/coding-guidelines.md` (readability, duplication, error handling, coverage gaps); performs deliberate behavior-preserving refactors; owns the test architecture (fake/fixture strategy, flaky/slow tests). Doesn't check architecture boundaries; that's `architecture-reviewer`'s job |
| `requirements-writer` | ...describe a feature that isn't a REQ yet, or ask for a requirements review | Drafts new REQ entries in the established format, or reviews existing ones for testability and consistency |
| `doc-sync` | ...finish a coding session, or explicitly ask to sync docs | Diffs recent changes against the docs, updates whichever are now stale, appends a CHANGELOG entry |

**Slash commands** (`.claude/commands/*.md`) — you type these explicitly,
e.g. `/orchestrate`, when you want that specific workflow to run right now.

| Command | Use it to... |
|---|---|
| `/orchestrate` | Run a story/feature/bug through the whole workflow: intake → scope check → decomposition → delegation → quality gate → docs → done-validation |
| `/quality-gate` | Run the deterministic pre-merge gate: architecture review → quality review → full tests → doc check. Fails closed; anything the sandbox can't run is reported "deferred to CI", never "passed" |
| `/update-docs` | Run the full doc-sync workflow against the current diff |
| `/new-adr` | Scaffold a new Architecture Decision Record from the template |
| `/new-game` | Kick off `game-scaffolder` for adding a new game |
| `/test` | Reset dev test data and run the full test suite (unit + API + E2E) |

## A typical development session

1. Open Claude Code in the repo and run `/orchestrate` with the story
   (from `docs/backlog.md`), feature idea, or bug you want handled. One
   story per session/PR — the orchestrator will enforce this and push
   back on Tier 1/2 scope creep per `MVP-SCOPE.md`.
2. Watch the intake step: it should name the REQ-xxx it's implementing
   against before any code is written. A feature with no REQ goes through
   `requirements-writer` first. If it dives straight into code without
   mentioning a REQ, that's worth noticing — ask what requirement it's
   implementing against.
3. Implementation gets delegated (backend → `backend-implementer`,
   frontend → `ui-implementer`, tests → `test-writer`) with a visible
   checklist tracking progress.
4. Before the work is considered done, `/quality-gate` runs both review
   agents, the test suite, and the doc check — and reports honestly which
   suites could actually run in the sandbox versus which are deferred
   to CI.
5. Docs get synced (`/update-docs`) in the same session — this is what
   keeps the docs from drifting out of sync with reality, which is the
   whole point of this setup.

You can still skip the ceremony for small things — "fix this typo" doesn't
need `/orchestrate`. The commands make the common paths deterministic;
they don't forbid the short ones.

## A typical testing session

1. Run `/test` — Tier 0: runs the full suite locally against a fresh
   local stack (mirroring `ci.yml`), since there's no deployed dev
   environment yet. (Tier 1 adds the dev-env reset via REQ-802.) Note the
   sandbox often lacks the `dotnet` SDK — backend results then come from
   CI, and Claude should say so rather than claiming a local pass.
2. If coverage is missing for something you just built, ask for it
   directly or let `test-writer` pick it up — it'll name tests by REQ ID,
   so you can grep `docs/requirements-document.md` for a REQ and grep the
   test projects for the same ID to see if it's covered.
3. Failing tests should be reported with which REQ they affect — if
   Claude Code just dumps raw test output, ask it to summarize against
   requirement IDs instead.
4. If test *setup* is getting copy-pasted or a test is flaky/slow, that's
   `quality-architect`'s test-architecture lane — ask it to consolidate,
   rather than letting each session hand-roll another variant.

## Adding a new game — the actual process

1. Have a real conversation first about what the game *is* — mechanic,
   scoring, what a "round" means for it. `game-scaffolder` will ask if you
   don't provide this; better to front-load it.
2. Run `/new-game`. This creates the module skeleton (implementing
   `IGameModule`, matching xG Grid's pattern), a test project, and stub
   sections in `requirements-document.md` and `architecture-document.md`.
3. Fill in the actual requirements and logic — the scaffold gives you
   structure, not content.
4. Run `architecture-reviewer` before merging anything substantial — the
   most common way this goes wrong is the new game reaching into xG Grid's
   internals or Core's tables directly instead of through the established
   boundaries (ADR-0002, ADR-0003).
5. `doc-sync` / `/update-docs` to make sure the docs reflect what actually
   got built, not just the original stub.

## Working on design — the actual process

1. For any new screen or component, check `docs/design-document.md` §3 for
   an existing `SCREEN-xxx` spec first. If there isn't one, that's worth
   sketching (even roughly) before code, not after.
2. `ui-implementer` should pick up frontend tasks automatically and read
   both the design doc and the `frontend-design` skill before writing
   anything. If a component gets built with colors/fonts that aren't in
   the design doc's token table, that's drift — flag it.
3. If you make a real design decision while building (a state the doc
   didn't cover, a spacing choice, etc.), it should get written back into
   `design-document.md` in the same session, not left implicit in code.
4. For an intentional visual direction *change* (like the dark→light pivot
   this project already went through once), that's a conversation to have
   explicitly and directly — describe the problem with the current
   direction, and expect the whole token system to get reconsidered, not
   patched piecemeal.

## Git, requirements, code quality, and running notes

- **Git/PRs**: no dedicated agent — the orchestrator (main session) uses
  Claude Code's native git/PR capabilities directly. `CLAUDE.md`'s "Git
  and PR conventions" section is what keeps commit messages, branch
  names, and PR descriptions consistent with the REQ/ADR ID system used
  everywhere else.
- **Requirements**: use `requirements-writer` before writing code for
  anything not already covered by an existing REQ — it drafts in the
  established Given/When/Then format and checks for ID collisions/testability.
- **Code quality and refactoring**: `quality-architect` reviews against
  `docs/coding-guidelines.md` and owns deliberate refactors — a different
  concern from `architecture-reviewer`'s structural boundary checks.
  (Before 2026-07-17 the review half of this was an agent named
  `code-reviewer` — historical notes in the backlog/CHANGELOG referring
  to "a code-reviewer pass" mean this lane.)
- **`NOTES.md`**: a lightweight running log for gotchas and context that
  don't rise to the level of a formal ADR (a third-party API quirk, "this
  took longer than expected because..."). Not agent-managed — just add to
  it directly when something's worth remembering.

## When something doesn't work as expected

If an agent isn't triggering when you'd expect (e.g. `doc-sync` never
seems to run), just invoke it explicitly by name in your request — Claude
Code's automatic triggering is a convenience, not a guarantee, and being
explicit always works. `/orchestrate` and `/quality-gate` exist precisely
to make the important sequences deterministic instead of relying on
automatic triggering.

If Claude Code produces something that contradicts an ADR or a boundary
rule, that's worth stopping on immediately rather than letting it compound
— the earlier a boundary violation is caught, the cheaper it is to unwind.
