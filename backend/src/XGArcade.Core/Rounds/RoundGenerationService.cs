using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Rounds;

// COMP-03: REQ-301's "generate one round ahead" scheduling rule.
//
// The rule is deliberately framed as an idempotency check, not a counter:
// if a round for this GameKey hasn't started yet, that upcoming round IS
// "round N+1" — nothing to do until it becomes active itself, however many
// times the scheduler job fires in the meantime. This is what makes the
// job safe to trigger more often than strictly necessary (a manual
// workflow_dispatch, a retried cron run) without ever accumulating extra
// rounds ahead of the active one.
public class RoundGenerationService(
    IRoundRepository roundRepository,
    IGameModuleResolver gameModuleResolver,
    IRoundCloseService roundCloseService,
    RoundSchedulingOptions options,
    TimeProvider timeProvider) : IRoundGenerationService
{
    public async Task<Round> GenerateNextRoundIfNeededAsync(RoundConfig config, TimeSpan? roundDurationOverride = null, CancellationToken cancellationToken = default)
    {
        var latest = await roundRepository.GetLatestByGameKeyAsync(options.GameKey, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // REQ-205: this scheduler job is the only production-scheduled
        // trigger point Tier 0 has (generate-round.yml's cron), so closing
        // (and thereby locking FinalPoints/the leaderboard total for) a round
        // happens here rather than needing a second scheduled job of its own.
        //
        // The round to close is never "latest" itself here — "latest" only
        // ever becomes the round about to start (or already active), one
        // full cycle before it, in the same generation call that made it
        // "latest" (see the branch below: startTime = latest?.EndTime ??
        // now). By construction, that predecessor's EndTime equals latest's
        // StartTime, so once latest has actually started (checked below),
        // its predecessor has necessarily already ended and is exactly the
        // round this job has never had a chance to close until now.
        // CloseRoundAsync is idempotent (its own doc comment), so a repeat
        // call here on an already-closed predecessor is harmless.
        if (latest is not null && latest.StartTime <= now)
        {
            var previous = await roundRepository.GetPreviousByGameKeyAsync(options.GameKey, latest.StartTime, cancellationToken);
            if (previous is not null && previous.EndTime <= now)
                await roundCloseService.CloseRoundAsync(previous.Id, now, cancellationToken);
        }

        // An upcoming (not-yet-started) round already exists for this game —
        // that already satisfies "one round ahead"; generating another would
        // put the schedule two rounds ahead instead of one.
        if (latest is not null && latest.StartTime > now)
            return latest;

        var gameModule = gameModuleResolver.Resolve(options.GameKey);
        // Games.XGGrid: assemble the instance and return its ID first —
        // Core.Rounds only creates the Round once generation has actually
        // succeeded (architecture-document.md §6.1's flow).
        var instance = await gameModule.GenerateInstanceAsync(config, cancellationToken);

        var startTime = latest?.EndTime ?? now;
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = options.GameKey,
            GameInstanceId = instance.Id,
            StartTime = startTime,
            // roundDurationOverride, when supplied, wins for this call only —
            // it never mutates the shared RoundSchedulingOptions singleton
            // (IRoundGenerationService's own doc comment).
            EndTime = startTime + (roundDurationOverride ?? options.RoundDuration),
            AllowGuessChange = options.AllowGuessChange,
        };

        return await roundRepository.AddAsync(round, cancellationToken);
    }
}
