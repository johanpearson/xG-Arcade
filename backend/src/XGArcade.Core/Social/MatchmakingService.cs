using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Social;

// COMP-16 (Core.Social)/ADR-0103, S-210: REQ-1403's opt-in creation. See
// IMatchmakingService's own doc comment for why this is the only method —
// pairing lives in XGArcade.Api.Social.MatchmakingSweepService instead.
public class MatchmakingService(
    IMatchmakingOptInRepository matchmakingOptInRepository,
    TimeProvider timeProvider) : IMatchmakingService
{
    // REQ-1403: "a 12-hour pairing window" — fixed at opt-in time, not
    // recomputed later, so a sweep run's own clock reading never silently
    // extends or shrinks an already-created opt-in's deadline.
    private static readonly TimeSpan PairingWindow = TimeSpan.FromHours(12);

    public Task<MatchmakingOptIn> OptInAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var optIn = new MatchmakingOptIn
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OptedInAt = now,
            ExpiresAt = now + PairingWindow,
            Status = MatchmakingOptInStatus.Waiting,
        };

        return matchmakingOptInRepository.AddOptInAsync(optIn, cancellationToken);
    }
}
