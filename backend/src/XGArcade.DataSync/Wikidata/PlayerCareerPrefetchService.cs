using Microsoft.Extensions.Logging;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// ADR-0055: `dotnet run -- prefetch-player-careers`'s own service — see
// IPlayerCareerPrefetchService's own doc comment for the "what this is for
// / how it differs from PlayerCareerStintRefreshService" summary. A CLI
// verb, not an HTTP endpoint or background task, same ADR-0024 reasoning as
// every other bulk Wikidata job in this codebase (warm-player-cache,
// import-player-name-index, the two backfill services): this can run for a
// long time against a real player pool, far longer than the deployed
// backend's ~240s HTTP ingress allows, and this Container App's
// minReplicas: 0 would silently kill a fire-and-forget background task
// mid-run.
//
// Deliberately scoped to already-SEEDED countries (CategoryValueRepository
// .GetCountriesAsync, not a broader unfiltered pool) — the product owner's
// own explicit choice when this was proposed (ADR-0055's Open questions),
// not a default picked silently. A country with no eligible players simply
// contributes an empty pool, not a failure.
public class PlayerCareerPrefetchService(
    ICategoryValueRepository categoryValueRepository,
    IPlayerStoreRepository playerStore,
    IWikidataClient wikidataClient,
    ILogger<PlayerCareerPrefetchService> logger) : IPlayerCareerPrefetchService
{
    // Conservative batch size for QueryPlayerCareerStintsByQidsAsync's VALUES
    // clause within one country's pool — same size PlayerPhotoBackfillService/
    // PlayerPositionBirthYearBackfillService already use, safely inside
    // implementation-document.md §6a's "few-thousand-row, no ORDER BY/LIMIT/
    // OFFSET" bounded-query class.
    public const int CareerBatchSize = 200;

    public async Task<PlayerCareerPrefetchResult> PrefetchAsync(CancellationToken cancellationToken = default)
    {
        var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);

        var countriesProcessed = 0;
        var countriesFailed = 0;
        var careerBatchesFailed = 0;
        var playersTouched = 0;
        var stintsAdded = 0;
        var failedCountryNames = new List<string>();

        foreach (var country in countries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // REQ-109's "an unresolved QID isn't an error" reasoning — a
            // seeded country with no QID yet is simply skipped, not a
            // failure.
            if (country.WikidataQid is null)
                continue;

            IReadOnlyList<WikidataNameIndexEntry> pool;
            try
            {
                pool = await wikidataClient.QueryPlayerPoolByNationalityAsync(
                    country.WikidataQid, country.UsesCountryForSportProperty, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                countriesFailed++;
                failedCountryNames.Add(country.Name);
                logger.LogWarning(ex,
                    "prefetch-player-careers: {Country} failed; continuing with the remaining countries. " +
                    "This run WILL fail at the end, but the job is idempotent — re-run it to fill in the failed countries.",
                    country.Name);
                continue;
            }

            countriesProcessed++;
            if (pool.Count == 0)
                continue;

            foreach (var batch in pool.Chunk(CareerBatchSize))
            {
                var (touched, added, batchFailed) = await FetchAndPersistBatchAsync(batch, cancellationToken);
                playersTouched += touched;
                stintsAdded += added;
                if (batchFailed)
                    careerBatchesFailed++;
            }

            logger.LogInformation(
                "prefetch-player-careers: {Country} done — pool of {PoolSize} player(s) processed " +
                "(running totals: {PlayersTouched} player(s) touched, {StintsAdded} stint(s) added).",
                country.Name, pool.Count, playersTouched, stintsAdded);
        }

        if (countriesFailed > 0 || careerBatchesFailed > 0)
        {
            throw new InvalidOperationException(
                $"prefetch-player-careers: {countriesFailed} countr{(countriesFailed == 1 ? "y" : "ies")} " +
                $"failed to fetch their player pool ({string.Join(", ", failedCountryNames)}), and " +
                $"{careerBatchesFailed} career-fetch batch(es) failed. {playersTouched} player(s) were still " +
                "touched and " + $"{stintsAdded} stint(s) added from what succeeded; the job is idempotent — " +
                "re-run it to retry what failed.");
        }

        return new PlayerCareerPrefetchResult(countriesProcessed, playersTouched, stintsAdded, countriesFailed, careerBatchesFailed);
    }

    // Returns whether the career-fetch step itself failed (distinct from
    // "fetched but found nothing," which is a normal, non-failure outcome)
    // so the caller's loop can keep a separate failure tally without this
    // method needing to throw and unwind the whole country's remaining
    // batches over one batch's failure.
    private async Task<(int PlayersTouched, int StintsAdded, bool BatchFailed)> FetchAndPersistBatchAsync(
        IReadOnlyList<WikidataNameIndexEntry> batch, CancellationToken cancellationToken)
    {
        // REQ-214/REQ-1207's existing "set only at creation, never
        // overwritten on a later lookup" contract applies here unchanged —
        // a player already known from an earlier xG Grid lookup keeps
        // whatever PhotoUrl/Position they already have; PlayerNameIndex has
        // no PhotoUrl/Position of its own to offer regardless (dropped
        // 2026-07-18 / never added — see PlayerNameIndex.cs's own comment).
        var requests = batch
            .Select(entry => new PlayerCreationRequest(entry.WikidataQid, entry.FullName, PhotoUrl: null, Position: null, entry.BirthYear))
            .ToList();
        var playersByQid = await playerStore.GetOrCreatePlayersByWikidataQidAsync(requests, cancellationToken);

        IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> stintsByQid;
        try
        {
            stintsByQid = await wikidataClient.QueryPlayerCareerStintsByQidsAsync(playersByQid.Keys.ToList(), cancellationToken);
        }
        catch (WikidataQueryException ex)
        {
            logger.LogWarning(ex,
                "prefetch-player-careers: a career-fetch batch of {BatchSize} player(s) failed; " +
                "skipping to the next batch.", playersByQid.Count);
            return (playersByQid.Count, 0, true);
        }

        if (stintsByQid.Count == 0)
            return (playersByQid.Count, 0, false);

        var qidToPlayerId = playersByQid.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
        var affectedPlayerIds = stintsByQid.Keys.Select(qid => qidToPlayerId[qid]).ToList();
        var existingStintsByPlayerId = await playerStore.GetCareerStintsByPlayerIdsAsync(affectedPlayerIds, cancellationToken);

        var newStintsByPlayerId = PlayerCareerStintRefreshService.BuildNewStintsByPlayerId(
            stintsByQid, qidToPlayerId, existingStintsByPlayerId);

        if (newStintsByPlayerId.Count > 0)
            await playerStore.AddCareerStintsBatchAsync(newStintsByPlayerId, cancellationToken);

        return (playersByQid.Count, newStintsByPlayerId.Sum(kv => kv.Value.Count), false);
    }
}
