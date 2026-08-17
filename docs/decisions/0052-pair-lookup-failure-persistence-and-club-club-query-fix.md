# ADR-0052: Remove cache-warming's same-run retry, persist cross-run technical failures (PairLookupFailure), and fix club-club's combinatorial query blowup

- **Status:** Accepted
- **Date:** 2026-08-01
- **Related requirements:** REQ-110
- **Related components:** COMP-05 (Games.XGGrid), COMP-06 (Data.PlayerStore), COMP-07 (DataSync.Clients)

## Context

`warm-player-cache.yml` stopped completing. Run #15 (2026-07-28 through
2026-08-01) was manually re-dispatched three times and every attempt got
cancelled at the workflow's 90-minute ceiling without finishing, on top of
CI logs that had become effectively unreadable (thousands of per-pair
`Warning`-level log lines, some carrying 15-20 line stack traces, for every
technical failure).

Diagnosis traced this to the 2026-07-28 "cache-warming-specific timeout and
same-run retry" extension (REQ-110), which was itself a response to a real
problem (133/1214 live queries silently swallowed as technical failures in
one run) but introduced a regression:

1. **The same-run retry doubled the cost of every failing pair.** A pair
   that fails technically now costs up to 2 × the cache-warming timeout
   (45s) instead of 1×, because it's retried once within the same run
   before being counted. A same-run retry only helps a *transient* failure
   (a one-off 502, a momentary slow response) — for a *structural* one
   (a query that always fails for that pair) it just pays the cost twice
   for no benefit.
2. **A technical failure was never persisted anywhere.** Only a genuine
   (possibly zero-match) answer gets a `ConfirmedLowMatchPair` marker
   (ADR-0050). A pair that fails technically is retried, at full — now
   doubled — cost, on every single future run, forever, with no way to
   converge.
3. **A specific, confirmed, structural cause was found for many of the
   failures.** Reading run #15's tail log showed a contiguous stretch where
   *every* club-club pair involving a handful of specific clubs failed —
   not intermittently, every time, regardless of partner club. One failure
   surfaced the mechanism directly: a JSON parse error at binding row
   250,204. `WikidataClient.BuildClubClubIntersectionQuery`'s plain join on
   two independent P54 statement-path patterns (`?player p:P54
   ?clubAStatement`, `?player p:P54 ?clubBStatement`) binds both statement
   variables in the outer pattern — a player with multiple non-deprecated
   P54 statements at club A (loan spells, a return transfer) times multiple
   at club B produces one result row per *combination*, on top of the
   query's existing per-alias multiplication. For two clubs with a large,
   well-documented, historically-overlapping squad, this produced a real
   250,000+ row WDQS response. No timeout, however long, reliably finishes
   that — it needed a smaller query, not a bigger budget.

Together: (1) and (2) meant every run re-fought the same doomed pairs from
scratch, at doubled cost, before ever making progress on anything new; (3)
meant a real, non-empty set of pairs were genuinely undoomed-by-nothing-but-
query-shape and would fail on literally every run regardless of retries,
timeouts, or WDQS load. The 90-minute budget was being spent entirely on
pairs that could never succeed.

## Decision

Three changes, one ADR because they were diagnosed and fixed together and
the first two are mechanically linked (removing the same-run retry is only
safe once cross-run persistence exists to actually converge):

**1. Remove the same-run retry** (`PlayerCacheWarmingService`'s
`LookupWithSameRunRetryAsync`, `MaxAttemptsPerPair`). Every pair is
attempted exactly once per run. The cache-warming-specific timeout tier
itself (`WikidataQueryTimeoutTier.CacheWarming`, 45s) is unaffected and
stays — this only removes the *retry*, not the wider budget.

**2. Persist cross-run technical failures**, mirroring `ConfirmedLowMatchPair`'s
own shape (ADR-0050) but as a new table, `PairLookupFailure`
(`XGArcade.Data.Entities`), reachable only through
`IPlayerStoreRepository.IsPersistentTechnicalFailureAsync`/
`RecordTechnicalFailureAsync`/`ClearTechnicalFailureAsync`. Same
composite-key shape `(FirstAttributeType, FirstAttributeValue,
SecondAttributeType, SecondAttributeValue)`, same "new table, not a column
on `PlayerAttribute`/`ConfirmedLowMatchPair`" reasoning (a technical
failure often has no `Player` rows to reference, same as a genuine
confirmed-low pair), same invalidation surface (`StaleClubAttributeCleaner`,
`purge-player-pool`). A separate table from `ConfirmedLowMatchPair`
specifically because the two are different *kinds* of fact:
`ConfirmedLowMatchPair` is a confirmed fact about Wikidata's data (won't
change unless the reference data does); `PairLookupFailure` is a fact about
this codebase's own query reliability against a pair right now (might
resolve on its own — a WDQS outage recovering — or with a query-shape fix).
`ConsecutiveFailureCount` increments on each run-level failure and resets
(row deleted) the moment the pair gets a real answer.
`PlayerCacheWarmingService` skips a pair once the count reaches
`PersistentFailureThreshold = 2` — two *consecutive runs*, not attempts, so
a one-off transient blip still gets a real, independent second chance on
the very next run before being treated as structural.

**3. Fix the confirmed query-shape blowup**: `BuildClubClubIntersectionQuery`
wraps each club's P54 statement-path match in its own `FILTER EXISTS { }`
block instead of a plain join. `FILTER EXISTS` checks "does at least one
qualifying statement exist" without binding `?clubAStatement`/
`?clubBStatement` in the outer pattern, so neither club's statement count
can multiply result rows — the result is exactly one row per matching
player (before the still-intentional per-alias multiplication, unchanged).
This is safe specifically because club-club never reads the shared query
footer's per-statement qualifiers (`?clubStatement`, singular — a different
variable, confirmed dead code for this builder per its own pre-existing
comment); country-club/national-team-club/trophy-club **cannot** use the
same trick without losing those qualifiers (ADR-0042/S-079's career-stint
data), so this fix is scoped to `BuildClubClubIntersectionQuery` only.

**Also, separately (log cleanup, not a structural decision but recorded
here since it shipped in the same pass):** the two per-pair failure logs in
`WikidataClient.RunIntersectionQueryAsync` (timeout, and HTTP/parse error)
are downgraded from `Warning` to `Debug`. At `Warning`, a run with a few
hundred technical failures produced thousands of lines — including full
multi-frame stack traces for JSON parse errors — that buried the one
`Information`-level line that actually matters (`WarmAsync`'s own run
summary, which already reports the technical-failure count and names every
failing pair). `Debug` is filtered out by this project's default
`Information` log level (`appsettings.json`), so a normal run's console
stays readable; the per-pair detail is still available by setting
`Logging:LogLevel:Default` to `Debug` when actually troubleshooting a
specific pair. `PlayerCacheWarmingService`'s own periodic progress
checkpoint now also includes a running technical-failure count, so a run
that gets cancelled mid-way still leaves a useful trail (the
`Information`-level summary line never runs if the process is killed
first).

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Widen the cache-warming timeout further instead of removing the retry | Trivial one-line change | Doesn't help — the failing pairs are structural (the 250k-row case), not slow-but-finishable; a longer timeout just wastes more time per failed attempt, twice, before giving up | Evidence (the JSON parse error at row 250,204) showed the query genuinely cannot finish, at any timeout, for these pairs |
| Keep the same-run retry, add cross-run persistence on top | Retry still recovers same-run transient blips | The retry is what doubled every failure's cost in the first place — keeping it means every failure (even ones that will be marked persistent after 2 runs) still costs 2x instead of 1x on the way there | Removing the retry and relying on the cross-run threshold (2 consecutive runs) already covers the transient case without the doubled per-run cost |
| Fold `PairLookupFailure` into `ConfirmedLowMatchPair` (e.g. a `IsTechnicalFailure` flag column) | One fewer table | Conflates two different kinds of fact (an objective fact about Wikidata's data vs. an operational fact about this run's query reliability) with different reset/invalidation triggers (`ConfirmedLowMatchPair` never expires on its own; `PairLookupFailure` resets the moment a real answer arrives) — would need extra columns and branching logic to keep the two meanings apart in one table anyway | Same "best fit for the actual data shape" reasoning ADR-0050 already used for choosing a new table over a column; a second new table is cheap and keeps each table's invariants simple |
| Rewrite every intersection query builder (all five) into a two-phase existence-then-hydrate protocol | Would harden every builder against the same class of blowup, not just club-club | Touches the shared query builder used by REQ-211's guess-time live lookup — a correctness-critical, synchronous-request path — for four builders that have never actually been observed to blow up in production; much larger blast radius and test-surface for unconfirmed benefit | The evidence names club-club specifically; `FILTER EXISTS` fixes the confirmed cause with a single-builder, single-request change and zero behavior change for the other four query shapes |
| Persist and skip after a single technical failure (threshold = 1) | Simpler, no counter needed | A one-off transient WDQS blip (a momentary 502) would permanently starve a pair that would have resolved fine on the very next run — turns "skip a doomed pair" into "skip any pair unlucky once" | 2 consecutive runs gives a real, independent second chance before treating a pair as structural, at the cost of one extra `int` column |

## Consequences

- Positive: `warm-player-cache.yml` converges — pairs that are genuinely
  undoomed by query shape (the large majority, per the FILTER EXISTS fix)
  succeed and get cached; pairs that keep failing get skipped after 2 runs
  instead of eating the full per-pair timeout on every run forever, so the
  90-minute budget is spent on pairs that can actually make progress.
- Positive: cache-warming's console output is readable again — a normal
  run's failures are visible as a single aggregate summary line instead of
  thousands of per-pair Warning/stack-trace lines.
- Positive: `PairsSkippedPersistentFailure` in `CacheWarmingResult` gives an
  operator a direct signal ("N pairs are structurally stuck, look at
  `FailingPairs` from a recent run to see which") distinct from
  `PairsSkippedConfirmedLow`'s "N pairs are fine, just below threshold."
- Negative / trade-offs accepted: a pair marked persistent-failure is not
  automatically re-checked once whatever caused it resolves (a WDQS outage
  ending, a future query-shape fix) — it stays skipped until
  `StaleClubAttributeCleaner`/`purge-player-pool` clears it, or an operator
  investigates directly. This is the same trade-off ADR-0050 already
  accepted for `ConfirmedLowMatchPair`; `PairLookupFailure` shares it
  rather than introducing a new invalidation shape.
- Negative / trade-offs accepted: a third invalidation call site
  (`StaleClubAttributeCleaner`, `purge-player-pool`) must now remember to
  clear `PairLookupFailure` alongside `ConfirmedLowMatchPair` whenever
  either extends further — same accepted risk ADR-0050 already named for
  itself, now shared by a second table.
- Not eligible for `infra/scripts/lib/game-data-tables.sh`'s prod/dev sync
  allowlist, same reasoning as `ConfirmedLowMatchPair` (ADR-0050): a
  derived, operational marker about this codebase's own process state, not
  an objective Wikidata fact — syncing it risks one environment's warming
  history suppressing a re-check the other environment's own state needs.
- Follow-up: if a future incident shows the `FILTER EXISTS` fix wasn't
  sufficient on its own (a new class of blowup, e.g. from the alias
  multiplication alone reaching pathological scale for some pair), revisit
  the "rewrite every builder into a two-phase protocol" alternative above
  with real evidence for the specific builder involved — don't
  preemptively widen this fix's scope without a confirmed case.

## Status note (2026-08-01, follow-up)

The very first `warm-player-cache` run after this ADR's `FILTER EXISTS` fix
(commit `b92d044`, itself `run_attempt: 2` — its own attempt 1 had already
failed/been retried) left 125 Club×Club pairs at
`ConsecutiveFailureCount >= PersistentFailureThreshold`, permanently skipped
per this ADR's own §Decision/2 design. Those 125 pairs collectively touch
all 32 seeded clubs — the entire pool — so `StaleClubAttributeCleaner`
(club-name-scoped, per this ADR's Decision/2 and Consequences sections) was
the wrong tool to clear them: passing all 32 names would delete
`PlayerAttribute`/`PlayerData` for the ~850 other pairs that are already
correctly cached, not just the 125 broken ones, purely because they share a
club with something broken.

Added a third invalidation path, `PairLookupFailureCleaner`
(`XGArcade.Data.Seeding`) / the `clear-pair-lookup-failures` CLI verb, which
reads `PairLookupFailures` directly for rows at/above the threshold and
deletes only those rows — no `PlayerAttribute`/`PlayerData`/
`ConfirmedLowMatchPair` touched, so it can't have the same collateral-purge
problem `StaleClubAttributeCleaner` has here by construction (it was never
club-scoped in the first place). `PersistentFailureThreshold` is duplicated
as a private literal in `PairLookupFailureCleaner` rather than shared,
commented with a cross-reference back to `PlayerCacheWarmingService`'s copy
— `XGArcade.Data` sits below `XGArcade.Games.XGGrid` in the project-reference
graph (boundary rule 1), so a real shared reference would invert that
direction. This is a currently-accepted, comment-guarded drift risk, not a
resolved one: no automated check pins the two constants together yet.

This does not reverse this ADR's own "same invalidation surface as
`ConfirmedLowMatchPair`" reasoning (Decision/2, Consequences) — `clear-pair-lookup-failures`
still only clears the failure *marker*, on the same "pair might resolve on
its own, needs a forced re-check" logic as the other two tools; it's a
narrower-grained way to trigger that re-check, not a different kind of
invalidation.

## Status note (2026-08-10, follow-up — `PairLookupFailure` gets a second reader)

A player reported REQ-211's guess-time live-lookup fallback timing out
"quite often" on guesses they expected to be straightforwardly incorrect.
Investigation found `GridGameModule.RefreshCellFromLiveLookupAsync` never
consulted `PairLookupFailure` at all — only `PlayerCacheWarmingService`
read it, per this ADR's original Decision. So a Country×Club or Club×Club
pair `PlayerCacheWarmingService` had already confirmed, independently and
in advance, as a persistent technical failure (`ConsecutiveFailureCount >=
PersistentFailureThreshold`) still paid the full guess-time-fallback
timeout (currently 28s, ADR-0046) on every single guess against it — the
guess-time path had no way to know cache-warming had already given up on
that exact pair.

Fixed by adding `IsPersistentTechnicalFailureAsync` as a second read call,
now also from `GridGameModule.RefreshCellFromLiveLookupAsync`, before it
calls `LookupLiveMatchesAsync`: if the pair is already a known persistent
failure, it throws `LiveLookupUnavailableException` immediately instead of
attempting (and waiting out) a live call already known to be doomed. This
does not change REQ-211/ADR-0046's correctness guarantee — the pair is
still reported as genuinely UNKNOWN, not "incorrect," and no REQ-210
attempt is consumed either way; it only removes a redundant ~28s wait for
a case the system already had a confident answer to. `GridGameModule`
reaches `PairLookupFailure` the same sanctioned way `PlayerCacheWarmingService`
already does — through `IPlayerStoreRepository.IsPersistentTechnicalFailureAsync`,
never a direct `DbContext` query — so this is a second consumer, not a new
access path.

`PlayerCacheWarmingService.PersistentFailureThreshold` was changed from
`private` to `internal` so `GridGameModule` (same project, `Games.XGGrid`)
can reference the identical value rather than duplicating it — unlike
`PairLookupFailureCleaner`'s copy (this ADR's 2026-08-01 status note),
which had to duplicate because `XGArcade.Data.Seeding` sits below
`Games.XGGrid` in the project-reference graph; no such inversion applies
here, both types already live in the same project.

Only ever helps Country×Club/Club×Club — `PlayerCacheWarmingService.WarmAsync`
does not iterate Trophy pairings (Country×Trophy, Club×Trophy, also served
by this same fallback per ADR-0018/S-031), so `PairLookupFailure` never has
rows for those; the new check is a guaranteed-false, effectively free read
for a Trophy-pairing guess, never a false skip.

## For AI agents

`PairLookupFailure` now has two readers: `PlayerCacheWarmingService`
(original, decides whether to skip a pair during a warming run) and
`GridGameModule.RefreshCellFromLiveLookupAsync` (2026-08-10, decides
whether to skip a guess-time live call). Both go through
`IPlayerStoreRepository.IsPersistentTechnicalFailureAsync` — do not add a
third reader that queries `DbContext.PairLookupFailures` directly. If you
change `PersistentFailureThreshold`'s value, both readers pick it up
automatically since `GridGameModule` references
`PlayerCacheWarmingService.PersistentFailureThreshold` directly, not a
duplicated copy — do not reintroduce a duplicate for this pair the way
`PairLookupFailureCleaner` had to for its own, different (cross-project)
reason.

Do not reintroduce a same-run retry in `PlayerCacheWarmingService` without
re-reading this ADR's evidence first — it was removed because it made a
structural failure's cost *worse*, not because retries are categorically
wrong. Do not simplify `BuildClubClubIntersectionQuery`'s `FILTER EXISTS`
blocks back to a plain join — see the 250,000-row incident this fixes. Do
not apply the same `FILTER EXISTS` restructuring to
`BuildCountryClubIntersectionQuery`/`BuildNationalTeamClubIntersectionQuery`/
`BuildTrophyClubIntersectionQuery` without first solving how their
qualifier fetch (`?clubStatement`'s `pq:P580`/`pq:P582`/`pq:P1350` OPTIONAL
block) would still bind — those three genuinely need it (ADR-0042/S-079),
club-club does not. The invalidation surface for `PairLookupFailure` is now
`StaleClubAttributeCleaner`/`purge-player-pool`/`clear-pair-lookup-failures`
(see the 2026-08-01 status note above) — do not add a fourth without also
updating this ADR, same "exactly one place each correction path's
force-a-re-check logic lives" reasoning ADR-0050 established for
`ConfirmedLowMatchPair`. Do not read or write `PairLookupFailure` through
anything other than `IPlayerStoreRepository`'s three dedicated methods from
`Games.XGGrid` — a direct `DbContext` query from `Games.XGGrid` would
violate boundary rule 1 the same way it would for
`PlayerAttribute`/`ConfirmedLowMatchPair`. `XGArcade.Data.Seeding`'s own
maintenance tools (`StaleClubAttributeCleaner`, `PairLookupFailureCleaner`)
are the established exception to that same rule, per ADR-0052/ADR-0050
precedent — they live in the same project as the entity and read/write the
`DbContext` directly.

**Follow-up (S-134, 2026-08-17):** every `warm-player-cache.yml`/
`warm-player-cache` workflow reference above was renamed to
`warm-grid-cache.yml`/`warm-grid-cache` in S-134 (workflow-naming audit).
This does not change the decision recorded above — the incident, the
fix, and the CLI verb it describes are all unchanged; only the
Actions-tab filename differs. See `docs/backlog.md` S-134 and
`docs/CHANGELOG.md`.
