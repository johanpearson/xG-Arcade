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
        // than whatever was already scheduled.
        var needsUpdate = false;
        if (round.EndTime > closedAt)
        {
            round.EndTime = closedAt;
            needsUpdate = true;
        }

        // REQ-408: first close wins, same idempotent pattern as the EndTime
        // pull-forward above — a second CloseRoundAsync call (accepted risk,
        // see MaterializeUnansweredCellsAsync's own doc comment) must never
        // overwrite the original ClosedAt.
        if (round.ClosedAt is null)
        {
            round.ClosedAt = closedAt;
            needsUpdate = true;
        }

        if (needsUpdate)
        {
            await roundRepository.UpdateAsync(round, cancellationToken);
        }

        // REQ-205: Core.Rounds triggers closing at the right moment, but
        // Core.Scoring (COMP-04) owns the actual score-locking logic — see
        // IScoreLockingService's own doc comment.
        await scoreLockingService.LockRoundScoresAsync(roundId, cancellationToken);

        return round;
    }
}
