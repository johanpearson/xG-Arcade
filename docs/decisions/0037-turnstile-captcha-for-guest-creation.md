# ADR-0037: Cloudflare Turnstile as the captcha layer for guest creation, wired through Supabase's native captcha-token verification

- **Status:** Accepted
- **Date:** 2026-07-21
- **Related requirements:** REQ-717 (guest play — 2026-07-21 "Bot-check
  (captcha) for guest creation" addition), REQ-606 (security baseline /
  rate limiting, unaffected — see Context)
- **Related components:** COMP-01 (Core.Users)

## Context

ADR-0036 already made guest creation (`POST /auth/guest`) a real,
backend-mediated Supabase Anonymous Sign-in, and flagged — as an accepted
trade-off, not an afterthought — that an anonymous-sign-in endpoint has
strictly less friction than email signup (no address to type, no inbox to
control), making it a cheaper target for scripted mass identity creation
aimed at probing a cell's hidden answer or manipulating REQ-204's
uniqueness denominator. REQ-717 answered that with a dedicated, tighter
`auth-guest` rate-limit policy (3/min per IP by default).

Enabling Supabase's Anonymous Sign-ins feature itself surfaces a dashboard
warning independent of anything this project wrote: "Enable captcha for
anonymous sign-ins — this will prevent potential abuse on sign-ins which
may bloat your database and incur costs for monthly active users (MAU)."
A per-IP rate limit and a captcha check are genuinely different layers,
not redundant ones: the rate limit caps *how fast* one IP can act; a
captcha raises the cost of automating the request at all, including from a
distributed/multi-IP attacker who never trips any single IP's limit. This
ADR decides how to close that specific gap — which provider, and exactly
how its result reaches Supabase's verification — because real alternatives
exist and the choice has actual trade-offs, not because "add a captcha"
by itself needed a structural decision.

`ISupabaseAuthClient.SignInAnonymouslyAsync` (`backend/src/
XGArcade.Core/Auth/ISupabaseAuthClient.cs`) currently takes no
captcha-related parameter, and `AuthController.Guest` currently maps
every rejection from Supabase — including a future captcha rejection —
to the same generic `"Guest sign-in failed"` problem response. Both are
accurate descriptions of the code as it stands today, not gaps this ADR
itself is expected to close; implementing them is `backend-implementer`/
`ui-implementer` work following this decision and REQ-717's newly-added
acceptance criteria.

## Decision

**Cloudflare Turnstile**, wired through Supabase Auth's own native
captcha-token support rather than any custom verification code in this
backend. Supabase's `/auth/v1/signup` endpoint (which
`SignInAnonymouslyAsync` already calls for the no-password anonymous case)
accepts an optional `gotrue_meta_security.captcha_token` field and
verifies it server-side against the configured captcha provider —
Supabase already speaks Turnstile natively, so no new outbound HTTP call
to Cloudflare is written in this codebase at all.

Concretely, this decision has three parts:

1. **Provider: Cloudflare Turnstile**, not hCaptcha and not a
   custom/self-hosted check. Chosen for the reasons in the comparison
   table below — free with no meaningful volume cap for this project's
   scale, less visible/annoying to real players, and a simpler two-key
   integration that Supabase already supports as a first-class option.
2. **Wiring: token flows frontend → backend → Supabase, verified by
   Supabase against Cloudflare — never verified by this backend directly.**
   The frontend's "Play as guest" flow obtains a Turnstile token via
   Cloudflare's client-side widget/JS, sends it to `POST /auth/guest`,
   and the backend passes it through unmodified as
   `gotrue_meta_security.captcha_token` on the existing
   `SignInAnonymouslyAsync` call. This is the same "mediate, don't
   reimplement" boundary ADR-0013 already drew for signup/login password
   handling: Supabase owns the actual verification, this backend is a
   pass-through, never a second, independent Turnstile-verification client.
3. **Configuration split, following existing precedent:** the Turnstile
   **site key** is public (safe in frontend code, like Supabase's own anon
   key per ADR-0013) and belongs in the frontend as a new Vite environment
   variable, `VITE_TURNSTILE_SITE_KEY` — the same convention
   `frontend/src/lib/api.ts` and `frontend/src/App.tsx` already use for
   `VITE_API_BASE_URL`, rather than inventing a different configuration
   mechanism for one more value. The Turnstile **secret key** is a true
   secret and is configured directly in Supabase's own Auth dashboard
   settings (where Supabase's captcha verification itself reads it from),
   **not** as a value this application's backend holds or reads —
   unlike `Supabase:ServiceRoleKey`, this secret never enters this
   codebase's configuration surface at all, because this backend never
   calls Cloudflare directly.

**Widget UX:** Turnstile's invisible/managed mode is recommended over the
always-visible checkbox widget (REQ-717's own newly-added acceptance
criteria states this as a recommendation with reasoning — zero-friction
intent of "Play as guest," minimal visual footprint, an interactive
challenge shown only if Cloudflare's own risk scoring escalates to one).

**Failure mode:** a missing/expired/invalid token must map to a distinct,
specific rejection from `POST /auth/guest` — not the existing generic
`"Guest sign-in failed"` `Problem` response `AuthController.Guest`
currently returns for its other failure modes — so the frontend can reset
the widget and retry rather than treating a captcha failure like any other
opaque error. The exact response shape (e.g. a distinguishing error code
or title) is left to implementation, but it must be distinguishable by the
frontend; this is stated as a hard acceptance criterion in REQ-717's
2026-07-21 addition, not left vague.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| **Cloudflare Turnstile** (chosen) | Free with no meaningful volume cap at this project's scale; invisible/managed mode is minimally intrusive to real players; Supabase Auth has first-class native support (`gotrue_meta_security.captcha_token`), so no custom verification client is needed in this backend | Adds an external dependency (Cloudflare) and a manual one-time dashboard setup step (site creation) before guest play can function with captcha enabled | Best fit for the stated goal (harden guest creation specifically) at the lowest integration cost, per the product owner's direct comparison against hCaptcha |
| hCaptcha | Also free tier, also natively supported by Supabase's same `captcha_token` mechanism | More visible/interactive by reputation for the free tier; no material capability advantage over Turnstile for this use case | Turnstile does the same job with less friction for real players, at the same integration cost |
| A custom/self-built bot-check (e.g. a honeypot field, a timing heuristic) | No external dependency at all | Meaningfully weaker against a real scripted/distributed attacker — exactly the gap this ADR exists to close; would need ongoing tuning as attackers adapt, with no vendor doing that work | Reinventing a weaker version of a problem Cloudflare and Supabase already solve well together |
| Frontend verifies the Turnstile token directly against Cloudflare's siteverify API itself (no backend involvement) | Removes one hop | Directly contradicts ADR-0013's already-settled backend-mediation precedent — a client-side-only check is bypassable and untestable at the API level, the same objection ADR-0013 already raised against frontend-direct signup/login | Supabase's native support makes backend pass-through free to wire in anyway — no reason to reintroduce a client-side-only gate |
| Backend calls Cloudflare's `siteverify` API itself, independently of Supabase | Backend has direct visibility into verification results/error codes | Duplicate-mechanism risk: two independent verifiers (this backend's direct call, and Supabase's own, if ever also configured) could disagree; also reimplements verification Supabase already does natively for free | Supabase already verifies natively — a second, parallel verification path is exactly the kind of duplicated-mechanism risk ADR-0007 rejected once already (autocomplete vs. correctness-checking), for an analogous reason |

## Consequences

- Positive: no new outbound HTTP client or Cloudflare-verification logic
  is written in this codebase — `SignInAnonymouslyAsync` gains one
  additional pass-through parameter, and Supabase does the rest, matching
  the "mediate, don't reimplement" pattern already established for
  password credentials (ADR-0013) and JWKS validation (ADR-0017).
- Positive: the rate limit (REQ-717/ADR-0036) and this captcha layer are
  independent and additive — a distributed attacker who evades one doesn't
  automatically evade the other.
- Negative / trade-off accepted: a new external dependency (Cloudflare)
  and a new manual, one-time setup step (creating a Turnstile site to get
  a site key + secret key) are required before guest creation can enforce
  this — this needs its own line in `SETUP.md` alongside the existing
  Supabase Anonymous Sign-ins toggle documentation, the same way every
  other manual dashboard precondition in that file is recorded (see
  Follow-up below — that precondition line for Anonymous Sign-ins itself
  does not yet exist in `SETUP.md`/`MVP-SCOPE.md`, a pre-existing gap this
  ADR does not by itself close).
- Negative / trade-off accepted: `POST /auth/guest`'s existing generic
  `"Guest sign-in failed"` response must be split into at least two
  distinguishable outcomes (captcha rejection vs. every other failure) —
  a real, if small, code change to `AuthController.Guest`'s existing error
  handling, not purely additive.
- Follow-up: implementing this requires threading a `captchaToken`
  parameter through `ISupabaseAuthClient.SignInAnonymouslyAsync` and a
  request body on `POST /auth/guest` (currently parameterless per
  REQ-717's original "no request body" design) — a small, real contract
  change to an already-shipped endpoint, not a net-new one.
- Follow-up: `SETUP.md` needs a new step (alongside wherever the Supabase
  Anonymous Sign-ins toggle itself should already be documented, per
  ADR-0036) covering: create a Cloudflare Turnstile site (free), save the
  site key for `VITE_TURNSTILE_SITE_KEY`, and paste the secret key into
  Supabase's Auth settings (Authentication → Attack Protection / Bot and
  Abuse Protection, wherever Supabase's dashboard currently exposes
  captcha provider configuration) — not into this application's own
  configuration or secrets.
- Follow-up: `infra/README.md`'s frontend build-time configuration and
  `infra/bicep`'s Static Web App parameters need `VITE_TURNSTILE_SITE_KEY`
  added alongside however `VITE_API_BASE_URL` is currently wired through
  the build, since it's a build-time Vite value, not a runtime secret.
- Follow-up: `architecture-document.md`'s ADR summary table (§10) needs a
  new row for this ADR — flagged for `doc-sync`, not applied here per this
  agent's own scope boundary (never edits `architecture-document.md`
  directly).

## For AI agents

`POST /auth/guest` must keep passing its captcha token straight through to
Supabase's `gotrue_meta_security.captcha_token` field — never add a
second, independent call to Cloudflare's `siteverify` API in this backend.
If you find yourself writing an HTTP client for Cloudflare's API directly,
stop: that's a sign you're duplicating verification Supabase already does
natively, the same class of mistake ADR-0007 already rejected once for
autocomplete vs. correctness-checking.

The Turnstile secret key must never be added to this application's own
configuration (no `Turnstile:SecretKey` in `appsettings`/Container App
secrets/etc.) — it belongs solely in Supabase's own Auth dashboard
settings, which is the only place that ever calls Cloudflare to verify it.
If a task seems to require this backend holding that secret, stop and
flag it — that would mean this backend is calling Cloudflare directly,
which contradicts this ADR's whole point.

This captcha check is scoped to `POST /auth/guest` only. Do not add a
Turnstile check to `/auth/signup` or `/auth/login` as a side effect of
implementing this — that would be a scope change needing its own product
decision and its own REQ/ADR update, not a natural extension of this one.
