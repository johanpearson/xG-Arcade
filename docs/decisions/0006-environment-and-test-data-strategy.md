# ADR-0006: Two-project environment split, gated test-data API, one-way non-PII sync

- **Status:** Accepted — the "one-way only" sync direction described below
  is superseded by ADR-0009 (sync is now bidirectional for game/reference
  data specifically). The two-project split, allowlist approach, and
  gated test-data API remain unchanged.
- **Date:** 2026-07-04
- **Related requirements:** REQ-606, REQ-607, REQ-801 through REQ-804
- **Related components:** COMP-02 through COMP-08 (test-data reset touches all of them), COMP-09 (new)

## Context

Automated UI/API testing (REQ-601) and manual exploratory testing both need
a non-production environment with data that can be created and reset on
demand, without risking production data. Separately, realistic testing
benefits from non-prod having data that resembles production (real grids,
real player attributes) rather than only hand-crafted fixtures. Supabase's
free plan grants exactly two active projects and no database branching
without a paid add-on (~$0.01344/branch/hour), so branching is not a free
option.

## Decision

- **Two Supabase projects**: one production, one non-production (covers
  local dev pointed at a shared non-prod instance, CI's ephemeral test runs,
  and manual QA). This uses exactly the two free projects the plan allows.
- **A gated test-data API** (COMP-09, `XGArcade.Api` endpoints under
  `/internal/test-data/*`) exists in the codebase but is only registered
  when `ASPNETCORE_ENVIRONMENT != Production` — calling it in production
  returns 404, not just a permissions error, so it's not discoverable as an
  attack surface in prod at all.
- **One-way, non-PII sync from prod to non-prod**: a manual/scheduled job
  copies game/reference data (players, grids, rounds, leagues — anything
  COMP-05/06/07 own) from prod into non-prod, but explicitly **excludes**
  the `User` table, `NotificationPreference`, and anything from Supabase
  Auth. Non-prod test users are always synthetic, created via the test-data
  API (REQ-803), never copied from real accounts. Sync is one-way only —
  nothing ever flows from non-prod back to prod.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Supabase database branching | Official feature, tight integration | Paid add-on, not part of the free plan | Fails REQ-602 cost constraint |
| A single shared project with a "test" flag on rows | No second project needed | Test data intermixed with real data in the same tables/backups; a bug in the flag check risks leaking test data into real leaderboards or vice versa | Too risky for a small mistake to have a big blast radius |
| Full prod data sync including users | Non-prod would behave identically to prod | Copies real emails/PII into a lower-security environment for no testing benefit | Unnecessary privacy exposure; game data alone is enough to test realistically |

## Consequences

- Positive: realistic non-prod data without ever exposing real user PII
  outside production; test-data reset is impossible to trigger in prod by
  construction (endpoint doesn't exist there), not just by permission check
- Negative / trade-offs accepted: two Container Apps + two Static Web Apps
  environments to maintain (still free at this scale, see infra/README.md);
  the sync job is another piece of infrastructure to keep working
- Follow-up: if the project ever needs paid Supabase branching for a more
  sophisticated environment story, this ADR should be revisited rather than
  organically growing extra environments

## For AI agents

Never register `/internal/test-data/*` endpoints (COMP-09) outside a
non-Production environment check — this must be enforced in startup
configuration (`Program.cs`), not just an attribute on the controller, so a
misconfigured attribute can't accidentally expose it. Never include `User`
or `NotificationPreference` rows in any prod→dev sync script (currently
`infra/scripts/sync-prod-to-dev.sh`). If a task seems to require syncing
user data for a good reason, stop and flag it — that's exactly the case
this ADR exists to prevent.

## Addendum, 2026-07-07: concrete environment naming

This ADR's reasoning above used "non-production"/"non-prod" generically.
The concrete environment is named **dev** in all resource names, file
names, and secrets (e.g. `xg-arcade-api-dev`, `main.parameters.dev.json`,
`DEV_DATABASE_CONNECTION_STRING`) — chosen for brevity and because it's
what a developer actually calls the environment day to day. Also added:
dev now redeploys automatically on every PR/push via `ci.yml`'s
`deploy-dev` job (E2E tests depend on it completing), rather than relying
on a manual deploy that could silently go stale. Nothing about the
two-project split or the sync strategy above changed — only the concrete
name and the fact that deployment is now automated rather than manual-only.

## Addendum, 2026-07-07 (later same day): Tier 0 has dev only, prod is Tier 1

`MVP-SCOPE.md` scopes Tier 0 to a single environment — and that
environment is **dev**, not prod: Tier 0 has no backups, no email
confirmation, no legal docs, which is what a dev environment is for, not
a production one. Prod doesn't exist yet; it's created at Tier 1's bright
line (a real user besides you), which also triggers backups/alerting/
legal-docs. `ci.yml`'s automatic dev-redeploy job described just above is
itself Tier 1 too — the committed `ci.yml` is Tier 0-shaped (local-stack
E2E in the workflow itself, no deployed environment involved), and
`deploy.yml` is what currently redeploys dev on every push to `main`.
Nothing about the two-project split's eventual shape changed — only when
each half of it actually gets built.
