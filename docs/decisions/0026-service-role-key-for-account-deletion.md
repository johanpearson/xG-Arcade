# ADR-0026: A dedicated `service_role` secret for Supabase Auth account deletion

- **Status:** Accepted
- **Date:** 2026-07-14
- **Related requirements:** REQ-710
- **Related components:** COMP-01 (Core.Users)

## Context

REQ-710 (self-service account deletion, `docs/backlog.md` S-025) requires
that "the user can no longer log in, and their email becomes available for
a new account to register with" — which means actually deleting the
underlying Supabase Auth identity, not just the local `User` row. Every
existing call to Supabase Auth (`SupabaseAuthClient.SignUpAsync`/
`SignInWithPasswordAsync`, ADR-0013) uses the anon/publishable key, which
is deliberately safe to expose (Supabase's own design, safe in a frontend
bundle). Supabase's Admin API — `DELETE /auth/v1/admin/users/{id}`, the
only way to actually delete an identity — rejects the anon key outright and
requires the `service_role` key instead: a genuinely privileged credential
that bypasses Row Level Security entirely. This is qualitatively different
from every secret this backend has held so far, and introducing it is a
real decision, not just "add one more config value" the way `Supabase:Url`/
`Supabase:AnonKey` were under ADR-0013.

## Decision

Add `Supabase:ServiceRoleKey` as a new, explicitly-`@secure()` configuration
value (env var `Supabase__ServiceRoleKey`, GitHub secret
`DEV_SUPABASE_SERVICE_ROLE_KEY`/`PROD_SUPABASE_SERVICE_ROLE_KEY`), used by
exactly one call site: `SupabaseAuthClient.DeleteUserAsync`. Rather than a
second `HttpClient` (which would need `XGArcade.Core` to depend on
`Microsoft.Extensions.Http` for `IHttpClientFactory`, a package it doesn't
otherwise need), the key is set directly on that one call's
`HttpRequestMessage` headers — a request's own header value always wins
over an `HttpClient`'s `DefaultRequestHeaders` for the same header name, so
the anon key configured as `SupabaseAuthClient`'s defaults is never sent on
this specific call. `ci.yml`'s local E2E stack (`Auth:Mode=local-e2e`) never
constructs a real `SupabaseAuthClient` at all — `LocalE2EAuthClient`'s
`DeleteUserAsync` is a no-op returning success — so this secret is never
needed there, matching the existing carve-out for `Supabase:Url`/
`Supabase:AnonKey`.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Defer identity deletion — anonymize/delete local data only, leave the Supabase Auth identity intact | No new secret, no new attack surface | Directly contradicts REQ-710's own acceptance criteria ("can no longer log in," "email becomes available for a new account") — a stale JWT would still validate against JWKS, and the email would stay unavailable for a new signup indefinitely | REQ-710 already exists and specifically calls for this; not something this story gets to quietly narrow |
| A second, separately-configured `HttpClient` (or a named client via `IHttpClientFactory`) | Cleaner separation between "the anon-keyed client" and "the admin-keyed client" as two distinct objects | Requires `XGArcade.Core` to take a dependency on `Microsoft.Extensions.Http` (for `IHttpClientFactory`) purely for one call, and/or a hand-rolled manual DI factory registration in `Program.cs` instead of the existing `AddHttpClient<TClient,TImplementation>` typed-client pattern every other client in this codebase already uses | The per-request header override achieves the identical security property (the anon key is never sent on the admin call) with no new package and no deviation from the established typed-client registration pattern |
| Backend writes directly to Supabase's `auth` schema via the existing Postgres connection | No new secret at all (reuses `ConnectionStrings:Database`) | Same objection ADR-0013 already raised for signup/login: bypasses GoTrue's own logic entirely — deleting an identity out from under GoTrue's internal bookkeeping (sessions, refresh tokens, etc.) risks leaving it in a state Supabase's own tooling doesn't expect | Would silently re-implement (and likely get wrong) what Supabase's Admin API already does correctly |

## Consequences

- Positive: REQ-710's full acceptance criteria (can't log in, email freed
  for reuse) is actually met, not partially deferred; no new package
  dependency added to `XGArcade.Core`; `SupabaseAuthClient` stays registered
  through the same `AddHttpClient<ISupabaseAuthClient, SupabaseAuthClient>`
  typed-client pattern as before, with the key supplied via one extra,
  ordinary DI-resolved constructor parameter (`SupabaseServiceRoleKey`).
- Negative / trade-offs accepted: a new, genuinely sensitive secret now
  exists in this system for the first time (`DEV_SUPABASE_SERVICE_ROLE_KEY`/
  `PROD_SUPABASE_SERVICE_ROLE_KEY`) — unlike the anon key, a leak of this
  one is a real incident (full database access, bypassing RLS). It's
  `@secure()` in Bicep and stored only as a Container App secret / GitHub
  Actions secret, same handling as `DEV_DATABASE_CONNECTION_STRING`, but
  this is new *surface*, not just a new value, and needs threading through
  `infra/bicep`, `deploy.yml`, `infra/README.md`, `SETUP.md`, and
  `MVP-SCOPE.md`'s precondition list — done in this same change per
  ADR-0013's own precedent for new Supabase config values.
- Negative / trade-offs accepted: account deletion is not currently a
  single atomic transaction across the local database and Supabase Auth —
  the Supabase Admin API call happens last, as its own non-transactional
  HTTP call, after the local anonymize/delete writes already committed
  (matches `implementation-document.md` §6.8's already-documented flow). If
  that call fails, the local account data is already gone but the
  credential (and its email) is not — `AccountDeletionService` surfaces
  this as a failure result rather than swallowing it, but no retry/saga
  exists yet. Acceptable at MVP scale (no evidence this failure mode is
  common); revisit if it's ever observed in practice.

## For AI agents

`Supabase:ServiceRoleKey` must never be sent to the frontend, logged, or
used on any call other than `SupabaseAuthClient.DeleteUserAsync`'s Admin
API request. If a future feature seems to need broader Supabase admin
access (e.g. bulk user management), don't just reuse this key more widely —
stop and reconsider the boundary; the fact that it's already configured is
not a reason to widen its use without a fresh look at the blast radius.
