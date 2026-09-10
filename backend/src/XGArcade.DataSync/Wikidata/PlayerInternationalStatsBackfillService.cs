using Microsoft.Extensions.Logging;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// REQ-1501/REQ-1506 (xG Higher/Lower, S-231, ADR-0112): `dotnet run --
// backfill-player-international-stats`'s own driving loop — the full-pool
// population mechanism this story's task description calls for ("xG
// Higher/Lower's candidate pool must already be populated BEFORE generation
// runs, the same precondition club/trophy PlayerAttribute rows already
// have — so international-caps/goals sourcing needs a batch/scheduled
// population mechanism..., not a just-in-time refresh").
//
// Design choice (flagged for review, not silently assumed): rather than
// extending PlayerCareerPrefetchService (which discovers NEW players by
// sweeping seeded CountryDefinition/ClubDefinition rows via
// QueryPlayerPoolByNationalityAsync/QueryPlayerPoolByClubAsync — a
// fundamentally different iteration shape, "which players belong to this
// external entity," not "which of our own already-known players are
// missing this fact"), this mirrors PlayerPositionBirthYearBackfillService
// instead: a cursor over Player rows we ALREADY have
// (GetPlayersMissingInternationalStatsAsync, the exact "missing X" shape
// GetPlayersMissingPositionOrBirthYearAsync already establishes), driven
// to completion batch by batch. International caps/goals apply to a player
// regardless of which seeded country/club swept them into this codebase in
// the first place, so there is no country/club to iterate here — every
// existing Player row with a WikidataQid is a candidate, which is exactly
// what a backfill-shaped cursor (not a discovery-shaped sweep) answers.
//
// UNLIKE PlayerPositionBirthYearBackfillService, the actual Wikidata
// fetch+write for each batch is delegated to
// IPlayerInternationalStatsRefreshService.RefreshInternationalStatsAsync
// (ADR-0112 Decision point 3 mandates that exact interface, mirroring
// IPlayerCareerStintRefreshService's shape) rather than done inline here —
// this class's own job is purely "find the next batch of missing players
// and drive the refresh service across the whole pool," never the
// Wikidata call or the PlayerAttribute/PlayerData write itself. Passes
// throwOnFailure: true so a batch's Wikidata failure is distinguishable
// from "these players genuinely have no qualifying national-team
// statement" for this service's own batchesFailed reporting — see
// IPlayerInternationalStatsRefreshService's own doc comment.
//
// PlayersAttempted (not PlayersBackfilled, unlike
// PlayerPositionBirthYearBackfillResult): RefreshInternationalStatsAsync
// returns plain Task, not a written-row count (mirroring
// IPlayerCareerStintRefreshService.RefreshCareerStintsAsync's own void-Task
// contract, per ADR-0112) — so this only reports how many missing players
// were SENT to the refresh service this run, not how many actually
// resolved a qualifying Wikidata statement. Good enough for this job's own
// progress logging; not a correctness-relevant distinction (a player whose
// batch succeeded but resolved nothing simply keeps being selected by
// GetPlayersMissingInternationalStatsAsync on every future run — the same
// accepted "occasional job, not a tight recurring schedule" limitation
// every sibling backfill in this codebase already carries).
public class PlayerInternationalStatsBackfillService(
    IPlayerBackfillRepository playerBackfillRepository,
    IPlayerInternationalStatsRefreshService refreshService,
    ILogger<PlayerInternationalStatsBackfillService> logger)
{
    // Same conservative batch size as PlayerPositionBirthYearBackfillService.BatchSize/
    // PlayerPhotoBackfillService.BatchSize — safely inside the
    // "few-thousand-row, no ORDER BY/LIMIT/OFFSET" bounded-query class
    // (implementation-document.md §6a).
    public const int BatchSize = 200;

    private const int ProgressLogBatchInterval = 5;

    public async Task<PlayerInternationalStatsBackfillResult> BackfillAsync(CancellationToken cancellationToken = default)
    {
        var attemptedPlayerIds = new HashSet<Guid>();
        var batchesProcessed = 0;
        var batchesFailed = 0;
        var playersAttempted = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await playerBackfillRepository.GetPlayersMissingInternationalStatsAsync(
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
                // failure from "these players simply have no qualifying
                // national-team statement," the latter of which
                // RefreshInternationalStatsAsync handles as an ordinary,
                // non-failure outcome).
                await refreshService.RefreshInternationalStatsAsync(batchPlayerIds, throwOnFailure: true, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                batchesFailed++;
                logger.LogWarning(ex,
                    "backfill-player-international-stats: batch of {BatchSize} player(s) failed; skipping to the next batch. " +
                    "This job is idempotent and safe to re-run — these players still show as missing this data " +
                    "and will be retried on the next run.",
                    batchPlayerIds.Count);
                LogProgressCheckpoint(batchesProcessed, playersAttempted);
                continue;
            }

            LogProgressCheckpoint(batchesProcessed, playersAttempted);
        }

        var result = new PlayerInternationalStatsBackfillResult(batchesProcessed, playersAttempted, batchesFailed);
        logger.LogInformation(
            "backfill-player-international-stats: complete — {BatchesProcessed} batch(es) processed, " +
            "{PlayersAttempted} player(s) attempted, {BatchesFailed} batch(es) failed.",
            result.BatchesProcessed, result.PlayersAttempted, result.BatchesFailed);

        return result;
    }

    private void LogProgressCheckpoint(int batchesProcessed, int playersAttempted)
    {
        if (batchesProcessed % ProgressLogBatchInterval == 0)
            logger.LogInformation(
                "backfill-player-international-stats progress: {BatchesProcessed} batch(es) processed so far, {PlayersAttempted} player(s) attempted.",
                batchesProcessed, playersAttempted);
    }
}

public record PlayerInternationalStatsBackfillResult(int BatchesProcessed, int PlayersAttempted, int BatchesFailed);
