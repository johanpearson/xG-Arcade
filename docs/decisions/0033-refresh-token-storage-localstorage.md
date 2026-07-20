# ADR-0033: Refresh token stored in localStorage, same as the access token

- **Status:** Accepted
- **Date:** 2026-07-20
- **Related requirements:** REQ-715
- **Related components:** COMP-01 (Core.Users), CONT-01 (Web Frontend), CONT-02 (Backend API)

## Context

REQ-715 (persistent login / remember-me) needs the frontend to hold onto a refresh
token across page reloads and new browser sessions, and needs a backend-mediated
refresh endpoint so `POST /auth/*` continues to be the only place the frontend talks
to Supabase Auth through (ADR-0013 — already settled; this refresh endpoint is that
ADR's own anticipated `POST /auth/*` extension, not a new mediation decision).

What is not yet decided is where the frontend stores the refresh token. Today,
`frontend/src/App.tsx`'s `ACCESS_TOKEN_STORAGE_KEY` stores the access token in
`localStorage` — the only token-storage pattern in this codebase. A refresh token is
materially different from that access token: it is long-lived (the whole point of
REQ-715) rather than short-lived, so any exposure window (e.g. via XSS) is much wider.

Two real options exist:
1. `localStorage`, matching the existing pattern.
2. An httpOnly cookie, set by the backend on `POST /auth/login`/the new refresh
   endpoint.

This codebase today is a pure bearer-token/JSON API: `Program.cs`'s CORS policy
(`AddCors`, `WithOrigins(...).AllowAnyHeader().AllowAnyMethod()`) does not set
`AllowCredentials()`, and there is no cookie, `SameSite`, `HttpOnly`, or CSRF/
antiforgery infrastructure anywhere in `backend/` (confirmed by search — zero
matches). Frontend and backend are also separate origins (Static Web Apps + Container
Apps, `architecture-document.md` §9), so a cookie here would be a cross-site cookie
(`SameSite=None; Secure`), the option requiring the most new machinery to get right,
including CSRF protection this codebase has never needed before because nothing is
auto-attached to a request today.

## Decision

The refresh token is stored in `localStorage`, under a new key alongside the existing
`ACCESS_TOKEN_STORAGE_KEY` (e.g. `xg-arcade-refresh-token`), read/written by the same
`App.tsx` logout/login handlers that already manage the access token. No new CORS,
cookie, or CSRF infrastructure is introduced.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| `localStorage` (chosen) | Matches the existing access-token pattern exactly; no new CORS/cookie/CSRF surface; smallest diff; one person can reason about the whole auth surface as one mechanism | A genuinely long-lived credential is exposed to any XSS on the frontend, same as the (short-lived) access token already is today, but for much longer | Consistency and no new infrastructure outweigh the incremental XSS exposure at this stage; see Consequences for the mitigations that remain available |
| httpOnly cookie, `SameSite=None; Secure`, set by the backend | Meaningfully more XSS-resistant — JS on the page can never read the token at all | Requires `AllowCredentials()` + credentialed CORS (new), `SameSite=None`/`Secure` cross-site cookie handling (new), and CSRF protection (new — nothing today defends against it, since nothing is auto-attached to requests currently); a second, structurally different auth mechanism alongside the existing pure-bearer-token pattern, for one endpoint's refresh token specifically while the access token stays in `localStorage` either way | More new surface than this stage's risk profile justifies, for a one-person team maintaining an otherwise-uniform bearer-token API; revisit if real usage/threat model changes (see Follow-up) |

## Consequences

- Positive: one storage mechanism, one mental model, for both tokens; no new
  backend cross-cutting concern; `App.tsx`'s existing login/logout/account-deletion
  clearing logic extends directly to the refresh token with no new plumbing.
- Negative / trade-offs accepted: an XSS vulnerability on the frontend now has a
  much longer exposure window than today (refresh-token lifetime vs. access-token
  lifetime) — this is a real increase in blast radius, not a cosmetic one. Mitigated
  only by whatever XSS-prevention already exists in the frontend build (React's
  default JSX escaping) — no additional hardening is introduced by this decision.
- Follow-up: revisit if/when the frontend introduces any third-party script surface
  (analytics, ads, embedded widgets) that meaningfully raises XSS risk, or if a real
  security incident occurs — at that point the httpOnly-cookie alternative's added
  infrastructure cost becomes easier to justify.

## For AI agents

Refresh-token storage in `localStorage` is a deliberate, XSS-trade-off-aware choice,
not an oversight — do not "fix" it to a cookie without a new ADR superseding this one.
The refresh endpoint itself must still go through `POST /auth/*`, mediated the same
way `/auth/login`/`/auth/signup` are (ADR-0013) — never a direct frontend-to-Supabase
call, regardless of where the token ends up stored.
