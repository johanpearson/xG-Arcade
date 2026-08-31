using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGPredict;

// ADR-0100 Decision §2/§3/§4: the "xg-predict" implementation of
// Core.Scoring's IRoundScoreSource — wraps IPredictInstanceRepository only.
// Registered once at the composition root, against
// XGPredictGameModule.XGPredictGameKey.
//
// Per ADR-0100's "For AI agents" section this must NEVER inject
// IRoundRepository or IUserRepository — every Round/User this class needs
// is handed in by the caller (LeaderboardService, via the already-resolved
// IRoundScoreSource). Do not add either dependency here even if it looks
// more convenient; that inverts the established Core-resolves/game-module-
// reads direction ScoreLockingService.MaterializeUnansweredCellsAsync
// already established for round-close.
public class PredictRoundScoreSource(IPredictInstanceRepository predictInstanceRepository) : IRoundScoreSource
{
    // ADR-0100 §3: per closed "xg-predict" round, pair participation
    // (GetParticipantUserIdsByInstanceIdAsync — "did this user predict at
    // all") with graded totals (GetTotalPointsByInstanceIdAsync — "how many
    // points have they earned so far", defaulting to 0 for a participant
    // with nothing graded yet). A round with zero participants contributes
    // nothing to anyone. REQ-717/ADR-0036 eligibility (members' IsGuest/
    // ClaimedAt vs. round.ClosedAt) is applied the same way
    // IGuessRepository's own query already does it, just in memory here
    // instead of in SQL.
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> GetPerRoundTotalsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Round> closedRounds,
        IReadOnlyCollection<User> members,
        CancellationToken cancellationToken = default,
        bool applyGuestEligibilityRules = true)
    {
        var userIdSet = userIds.ToHashSet();
        var membersById = members.ToDictionary(m => m.Id);
        var totalsByUserId = new Dictionary<Guid, List<int>>();

        foreach (var round in closedRounds.Where(r => r.GameKey == XGPredictGameModule.XGPredictGameKey))
        {
            var participantUserIds = await predictInstanceRepository.GetParticipantUserIdsByInstanceIdAsync(
                round.GameInstanceId, cancellationToken);
            if (participantUserIds.Count == 0)
                continue;

            var gradedTotalsByUserId = await predictInstanceRepository.GetTotalPointsByInstanceIdAsync(
                round.GameInstanceId, cancellationToken);

            foreach (var userId in participantUserIds)
            {
                if (!userIdSet.Contains(userId))
                    continue;

                if (applyGuestEligibilityRules)
                {
                    if (!membersById.TryGetValue(userId, out var member))
                        continue;
                    if (member.IsGuest)
                        continue;
                    if (member.ClaimedAt is DateTime claimedAt && round.ClosedAt <= claimedAt)
                        continue;
                }

                if (!totalsByUserId.TryGetValue(userId, out var perRoundTotals))
                {
                    perRoundTotals = [];
                    totalsByUserId[userId] = perRoundTotals;
                }

                perRoundTotals.Add(gradedTotalsByUserId.GetValueOrDefault(userId, 0));
            }
        }

        return totalsByUserId.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
    }

    // ADR-0100 §4: the exact same graded-so-far read GetTotalsByRoundAsync
    // below uses — no separate "live, in-progress" formula. A
    // PredictMatchPrediction has no equivalent of a Grid/Path guess's
    // in-progress, unresolved-attempt state (see that ADR section's full
    // reasoning); whatever is graded so far is the whole live total.
    public Task<IReadOnlyDictionary<Guid, int>> GetActiveRoundTotalsByUserIdAsync(
        Round activeRound, CancellationToken cancellationToken = default) =>
        predictInstanceRepository.GetTotalPointsByInstanceIdAsync(activeRound.GameInstanceId, cancellationToken);

    // REQ-408: one closed round's graded-points total per user — absent
    // key means "no graded points yet" (same "absent, not defaulted"
    // convention as every other scope), never a synthesized 0 row.
    public Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundAsync(
        Round round, CancellationToken cancellationToken = default) =>
        predictInstanceRepository.GetTotalPointsByInstanceIdAsync(round.GameInstanceId, cancellationToken);

    // REQ-405: the same graded-points read as GetTotalsByRoundAsync above,
    // summed across every round in the window — mirrors
    // IGuessRepository.GetTotalFinalPointsByRoundIdsAsync's own "treat a
    // round with nothing to contribute as 0, sum the rest" shape, just
    // computed in memory (ADR-0100's accepted N+1-shaped trade-off; see
    // that ADR's Consequences section) rather than one joined query.
    public async Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundsAsync(
        IReadOnlyCollection<Round> rounds, CancellationToken cancellationToken = default)
    {
        var totalsByUserId = new Dictionary<Guid, int>();

        foreach (var round in rounds)
        {
            var roundTotalsByUserId = await predictInstanceRepository.GetTotalPointsByInstanceIdAsync(
                round.GameInstanceId, cancellationToken);
            foreach (var (userId, points) in roundTotalsByUserId)
                totalsByUserId[userId] = totalsByUserId.GetValueOrDefault(userId, 0) + points;
        }

        return totalsByUserId;
    }
}
