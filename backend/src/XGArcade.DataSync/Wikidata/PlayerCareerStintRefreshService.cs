using Microsoft.Extensions.Logging;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// ADR-0054: called from XGPathGameModule.GenerateInstanceAsync, once per
// generated instance, for exactly the N players it just picked as targets —
// deliberately NOT a bulk/background job like PlayerCacheWarmingService or
// PlayerPhotoBackfillService, and deliberately NOT a widening of xG Path's
// candidate POOL (which still comes entirely from GetEligiblePlayerIdsAsync's
// existing PlayerCareerStint-byproduct read, unchanged by this class). This
// only makes an already-selected target's OWN puzzle data complete — it
// cannot make a previously-ineligible player newly eligible for THIS
// generation, since eligibility is decided before this service ever runs.
// Widening the candidate pool itself (so players who never triggered an xG
// Grid lookup can become targets at all) is a separate, larger decision —
// see ADR-0054's own "Alternatives considered"/Follow-up sections, not
// assumed or attempted here.
public class PlayerCareerStintRefreshService(
    IWikidataClient wikidataClient,
    IPlayerStoreRepository playerStore,
    ILogger<PlayerCareerStintRefreshService> logger) : IPlayerCareerStintRefreshService
{
    public async Task RefreshCareerStintsAsync(IReadOnlyList<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return;

        var players = await playerStore.GetPlayersByIdsAsync(playerIds, cancellationToken);

        // REQ-109's "an unresolved QID isn't an error" reasoning, applied
        // here: a Player row created before REQ-214/WikidataQid existed, or
        // one whose QID is simply missing, is skipped rather than failing
        // the whole batch — same "one bad row costs only that row" shape as
        // PlayerPhotoBackfillService's malformed-QID filtering.
        var qidToPlayerId = players.Values
            .Where(p => p.WikidataQid is not null && WikidataQid.IsValid(p.WikidataQid))
            .ToDictionary(p => p.WikidataQid!, p => p.Id);

        if (qidToPlayerId.Count == 0)
            return;

        IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> stintsByQid;
        try
        {
            stintsByQid = await wikidataClient.QueryPlayerCareerStintsByQidsAsync(qidToPlayerId.Keys.ToList(), cancellationToken);
        }
        catch (WikidataQueryException ex)
        {
            // Never propagates — see IPlayerCareerStintRefreshService's own
            // doc comment. The affected players simply keep whatever
            // PlayerCareerStint rows they already had (xG Grid byproduct
            // data, possibly incomplete) for this round; the next round that
            // happens to pick them as a target tries again.
            logger.LogWarning(ex,
                "xg-path career-stint refresh: batch of {PlayerCount} player(s) failed; " +
                "these players keep whatever career data they already had for this round.",
                qidToPlayerId.Count);
            return;
        }

        if (stintsByQid.Count == 0)
            return;

        // Same dedup-against-what's-already-stored discipline as
        // WikidataLookupService.PersistCareerStintsAsync — this call is
        // additive (it can only ever ADD stints Wikidata's full-career
        // response reveals that xG Grid's narrower byproduct queries never
        // happened to discover), never a wipe-and-replace. A previously
        // wrong stint is not this method's concern (see ADR-0054's
        // Consequences section).
        var affectedPlayerIds = stintsByQid.Keys.Select(qid => qidToPlayerId[qid]).ToList();
        var existingStintsByPlayerId = await playerStore.GetCareerStintsByPlayerIdsAsync(affectedPlayerIds, cancellationToken);

        var newStintsByPlayerId = new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>();
        foreach (var (qid, fetchedStints) in stintsByQid)
        {
            var playerId = qidToPlayerId[qid];
            var seenTuples = existingStintsByPlayerId.TryGetValue(playerId, out var existingStints)
                ? existingStints.Select(s => (s.ClubName, s.StartYear, s.EndYear, s.AppearanceCount)).ToHashSet()
                : [];

            var newStints = fetchedStints
                .Where(s => seenTuples.Add((s.ClubName, s.StartYear, s.EndYear, s.AppearanceCount)))
                .Select(s => new PlayerCareerStint
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    ClubName = s.ClubName,
                    StartYear = s.StartYear,
                    EndYear = s.EndYear,
                    AppearanceCount = s.AppearanceCount,
                    // Resolved by IPlayerStoreRepository.AddCareerStintsBatchAsync
                    // across the player's full stint set — this placeholder is
                    // always overwritten before SaveChangesAsync.
                    SequenceOrder = 0,
                })
                .ToList();

            if (newStints.Count > 0)
                newStintsByPlayerId[playerId] = newStints;
        }

        if (newStintsByPlayerId.Count > 0)
            await playerStore.AddCareerStintsBatchAsync(newStintsByPlayerId, cancellationToken);
    }
}
