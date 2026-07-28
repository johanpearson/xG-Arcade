using Microsoft.Extensions.Logging;
using XGArcade.Data.Entities;
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
// (every reference pair, each up to a real ~15-45s live Wikidata call —
// see WikidataClient's own _cacheWarmingQueryTimeout comment) — running it
// inside a synchronous HTTP request would hit the exact ingress-timeout
// wall ADR-0023 just fixed round generation against, and this Container
// App's `minReplicas: 0` scale-to-zero (NOTES.md, 2026-07-09) makes a
// fire-and-forget background task inside the app unsafe too (a scale-down
// mid-run would silently lose all progress with no persisted state to
// resume from). A plain foreground CI-runner process, bounded only by the
// workflow's own generous job timeout, has neither problem.
//
// Deliberately sequential, not concurrent — same DbContext-safety
// reasoning as GridGameModule.PickHeadersAsync (see that class's own doc
// comment). This service doesn't share a request-scoped context (its CLI
// caller builds a single-use one), but nothing here is safe for
// concurrent use of a single DbContext instance regardless of scope.
//
// Idempotent and safe to re-run: skips any pair already at or above
// MinValidAnswers (a fast, cache-only read) rather than re-querying
// Wikidata for data that can't have changed. A pair cached BELOW
// MinValidAnswers is, as of the 2026-07-28 "persisted confirmed-low
// signal" extension below, ALSO skipped once a prior run has confirmed it
// — see the ConfirmedLowMatchPair check in each loop below and that
// entity's own doc comment for the full "why" (this was an accepted gap
// in REQ-110's first pass, no longer true).
//
// REQ-110 (2026-07-28 extension) — technical-failure visibility: three
// consecutive runs (2026-07-26/27) produced byte-identical summaries with
// zero visible progress. Most of that was the (now-closed) re-querying gap
// above, but a real, separate contributor was WikidataClient's
// throwOnTimeout=false sync path swallowing genuine technical failures
// (WDQS timeout, HTTP error, JSON parse error) into the exact same empty
// match list a real "queried successfully, genuinely below threshold"
// pair produces — one run alone had 133/1214 live queries (11%) end this
// way, invisible in the summary. WikidataLookupService.LookupAndPersistAsync/
// LookupAndPersistClubClubAsync accept an optional onTechnicalFailure
// callback (threaded from WikidataClient's own RunIntersectionQueryAsync)
// that this class supplies per pair, purely to build PairsWithTechnicalFailure/
// FailingPairs below.
//
// REQ-110 (2026-07-28 extension) — cache-warming-specific timeout + same-run
// retry: a real portion of those 133 technical failures were WDQS queries
// timing out at round-generation's 15s budget even though nobody is waiting
// synchronously on a cache-warming run — see WikidataQueryTimeoutTier and
// WikidataClient's _cacheWarmingQueryTimeout for the third, longer,
// cache-warming-only budget this class now requests
// (WikidataQueryTimeoutTier.CacheWarming, passed on every live lookup
// below). Same-run retry (LookupWithSameRunRetryAsync below) lives HERE,
// not inside WikidataClient/WikidataLookupService: those two stay
// single-attempt and stateless per call (WikidataClient in particular has
// no concept of "a run" at all — it's origin-agnostic beyond
// throwOnTimeout/timeoutTier), while this class already owns the "one
// WarmAsync call = one run" concept and its own per-pair summary
// bookkeeping. Retrying inside WikidataClient would mean it secretly knows
// about cache-warming's retry policy; retrying here keeps that policy
// colocated with the only caller that needs it, and mirrors
// warm-player-cache.yml's own external, orchestration-level retry (a
// process-crash-survival retry around the whole job — a different,
// complementary layer from this single-pair, same-process retry).
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

    // REQ-110 (2026-07-28): "retried at least once within the same run"
    // (docs/requirements-document.md) — 2 total attempts (1 initial + 1
    // retry) per pair. Not configurable/higher: a genuinely down WDQS
    // (rather than a transient blip) would otherwise multiply this run's
    // total wall-clock cost by however many attempts are configured, across
    // every one of the few hundred still-failing pairs — 2 is enough to
    // recover the transient case (a momentary 502, a one-off slow query)
    // this extension's own evidence describes ("a transient WDQS 502 or a
    // momentary timeout may well succeed on a same-run retry a few seconds
    // later") without turning a real outage into a run that takes several
    // times longer than necessary before reporting it.
    private const int MaxAttemptsPerPair = 2;

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
        var pairsSkippedConfirmedLow = 0;
        var pairsProcessed = 0;
        var pairsWithTechnicalFailure = 0;
        var failingPairs = new List<string>();

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
                // REQ-110 (2026-07-28 "persisted confirmed-low signal"
                // extension): checked only once cachedCount has already
                // shown this pair is below threshold THIS run (a real,
                // freshly-computed count, not a stale one) — so this check
                // is safe even if MinValidAnswers itself has changed since
                // the pair was marked (see ConfirmedLowMatchPair's own doc
                // comment for why that ordering matters).
                else if (await playerStoreRepository.IsConfirmedLowAsync(
                    NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken))
                {
                    pairsSkippedConfirmedLow++;
                    logger.LogDebug("{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
                        country.Name, club.Name);
                }
                else
                {
                    var (matches, hadTechnicalFailure) = await LookupWithSameRunRetryAsync(
                        onFail => wikidataLookupService.LookupAndPersistAsync(
                            country, club, WikidataLookupOrigin.Sync, cancellationToken,
                            onTechnicalFailure: onFail, timeoutTier: WikidataQueryTimeoutTier.CacheWarming),
                        cancellationToken);
                    pairsQueriedLive++;
                    if (hadTechnicalFailure)
                    {
                        pairsWithTechnicalFailure++;
                        failingPairs.Add($"{country.Name} x {club.Name}");
                    }
                    else
                    {
                        // REQ-110: a real (possibly zero-match) answer — not
                        // a swallowed technical failure — so if it's still
                        // below threshold, persist the confirmed-low marker
                        // for next run. matches.Count is the query's
                        // complete, un-LIMITed result set (implementation-
                        // document.md §6a), so it's the true current match
                        // count, not just "however many were new."
                        if (matches.Count < options.MinValidAnswers)
                        {
                            await playerStoreRepository.RecordConfirmedLowAsync(
                                NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
                        }
                    }
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
                // REQ-110: see the Country x Club loop's own comment above
                // — same reasoning here.
                else if (await playerStoreRepository.IsConfirmedLowAsync(
                    ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken))
                {
                    pairsSkippedConfirmedLow++;
                    logger.LogDebug("{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
                        clubs[i].Name, clubs[j].Name);
                }
                else
                {
                    var (matches, hadTechnicalFailure) = await LookupWithSameRunRetryAsync(
                        onFail => wikidataLookupService.LookupAndPersistClubClubAsync(
                            clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
                            onTechnicalFailure: onFail, timeoutTier: WikidataQueryTimeoutTier.CacheWarming),
                        cancellationToken);
                    pairsQueriedLive++;
                    if (hadTechnicalFailure)
                    {
                        pairsWithTechnicalFailure++;
                        failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
                    }
                    else
                    {
                        // REQ-110: see the Country x Club loop's own comment
                        // above — same reasoning here.
                        if (matches.Count < options.MinValidAnswers)
                        {
                            await playerStoreRepository.RecordConfirmedLowAsync(
                                ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
                        }
                    }
                    logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
                        clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
                }

                LogProgressCheckpoint(pairsProcessed, totalPairs);
            }
        }

        var result = new CacheWarmingResult(
            totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs, pairsSkippedConfirmedLow);

        // REQ-110: the failing-pairs list is logged in full here, at
        // Information level, exactly once per run — not per-pair (each
        // pair's own failure was already logged as a Warning inside
        // WikidataClient when it happened; see RunIntersectionQueryAsync).
        // A comma-joined string rather than one log call per pair, matching
        // this method's existing "coarse summary, not a per-pair stream"
        // logging shape (see ProgressLogInterval's own comment).
        logger.LogInformation(
            "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
            "{PairsSkippedConfirmedLow} skipped as previously confirmed low, " +
            "{PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error, after a same-run retry) rather than a clean answer.{FailingPairsSuffix}",
            result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow, result.PairsWithTechnicalFailure,
            result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);

        return result;
    }

    // REQ-110 (2026-07-28 "cache-warming-specific timeout + same-run retry"
    // extension): shared by both loops above — `attempt` closes over
    // whichever Lookup*Async call the caller needs (Country x Club vs.
    // Club x Club), receiving this method's own per-attempt onTechnicalFailure
    // callback so each attempt's outcome is observed independently (a
    // failure on attempt 1 must not leak into attempt 2's result). Returns
    // the LAST attempt's matches/failure state — if the retry succeeds, that
    // becomes the final (non-failure) result; if it doesn't, the pair is
    // reported as a technical failure exactly once, not twice.
    private static async Task<(IReadOnlyList<Player> Matches, bool HadTechnicalFailure)> LookupWithSameRunRetryAsync(
        Func<Action, Task<IReadOnlyList<Player>>> attempt, CancellationToken cancellationToken)
    {
        IReadOnlyList<Player> matches = [];
        var hadTechnicalFailure = false;

        for (var attemptNumber = 1; attemptNumber <= MaxAttemptsPerPair; attemptNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var thisAttemptFailed = false;
            matches = await attempt(() => thisAttemptFailed = true);
            hadTechnicalFailure = thisAttemptFailed;

            if (!hadTechnicalFailure)
                break;
        }

        return (matches, hadTechnicalFailure);
    }

    private void LogProgressCheckpoint(int pairsProcessed, int totalPairs)
    {
        if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
            logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked.", pairsProcessed, totalPairs);
    }
}
