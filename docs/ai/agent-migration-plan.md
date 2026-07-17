---
doc_id: agent-migration-plan
title: Agent Ecosystem — Migration Plan and Organization Reference
version: "1.0"
status: implemented
last_updated: 2026-07-17
owner: Johan
related_docs:
  - ../../CLAUDE.md
  - ../../.claude/README.md
  - ../coding-guidelines.md
  - ../review-2026-07-07.md
update_when:
  - "An agent or slash command is added, removed, merged, split, or renamed"
  - "A responsibility moves between agents"
  - "The orchestration workflow changes"
---

# Agent Ecosystem — Migration Plan and Organization Reference

This document is both the record of the 2026-07-17 agent-ecosystem
redesign and the ongoing reference for who owns what. It plays the role
an ADR plays for product architecture: if you're about to change an
agent, command, or ownership boundary, read the relevant section here
first — and update this document in the same change. (The `docs/decisions/`
ADR namespace is deliberately reserved for the product system; process
and tooling decisions live here.)

**Governing rule of the migration: no knowledge loss.** Every
responsibility and every piece of repository-specific expertise held by
the pre-migration setup is accounted for in §6's transfer matrix. Nothing
was discarded to make the design cleaner.

---

## 1. Current state before migration (inventory)

### 1.1 Agents (7)

| Agent | Responsibilities | Unique knowledge it carried | Overlaps/gaps |
|---|---|---|---|
| `test-writer` | Turn REQ Given/When/Then into NUnit/Vitest/Playwright tests; REQ naming; run suites; report coverage per acceptance branch | REQ###_ naming convention; one-test-per-And-clause heuristic; WebApplicationFactory pattern; "extend, don't duplicate, prior tests on the same REQ" | No owner for the *shared* test infrastructure its tests sit on |
| `doc-sync` | Diff-vs-docs reconciliation for requirements/architecture/implementation docs + CHANGELOG; frontmatter bumps; ADR-needed flagging | `update_when` frontmatter protocol; never-renumber-IDs discipline; "implementation doc drifts fastest, check most literally"; never edit acceptance criteria to make a test pass | None — clean lane |
| `architecture-reviewer` | Boundary/data-flow review against architecture-document.md; ADR-needed flagging; consistent/drift/recommendation verdicts | The concrete violation checklist (game module reaching PlayerData/PlayerOverride directly; cross-game references; new dependency without ADR; logic creeping into Api controllers; §6 data-flow mismatch); "don't approve or block, produce input" | Clean vs. code-reviewer (verified in review-2026-07-07) |
| `game-scaffolder` | Scaffold new game modules on the IGameModule boundary + doc stubs; ask for mechanics, never invent rules | NotImplementedException-with-pointer stub style; next-REQ-hundred-block allocation; "don't assume every game is football-player-guessing shaped" | None — specialized, rarely used |
| `ui-implementer` | Frontend delivery against design-document.md tokens + frontend-design skill; accessibility floor; copy voice; write design decisions back | Token-traceability rule; never-color-only states; 44×44px targets; reduced-motion; badge-dock is the only bold motion moment; ADR-0007 autocomplete answer-leak warning | No explicit link to E2E-drift trap (bit in S-029) |
| `requirements-writer` | Draft/review REQs in Given/When/Then; testability; ID collisions; §5-default vs §7-open-question discipline | Hundred-block numbering map; deprecate-never-renumber; WHAT/VERIFY vs HOW separation; "don't touch architecture/implementation docs yourself" | None — clean lane |
| `code-reviewer` (retired) | Quality review against coding-guidelines.md: duplication, error handling, coverage gaps, readability; explicitly told **not** to refactor | COMP-06 override-merge and REQ-208 name-normalization as canonical duplication hot-spots; REQ-902 debuggability lens; "flag coverage gaps to test-writer"; "propose missing guidelines, don't enforce undocumented rules" | Refactoring explicitly excluded but assigned to no one; test-infrastructure concerns unowned |

### 1.2 Slash commands (4)

| Command | Purpose | Notes |
|---|---|---|
| `/update-docs` | Runs doc-sync workflow against current diff | Kept as-is |
| `/new-adr` | Scaffold ADR from template, cross-check related REQs/COMPs, §10 table row, CHANGELOG | Kept as-is |
| `/new-game` | Front-door to game-scaffolder | Kept as-is |
| `/test` | Full local suite mirroring ci.yml; Tier 1 evolution note (test-data reset) | Kept as-is |

### 1.3 Orchestration and other prompt surfaces

- **CLAUDE.md** — document map, before/after-work workflows, conventions
  (the boundary-rule digest), definition of done, agent/command tables.
- **`.claude/README.md`** — human-facing workflows (development, testing,
  new game, design).
- **`docs/backlog.md` header** — the de-facto orchestration rule: one
  story per session/PR, work top to bottom, don't pull Tier 1 forward.
- **External skill** `/mnt/skills/public/frontend-design` — consumed by
  ui-implementer; not repo-owned, unchanged.
- **Institutional-knowledge stores**: `NOTES.md` (gotchas),
  `docs/CHANGELOG.md` (dated doc history), `docs/coding-guidelines.md`
  (conventions), ADRs (structural decisions). All unchanged by this
  migration; several scattered facts from them are now *also* codified in
  agent definitions (see §6.2).

### 1.4 Ownership matrix — before

| Responsibility | Owner before |
|---|---|
| Story intake / decomposition / routing / tracking | Implicit (CLAUDE.md prose + backlog header + main session's judgment) |
| Completion validation | Implicit ("Definition of done" prose, unenforced) |
| Requirements authoring/review | requirements-writer |
| Backend delivery | **Nobody** (main session ad hoc) |
| Frontend delivery | ui-implementer |
| New game scaffolding | game-scaffolder |
| Per-REQ test delivery | test-writer |
| Test architecture (fakes/fixtures/helpers strategy, flakiness, speed) | **Nobody** (emergent per-project convention) |
| Quality review | code-reviewer |
| Refactoring | **Nobody** (code-reviewer explicitly forbidden; never reassigned) |
| Engineering standards evolution (coding-guidelines.md) | Weakly implied (doc's own `update_when`) |
| Quality gates (which reviews run, when, pass/fail) | **Nobody** (CHANGELOG shows passes ran inconsistently: sometimes 1 reviewer, sometimes 5) |
| Architecture boundaries + ADR flagging | architecture-reviewer |
| Doc/CHANGELOG sync | doc-sync |
| Git/PR operations | Main session (native, deliberate — no persona wrapper) |

---

## 2. Analysis — what needed to change

- **F-1 · Orchestration was implicit.** The workflow that actually ships
  a story (scope check → REQ mapping → implement → review passes → docs)
  existed only as prose scattered across CLAUDE.md and the backlog
  header. Whether a session ran zero, one, or five review passes was
  judgment, not process — the CHANGELOG shows all three happening.
- **F-2 · Three responsibilities were orphaned.** Refactoring (explicitly
  carved out of code-reviewer's remit, assigned to no one), test
  architecture (five hand-rolled `Fake*` classes and zero shared
  builders/helpers grew by accretion, nobody watching for duplication,
  flakiness, or speed), and quality-gate definition.
- **F-3 · Backend delivery knowledge lived only in logs.** The
  InMemory-provider trap, the request-scoped-DbContext concurrency trap,
  the CLI-verb-not-endpoint job pattern, the no-dotnet-SDK sandbox
  reality, the unverifiable-QIDs constraint — all real, all repeatedly
  bitten, all discoverable only by re-reading NOTES.md/CHANGELOG. Every
  other delivery lane (frontend, games, tests) had an agent carrying its
  patterns; backend didn't.
- **F-4 · The E2E-drift trap had no owner.** S-029 shipped a behavior
  change that silently invalidated Playwright assertions that only run in
  CI; nothing in any prompt told anyone to grep the E2E specs.
- **F-5 · What was already healthy** (and was therefore left alone): the
  review-2026-07-07 verification that agent responsibilities don't
  overlap; the explicit "what NOT to do" sections; the decision to keep
  git/PR un-personified; doc-sync / requirements-writer / game-scaffolder
  / architecture-reviewer lanes; all four commands.

---

## 3. Migration decisions

| Agent | Decision | Rationale |
|---|---|---|
| `test-writer` | **Keep** (small edit) | Clean delivery lane. Gained an explicit consumer/owner split with quality-architect: it consumes test infrastructure, flags new shared infrastructure needs instead of inventing patterns. |
| `doc-sync` | **Keep** (unchanged) | Clean lane, heavily used, no gaps found. |
| `architecture-reviewer` | **Keep** (small edit) | Clean lane. Gained one paragraph making the quality-vs-structure split explicit from its side too (it previously existed only in code-reviewer's file). |
| `game-scaffolder` | **Keep** (unchanged) | Specialized, correct, self-contained. |
| `ui-implementer` | **Keep** (small edit) | Gained the S-029 lesson (grep + update E2E specs when changing asserted behavior; frontend verification commands; quality-gate handoff). |
| `requirements-writer` | **Keep** (unchanged) | Clean lane. |
| `code-reviewer` | **Retire → merge into `quality-architect`** | Every duty preserved (§6.1). The retirement exists to give the three orphaned responsibilities (refactoring, test architecture, quality gates) a single owner alongside review — they belong together because they share one concern: maintainability. |
| `quality-architect` | **New** (merge + expansion) | Review mode (all of code-reviewer), refactor mode (new, was orphaned), test-architecture mode (new, was orphaned), engineering-standards ownership of coding-guidelines.md (was weakly implied). |
| `backend-implementer` | **New** (split from the implicit main-session role) | Codifies backend delivery patterns and sandbox constraints (F-3) the way ui-implementer already did for frontend. |
| Orchestrator | **New — implemented as a main-session protocol (`/orchestrate`), not a subagent** | Claude Code subagents cannot invoke other subagents, so a persona-wrapped orchestrator could coordinate nothing — the same "wrapping a built-in capability in a persona adds a layer without adding value" reasoning CLAUDE.md already applied to git. The main session **is** the orchestrator; `/orchestrate` makes its workflow deterministic and inspectable instead of implicit. |
| `/quality-gate` | **New command** | Makes the review/verification gate deterministic (fixed order, fails closed, explicit deferred-to-CI status for what the sandbox can't run) instead of per-session judgment (F-1). |
| `/update-docs`, `/new-adr`, `/new-game`, `/test` | **Keep** (unchanged) | All verified useful in practice by CHANGELOG history. |

---

## 4. Final organization

### 4.1 Org chart

```
                     Human (product owner)
                            │
                   ORCHESTRATOR (main session,
                    protocol = /orchestrate)
        ┌───────────┬───────┼────────────┬──────────────┐
   Product        Delivery            Protection      Stewardship
        │           │                     │              │
 requirements-  backend-implementer  architecture-    doc-sync
 writer         ui-implementer       reviewer
                game-scaffolder      quality-architect
                test-writer            (review · refactor ·
                                        test architecture ·
                                        standards · gates)
```

- **Implementation agents deliver** (backend-implementer, ui-implementer,
  game-scaffolder, test-writer).
- **Architecture agents protect structure** (architecture-reviewer).
- **Quality agents protect maintainability** (quality-architect).
- **The orchestrator coordinates** and avoids direct implementation;
  it retains only glue no agent owns — notably git/PR operations, kept
  un-personified by prior deliberate decision (CLAUDE.md).

### 4.2 Orchestration model

The main session, running `/orchestrate`:
intake (classify + MVP-SCOPE tier check + REQ mapping) → decompose
(dependency-ordered sub-tasks, one story per session/PR) → route (fixed
ownership table) → track (visible checklist) → `/quality-gate` (fixed
order, fails closed) → `/update-docs` → completion validation against
CLAUDE.md's definition of done, item by item.

### 4.3 Ownership matrix — after (every responsibility, exactly one owner)

| Responsibility | Owner |
|---|---|
| Intake, scope/tier enforcement, REQ mapping, decomposition, routing, tracking, completion validation | Orchestrator (main session via `/orchestrate`) |
| Requirements authoring + review (testability, ID stability, §5/§7 discipline) | requirements-writer |
| Backend delivery (C#/ASP.NET Core, EF Core, CLI verbs, DataSync) | backend-implementer |
| Frontend delivery (tokens, accessibility floor, copy voice, design write-back) | ui-implementer |
| New game module scaffolding (IGameModule boundary, doc stubs, REQ-block allocation) | game-scaffolder |
| Per-REQ test delivery (naming, one test per acceptance branch, coverage reporting) | test-writer |
| Test architecture (fake/fixture/builder strategy, shared helpers, test-host patterns, flakiness, execution speed, E2E strategy) | quality-architect |
| Quality review (coding-guidelines compliance, duplication, error handling, readability) | quality-architect |
| Refactoring (deliberate, behavior-preserving, own commits) | quality-architect |
| Engineering standards (`docs/coding-guidelines.md` enforcement and evolution) | quality-architect |
| Quality-gate definition and verdicts | quality-architect (definition) via `/quality-gate` (execution) |
| Architecture boundaries, data flows, ADR-needed flagging | architecture-reviewer |
| Doc/CHANGELOG sync, frontmatter discipline | doc-sync |
| Git/PR operations and conventions | Orchestrator (native capability; conventions in CLAUDE.md) |
| Design-token system content | `docs/design-document.md` (ui-implementer enforces, never invents) |
| Informal gotcha capture | `NOTES.md` (anyone writes; not agent-owned, by prior decision) |

### 4.4 Standard workflows

- **Story/feature/bug** → `/orchestrate` (the default path for any unit
  of work).
- **Pre-merge check only** → `/quality-gate`.
- **Docs drifted** → `/update-docs`.
- **New structural decision** → `/new-adr`.
- **New game** → `/new-game`.
- **Run the suite** → `/test`.

Direct agent invocation remains available and legitimate for narrow
tasks ("use test-writer to cover REQ-210") — the commands make the
common paths deterministic; they don't forbid the short ones.

---

## 5. Knowledge transfer matrix — retired/changed owners

### 5.1 `code-reviewer` (retired) → every responsibility's new home

| Responsibility / knowledge | New owner |
|---|---|
| Review against `docs/coding-guidelines.md` (its designated enforcement agent) | quality-architect (review mode) |
| Duplication check, incl. COMP-06 override-merge + REQ-208 name-normalization as canonical hot-spots | quality-architect (review mode — examples preserved verbatim) |
| Error-handling review, incl. REQ-902 debuggability lens | quality-architect (review mode) |
| Test-coverage-gap flagging → hand off to test-writer, don't write tests | quality-architect (review mode — handoff preserved) |
| Readability / why-not-what comments lens | quality-architect (review mode) |
| "Don't re-check architecture boundaries; defer to architecture-reviewer" | quality-architect (header rule) |
| "Don't rewrite code during review; refactoring is a separate deliberate step" | quality-architect (review-mode rule; the separate step itself now owned by refactor mode) |
| "Don't enforce undocumented style; propose guideline additions instead" | quality-architect (review mode + standards ownership — upgraded from "say so" to "propose the addition, you own the doc") |
| Output format (Issues / Suggested refactors / Coverage gaps / say-plainly-if-clean) | quality-architect (preserved, plus "proposed guideline additions" section) |

### 5.2 Previously orphaned or log-buried knowledge → first-class owners

| Knowledge / responsibility (previous location) | New owner |
|---|---|
| Refactoring ownership (nobody) | quality-architect (refactor mode) |
| Fake/mock strategy: hand-rolled `Fake*`, no mocking framework, per-project placement, promote-on-third-duplication (emergent convention, unwritten) | quality-architect (test-architecture mode) |
| Fixture/builder strategy: none-yet-by-design, introduce on real pain (unwritten) | quality-architect (test-architecture mode) |
| EF InMemory can't translate `ExecuteUpdate/DeleteAsync` (NOTES.md 2026-07-14, coding-guidelines) | quality-architect (police) + backend-implementer (follow) — doc remains source of truth |
| Request-scoped DbContext concurrency trap: passes InMemory, throws on Npgsql (ADR-0023 follow-up note) | backend-implementer |
| CLI-verb-not-endpoint pattern for jobs; ~240s ingress timeout; `minReplicas: 0` (ADR-0022/0024) | backend-implementer |
| Shared HttpClient config helpers, don't hand-duplicate registrations (S-036 review finding) | backend-implementer |
| No `dotnet` SDK in sandbox → hand-trace, follow patterns, defer to CI, report honestly (S-018/S-022/S-028/S-029 precedent, CHANGELOG only) | backend-implementer + quality-architect + `/quality-gate`'s "deferred to CI" status |
| No Docker daemon → no real-Postgres tests exist (NOTES.md S-013) | backend-implementer |
| No wikidata.org access → QIDs unverifiable here, guessed QIDs were silently wrong (S-036/S-037) | backend-implementer ("flag every QID for manual human verification") |
| E2E specs only run in CI; behavior changes silently break them (S-029) | quality-architect (test-architecture mode) + ui-implementer (grep-and-update rule) |
| Problem-details + `/internal/*` raw-detail carve-out conditions (coding-guidelines 0.2) | backend-implementer (follow) + quality-architect (check conditions hold) |
| One-story-per-session/PR; work top-to-bottom; don't pull Tier 1 forward (backlog header) | Orchestrator (`/orchestrate` steps 1–2 — backlog header remains authoritative) |
| Definition-of-done validation (CLAUDE.md prose, unenforced) | Orchestrator (`/orchestrate` step 7, item-by-item) |
| Which review passes run before merge, in what order (per-session judgment) | `/quality-gate` (fixed order, fails closed) |

### 5.3 Explicitly NOT moved (deliberate)

- **Doc content stays in docs.** Agents reference
  `coding-guidelines.md`/ADRs/NOTES.md rather than duplicating them
  wholesale; where an agent file repeats a fact, the doc remains the
  source of truth and the agent copy is a pointer with just enough
  context to trigger the lookup. This is the drift-avoidance strategy:
  one authoritative home per fact.
- **Git/PR stays un-personified** (prior deliberate decision, CLAUDE.md).
- **`NOTES.md` stays human/main-session-written**, not agent-owned.
- **The `frontend-design` platform skill** is external and unchanged.

---

## 6. Historical references

`docs/backlog.md`, `docs/CHANGELOG.md`, `docs/requirements-document.md`,
`docs/design-document.md`, `docs/review-2026-07-07.md`, and two frontend
test comments mention `code-reviewer` passes by name. Those are accurate
historical records of passes that ran under the old organization and are
deliberately left untouched — never rewrite history to match a rename.
Read any historical "`code-reviewer` pass" as "the review lane now owned
by `quality-architect`."

## 7. Revisit criteria

Same discipline review-2026-07-07 applied to the original seven agents:
the new pieces (`/orchestrate`, `/quality-gate`, quality-architect's
refactor and test-architecture modes, backend-implementer) are unproven
until they've run through real stories. After ~5 stories under the new
organization, check: does `/orchestrate` get used or bypassed? Has
quality-architect actually introduced/consolidated shared test
infrastructure, or only reviewed? Prune or adjust based on evidence, and
record the adjustment here.
