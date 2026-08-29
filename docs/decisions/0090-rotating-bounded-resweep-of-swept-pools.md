# ADR-0090: Rotating, bounded re-sweep of already-swept country/club pools

- **Status:** Accepted
- **Date:** 2026-08-29
- **Related requirements:** REQ-110
- **Related components:** COMP-07 (DataSync.Clients)

## Context

ADR-0088 (2026-08-25, same incident window as this ADR) fixed the confirmed
root cause of a Supabase free-tier egress overage: `PlayerCareerPrefetchService.SweepAsync`
unconditionally re-swept every seeded `CountryDefinition`/`ClubDefinition`
row's full player pool on every `prefetch-player-careers` dispatch,
regardless of whether that row had ever been swept before. ADR-0088's fix —
skip a row entirely once its `PlayerPoolSweptAt` is non-null, "ever swept"
being sufficient, no staleness window — closed that incident, but its own
"Alternatives considered" table explicitly named and rejected a time-boxed
staleness window as unnecessary complexity for the data's own volatility.

That fix introduced a new, previously-nonexistent gap: a player transferring
into an already-swept country's or club's pool now has **no path back to
being noticed**, ever, since a fresh Wikidata query for that pool would
return a different (larger) player set but the row is skipped before any
such query ever runs. `prefetch-player-careers.yml`'s own `workflow_dispatch`
trigger is the only way to force a real re-sweep, and it re-sweeps
unconditionally and unboundedly across all ~64 seeded entities (49
countries + ~15 clubs, `MVP-SCOPE.md`) — the exact cost profile ADR-0088
exists to avoid paying casually or repeatedly. Manually re-dispatching it on
a regular cadence to catch transfers would silently reintroduce the
incident ADR-0088 just fixed.

This gap and remediation were raised directly by the product owner in a
design discussion following S-186, not discovered independently.

## Decision

`PlayerCareerPrefetchService.PrefetchAsync` gains an optional
`int? maxEntitiesToResweep` parameter (default `null`):

- **`null`** (the value `prefetch-player-careers.yml`'s existing
  `workflow_dispatch`-only trigger keeps passing, unchanged) reproduces
  ADR-0088's exact behavior with no behavior change: every never-swept
  country/club is swept, every already-swept one is skipped forever. This
  is still the deliberate, unbounded "sweep everything not-yet-done" escape
  hatch after a purge or reference-data change.
- **A non-null N** (used only by the new weekly `resweep-player-careers.yml`
  cron, default 2) additionally re-sweeps up to N already-swept entities —
  chosen as the N entities with the **oldest** `PlayerPoolSweptAt` values,
  i.e. the ones most overdue for a refresh — on top of every never-swept
  entity, which remains always swept, uncapped, and never competing for the
  N budget. A row selected this way is treated identically to a never-swept
  row for the rest of `SweepAsync`: same live Wikidata fetch, same
  `markSweptAsync` re-write (which restarts its place in the rotation), same
  `SweepPoolAsync` dedup read-back.

`SplitResweepBudget` divides one top-level N across the country and club
sweeps, since each only knows its own row set: `countriesShare = ceil(N/2)`,
`clubsShare = N - countriesShare`. The odd remainder rounds toward the
country side deliberately — there are more seeded countries (49) than clubs
(~15), so giving countries the extra unit keeps each pool's own full
rotation period roughly comparable rather than letting the smaller club
budget round down twice as often. `N=2` (the product owner's own stated
default) therefore splits into 1 country + 1 club per run. `null` or
non-positive input collapses to `(0, 0)`, which is `SweepAsync`'s existing
"0 means no resweep" default — i.e. ADR-0088's unchanged skip-forever
behavior with no new code path exercised.

A new, separate workflow, `resweep-player-careers.yml`, calls the same
`prefetch-player-careers` CLI verb with this bounded argument on a weekly
cron (Sunday 05:15 UTC, staggered after `warm-grid-cache.yml`'s 04:30 UTC
slot on the same DevOps rotation). It is deliberately a separate file, not a
second cron entry on `prefetch-player-careers.yml`: GitHub Actions cannot
parameterize `on.schedule` entries with different inputs, and the two jobs'
purposes are different enough (a small, bounded, automatic weekly rotation
vs. an explicit, unbounded, manual "sweep everything" escape hatch) that
keeping them visually and operationally distinct in the Actions UI is
clearer than one file serving two different "modes." `prefetch-player-careers.yml`
itself is otherwise untouched by this ADR.

The CLI verb (`CliVerbDispatcher.HandlePrefetchPlayerCareersAsync`) now
accepts an optional second argument, `maxEntitiesToResweep`, and — because a
second token is now meaningful where before any extra token was ignored —
switches from S-112's "exact-match, any extra token silently falls through
to starting the server" dispatch shape to the "prefix-match, validate and
throw on a malformed shape" shape every other verb that takes a real
argument already uses (e.g. `purge-player-pool`'s confirmation phrase). A
non-integer or negative second argument now throws `InvalidOperationException`
loudly rather than silently falling through to starting the web server —
narrowing this one verb's own argument handling, not a dispatcher-wide
change to `CliVerbDispatcher`'s two established match shapes (see that
class's own S-112 doc comment for the full "exact-match" vs. "prefix-match"
taxonomy this deliberately moves one entry between).

## Reconciling this with ADR-0088

This is a **deliberate, controlled, bounded** reopening of ADR-0088's "no
staleness window" call, not a contradiction of it, and not a reversal of
ADR-0088's own reasoning for rejecting a staleness window as the *primary*
skip mechanism:

- ADR-0088's core decision — skip a row entirely once ever swept, as the
  default and only behavior of an unbounded `null`-argument run — is
  completely unchanged. This ADR does not touch that default; it adds a
  second, opt-in, separately-triggered mode on top of it.
- The worst-case cost delta this ADR adds is precise and small: **at most N
  additional pool fetches (live Wikidata queries) plus N dedup read-backs
  (`GetPlayerAttributesAsync`/`GetCareerStintsByPlayerIdsAsync` against
  Supabase Postgres) per week**, bounded and known ahead of time by the
  workflow's own `max_entities_to_resweep` input (default 2). Compare this
  to the actual incident ADR-0088 fixed: 9 unbounded manual re-dispatches in
  ~36 hours, each re-sweeping the **entire** ~64-entity seeded pool (touching
  193,382 players / 527,252 stints on the one run that completed), which
  produced a confirmed ~1.3GB single-day egress spike. This ADR's weekly
  cost ceiling is roughly two orders of magnitude smaller than a single one
  of those 9 re-dispatches, and it happens on a fixed weekly schedule
  instead of an unpredictable manual burst — there is no code path by which
  this rotation can escalate into a full unbounded re-sweep; the unbounded
  path (`null`) remains completely separate, still gated behind a deliberate
  `workflow_dispatch`, and this ADR does not change how often that path gets
  used.
- Because the bounded and unbounded modes are the same service and the same
  `SweepAsync` skip logic with only the selection set widened, there is no
  new query shape, no new failure mode, and no new class of Supabase load —
  only a small, fixed-size, known-in-advance addition to the existing
  per-row cost the skip logic already bounds correctly.

## The accepted staleness-window trade-off (stated explicitly)

At the product owner's own stated default of N=2/week, split roughly 1
country + 1 club: a full rotation through all ~49 seeded countries takes
roughly 49 weeks, and through all ~15 seeded clubs roughly 15 weeks. A
player transferring into an already-swept country's or club's pool is
therefore **not** noticed immediately — it surfaces only once that specific
country or club comes back up in the rotation, which could be anywhere from
one week to most of a year out, depending on where in the rotation order
that particular row currently sits (oldest-`PlayerPoolSweptAt`-first, so a
row that was swept very recently sits at the back of the queue).

This is the accepted trade-off: **freshness within roughly a season, not
real-time.** It is explicitly not a claim that this rotation keeps every
pool continuously up to date — it trades a small, known, weekly egress cost
for eventual (not immediate) correction of the exact gap ADR-0088's
skip-forever default introduced. If a specific country or club needs to be
refreshed sooner than its place in the rotation would produce, the existing
unbounded `workflow_dispatch` path (or REQ-111's invalidation tools) remain
the way to force that, same as before this ADR.

## The `concurrency:` group decision

`resweep-player-careers.yml` and `prefetch-player-careers.yml` each use
`concurrency: { group: ${{ github.workflow }}, cancel-in-progress: false }`
— the same S-186 pattern applied to every bulk Wikidata workflow. Because
`${{ github.workflow }}` resolves to each file's own workflow name, these
are **two separate concurrency groups**, not one shared group. This means
the two workflows **can** run concurrently against the same underlying
`PlayerPoolSweptAt`/`PlayerCareerStint`/`PlayerAttribute` state if a manual
`workflow_dispatch` of the unbounded job happens to land during the weekly
bounded cron's run window (or vice versa).

This is recorded here as a deliberate, low-probability-overlap trade-off,
not an oversight — `resweep-player-careers.yml`'s own header comment says
so directly: "This is a same-workflow guard only; it does not by itself
prevent this workflow and prefetch-player-careers.yml from running at the
same time (they are different `github.workflow` values, so different
concurrency groups) — an acceptable overlap given prefetch-player-careers
stays manual/exceptional and this job's own cost is small either way." A
single shared concurrency group spanning both files was considered
implicitly and rejected: it would serialize an exceptional, manual,
unbounded sweep behind a routine weekly bounded one (or the reverse) for no
correctness reason — both jobs' actual write paths
(`markSweptAsync`/`AddCareerStintsBatchAsync`/`UpdateCareerStintCompletionsAsync`)
are idempotent per-row and do not corrupt each other under interleaving; the
worst case of an accidental overlap is some duplicated Wikidata query cost
for whichever rows both runs happen to touch in the same window, not a data
correctness issue. Given the unbounded job's manual/exceptional nature
("stays manual/exceptional" per that same comment), this overlap is
expected to be rare in practice.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| A single shared `concurrency` group across both workflows | Guarantees the two jobs never run at the same time | Serializes an exceptional manual unbounded sweep behind a routine weekly bounded one (or vice versa) for no correctness reason — both write paths are already idempotent per row under interleaving | Rejected — the two jobs' purposes are different enough (routine small rotation vs. exceptional full sweep) that forcing mutual exclusion adds an operational constraint (a manual re-dispatch might have to wait out an in-progress weekly cron) with no correctness benefit |
| Add the rotation as a second cron entry on `prefetch-player-careers.yml` itself | One fewer workflow file | GitHub Actions cannot pass a different `arg` input per `on.schedule` entry within one workflow — there is no way to express "manual dispatch gets no argument, the Sunday cron entry gets `max_entities_to_resweep: 2`" in a single file | Rejected — not mechanically possible with GitHub Actions' schedule trigger shape |
| No bound at all — just re-run `prefetch-player-careers.yml` (unbounded) on a schedule instead of `workflow_dispatch`-only | Simplest possible change, no new parameter | Exactly the unbounded full-sweep cost profile the current incident (ADR-0088) fixed — putting that on any recurring schedule reintroduces unbounded weekly egress risk, the very thing this story exists to avoid | Rejected — defeats the purpose |

## Consequences

- Positive: a player transferring into an already-swept country's or club's
  pool is eventually noticed again, without reintroducing unbounded
  re-sweep cost — the exact gap identified in the product owner's own
  design-discussion follow-up to S-186.
- Positive: the weekly cost ceiling is fixed, known in advance, and roughly
  two orders of magnitude smaller than a single one of the 9 re-dispatches
  that caused the original incident — this cannot, by construction,
  reintroduce that incident's shape (an unbounded full-pool re-sweep
  repeated in a tight burst).
- Negative / trade-off accepted: freshness for any specific already-swept
  pool is bounded by its place in a roughly-season-long rotation (~49 weeks
  for countries, ~15 weeks for clubs at N=2/week split), not immediate — see
  "The accepted staleness-window trade-off" above.
- Negative / trade-off accepted: `resweep-player-careers.yml` and
  `prefetch-player-careers.yml` can run concurrently against the same
  underlying data, an accepted low-probability overlap rather than a
  cross-workflow mutual-exclusion guarantee — see "The `concurrency:` group
  decision" above.
- Negative / trade-off accepted: `prefetch-player-careers`'s CLI argument
  handling moved from S-112's "exact-match, silent-fallthrough-on-extra-token"
  shape to the "prefix-match, validate-and-throw" shape for this one verb
  only — a narrow, deliberate exception to that class's general taxonomy,
  not a dispatcher-wide behavior change.
- Follow-up: none currently identified. If the rotation period (~49/~15
  weeks) proves too slow in practice once real transfer-window data is
  observed, raising the default `max_entities_to_resweep` (currently 2) is
  the expected first lever, not a structural change to this ADR's design.

## For AI agents

Do not widen `maxEntitiesToResweep`'s selection to include never-swept
rows in its cap — never-swept rows must always remain unconditionally and
uncapped included in every sweep, regardless of budget size; only the
already-swept population is bounded. Doing otherwise would silently throttle
onboarding of brand-new seeded reference data behind the same budget meant
only to bound *re*-sweeping.

Do not merge `resweep-player-careers.yml` and `prefetch-player-careers.yml`
into a single workflow file, or give them a shared `concurrency` group, as
a "cleanup," without first re-reading "The `concurrency:` group decision"
above — both the separate-file and separate-group choices are deliberate,
not incidental duplication.

Do not read this ADR as reopening ADR-0088's core "no staleness window"
decision for the unbounded (`null`) path — that path's behavior is
completely unchanged by this ADR. This ADR only adds a second, separately
bounded and separately triggered mode; if a future change is tempted to
also add a staleness window to the unbounded path itself, that is a
different decision needing its own ADR, not an incidental extension of this
one.
