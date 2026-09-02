using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Core.Social's (COMP-16) own persistence for REQ-1402 — the only path
// Core.Social reaches Challenge through. Same pure-persistence-primitives
// scope as IFriendRepository: no accept/decline validation, no
// existing-friendship precondition check. Those are S-210's business
// logic. See ADR-0103.
public interface IChallengeRepository
{
    Task<Challenge> AddChallengeAsync(Challenge challenge, CancellationToken cancellationToken = default);

    Task<Challenge?> GetChallengeByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Challenge>> GetPendingChallengesForUserAsync(
        Guid challengedUserId, CancellationToken cancellationToken = default);

    // Load-then-save. resultingMatchId is optional so this one method can
    // serve both a plain accept/decline transition and, on acceptance, the
    // same call folding in the newly-created ConnectMatch's opaque id
    // (Challenge.ResultingMatchId — see that property's own doc comment for
    // why it carries no FK) — S-210 decides which callers pass it.
    Task UpdateChallengeStatusAsync(
        Guid challengeId, ChallengeStatus status, DateTime resolvedAt, Guid? resultingMatchId = null,
        CancellationToken cancellationToken = default);
}
