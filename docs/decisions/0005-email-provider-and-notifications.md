# ADR-0005: Custom SMTP via Resend for auth emails; a separate Notifications component for product emails

- **Status:** Accepted
- **Date:** 2026-07-04
- **Related requirements:** REQ-701 through REQ-706
- **Related components:** COMP-01 (Core.Users), COMP-08 (Core.Notifications, new)

## Context

Account creation requires a confirmation email (REQ-701–705). Later, the
product wants round-result notification emails (REQ-706, deferred). Auth is
already delegated to Supabase Auth (ADR-0004), which can send confirmation
emails itself, but Supabase's own default mail sender is explicitly
best-effort: capped at 2 emails/hour, and it will only deliver to addresses
already on the Supabase project's team — it cannot send to real signups at
all. Production use requires configuring a custom SMTP provider, which
raises the cap to 30/hour by default (further configurable).

Separately, round-result notifications are not an auth-lifecycle email —
they're a product feature triggered by the Round Scheduler Job, and
Supabase Auth has no mechanism for sending them. That requires the
Backend API to send email directly.

## Decision

- **Auth emails** (signup confirmation, password reset): sent by Supabase
  Auth, configured with **custom SMTP via Resend** (free tier: 3,000
  emails/month, 100/day) instead of the default sender. The confirmation
  email template includes both a one-tap confirmation link and a numeric
  code, satisfying REQ-703's "code or button" requirement — Supabase Auth
  supports both a confirmation URL and an OTP token in the same template.
- **Product notification emails** (round results, future): sent directly
  from the Backend API via Resend's HTTP API, through a new component,
  **Core.Notifications** (COMP-08), triggered by the Round Scheduler Job
  after scores lock (REQ-205). This is deliberately not routed through
  Supabase Auth, since Supabase Auth's mailer is scoped to auth-lifecycle
  events only.
- Both paths use the same Resend account but are configured and rate-limited
  independently (Supabase's SMTP settings vs. direct API calls), consistent
  with the "don't mix auth and product emails" guidance from Resend/Supabase
  best practice — if one sending reputation degrades, it doesn't take down
  the other.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Supabase's default mail sender for everything | Zero setup | 2 emails/hour cap and delivery restricted to project team members — cannot serve real signups at all | Not viable even for the confirmation flow, let alone notifications |
| Route notification emails through Supabase Auth too (e.g. custom auth hook) | One integration point | Auth hooks are designed for auth-lifecycle events; forcing product notifications through that path is a boundary violation and couples unrelated concerns | Adds complexity for no benefit; a direct API call from Core.Notifications is simpler and clearer |
| A different provider (SendGrid, Postmark, SES) | All viable | SendGrid's free tier has been discontinued; Postmark/SES have less generous free allowances or more setup overhead for this scale | Resend's free tier (3,000/month) comfortably covers both auth and notification volume at current scale |

## Consequences

- Positive: confirmation emails work for real users from day one (unlike
  Supabase's default sender); round-result notifications have a clear,
  separate sending path that doesn't risk auth email deliverability
- Negative / trade-offs accepted: one more external account to manage
  (Resend), and DNS records (SPF/DKIM) need to be set up for the sending
  domain for good deliverability — see `infra/README.md`
- Follow-up: if volume ever approaches the Resend free tier's 100/day or
  3,000/month limits, revisit — likely still cheap at the next tier, but
  worth tracking in the cost reality check in `infra/README.md`

## For AI agents

Do not send product notification emails (round results, etc.) through
Supabase Auth or any auth-hook mechanism — they go through
Core.Notifications calling Resend's API directly. Do not add a second email
provider without updating this ADR; both auth and notification emails use
the same Resend account by design, so the sending reputation and cost
tracking stay in one place.
