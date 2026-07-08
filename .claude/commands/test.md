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

Tier 1 evolution: once the dev environment and test-data API exist
(ADR-0006, REQ-801-804), this command gains a step to reset dev test data
via `/internal/test-data/reset` and point E2E at the deployed dev env.
