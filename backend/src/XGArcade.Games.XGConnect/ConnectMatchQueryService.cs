using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// See IConnectMatchQueryService's own doc comment for the "reuse, don't
// re-derive" list this class follows.
public class ConnectMatchQueryService(
    IConnectMatchRepository connectMatchRepository,
    IConnectMatchLifecycleService connectMatchLifecycleService,
    IPlayerRepository playerRepository,
    IUserRepository userRepository) : IConnectMatchQueryService
{
    public async Task<IReadOnlyList<ConnectMatchSummary>> GetMatchesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var matches = await connectMatchRepository.GetAllMatchesForUserAsync(userId, cancellationToken);

        // REQ-1411: the exact same per-slot terminal-state check the
        // notification indicator already applies — reused here (rather
        // than re-derived from ConnectMatch's raw fields a second time) so
        // "awaiting my move" can never quietly mean something different
        // between the two surfaces. GetMatchesAwaitingActionAsync's own
        // candidate set already excludes Resolved matches
        // (GetOpenMatchesForUserAsync's filter), so a Resolved match below
        // naturally resolves to AwaitingMyAction: false without a separate
        // branch here.
        var awaitingActionMatchIds = (await connectMatchLifecycleService
                .GetMatchesAwaitingActionAsync(userId, cancellationToken))
            .Select(m => m.Id)
            .ToHashSet();

        // SCREEN-15 "Identity gap" fix (mirrors Core.Social's
        // FriendEndpoints/ChallengeEndpoints): every distinct opponent id
        // across the whole page, resolved in one IUserRepository.
        // GetByIdsAsync call rather than one round-trip per row. Null
        // opponent ids (REQ-710 anonymization) are excluded before the
        // call — GetByIdsAsync only accepts non-null ids.
        var opponentUserIds = matches
            .Select(match => match.PlayerAUserId == userId ? match.PlayerBUserId : match.PlayerAUserId)
            .Where(opponentUserId => opponentUserId.HasValue)
            .Select(opponentUserId => opponentUserId!.Value);
        var opponentDisplayNamesById = await ResolveDisplayNamesAsync(opponentUserIds, cancellationToken);

        return matches
            .Select(match =>
            {
                var isPlayerA = match.PlayerAUserId == userId;
                var opponentUserId = isPlayerA ? match.PlayerBUserId : match.PlayerAUserId;

                return new ConnectMatchSummary(
                    match.Id,
                    opponentUserId,
                    ResolveOpponentDisplayName(opponentUserId, opponentDisplayNamesById),
                    match.Status,
                    match.CreatedAt,
                    match.StartedAt,
                    match.DeadlineUtc,
                    match.ResolvedAt,
                    TranslateOutcome(match.Outcome, isPlayerA),
                    awaitingActionMatchIds.Contains(match.Id));
            })
            .ToList();
    }

    public async Task<ConnectMatchDetailResult> GetMatchDetailAsync(
        Guid matchId, Guid userId, CancellationToken cancellationToken = default)
    {
        // Reused, not re-derived — the same found/not-found/not-a-
        // participant check every xG Connect write endpoint's own service
        // already applies (ConnectMatchAccessExtensions's own doc comment).
        var access = await connectMatchRepository.ResolveParticipantMatchAsync(matchId, userId, cancellationToken);
        if (access.Outcome == ConnectMatchAccessOutcome.MatchNotFound)
            return new ConnectMatchDetailResult(ConnectMatchDetailOutcome.MatchNotFound, null);
        if (access.Outcome == ConnectMatchAccessOutcome.NotAParticipant)
            return new ConnectMatchDetailResult(ConnectMatchDetailOutcome.NotAParticipant, null);
        var match = access.Match!;

        var isPlayerA = match.PlayerAUserId == userId;
        var opponentUserId = isPlayerA ? match.PlayerBUserId : match.PlayerAUserId;

        // SCREEN-15 "Identity gap" fix: single-id resolve, same
        // ResolveDisplayNamesAsync helper GetMatchesForUserAsync above
        // batches across a whole page — this is a single-match read, so
        // there's only ever one opponent id to resolve (or none, per REQ-710
        // anonymization).
        var opponentDisplayNamesById = await ResolveDisplayNamesAsync(
            opponentUserId.HasValue ? [opponentUserId.Value] : [], cancellationToken);
        var opponentDisplayName = ResolveOpponentDisplayName(opponentUserId, opponentDisplayNamesById);

        var myPick = await connectMatchRepository.GetTargetPickAsync(matchId, userId, cancellationToken);

        // REQ-1404: mutually invisible until the match has left
        // AwaitingTargetPicks (i.e. both picks are locked and the match is
        // Active or Resolved) — see ConnectMatchDetail.OpponentTargetPick's
        // own doc comment for why this checks Status rather than the
        // opponent's own IsLocked flag.
        var opponentPick = match.Status == ConnectMatchStatus.AwaitingTargetPicks
            ? null
            : await connectMatchRepository.GetTargetPickAsync(matchId, opponentUserId, cancellationToken);

        var myChainSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, userId, cancellationToken);

        // Fetched ONLY to derive OpponentTerminalState.Completed below via
        // ConnectChainStepExtensions.HasClosedChain — never returned to the
        // caller. See IConnectMatchQueryService's own doc comment for why.
        var opponentChainSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, opponentUserId, cancellationToken);

        var playerIdsToResolve = new HashSet<Guid>();
        if (myPick is not null)
            playerIdsToResolve.Add(myPick.TargetPlayerId);
        if (opponentPick is not null)
            playerIdsToResolve.Add(opponentPick.TargetPlayerId);
        foreach (var step in myChainSteps)
            playerIdsToResolve.Add(step.CandidatePlayerId);

        var players = await playerRepository.GetPlayersByIdsAsync(playerIdsToResolve, cancellationToken);

        var myTargetPickView = myPick is null
            ? null
            : new ConnectTargetPickView(myPick.TargetPlayerId, ResolvePlayerName(players, myPick.TargetPlayerId), myPick.IsLocked);
        var opponentTargetPickView = opponentPick is null
            ? null
            : new ConnectTargetPickView(opponentPick.TargetPlayerId, ResolvePlayerName(players, opponentPick.TargetPlayerId), opponentPick.IsLocked);

        var myChainStepViews = myChainSteps
            .OrderBy(s => s.Position)
            .ThenBy(s => s.AttemptNumber)
            .Select(s => new ConnectChainStepView(
                s.Id,
                s.Position,
                s.AttemptNumber,
                s.CandidatePlayerId,
                ResolvePlayerName(players, s.CandidatePlayerId),
                s.MatchedClubName,
                s.MatchedOverlapStartYear,
                s.MatchedOverlapEndYear,
                s.IsValid,
                s.ClosesChain,
                s.SubmittedAt))
            .ToList();

        var myBustedAt = isPlayerA ? match.PlayerABustedAt : match.PlayerBBustedAt;
        var myTimedOutAt = isPlayerA ? match.PlayerATimedOutAt : match.PlayerBTimedOutAt;
        var opponentBustedAt = isPlayerA ? match.PlayerBBustedAt : match.PlayerABustedAt;
        var opponentTimedOutAt = isPlayerA ? match.PlayerBTimedOutAt : match.PlayerATimedOutAt;

        var myTerminalState = new ConnectTerminalState(
            myBustedAt is not null, myTimedOutAt is not null, myChainSteps.HasClosedChain());
        var opponentTerminalState = new ConnectTerminalState(
            opponentBustedAt is not null, opponentTimedOutAt is not null, opponentChainSteps.HasClosedChain());

        var myScore = isPlayerA ? match.PlayerAScore : match.PlayerBScore;
        var opponentScore = isPlayerA ? match.PlayerBScore : match.PlayerAScore;

        var detail = new ConnectMatchDetail(
            match.Status,
            match.CreatedAt,
            match.StartedAt,
            match.DeadlineUtc,
            match.ResolvedAt,
            TranslateOutcome(match.Outcome, isPlayerA),
            opponentUserId,
            opponentDisplayName,
            myTargetPickView,
            opponentTargetPickView,
            myChainStepViews,
            myTerminalState,
            opponentTerminalState,
            myScore,
            opponentScore);

        return new ConnectMatchDetailResult(ConnectMatchDetailOutcome.Found, detail);
    }

    private static string ResolvePlayerName(IReadOnlyDictionary<Guid, Player> players, Guid playerId) =>
        players.TryGetValue(playerId, out var player) ? player.FullName : "Unknown player";

    // SCREEN-15 "Identity gap" fix: resolves every distinct user id's
    // DisplayName in one IUserRepository.GetByIdsAsync call — same
    // batch-then-map shape LeaderboardService (REQ-404) and
    // FriendEndpoints/ChallengeEndpoints (Core.Social) already established
    // for this exact repository method.
    private async Task<IReadOnlyDictionary<Guid, string>> ResolveDisplayNamesAsync(
        IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var distinctIds = userIds.Distinct().ToList();
        if (distinctIds.Count == 0)
            return new Dictionary<Guid, string>();

        var users = await userRepository.GetByIdsAsync(distinctIds, cancellationToken);
        return users.ToDictionary(u => u.Id, u => u.DisplayName);
    }

    // OpponentDisplayName must mirror OpponentUserId's own nullability
    // exactly — null whenever OpponentUserId is null (REQ-710
    // anonymization), never a placeholder for that case. A non-null
    // opponentUserId with no matching row is defensive-only (same "should
    // never actually happen" caveat as ResolvePlayerName's own "Unknown
    // player" fallback above — every ConnectMatch row is created between two
    // users that existed at creation time, and REQ-710 anonymizes the
    // UserId column itself rather than deleting the User row it once
    // pointed to).
    private static string? ResolveOpponentDisplayName(
        Guid? opponentUserId, IReadOnlyDictionary<Guid, string> opponentDisplayNamesById) =>
        opponentUserId is null
            ? null
            : opponentDisplayNamesById.TryGetValue(opponentUserId.Value, out var displayName) ? displayName : "Unknown player";

    // REQ-1409: the single place this match/perspective translation is
    // computed — see ConnectMatchPerspectiveOutcome's own doc comment.
    private static ConnectMatchPerspectiveOutcome TranslateOutcome(ConnectMatchOutcome outcome, bool isPlayerA) => outcome switch
    {
        ConnectMatchOutcome.Pending => ConnectMatchPerspectiveOutcome.Pending,
        ConnectMatchOutcome.Draw => ConnectMatchPerspectiveOutcome.Draw,
        ConnectMatchOutcome.PlayerAWin => isPlayerA ? ConnectMatchPerspectiveOutcome.Win : ConnectMatchPerspectiveOutcome.Loss,
        ConnectMatchOutcome.PlayerBWin => isPlayerA ? ConnectMatchPerspectiveOutcome.Loss : ConnectMatchPerspectiveOutcome.Win,
        _ => throw new InvalidOperationException($"Unhandled ConnectMatchOutcome '{outcome}'."),
    };
}
