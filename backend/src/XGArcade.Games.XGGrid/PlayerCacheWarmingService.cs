using Microsoft.Extensions.Logging;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid;

// REQ-110: proactively fills the PlayerAttribute cache for every reference
// Country x Club and Club x Club pair, instead of only ever discovering a
// pair's real match count as a side effect of a live round-generation
// attempt (REQ-101/103). Predicted and deliberately deferred back in
// S-011's backlog entry ("a scheduled/proactive cache pre-warming job ...
// revisit if S-014's threshold bump makes grid generation struggle in
// practice") — it did, 2026-07-13 (see NOTES.md and
// docs/decisions/0023-grid-generation-wall-clock-deadline.md).
//
// Deliberately a `dotnet run -- warm-player-cache` CLI verb (Program.cs),
// not an HTTP endpoint — same shape as the existing `migrate-and-seed`
// verb. This job's whole point is that it's allowed to take a long time
// (every reference pair, each up to a real ~15-27s live Wikidata call per
// ADR-0011) — running it inside a synchronous HTTP request would hit the
// exact ingress-timeout wall ADR-0023 just fixed round generation against,
// and this Container App's `minReplicas: 0` scale-to-zero (NOTES.md,
// 2026-07-09) makes a fire-and-forget background task inside the app
// unsafe too (a scale-down mid-run would silently lose all progress with
// no persisted state to resume from). A plain foreground CI-runner
// process, bounded only by the workflow's own generous job timeout, has
// neither problem.
//
// Deliberately sequential, not concurrent — same DbContext-safety
// reasoning as GridGameModule.PickHeadersAsync (see that class's own doc
// comment). This service doesn't share a request-scoped context (its CLI
// caller builds a single-use one), but nothing here is safe for
// concurrent use of a single DbContext instance regardless of scope.
//
// Idempotent and safe to re-run: skips any pair already at or above
// MinValidAnswers (a fast, cache-only read) rather than re-querying
// Wikidata for data that can't have changed. Does NOT skip a pair that's
// cached below MinValidAnswers — there's no persisted "already checked,
// genuinely below threshold" signal distinct from "never checked" (a
// query that finds zero matches persists nothing at all, per
// WikidataLookupService's own contract), so a below-threshold pair gets
// re-queried on every run. Accepted for this first pass: the reference
// pool is a few hundred pairs, and this is meant to be run after a
// reference-data change, not on a tight recurring schedule, so the
// re-querying cost is bounded and infrequent, not a real problem yet.
public class PlayerCacheWarmingService(
    ICategoryValueRepository categoryValueRepository,
    IPlayerStoreRepository playerStoreRepository,
    IWikidataLookupService wikidataLookupService,
    GridGenerationOptions options,
    ILogger<PlayerCacheWarmingService> logger) : IPlayerCacheWarmingService
{
    private const string NationalityAttributeType = "nationality";
    private const string ClubAttributeType = "club";

    // Coarse enough not to flood the log across a few hundred pairs,
    // frequent enough that a long run's progress is still visible in real
    // time (both the CLI console and a GitHub Actions log stream live).
    private const int ProgressLogInterval = 25;

    public async Task<CacheWarmingResult> WarmAsync(CancellationToken cancellationToken = default)
    {
        var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
        var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);

        var countryClubPairCount = countries.Count * clubs.Count;
        var clubClubPairCount = clubs.Count * (clubs.Count - 1) / 2;
        var totalPairs = countryClubPairCount + clubClubPairCount;

        logger.LogInformation(
            "Starting player cache warming: {CountryCount} countries x {ClubCount} clubs = {CountryClubPairCount} Country x Club pairs, " +
            "plus {ClubClubPairCount} unique Club x Club pairs ({TotalPairs} total), MinValidAnswers={MinValidAnswers}.",
            countries.Count, clubs.Count, countryClubPairCount, clubClubPairCount, totalPairs, options.MinValidAnswers);

        var pairsQueriedLive = 0;
        var pairsAlreadyValid = 0;
        var pairsProcessed = 0;

        foreach (var country in countries)
        {
            foreach (var club in clubs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pairsProcessed++;

                var cachedCount = await playerStoreRepository.CountPlayersWithBothAttributesAsync(
                    NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
                if (cachedCount >= options.MinValidAnswers)
                {
                    pairsAlreadyValid++;
                }
                else
                {
                    var matches = await wikidataLookupService.LookupAndPersistAsync(country, club, cancellationToken);
                    pairsQueriedLive++;
                    logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
                        country.Name, club.Name, matches.Count, cachedCount);
                }

                LogProgressCheckpoint(pairsProcessed, totalPairs);
            }
        }

        for (var i = 0; i < clubs.Count; i++)
        {
            for (var j = i + 1; j < clubs.Count; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pairsProcessed++;

                var cachedCount = await playerStoreRepository.CountPlayersWithBothAttributesAsync(
                    ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
                if (cachedCount >= options.MinValidAnswers)
                {
                    pairsAlreadyValid++;
                }
                else
                {
                    var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(clubs[i], clubs[j], cancellationToken);
                    pairsQueriedLive++;
                    logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
                        clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
                }

                LogProgressCheckpoint(pairsProcessed, totalPairs);
            }
        }

        var result = new CacheWarmingResult(totalPairs, pairsQueriedLive, pairsAlreadyValid);
        logger.LogInformation(
            "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid.",
            result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid);

        return result;
    }

    private void LogProgressCheckpoint(int pairsProcessed, int totalPairs)
    {
        if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
            logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked.", pairsProcessed, totalPairs);
    }
}
