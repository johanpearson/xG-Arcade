using Microsoft.Extensions.Logging;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// REQ-1501 (xG Higher/Lower, S-232, ADR-0113): `dotnet run --
// backfill-player-trophy-stats`'s own driving loop — the full-pool
// population mechanism ADR-0113 calls for. Mirrors
// PlayerInternationalStatsBackfillService's exact shape (ADR-0112's own
// design choice, itself modeled on PlayerPositionBirthYearBackfillService):
// a cursor over Player rows already known
// (GetPlayersMissingTrophyStatsAsync, the same "missing X" shape
// GetPlayersMissingInternationalStatsAsync/GetPlayersMissingPositionOrBirthYearAsync
// already establish), driven to completion batch by batch — never a
// PlayerCareerPrefetchService-style country/club discovery sweep, since a
// trophy holder can come from any country/club regardless of which seeded
// entity originally swept them into this codebase.
//
// The actual Wikidata fetch+write for each batch is delegated to
// IPlayerTrophyStatsRefreshService.RefreshTrophyStatsAsync (ADR-0113
// mandates that exact interface shape, mirroring
// IPlayerInternationalStatsRefreshService) rather than done inline here —
// this class's own job is purely "find the next batch of missing players and
// drive the refresh service across the whole pool," never the Wikidata call
// or the PlayerAttribute/PlayerData write itself. Passes throwOnFailure:
// true so a batch's Wikidata failure is distinguishable from "these players
// genuinely won none of the seeded trophies" for this service's own
// batchesFailed reporting.
//
// PlayersAttempted (not PlayersBackfilled): RefreshTrophyStatsAsync returns
// plain Task, not a written-row count (mirroring
// IPlayerTrophyStatsRefreshService/IPlayerInternationalStatsRefreshService's
// own void-Task contract) — so this only reports how many missing players
// were SENT to the refresh service this run, not how many actually won a
// qualifying trophy. Good enough for this job's own progress logging — the
// large majority of any batch will legitimately resolve zero trophies, same
// accepted shape as the caps/goals sweep's own reporting.
public class PlayerTrophyStatsBackfillService(
    IPlayerBackfillRepository playerBackfillRepository,
    IPlayerTrophyStatsRefreshService refreshService,
    ILogger<PlayerTrophyStatsBackfillService> logger)
{
    // Same conservative batch size as PlayerInternationalStatsBackfillService.BatchSize/
    // PlayerPositionBirthYearBackfillService.BatchSize — safely inside the
    // "few-thousand-row, no ORDER BY/LIMIT/OFFSET" bounded-query class
    // (implementation-document.md §6a).
    public const int BatchSize = 200;

    private const int ProgressLogBatchInterval = 5;

    public async Task<PlayerTrophyStatsBackfillResult> BackfillAsync(CancellationToken cancellationToken = default)
    {
        var attemptedPlayerIds = new HashSet<Guid>();
        var batchesProcessed = 0;
        var batchesFailed = 0;
        var playersAttempted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await playerBackfillRepository.GetPlayersMissingTrophyStatsAsync(
                attemptedPlayerIds, BatchSize, cancellationToken);
            if (batch.Count == 0)
                break;

            batchesProcessed++;
            var batchPlayerIds = new List<Guid>(batch.Count);
            foreach (var player in batch)
            {
                attemptedPlayerIds.Add(player.Id);
                batchPlayerIds.Add(player.Id);
            }
            playersAttempted += batchPlayerIds.Count;

            try
            {
                // throwOnFailure: true — see this class's own doc comment
                // for why (distinguishes a genuine Wikidata technical
                // failure from "these players simply won none of the seeded
                // trophies," the latter of which RefreshTrophyStatsAsync
                // handles as an ordinary, non-failure outcome).
                await refreshService.RefreshTrophyStatsAsync(batchPlayerIds, throwOnFailure: true, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                batchesFailed++;
                logger.LogWarning(ex,
                    "backfill-player-trophy-stats: batch of {BatchSize} player(s) failed; skipping to the next batch. " +
                    "This job is idempotent and safe to re-run — these players still show as missing this data " +
                    "and will be retried on the next run.",
                    batchPlayerIds.Count);
                LogProgressCheckpoint(batchesProcessed, playersAttempted);
                continue;
            }

            LogProgressCheckpoint(batchesProcessed, playersAttempted);
        }

        var result = new PlayerTrophyStatsBackfillResult(batchesProcessed, playersAttempted, batchesFailed);
        logger.LogInformation(
            "backfill-player-trophy-stats: complete — {BatchesProcessed} batch(es) processed, " +
            "{PlayersAttempted} player(s) attempted, {BatchesFailed} batch(es) failed.",
            result.BatchesProcessed, result.PlayersAttempted, result.BatchesFailed);

        return result;
    }

    private void LogProgressCheckpoint(int batchesProcessed, int playersAttempted)
    {
        if (batchesProcessed % ProgressLogBatchInterval == 0)
            logger.LogInformation(
                "backfill-player-trophy-stats progress: {BatchesProcessed} batch(es) processed so far, {PlayersAttempted} player(s) attempted.",
                batchesProcessed, playersAttempted);
    }
}

public record PlayerTrophyStatsBackfillResult(int BatchesProcessed, int PlayersAttempted, int BatchesFailed);
