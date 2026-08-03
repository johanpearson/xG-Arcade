# ADR-0058: xG Path target cycle tracking — persistence shape and pool scope

- **Status:** Accepted
- **Date:** 2026-08-03
- **Related requirements:** REQ-1208 (no-repeat target selection across rounds), REQ-1209 (admin cycle visibility), REQ-1201 (target eligibility), REQ-1202 (round structure)
- **Related components:** COMP-11 (Games.XGPath), COMP-06 (Data.PlayerStore)

## Context

Player feedback (`docs/backlog.md` S-093): as more familiar players get
selected via ADR-0056's Wikipedia-sitelink-count familiarity filter, the
same xG Path targets are repeating noticeably across rounds.
`XGPathGameModule.PickDistinct` (REQ-1202) only guarantees no repeat
*within* one round instance — nothing tracks or prevents a target
reappearing *across* rounds. REQ-1208 (drafted this session) requires that
a target not be reselected until every eligible player in the current pool
has been used once (a full "cycle"), and REQ-1209 requires an admin-visible
signal when a cycle completes.

Two questions had no obvious default and needed an explicit decision
rather than an assumption:

1. **What persists "already used this cycle,"** and where does that state
   live? `Player` (COMP-06) is a shared entity read by xG Grid as well as
   xG Path — ADR-0042 already rejected widening a shared table
   (`PlayerCareerStint` vs. `PlayerAttribute`) for one consumer's needs, on
   the same "no cross-game leakage into shared data" reasoning that applies
   here.
2. **Which pool a cycle is scored against.** ADR-0056's familiarity filter
   narrows REQ-1201's structurally-eligible pool, and is itself live and
   somewhat unstable — re-queried every generation, can shrink or grow,
   fails open on a Wikidata outage. A cycle over the full
   structurally-eligible pool is a different, larger, more stable cycle
   than one over the familiarity-filtered pool that `PickDistinct` actually
   samples from.

## Decision

**Persistence:** cycle-tracking state is xG Path's own data, following the
same "every game module's entities live in the shared `DbContext`, scoped
to that module" precedent ADR-0014 already established and
`PathInstance`/`PathPuzzle`/`PathTemplate` (ADR-0045) already follow — not
a new field on the shared `Player` entity. Concretely: a cycle counter
(current cycle number, incremented on rollover) plus a per-player "used in
the current cycle" record, scoped to `XGArcade.Games.XGPath`'s own tables
in `XGArcade.Data`. Enough state is persisted to answer REQ-1209's display
needs directly (pool size as of the most recent generation, used/remaining
counts, most recent cycle-completion timestamp) without new aggregation
queries. Exact schema is `implementation-document.md`'s job, not this
ADR's.

**Pool scope:** a cycle is scored against the same pool
`GetEligiblePlayerIdsAsync` already computes and `PickDistinct` already
samples from — REQ-1201's structural checks **narrowed by ADR-0056's
familiarity filter** — not the larger structurally-eligible-only pool.
Targets are only ever actually selected from the familiarity-filtered set,
so scoring a cycle against the larger pool would count players selection
can structurally never reach, and such a cycle could never complete.
Cycle completion is defined tolerantly, not exactly: a cycle completes once
the *remaining unused* portion of the current live pool drops below what a
generation needs (not once it hits exactly zero), so the rule degrades
gracefully against ADR-0056's documented live instability instead of
depending on the pool ever stabilizing.

**Amendment (2026-08-03, post-review):** `GET /admin/xg-path/cycle`
(REQ-1209) reads `IPathInstanceRepository.GetCycleStateAsync` directly from
the Api layer, bypassing `IGameModule` — the same mechanism ADR-0016/
ADR-0048 already bless for read-only display endpoints. Those two ADRs'
own decision text is scoped to queries "against an already-generated game
instance" (`GridInstance`/`GridCell`, `PathInstance`/`PathPuzzle`) —
`PathTargetCycle` is not per-instance content, it's cross-instance
rotation/bookkeeping state, a structurally different kind of read neither
ADR examined directly. This ADR confirms, as a deliberate extension rather
than a silent assumption, that ADR-0016/ADR-0048's direct-repository-read
precedent covers this case too: a pure, read-only query against a game
module's own persisted state, regardless of whether that state is scoped
to one instance or spans instances. The same conditions both ADRs require
still apply — read-only, no generation trigger, no scoring — and continue
to apply here (`GetCycleStateAsync` never calls `IPlayerFamiliarityService`
or `GenerateInstanceAsync`).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Flag/timestamp column on `Player` (e.g. `LastPathTargetCycle`) | No new table; simple read | Repeats the exact cross-game shared-table leakage ADR-0042 already rejected — `Player` is read by xG Grid too, and xG Path's rotation concern has nothing to do with xG Grid | Rejected — violates existing precedent, not xG Path's own data |
| New xG Path-scoped table(s) (chosen) | Keeps the concern inside `Games.XGPath`'s own boundary, mirrors `PathInstance`/`PathPuzzle`/`PathTemplate`'s existing pattern (ADR-0014/ADR-0045) | One more table to maintain | Best fit — consistent with how every other xG Path-specific concern is already modeled |
| Cycle scored against the full structurally-eligible pool (REQ-1201 only, ignoring ADR-0056) | Simpler, more stable — doesn't move generation to generation | Could never complete in practice: it would always be waiting on players `PickDistinct` structurally cannot select (permanently below ADR-0056's sitelink threshold) | Rejected — doesn't match what "cycle" needs to mean for players to actually stop seeing repeats |
| Cycle scored against the live familiarity-filtered pool, requiring an exact-zero remaining count to complete | Simple completion rule | ADR-0056's pool is live and can shrink/grow/fail open between generations — an exact-zero requirement could stall indefinitely if the filtered set keeps shifting slightly | Rejected — chose a tolerant "remaining < N" threshold instead, for the same reason |

## Consequences

- Positive: directly fixes the reported "same familiar players repeating"
  complaint without touching REQ-1201's structural eligibility checks or
  ADR-0056's familiarity filter itself
- Positive: keeps xG Path's rotation state fully inside `Games.XGPath`'s own
  boundary — no new read/write path on the shared `Player` entity, no risk
  to xG Grid
- Negative / trade-off accepted: a cycle's membership is defined by a live,
  per-generation-recomputed pool (ADR-0056), so the exact player set a
  cycle "contains" is not fixed at cycle start — accepted because scoring
  against the larger, stable structural pool would make cycles that never
  complete, which is worse
- Negative / trade-off accepted: one more xG Path-scoped table to maintain
  and keep in sync with `PickDistinct`'s selection step
- Positive: `AddInstanceWithCycleUsageAsync`'s persistence write (`PathInstance`
  + `Puzzles` + `PathTargetCycle` + `PathCycleTargetUsage` rows, all in one
  `SaveChangesAsync`) prevents the puzzle-target write and the "recorded as
  used" write from ever diverging on a partial failure, per REQ-1208's own
  "at the same time" wording. This is a new shape for this codebase — every
  prior multi-aggregate write here (e.g. `LeagueRepository`'s league +
  membership calls) composes across separate `SaveChangesAsync` calls — worth
  reusing this bundled-write shape for a future feature only when the same
  "these two writes must never diverge" requirement genuinely applies, not
  as a default way to write multiple entities.
- Follow-up: if ADR-0056's `MinSitelinkCount` is later tuned (per that
  ADR's own follow-up), the live pool's size changes, which changes how
  often cycles complete — that's expected and requires no change here

## For AI agents

Cycle-tracking state must stay inside `Games.XGPath`'s own tables — never
add a cycling-related field to the shared `Player` entity (COMP-06) to
"simplify" this; that would reproduce the exact cross-game leakage ADR-0042
already rejected. Cycle completion must stay a tolerant "remaining unused
count below what this generation needs," not an exact-zero check — ADR-0056's
pool is deliberately live and unstable, and an exact-zero rule can stall
indefinitely against that. If ADR-0056's fail-open behavior on a Wikidata
outage is ever changed, revisit whether this ADR's pool-scope reasoning
still holds.
