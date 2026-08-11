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
// Grid lookup can become targets at all) is ADR-0055's job
// (PlayerCareerPrefetchService, which shares this class's reconciliation
// logic below — see BuildNewStintsByPlayerId's own doc comment).
//
// categoryValueRepository (bug fix, 2026-08-04, xG Path duplicate-node bug,
// REQ-1203 follow-up, see ADR-0059): added purely to build the QID -> seeded
// ClubDefinition.Name canonicalization map BuildNewStintsByPlayerId now
// needs — see that method's own doc comment for the full "why." ClubDefinition
// is a small, hand-seeded (~15 rows, MVP-SCOPE.md) reference table, so
// re-reading it once per refresh call is not a perf concern the way
// PlayerCareerStint's ~608K rows are.
// S-106/S-107 (pure refactor): playerRepository carries
// GetPlayersByIdsAsync (split out of the original, now-deleted
// IPlayerStoreRepository); playerCareerStintRepository carries
// GetCareerStintsByPlayerIdsAsync/AddCareerStintsBatchAsync — see ADR-0067.
public class PlayerCareerStintRefreshService(
    IWikidataClient wikidataClient,
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerRepository playerRepository,
    ICategoryValueRepository categoryValueRepository,
    ILogger<PlayerCareerStintRefreshService> logger) : IPlayerCareerStintRefreshService
{
    public async Task RefreshCareerStintsAsync(IReadOnlyList<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return;

        var players = await playerRepository.GetPlayersByIdsAsync(playerIds, cancellationToken);

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

        var affectedPlayerIds = stintsByQid.Keys.Select(qid => qidToPlayerId[qid]).ToList();
        var existingStintsByPlayerId = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(affectedPlayerIds, cancellationToken);
        var clubNameByClubQid = await BuildClubNameByClubQidAsync(categoryValueRepository, cancellationToken);

        var newStintsByPlayerId = BuildNewStintsByPlayerId(stintsByQid, qidToPlayerId, existingStintsByPlayerId, clubNameByClubQid);

        if (newStintsByPlayerId.Count > 0)
            await playerCareerStintRepository.AddCareerStintsBatchAsync(newStintsByPlayerId, cancellationToken);
    }

    // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
    // ADR-0059): QID -> seeded ClubDefinition.Name, built once per call
    // (this class's own RefreshCareerStintsAsync) or once per prefetch run
    // (PlayerCareerPrefetchService.PrefetchAsync — see that class's own
    // comment for why it's hoisted out of the per-batch loop there) rather
    // than re-derived per stint. Static (takes ICategoryValueRepository as a
    // parameter, not instance state) and internal, not private, for the
    // same shared-assembly reason as BuildNewStintsByPlayerId below — both
    // callers need this without instantiating PlayerCareerStintRefreshService
    // itself. WikidataQid is nullable on ClubDefinition (not yet resolved
    // for every seeded club) and is not guaranteed unique by a database
    // constraint, so the last club wins on a QID collision — not expected
    // in practice for a hand-seeded ~15-row table, and no worse than
    // silently picking one arbitrarily.
    internal static async Task<IReadOnlyDictionary<string, string>> BuildClubNameByClubQidAsync(
        ICategoryValueRepository categoryValueRepository, CancellationToken cancellationToken)
    {
        var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
        return clubs
            .Where(c => c.WikidataQid is not null)
            .ToDictionary(c => c.WikidataQid!, c => c.Name);
    }

    // ADR-0055: extracted so PlayerCareerPrefetchService's bulk job can share
    // the exact same dedup-against-what's-already-stored logic RefreshCareerStintsAsync
    // uses above, rather than a second, easy-to-drift-apart copy. Same
    // "additive only, never a wipe-and-replace" discipline as
    // WikidataLookupService.PersistCareerStintsAsync — a previously wrong
    // stint is not this method's concern (see ADR-0054's Consequences
    // section). Internal, not private: XGArcade.DataSync is a shared
    // assembly between the two callers.
    //
    // clubNameByClubQid (bug fix, 2026-08-04, xG Path duplicate-node bug,
    // REQ-1203 follow-up, ADR-0059): canonicalizes each fetched stint's
    // ClubName to the matching seeded ClubDefinition.Name whenever the
    // stint's ClubQid resolves in this map — the same club, reached via
    // xG Grid's byproduct persistence (WikidataLookupService.PersistCareerStintsAsync,
    // which already writes ClubDefinition.Name directly and needs no change)
    // vs. this class's own full-career fetch (raw ?clubLabel, only ever
    // suffix-normalized), previously could and did diverge whenever
    // Wikidata's own preferred label for a QID differs from this codebase's
    // hand-picked seed name by more than a legal-suffix token (e.g. "Lyon"
    // vs. "Olympique Lyonnais", both valid labels for the same QID) —
    // producing two differently-named PlayerCareerStint rows for what is
    // really one real stint, surfaced as two xG Path club-reveal nodes. A
    // stint whose ClubQid is null (defensive-only case, see
    // WikidataClient.ParseCareerStintBindings) or doesn't match any seeded
    // club (a genuinely unseeded club) keeps its best-effort, suffix-
    // normalized label unchanged — still useful for xG Path's own display
    // and for ClubGapAuditService's gap-detection query, which this
    // canonicalization does not touch.
    internal static Dictionary<Guid, IReadOnlyList<PlayerCareerStint>> BuildNewStintsByPlayerId(
        IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> stintsByQid,
        IReadOnlyDictionary<string, Guid> qidToPlayerId,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>> existingStintsByPlayerId,
        IReadOnlyDictionary<string, string> clubNameByClubQid)
    {
        var newStintsByPlayerId = new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>();
        foreach (var (qid, fetchedStints) in stintsByQid)
        {
            var playerId = qidToPlayerId[qid];
            var seenTuples = existingStintsByPlayerId.TryGetValue(playerId, out var existingStints)
                ? existingStints.Select(s => (s.ClubName, s.StartYear, s.EndYear, s.AppearanceCount)).ToHashSet()
                : [];

            var newStints = new List<PlayerCareerStint>();
            foreach (var s in fetchedStints)
            {
                var clubName = s.ClubQid is not null && clubNameByClubQid.TryGetValue(s.ClubQid, out var canonicalName)
                    ? canonicalName
                    : s.ClubName;

                if (!seenTuples.Add((clubName, s.StartYear, s.EndYear, s.AppearanceCount)))
                    continue;

                newStints.Add(new PlayerCareerStint
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    ClubName = clubName,
                    StartYear = s.StartYear,
                    EndYear = s.EndYear,
                    AppearanceCount = s.AppearanceCount,
                    // Resolved by IPlayerStoreRepository.AddCareerStintsBatchAsync
                    // across the player's full stint set — this placeholder is
                    // always overwritten before SaveChangesAsync.
                    SequenceOrder = 0,
                });
            }

            if (newStints.Count > 0)
                newStintsByPlayerId[playerId] = newStints;
        }

        return newStintsByPlayerId;
    }
}
