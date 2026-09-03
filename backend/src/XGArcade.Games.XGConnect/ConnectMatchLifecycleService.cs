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
            // column on this entity at all, so it's checked here via
            // ConnectChainStepExtensions.HasClosedChain, the same check
            // ResolveIfBothTerminalAsync below uses.
            //
            // Both slots' chain steps are fetched exactly once each, right
            // here, and threaded straight into ResolveIfBothTerminalAsync
            // below — that method used to re-fetch both slots' steps itself
            // on every call, which meant up to 4 GetChainStepsForMatchAndUserAsync
            // round trips per swept match where 2 suffice. Reusing what this
            // loop already loaded removes the redundant pair (quality-architect
            // review of this diff, ADR-0084's duplicated-shape/duplicated-work
            // budget).
            var playerASteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, match.PlayerAUserId, cancellationToken);
            var playerBSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, match.PlayerBUserId, cancellationToken);

            var playerATimedOutAt = match.PlayerATimedOutAt;
            if (playerATimedOutAt is null && match.PlayerABustedAt is null && !playerASteps.HasClosedChain())
            {
                await connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, now, cancellationToken);
                playerATimedOutAt = now;
                playersForfeited++;
            }

            var playerBTimedOutAt = match.PlayerBTimedOutAt;
            if (playerBTimedOutAt is null && match.PlayerBBustedAt is null && !playerBSteps.HasClosedChain())
            {
                await connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: false, now, cancellationToken);
                playerBTimedOutAt = now;
                playersForfeited++;
            }

            // REQ-1405/1409: resolve immediately in this same sweep call,
            // never deferred to a later pass — ResolveIfBothTerminalAsync
            // evaluates the match's full terminal state itself (timeout,
            // bust, AND chain completion, whichever mix applies) from the
            // steps and timeout values just established above, which is
            // what makes a mixed outcome (one timed out, one already
            // busted/completed) resolve correctly here too, not just the
            // both-timed-out case this sweep used to special-case inline.
            if (await ResolveIfBothTerminalAsync(match, playerASteps, playerBSteps, playerATimedOutAt, playerBTimedOutAt, cancellationToken))
                matchesResolved++;
        }

        return new ForfeitSweepResult(playersForfeited, matchesResolved);
    }

    public async Task<bool> TryResolveMatchIfBothTerminalAsync(Guid matchId, CancellationToken cancellationToken = default)
    {
        var match = await connectMatchRepository.GetMatchByIdAsync(matchId, cancellationToken);
        if (match is null)
            return false;

        // REQ-1408: chain completion is detected the same way
        // ConnectChainStepService itself detects it — a ClosesChain=true
        // ConnectChainStep row for that slot's UserId. Queried by slot
        // (match.PlayerAUserId/PlayerBUserId) rather than by an
        // independently-supplied userId, consistent with every other
        // slot-based read/write in this class. This is the one-off,
        // externally-triggered call path (from ConnectChainStepService) that
        // has no already-loaded steps to reuse, unlike RunForfeitSweepAsync
        // above — so it fetches fresh here and delegates the actual
        // terminal-state evaluation to the same ResolveIfBothTerminalAsync
        // helper that loop reuses.
        var playerASteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, match.PlayerAUserId, cancellationToken);
        var playerBSteps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(matchId, match.PlayerBUserId, cancellationToken);

        return await ResolveIfBothTerminalAsync(
            match, playerASteps, playerBSteps, match.PlayerATimedOutAt, match.PlayerBTimedOutAt, cancellationToken);
    }

    // REQ-1405/1407/1408/1409/S-214: the single place every one of a
    // match's three terminal-reaching paths (timeout, bust, chain
    // completion) converges into a resolution decision — shared by
    // RunForfeitSweepAsync (which already has this match's current
    // playerA/playerBSteps and this pass's own timed-out-at values loaded)
    // and TryResolveMatchIfBothTerminalAsync (which loads them fresh for its
    // single match). Takes the terminal-relevant state as parameters rather
    // than loading it itself so neither caller re-fetches what the other
    // already has — see this class's two callers for why each shape of
    // input is what it is. playerATimedOutAt/playerBTimedOutAt are passed
    // separately from `match` (rather than read off match.PlayerATimedOutAt/
    // PlayerBTimedOutAt directly) because RunForfeitSweepAsync's `match`
    // came back AsNoTracking from before this sweep pass's own writes — its
    // caller folds in whatever this pass just wrote locally, the same way
    // the pre-refactor version of this method used to.
    private async Task<bool> ResolveIfBothTerminalAsync(
        ConnectMatch match,
        IReadOnlyList<ConnectChainStep> playerASteps,
        IReadOnlyList<ConnectChainStep> playerBSteps,
        DateTime? playerATimedOutAt,
        DateTime? playerBTimedOutAt,
        CancellationToken cancellationToken)
    {
        if (match.Status == ConnectMatchStatus.Resolved)
            return false;

        var playerACompleted = playerASteps.HasClosedChain();
        var playerBCompleted = playerBSteps.HasClosedChain();

        var playerAForfeited = playerATimedOutAt is not null || match.PlayerABustedAt is not null;
        var playerBForfeited = playerBTimedOutAt is not null || match.PlayerBBustedAt is not null;

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

        await connectMatchRepository.ResolveMatchAsync(match.Id, outcome, now, playerAScore, playerBScore, cancellationToken);
        return true;
    }

    // REQ-1411/S-216: see this method's own doc comment on
    // IConnectMatchLifecycleService. Small-N by construction (a player
    // realistically has a handful of open matches at once), so a
    // GetChainStepsForMatchAndUserAsync call per open match here is the
    // simpler, sufficient approach — same reasoning the brief for this story
    // uses to reject a batched chain-step query as unnecessary at this data
    // scale.
    public async Task<IReadOnlyList<ConnectMatch>> GetMatchesAwaitingActionAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        var openMatches = await connectMatchRepository.GetOpenMatchesForUserAsync(userId, cancellationToken);

        var awaitingAction = new List<ConnectMatch>();
        foreach (var match in openMatches)
        {
            var isPlayerA = match.PlayerAUserId == userId;
            var bustedAt = isPlayerA ? match.PlayerABustedAt : match.PlayerBBustedAt;
            var timedOutAt = isPlayerA ? match.PlayerATimedOutAt : match.PlayerBTimedOutAt;

            // Already terminal via bust or timeout — not awaiting this
            // player's move, regardless of the other participant's state.
            if (bustedAt is not null || timedOutAt is not null)
                continue;

            // Already terminal via a completed chain (REQ-1408). A player
            // who hasn't submitted a target pick yet, or who has an
            // in-progress chain with no ClosesChain=true step, naturally
            // falls through both checks above and is included below — no
            // separate "no target pick yet" branch is needed.
            var steps = await connectMatchRepository.GetChainStepsForMatchAndUserAsync(match.Id, userId, cancellationToken);
            if (steps.HasClosedChain())
                continue;

            awaitingAction.Add(match);
        }

        return awaitingAction;
    }
}
