using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect;

// REQ-1405/S-212: see IConnectMatchLifecycleService's own doc comment.
public class ConnectMatchLifecycleService(
    IConnectMatchRepository connectMatchRepository,
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
            // Track each slot's terminal status locally rather than
            // re-reading the match after every write — `match` came back
            // AsNoTracking from GetActiveMatchesPastDeadlineAsync, so its
            // own PlayerATimedOutAt/PlayerBTimedOutAt already reflect
            // whatever was true BEFORE this sweep call (e.g. a slot marked
            // terminal by a future S-213/S-214 bust/completion path, or an
            // earlier sweep pass that only got as far as one slot). Folding
            // in this pass's own writes locally is what lets "both reached
            // terminal" be evaluated correctly in the SAME iteration that
            // just wrote one or both of them, rather than needing a second
            // sweep pass to notice.
            var playerATimedOutAt = match.PlayerATimedOutAt;
            var playerBTimedOutAt = match.PlayerBTimedOutAt;

            if (playerATimedOutAt is null)
            {
                await connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: true, now, cancellationToken);
                playerATimedOutAt = now;
                playersForfeited++;
            }

            if (playerBTimedOutAt is null)
            {
                await connectMatchRepository.MarkPlayerTimedOutAsync(match.Id, isPlayerA: false, now, cancellationToken);
                playerBTimedOutAt = now;
                playersForfeited++;
            }

            // Both slots are now terminal — timeout is currently the ONLY
            // terminal-reaching path with real code behind it (REQ-1407's
            // bust and REQ-1408's chain completion are S-213/S-214's own
            // work), so both slots reaching terminal via timeout is,
            // unambiguously by REQ-1409's already-documented "both players
            // forfeit -> draw" rule, a Draw. A mixed outcome (one timed
            // out, one legitimately busted/completed before the deadline)
            // is S-213/S-214's own resolution logic to add once those
            // terminal paths exist — out of scope here. Resolving inside
            // this same loop iteration, in the same sweep call that just
            // learned both slots are terminal, is what satisfies REQ-1405's
            // "resolves immediately once both are reached, never waiting
            // out an unused remainder of the 6h window" rule — there is no
            // later pass this waits for.
            if (playerATimedOutAt is not null && playerBTimedOutAt is not null)
            {
                await connectMatchRepository.ResolveMatchAsync(match.Id, ConnectMatchOutcome.Draw, now, cancellationToken);
                matchesResolved++;
            }
        }

        return new ForfeitSweepResult(playersForfeited, matchesResolved);
    }
}
