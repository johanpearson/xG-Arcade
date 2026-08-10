# ADR-0066: Server-side cached polling of GitHub Issues for admin incident notification

- **Status:** Accepted
- **Date:** 2026-08-10
- **Related requirements:** REQ-904 (and REQ-903, whose boundary this decision operates against)
- **Related components:** COMP-12 (extended, not new)

## Context

REQ-904 wants admins to see, from inside the admin UI, that a new in-app
incident report (REQ-903) exists — without opening GitHub Issues by hand.
REQ-903/ADR-0064 deliberately keeps **no in-app record** of a created
incident ("no in-app moderation/review queue") — every valid submission
becomes a real GitHub issue immediately, and nothing about that submission
is persisted anywhere in `XGArcade.Data`. That's a settled boundary
(confirmed with the product owner, `docs/backlog.md`'s S-098 entry), not
something this ADR revisits.

With no local record to badge against (unlike REQ-512's pending-suggestion
badge, which just re-reads an existing table), the only source of truth
for "how many open incident reports exist" is GitHub itself. That means
this feature requires a genuinely new kind of access this codebase hasn't
had before: a **live outbound read from GitHub's REST API, triggered by an
admin page load** — not a webhook, not a scheduled job, not a read of our
own database. That's a structural choice worth recording for two reasons:
it introduces a new external-call-on-page-load shape, and it raises a real
question ADR-0064 didn't have to answer (that ADR only ever *writes* one
issue per player submission, which is naturally self-rate-limiting by
player behavior) — an admin page load is comparatively cheap to trigger,
repeatedly, by multiple admins, and GitHub's REST API enforces its own
rate limits (5,000 requests/hour for an authenticated PAT) that a careless
"call GitHub on every page load" implementation could approach under
nothing more than normal admin usage (a handful of admins refreshing the
page repeatedly while triaging).

## Decision

Add a `ListOpenIssuesByLabelAsync` method to the existing
`IGitHubIssueClient`/`GitHubIssueClient` (COMP-12, `XGArcade.Core.IncidentReporting`)
— the same fine-grained PAT (`GITHUB_INCIDENT_REPORT_PAT`,
`Issues: write` scope on this one repo) and the same fixed
`GitHubIncidentReportOptions` (owner/repo/label) ADR-0064 already
established. **No PAT scope change is needed**: GitHub's fine-grained PAT
model treats `Issues: write` as inclusive of read access to issues on that
repository — this call lists, it never writes, and needs no broader grant.
This keeps "the one and only class that calls GitHub's REST API for this
feature" true, rather than introducing a second GitHub-calling code path.

Wrap that read behind a new, small, in-memory caching layer
(`ICachedIncidentIssueSummaryProvider`/`CachedIncidentIssueSummaryProvider`,
also in `Core.IncidentReporting`) using `IMemoryCache` with a single
fixed cache key and a short absolute-expiration TTL (default 60 seconds,
overridable via `GitHub:IncidentReportCacheTtlSeconds`, following the same
"sane default, overridable via configuration" convention COMP-12's own
rate-limit numbers already use). All admin requests within the TTL window
are served from that single shared cache entry — the cache is not
per-admin/per-request. A new `GET /admin/incident-reports` endpoint
(`XGArcade.Api.Admin`, same `"Admin"` policy every other admin endpoint
uses) calls the cached provider, never `GitHubIssueClient` directly.

On a GitHub API failure (network error, non-success status, rate limit,
or an unconfigured/blank token — the same conditions
`GitHubIssueClient.CreateIssueAsync` already treats as failure), the
provider:
- serves the last successfully-cached result if one still exists, even if
  its TTL has technically expired (a short-lived transient GitHub outage
  should not immediately flip a working admin UI to an error state), and
- only if there has never been a successful poll yet (cold start during
  an outage, or the token has never been configured) returns an explicit
  failure result — never a fabricated zero count. `GET /admin/incident-reports`
  turns that into a distinct client-visible failure shape (never a `200`
  with `count: 0`), so the frontend can render REQ-904's "never a false
  zero-count" requirement correctly.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Live GitHub call on every `GET /admin/incident-reports` request, no cache | Simplest to implement; always maximally fresh | Admin page loads are cheap to trigger repeatedly; a handful of admins refreshing during triage could approach GitHub's rate limit for no real benefit, since REQ-904 only needs a count, not sub-minute freshness | Rejected — freshness requirement (REQ-904's own "fetch on load, no polling" language) doesn't demand per-request live truth, so paying the rate-limit risk for it is pure downside |
| Scheduled background job (e.g. a recurring GitHub Actions workflow or hosted timer) polls GitHub and writes the result to `XGArcade.Data` | Removes GitHub calls from the request path entirely; trivial to badge against, same shape as REQ-512 | Requires a new table to hold the polled result — directly encroaches on ADR-0064's "no in-app record of a created incident" boundary, even though the record would be a derived count rather than the incident itself; also adds a new scheduled-job dependency (REQ-902's failure-alerting surface) for a feature this small | Rejected — trades a small in-memory cache for a new persistence surface and a new job to monitor, disproportionate to what REQ-904 needs |
| Frontend calls GitHub's API directly from the browser | No backend code needed | Same rejected-outright reasoning ADR-0064 already gives for issue creation: reading this repo's issues doesn't strictly require a secret token (GitHub's public issue list is world-readable), but routing it through the backend keeps one consistent "this app never calls GitHub directly from the browser" rule and lets the response be cached server-side once for every admin, not fetched independently per browser | Rejected — inconsistent with ADR-0064's existing pattern for no real benefit, and loses the shared-cache economy of a server-side read |
| Distributed/shared cache (e.g. Redis) instead of in-process `IMemoryCache` | Cache shared correctly across multiple backend instances, if the API ever runs more than one | No existing distributed cache infrastructure anywhere in this codebase; `XGArcade.Api` runs as a single Azure Container App instance today (per `infra/`) | Rejected as premature — revisit if/when the backend runs multiple instances, per this ADR's own follow-up note below |

## Consequences

- **Positive:** admins get a real, GitHub-sourced notification with no new
  persistence table and no violation of ADR-0064's review-queue boundary;
  outbound GitHub API usage stays bounded and roughly constant regardless
  of how many admins load the page or how often; the existing
  `IGitHubIssueClient` remains the single call site for all GitHub REST
  API access this backend makes.
- **Negative / trade-offs accepted:** the admin-visible count can lag
  reality by up to the cache TTL (60s default) — acceptable per REQ-904's
  own "fetch on load, no polling" freshness model, which never promised
  sub-minute accuracy. A single in-process `IMemoryCache` entry means the
  cache is not shared if the API ever scales to multiple instances (each
  instance would poll GitHub independently, multiplying calls by instance
  count) — acceptable today since the backend runs as one instance; if
  that stops being true, this decision needs revisiting rather than
  silently under-protecting GitHub's rate limit. Serving a stale cached
  result during a GitHub outage means the badge can display an
  out-of-date count during an incident, rather than immediately flipping
  to a failure state — accepted as the better trade-off for transient
  blips, at the cost of not being maximally "honest" about staleness
  during a real outage.
- **Follow-up:** revisit the TTL value once real admin usage patterns
  exist (60 seconds is a starting guess, not a measured number, same
  spirit as REQ-903's rate-limit numbers being "left to implementation");
  move to a distributed cache if the backend ever runs more than one
  instance; revisit whether per-issue detail (title/URL list, not just a
  count) becomes worth surfacing in-app if admins report the "click
  through to GitHub" step is friction — that would still not require a
  local persistence table, since the same cached provider could just
  return more fields.

## For AI agents

Do not add a database table for incident-report state — the count comes
from GitHub via `ListOpenIssuesByLabelAsync`, cached in memory, full stop.
Do not widen `GITHUB_INCIDENT_REPORT_PAT`'s scope for this feature — listing
issues is already covered by `Issues: write`. Do not call GitHub's API
directly from `GET /admin/incident-reports` or from any frontend code —
always go through `IGitHubIssueClient` and the cached provider. If a
future change needs this to update faster than the cache TTL allows, that
is a new decision (e.g. a webhook-driven push), not a quiet removal of the
cache.
