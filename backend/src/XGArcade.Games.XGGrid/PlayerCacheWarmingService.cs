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
// reasoning as GridGenerationService.PickHeadersAsync (see that class's own
// doc comment; PickHeadersAsync moved there from GridGameModule in S-119's
// pure refactor). This service doesn't share a request-scoped context (its CLI
// caller builds a single-use one), but nothing here is safe for
// concurrent use of a single DbContext instance regardless of scope.
//
// Idempotent and safe to re-run: skips any pair already at or above
// MinValidAnswers (a fast, cache-only read) rather than re-querying
// Wikidata for data that can't have changed. A pair cached BELOW
// MinValidAnswers is, as of the 2026-07-28 "persisted confirmed-low
// signal" extension below, ALSO skipped once a prior run has confirmed it
// — see the ConfirmedLowMatchPair check in SweepPairsAsync below and that
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
// warm-grid-cache.yml's own external, orchestration-level retry (a
// process-crash-survival retry around the whole job) is unaffected — a
// different, complementary layer from this now-removed single-pair,
// same-process retry.
//
// REQ-110/ADR-0078/S-160 (2026-08-18) — confirmed-low-from-sweep
// short-circuit: once PlayerCareerPrefetchService has fully swept BOTH
// sides of a pair (CountryDefinition.PlayerPoolSweptAt/
// ClubDefinition.PlayerPoolSweptAt both non-null), this class's own
// cachedCount is no longer a partial cache hint — it is the true, final
// match count for that pair, because the pool sweep that produced each
// side was itself unfiltered and complete. WarmAsync checks this BEFORE
// IsConfirmedLowAsync/IsPersistentTechnicalFailureAsync/the live-query
// chain and, when both sides are swept, calls RecordConfirmedLowAsync
// directly with cachedCount — no live Wikidata round-trip. Deliberately
// requires BOTH sides swept, never just one: a partial pool on either side
// means the true count is still unknown. See ADR-0078 for the full
// decision, including its "For AI agents" section on why
// StaleClubAttributeCleaner/purge-player-pool MUST also invalidate
// PlayerPoolSweptAt, not just PlayerAttribute/ConfirmedLowMatchPair/
// PairLookupFailure.
public class PlayerCacheWarmingService(
    ICategoryValueRepository categoryValueRepository,
    // S-106/S-107 (pure refactor): the original, now-deleted
    // IPlayerStoreRepository's ConfirmedLowMatchPair/PairLookupFailure
    // methods now live on IPlayerDataQualityRepository — see ADR-0067.
    IPlayerDataQualityRepository playerDataQualityRepository,
    IPlayerAttributeRepository playerAttributeRepository,
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
    // internal, not private: GridLiveLookupDispatcher.TryRefreshCellAsync
    // (2026-08-10; moved from GridGameModule.RefreshCellFromLiveLookupAsync
    // in S-119's pure refactor) reuses this exact value so a guess-time fallback call
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

        var failingPairs = new List<string>();

        var countryClubOutcome = await SweepCountryClubPairsAsync(
            countries, clubs, new SweepPairsOutcome(), failingPairs, totalPairs, cancellationToken);
        var clubClubOutcome = await SweepClubClubPairsAsync(
            clubs, countryClubOutcome, failingPairs, totalPairs, cancellationToken);

        var result = new CacheWarmingResult(
            totalPairs, clubClubOutcome.PairsQueriedLive, clubClubOutcome.PairsAlreadyValid, clubClubOutcome.PairsWithTechnicalFailure, failingPairs,
            clubClubOutcome.PairsSkippedConfirmedLow, clubClubOutcome.PairsSkippedPersistentFailure, clubClubOutcome.PairsConfirmedLowFromSweep);

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
            "technical failure, {PairsConfirmedLowFromSweep} confirmed low from a fully-swept pool with zero live query (ADR-0078), " +
            "{PairsWithTechnicalFailure} of the queried-live pairs hit a technical failure (timeout/HTTP/parse error) " +
            "rather than a clean answer.{FailingPairsSuffix}",
            result.TotalPairs, result.PairsQueriedLive, result.PairsAlreadyValid, result.PairsSkippedConfirmedLow,
            result.PairsSkippedPersistentFailure, result.PairsConfirmedLowFromSweep, result.PairsWithTechnicalFailure,
            result.FailingPairs.Count > 0 ? $" Failing pairs: {string.Join(", ", result.FailingPairs)}." : string.Empty);

        return result;
    }

    // S-166: running totals threaded through both SweepPairsAsync calls
    // below (same "starting totals continue across sweeps" shape as S-165's
    // SweepOutcome) — WarmAsync's summary and LogProgressCheckpoint need
    // cumulative totals across both sweeps, not two separate counts.
    private readonly record struct SweepPairsOutcome(
        int PairsProcessed, int PairsQueriedLive, int PairsAlreadyValid, int PairsSkippedConfirmedLow,
        int PairsSkippedPersistentFailure, int PairsConfirmedLowFromSweep, int PairsWithTechnicalFailure);

    // S-166: supplies what genuinely differs from the Club x Club sweep
    // below to shared SweepPairsAsync. SelectMany matches the original
    // nested foreach's country-outer/club-inner pair order.
    private Task<SweepPairsOutcome> SweepCountryClubPairsAsync(
        IReadOnlyList<CountryDefinition> countries, IReadOnlyList<ClubDefinition> clubs,
        SweepPairsOutcome starting, List<string> failingPairs, int totalPairs, CancellationToken cancellationToken) =>
        SweepPairsAsync(
            countries.SelectMany(country => clubs.Select(club => (country, club))),
            NationalityAttributeType, (CountryDefinition c) => c.Name, (CountryDefinition c) => c.PlayerPoolSweptAt,
            ClubAttributeType, (ClubDefinition c) => c.Name, (ClubDefinition c) => c.PlayerPoolSweptAt,
            lookupAsync: (country, club, onTechnicalFailure, ct) => wikidataLookupService.LookupAndPersistAsync(
                country, club, WikidataLookupOrigin.Sync, ct,
                onTechnicalFailure: onTechnicalFailure, timeoutTier: WikidataQueryTimeoutTier.CacheWarming),
            logConfirmedLowFromSweep: (country, club, cachedCount) => logger.LogDebug(
                "{Country} x {Club}: confirmed low from sweep — both sides fully swept, cached count ({CachedCount}) is final, no live query needed.",
                country.Name, club.Name, cachedCount),
            logSkippedConfirmedLow: (country, club) => logger.LogDebug(
                "{Country} x {Club}: skipped — previously confirmed below MinValidAnswers.",
                country.Name, club.Name),
            logSkippedPersistentFailure: (country, club) => logger.LogDebug(
                "{Country} x {Club}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
                country.Name, club.Name, PersistentFailureThreshold),
            logQueriedLive: (country, club, matchCount, cachedCount) => logger.LogDebug(
                "{Country} x {Club}: {MatchCount} matches (was {CachedCount} cached).",
                country.Name, club.Name, matchCount, cachedCount),
            failingPairLabel: (country, club) => $"{country.Name} x {club.Name}",
            failingPairs, totalPairs, starting, cancellationToken);

    // S-166: mirrors SweepCountryClubPairsAsync above. Builds unique
    // (clubs[i], clubs[j]) pairs with j > i, matching the original nested
    // for-loop's pair order; `starting` continues the running totals.
    private Task<SweepPairsOutcome> SweepClubClubPairsAsync(
        IReadOnlyList<ClubDefinition> clubs, SweepPairsOutcome starting,
        List<string> failingPairs, int totalPairs, CancellationToken cancellationToken) =>
        SweepPairsAsync(
            clubs.SelectMany((clubA, i) => clubs.Skip(i + 1).Select(clubB => (clubA, clubB))),
            ClubAttributeType, (ClubDefinition c) => c.Name, (ClubDefinition c) => c.PlayerPoolSweptAt,
            ClubAttributeType, (ClubDefinition c) => c.Name, (ClubDefinition c) => c.PlayerPoolSweptAt,
            lookupAsync: (clubA, clubB, onTechnicalFailure, ct) => wikidataLookupService.LookupAndPersistClubClubAsync(
                clubA, clubB, WikidataLookupOrigin.Sync, ct,
                onTechnicalFailure: onTechnicalFailure, timeoutTier: WikidataQueryTimeoutTier.CacheWarming),
            logConfirmedLowFromSweep: (clubA, clubB, cachedCount) => logger.LogDebug(
                "{ClubA} x {ClubB}: confirmed low from sweep — both sides fully swept, cached count ({CachedCount}) is final, no live query needed.",
                clubA.Name, clubB.Name, cachedCount),
            logSkippedConfirmedLow: (clubA, clubB) => logger.LogDebug(
                "{ClubA} x {ClubB}: skipped — previously confirmed below MinValidAnswers.",
                clubA.Name, clubB.Name),
            logSkippedPersistentFailure: (clubA, clubB) => logger.LogDebug(
                "{ClubA} x {ClubB}: skipped — {Threshold}+ consecutive run failures, treated as a structural query failure.",
                clubA.Name, clubB.Name, PersistentFailureThreshold),
            logQueriedLive: (clubA, clubB, matchCount, cachedCount) => logger.LogDebug(
                "{ClubA} x {ClubB}: {MatchCount} matches (was {CachedCount} cached).",
                clubA.Name, clubB.Name, matchCount, cachedCount),
            failingPairLabel: (clubA, clubB) => $"{clubA.Name} x {clubB.Name}",
            failingPairs, totalPairs, starting, cancellationToken);

    // S-166: shared "check cache -> confirmed-low-from-sweep -> confirmed-low
    // -> persistent-failure -> live lookup" decision tree both loops in
    // WarmAsync used to duplicate end to end. The delegate params are
    // exactly the genuine per-sweep differences (which two AttributeType/
    // name pairs, which LookupAndPersist* method, each log line's wording).
    private async Task<SweepPairsOutcome> SweepPairsAsync<TLeft, TRight>(
        IEnumerable<(TLeft Left, TRight Right)> pairs,
        string attributeTypeA, Func<TLeft, string> nameA, Func<TLeft, DateTime?> sweptAtA,
        string attributeTypeB, Func<TRight, string> nameB, Func<TRight, DateTime?> sweptAtB,
        Func<TLeft, TRight, Action, CancellationToken, Task<IReadOnlyList<Player>>> lookupAsync,
        Action<TLeft, TRight, int> logConfirmedLowFromSweep,
        Action<TLeft, TRight> logSkippedConfirmedLow,
        Action<TLeft, TRight> logSkippedPersistentFailure,
        Action<TLeft, TRight, int, int> logQueriedLive,
        Func<TLeft, TRight, string> failingPairLabel,
        List<string> failingPairs, int totalPairs, SweepPairsOutcome starting, CancellationToken cancellationToken)
    {
        var pairsProcessed = starting.PairsProcessed;
        var pairsQueriedLive = starting.PairsQueriedLive;
        var pairsAlreadyValid = starting.PairsAlreadyValid;
        var pairsSkippedConfirmedLow = starting.PairsSkippedConfirmedLow;
        var pairsSkippedPersistentFailure = starting.PairsSkippedPersistentFailure;
        var pairsConfirmedLowFromSweep = starting.PairsConfirmedLowFromSweep;
        var pairsWithTechnicalFailure = starting.PairsWithTechnicalFailure;

        foreach (var (left, right) in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pairsProcessed++;

            var cachedCount = await playerAttributeRepository.CountPlayersWithBothAttributesAsync(
                attributeTypeA, nameA(left), attributeTypeB, nameB(right), cancellationToken);
            if (cachedCount >= options.MinValidAnswers)
            {
                pairsAlreadyValid++;
            }
            // REQ-110/ADR-0078/S-160: both sides fully swept -> cachedCount
            // is final, zero Wikidata round-trip. See this class's own top
            // comment for the full "why" (ADR-0078's "For AI agents"
            // section in particular).
            else if (sweptAtA(left) is not null && sweptAtB(right) is not null)
            {
                pairsConfirmedLowFromSweep++;
                await playerDataQualityRepository.RecordConfirmedLowAsync(
                    attributeTypeA, nameA(left), attributeTypeB, nameB(right), cachedCount, cancellationToken);
                logConfirmedLowFromSweep(left, right, cachedCount);
            }
            // REQ-110 (2026-07-28): a prior run already confirmed this pair
            // below threshold — see ConfirmedLowMatchPair's own doc comment.
            else if (await playerDataQualityRepository.IsConfirmedLowAsync(
                attributeTypeA, nameA(left), attributeTypeB, nameB(right), cancellationToken))
            {
                pairsSkippedConfirmedLow++;
                logSkippedConfirmedLow(left, right);
            }
            // REQ-110 (2026-08-01, ADR-0052): see PersistentFailureThreshold's
            // own comment for the full "why 2 consecutive runs, not 1" reasoning.
            else if (await playerDataQualityRepository.IsPersistentTechnicalFailureAsync(
                attributeTypeA, nameA(left), attributeTypeB, nameB(right), PersistentFailureThreshold, cancellationToken))
            {
                pairsSkippedPersistentFailure++;
                logSkippedPersistentFailure(left, right);
            }
            else
            {
                var hadTechnicalFailure = false;
                var matches = await lookupAsync(left, right, () => hadTechnicalFailure = true, cancellationToken);
                pairsQueriedLive++;
                if (hadTechnicalFailure)
                {
                    pairsWithTechnicalFailure++;
                    failingPairs.Add(failingPairLabel(left, right));
                    await playerDataQualityRepository.RecordTechnicalFailureAsync(
                        attributeTypeA, nameA(left), attributeTypeB, nameB(right), cancellationToken);
                }
                else
                {
                    // REQ-110: a real (possibly zero-match) answer — clear
                    // any prior failure marker and, if still below
                    // threshold, persist a fresh confirmed-low marker.
                    await playerDataQualityRepository.ClearTechnicalFailureAsync(
                        attributeTypeA, nameA(left), attributeTypeB, nameB(right), cancellationToken);
                    if (matches.Count < options.MinValidAnswers)
                    {
                        await playerDataQualityRepository.RecordConfirmedLowAsync(
                            attributeTypeA, nameA(left), attributeTypeB, nameB(right), matches.Count, cancellationToken);
                    }
                }
                logQueriedLive(left, right, matches.Count, cachedCount);
            }

            LogProgressCheckpoint(pairsProcessed, totalPairs, pairsWithTechnicalFailure);
        }

        return new SweepPairsOutcome(
            pairsProcessed, pairsQueriedLive, pairsAlreadyValid, pairsSkippedConfirmedLow,
            pairsSkippedPersistentFailure, pairsConfirmedLowFromSweep, pairsWithTechnicalFailure);
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
