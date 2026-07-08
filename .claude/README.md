# Using Claude Code on this project

This is the guide for a human sitting down to actually use Claude Code on
xG Arcade — what exists, when it kicks in, and how the common workflows
(building, testing, adding a game, working on design) actually go. `CLAUDE.md`
at the repo root is the file Claude Code reads automatically for project
context; this file is for you.

## What's here

**Subagents** (`.claude/agents/*.md`) — Claude Code invokes these
automatically when your request matches what the agent's description says
it's for. You don't need to explicitly summon them by name, though you can
("use the test-writer agent to cover REQ-210") if you want to be sure.

| Agent | Kicks in when you... | What it actually does |
|---|---|---|
| `test-writer` | ...ask for tests, or finish implementing a REQ | Turns a requirement's Given/When/Then into NUnit/Playwright/Vitest tests, named `REQ###_...` |
| `doc-sync` | ...finish a coding session, or explicitly ask to sync docs | Diffs recent changes against the docs, updates whichever are now stale, appends a CHANGELOG entry |
| `architecture-reviewer` | ...make a change touching more than one component, or add a dependency | Checks the diff against `architecture-document.md`'s boundary rules, flags drift, recommends whether it's a bug or needs an ADR |
| `game-scaffolder` | ...say "add a new game" or similar | Scaffolds a new `IGameModule` implementation with the correct boundaries, plus matching requirements/architecture doc stubs |
| `ui-implementer` | ...build or change any frontend screen/component | Enforces `design-document.md`'s tokens and the `frontend-design` skill's constraints before writing UI code |
| `requirements-writer` | ...describe a feature that isn't a REQ yet, or ask for a requirements review | Drafts new REQ entries in the established format, or reviews existing ones for testability and consistency |
| `code-reviewer` | ...finish non-trivial code, or ask for a review/refactor | Checks against `docs/coding-guidelines.md` — readability, duplication, error handling, test coverage gaps. Doesn't check architecture boundaries; that's `architecture-reviewer`'s job |

**Slash commands** (`.claude/commands/*.md`) — you type these explicitly,
e.g. `/update-docs`, when you want that specific action to run right now.

| Command | Use it to... |
|---|---|
| `/update-docs` | Run the full doc-sync workflow against the current diff |
| `/new-adr` | Scaffold a new Architecture Decision Record from the template |
| `/new-game` | Kick off `game-scaffolder` for adding a new game |
| `/test` | Reset dev test data and run the full test suite (unit + API + E2E) |

## A typical development session

1. Open Claude Code in the repo. It reads `CLAUDE.md` automatically —
   check the "Getting started" section if no code exists yet, otherwise
   just describe what you want built.
2. Claude Code should naturally read the relevant doc(s) before writing
   code (that's what `CLAUDE.md`'s document map is for). If it dives
   straight into code without mentioning a REQ or ADR, that's worth
   noticing — ask it what requirement it's implementing against.
3. As it finishes a unit of work, `doc-sync` should trigger (or run
   `/update-docs` yourself if it doesn't) — this is what keeps the docs
   from drifting out of sync with reality, which is the whole point of
   this setup.
4. For anything touching more than one component or adding a dependency,
   expect (or ask for) an `architecture-reviewer` pass before you consider
   it done.

## A typical testing session

1. Run `/test` — Tier 0: runs the full suite locally against a fresh
   local stack (mirroring `ci.yml`), since there's no deployed dev
   environment yet. (Tier 1 adds the dev-env reset via REQ-802.)
2. If coverage is missing for something you just built, ask for it
   directly or let `test-writer` pick it up — it'll name tests by REQ ID,
   so you can grep `docs/requirements-document.md` for a REQ and grep the
   test projects for the same ID to see if it's covered.
3. Failing tests should be reported with which REQ they affect — if
   Claude Code just dumps raw test output, ask it to summarize against
   requirement IDs instead.

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

- **Git/PRs**: no dedicated agent — Claude Code's native git/PR
  capabilities handle this directly. `CLAUDE.md`'s "Git and PR conventions"
  section is what keeps commit messages, branch names, and PR descriptions
  consistent with the REQ/ADR ID system used everywhere else.
- **Requirements**: use `requirements-writer` before writing code for
  anything not already covered by an existing REQ — it drafts in the
  established Given/When/Then format and checks for ID collisions/testability.
- **Code quality**: `code-reviewer` checks against `docs/coding-guidelines.md`
  for general quality (duplication, error handling, test coverage) — a
  different concern from `architecture-reviewer`'s structural boundary checks.
- **`NOTES.md`**: a lightweight running log for gotchas and context that
  don't rise to the level of a formal ADR (a third-party API quirk, "this
  took longer than expected because..."). Not agent-managed — just add to
  it directly when something's worth remembering.

## When something doesn't work as expected

If an agent isn't triggering when you'd expect (e.g. `doc-sync` never
seems to run), just invoke it explicitly by name in your request — Claude
Code's automatic triggering is a convenience, not a guarantee, and being
explicit always works.

If Claude Code produces something that contradicts an ADR or a boundary
rule, that's worth stopping on immediately rather than letting it compound
— the earlier a boundary violation is caught, the cheaper it is to unwind.
