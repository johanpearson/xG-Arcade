using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Core.Social's (COMP-16) own persistence for REQ-1403 — the only path
// Core.Social reaches MatchmakingOptIn through. Same pure-persistence-
// primitives scope as IFriendRepository/IChallengeRepository: no pairing
// logic, no expiry sweep. Those are S-210's business logic. See ADR-0103.
public interface IMatchmakingOptInRepository
{
    Task<MatchmakingOptIn> AddOptInAsync(MatchmakingOptIn optIn, CancellationToken cancellationToken = default);

    Task<MatchmakingOptIn?> GetOptInByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // Plain status filter — the future sweep job's pairing-candidate pool.
    // Not itself the pairing/expiry logic.
    Task<IReadOnlyList<MatchmakingOptIn>> GetWaitingOptInsAsync(CancellationToken cancellationToken = default);

    // Load-then-save. resultingMatchId mirrors
    // IChallengeRepository.UpdateChallengeStatusAsync's own optional-fold-in
    // shape, for the Paired transition.
    Task UpdateOptInStatusAsync(
        Guid optInId, MatchmakingOptInStatus status, Guid? resultingMatchId = null,
        CancellationToken cancellationToken = default);
}
