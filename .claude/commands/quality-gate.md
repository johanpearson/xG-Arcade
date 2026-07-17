---
description: Run the deterministic pre-merge quality gate — architecture review, quality review, full test suite, doc-sync check — and report a pass/fail verdict per gate
---

Run the pre-merge quality gate against the current diff (working tree +
branch commits vs. the base branch). The gates run in a fixed order and
the gate **fails closed**: an unresolved finding or an unrunnable check
is reported as not-passed, never waved through.

1. **Architecture gate** — run the `architecture-reviewer` subagent on
   the diff. It checks module boundaries, data flows, and whether the
   change needs an ADR (`docs/architecture-document.md`,
   `docs/decisions/`).
2. **Quality gate** — run the `quality-architect` subagent (review mode)
   on the diff. It checks `docs/coding-guidelines.md` compliance,
   duplication, error handling, readability, and test coverage gaps.
3. **Resolve findings** — route each finding to its owner
   (`backend-implementer`, `ui-implementer`, `test-writer`,
   `quality-architect` for refactors/test infrastructure,
   `requirements-writer` for wrong requirement text). Re-run the
   affected gate after fixes. A finding may be explicitly waived only by
   the person, not by an agent.
4. **Test gate** — run the full suite per `/test`. In this sandbox the
   backend suite needs the `dotnet` SDK and E2E needs a local stack —
   for whatever can't run here, say so explicitly and mark the gate
   "deferred to CI", never "passed".
5. **Doc gate** — confirm docs are in sync (`/update-docs` if any
   behavior, boundary, or data-model reality changed; CHANGELOG entry if
   any doc changed).

Finish with a verdict table: each gate → passed / failed / deferred to
CI, plus the affected REQ IDs and any findings left open. The work is
not done while any gate is failed or any finding is unresolved and
unwaived.
