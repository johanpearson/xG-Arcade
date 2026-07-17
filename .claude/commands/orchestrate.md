---
description: Run a story, feature request, or bug through the full engineering workflow — intake, scoping, decomposition, delegation to the right agents, quality gate, docs, completion validation
---

You (the main session) are the **orchestrator** for this piece of work:
you coordinate, delegate, track, and validate — you avoid doing the
implementation work directly whenever a delivery agent owns it. (The
orchestrator is deliberately the main session, not a subagent: subagents
can't delegate to other subagents, and coordination without the power to
delegate is just narration. See `docs/ai/agent-migration-plan.md` §4.)

Work the phases in order; don't skip ahead.

## 1. Intake

Classify the request: backlog story (S-xxx) / new feature idea / bug.

- Read `docs/CHANGELOG.md` (recent entries) and, for a story,
  its `docs/backlog.md` entry including dependencies.
- Check `MVP-SCOPE.md`: if the work is Tier 1/2 and not an explicitly
  pulled-forward backlog story, stop and flag it instead of building it.
- Map the work to REQ-xxx (and COMP-xxx) IDs. A feature with no REQ goes
  to `requirements-writer` **before any code**. A bug that contradicts a
  REQ's acceptance criteria is a code fix; a bug in the REQ itself goes
  to `requirements-writer`.

## 2. Decompose

Break the work into sub-tasks with explicit dependency order. Keep it to
**one story per session/PR** (`docs/backlog.md`'s rule) — if intake
revealed multiple stories, pick one and queue the rest as backlog
entries, don't bundle.

## 3. Route

Delegate each sub-task to its owner:

| Work | Owner |
|---|---|
| Requirements drafting/review | `requirements-writer` |
| Backend code | `backend-implementer` |
| Frontend screens/components | `ui-implementer` |
| A brand-new game module | `game-scaffolder` (via `/new-game`) |
| Per-REQ test coverage | `test-writer` |
| Refactoring / shared test infrastructure | `quality-architect` |
| Doc sync | `doc-sync` (via `/update-docs`) |

The orchestrator itself only does glue that no agent owns (git
operations, wiring a delegated backend change to a delegated frontend
change, answering agents' questions from conversation context).

## 4. Track

Maintain a visible checklist of sub-tasks and their status as you go, so
the person can see where the work stands at any point.

## 5. Quality gate

Run `/quality-gate` on the combined diff before considering the work
done. Findings route back through step 3 by ownership — the orchestrator
doesn't fix review findings itself when a delivery agent owns the area.

## 6. Documentation

Run `/update-docs`. ADR via `/new-adr` for any decision that could
reasonably have gone another way. CHANGELOG entry if any doc changed.

## 7. Completion validation

Verify CLAUDE.md's definition of done explicitly, item by item: tests
pass (state exactly which suites actually ran in this sandbox and which
only run in CI), docs updated or confirmed unaffected, CHANGELOG entry
present if docs changed, ADR present if a structural decision was made.
Report the result against the original request — including anything
descoped or deferred, stated plainly.
