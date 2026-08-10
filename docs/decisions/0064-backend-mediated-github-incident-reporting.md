# ADR-0064: Backend-mediated GitHub issue creation for in-app incident reports

- **Status:** Proposed
- **Date:** 2026-08-10
- **Related requirements:** REQ-903
- **Related components:** COMP-12 (new), COMP-01

## Context

Players hit bugs and rough edges while playing, and today the only way to
report one is out-of-band (a message to the team directly). That's lossy —
reports don't land where the work actually happens (GitHub Issues in this
repo), and doing it well by hand doesn't scale.

We want a logged-in player to file an incident from inside the app, with
that report landing as a real GitHub issue, **without the player needing a
GitHub account of their own.** That constraint rules out the obvious
"just call the GitHub API from the browser" approach: any credential
capable of creating issues would have to ship in client-side JS, where
anyone can read it out of the bundle and use it for anything the token can
do. It also rules out requiring GitHub sign-in to file a report — that
directly contradicts the goal.

Separately, this repo's issue tracker is a shared, visible surface — an
endpoint that write to it needs real abuse resistance, since not every
authenticated user should be treated as fully trusted (see REQ-717's guest
accounts, which exist specifically to be created disposably).

## Decision

Add a new backend-only component, **Core.IncidentReporting** (COMP-12), in
`XGArcade.Core`. It exposes one authenticated endpoint
(`POST /incidents`, REQ-903) that:

- Requires a valid session (`[RequireAuthorization]`, same JWT validation
  every other Core endpoint uses, ADR-0017), and resolves the caller via
  `IUserRepository.GetByAuthProviderUserIdAsync`, same as REQ-215's
  suggestion endpoint.
- Rejects guest accounts (`IsGuest == true`) with `403`, server-side,
  regardless of what the client sends — the same boundary REQ-215 already
  established for a different write path, chosen here for the same
  reason: guest accounts are disposable and rate-limited for gameplay, not
  vetted enough to write into a shared GitHub-facing tracker.
- Is rate-limited per user (mirroring the existing `auth-guest`
  rate-limiter pattern, exact numbers left to implementation).
- On a valid request, calls GitHub's REST API server-side to create an
  issue in this repo, using a **fine-grained personal access token scoped
  to `Issues: write` on this repository only** — no other scope, no
  org-wide access. The token is held as a backend secret/environment
  variable (same "no secrets in source control, config via environment
  variables" convention every other secret in this project already
  follows) and is never sent to, or accepted from, the client.
- Tags every created issue with a fixed, server-chosen label (e.g.
  `user-reported`) for triage. The target repo and label are hard-coded
  server-side — never accepted as client input.
- Includes non-PII triage context in the issue body (internal
  `UserId`, current route/screen if supplied, timestamp) — never an email
  address, and never the GitHub token itself.
- Returns success (optionally the created issue's URL, which is not
  secret) or a generic failure to the client without leaking GitHub-side
  error detail.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| GitHub App installation token | Finer-grained; independently rotatable/revocable; not tied to a personal account | More setup: register an app, install it on the repo, exchange installation tokens | Heavier than this needs right now; a PAT scoped to one repo's `Issues: write` is equally revocable and much simpler to stand up. Can migrate later without changing REQ-903 itself. |
| Frontend calls GitHub's API directly with an embedded token | No backend code needed | Any credential shipped in client JS is readable by anyone who opens dev tools; defeats "no GitHub account needed" if scoped to a real account | Direct credential-exfiltration risk; rejected outright |
| Require GitHub OAuth sign-in to file a report | Issue is correctly attributed to a real GitHub identity | Contradicts the explicit goal of not requiring a GitHub account | Rejected by definition |
| Route through email (Resend) to a shared inbox, triaged into GitHub by hand | No GitHub credential in the app at all | Reintroduces the manual step this feature exists to remove; Resend/email integration is itself deferred (ADR-0005, Tier 1 email confirmation) | Doesn't solve the stated problem |

## Consequences

- **Positive:** players can report a problem with zero setup on their
  side; reports land directly where development work happens, closing the
  loop faster. Follows the same backend-mediated-credential pattern
  already established for signup (ADR-0013) and player suggestions
  (ADR-0053) — no new category of trust boundary introduced.
- **Negative / trade-offs accepted:** a bug in this endpoint (or a
  compromised backend) could create spam/junk issues — mitigated, not
  eliminated, by scoping the PAT to `Issues: write` only (it cannot touch
  code, branches, releases, or settings), per-user rate limiting, and
  guest exclusion. There is no in-app moderation/review queue before an
  issue is created (unlike REQ-215/REQ-509-510's suggestion
  review-then-commit pipeline) — every valid submission becomes a real
  issue immediately. GitHub has no way to independently verify who
  actually filed a given issue beyond the internal `UserId` recorded in
  its body.
- **Follow-up:** revisit PAT vs. GitHub App if the token's scope ever
  needs to grow, or if per-installation audit trails become valuable;
  revisit the rate-limit numbers once real usage exists; revisit whether
  a review queue is needed if spam/noise turns out to be a real problem.

## For AI agents

Never widen the PAT's scope beyond `Issues: write` on this one repo.
Never accept a client-supplied target repo, label, or GitHub credential in
the request body — both are fixed server-side. Never call the GitHub API
for this feature from frontend code. If a future change needs the token to
do more than create issues (e.g. comment, close, or read other repos'
data), that's a new decision, not a quiet scope creep of this one.
