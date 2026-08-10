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
// REQ-110 (2026-07-28 extension) — cache-warming-specific timeout: a real
// portion of those 133 technical failures were WDQS queries timing out at
// round-generation's 15s budget even though nobody is waiting synchronously
// on a cache-warming run — see WikidataQueryTimeoutTier and WikidataClient's
// _cacheWarmingQueryTimeout for the third, longer, cache-warming-only
// budget this class now requests (WikidataQueryTimeoutTier.CacheWarming,
// passed on every live lookup below).
//
// REQ-110 (2026-08-01 "persistent technical-failure tracking" extension,
// ADR-0052) — REMOVES the same-run retry this class previously had here:
// a same-run retry only helps a TRANSIENT failure (a one-off 502, a
// momentary timeout); it does nothing for a STRUCTURAL one (a query shape
// that always blows up for a specific pair, see
// WikidataClient.BuildClubClubIntersectionQuery's own incident comment),
// and in fact makes a structural failure's cost WORSE — every failing pair
// paid the full cache-warming timeout TWICE instead of once. That
// regression is exactly what turned a ~1-hour job into one that reliably
// blew through its 90-minute CI budget starting 2026-07-28 (see NOTES.md's
// 2026-08-01 entry). The right lever for "this pair keeps failing" is
// cross-run persistence (PairLookupFailure, IsPersistentTechnicalFailureAsync/
// RecordTechnicalFailureAsync/ClearTechnicalFailureAsync below), not a
// same-run retry that can only ever pay the timeout cost again on the exact
// same process, moments later, against the exact same doomed query.
// warm-player-cache.yml's own external, orchestration-level retry (a
// process-crash-survival retry around the whole job) is unaffected — a
// different, complementary layer from this now-removed single-pair,
// same-process retry.
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

    // REQ-110 (2026-08-01 "persistent technical-failure tracking"
    // extension, ADR-0052): a pair is skipped, without any live query, once
    // its PairLookupFailure.ConsecutiveFailureCount reaches this — 2
    // consecutive RUN-level failures (not attempts; see this class's own
    // "removes the same-run retry" comment above), so a single transient
    // blip on one run never permanently starves a pair that resolves fine
    // on the very next run, while a pair that's still failing after a
    // second independent run (a real, separate chance against whatever
    // WDQS load/network conditions that later run happens to see) is
    // treated as structural and stops being retried until an operator
    // investigates or a query-shape fix clears it (StaleClubAttributeCleaner/
    // purge-player-pool, same invalidation surface as ConfirmedLowMatchPair).
    // internal, not private: GridGameModule.RefreshCellFromLiveLookupAsync
    // (2026-08-10) reuses this exact value so a guess-time fallback call
    // agrees with cache-warming on what counts as "already known doomed" -
    // both live in this same project (Games.XGGrid), so a real shared
    // reference doesn't invert the project-reference graph the way
    // PairLookupFailureCleaner's own duplicated copy had to (ADR-0052's
    // 2026-08-01 status note, XGArcade.Data.Seeding sitting below this
    // project). Do not duplicate this value instead of referencing it.
    internal const int PersistentFailureThreshold = 2;

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
        var pairsSkippedPersistentFailure = 0;
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
                // REQ-110 (2026-08-01 "persistent technical-failure
                // tracking" extension): checked only once the pair is
                // neither already-valid nor confirmed-low — see
                // PairLookupFailure's own doc comment and
                // PersistentFailureThreshold's own comment for the full
                // "why 2 consecutive runs, not 1" reasoning.
                else if (await playerStoreRepository.IsPersistentTechnicalFailureAsync(
                    NationalityAttributeType, country.Name, ClubAttributeType, club.Name, PersistentFailureThreshold, cancellationToken))
                {
                    pairsSkippedPersistentFailure++;
                    logger.LogDebug("{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
                        country.Name, club.Name, PersistentFailureThreshold);
                }
                else
                {
                    var hadTechnicalFailure = false;
                    var matches = await wikidataLookupService.LookupAndPersistAsync(
                        country, club, WikidataLookupOrigin.Sync, cancellationToken,
                        onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
                    pairsQueriedLive++;
                    if (hadTechnicalFailure)
                    {
                        pairsWithTechnicalFailure++;
                        failingPairs.Add($"{country.Name} x {club.Name}");
                        await playerStoreRepository.RecordTechnicalFailureAsync(
                            NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
                    }
                    else
                    {
                        // REQ-110: a real (possibly zero-match) answer — not
                        // a swallowed technical failure — so clear any prior
                        // run's failure marker (a no-op if this pair never
                        // failed before) and, if it's still below threshold,
                        // persist the confirmed-low marker for next run.
                        // matches.Count is the query's complete, un-LIMITed
                        // result set (implementation-document.md §6a), so
                        // it's the true current match count, not just
                        // "however many were new."
                        await playerStoreRepository.ClearTechnicalFailureAsync(
                            NationalityAttributeType, country.Name, ClubAttributeType, club.Name, cancellationToken);
                        if (matches.Count < options.MinValidAnswers)
                        {
                            await playerStoreRepository.RecordConfirmedLowAsync(
                                NationalityAttributeType, country.Name, ClubAttributeType, club.Name, matches.Count, cancellationToken);
                        }
                    }
                    logger.LogDebug("{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
                        country.Name, club.Name, matches.Count, cachedCount);
                }

                LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
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
                // REQ-110 (2026-08-01): see the Country x Club loop's own
                // comment above — same reasoning here. This is the loop
                // that actually needed this extension in practice — see
                // WikidataClient.BuildClubClubIntersectionQuery's own
                // comment for the specific club-club query-shape incident.
                else if (await playerStoreRepository.IsPersistentTechnicalFailureAsync(
                    ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, PersistentFailureThreshold, cancellationToken))
                {
                    pairsSkippedPersistentFailure++;
                    logger.LogDebug("{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
                        clubs[i].Name, clubs[j].Name, PersistentFailureThreshold);
                }
                else
                {
                    var hadTechnicalFailure = false;
                    var matches = await wikidataLookupService.LookupAndPersistClubClubAsync(
                        clubs[i], clubs[j], WikidataLookupOrigin.Sync, cancellationToken,
                        onTechnicalFailure: () => hadTechnicalFailure = true, timeoutTier: WikidataQueryTimeoutTier.CacheWarming);
                    pairsQueriedLive++;
                    if (hadTechnicalFailure)
                    {
                        pairsWithTechnicalFailure++;
                        failingPairs.Add($"{clubs[i].Name} x {clubs[j].Name}");
                        await playerStoreRepository.RecordTechnicalFailureAsync(
                            ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
                    }
                    else
                    {
                        // REQ-110: see the Country x Club loop's own comment
                        // above — same reasoning here.
                        await playerStoreRepository.ClearTechnicalFailureAsync(
                            ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, cancellationToken);
                        if (matches.Count < options.MinValidAnswers)
                        {
                            await playerStoreRepository.RecordConfirmedLowAsync(
                                ClubAttributeType, clubs[i].Name, ClubAttributeType, clubs[j].Name, matches.Count, cancellationToken);
                        }
                    }
                    logger.LogDebug("{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
                        clubs[i].Name, clubs[j].Name, matches.Count, cachedCount);
                }

                LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
            }
        }

        var result = new CacheWarmingResult(
            totalPairs, pairsQueriedLive, pairsAlreadyValid, pairsWithTechnicalFailure, failingPairs,
            pairsSkippedConfirmedLow, pairsSkippedPersistentFailure);

        // REQ-110: the failing-pairs list is logged in full here, at
        // Information level, exactly once per run — not per-pair (each
        // pair's own failure was already logged inside WikidataClient when
        // it happened, at Debug level as of 2026-08-01 — see
        // RunIntersectionQueryAsync's own comment on why). A comma-joined
        // string rather than one log call per pair, matching this method's
        // existing "coarse summary, not a per-pair stream" logging shape
        // (see ProgressLogInterval's own comment).
        logger.LogInformation(
            "Player cache warming complete: {TotalPairs} pairs checked, {PairsQueriedLive} queried live, {PairsAlreadyValid} already valid, " +
            "{PairsSkippedConfirmedLow} skipped as previously confirmed low, {PairsSkippedPersistentFailure} skipped as a persistent (2+ run) " +
            "technical failure, {PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
            "rather than a clean answer.{FailingPairsSuffix}",
            result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
            result.PairsSkippedPersistentFailure, result.PairsWithTechnicalFailure,
            result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);

        return result;
    }

    // REQ-110 (2026-08-01): includes the running technical-failure count so
    // a run that gets cancelled mid-way (this job's own 90-minute CI
    // timeout, or a manual cancellation) still leaves a useful trail in the
    // log — WarmAsync's own Information-level summary line never gets to
    // run if the process is killed first, so this periodic checkpoint is
    // the only signal an operator gets from an incomplete run.
    private void LogProgressCheckpoint(int pairsProcessed, int totalPairs, int pairsWithTechnicalFailure)
    {
        if (pairsProcessed % ProgressLogInterval == 0 || pairsProcessed == totalPairs)
            logger.LogInformation("Progress: {PairsProcessed}/{TotalPairs} pairs checked ({PairsWithTechnicalFailure} technical failures so far).",
                pairsProcessed, totalPairs, pairsWithTechnicalFailure);
    }
}
