using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Scoring;

// ADR-0100 Decision §2: a thin pass-through to the existing
// IGuessRepository/ILiveRoundContributionService calls LeaderboardService
// already made before this ADR — zero behavior change for "xg-grid"/
// "xg-path". GameKey is supplied by the composition root at registration
// time (never hardcoded here, same boundary reason as
// UniquenessScoringStrategy.GameKey/ClueEfficiencyScoringStrategy.GameKey,
// ADR-0003) — this type is registered TWICE, once per GameKey it serves,
// each instance carrying its own GameKey value, since both existing games
// share this one Guess-backed implementation.
//
// GetPerRoundTotalsByUserIdsAsync deliberately ignores the
// closedRounds/members parameters — it still delegates straight to
// IGuessRepository.GetPerRoundFinalPointsByUserIdsAsync(userIds, GameKey,
// cancellationToken, applyGuestEligibilityRules), which already does this
// more efficiently as a single DB-side join. This is a deliberate, accepted
// asymmetry (see ADR-0100's Alternatives table), not an oversight.
public class GuessRoundScoreSource(
    IGuessRepository guessRepository, ILiveRoundContributionService liveRoundContributionService) : IRoundScoreSource
{
    public required string GameKey { get; init; }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> GetPerRoundTotalsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Round> closedRounds,
        IReadOnlyCollection<User> members,
        CancellationToken cancellationToken = default,
        bool applyGuestEligibilityRules = true) =>
        guessRepository.GetPerRoundFinalPointsByUserIdsAsync(userIds, GameKey, cancellationToken, applyGuestEligibilityRules);

    public Task<IReadOnlyDictionary<Guid, int>> GetActiveRoundTotalsByUserIdAsync(
        Round activeRound, CancellationToken cancellationToken = default) =>
        liveRoundContributionService.GetContributionsByUserIdAsync(activeRound, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundAsync(
        Round round, CancellationToken cancellationToken = default) =>
        guessRepository.GetTotalFinalPointsByRoundIdAsync(round.Id, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundsAsync(
        IReadOnlyCollection<Round> rounds, CancellationToken cancellationToken = default) =>
        guessRepository.GetTotalFinalPointsByRoundIdsAsync(rounds.Select(r => r.Id).ToList(), cancellationToken);
}
