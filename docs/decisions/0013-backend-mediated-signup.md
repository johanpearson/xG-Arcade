# ADR-0013: Backend-mediated signup/login, not frontend-direct-to-Supabase

- **Status:** Accepted
- **Date:** 2026-07-09
- **Related requirements:** REQ-701 (checkbox clause only — see S-004, `docs/backlog.md`)
- **Related components:** COMP-01 (Core.Users)

## Context

S-004 needs "signup blocked without checkbox" to be a real, API-level gate
(REQ-701: "signup cannot proceed unchecked"), not just a disabled button in
a UI that hasn't been built yet (Epic 0 has no frontend auth screen —
that's S-010+). Two ways to wire "signup/login via Supabase Auth" existed:

1. The frontend calls Supabase Auth directly (its JS client, using the
   project's URL + publishable anon key) and the backend only validates the
   resulting JWT.
2. The backend proxies signup/login by calling Supabase Auth's REST API
   itself, so the checkbox check can run *before* any identity is created.

`architecture-document.md` §6.4 already documents flow (2): "Player submits
signup form → Core.Users (COMP-01) → Auth provider (Supabase Auth): create
unconfirmed identity." `MVP-SCOPE.md`'s precondition secrets list, however,
only provisions `DEV_SUPABASE_JWT_SECRET` for the backend — no project URL
or anon key — which reads as if the backend was never meant to call
Supabase's API directly. That gap made this a real decision rather than
"just implement what's already written down."

## Decision

Backend-mediated: `XGArcade.Api` exposes `POST /auth/signup` and
`POST /auth/login`, which proxy to Supabase Auth's REST API
(`/auth/v1/signup`, `/auth/v1/token?grant_type=password`) using two new
non-secret-but-still-configured values, `Supabase:Url` and
`Supabase:AnonKey` (the anon key is a publishable client key by Supabase's
own design, safe in a container's env vars the same way it would be safe
embedded in frontend JS — see `implementation-document.md` if this is ever
questioned). `POST /auth/signup` validates the 16+ checkbox **before**
calling Supabase at all — an unchecked box never reaches Supabase, so no
identity is ever created. `MVP-SCOPE.md`'s precondition list and
`SETUP.md`/`infra/README.md` are updated in the same change to include
these two new values, closing the gap rather than leaving it silently
inconsistent with `architecture-document.md`.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Frontend calls Supabase directly, backend only validates JWTs | No new backend secrets; less backend code | The checkbox gate becomes a client-side-only check (bypassable, untestable at the API level); doesn't match REQ-701's "signup cannot proceed unchecked" as a server-enforced rule; contradicts `architecture-document.md` §6.4's documented flow | REQ-701's accept criteria (`docs/backlog.md` S-004) explicitly wants an API-level test, not a UI-only one |
| Backend writes directly to Supabase's `auth` schema via the existing Postgres connection | No new secrets at all (reuses `ConnectionStrings:Database`) | Bypasses GoTrue's password hashing/session/business logic entirely — exactly what "password credentials live in Supabase Auth, not here" (implementation-document.md §5) exists to prevent | Would silently re-implement (badly) what Supabase Auth already does correctly |

**`Auth:Mode=local-e2e` for CI, discovered mid-implementation:** `ci.yml`'s
`e2e-tests` job already carried an `Auth__Mode: "local-e2e"` env var and a
comment — "bypasses Supabase JWT validation with a local test signer —
never enabled outside Development" — written ahead of this story by S-002,
anticipating it. `ci.yml`'s local E2E stack has no live Supabase project at
all, so backend-mediated signup/login would otherwise crash the API on
startup in CI (missing `Supabase:Url`/`Supabase:AnonKey`). `Program.cs` now
re-checks `Auth:Mode == "local-e2e" && IsDevelopment()` itself (not trusting
the config flag alone — same gating principle ADR-0006 established for
COMP-09's `Testing.SeedManager`) and, only then, swaps in
`LocalE2EAuthClient` (a fake `ISupabaseAuthClient` that mints a locally
HS256-signed JWT, no real password check) and validates JWTs against that
same local signing key/issuer instead of Supabase's. This is what actually
makes backend-mediated signup practical in CI — a frontend-direct design
would have needed the frontend itself to grow an equivalent fake-Supabase
swap.

## Consequences

- Positive: REQ-701's checkbox gate is a real, testable, server-enforced
  rule; matches the architecture doc's already-documented flow instead of
  drifting from it; login also gets a stable backend surface for a future
  frontend to call, rather than depending on the frontend embedding
  Supabase project details itself; `ci.yml`'s local E2E stack (and S-010's
  future login E2E test) can exercise the real `/auth/signup`→`/auth/me`
  path without a live Supabase project, via `Auth:Mode=local-e2e`.
- Negative / trade-offs accepted: two new configuration values
  (`Supabase:Url`, `Supabase:AnonKey`) needed threading through
  `infra/bicep`, GitHub Actions secrets, `SETUP.md`, and
  `MVP-SCOPE.md`'s precondition list — a small amount of infra surface
  `MVP-SCOPE.md` hadn't anticipated; a second, test-only auth code path
  (`LocalE2EAuthClient`) now exists alongside the real one, which is extra
  surface to keep in sync if the real client's contract ever changes.
- Follow-up: REQ-702–705 (email confirmation, resend, expiry) remain
  deferred per `MVP-SCOPE.md`; when they're built, they extend this same
  `POST /auth/*` surface rather than moving auth to a different mediation
  model.

## For AI agents

Signup and login are backend endpoints (`XGArcade.Api`'s `AuthController`),
not something the frontend calls Supabase for directly. The 16+ checkbox
check must run and be able to reject the request before any call to
Supabase Auth's REST API is made — don't reorder this so the checkbox is
checked after an identity already exists. If a future story needs the
frontend to talk to Supabase directly for some other reason, that
supersedes this ADR — don't do it silently.

`LocalE2EAuthClient`/`Auth:Mode=local-e2e` is test-only plumbing for
`ci.yml`'s local stack, gated by a real `IsDevelopment()` check in
`Program.cs`, not just the config flag. Never relax that gate, and never
route real signup/login traffic through it outside CI.
