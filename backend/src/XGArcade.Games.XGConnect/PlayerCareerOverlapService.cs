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
//
// 2026-09-02 (architecture-review follow-up, S-211): delegates the actual
// fetch/persist to the shared IPlayerCareerStintRefreshService
// (XGArcade.DataSync, ADR-0054) via its throwOnFailure=true opt-in, rather
// than re-deriving QID resolution/fetch/persist logic here. See that
// service's own doc comment for the throwOnFailure shape this mirrors
// (IWikidataClient's own throwOnTimeout parameter). This also means a fetch
// through this path now gets PlayerCareerStintRefreshService's own
// ClubName canonicalization against seeded ClubDefinition.Name (ADR-0059)
// for free — this class no longer needs (and no longer has) its own,
// necessarily-worse-off copy of that logic.
public class PlayerCareerOverlapService(
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerCareerStintRefreshService playerCareerStintRefreshService) : IPlayerCareerOverlapService
{
    public async Task<bool> HaveSharedClubOverlapAsync(
        Guid playerAId, Guid playerBId, CancellationToken cancellationToken = default)
    {
        var (stintsA, stintsB) = await LoadBothPlayersStintsAsync(playerAId, playerBId, cancellationToken);

        // Exact, case-insensitive ClubName match + a true two-range interval
        // overlap test — EndYear == null means "ongoing," treated as
        // unbounded/open going forward, mirroring
        // PathCareerStintFilter.IsInferredLoan's own null-handling
        // convention for this codebase's StartYear/EndYear fields (adapted
        // here into a genuine two-range intersection, not a single-player
        // containment check).
        //
        // ClubName here is whatever PlayerCareerStintRefreshService (or xG
        // Grid's own byproduct writer, WikidataLookupService
        // .PersistCareerStintsAsync) already persisted — including that
        // service's own ClubName canonicalization against seeded
        // ClubDefinition.Name (ADR-0059), now inherited for free since this
        // class delegates its refresh to that shared service rather than
        // forking it (see this class's own doc comment above).
        return stintsA.Any(sa => stintsB.Any(sb =>
            string.Equals(sa.ClubName, sb.ClubName, StringComparison.OrdinalIgnoreCase) &&
            sa.StartYear <= (sb.EndYear ?? int.MaxValue) &&
            sb.StartYear <= (sa.EndYear ?? int.MaxValue)));
    }

    // S-213/REQ-1406: identical interval-overlap math to
    // HaveSharedClubOverlapAsync above, filtered to the one claimed club on
    // both sides instead of any matching pair of clubs — see this class's
    // own IPlayerCareerOverlapService doc comment for why a chain step needs
    // this narrower check.
    public async Task<bool> HaveOverlapAtClubAsync(
        Guid playerAId, Guid playerBId, string clubName, CancellationToken cancellationToken = default)
    {
        var (stintsA, stintsB) = await LoadBothPlayersStintsAsync(playerAId, playerBId, cancellationToken);

        var stintsAAtClub = stintsA.Where(s => string.Equals(s.ClubName, clubName, StringComparison.OrdinalIgnoreCase));
        var stintsBAtClub = stintsB.Where(s => string.Equals(s.ClubName, clubName, StringComparison.OrdinalIgnoreCase));

        return stintsAAtClub.Any(sa => stintsBAtClub.Any(sb =>
            sa.StartYear <= (sb.EndYear ?? int.MaxValue) &&
            sb.StartYear <= (sa.EndYear ?? int.MaxValue)));
    }

    // Shared "ensure both players' stints are loaded, refreshing from
    // Wikidata if either has zero cached rows" logic — extracted (S-213) so
    // HaveSharedClubOverlapAsync and HaveOverlapAtClubAsync don't each
    // duplicate the fetch-once/live-refresh/re-read plumbing. Behavior is
    // byte-for-byte what HaveSharedClubOverlapAsync did before this
    // extraction.
    private async Task<(IReadOnlyList<PlayerCareerStint> StintsA, IReadOnlyList<PlayerCareerStint> StintsB)> LoadBothPlayersStintsAsync(
        Guid playerAId, Guid playerBId, CancellationToken cancellationToken)
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
            //
            // throwOnFailure: true — a genuine Wikidata technical failure
            // must surface as LiveLookupUnavailableException below, never be
            // logged and swallowed the way xG Path's own (default) call
            // is — see IPlayerCareerStintRefreshService's own doc comment.
            try
            {
                await playerCareerStintRefreshService.RefreshCareerStintsAsync(
                    playersNeedingRefresh, throwOnFailure: true, cancellationToken);
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
                    $"Live Wikidata career-stint lookup for player(s) {string.Join(", ", playersNeedingRefresh)} did not complete in time: {ex.Message}");
            }

            stintsByPlayerId = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(
                [playerAId, playerBId], cancellationToken);
        }

        var stintsA = stintsByPlayerId.TryGetValue(playerAId, out var a) ? a : (IReadOnlyList<PlayerCareerStint>)[];
        var stintsB = stintsByPlayerId.TryGetValue(playerBId, out var b) ? b : (IReadOnlyList<PlayerCareerStint>)[];

        return (stintsA, stintsB);
    }

    private static bool HasStints(
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>> stintsByPlayerId, Guid playerId) =>
        stintsByPlayerId.TryGetValue(playerId, out var stints) && stints.Count > 0;
}
