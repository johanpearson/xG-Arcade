# ADR-0023: Grid generation gets its own wall-clock deadline, separate from MaxAttempts

- **Status:** Accepted
- **Date:** 2026-07-13
- **Related requirements:** REQ-101, REQ-301
- **Related components:** COMP-05 (Games.XGGrid), COMP-03 (Core.Rounds)

## Context

A manual `generate-round.yml` dispatch against the deployed dev environment
failed three times in a row on 2026-07-12/13, each differently:

1. Two bare HTTP 500s (11s, 30s) — root cause invisible at the time, since
   `InternalRoundEndpoints.cs`'s `/internal/generate-round` handler only
   caught `GridGenerationException`; anything else fell through to ASP.NET's
   default empty 500. Fixed separately (REQ-301, see `CHANGELOG.md`'s
   2026-07-12 entry): a catch-all now logs and returns a real
   problem-details response for any exception.
2. An HTTP 503 "no healthy upstream" — an unrelated deploy race (the manual
   dispatch landed mid-rollout of a `deploy.yml` run from the same PR
   merge). Not a code issue.
3. An HTTP 504 "stream timeout" after exactly 240 seconds — Azure Container
   Apps' own ingress gave up waiting and cut the connection before the app
   could respond. The Container App's own log (fetched separately, not
   visible in the GitHub Actions log) showed why: an unhandled
   `OperationCanceledException` inside
   `WikidataClient.QueryCountryClubIntersectionAsync`, propagated up through
   `WikidataLookupService.LookupAndPersistAsync` →
   `GridGameModule.GetMatchCountAsync` → `PickHeadersAsync` →
   `GenerateInstanceAsync` → `RoundGenerationService` →
   `InternalRoundEndpoints`. The cancellation itself was correct behavior
   (the caller's own token, ultimately `HttpContext.RequestAborted`, firing
   once the ingress gave up) — the real problem is that nothing upstream of
   it ever proactively decided "this has taken too long" on its own terms.

`PickHeadersAsync` tries column candidates one at a time, and any candidate
not already cached costs a real, synchronous Wikidata SPARQL call — up to
15s (`WikidataClient`'s own per-query timeout), and ADR-0011's own evidence
says a genuinely answering query can take 9-27s under load. The loop's only
stated bounds are the candidate pool size and `GridGenerationOptions.MaxAttempts`
(500 by default) — effectively no ceiling in practice; 500 sequential
worst-case live calls would take over two hours. A run of bad luck on which
row headers get picked (some countries/club pairs genuinely have few or no
shared players in the Tier 0 reference data) can chain together enough
live-lookup misses to blow past any HTTP request timeout — Azure Container
Apps' dev ingress here, empirically, at 240s — long before either existing
bound would ever kick in.

## Decision

`GridGenerationOptions` gets a new `MaxDuration` (`TimeSpan`, default 90s).
`PickHeadersAsync` records a deadline (`_timeProvider.GetUtcNow() + options.MaxDuration`,
via the same injectable `TimeProvider` pattern `RoundGenerationService`
already uses) when it starts, and checks it alongside the existing
"candidate pool exhausted" / "MaxAttempts reached" checks on every loop
iteration. Exceeding it aborts with the same `GridGenerationException` path
REQ-101 already defines, with a log line naming how many candidates were
tried and accepted before the deadline hit.

90s leaves substantial margin under the observed 240s ingress ceiling for
the rest of the request's own overhead (auth, template resolution, writing
the round, JSON serialization) and for any variance in what that ceiling
actually is in a different environment. The result: `/internal/generate-round`
now always returns a definitive, fast answer — success or a clean, logged,
caller-visible failure — instead of occasionally being killed out from
under the app by infrastructure with nothing to show for it.

**This does not raise the odds of a round successfully generating** — it
only bounds how long a failed attempt is allowed to take. Raising the
success rate needs either better reference data (more/better-connected
countries and clubs) or genuine concurrency in the candidate search, and
concurrency specifically turned out not to be safe to add in this same
change — see Alternatives.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Bounded-concurrency candidate search (`Task.WhenAll` over a small batch of candidates instead of one at a time) | Directly raises success odds — more candidates tried in the same wall-clock budget, not just a faster failure | `PlayerStoreRepository`/`CategoryValueRepository`/`WikidataLookupService` all share one request-scoped `XGArcadeDbContext` (`Program.cs`'s `AddDbContext`/`AddScoped`); EF Core's `DbContext` is not safe for concurrent use by a single instance. This throws (`"A second operation was started on this context before a previous operation completed"`) against real Npgsql while silently working against the lenient InMemory provider tests use — exactly the class of bug that passes CI and breaks production. Real concurrency needs `IDbContextFactory`-based per-call contexts threaded through all three components — a larger, separately-scoped change. Additionally, ADR-0011 documents Wikidata's own throttling as *cumulative query time per minute per IP/user-agent* (60s/min steady, 120s/min burst), not a request count — running several ~15-27s queries concurrently increases the *rate* of query-time consumed per wall-clock minute compared to sequential, and could trip WDQS's own rate limiting for this system's user-agent, degrading reliability further rather than improving it, unless the concurrency limit is chosen with that budget explicitly in mind | Not chosen for this pass — real correctness risk (concurrent `DbContext` use) plus a real risk of worsening the exact problem being fixed (tripping Wikidata's throttle harder). Flagged as follow-up below, to be designed properly with `IDbContextFactory` and the throttle math, not retrofitted under incident pressure |
| Raise `MaxAttempts` instead of adding a new bound | No new config surface | Attempt *count* was never the actual constraint — the incident ran out of *time*, not attempts (500 was already far more attempts than the 15-club pool could ever supply). Doesn't address the actual failure mode at all | Not chosen: treats the wrong variable |
| Lower `WikidataClient`'s per-query timeout (e.g. 15s → 5s) instead of adding a wall-clock deadline on the whole search | Smaller change, one existing knob | ADR-0011's addendum already raised this from an earlier, shorter default specifically because a shorter timeout discards a large share of genuinely-successful-but-slow queries (9-27s observed range), pushing otherwise-answerable lookups to failure — this would reopen a settled, evidenced decision on a different, unrelated basis | Not chosen: revisits ADR-0011's addendum without new evidence about per-query latency; the actual problem is unbounded *total* search time, not any single query's timeout |
| Move grid generation off the synchronous request path (fire-and-check-later / background job) | Removes the HTTP-request-timeout constraint entirely | Materially larger architectural change — a new async job/polling mechanism, a new "generation in progress" state for `Round`, and `generate-round.yml`'s cron would need to poll for completion instead of getting a synchronous answer. Tier 0 scale doesn't need this yet | Not chosen: disproportionate to the problem at Tier 0's current scale; worth reconsidering only if `MaxDuration` itself starts being hit routinely rather than rarely |

## Consequences

- Positive: `/internal/generate-round` can no longer hang until an external
  ingress kills it — every invocation now resolves within `MaxDuration`
  plus a small fixed overhead, with a clean, diagnosable
  `GridGenerationException` on failure (title, detail, and a log line
  naming attempts/candidates tried) instead of a silent infrastructure-level
  cutoff.
- Negative / trade-offs accepted: does not improve the actual success rate
  of a cold-cache generation — a run that would eventually have succeeded
  given enough time can now be cut off at 90s instead. Judged acceptable
  because `generate-round.yml`'s "one round ahead" design (REQ-301) already
  gives a full round-length window to notice and retry a failed generation
  before players see a gap, and a fast, clean failure is strictly more
  actionable than a slow, silent one.
- Follow-up: genuine concurrent candidate search, via `IDbContextFactory`-based
  per-call `DbContext` instances threaded through `PlayerStoreRepository`,
  `CategoryValueRepository`, and `WikidataLookupService`, with a
  concurrency limit chosen against ADR-0011's documented query-time budget
  (not picked arbitrarily) — this is the change that would actually raise
  the odds of a cold-cache generation succeeding within `MaxDuration`,
  rather than just failing it faster. Also worth revisiting: whether the
  Tier 0 reference data (15 clubs, 20 countries) has any country/club (or,
  post-S-030, club/club) pairs sparse enough in practice to make a cold
  generation attempt routinely hit this deadline — if so, curating a better-
  connected reference set may be a cheaper fix than any code change.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change. In
particular: do not add `Task.WhenAll`/`Parallel.ForEach`-style concurrency
over anything that touches `XGArcadeDbContext` (directly or via a
repository) without first switching that code path to
`IDbContextFactory`-based per-call contexts — the shared, request-scoped
`DbContext` this codebase uses everywhere else is not safe for concurrent
access, and a test passing against the InMemory provider does not prove
otherwise.
