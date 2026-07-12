# ADR-0019: Silent auto-rename to resolve pre-existing DisplayName collisions during the uniqueness migration

- **Status:** Accepted
- **Date:** 2026-07-11
- **Related requirements:** REQ-701, REQ-401
- **Related components:** COMP-01

## Context

S-017 (`docs/backlog.md`) adds a case-insensitive unique index on
`User.NormalizedDisplayName` (REQ-701). Adding that constraint against a
database that may already contain colliding data is not automatically
safe: some existing rows may still have an empty `DisplayName` (any row
created before S-011's `DisplayName` column existed, if
`UserDisplayNameBackfiller` — which only runs *after* migrations, from
`dotnet run -- migrate-and-seed` — hasn't executed yet in this deploy),
and separately, two different users' chosen or backfilled display names
may already collide case-insensitively (e.g. two accounts whose email
local part is the same on different domains). If the migration simply
added the unique index against this data, it would fail outright on
deploy.

This is a real decision with genuine alternatives, not an obvious
mechanical step — an architecture-reviewer pass on this story's diff
flagged it as exactly the kind of choice CLAUDE.md's ADR test names
("if reverting it would require someone to understand *why* the original
choice was made, it needs an ADR").

## Decision

The migration (`20260711203352_AddDisplayNameUniqueness`) resolves
collisions automatically, as part of `Up()`, before creating the unique
index:

1. Any row with an empty `DisplayName` falls back to its email's local
   part — the same rule `UserDisplayNameBackfiller` already applies, run
   here via raw SQL so it's guaranteed to have happened before the index
   is created regardless of backfiller ordering.
2. Any remaining case-insensitive collision (including one the fallback
   above might itself create) is resolved by appending a short,
   deterministic suffix derived from the row's own `Id` to every row
   after the first, ordered by `CreatedAt` then `Id` — so the earliest
   account keeps its original display name unchanged, and only later,
   colliding rows are renamed.
3. Only then is the unique index created.

This is a one-time, irreversible data fix. `Down()` does not attempt to
reverse it (the same pattern this codebase already accepts for other
migrations that don't reverse data fixes).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Silent auto-rename on collision (chosen) | Migration always succeeds; no deploy blocked on manual intervention; fully idempotent and automatable | A user's displayed name can change without their knowledge or consent | Tier 0 has no real users yet (`MVP-SCOPE.md`'s dev-environment bright line) and no notification system (REQ-706 is Phase 2) to tell them even if we wanted to — a blocking alternative buys safety this story doesn't yet need to pay for |
| Block the migration / require manual admin resolution first | No silent identity change, ever | Turns a routine `migrate-and-seed` deploy into a manual, blocking, per-collision operation; no admin tooling exists yet to even list/resolve collisions (S-012's admin surface doesn't cover this) | Disproportionate for Tier 0's dev-only, pre-real-user stage; revisit once real users exist |
| Reject the deploy and require operator judgement per collision | Most conservative | Same operational cost as above, worse: doesn't even complete automatically | Same reasoning as above |

## Consequences

- Positive: `migrate-and-seed` remains a single, idempotent, unattended
  step — no new manual deploy gate, consistent with every other migration
  in this codebase.
- Negative / trade-offs accepted: a pre-existing user whose display name
  collides with another's (including via the email-local-part fallback)
  gets silently renamed with no notification. Acceptable now because Tier
  0's only environment is "dev," not real users (`MVP-SCOPE.md`); would
  **not** be acceptable once real users exist without a way to tell them.
- Follow-up: **revisit before or alongside REQ-706 (round-result
  notification emails, Phase 2) and before Tier 1's "real prod
  environment" trigger** (`MVP-SCOPE.md`) — once a real, non-test user
  base exists, an unannounced identity rename during a schema migration is
  no longer an acceptable default, and should be replaced with either a
  notification on rename or an admin-driven resolution flow.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
