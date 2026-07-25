# ADR-0038: Guest account cleanup reuses `IAccountDeletionService`; activity tracked via a new `User.LastActiveAt`, updated only on genuine engagement

- **Status:** Accepted
- **Date:** 2026-07-25
- **Related requirements:** REQ-718 (guest account lifecycle cleanup),
  REQ-710 (account deletion — the anonymize-and-keep mechanism this
  decision reuses unmodified), REQ-717 (guest play), REQ-201 (submit a
  guess), REQ-204 (uniqueness), REQ-409 (all-time median leaderboard),
  REQ-715 (persistent login/refresh token — the closest existing
  precedent for what "logging out" currently means in this system)
- **Related components:** COMP-01 (Core.Users), COMP-03 (Core.Rounds —
  the existing scheduled-job/cron precedent this decision follows)

## Context

REQ-718 requires three related cleanup behaviors for guest accounts
(REQ-717/ADR-0036): delete a guest's account at logout, purge unclaimed
guests after 30 days, and purge inactive guests after 7 days. None of
these can be built as drafted without first deciding three things that
could each reasonably have gone another way:

1. **What "inactive" means, mechanically.** `User` (`backend/src/
   XGArcade.Data/Entities/User.cs`) has no activity-tracking field today —
   only `CreatedAt`. A field has to be added, and something has to decide
   *when* it updates. Too broad (every request) adds a database write to
   every single API call in the system for a signal only this one cleanup
   feature needs; too narrow (e.g. explicit login only) risks incorrectly
   purging a guest who is still actively playing a long session under a
   persistent, auto-refreshed login (REQ-715/ADR-0033) without ever
   hitting an explicit "login" event again.
2. **Hard-delete or reuse REQ-710's anonymize-and-keep mechanism.** A
   guest's `Guess` rows are, by REQ-717/ADR-0036's own design, completely
   ordinary rows that count fully toward REQ-204's live uniqueness
   calculation and REQ-409's (for non-guest rows) qualifying totals — the
   same "other players' historical denominators depend on the total guess
   count staying intact" property REQ-710 already solved for real
   accounts via `IAccountDeletionService`/`AccountDeletionService`
   (`backend/src/XGArcade.Core/Auth/AccountDeletionService.cs`). Building
   a second deletion code path for guests specifically would duplicate
   that mechanism for no benefit, and risks the two silently drifting
   apart over time (the same class of risk ADR-0007 already rejected once
   for autocomplete-vs-correctness-checking).
3. **How "delete at logout" is actually triggered.** REQ-715's own status
   note records that logout today is **entirely client-side** —
   `App.tsx`'s `handleLogout` clears `localStorage` and nothing else; no
   backend logout endpoint exists at all. REQ-718's first rule cannot be
   satisfied without introducing one, and that endpoint's failure modes
   (the call never reaches the backend because the browser closed first;
   the call reaches the backend but fails) need a deliberate answer, not
   an implicit assumption that the call always succeeds.

## Decision

**1. New field: `User.LastActiveAt` (nullable at the type level in the
migration, but always populated from `CreatedAt` at insert time — so no
row is ever actually null in practice).** It is updated on exactly four
events, each already an existing authenticated write path: successful
login (`POST /auth/login`), guest provisioning (`POST /auth/guest`),
guest claim (`POST /auth/claim`), and a submitted guess (REQ-201). It is
**not** updated on any read-only request (leaderboard views, fetching the
current grid). This mirrors the same discipline ADR-0036 already
established for `IsGuest` itself ("consulted in exactly one place") —
`LastActiveAt`'s write path has no `IsGuest` branch at all; it updates
identically for every account. Only REQ-718's rule 3 (the purge job)
filters by `IsGuest` when deciding what to act on.

**2. Guest cleanup reuses `IAccountDeletionService.DeleteAccountAsync
(userId)` unmodified**, for all three of REQ-718's rules. That method
already identifies its target purely by local `User.Id` — not by a
password or JWT, which a background sweep has neither of, and which a
logout-triggered call doesn't strictly need either, since the caller's own
session already resolves `User.Id`. No second `IGuestCleanupService` or
equivalent is introduced.

**3. Logout-triggered deletion is a new, guest-only backend call, made
best-effort, with the two scheduled purges as its safety net.** A new
backend logout path (or an addition to whatever logout mechanism is
introduced) checks `IsGuest`/`ClaimedAt` and, only for an unclaimed guest,
calls `IAccountDeletionService.DeleteAccountAsync` synchronously before
the logout response is returned. This does not change logout for a
claimed or non-guest account — REQ-715's existing frontend-only,
clear-`localStorage` behavior is untouched for those. If the call never
reaches the backend, or fails once it does, the account is not left
behind forever: REQ-718's rule 3 (7-day inactivity purge) will
independently catch it once `LastActiveAt` is old enough, without either
rule's correctness depending on the other. The two scheduled purges
(rules 2 and 3) run as a bearer-token-protected internal endpoint
triggered by a new GitHub Actions cron workflow — the identical pattern
`generate-round.yml`/`POST /internal/generate-round` (ADR-0022/ADR-0027)
already established for Tier 0's only other production-scheduled trigger,
not a new scheduling mechanism.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| `LastActiveAt` updated on every authenticated request (a middleware-level touch) | Simplest to reason about — "active" always means "the most recent request," no event list to maintain | A database write on every single API call in the system (including read-only leaderboard polling) purely to serve one cleanup feature's 7-day window; disproportionate cost for the signal actually needed | The four explicit events (login/guest-create/claim/guess) already cover every case that matters for "is this guest still playing," at a small fraction of the write volume |
| `LastActiveAt` updated on login only (no guess-submission event) | Fewer write sites; simpler mental model ("active" = "has signed in recently") | A guest playing a long session under REQ-715's persistent, auto-refreshed login may never trigger a fresh explicit login event again — risks the 7-day purge deleting an account mid-session that a person is still actively using | Fails the actual goal (don't purge someone still playing) for the specific case guest play's own persistent-login mechanism makes common |
| A second, guest-specific deletion path (e.g. a raw `DELETE FROM "User" WHERE Id = @id` in the cleanup job, bypassing `IAccountDeletionService`) | Slightly less code to route through an existing service; a cleanup job "owns" its own deletion logic end to end | Reintroduces the exact corruption risk REQ-710 already solved (hard-deleting `Guess` rows corrupts other players' uniqueness/leaderboard denominators) — a guest's guesses are ordinary rows by design (REQ-717/ADR-0036), so this is not a smaller-stakes case; also a second place the anonymize-and-keep logic could drift from REQ-710's | Duplicates a mechanism that already exists and already does exactly what's needed |
| Logout-triggered deletion treated as the *only* mechanism (no scheduled purge as a safety net) | One less scheduled job to build/maintain | A browser closing before the logout call completes (a normal, common case — closing a tab is not the same as clicking "log out") would leave the account permanently un-purged, defeating REQ-718's stated purpose | REQ-718 explicitly requires the 30-day and 7-day purges regardless; treating logout as sufficient on its own isn't an option available under the actual requirement |
| A dedicated new `IGuestCleanupService` that itself calls `IAccountDeletionService` internally, rather than the scheduled job/logout path calling it directly | Slightly more indirection, arguably matches "one service per REQ" tidiness | No behavioral difference from calling `IAccountDeletionService` directly at the two call sites (logout, scheduled job) — an extra layer with no boundary it actually protects, since both callers already have exactly what `IAccountDeletionService` needs (a `User.Id`) | Added indirection with no corresponding benefit; can be introduced later if a third caller ever needs shared guest-selection logic (e.g. the 30-day/7-day query itself), which is a real candidate for its own small helper but is not the same thing as wrapping the deletion call |

## Consequences

- Positive: REQ-710's existing anonymize-and-keep mechanism, already
  proven and tested, is the only account-deletion code path in the system
  — REQ-718 adds callers, not a second implementation.
- Positive: `LastActiveAt`'s write path needs no `IsGuest` branching,
  consistent with ADR-0036's existing discipline that `IsGuest` is
  consulted in exactly one place (now two: REQ-409's qualifying-rounds
  query, and REQ-718's purge-selection query) — never inside the
  guessing/scoring/leaderboard code paths themselves.
- Positive: the scheduled-purge mechanism follows an already-proven
  pattern (`generate-round.yml`/`/internal/generate-round`,
  ADR-0022/ADR-0027) rather than inventing a new scheduling approach.
- Negative / trade-off accepted: this is the first time any backend
  logout call exists in this system at all — REQ-715 was built and shipped
  assuming logout is purely client-side. Introducing a backend call here,
  scoped only to unclaimed guests, is new surface area (a new endpoint or
  a new branch in whatever endpoint is introduced) that non-guest logout
  does not need and must not be affected by.
- Negative / trade-off accepted: `LastActiveAt` is tracked for every
  account, not only guests, even though only REQ-718's rule 3 currently
  reads it for non-guest accounts. This is deliberate (a single,
  unconditional write path is simpler and safer than a guest-only one) but
  means a column exists that most accounts never have a feature consuming
  it for — acceptable, and consistent with `IsGuest` itself having exactly
  one consumer despite being a general-purpose column.
- Negative / trade-off accepted: guest cleanup depends on two independent
  time-boxed rules (30-day unclaimed, 7-day inactive) rather than one —
  more to test and reason about than a single rule, but each catches a
  distinct case the other doesn't (see REQ-718's "Interaction between
  rules 2 and 3").
- Follow-up: this decision does not specify the exact new logout
  endpoint's shape, nor the exact new internal purge endpoint's route —
  those are `architecture-document.md`/`implementation-document.md`
  concerns to record when this is actually built, not fixed here.
- Follow-up: adding `User.LastActiveAt` is a schema change and a new
  scheduled job/endpoint — `architecture-document.md` (COMP-01's data
  flow, and a new §6 entry alongside the existing round-generation flow)
  and `implementation-document.md` (the data model, the new endpoint, the
  new GitHub Actions workflow) both need updating when this is
  implemented; not done as part of this ADR, which is documentation-only.
- Follow-up: adding a new tracked timestamp is a (small) new piece of
  collected data — `docs/legal/*.md` should be checked against this the
  same way any other new data-collection change already must be, per
  `CLAUDE.md`'s legal-drafts rule, when this is implemented.

## For AI agents

Never add a second account-deletion code path for guests — every caller
that needs to delete a guest account (logout, the scheduled purge job, any
future admin path) must call `IAccountDeletionService.DeleteAccountAsync`.
Never add an `IsGuest` branch anywhere inside REQ-201-210/204/406/407/408's
logic to support `LastActiveAt` tracking — the four update events listed
above are ordinary write paths already, and `LastActiveAt` updates
unconditionally within them; the *only* place `IsGuest` is consulted for
this feature is the purge job's own selection query, alongside REQ-409's
existing exception (ADR-0036). If a future change seems to need
`LastActiveAt` updated on some additional event, re-check the trade-off
made here (broad tracking vs. write cost) rather than assuming more
tracking is automatically better.
