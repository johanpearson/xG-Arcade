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
    RoundSchedulingOptions options,
    TimeProvider timeProvider) : IRoundGenerationService
{
    public async Task<Round> GenerateNextRoundIfNeededAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        var latest = await roundRepository.GetLatestByGameKeyAsync(options.GameKey, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

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
            EndTime = startTime + options.RoundDuration,
            AllowGuessChange = options.AllowGuessChange,
        };

        return await roundRepository.AddAsync(round, cancellationToken);
    }
}
