using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Rounds;

public class RoundCloseService(IRoundRepository roundRepository, IScoreLockingService scoreLockingService) : IRoundCloseService
{
    public async Task<Round?> CloseRoundAsync(Guid roundId, DateTime closedAt, CancellationToken cancellationToken = default)
    {
        var round = await roundRepository.GetByIdAsync(roundId, cancellationToken);
        if (round is null)
            return null;

        // Idempotent: only ever pulls EndTime earlier, never pushes it later
        // than whatever was already scheduled. Safe to persist ahead of
        // locking below — LockRoundScoresAsync doesn't read EndTime, and
        // ClosedAt (the field the leaderboard actually gates "closed" on)
        // isn't touched by this save.
        if (round.EndTime > closedAt)
        {
            round.EndTime = closedAt;
            await roundRepository.UpdateAsync(round, cancellationToken);

            // REQ-505 bug fix (2026-08-31): this branch only runs when
            // EndTime is being pulled forward of its originally-scheduled
            // value — i.e. exactly the admin's "end round now" early-close
            // path (REQ-806's force-close endpoint hits it too), never
            // RoundGenerationService's own predecessor-close call (that one
            // always passes a closedAt >= the round's already-past EndTime,
            // so this guard is false there). REQ-301's "one round ahead"
            // scheduling may already have chained a successor Round onto
            // this round's now-superseded EndTime (RoundGenerationService:
            // `startTime = latest?.EndTime ?? now`) before this early close
            // happened. Left alone, that successor sits orphaned at a stale
            // future StartTime with no relation to when this round actually
            // ended — defeating REQ-505's whole point of not having to wait
            // for real time to pass. Reschedule it to start now instead,
            // preserving its own configured duration.
            var latest = await roundRepository.GetLatestByGameKeyAsync(round.GameKey, cancellationToken);
            if (latest is not null && latest.Id != round.Id && latest.StartTime > closedAt)
            {
                var successor = await roundRepository.GetByIdAsync(latest.Id, cancellationToken)
                    ?? throw new InvalidOperationException($"Round '{latest.Id}' was found moments ago and no longer exists.");
                var duration = successor.EndTime - successor.StartTime;
                successor.StartTime = closedAt;
                successor.EndTime = closedAt + duration;
                await roundRepository.UpdateAsync(successor, cancellationToken);
            }
        }

        // REQ-205: Core.Rounds triggers closing at the right moment, but
        // Core.Scoring (COMP-04) owns the actual score-locking logic — see
        // IScoreLockingService's own doc comment.
        //
        // Must run to completion BEFORE ClosedAt is set/persisted below.
        // LeaderboardService.GetClosedRoundLeaderboardAsync gates purely on
        // ClosedAt being non-null to treat a round as browsable/final — if
        // ClosedAt were persisted first (or concurrently), a reader landing
        // between the two calls, or a LockRoundScoresAsync that throws
        // partway through its per-guess loop, could see a round marked
        // closed/complete while some guesses still have FinalPoints == null,
        // making totals look silently final when they're not. If this
        // throws, it propagates out of CloseRoundAsync and ClosedAt is never
        // set — a retry will resume/redo locking (LockRoundScoresAsync is
        // itself idempotent, see its own doc comment) and only then close.
        await scoreLockingService.LockRoundScoresAsync(roundId, cancellationToken);

        // REQ-408: first close wins, same idempotent pattern as the EndTime
        // pull-forward above — a second CloseRoundAsync call (accepted risk,
        // see MaterializeUnansweredCellsAsync's own doc comment) must never
        // overwrite the original ClosedAt.
        if (round.ClosedAt is null)
        {
            round.ClosedAt = closedAt;
            await roundRepository.UpdateAsync(round, cancellationToken);
        }

        return round;
    }

    // REQ-505 (2026-08-31 addition): the companion to the reschedule logic
    // above, exposed directly for when there's no active round left to
    // close at all (e.g. one was already ended, and REQ-301's
    // already-provisioned successor is the only round left for this
    // GameKey) — the admin's "start upcoming round now" action.
    // Deliberately independent of CloseRoundAsync's own inline reschedule
    // (not a shared private helper): CloseRoundAsync's chain-repair only
    // ever applies to a round it can prove is the successor of the one
    // just closed (`latest.Id != round.Id`), while this method has no such
    // "round just closed" to exclude — it always targets the latest round
    // for this GameKey, whatever that is.
    public async Task<Round?> StartUpcomingRoundNowAsync(string gameKey, DateTime now, CancellationToken cancellationToken = default)
    {
        var latest = await roundRepository.GetLatestByGameKeyAsync(gameKey, cancellationToken);
        if (latest is null || latest.StartTime <= now)
            return null;

        var round = await roundRepository.GetByIdAsync(latest.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Round '{latest.Id}' was found moments ago and no longer exists.");
        var duration = round.EndTime - round.StartTime;
        round.StartTime = now;
        round.EndTime = now + duration;
        await roundRepository.UpdateAsync(round, cancellationToken);

        return round;
    }
}
