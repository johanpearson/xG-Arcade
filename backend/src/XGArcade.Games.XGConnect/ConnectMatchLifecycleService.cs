using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// REQ-1405/S-212, REQ-1407/1408/1409/S-214: see
// IConnectMatchLifecycleService's own doc comment.
public class ConnectMatchLifecycleService(
    IConnectMatchRepository connectMatchRepository,
    IConnectScoringService connectScoringService,
    TimeProvider timeProvider) : IConnectMatchLifecycleService
{
    public async Task StartMatchIfBothPicksLockedAsync(Guid matchId, CancellationToken cancellationToken = default)
    {
        // Re-confirms state itself rather than trusting the caller blindly
        // (see this method's own doc comment on the interface) — the only
        // way both target picks are ever locked is
        // IConnectMatchRepository.LockTargetPicksForMatchAsync's own
        // whole-match-scoped write, so "exactly two rows, both IsLocked" is
        // the correct, sufficient re-check.
        var picks = await connectMatchRepository.GetTargetPicksForMatchAsync(matchId, cancellationToken);
        if (picks.Count != 2 || !picks.All(p => p.IsLocked))
            return;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await connectMatchRepository.StartMatchAsync(matchId, now, now + TimeSpan.FromHours(6), cancellationToken);
    }

    public async Task<ForfeitSweepResult> RunForfeitSweepAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var matches = await connectMatchRepository.GetActiveMatchesPastDeadlineAsync(now, cancellationToken);

        var playersForfeited = 0;
        var matchesResolved = 0;

        foreach (var match in matches)
        {
            // REQ-1407/1408/S-214: a slot can already be terminal via a
            // bust or a completed chain while the match is still Active
            // (because the OTHER player hasn't reached terminal yet) — such
            // a slot must NOT be marked timed-out just because the shared
            // deadline has passed. `match` came back AsNoTracking from
            // GetActiveMatchesPastDeadlineAsync, so PlayerATimedOutAt/
            // PlayerBTimedOutAt/PlayerABustedAt/PlayerBBustedAt reflect
            // state as of before this sweep pass; chain completion isn't a
            // column on this entity at all, so it's checked here the same
            // way TryResolveMatchIfBothTerminalAsync checks it below — a
            // ClosesChain=true ConnectChainStep row for that slot's UserId.
            if (match.PlayerATimedOutAt is null && match.PlayerABustedAt is null)
            {
                var playerASteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, match.PlayerAUserId, cancellationToken);
                if (!playerASteps.Any(s => s.IsValid && s.ClosesChain))
                {
                    await connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, now, cancellationToken);
                    playersForfeited++;
                }
            }

            if (match.PlayerBTimedOutAt is null && match.PlayerBBustedAt is null)
            {
                var playerBSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, match.PlayerBUserId, cancellationToken);
                if (!playerBSteps.Any(s => s.IsValid && s.ClosesChain))
                {
                    await connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: false, now, cancellationToken);
                    playersForfeited++;
                }
            }

            // REQ-1405/1409: resolve immediately in this same sweep call,
            // never deferred to a later pass — TryResolveMatchIfBothTerminalAsync
            // re-reads the match's full terminal state itself (timeout,
            // bust, AND chain completion, whichever mix applies) rather
            // than this loop trying to track it locally, which is what
            // makes a mixed outcome (one timed out, one already
            // busted/completed) resolve correctly here too, not just the
            // both-timed-out case this sweep used to special-case inline.
            if (await TryResolveMatchIfBothTerminalAsync(match.Id, cancellationToken))
                matchesResolved++;
        }

        return new ForfeitSweepResult(playersForfeited, matchesResolved);
    }

    public async Task<bool> TryResolveMatchIfBothTerminalAsync(Guid matchId, CancellationToken cancellationToken = default)
    {
        var match = await connectMatchRepository.GetMatchByIdAsync(matchId, cancellationToken);
        if (match is null || match.Status == ConnectMatchStatus.Resolved)
            return false;

        // REQ-1408: chain completion is detected the same way
        // ConnectChainStepService itself detects it — a ClosesChain=true
        // ConnectChainStep row for that slot's UserId. Queried by slot
        // (match.PlayerAUserId/PlayerBUserId) rather than by an
        // independently-supplied userId, consistent with every other
        // slot-based read/write in this class.
        var playerASteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, match.PlayerAUserId, cancellationToken);
        var playerBSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, match.PlayerBUserId, cancellationToken);

        var playerACompleted = playerASteps.Any(s => s.IsValid && s.ClosesChain);
        var playerBCompleted = playerBSteps.Any(s => s.IsValid && s.ClosesChain);

        var playerAForfeited = match.PlayerATimedOutAt is not null || match.PlayerABustedAt is not null;
        var playerBForfeited = match.PlayerBTimedOutAt is not null || match.PlayerBBustedAt is not null;

        var playerATerminal = playerACompleted || playerAForfeited;
        var playerBTerminal = playerBCompleted || playerBForfeited;

        // REQ-1405's "not resolved until both players have reached a
        // terminal state" rule — the still-active player keeps playing
        // normally, unaffected by the other's already-reached terminal
        // state.
        if (!playerATerminal || !playerBTerminal)
            return false;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        ConnectMatchOutcome outcome;
        int? playerAScore = null;
        int? playerBScore = null;

        if (playerACompleted && playerBCompleted)
        {
            // REQ-1409: both completed a valid chain — strictly lower score
            // wins, equal scores draw.
            playerAScore = connectScoringService.CalculateScore(playerASteps);
            playerBScore = connectScoringService.CalculateScore(playerBSteps);
            outcome = playerAScore < playerBScore
                ? ConnectMatchOutcome.PlayerAWin
                : playerBScore < playerAScore
                    ? ConnectMatchOutcome.PlayerBWin
                    : ConnectMatchOutcome.Draw;
        }
        else if (playerACompleted)
        {
            // REQ-1409: A completed, B forfeited (playerBTerminal is true
            // and playerBCompleted is false, so B must be forfeited) — A
            // wins outright, no minimum score required; B has no score.
            playerAScore = connectScoringService.CalculateScore(playerASteps);
            outcome = ConnectMatchOutcome.PlayerAWin;
        }
        else if (playerBCompleted)
        {
            playerBScore = connectScoringService.CalculateScore(playerBSteps);
            outcome = ConnectMatchOutcome.PlayerBWin;
        }
        else
        {
            // REQ-1409: both forfeited (any mix of bust/timeout) — draw,
            // neither gets a score.
            outcome = ConnectMatchOutcome.Draw;
        }

        await connectMatchRepository.ResolveMatchAsync(matchId, outcome, now, playerAScore, playerBScore, cancellationToken);
        return true;
    }
}
