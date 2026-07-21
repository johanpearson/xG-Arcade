# ADR-0036: Guest play via backend-mediated Supabase Anonymous Sign-ins, not a client-local scheme

- **Status:** Accepted
- **Date:** 2026-07-21
- **Related requirements:** REQ-717 (guest play), REQ-303 (fetch round/grid),
  REQ-201-210 (guessing/scoring), REQ-204 (uniqueness), REQ-406/407/408
  (round-scoped/live leaderboards), REQ-409 (all-time median leaderboard —
  the one REQ this decision explicitly carves an exception into), REQ-606
  (rate limiting), REQ-714 (display name), REQ-710 (account deletion —
  closest existing precedent for an identity-transition preserving history)
- **Related components:** COMP-01 (Core.Users)

## Context

REQ-717 (drafted the same session as this ADR) specifies that a guest
player must be a real, guessable, scoreable participant — their guesses
count normally toward REQ-204's uniqueness calculation and appear on
REQ-406/407/408's round-scoped leaderboards via ordinary league
membership — while being explicitly excluded from REQ-409's all-time
median ranking. That requirement is agnostic to *how* a guest identity is
created; this ADR decides the actual mechanism, because it's a genuine
fork with real trade-offs, not an obvious implementation detail.

Two materially different approaches exist:

1. **A real `User` row, auto-provisioned server-side with no email/
   password**, using Supabase Auth's native Anonymous Sign-ins feature,
   mediated by the backend the same way ADR-0013 already mediates
   signup/login (never called directly from the frontend).
2. **No server-side identity at all** — grid progress and a locally-
   generated display name live only in browser storage (`localStorage`),
   with the frontend attaching some client-generated token to guess
   requests.

The two aren't a matter of taste: option 2 cannot satisfy REQ-717's own
acceptance criteria. `LeagueMembership.UserId` and `Guess.UserId` are
real, non-nullable (for `LeagueMembership`) or FK-backed columns
(`implementation-document.md` ~line 517-627); the `(RoundId, UserId,
CellId)` unique index is what enforces REQ-210's two-attempts-per-cell
limit and REQ-201's "one active guess per cell" rule. Without a real,
server-recognized identity, there is no way to enforce those limits
(a client could simply generate a new token to reset its own attempt
count) or to have a guest appear in REQ-406/407/408's `LeagueMembership`-
joined leaderboard queries without writing a second, parallel
leaderboard/membership code path solely for guests — which is exactly
the kind of duplicated-mechanism risk ADR-0007 already rejected once
(autocomplete vs. correctness-checking) for an analogous reason.

## Decision

A guest is a **real `User` row**, created via `POST /auth/guest`
(`XGArcade.Api`'s `AuthController`, alongside the existing `/auth/signup`/
`/auth/login`), which proxies to Supabase Auth's Anonymous Sign-ins
endpoint (`/auth/v1/signup` with no email/password, Supabase's supported
anonymous-user mode) — backend-mediated, following ADR-0013's exact
precedent, never called directly from the frontend. The resulting Supabase
identity is linked to a local `User` row exactly like a real signup, with
one addition: a new `User.IsGuest` boolean column (default `false`),
set `true` only for rows created through this endpoint.

`User.IsGuest` is the *only* schema addition this decision requires.
Every other REQ (201-210, 204, 406, 407, 408, 210's attempt limits, 714's
display-name mechanism) reads `User`/`Guess`/`LeagueMembership` exactly as
it already does today — a guest is indistinguishable from a real account
to all of that code, by design. `IsGuest` is consulted in exactly one
place: REQ-409's qualifying-rounds query, to exclude guest rows from the
all-time median ranking.

Guest creation gets its own rate limit, separate from and tighter than
REQ-606's existing signup/login limits (REQ-717 leaves the exact threshold
as an implementation detail, same as REQ-606 itself did) — because an
anonymous-sign-in endpoint has strictly less friction than email signup
(no address to type, no inbox to control), making it a cheaper target for
scripted identity creation aimed at probing a cell's hidden answer or
manipulating its uniqueness denominator.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Fully client-local guest (option 2 above): no server `User` row, a client-generated token/localStorage identity | No backend change at all; literally "no account" | Cannot enforce REQ-210's attempt limits or REQ-201's one-guess-per-cell rule (a client can trivially mint a new token); cannot appear in REQ-406/407/408's `LeagueMembership`-based leaderboard queries without a second, parallel query path built solely for guests; cannot durably support the claim/upgrade path REQ-717 also specifies | Fails REQ-717's own acceptance criteria outright — this isn't a stylistic trade-off, the mechanism can't do what the requirement asks |
| A bespoke `GuestSession` table/mechanism, parallel to `User`, joined into leaderboard/guess queries via a second code path | Keeps `User` "pure" (only ever real accounts); no dependency on Supabase's specific anonymous-auth feature | Every one of REQ-204/210/406/407/408's existing queries needs a second, guest-aware variant (or a `UNION`) to include guests — real, ongoing duplication-of-mechanism risk (the same class of problem ADR-0007 rejected for autocomplete/correctness); the claim/upgrade path becomes a genuine data migration (`GuestSession` row → new `User` row) instead of an in-place flag flip | Costs strictly more (a second mechanism to maintain) for no capability gain over option 1 |
| Frontend calls Supabase's Anonymous Sign-ins directly, backend only validates the resulting JWT | Less backend code; mirrors how some apps use Supabase's anonymous auth out of the box | Directly contradicts ADR-0013's already-settled precedent (backend-mediated signup/login) for the identical reason that ADR gave: a server-enforced check point is lost — here, that's the new guest-creation rate limit (point 5 in REQ-717), which needs to run before Supabase ever issues an identity, the same way REQ-701's checkbox check needs to run before Supabase's real signup | Would reintroduce the exact problem ADR-0013 already solved, for a second endpoint |

## Consequences

- Positive: REQ-406/407/408 (round-scoped/live leaderboards), REQ-201-210
  (guessing/scoring/attempt-limits), and REQ-204 (uniqueness) require
  **zero code changes** to support guests — they already operate on
  `User`/`Guess`/`LeagueMembership`, and a guest is one of those rows.
  Only REQ-409's qualifying-rounds query needs a one-line `WHERE
  NOT IsGuest` addition (or equivalent).
- Positive: the claim/upgrade path (a guest later adding email+password)
  is a Supabase Anonymous Sign-ins native operation (linking real
  credentials to an existing anonymous identity) plus flipping
  `User.IsGuest` to `false` — an in-place transition, not a data
  migration between two different tables.
- Negative / trade-off accepted: a new, tighter rate limit specifically
  for `POST /auth/guest` is required *before* this ships, not as a
  follow-up — an anonymous-sign-in endpoint is a materially easier abuse
  target than email signup (REQ-717's own acceptance criteria already
  make this a hard requirement, not optional polish).
- Negative / trade-off accepted: this is Tier 1/2 scope by
  `MVP-SCOPE.md`'s own classification (a new auth flow, touching the
  account boundary Tier 0 already locked in) — pulled forward by
  deliberate product decision, same precedent as REQ-108/214/402-403, not
  because a Tier 1 trigger fired organically.
- Follow-up: REQ-717 explicitly recommends that claiming a guest account
  does **not** retroactively make guest-era rounds eligible for REQ-409's
  ≥5-round qualification floor (only rounds played after claiming count) —
  this needs to be enforced in whatever query change implements the claim
  flow, not just in the guest-exclusion check itself; flagging here so the
  two don't get implemented as if only one flag mattered.
- Follow-up: `MVP-SCOPE.md`'s Tier 1 list has no existing "guest play"
  trigger bullet to strike through (verified — this is a genuinely new
  item, not a pre-existing trigger being pulled forward); it needs a new
  entry recording this pull-forward decision, the way REQ-108/214/402-403
  each got one.

## For AI agents

A guest's `User` row must go through the exact same code paths as a real
account for guessing, scoring, and round-scoped leaderboards — never add
an `IsGuest` branch to REQ-201-210/204/406/407/408's logic. The **only**
place `IsGuest` should ever be checked is REQ-409's all-time qualifying-
rounds query. If you find yourself adding a second guest-aware branch
anywhere else, stop — that's a sign the guest identity isn't being treated
as a real `User` row the way this ADR requires, and the fix is to remove
the branch, not to add a parallel one for symmetry.

Guest account creation (`POST /auth/guest`) must never be exempted from
its own rate limit "to keep guest play frictionless" — the whole point of
that limit is that guest creation is the easiest identity to script at
scale, precisely because it has no friction already.
