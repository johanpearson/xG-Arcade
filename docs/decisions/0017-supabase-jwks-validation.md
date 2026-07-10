# ADR-0017: Validate Supabase JWTs against its JWKS endpoint, not a static shared secret

- **Status:** Accepted
- **Date:** 2026-07-10
- **Related requirements:** REQ-606 (security — JWT validation)
- **Related components:** COMP-01 (Core.Users), the JWT bearer middleware in `XGArcade.Api`

## Context

After S-010 shipped a real login UI, a live test against the deployed dev
environment surfaced a genuine production bug: a user could sign up and
Supabase confirmed the login succeeded, but every subsequent authenticated
request (starting with `GET /rounds/current`) was rejected, silently
bouncing the player back to the login screen with no visible error. Live
log-stream debugging (with `Microsoft.AspNetCore` logging temporarily
raised to `Information` to see past the deployed default's `Warning`
suppression) surfaced the real cause:

```
Microsoft.IdentityModel.Tokens.SecurityTokenSignatureKeyNotFoundException:
IDX10503: Signature validation failed. The token's kid is:
'7bf6236f-4437-43fe-bf1d-756e6476998a', but did not match any keys in
TokenValidationParameters or Configuration. Keys tried:
'Microsoft.IdentityModel.Tokens.SymmetricSecurityKey...'.
Number of keys in TokenValidationParameters: '1'.
Number of keys in Configuration: '0'.
```

`Program.cs`'s real-Supabase JWT validation branch (built under ADR-0013)
only ever checked tokens against a single static HS256 symmetric secret
(`Auth:SupabaseJwtSecret`, copied by hand from Supabase's dashboard). The
`kid` (key ID) header claim on the real token is the tell: this Supabase
project issues tokens signed with Supabase's newer **JWT Signing Keys**
system — rotating asymmetric keys, verified via a published JWKS (JSON Web
Key Set) endpoint, not a shared secret at all. No secret *value* could
ever have fixed this; it's a structural mismatch between what the code
assumed and how Supabase actually signs tokens today. ADR-0013 itself
never asserted a signing algorithm — this was an undocumented
implementation gap, not a documented decision being violated.

## Decision

Replace the static-secret `TokenValidationParameters.IssuerSigningKey`
with the framework's own async, auto-refreshing configuration machinery:
`JwtBearerOptions.ConfigurationManager` set to a
`ConfigurationManager<OpenIdConnectConfiguration>` fed by a new
`SupabaseJwksConfigurationRetriever` (`XGArcade.Api.Auth`). Supabase does
not appear to expose a full OpenID Connect discovery document — only the
bare JWKS at `{Supabase:Url}/auth/v1/.well-known/jwks.json` — so this
retriever fetches that document directly and wraps it in a minimal
`OpenIdConnectConfiguration`, explicitly populating `.SigningKeys` from
`JsonWebKeySet.GetSigningKeys()` (verified directly against this project's
resolved `Microsoft.IdentityModel.Protocols.OpenIdConnect` 8.0.1: setting
`.JsonWebKeySet` alone does **not** populate `.SigningKeys` — that's not
documented behavior, just what the setter actually does, and the first
version of this fix got it wrong until a unit test caught it).

The JWKS path is configurable (`Auth:SupabaseJwksPath`, default
`/auth/v1/.well-known/jwks.json`) rather than a bare literal, and both a
startup log line (fires unconditionally at boot, before anyone can log in)
and a `JwtBearerEvents.OnAuthenticationFailed` log line (naming the
resolved JWKS address alongside the exception) were added — this whole bug
was hard to diagnose specifically because production's default logging
(`Microsoft.AspNetCore: Warning`) suppresses the framework's own
authentication-failure logging, and because a wrong secret value produces
the same opaque symptom as a structurally-wrong validation approach. If
the documented JWKS path turns out to be wrong on a live deployment, this
is now a one-line `Auth:SupabaseJwksPath` environment variable correction
(Container App env var or Bicep parameter), not a rebuild.

`Auth:SupabaseJwtSecret`/`DEV_SUPABASE_JWT_SECRET` are removed entirely
(Bicep, GitHub Actions secrets, `SETUP.md`, `infra/README.md`) rather than
left as a harmless unused value — no code reads it anymore, and no live
prod deployment exists yet to accidentally depend on it. JWKS-based
validation needs only `Supabase:Url`, already configured.

`Auth:Mode=local-e2e` (CI's fake in-process auth, HS256, no network) is
**unchanged** — this decision only replaces the real-Supabase branch. The
three `XGArcade.Api.Tests` files that previously minted their own JWT
against the now-removed static-secret branch were reconfigured to use
`Auth:Mode=local-e2e` too (via `LocalE2EAuth.MintToken`, a new method
added to the existing test-only signer rather than a fourth
hand-rolled JWT-minting implementation) — API/unit tests must never depend
on live network (`docs/coding-guidelines.md`), and this branch now does.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Hand-rolled synchronous `TokenValidationParameters.IssuerSigningKeyResolver` fetching the JWKS inline | Looks like the most direct fix | Forces either a blocking `.GetAwaiter().GetResult()` call on every cache-miss (violates `docs/coding-guidelines.md`'s "async all the way down, no blocking calls") or a hand-rolled background-refresh cache — reinventing what `ConfigurationManager<T>` already does (TTL caching, coalesced concurrent refresh, last-known-good fallback on transient failure) | Rejected — worse code for no benefit over the framework's own mechanism |
| Full OIDC discovery via the stock `OpenIdConnectConfigurationRetriever` (`options.Authority = ...`) | Zero custom code — the framework handles everything if a discovery document exists | Supabase does not appear to expose a full `/.well-known/openid-configuration` document, only the bare JWKS — this retriever expects a discovery doc containing a `jwks_uri` field and has nothing to point at otherwise | Rejected — no discovery document to rely on |
| Keep the static secret, just fix the *value* | No code change | The real problem is structural (asymmetric vs. symmetric signing), not a wrong value — this was tried first (re-pasting the "legacy JWT" secret, redeploying) and confirmed not to fix anything; the token's `kid` claim proves no static secret can ever validate it | Rejected — doesn't address the actual root cause |

## Consequences

- Positive: matches Supabase's actual production auth model; key rotation
  is handled transparently by the framework's own refresh machinery
  (`RefreshOnIssuerKeyNotFound`, on by default) — no manual secret rotation
  ever needed again; removes a secret from the deployment surface entirely
  (one less value to keep in sync across environments, one less thing that
  can silently drift out of date); the new startup/failure log lines make
  a future misconfiguration of this kind diagnosable from the log stream
  in one check, not several rounds of live debugging
- Negative / trade-offs accepted: one more moving part on the auth hot
  path (an outbound HTTPS call to fetch the JWKS, mitigated by
  `ConfigurationManager<T>`'s built-in caching); the exact JWKS path
  cannot be verified from an isolated dev sandbox with no network access
  to Supabase — it's Supabase's documented endpoint for this system, but
  the real first confirmation only happens on a live deploy
- Follow-up: confirm the JWKS path resolves correctly on the next live
  deploy via the new startup log line; if wrong, correct via
  `Auth:SupabaseJwksPath`, no rebuild needed

## For AI agents

Never reintroduce a static-secret (HS256, or any single fixed key) branch
for real-Supabase JWT validation — Supabase's production tokens are signed
with rotating asymmetric keys identified by a `kid` claim, and this is not
expected to change back. `Auth:Mode=local-e2e` staying HS256/static is
fine and intentional — it's isolated CI-only test infrastructure that
never talks to real Supabase, not a precedent for the production path. If
a new API/unit test needs an authenticated request, mint its token via
`LocalE2EAuth.MintToken`, not a fourth hand-rolled JWT-signing
implementation — and never make an API/unit test depend on live network
JWKS resolution (see `docs/coding-guidelines.md`).
