using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGConnect;

// S-211/REQ-1404: see IPlayerCareerOverlapService's own doc comment for the
// "why here, why generic" framing. Implements ADR-0010/0011's "fetch once,
// cache forever" philosophy: a player's PlayerCareerStint rows are trusted
// once at least one exists, and a live Wikidata refresh only ever runs for a
// player with zero cached rows.
public class PlayerCareerOverlapService(
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerRepository playerRepository,
    IWikidataClient wikidataClient) : IPlayerCareerOverlapService
{
    public async Task<bool> HaveSharedClubOverlapAsync(
        Guid playerAId, Guid playerBId, CancellationToken cancellationToken = default)
    {
        var stintsByPlayerId = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(
            [playerAId, playerBId], cancellationToken);

        var playersNeedingRefresh = new List<Guid>();
        if (!HasStints(stintsByPlayerId, playerAId))
            playersNeedingRefresh.Add(playerAId);
        if (!HasStints(stintsByPlayerId, playerBId))
            playersNeedingRefresh.Add(playerBId);

        if (playersNeedingRefresh.Count > 0)
        {
            // One batched Wikidata call even when BOTH players need a
            // refresh — same "fetch once" discipline
            // PlayerCareerStintRefreshService applies per generated xG Path
            // instance, just scoped here to at most two players.
            await RefreshCareerStintsAsync(playersNeedingRefresh, cancellationToken);
            stintsByPlayerId = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(
                [playerAId, playerBId], cancellationToken);
        }

        var stintsA = stintsByPlayerId.TryGetValue(playerAId, out var a) ? a : (IReadOnlyList<PlayerCareerStint>)[];
        var stintsB = stintsByPlayerId.TryGetValue(playerBId, out var b) ? b : (IReadOnlyList<PlayerCareerStint>)[];

        // Exact, case-insensitive ClubName match + a true two-range interval
        // overlap test — EndYear == null means "ongoing," treated as
        // unbounded/open going forward, mirroring
        // PathCareerStintFilter.IsInferredLoan's own null-handling
        // convention for this codebase's StartYear/EndYear fields (adapted
        // here into a genuine two-range intersection, not a single-player
        // containment check).
        //
        // Deliberate simplification vs. PlayerCareerStintRefreshService's
        // own club-name canonicalization (that method maps a fetched raw
        // Wikidata label to the matching seeded ClubDefinition.Name via
        // ICategoryValueRepository, ADR-0059) — this service does NOT
        // canonicalize freshly-fetched ClubName values, since
        // PlayerCareerStintRefreshService's own canonicalization helper is
        // `internal` to XGArcade.DataSync (not visible from this assembly)
        // and re-deriving it here would mean forking, not reusing, that
        // logic — exactly what this story was told not to do. In practice
        // this only risks a false NEGATIVE (missing a real connection
        // because two rows for the same real club carry differently-worded
        // labels), never a false positive, and is the same class of
        // best-effort, expected-to-need-iteration heuristic this codebase
        // already accepts elsewhere (see PathCareerStintFilter's own
        // national-team/B-team heuristics). Flagged here for a follow-up if
        // real false negatives surface in practice.
        return stintsA.Any(sa => stintsB.Any(sb =>
            string.Equals(sa.ClubName, sb.ClubName, StringComparison.OrdinalIgnoreCase) &&
            sa.StartYear <= (sb.EndYear ?? int.MaxValue) &&
            sb.StartYear <= (sa.EndYear ?? int.MaxValue)));
    }

    private static bool HasStints(
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>> stintsByPlayerId, Guid playerId) =>
        stintsByPlayerId.TryGetValue(playerId, out var stints) && stints.Count > 0;

    // Mirrors PlayerCareerStintRefreshService's own QID-mapping/fetch shape
    // (ADR-0054) — same "resolve Player.WikidataQid, skip missing/invalid
    // ones, fetch via IWikidataClient.QueryPlayerCareerStintsByQidsAsync"
    // steps — but is deliberately NOT a call to that service directly.
    // PlayerCareerStintRefreshService.RefreshCareerStintsAsync's own
    // documented contract is "Never throws" (it catches
    // WikidataQueryException internally and logs a warning, per its own doc
    // comment and implementation) — calling it here would make a genuine
    // live-lookup failure indistinguishable from "this player really has no
    // Wikidata career data," which is exactly the ambiguity REQ-1404's
    // LiveLookupUnavailable outcome exists to avoid. This method calls
    // IWikidataClient directly instead, letting WikidataQueryException
    // propagate, and persists via
    // IPlayerCareerStintRepository.AddCareerStintsBatchAsync exactly like
    // that service does for its own insert path.
    //
    // Reconciliation against already-stored rows (no-op/insert/complete
    // decisions, PlayerCareerStintRefreshService.BuildNewStintsByPlayerId's
    // own job) is deliberately skipped here: this method is only ever called
    // for a player with ZERO existing PlayerCareerStint rows (see the
    // caller above), so there is nothing to reconcile against — every
    // fetched stint is a straightforward new row.
    private async Task RefreshCareerStintsAsync(IReadOnlyList<Guid> playerIds, CancellationToken cancellationToken)
    {
        var players = await playerRepository.GetPlayersByIdsAsync(playerIds, cancellationToken);

        // Same "an unresolved QID isn't an error, just skip that row" reasoning as
        // PlayerCareerStintRefreshService.RefreshCareerStintsAsync's own filter.
        var qidToPlayerId = players.Values
            .Where(p => p.WikidataQid is not null && WikidataQid.IsValid(p.WikidataQid))
            .ToDictionary(p => p.WikidataQid!, p => p.Id);

        if (qidToPlayerId.Count == 0)
            return;

        IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> stintsByQid;
        try
        {
            stintsByQid = await wikidataClient.QueryPlayerCareerStintsByQidsAsync(
                qidToPlayerId.Keys.ToList(), cancellationToken);
        }
        catch (WikidataQueryException ex)
        {
            // Translate-and-rethrow, mirroring
            // GridLiveLookupDispatcher.TryRefreshCellAsync's exact pattern
            // (ADR-0010/0011): a Wikidata technical failure here means this
            // pair's connectivity is genuinely UNKNOWN — never "not
            // connected," never "connected." Games.XGConnect is the one
            // place a DataSync-specific exception is allowed to cross into
            // Core's cross-boundary contract (LiveLookupUnavailableException,
            // XGArcade.Core.Games) — same precedent Games.XGGrid already
            // established.
            throw new LiveLookupUnavailableException(
                $"Live Wikidata career-stint lookup for player(s) {string.Join(", ", playerIds)} did not complete in time: {ex.Message}");
        }

        if (stintsByQid.Count == 0)
            return;

        var newStintsByPlayerId = new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>();
        foreach (var (qid, fetchedStints) in stintsByQid)
        {
            if (fetchedStints.Count == 0)
                continue;

            var playerId = qidToPlayerId[qid];
            newStintsByPlayerId[playerId] = fetchedStints
                .Select(s => new PlayerCareerStint
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    ClubName = s.ClubName,
                    StartYear = s.StartYear,
                    EndYear = s.EndYear,
                    AppearanceCount = s.AppearanceCount,
                    // Resolved by AddCareerStintsBatchAsync across the
                    // player's full stint set — this placeholder is always
                    // overwritten before SaveChangesAsync.
                    SequenceOrder = 0,
                })
                .ToList();
        }

        if (newStintsByPlayerId.Count > 0)
            await playerCareerStintRepository.AddCareerStintsBatchAsync(newStintsByPlayerId, cancellationToken);
    }
}
