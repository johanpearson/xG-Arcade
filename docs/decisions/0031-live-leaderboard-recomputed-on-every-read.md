# ADR-0031: Live leaderboard contributions are recomputed on every read, never cached or snapshotted

- **Status:** Accepted
- **Date:** 2026-07-19
- **Related requirements:** REQ-406, REQ-407, REQ-204, REQ-206, REQ-401, REQ-404, REQ-607
- **Related components:** COMP-02 (Core.Leagues), COMP-03 (Core.Rounds), COMP-04 (Core.Scoring), COMP-05 (Games.XGGrid / `IGameModule`)

## Context

REQ-406 (the shared/per-league leaderboard folds in a live contribution from
the currently-active round) and REQ-407 (a standalone leaderboard scoped to
just the active round) were drafted 2026-07-19 to close the gap REQ-206's
status note has flagged since S-029: the leaderboard only ever reflects
`SUM(FinalPoints ?? 0)` over *closed* rounds, so a round in progress
contributes nothing until it locks at close (ADR-0022).

Both REQs, as drafted, require every member's provisional total — computed
per cell from `LivePoints` (REQ-204) or `MaxPointsPerCell` for a
locked-incorrect cell — to be "recomputed on every request — no stored or
cached snapshot," mirroring REQ-204's existing "always live, never persisted
until close" rule for a single cell read by a single player.

That precedent doesn't transfer for free. REQ-204's live computation is one
player, reading their own already-correctly-guessed cells, on their own `GET
/rounds/current` request. REQ-406/407 require, on *every* leaderboard read,
enumerating *every* round participant and *every* cell of the active round
(via `IGameModule.GetCellIdsAsync`, ADR-0021) to build the full ranked list
before any page can be sliced. This collides with two things
`architecture-document.md` already documents as deliberate:

- **§6.2a's global leaderboard flow** explicitly computes the all-time total
  "database-side (`GroupBy`), not by re-summing REQ-206's per-round
  `ScoreCalculator` in memory" — a deliberate cost choice. A live per-cell
  `LivePoints` contribution cannot be expressed as a single SQL aggregate;
  it requires `UniquenessCalculator`'s per-cell, per-candidate, app-side
  computation, for every member, on every read.
- **REQ-607/S-034's pagination contract** (`cursor`/`pageSize`) was built so
  a leaderboard read has a bounded cost regardless of league size — the DB
  sorts/limits, the app slices. Once ranking itself depends on a live,
  app-side-computed number for every member, the full membership (and full
  active-round cell set) must be recomputed before any page can be sliced,
  which breaks that bounded-cost property.

This is a real architectural tradeoff — not just a bigger instance of an
already-accepted pattern — so it needs its own decision record rather than
being silently implied by REQ-406/407's acceptance criteria.

## Decision

Accept the cost/coupling change: REQ-406/407's leaderboard aggregation is
computed **live, in application memory, on every read**, with no snapshot,
cache, or materialized view of any kind. `LeaderboardService` (COMP-02)
resolves the active round (COMP-03) and enumerates its cells
(`IGameModule.GetCellIdsAsync`, COMP-05) on every leaderboard request, not
only at round close.

REQ-607's `cursor`/`pageSize` contract is kept, but its guarantee is
narrowed: it still bounds the **size of the response page**, but no longer
bounds the **cost of computing the full ranking** that page is sliced from —
every member's live contribution across the whole active round must be
computed before any page can be returned. This is an explicit, accepted
reversal of the cost property §6.2a originally recorded for the all-time
leaderboard, not a silent one.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Full live recompute, no cache (chosen) | Consistent with REQ-204's existing "always live" precedent; always correct by construction, no invalidation logic to get wrong; matches the product ask exactly ("reflected... while a round is still in progress," not "eventually consistent") | O(active participants × grid cells) work on every leaderboard read; breaks REQ-607/§6.2a's bounded-read-cost guarantee; new COMP-02→COMP-03/COMP-05 coupling on the hot read path, not just at round close | Chosen: at Tier 0's actual scale (one dev environment, a handful of testers, a hand-curated ~15-20-category grid, `MVP-SCOPE.md`) this cost is negligible in absolute terms even though it changes the asymptotic shape; avoids building cache-invalidation machinery before there is any evidence it's needed |
| Periodic snapshot / materialized view, refreshed on an interval | Bounds read cost independent of grid/membership size; keeps REQ-607's original guarantee intact | Reintroduces exactly the "stale value" problem REQ-204/ADR-0020 deliberately avoided for live per-cell points; needs a new scheduled refresh job with its own cadence-coupling risk (the same class of problem ADR-0027 already had to solve once for round generation) | Not chosen: no observed cost problem yet to justify the added complexity, and it would make the "live" leaderboard visibly lag, contradicting REQ-406/407's explicit intent |
| Push-based incremental update — update a stored provisional total on each guess submission instead of recomputing on read | Read stays a cheap stored-column lookup; write cost is spread out over time | Fan-out on every guess write (must update every affected league member's stored total); a stored provisional value can silently drift from the true live value if any write path misses the update, a correctness risk with no existing precedent in this codebase to build on safely | Not chosen: trades a read-cost problem for a write-fan-out and drift-risk problem that's arguably worse, with no established pattern here for safely maintaining derived state like this |
| Short-TTL cache in front of the live computation | Bounds worst-case read cost while staying mostly live; simple to layer on later | Directly contradicts REQ-406/407's drafted "recomputed on every request — no stored or cached snapshot" acceptance criteria as written; doesn't fix the underlying O(members × cells) cost, only amortizes it | Not chosen now: not a real fix, and adopting it would require revising REQ-406/407 too rather than being a transparent implementation detail |

## Consequences

- Positive: leaderboard behavior stays exactly as simple and provably
  correct as REQ-204's existing live-points precedent — no new
  invalidation logic, background job, or derived-state drift risk to get
  wrong.
- Negative / trade-offs accepted: `LeaderboardService` (COMP-02) gains a new
  dependency on active-round resolution (COMP-03) and
  `IGameModule.GetCellIdsAsync` (COMP-05) on **every** leaderboard read, not
  only at round close — a materially larger version of the coupling
  increase ADR-0021 already accepted for `ScoreLockingService` (which only
  runs once, at close, per round). REQ-607/S-034's original "leaderboard
  read cost is bounded regardless of league size" property (§6.2a) no
  longer holds for the live component — `cursor`/`pageSize` still bounds
  the response page size, not the cost of producing the full ranking behind
  it.
- Follow-up: revisit with a caching or materialized-view approach if any of
  the following is actually observed (not merely hypothesized): (a) an
  active round's participant count grows past roughly a few hundred in one
  league — Tier 0's dev-environment test scale is nowhere near this; (b)
  real-environment leaderboard read latency becomes user-noticeable; or (c)
  grid size grows enough (e.g. Tier 1's Trophy/expanded-club-pool work,
  `MVP-SCOPE.md`) that per-member cell enumeration cost becomes material.
  None of these are expected to fire at Tier 0 scale — same "ship small,
  revisit on evidence" posture `MVP-SCOPE.md` already applies everywhere
  else, and the same style of explicit, observable trigger ADR-0016/
  ADR-0019/ADR-0021 already use.

## For AI agents

If code you are about to write would contradict this decision — e.g. adding
a cache, snapshot, or scheduled refresh job for REQ-406/407's live
leaderboard contribution, or reintroducing a pure DB-side aggregate that
can't reflect `LivePoints` — stop and flag it rather than silently working
around it. Conversely, if you are implementing REQ-406/407 and find the
live recompute cost is *not* negligible even at Tier 0 scale (e.g. it
measurably slows a real leaderboard read), that is exactly this ADR's
documented follow-up trigger — flag it for a revisit rather than silently
adding caching without updating this record.
