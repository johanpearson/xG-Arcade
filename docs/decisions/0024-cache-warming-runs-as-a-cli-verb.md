# ADR-0024: Player cache warming runs as a CLI verb, never an HTTP endpoint or background task

- **Status:** Accepted
- **Date:** 2026-07-13
- **Related requirements:** REQ-110
- **Related components:** COMP-05 (Games.XGGrid)

## Context

REQ-110 (S-036) needed a way to proactively warm the `PlayerAttribute`
cache for every reference Country×Club and Club×Club pair, following
directly from ADR-0023: a real generation attempt failing fast with
`"Ran out of candidates before completing the grid"` showed that
`MaxDuration` alone (ADR-0023) only bounds how long a *failed* attempt
takes — it does nothing to raise the odds of success when the underlying
reference data genuinely lacks enough shared players for a given pairing.

Warming every reference pair means, in the worst case, a few hundred live
Wikidata SPARQL calls, each up to a real ~15-27s under load (ADR-0011's own
evidence). That is explicitly allowed to take a long time — unlike
round generation, nothing is waiting synchronously on the result. The
question this ADR actually answers is *where does that long-running work
run*, given three options were on the table and two of them fail for
reasons specific to this deployment, not in general.

## Decision

`PlayerCacheWarmingService.WarmAsync` runs as a second `dotnet run --`
CLI verb (`warm-player-cache`) in `Program.cs`, alongside the pre-existing
`migrate-and-seed` verb — same shape: builds its own `XGArcadeDbContext`,
repositories, and `WikidataClient` directly rather than spinning up the
full `WebApplication` DI container, and returns before any of that
container's own setup runs. Triggered manually via
`warm-player-cache.yml` (`workflow_dispatch`, no recurring schedule — run
this after a reference-data change, not on a fixed cadence), executing as
a plain foreground GitHub Actions job step, not a request against the
deployed backend.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| An HTTP endpoint (`POST /internal/warm-player-cache`), same bearer-token pattern as `/internal/generate-round` | Consistent with every other `/internal/*` job trigger in this codebase; no second execution model to reason about | Hits the exact same ~240s Azure Container Apps ingress timeout ADR-0023 had to fix round generation against — a few hundred live Wikidata calls can genuinely take longer than that in the worst case, and unlike round generation this job has no small, fast-failing "give up cleanly" behavior to fall back on; it would need the same deadline/resume-and-call-again complexity ADR-0023 built for a much smaller (per-instance) search, multiplied across the whole reference pool | Not chosen: reintroduces the problem ADR-0023 exists to solve, for a job with no reason to accept that constraint in the first place |
| A fire-and-forget background task inside the deployed app (`IServiceScopeFactory.CreateScope()` + `Task.Run`, triggered by a fast-returning endpoint) | Endpoint returns immediately (202 Accepted); no GitHub Actions job needs to stay open for the duration | This Container App has `minReplicas: 0` (scale-to-zero, NOTES.md 2026-07-09) — a scale-down event mid-run would silently kill the background task with no persisted state to resume from and no error surfaced anywhere. Also the first use of this execution pattern anywhere in this codebase, adding real complexity (explicit DI scope management, since a fire-and-forget task outliving its request must not reuse request-scoped services) for a job that doesn't need the "return fast" property this pattern exists to provide | Not chosen: trades a real, silent-data-loss risk for a UX property (fast HTTP response) nothing actually needs here |
| CLI verb (chosen) | No ingress timeout, no scale-to-zero risk, no new execution pattern — direct reuse of `migrate-and-seed`'s already-proven shape; progress streams live to the Actions log, which is arguably *better* observability than either alternative | The workflow can only be triggered manually (`workflow_dispatch`) — no way for the deployed app itself to kick this off | Accepted: this job's own operational profile (run rarely, after a deliberate reference-data change, by the person making that change) never needed an automatic/self-service trigger in the first place |

## Consequences

- Positive: warming a few hundred reference pairs can safely take the
  amount of time it actually needs (up to the workflow's 90-minute job
  timeout) without touching any HTTP-request-timeout constraint, and
  without any risk from this Container App's scale-to-zero configuration.
- Positive: reuses an already-reviewed pattern (`migrate-and-seed`)
  instead of introducing a new one — smaller diff, smaller review surface,
  no new failure mode class to reason about.
- Negative / trade-offs accepted: no way to trigger this from inside the
  deployed application itself (e.g. an admin UI button) — only from a
  CI runner with the database connection string available as a secret.
  Acceptable at Tier 0 scale; revisit if an admin-facing trigger is ever
  genuinely needed (COMP-09's future admin surface would be the natural
  place, not this ADR's concern to solve pre-emptively).
- Negative / trade-offs accepted: `Program.cs` now has two CLI verbs
  sharing the "build dependencies by hand instead of via DI" pattern —
  each new verb duplicates a little construction logic (mitigated for the
  `WikidataClient` piece specifically by extracting a shared
  `ConfigureWikidataHttpClient` local function both the CLI verb and the
  real `AddHttpClient` registration call, so `BaseAddress`/`User-Agent`
  can't drift between them). If a third verb needing similar setup shows
  up, extracting a small shared bootstrap helper is worth doing then,
  not speculatively now.
- Follow-up: if `PlayerCacheWarmingService` is ever moved into an
  `XGArcade.DataSync`-owned worker process (CONT-04, "Sync Worker" in
  `architecture-document.md`'s design placeholder, not yet built), this
  ADR's reasoning should be revisited — a real long-lived worker process
  would remove the scale-to-zero risk this ADR is specifically avoiding,
  and might make an HTTP-triggered or scheduled model viable again.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change. In
particular: do not add a new `/internal/*` HTTP endpoint that runs a
long, unbounded-duration operation (multiple live external API calls,
no per-call deadline) against this deployed backend — check whether the
work can accept CLI-verb execution the way this one does before assuming
an endpoint is the default shape.
