using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGHigherLower;

// ADR-0100 Decision §2/§3/§4/REQ-1505/S-226: the "xg-higher-lower"
// implementation of Core.Scoring's IRoundScoreSource — wraps
// IHigherLowerInstanceRepository only. Registered once at the composition
// root, against XGHigherLowerGameModule.XGHigherLowerGameKey.
//
// Per ADR-0100's "For AI agents" section this must NEVER inject
// IRoundRepository or IUserRepository — every Round/User this class needs
// is handed in by the caller (LeaderboardService, via the already-resolved
// IRoundScoreSource). Do not add either dependency here even if it looks
// more convenient.
//
// Mirrors PredictRoundScoreSource's exact shape, with one real difference:
// PredictMatchPrediction.FinalPoints is null until a separate grading job
// runs (ADR-0100 §4's "graded so far" case), so predict's methods default
// an ungraded prediction to 0 points. HigherLowerAttempt.StreakLength has
// no such gap — REQ-1504's own semantics make it always a real, current
// value the instant an attempt row exists at all, whether that attempt has
// ended or is still in progress (see HigherLowerAttempt's own doc comment).
// So there is nothing here equivalent to predict's "not yet graded"
// filtering — every attempt row's StreakLength directly IS both the
// closed-round total (once the round has closed) and the live in-progress
// total (while it's still open), the same "no separate live formula
// needed" reasoning ADR-0100 §4 gives for predict, just for a different
// underlying reason (never-null vs. always-current-not-just-graded).
public class HigherLowerRoundScoreSource(IHigherLowerInstanceRepository higherLowerInstanceRepository) : IRoundScoreSource
{
    // ADR-0100 §3: per closed "xg-higher-lower" round, pair participation
    // (GetParticipantUserIdsByInstanceIdAsync — "did this user ever submit
    // a guess at all") with current totals
    // (GetStreakLengthsByInstanceIdAsync — every participant's
    // StreakLength, defaulting to 0 for a participant somehow absent from
    // the streak-length map, which should never happen in practice since a
    // participant row always carries a StreakLength — kept only for the
    // same defensive shape predict's own GetValueOrDefault uses). A round
    // with zero participants contributes nothing to anyone. REQ-717/
    // ADR-0036 eligibility (members' IsGuest/ClaimedAt vs. round.ClosedAt)
    // is applied the same way IGuessRepository's own query already does
    // it, just in memory here instead of in SQL — copied verbatim from
    // PredictRoundScoreSource, which is GameKey-agnostic.
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

        foreach (var round in closedRounds.Where(r => r.GameKey == XGHigherLowerGameModule.XGHigherLowerGameKey))
        {
            var participantUserIds = await higherLowerInstanceRepository.GetParticipantUserIdsByInstanceIdAsync(
                round.GameInstanceId, cancellationToken);
            if (participantUserIds.Count == 0)
                continue;

            var streakLengthsByUserId = await higherLowerInstanceRepository.GetStreakLengthsByInstanceIdAsync(
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

                perRoundTotals.Add(streakLengthsByUserId.GetValueOrDefault(userId, 0));
            }
        }

        return totalsByUserId.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
    }

    // ADR-0100 §4: no separate "live, in-progress" formula — see this
    // class's own doc comment above for why StreakLength is always current
    // regardless of HasEnded.
    public Task<IReadOnlyDictionary<Guid, int>> GetActiveRoundTotalsByUserIdAsync(
        Round activeRound, CancellationToken cancellationToken = default) =>
        higherLowerInstanceRepository.GetStreakLengthsByInstanceIdAsync(activeRound.GameInstanceId, cancellationToken);

    // REQ-408: one closed round's totals per user — absent key means "never
    // submitted a guess this round," never a synthesized 0 row, same
    // "absent, not defaulted" convention as every other scope.
    public Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundAsync(
        Round round, CancellationToken cancellationToken = default) =>
        higherLowerInstanceRepository.GetStreakLengthsByInstanceIdAsync(round.GameInstanceId, cancellationToken);

    // REQ-405: the same per-round read as GetTotalsByRoundAsync above,
    // summed across every round in the window — mirrors
    // PredictRoundScoreSource.GetTotalsByRoundsAsync's own shape, computed
    // in memory (ADR-0100's accepted N+1-shaped trade-off) rather than one
    // joined query.
    public async Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundsAsync(
        IReadOnlyCollection<Round> rounds, CancellationToken cancellationToken = default)
    {
        var totalsByUserId = new Dictionary<Guid, int>();

        foreach (var round in rounds)
        {
            var roundTotalsByUserId = await higherLowerInstanceRepository.GetStreakLengthsByInstanceIdAsync(
                round.GameInstanceId, cancellationToken);
            foreach (var (userId, streakLength) in roundTotalsByUserId)
                totalsByUserId[userId] = totalsByUserId.GetValueOrDefault(userId, 0) + streakLength;
        }

        return totalsByUserId;
    }

    // REQ-305: reuses the same participation read
    // GetPerRoundTotalsByUserIdsAsync above already calls — a non-empty
    // result means at least one HigherLowerAttempt row exists for this
    // instance, i.e. someone actually played. "xg-higher-lower" never
    // writes Guess rows (see this class's own doc comment above), so this
    // is the only correct way to answer this question for this GameKey.
    public async Task<bool> HasAnyParticipantAsync(Round round, CancellationToken cancellationToken = default) =>
        (await higherLowerInstanceRepository.GetParticipantUserIdsByInstanceIdAsync(round.GameInstanceId, cancellationToken)).Count > 0;
}
