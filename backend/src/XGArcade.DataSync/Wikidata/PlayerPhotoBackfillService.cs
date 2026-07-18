using Microsoft.Extensions.Logging;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// REQ-214 backfill (S-045): fills Player.PhotoUrl for every Player row that
// predates REQ-214's P18 addition to WikidataClient's intersection queries.
// WikidataLookupService.GetOrCreatePlayerAsync only ever sets PhotoUrl at
// the moment a Player row is first created — an already-existing row (the
// common case for anyone who ran `warm-player-cache` before REQ-214
// shipped) is returned as-is and never revisited, so PhotoUrl stays NULL
// on it forever with no other code path that will ever backfill it.
//
// Deliberately a `dotnet run -- backfill-player-photos` CLI verb
// (Program.cs), not an HTTP endpoint or a background task — squarely the
// same ADR-0024 reasoning PlayerCacheWarmingService's own doc comment
// already lays out in full (this Container App's `minReplicas: 0`
// scale-to-zero would silently drop progress mid-run; a few hundred/
// thousand live Wikidata calls exceed the ~240s ingress timeout). Not a
// new decision, so no new ADR — see this story's own task notes.
//
// Deliberately sequential, not concurrent — same DbContext-safety
// reasoning as PlayerCacheWarmingService/GridGameModule.PickHeadersAsync
// (see either class's own doc comment): nothing here is safe for
// concurrent use of a single DbContext instance, and this service's CLI
// caller builds one single-use context for the whole run.
//
// Idempotent and safe to re-run indefinitely, which is the whole point —
// this replaces a destructive wipe-and-rerun the user explicitly rejected
// (a full `purge-player-pool` + `warm-player-cache` cycle would cascade
// into PlayerAttribute/Guess/GridCell history this codebase explicitly
// protects, REQ-710/purge-player-pool's own doc comment). Each run only
// ever WRITES rows still missing a photo (IPlayerStoreRepository.
// GetPlayersMissingPhotoAsync's own WHERE PhotoUrl IS NULL filter), so a
// second run touches nothing for a player already backfilled by the first.
// Known, accepted limitation (same class as PlayerCacheWarmingService's own
// "below MinValidAnswers, re-queried every run" note): a player who
// genuinely has no Wikidata P18 statement stays PhotoUrl == NULL forever
// (correctly — there is nothing to backfill), so every future full re-run
// re-queries Wikidata for that player again. There is no persisted
// "already checked, genuinely has no photo" signal distinct from "never
// checked" (an empty QueryPlayerPhotosByQidsAsync result persists nothing
// at all). Accepted for the same reason PlayerCacheWarmingService accepts
// it: this job is meant to be run occasionally, not on a tight recurring
// schedule, so the re-querying cost is bounded and infrequent.
//
// Per-batch failure handling judgment call: LOG-AND-CONTINUE to the next
// batch, not PlayerNameIndexImporter's retry-then-fail-loud. This is a
// deliberate deviation from that precedent, made because the two jobs'
// failure-detection problem is different in kind, not just in severity.
// PlayerNameIndexImporter's retry-then-fail-loud exists specifically
// because an empty QueryPlayerPoolBirthYearAsync result is genuinely
// ambiguous with a swallowed failure UNLESS the client throws and the
// importer tracks which years failed — without that bookkeeping, a failed
// year and a sparse-but-real empty year look identical, and the 2026-07-18
// incident (NOTES.md) was exactly that ambiguity going undetected. Here,
// there is no equivalent ambiguity to fail loudly about: this service's
// unit of work (GetPlayersMissingPhotoAsync's result) is a live read of
// current database state, not a one-shot fetch that needs its own
// separate "did this fail" bookkeeping — a batch that fails to fetch
// photos simply leaves those specific players' PhotoUrl NULL, and the
// very next full re-run's GetPlayersMissingPhotoAsync call will surface
// them again automatically, with zero extra state to track. Given that
// re-detection is free, failing the whole run loudly over one transient
// batch failure would only cost an operator a manual re-run for no
// additional signal a log line doesn't already give them.
public class PlayerPhotoBackfillService(
    IPlayerStoreRepository playerStoreRepository,
    IWikidataClient wikidataClient,
    ILogger<PlayerPhotoBackfillService> logger)
{
    // Conservative batch size for QueryPlayerPhotosByQidsAsync's VALUES
    // clause — small enough to stay safely inside the "few-thousand-row,
    // no ORDER BY/LIMIT/OFFSET" bounded-query class implementation-
    // document.md §6a already establishes as safe on WDQS (a 200-item
    // VALUES clause is a tiny fraction of that budget), large enough that a
    // multi-thousand-row backlog finishes in a reasonable number of round
    // trips rather than one per player.
    public const int BatchSize = 200;

    // Coarse enough not to flood the log across a few thousand players,
    // frequent enough that a long run's progress is still visible in real
    // time — same reasoning as PlayerCacheWarmingService's own
    // ProgressLogInterval, scaled down since a "pair" there is a live
    // Wikidata call per pair, while a "batch" here is one call per
    // BatchSize players.
    private const int ProgressLogBatchInterval = 5;

    public async Task<PlayerPhotoBackfillResult> BackfillAsync(CancellationToken cancellationToken = default)
    {
        // Every player ID attempted so far THIS RUN (success or failure) —
        // see IPlayerStoreRepository.GetPlayersMissingPhotoAsync's own doc
        // comment for why this is what guarantees the loop below
        // terminates instead of re-fetching the same failed batch forever.
        var attemptedPlayerIds = new HashSet<Guid>();
        var batchesProcessed = 0;
        var batchesFailed = 0;
        var playersBackfilled = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await playerStoreRepository.GetPlayersMissingPhotoAsync(
                attemptedPlayerIds, BatchSize, cancellationToken);
            if (batch.Count == 0)
                break;

            batchesProcessed++;
            foreach (var player in batch)
                attemptedPlayerIds.Add(player.Id);

            // Safe: GetPlayersMissingPhotoAsync's own WHERE clause only
            // ever returns rows with WikidataQid != null.
            var qids = batch.Select(p => p.WikidataQid!).ToList();

            IReadOnlyDictionary<string, string> photoUrlsByQid;
            try
            {
                photoUrlsByQid = await wikidataClient.QueryPlayerPhotosByQidsAsync(qids, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                batchesFailed++;
                logger.LogWarning(ex,
                    "backfill-player-photos: batch of {BatchSize} QID(s) failed; skipping to the next batch. " +
                    "This job is idempotent and safe to re-run — these players still show as missing a photo " +
                    "and will be retried on the next run.",
                    qids.Count);
                LogProgressCheckpoint(batchesProcessed, playersBackfilled);
                continue;
            }

            var photoUrlByPlayerId = batch
                .Where(p => photoUrlsByQid.ContainsKey(p.WikidataQid!))
                .ToDictionary(p => p.Id, p => photoUrlsByQid[p.WikidataQid!]);

            if (photoUrlByPlayerId.Count > 0)
            {
                await playerStoreRepository.UpdatePlayerPhotosAsync(photoUrlByPlayerId, cancellationToken);
                playersBackfilled += photoUrlByPlayerId.Count;
            }

            LogProgressCheckpoint(batchesProcessed, playersBackfilled);
        }

        var result = new PlayerPhotoBackfillResult(batchesProcessed, playersBackfilled, batchesFailed);
        logger.LogInformation(
            "backfill-player-photos: complete — {BatchesProcessed} batch(es) processed, " +
            "{PlayersBackfilled} player(s) backfilled, {BatchesFailed} batch(es) failed.",
            result.BatchesProcessed, result.PlayersBackfilled, result.BatchesFailed);

        return result;
    }

    private void LogProgressCheckpoint(int batchesProcessed, int playersBackfilled)
    {
        if (batchesProcessed % ProgressLogBatchInterval == 0)
            logger.LogInformation(
                "backfill-player-photos progress: {BatchesProcessed} batch(es) processed so far, {PlayersBackfilled} player(s) backfilled.",
                batchesProcessed, playersBackfilled);
    }
}

public record PlayerPhotoBackfillResult(int BatchesProcessed, int PlayersBackfilled, int BatchesFailed);
