# ADR-0022: Round closing runs inside the round-generation scheduled job

- **Status:** Accepted
- **Date:** 2026-07-12
- **Related requirements:** REQ-205, REQ-206, REQ-401, REQ-404
- **Related components:** COMP-03 (Core.Rounds), COMP-04 (Core.Scoring)

## Context

REQ-205's score locking (`ScoreLockingService.LockRoundScoresAsync`, via
`RoundCloseService.CloseRoundAsync`) has existed since S-011, but nothing in
any real (deployed) environment ever called it. The only caller was
`POST /internal/test-data/force-close-round/{roundId}`
(`InternalRoundEndpoints`) — deliberately gated to non-Production only and
never invoked automatically. `generate-round.yml`'s cron (REQ-301) creates a
new `Round` every time it finds one already started, but never closed the
one it superseded.

The practical effect, found via direct play-testing: a player completes a
grid, correctly guesses every cell, and never sees a total on the
leaderboard — `Guess.FinalPoints` stays null forever for every round ever
played in the deployed dev environment, so `SUM(FinalPoints)` is always 0
for everyone. This is a real Tier 0 gap, not a Tier 1 feature — REQ-205/206
are both already in scope (`MVP-SCOPE.md`: "REQ-201–206 ... this is the
actual game, not overhead"). It was simply never wired up.

## Decision

`RoundGenerationService.GenerateNextRoundIfNeededAsync` — already the one
piece of code invoked by Tier 0's only production-scheduled trigger point
(`generate-round.yml`'s cron via `POST /internal/generate-round`) — now
also closes the round it is about to supersede, via the existing
`IRoundCloseService`, before deciding whether to generate a new one.

The round to close is never "latest" itself: REQ-301's "one round ahead"
design means a round stops being `latest` (superseded by its successor)
long before it actually ends — the successor is deliberately generated a
full cycle early so gameplay never has a gap. So the round that needs
closing, at any given job invocation, is `latest`'s *predecessor* — the
round whose `EndTime` equals `latest.StartTime` by construction. A new
`IRoundRepository.GetPreviousByGameKeyAsync` finds it directly, without
assuming exact timestamp equality (it just orders by `StartTime`
descending, filtered to before `latest.StartTime`). It's closed only if its
own `EndTime` has actually passed, and only when `latest` has actually
started — never early, and never a round the job hasn't reached yet.

No schema change: no `Round.IsClosed`/`ClosedAt` column was added.
`CloseRoundAsync`/`LockRoundScoresAsync` were already documented as
idempotent for sequential calls, so a repeat close on an
already-closed predecessor (which shouldn't normally happen, given the job
only ever looks one round back) is harmless, not something that needs
guarding against with new persisted state.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| A dedicated `close-round.yml` cron + `/internal/close-round` endpoint | Symmetrical with generate-round.yml; single responsibility per job | A second GitHub Actions secret/workflow to keep in sync with the same cadence/duration coupling `RoundSchedulingOptions` already documents for generation; doubles the surface for the exact misconfiguration risk that doc comment already warns about | Not chosen: no functional benefit over reusing the job that's already correctly scheduled, and it multiplies the "keep cadence and RoundDuration coupled" footgun instead of containing it to one job |
| Add `Round.ClosedAt`, close *every* not-yet-closed ended round on every tick | Self-heals any historical backlog of never-closed rounds instantly; explicit, queryable "closed" state useful for a future past-round screen (REQ-206's other known gap) | Requires a hand-written EF Core migration with no `dotnet` SDK available in this environment to generate/verify it; adds persisted state purely to avoid one edge case | Not chosen for this pass — flagged as a real follow-up (see below), not attempted without being able to verify the migration |
| Infer "already closed" from whether any `Guess` in the round has `FinalPoints` set | No schema change | Ambiguous for a round nobody ever played (zero `Guess` rows even after "closing") — would look permanently unclosed and get needlessly reprocessed | Not chosen: unreliable signal, not actually cheaper than the chosen approach |

## Consequences

- Positive: a player's final per-round score and the leaderboard's all-time
  total now actually populate in the deployed dev environment, with no new
  scheduled job, secret, or workflow to add.
- Negative / trade-offs accepted: closes only the single immediate
  predecessor of `latest` per invocation. If several rounds had already
  ended-but-never-closed *before* this fix shipped (a real backlog, since
  the gap existed since S-008/S-011), each needs one additional
  `generate-round.yml` cron cycle to catch up — or can be closed
  immediately by hand via the existing non-Production
  `POST /internal/test-data/force-close-round/{roundId}` endpoint. Given
  `generate-round.yml` only fires twice a week and this app is newly
  deployed, this backlog is expected to be small (0-2 rounds).
- Negative / trade-offs accepted: the pre-existing, documented concurrent-
  call race in `ScoreLockingService.MaterializeUnansweredCellsAsync` (two
  simultaneous `LockRoundScoresAsync` calls for the same round could race on
  the `(RoundId, UserId, CellId)` unique index) is now reachable from a real
  scheduled path, not just the manual test-data endpoint — still not fixed,
  since both callers are low-cadence and unlikely to genuinely overlap at
  Tier 0 scale; the code comment was updated to record this rather than
  silently continue describing a caller that no longer matches reality.
- Follow-up: if a past-round-detail screen is ever built (REQ-206's other
  known gap — "nowhere to view one round's total distinctly from the
  leaderboard's running total"), revisit adding an explicit
  `Round.ClosedAt` column then, when a real `dotnet` environment is
  available to generate and verify the migration — it would also let this
  job self-heal an arbitrary backlog in one tick instead of one round per
  cycle.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
