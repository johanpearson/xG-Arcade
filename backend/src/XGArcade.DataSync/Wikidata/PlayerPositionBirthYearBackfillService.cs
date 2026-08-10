using Microsoft.Extensions.Logging;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// REQ-1207 backfill (bug-bundle fix, 2026-08-02): fills Player.Position/
// Player.BirthYear for every Player row that predates migration
// 20260727140000_AddPlayerPositionAndBirthYear's P413/P569 addition to
// WikidataClient's intersection queries — the exact mirror of
// PlayerPhotoBackfillService, just for Position/BirthYear instead of
// PhotoUrl. See that class's own doc comment for the full "why a CLI verb,
// not an HTTP endpoint or background task" (ADR-0024), "why sequential, not
// concurrent DbContext use," "why log-and-continue per batch instead of
// PlayerNameIndexImporter's retry-then-fail-loud," and "why a malformed
// WikidataQid is filtered out and logged before the batch is sent"
// reasoning — every one of those judgment calls applies identically here,
// unchanged, and is not repeated in full below to avoid the two classes'
// comments silently drifting apart from being maintained twice.
// PlayerStoreRepository.GetOrCreatePlayersByWikidataQidAsync only ever sets
// Position/BirthYear at the moment a Player row is first created — an
// already-existing row (the common case for anyone who ran
// `warm-player-cache`/played xG Path before REQ-1207 shipped, which is most
// of the pool as of this bug report) is returned as-is and never revisited,
// so both fields stay NULL on it forever with no other code path that will
// ever backfill them. This is exactly why xG Path testers saw "Position: not
// available"/"Age: not available" on essentially every puzzle.
//
// Idempotent and safe to re-run indefinitely, same reasoning as
// PlayerPhotoBackfillService: each run only ever WRITES rows still missing
// at least one of the two fields (IPlayerStoreRepository
// .GetPlayersMissingPositionOrBirthYearAsync's own WHERE Position IS NULL OR
// BirthYear IS NULL filter), so a second run touches nothing for a player
// already fully backfilled by the first. Same known/accepted limitation as
// PlayerPhotoBackfillService's own doc comment: a player who genuinely has
// no Wikidata P413/P569 statement for one of these fields stays NULL forever
// (correctly), so every future full re-run re-queries Wikidata for that
// player again — accepted for the same "occasional job, not a tight
// recurring schedule" reasoning.
public class PlayerPositionBirthYearBackfillService(
    IPlayerStoreRepository playerStoreRepository,
    IWikidataClient wikidataClient,
    ILogger<PlayerPositionBirthYearBackfillService> logger)
{
    // Same conservative batch size as PlayerPhotoBackfillService.BatchSize —
    // see that class's own comment for the "safely inside the bounded-query
    // budget, few enough round trips" reasoning, unchanged here.
    public const int BatchSize = 200;

    // Same coarse-but-visible progress cadence as PlayerPhotoBackfillService.
    private const int ProgressLogBatchInterval = 5;

    public async Task<PlayerPositionBirthYearBackfillResult> BackfillAsync(CancellationToken cancellationToken = default)
    {
        var attemptedPlayerIds = new HashSet<Guid>();
        var batchesProcessed = 0;
        var batchesFailed = 0;
        var playersBackfilled = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await playerStoreRepository.GetPlayersMissingPositionOrBirthYearAsync(
                attemptedPlayerIds, BatchSize, cancellationToken);
            if (batch.Count == 0)
                break;

            batchesProcessed++;
            foreach (var player in batch)
                attemptedPlayerIds.Add(player.Id);

            // Same malformed-QID filter-and-log-before-sending discipline as
            // PlayerPhotoBackfillService's own BackfillAsync — see that
            // method's doc comment for the full reasoning (a bad row costs
            // only that one player a delayed backfill, never the rest of its
            // batch).
            var qids = new List<string>(batch.Count);
            foreach (var player in batch)
            {
                if (WikidataQid.IsValid(player.WikidataQid!))
                {
                    qids.Add(player.WikidataQid!);
                }
                else
                {
                    logger.LogWarning(
                        "backfill-player-position-birthyear: player {PlayerId} has a malformed WikidataQid " +
                        "('{WikidataQid}') and is being skipped rather than failing its whole batch. " +
                        "This is a data-quality issue on the Player row, not a transient failure — it " +
                        "will keep being skipped on every future run until the row is corrected.",
                        player.Id, player.WikidataQid);
                }
            }

            IReadOnlyDictionary<string, PlayerPositionBirthYearEntry> entriesByQid;
            try
            {
                entriesByQid = await wikidataClient.QueryPlayerPositionsAndBirthYearsByQidsAsync(qids, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                batchesFailed++;
                logger.LogWarning(ex,
                    "backfill-player-position-birthyear: batch of {BatchSize} QID(s) failed; skipping to the next batch. " +
                    "This job is idempotent and safe to re-run — these players still show as missing this data " +
                    "and will be retried on the next run.",
                    qids.Count);
                LogProgressCheckpoint(batchesProcessed, playersBackfilled);
                continue;
            }

            var updatesByPlayerId = batch
                .Where(p => entriesByQid.ContainsKey(p.WikidataQid!))
                .ToDictionary(
                    p => p.Id,
                    p =>
                    {
                        var entry = entriesByQid[p.WikidataQid!];
                        return new PlayerPositionBirthYearUpdate(entry.Position, entry.BirthYear);
                    });

            if (updatesByPlayerId.Count > 0)
            {
                await playerStoreRepository.UpdatePlayerPositionsAndBirthYearsAsync(updatesByPlayerId, cancellationToken);
                playersBackfilled += updatesByPlayerId.Count;
            }

            LogProgressCheckpoint(batchesProcessed, playersBackfilled);
        }

        var result = new PlayerPositionBirthYearBackfillResult(batchesProcessed, playersBackfilled, batchesFailed);
        logger.LogInformation(
            "backfill-player-position-birthyear: complete — {BatchesProcessed} batch(es) processed, " +
            "{PlayersBackfilled} player(s) backfilled, {BatchesFailed} batch(es) failed.",
            result.BatchesProcessed, result.PlayersBackfilled, result.BatchesFailed);

        return result;
    }

    private void LogProgressCheckpoint(int batchesProcessed, int playersBackfilled)
    {
        if (batchesProcessed % ProgressLogBatchInterval == 0)
            logger.LogInformation(
                "backfill-player-position-birthyear progress: {BatchesProcessed} batch(es) processed so far, {PlayersBackfilled} player(s) backfilled.",
                batchesProcessed, playersBackfilled);
    }
}

public record PlayerPositionBirthYearBackfillResult(int BatchesProcessed, int PlayersBackfilled, int BatchesFailed);
