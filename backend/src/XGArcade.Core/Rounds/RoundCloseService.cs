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
        if (round.EndTime > closedAt)
        {
            round.EndTime = closedAt;
            await roundRepository.UpdateAsync(round, cancellationToken);
        }

        // REQ-205: Core.Rounds triggers closing at the right moment, but
        // Core.Scoring (COMP-04) owns the actual score-locking logic — see
        // IScoreLockingService's own doc comment.
        await scoreLockingService.LockRoundScoresAsync(roundId, cancellationToken);

        return round;
    }
}
