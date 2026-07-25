# ADR-0037: Cloudflare Turnstile as the captcha layer for guest creation, signup, login, and account-deletion password re-confirmation, wired through Supabase's native captcha-token verification

- **Status:** Accepted
- **Date:** 2026-07-21
- **Amended:** 2026-07-25 — scope widened from guest creation only to also
  cover signup and login; see Context and "For AI agents" below. Core
  wiring decisions (provider, mediation-through-Supabase, secret-key
  boundary) are unchanged by this amendment.
- **Amended:** 2026-07-25 (second amendment, same day) — scope widened
  again to also cover `DELETE /auth/account`'s password re-confirmation
  step (REQ-710), a fourth call site found by `backend-implementer` while
  implementing the amendment immediately above. See Context and "For AI
  agents" below. Core wiring decisions are unchanged by this amendment too
  — the only new wrinkle is UX shape (a re-confirmation inside an
  already-authenticated flow, not a fresh login/signup form), covered in
  Decision below.
- **Related requirements:** REQ-717 (guest play — 2026-07-21 "Bot-check
  (captcha) for guest creation" addition, 2026-07-25 scope-correction
  addition), REQ-701 (create account with email and password — 2026-07-25
  addition covering signup and login captcha), REQ-710 (account deletion —
  2026-07-25 addition covering the password re-confirmation step's captcha
  requirement), REQ-606 (security baseline / rate limiting, unaffected —
  see Context)
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

**2026-07-25 addendum — scope was wrong, now corrected:** this ADR
originally scoped Turnstile to `POST /auth/guest` only, on the assumption
that Supabase's "Enable Captcha Protection" setting could be applied to
one flow at a time. That assumption was never verified against a real
Supabase project (the original drafting sandbox had no network access,
and the "For AI agents" section below said so explicitly at the time).
It is now confirmed wrong: per `NOTES.md`'s 2026-07-25 entry, Supabase's
dashboard "Enable Captcha Protection" toggle is a single project-wide
setting covering every `gotrue` endpoint that can create or authenticate
an identity (`signup`, `token?grant_type=password`, `recover`, ...), not
one this project can enable for guest creation alone. The live symptom:
turning it on for guest-creation bot protection (per `SETUP.md` step 6)
broke real password-based login and signup outright, since
`SupabaseAuthClient.SignInWithPasswordAsync`/`SignUpAsync` had no
`captcha_token` to send — and `AuthController.Signup`'s REQ-701
account-enumeration-safe generic fallback message was masking the captcha
rejection as an ordinary signup failure. This ADR's Decision below is
amended accordingly: Turnstile now covers all three endpoints. The
underlying wiring decision (mediate through Supabase, never verify
independently, secret key never enters this backend) does not change —
only which endpoints send a token.

**2026-07-25 addendum #2 — a fourth call site found during
implementation:** while implementing the widened scope above,
`backend-implementer` found that `AuthController.DeleteAccount` (REQ-710)
also calls `ISupabaseAuthClient.SignInWithPasswordAsync` — the exact same
Supabase endpoint (`auth/v1/token?grant_type=password`) `Login` uses — to
re-verify the caller's password before permitting irreversible account
deletion. Now that `SignInWithPasswordAsync` requires a `captchaToken`
parameter (per the amendment above), this call site needs one too; a
placeholder empty string was left in its place, flagged with a comment,
specifically so this wouldn't ship silently broken. The product owner
decided (2026-07-25, same session) to close this in the same PR rather
than deferring it. This ADR's Decision below is amended accordingly:
Turnstile now covers four call sites, not three. Unlike the other three,
this one is a re-confirmation step inside an already-authenticated,
already-built `DeleteAccountScreen.tsx` flow (REQ-710/REQ-713), not a
fresh login/signup form — see Decision and "For AI agents" below for how
that changes the UX (not the wiring).

## Decision

**Cloudflare Turnstile**, wired through Supabase Auth's own native
captcha-token support rather than any custom verification code in this
backend, **covering all four identity-creating/authenticating/
re-confirming call sites this backend exposes: `POST /auth/guest`,
`POST /auth/signup`, `POST /auth/login`, and `DELETE /auth/account`'s
password re-confirmation step** (amended twice on 2026-07-25 — originally
scoped to `POST /auth/guest` only, then widened to also cover signup and
login, then widened again to also cover account-deletion's password
re-confirmation; see both Context addenda above for why each scope
correction was needed). Supabase's `/auth/v1/signup` and
`/auth/v1/token?grant_type=password` endpoints (which
`SignInAnonymouslyAsync`/`SignUpAsync`/`SignInWithPasswordAsync`
respectively call — `DELETE /auth/account` also calls
`SignInWithPasswordAsync`, the identical method `Login` uses, not a
separate one) each accept an optional `gotrue_meta_security.captcha_token`
field and verify it server-side against the configured captcha provider —
Supabase already speaks Turnstile natively for all of them, so no new
outbound HTTP call to Cloudflare is written in this codebase at all, for
any of the four call sites.

Concretely, this decision has three parts:

1. **Provider: Cloudflare Turnstile**, not hCaptcha and not a
   custom/self-hosted check. Chosen for the reasons in the comparison
   table below — free with no meaningful volume cap for this project's
   scale, less visible/annoying to real players, and a simpler two-key
   integration that Supabase already supports as a first-class option.
   This applies identically to all four call sites — there is no
   per-endpoint provider choice to make.
2. **Wiring: token flows frontend → backend → Supabase, verified by
   Supabase against Cloudflare — never verified by this backend directly.**
   Each of the four frontend flows ("Play as guest", account creation, log
   in, and the password re-confirmation step inside the already-built
   `DeleteAccountScreen.tsx`) obtains a Turnstile token via Cloudflare's
   client-side widget/JS, sends it to its respective endpoint
   (`POST /auth/guest`, `POST /auth/signup`, `POST /auth/login`,
   `DELETE /auth/account`), and the backend passes it through unmodified as
   `gotrue_meta_security.captcha_token` on the existing
   `SignInAnonymouslyAsync`/`SignUpAsync`/`SignInWithPasswordAsync` call —
   `DELETE /auth/account` reuses the identical `SignInWithPasswordAsync`
   call `Login` already makes, so no new backend method is introduced for
   it, only a caller now passing a real token instead of the placeholder
   empty string. This is the same "mediate, don't reimplement" boundary
   ADR-0013 already drew for signup/login password handling: Supabase owns
   the actual verification, this backend is a pass-through, never a
   second, independent Turnstile-verification client — for any of the
   four call sites.
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
The same recommendation now extends to the account-deletion password
re-confirmation step too (REQ-710's 2026-07-25 addition), for the same
minimal-friction reasoning, even though — like signup/login — that step
already involves more inherent friction than guest play (a password field
either way).

**Failure mode:** a missing/expired/invalid token must map to a distinct,
specific rejection on each of the four call sites — not the existing
generic `"Guest sign-in failed"` `Problem` response `AuthController.Guest`
returns for its other failure modes, and not `AuthController.Signup`'s
REQ-701 account-enumeration-safe generic fallback message (which the
2026-07-25 addendum above confirms was incorrectly swallowing a captcha
rejection as an ordinary signup failure), and not whatever generic
response `AuthController.Login` returns for other failures either, and not
the existing `"Incorrect password"` 401 response `AuthController
.DeleteAccount` already returns for a wrong password — so the frontend can
reset the widget and retry rather than treating a captcha failure like any
other opaque error, on any of the four call sites. On `DeleteAccountScreen
.tsx` specifically, this is a real, stated constraint, not left implicit:
the new captcha-rejection response's title (`"Captcha verification
failed"`, the same title the other three flows already use) must not
collide with the screen's existing string-match on the `"Incorrect
password"` title — that existing match is what tells a wrong-password
rejection (shown inline) apart from a 401 caused by an expired/invalid JWT
(which logs the user out instead), and it is the only one of the four call
sites with a pre-existing title-based branch a new title could collide
with. The exact response shape (e.g. a distinguishing error code or title)
is otherwise left to implementation, but it must be distinguishable by the
frontend on each endpoint; this is stated as a hard acceptance criterion in
REQ-717's 2026-07-21 addition (guest) and its 2026-07-25 scope-correction
addition (signup, login), and now also in REQ-710's 2026-07-25 addition
(account-deletion password re-confirmation) — not left vague.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| **Cloudflare Turnstile** (chosen) | Free with no meaningful volume cap at this project's scale; invisible/managed mode is minimally intrusive to real players; Supabase Auth has first-class native support (`gotrue_meta_security.captcha_token`), so no custom verification client is needed in this backend | Adds an external dependency (Cloudflare) and a manual one-time dashboard setup step (site creation) before guest play (originally) — and now signup/login (2026-07-25) and account-deletion password re-confirmation (2026-07-25, second amendment) too — can function with captcha enabled | Best fit for the stated goal (harden identity creation/authentication/re-confirmation against scripted abuse — originally scoped to guest creation, widened 2026-07-25 to signup/login once Supabase's project-wide toggle behavior was confirmed, then widened again the same day to account-deletion's password re-confirmation once the identical gap was found there) at the lowest integration cost, per the product owner's direct comparison against hCaptcha |
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
- Negative / trade-off accepted (added 2026-07-25): the same split now
  also applies to `AuthController.Signup` and `AuthController.Login` —
  `Signup`'s existing REQ-701 account-enumeration-safe generic fallback
  message must stop catching a captcha rejection indiscriminately (it
  needs a distinguishable captcha-rejection outcome carved out first,
  before the remaining generic fallback applies to every other rejection
  reason exactly as REQ-701 already specifies), and `Login` needs the
  same distinct captcha-rejection outcome added alongside whatever generic
  failure response it already returns. Real, if small, code changes to
  both, not purely additive — and not yet built as of this amendment.
- Negative / trade-off accepted (added 2026-07-25, second amendment): the
  same split now also applies to `AuthController.DeleteAccount`'s password
  re-confirmation call — it currently sends a placeholder empty-string
  `captchaToken` (flagged with a comment specifically so this wouldn't ship
  silently broken); it must now send a real Turnstile token obtained from
  `DeleteAccountScreen.tsx`'s own widget instance, and the screen's existing
  `"Incorrect password"`-title string-match must remain distinguishable
  from the new captcha-rejection title (see Decision's Failure mode section
  above for the exact collision risk). Real, if small, code changes to both
  frontend and backend, not purely additive — and not yet built as of this
  amendment.
- Follow-up: implementing this requires threading a `captchaToken`
  parameter through `ISupabaseAuthClient.SignInAnonymouslyAsync` and a
  request body on `POST /auth/guest` (currently parameterless per
  REQ-717's original "no request body" design) — a small, real contract
  change to an already-shipped endpoint, not a net-new one.
- Follow-up (added 2026-07-25): the same threading is needed for
  `ISupabaseAuthClient.SignUpAsync`/`SignInWithPasswordAsync` (a new
  `captchaToken` parameter each) and for `POST /auth/signup`/
  `POST /auth/login`'s request bodies (both already take a body, so this
  is an additive field on each, not a "parameterless to parameterized"
  change like the guest endpoint needed) — not yet built as of this
  amendment; scoped for `backend-implementer` following this ADR and
  REQ-717's/REQ-701's updated acceptance criteria.
- Follow-up (added 2026-07-25, second amendment): threading a
  `captchaToken` parameter through the account-deletion re-confirmation
  call needs no new `ISupabaseAuthClient` method — `DeleteAccount` already
  calls the identical `SignInWithPasswordAsync` `Login` uses — but
  `DeleteAccountScreen.tsx` needs its own Turnstile widget instance
  (obtaining and resetting a token independently of `AuthScreen.tsx`'s
  instance, since it's a different screen/component) and
  `AuthController.DeleteAccount`'s request body (currently just
  `{ Password }`) needs an additive `captchaToken` field, with the
  placeholder empty string replaced by the real value. Not yet built as of
  this amendment; scoped for `backend-implementer`/`ui-implementer`
  following this ADR and REQ-710's updated acceptance criteria.
- Follow-up (added 2026-07-25): `SETUP.md` step 6 (or wherever the
  Supabase "Enable Captcha Protection" toggle is documented) needs its
  own wording corrected — it currently reads as if enabling the toggle
  only affects guest creation, which is what led to this bug being
  discovered live rather than caught in setup. Flagged here for
  `doc-sync`/a follow-up session; not applied by this ADR amendment
  itself.
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

`POST /auth/guest`, `POST /auth/signup`, `POST /auth/login`, and
`DELETE /auth/account`'s password re-confirmation step must each keep
passing their captcha token straight through to Supabase's
`gotrue_meta_security.captcha_token` field on the corresponding
`SignInAnonymouslyAsync`/`SignUpAsync`/`SignInWithPasswordAsync` call —
note `DELETE /auth/account` calls the identical `SignInWithPasswordAsync`
`Login` uses, not a separate method, so there is nothing new to wire on
the Supabase-client side for it beyond passing a real token instead of the
placeholder empty string. Never add a second, independent call to
Cloudflare's `siteverify` API in this backend, for any of the four. If you
find yourself writing an HTTP client for Cloudflare's API directly, stop:
that's a sign you're duplicating verification Supabase already does
natively, the same class of mistake ADR-0007 already rejected once for
autocomplete vs. correctness-checking.

The Turnstile secret key must never be added to this application's own
configuration (no `Turnstile:SecretKey` in `appsettings`/Container App
secrets/etc.) — it belongs solely in Supabase's own Auth dashboard
settings, which is the only place that ever calls Cloudflare to verify it.
If a task seems to require this backend holding that secret, stop and
flag it — that would mean this backend is calling Cloudflare directly,
which contradicts this ADR's whole point. This applies identically to all
four call sites — there is no per-endpoint secret-key exception.

`DELETE /auth/account`'s captcha check is a re-confirmation inside an
already-authenticated, already-built `DeleteAccountScreen.tsx` flow, not a
fresh login/signup form — do not build a second Turnstile widget-loading
mechanism for it; reuse `frontend/src/lib/turnstile.ts`'s existing
`getTurnstileToken()`/`resetTurnstileWidget()` helpers (per REQ-717's
guest-flow implementation) rather than writing a parallel one, and take
care that the new captcha-rejection title does not collide with
`DeleteAccountScreen.tsx`'s existing `"Incorrect password"`-title check
(see Decision's Failure mode section above).

**Amended 2026-07-25 — the previous scope limit here was wrong and has
been reversed.** This captcha check now covers all three of
`POST /auth/guest`, `POST /auth/signup`, and `POST /auth/login` — see the
Context addendum and amended Decision section above for why (Supabase's
"Enable Captcha Protection" toggle is project-wide, not per-endpoint;
enabling it for guest creation alone silently broke real login/signup).
Do not re-narrow this back to guest-only "to match the original design" —
that original design is the thing this amendment corrects. If a future
task seems to call for scoping captcha to a subset of these three
endpoints again, that is itself a new product decision needing its own
REQ/ADR update, not something to infer from this file's edit history.

**Amended again, same day (2026-07-25, second amendment) — a fourth call
site was found and added.** This captcha check now covers `POST
/auth/guest`, `POST /auth/signup`, `POST /auth/login`, and `DELETE
/auth/account`'s password re-confirmation step (REQ-710) — see the second
Context addendum and amended Decision section above. Unlike the first
three, this fourth call site is a re-confirmation inside an
already-authenticated flow, not a fresh login/signup form — the
wiring/mediation/secret-key rules apply identically regardless; only the
UX shape and the title-collision constraint noted above differ. Do not
re-narrow this back to three call sites "to match the original ADR" —
this amendment corrects that too. If a future task seems to call for
scoping captcha to a subset of these four call sites, that is itself a new
product decision needing its own REQ/ADR update, not something to infer
from this file's edit history.
