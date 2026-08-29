# ADR-0093: Recent-transfer sweep also writes `PlayerAttribute` — a correction of ADR-0092's stated caution

- **Status:** Accepted
- **Date:** 2026-08-29
- **Related requirements:** REQ-110 (see ADR-0092's "The REQ-110 tag" for
  the accepted imperfection in this fit, which this ADR does not change)
- **Related components:** COMP-07 (DataSync.Clients), COMP-05
  (Games.XGGrid), COMP-06 (Data.PlayerStore)

## Context

This ADR is **a correction of ADR-0092, not an independent decision layered
on top of it.** ADR-0092 (S-188) called the Grid-vs-Path freshness asymmetry
"the single most important boundary this ADR records" and deliberately had
`RecentTransferSweepService` write only `PlayerCareerStint`, never
`PlayerAttribute`/`PlayerData` — on the stated grounds that a naive
`PlayerAttribute` write there "would insert that player without going
through any of that invalidation machinery" and "risk silently corrupting a
`ConfirmedLowMatchPair`/cached-final pair count that Grid's
generation/live-lookup paths currently trust," with no re-verification
step. Its own "For AI agents" section told future agents explicitly not to
extend this service to write `PlayerAttribute` without a new ADR solving
that risk.

This story (S-189, `docs/backlog.md` Epic 26), explicitly requested by the
product owner to close that gap, did the precise trace ADR-0092 asked for.
That trace — done this session, confirmed independently by both
`architecture-reviewer` and the implementing `backend-implementer`, and
hand-verified against the actual source rather than assumed — found
ADR-0092's risk assessment **overstated**, in two different ways for the
two tables it named:

- **`ConfirmedLowMatchPair` (ADR-0050): the caution does not apply at all.**
  `GridGenerationService.GetMatchCountAsync` (~line 301) computes candidate
  validity via `IPlayerAttributeRepository.CountPlayersWithBothAttributesAsync`
  — live, always-fresh, never cached, never consulting
  `ConfirmedLowMatchPair`. Guess-correctness checking
  (`IPlayerOverrideRepository.HasEffectiveAttributeAsync`) also reads
  `PlayerAttribute`/`PlayerOverride` directly, live. `ConfirmedLowMatchPair`
  is consulted in exactly one place codebase-wide:
  `PlayerCacheWarmingService.WarmAsync` (`SweepPairsAsync`, ~line 305), and
  only *after* `cachedCount >= options.MinValidAnswers` is already checked
  first (~line 288) — so a rising local count self-heals without ever
  consulting a stale row, and `RecordConfirmedLowAsync` is upsert-safe, not
  insert-only. **`ConfirmedLowMatchPair` staleness is provably never a live
  Grid-correctness risk — only a missed opportunity for
  `PlayerCacheWarmingService`'s own maintenance run to discover a newly
  grown pair sooner.**

- **`PairLookupFailure` (ADR-0052): the caution was partially right, for a
  reason ADR-0092's "never consult... at all" framing missed.** Unlike
  `ConfirmedLowMatchPair`, `PairLookupFailure` is consulted from TWO
  places, not one: `PlayerCacheWarmingService` (maintenance, same as
  `ConfirmedLowMatchPair`) AND `GridLiveLookupDispatcher.TryRefreshCellAsync`
  (`GridLiveLookupDispatcher.cs`, ~line 50) — a real guess-time path
  (REQ-211's live-lookup fallback), reached from
  `GuessSubmissionService.SubmitGuessAsync`. Concretely,
  `GuessSubmissionService.cs:71-78` shows a `LiveLookupUnavailableException`
  thrown from that dispatcher is caught and turned into
  `GuessSubmissionResult.Rejected(GuessSubmissionOutcome.LiveLookupUnavailable)`
  **before any `Guess` row is persisted** — no attempt is consumed, matching
  ADR-0046's existing guarantee. **So clearing a stale `PairLookupFailure`
  row is still never a correctness risk** — a live-lookup failure always
  fails closed as "unknown," never as a wrong "incorrect" verdict — **but
  leaving a stale one in place after a relevant local data change means the
  next guess against that pair pays an unnecessary live Wikidata round-trip
  (and its ~28s timeout, if the underlying failure was genuinely
  structural) that a still-present marker would have short-circuited.** A
  latency trade-off, not a correctness one, and self-healing even if never
  cleared: the next `PlayerCacheWarmingService` run that still fails
  re-records the marker regardless.

Both pieces of this trace matter and must not be collapsed back into
ADR-0092's cleaner-but-wrong single story: `ConfirmedLowMatchPair` was
never a risk at all; `PairLookupFailure` was a real (if narrow) live path,
just never a correctness one.

## Decision

`RecentTransferSweepService`'s arrival-persistence path now also writes a
`PlayerAttribute`+`PlayerData` row for `(player, "club", clubName)` on a
genuinely new arrival — mirroring
`PlayerCareerPrefetchService.FetchAndPersistBatchAsync`'s existing
dedup-then-write shape (a `HashSet`-backed "already has it" gate, one
`PlayerAttribute` paired with one `PlayerData` per newly-attributed player,
reusing `WikidataLookupService.ClubAttributeType`/`WikidataSource`/
`VerifiedConfidence` rather than a second copy of those constants).
Departures still get no attribute write or removal — Grid's "ever played
for this club" answer semantics mean a player who left remains a correct
answer forever, so nothing about departure handling changes.

Alongside the write, a new
`IPlayerDataQualityRepository.ClearMatchPairAsync(attributeTypeA, valueA,
attributeTypeB, valueB)` deletes any `ConfirmedLowMatchPair` row AND any
`PairLookupFailure` row pairing the new club against every OTHER attribute
value the newly-attributed player already has, checking BOTH possible
stored orderings — unlike every sibling method on
`IPlayerDataQualityRepository`, which can rely on a single fixed ordering
because their only caller, `PlayerCacheWarmingService.SweepPairsAsync`,
always passes one stable ordering per sweep type. This caller has no such
fixed convention to rely on (a Club × Club pair's stored order depends on
`ClubDefinition`'s seed-list position, not anything this caller's own
arguments can derive), so both orderings must be checked. Bounded by
however many other attributes one player already has (a handful at most)
— never a club-wide sweep the way `StaleClubAttributeCleaner`'s broader
"delete every row involving this club" shape is.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Do nothing — leave Grid unfixed, accept ADR-0092's asymmetry permanently | No new invalidation-safety surface to reason about | The product owner explicitly requested closing this gap and called it important; ADR-0092 itself flagged it as a candidate follow-up, not a permanent design choice | Rejected — the underlying risk this ADR corrects turned out to be smaller than ADR-0092 assumed, and the product need is real |
| Invalidate more broadly — reset `PlayerPoolSweptAt` for the whole club on any new arrival | Simpler to reason about ("just re-sweep everything") | `PlayerPoolSweptAt` resets are ADR-0078's established pattern for wipe/reset tools (`StaleClubAttributeCleaner`, `purge-player-pool`), not for narrow per-pair updates — using it here would misrepresent a single new attribute as a full pool re-verification, and would reintroduce exactly the unconditional-resweep cost shape ADR-0088 fixed | Rejected — wrong shape, and reintroduces a cost class this codebase already paid down |
| Add a full re-verification/live-requery step for every touched pair before writing, instead of clearing the stale markers | Would fully "solve" the invalidation risk rather than just neutralize it | The trace above shows there is no live-correctness risk to solve — `ConfirmedLowMatchPair`/`PairLookupFailure` are never consulted on any live-correctness path; a live re-query here would be pure unnecessary Wikidata cost for a risk that doesn't exist | Rejected — solving a problem that isn't there, at real query cost |

## Consequences

- Positive: a transfer picked up by `sweep-recent-transfers` is now a valid
  xG Grid guess answer immediately, closing the Grid-vs-Path freshness
  asymmetry ADR-0092 deliberately left open, without waiting for
  ADR-0090's rotation or a full `prefetch-player-careers` run.
- Positive: reuses `PlayerCareerPrefetchService.FetchAndPersistBatchAsync`'s
  existing dedup/write shape and `WikidataLookupService`'s own constants —
  no new attribute-write pattern introduced.
- Positive: the invalidation this decision performs is real and correctly
  scoped (bounded by one player's own attribute count), even though the
  correctness risk it neutralizes for `ConfirmedLowMatchPair` was never
  actually present — it is still the right call, since leaving a stale
  `PairLookupFailure` row in place has a real (if narrow) latency cost, and
  clearing both is cheap and mirrors ADR-0078's own "invalidation is not
  optional" stance.
- Negative / trade-off accepted: none new beyond what ADR-0092 already
  accepted for this service's overall shape (bounded club set,
  `workflow_dispatch`-only cadence) — this decision only removes a
  previously-assumed risk, it does not add a new one.
- Follow-up: none required by this ADR itself. If a future change adds a
  third consultation point for `ConfirmedLowMatchPair` or
  `PairLookupFailure` beyond the two named here
  (`PlayerCacheWarmingService` and, for `PairLookupFailure` only,
  `GridLiveLookupDispatcher`), this ADR's "never a correctness risk"
  conclusion needs re-checking against that new call site before being
  assumed to still hold.

## For AI agents

Do not assume `ConfirmedLowMatchPair`/`PairLookupFailure` gate live game
correctness anywhere — they don't. `ConfirmedLowMatchPair` is read only by
`PlayerCacheWarmingService.WarmAsync`'s maintenance heuristic, after the
local cached count is already checked first. `PairLookupFailure` is read by
that same maintenance path AND by `GridLiveLookupDispatcher
.TryRefreshCellAsync` (REQ-211's guess-time fallback) — but even there, a
stale/cleared row only ever changes whether a guess pays a live Wikidata
round-trip before answering, never whether the answer itself is correct
(ADR-0046's fail-closed-as-unknown guarantee is unconditional). If you find
a third place either table is consulted for anything beyond these two
established call sites, do a fresh trace of that specific site before
assuming this ADR's "never a correctness risk" reasoning still applies to
it — do not extend this conclusion by analogy without checking.

Do not read ADR-0092's original "Grid-vs-Path freshness asymmetry" section
as still describing current behavior — that asymmetry is closed as of this
ADR. ADR-0092 itself is not rewritten (historical ADRs are not edited to
reflect new decisions); its status line and the architecture/requirements
docs point here instead.
