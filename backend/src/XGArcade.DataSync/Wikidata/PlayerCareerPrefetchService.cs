using Microsoft.Extensions.Logging;
using XGArcade.Data.Entities;
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
//
// ADR-0069: also sweeps already-SEEDED clubs (CategoryValueRepository
// .GetClubsAsync), via IWikidataClient.QueryPlayerPoolByClubAsync — a
// fresh product decision that extends (not supersedes) ADR-0055's original
// nationality-only scope, so a player from an unseeded country who played
// for a seeded club is no longer invisible to this sweep. Symmetric to the
// country loop in every way: its own counters (clubsProcessed/clubsFailed),
// its own "no QID yet is a skip, not a failure" precedent, its own
// per-club try/catch isolating one club's failure from the rest — see the
// club loop below for the concrete mirror.
// S-106/S-107 (pure refactor): playerRepository carries
// GetOrCreatePlayersByWikidataQidAsync (split out of the original, now-
// deleted IPlayerStoreRepository); playerCareerStintRepository carries
// GetCareerStintsByPlayerIdsAsync/AddCareerStintsBatchAsync — see ADR-0067.
//
// REQ-110 follow-up (2026-08-18): playerAttributeRepository is new here —
// both sweeps now ALSO persist a PlayerAttribute row per pooled player
// (nationality for the country loop, club for the club loop), not just the
// Player/PlayerCareerStint rows they always wrote. Every player in a given
// country's/club's pool satisfies that attribute BY CONSTRUCTION of the
// pool query's own WHERE clause (QueryPlayerPoolByNationalityAsync/
// QueryPlayerPoolByClubAsync) — no separate Wikidata read-back is needed to
// know this. This is what lets PlayerCacheWarmingService's existing
// CountPlayersWithBothAttributesAsync pre-check
// (backend/src/XGArcade.Games.XGGrid/PlayerCacheWarmingService.cs) become
// the complete answer for a country/club pair once both sides have been
// swept, eliminating the live pairwise SPARQL intersection queries that
// otherwise time out on big-club combinations. PlayerCacheWarmingService
// itself is unchanged — its skip-logic just starts being right more often.
public class PlayerCareerPrefetchService(
    ICategoryValueRepository categoryValueRepository,
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerRepository playerRepository,
    IPlayerAttributeRepository playerAttributeRepository,
    IWikidataClient wikidataClient,
    ILogger<PlayerCareerPrefetchService> logger) : IPlayerCareerPrefetchService
{
    private const string NationalityAttributeType = "nationality";
    // Must stay byte-identical to WikidataLookupService's own
    // ClubAttributeType constant and to the club.Name value
    // PlayerCacheWarmingService's club loop uses — this is the exact string
    // CountPlayersWithBothAttributesAsync later matches against, so there is
    // no room for a second, potentially-divergent spelling.
    private const string ClubAttributeType = "club";

    // Conservative batch size for QueryPlayerCareerStintsByQidsAsync's VALUES
    // clause within one country's pool — same size PlayerPhotoBackfillService/
    // PlayerPositionBirthYearBackfillService already use, safely inside
    // implementation-document.md §6a's "few-thousand-row, no ORDER BY/LIMIT/
    // OFFSET" bounded-query class.
    public const int CareerBatchSize = 200;

    public async Task<PlayerCareerPrefetchResult> PrefetchAsync(CancellationToken cancellationToken = default)
    {
        var countries = await categoryValueRepository.GetCountriesAsync(cancellationToken);
        var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
        // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203
        // follow-up, ADR-0059): built once for the whole run, not once per
        // batch — ClubDefinition is small (~15 rows, hand-seeded,
        // MVP-SCOPE.md) and doesn't change mid-run, so there's no reason to
        // re-read it inside FetchAndPersistBatchAsync's per-batch loop. See
        // PlayerCareerStintRefreshService.BuildNewStintsByPlayerId's own doc
        // comment for what this map is used for.
        var clubNameByClubQid = await PlayerCareerStintRefreshService.BuildClubNameByClubQidAsync(categoryValueRepository, cancellationToken);

        var countriesProcessed = 0;
        var countriesFailed = 0;
        var clubsProcessed = 0;
        var clubsFailed = 0;
        var careerBatchesFailed = 0;
        var playersTouched = 0;
        var stintsAdded = 0;
        var attributesAdded = 0;
        var failedCountryNames = new List<string>();
        var failedClubNames = new List<string>();

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

            // REQ-110 follow-up: fetched once per country (not per batch/
            // player) — every player in `pool` satisfies this exact
            // nationality attribute by construction of the pool query's own
            // WHERE clause above, so the only remaining question is dedup
            // against what's already stored. Same "fetch once, HashSet.Add
            // as the dedup gate" pattern as WikidataLookupService.
            // PersistMatchesAsync's playerIdsWithAttributeA/B.
            var playerIdsWithNationality = (await playerAttributeRepository.GetPlayerAttributesAsync(
                    NationalityAttributeType, country.Name, cancellationToken))
                .Select(a => a.PlayerId)
                .ToHashSet();

            foreach (var batch in pool.Chunk(CareerBatchSize))
            {
                var (touched, added, attrsAdded, batchFailed) = await FetchAndPersistBatchAsync(
                    batch, clubNameByClubQid, NationalityAttributeType, country.Name, playerIdsWithNationality, cancellationToken);
                playersTouched += touched;
                stintsAdded += added;
                attributesAdded += attrsAdded;
                if (batchFailed)
                    careerBatchesFailed++;
            }

            logger.LogInformation(
                "prefetch-player-careers: {Country} done — pool of {PoolSize} player(s) processed " +
                "(running totals: {PlayersTouched} player(s) touched, {StintsAdded} stint(s) added, " +
                "{AttributesAdded} attribute(s) added).",
                country.Name, pool.Count, playersTouched, stintsAdded, attributesAdded);
        }

        // ADR-0069: symmetric to the country loop above — same
        // skip-if-no-QID, try/catch-isolate-one-failure, and running-totals
        // logging shape, just sourced from GetClubsAsync/QueryPlayerPoolByClubAsync
        // instead of GetCountriesAsync/QueryPlayerPoolByNationalityAsync.
        foreach (var club in clubs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Same REQ-109 "an unresolved QID isn't an error" precedent as
            // the country loop's own null-QID skip above.
            if (club.WikidataQid is null)
                continue;

            IReadOnlyList<WikidataNameIndexEntry> pool;
            try
            {
                pool = await wikidataClient.QueryPlayerPoolByClubAsync(club.WikidataQid, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                clubsFailed++;
                failedClubNames.Add(club.Name);
                logger.LogWarning(ex,
                    "prefetch-player-careers: {Club} failed; continuing with the remaining clubs. " +
                    "This run WILL fail at the end, but the job is idempotent — re-run it to fill in the failed clubs.",
                    club.Name);
                continue;
            }

            clubsProcessed++;
            if (pool.Count == 0)
                continue;

            // REQ-110 follow-up: same reasoning as the country loop's own
            // playerIdsWithNationality above, but for the "club" attribute
            // type — using club.Name (from the same ClubDefinition
            // clubNameByClubQid above is sourced from) so this is
            // byte-identical to what PlayerCacheWarmingService's club loop
            // and CountPlayersWithBothAttributesAsync's later match will use.
            var playerIdsWithClub = (await playerAttributeRepository.GetPlayerAttributesAsync(
                    ClubAttributeType, club.Name, cancellationToken))
                .Select(a => a.PlayerId)
                .ToHashSet();

            foreach (var batch in pool.Chunk(CareerBatchSize))
            {
                var (touched, added, attrsAdded, batchFailed) = await FetchAndPersistBatchAsync(
                    batch, clubNameByClubQid, ClubAttributeType, club.Name, playerIdsWithClub, cancellationToken);
                playersTouched += touched;
                stintsAdded += added;
                attributesAdded += attrsAdded;
                if (batchFailed)
                    careerBatchesFailed++;
            }

            logger.LogInformation(
                "prefetch-player-careers: {Club} done — pool of {PoolSize} player(s) processed " +
                "(running totals: {PlayersTouched} player(s) touched, {StintsAdded} stint(s) added, " +
                "{AttributesAdded} attribute(s) added).",
                club.Name, pool.Count, playersTouched, stintsAdded, attributesAdded);
        }

        if (countriesFailed > 0 || clubsFailed > 0 || careerBatchesFailed > 0)
        {
            throw new InvalidOperationException(
                $"prefetch-player-careers: {countriesFailed} countr{(countriesFailed == 1 ? "y" : "ies")} " +
                $"failed to fetch their player pool ({string.Join(", ", failedCountryNames)}), " +
                $"{clubsFailed} club(s) failed to fetch their player pool ({string.Join(", ", failedClubNames)}), and " +
                $"{careerBatchesFailed} career-fetch batch(es) failed. {playersTouched} player(s) were still " +
                "touched and " + $"{stintsAdded} stint(s) added and {attributesAdded} attribute(s) added " +
                "from what succeeded; the job is idempotent — re-run it to retry what failed.");
        }

        return new PlayerCareerPrefetchResult(
            countriesProcessed, playersTouched, stintsAdded, countriesFailed, careerBatchesFailed, clubsProcessed, clubsFailed, attributesAdded);
    }

    // Returns whether the career-fetch step itself failed (distinct from
    // "fetched but found nothing," which is a normal, non-failure outcome)
    // so the caller's loop can keep a separate failure tally without this
    // method needing to throw and unwind the whole country's remaining
    // batches over one batch's failure.
    //
    // REQ-110 follow-up: attributeType/attributeValue/playerIdsWithAttribute
    // describe THIS pool's own attribute (nationality+country.Name for the
    // country loop, club+club.Name for the club loop) — every player in
    // `batch` gets that attribute queued, deduped against
    // playerIdsWithAttribute, which the caller built once per country/club
    // (not per batch) and passes by reference so dedup state accumulates
    // correctly across a pool's multiple CareerBatchSize-sized batches.
    private async Task<(int PlayersTouched, int StintsAdded, int AttributesAdded, bool BatchFailed)> FetchAndPersistBatchAsync(
        IReadOnlyList<WikidataNameIndexEntry> batch,
        IReadOnlyDictionary<string, string> clubNameByClubQid,
        string attributeType,
        string attributeValue,
        HashSet<Guid> playerIdsWithAttribute,
        CancellationToken cancellationToken)
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
        var playersByQid = await playerRepository.GetOrCreatePlayersByWikidataQidAsync(requests, cancellationToken);

        // REQ-110 follow-up: every player just fetched/created for this
        // batch satisfies attributeType/attributeValue by construction of
        // the pool query that produced `batch` — queue+persist that fact
        // now, deduped against playerIdsWithAttribute (shared across this
        // pool's whole set of batches), regardless of whether the
        // career-fetch step below succeeds or fails. This intentionally
        // runs before the try/catch so a career-fetch batch failure never
        // costs the (unrelated, purely local) attribute write.
        var attributesToAdd = new List<PlayerAttribute>();
        foreach (var player in playersByQid.Values.Select(r => r.Player))
        {
            if (playerIdsWithAttribute.Add(player.Id))
                attributesToAdd.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = attributeType, AttributeValue = attributeValue });
        }
        if (attributesToAdd.Count > 0)
            await playerAttributeRepository.AddPlayerAttributesBatchAsync(attributesToAdd, cancellationToken);

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
            return (playersByQid.Count, 0, attributesToAdd.Count, true);
        }

        if (stintsByQid.Count == 0)
            return (playersByQid.Count, 0, attributesToAdd.Count, false);

        var qidToPlayerId = playersByQid.ToDictionary(kv => kv.Key, kv => kv.Value.Player.Id);
        var affectedPlayerIds = stintsByQid.Keys.Select(qid => qidToPlayerId[qid]).ToList();
        var existingStintsByPlayerId = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(affectedPlayerIds, cancellationToken);

        var newStintsByPlayerId = PlayerCareerStintRefreshService.BuildNewStintsByPlayerId(
            stintsByQid, qidToPlayerId, existingStintsByPlayerId, clubNameByClubQid);

        if (newStintsByPlayerId.Count > 0)
            await playerCareerStintRepository.AddCareerStintsBatchAsync(newStintsByPlayerId, cancellationToken);

        return (playersByQid.Count, newStintsByPlayerId.Sum(kv => kv.Value.Count), attributesToAdd.Count, false);
    }
}
