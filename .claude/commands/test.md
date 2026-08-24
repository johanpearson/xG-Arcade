---
description: Run the full test suite (unit, API, E2E) locally
---

Run the project's full testing workflow locally — Tier 0 has one
environment (prod) and no test-data API, so tests never run against a
deployed environment (see `MVP-SCOPE.md`). This mirrors what `ci.yml` does:

1. Backend unit + API tests: `dotnet test backend/XGArcade.sln`
2. Frontend unit tests: `npm run test` (from `/frontend`)
3. E2E tests: `npm run test:e2e` (from `/frontend`) — runs against the
   local stack (local Postgres + API from source, same shape as `ci.yml`'s
   e2e job). If the local stack isn't running/startable, say so rather
   than reporting a false pass/fail.

Report a summary: what passed, what failed, and for failures, which
requirement ID (if named in the test) is affected — don't just paste raw
test-runner output without a summary on top.

If the sandbox can't run a suite (no `dotnet` SDK, no Docker daemon —
check, don't assume), don't stop at "will run in CI": push the branch and
trigger `ci.yml`'s `workflow_dispatch` run instead, per CLAUDE.md "Testing
without a local dotnet SDK". Check job conclusions first and only pull
logs for jobs that failed, and only the failed portion — token efficiency
matters here, since a full green/red log dump is mostly noise. This is
what actually gets a suite that can't run locally tested before a PR
exists, instead of guessing at correctness.

Tier 1 evolution: once the dev environment and test-data API exist
(ADR-0006, REQ-801-804), this command gains a step to reset dev test data
via `/internal/test-data/reset` and point E2E at the deployed dev env.
