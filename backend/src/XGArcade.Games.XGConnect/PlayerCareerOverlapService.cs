using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGConnect;

// S-211/REQ-1404: see IPlayerCareerOverlapService's own doc comment for the
// "why here, why generic" framing.
//
// Bug fix (2026-09-04, ADR-0105): this class used to trust a player's
// PlayerCareerStint rows as complete once ANY row existed, refreshing from
// Wikidata only for a player with zero cached rows. That is the exact bug
// ADR-0054 already found and fixed once in xG Path (Timothy Weah missing
// real Juventus/Marseille stints): PlayerCareerStint is a shared table
// other features can write narrow, single-club byproduct rows into (e.g.
// an xG Grid guess-check persisting only the ONE club that guess queried),
// so "has any row" is not the same as "has a full career fetched." Real,
// reported incident: Reece James already had a Chelsea-only row from an
// earlier chain step, so a later step needing his Wigan Athletic loan
// (genuinely shared with Jonas Olsson) silently found no overlap — his
// Wigan stint had simply never been fetched. Fixed by following ADR-0054's
// own precedent exactly: always refresh both players' full career
// unconditionally before computing an overlap, never gated on whether they
// already have some rows. RefreshCareerStintsAsync's own reconciliation
// (XGArcade.DataSync) already dedupes against existing rows and adds only
// what's new, so this is safe and cheap to call every time, not a
// from-scratch refetch.
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
        var overlaps = await GetSharedClubOverlapsAsync(playerAId, playerBId, cancellationToken);
        return overlaps.Count > 0;
    }

    // Bug fix/design change (2026-09-04, REQ-1406): supersedes the former
    // HaveOverlapAtClubAsync(playerAId, playerBId, clubName) — see this
    // method's own interface doc comment (IPlayerCareerOverlapService) for
    // the full "why" (a real false-rejection bug from asking the player to
    // type a club name that had to exactly match an already-canonicalized
    // stored value). This is now the one place interval-overlap math is
    // computed — HaveSharedClubOverlapAsync above is a thin wrapper over
    // this, not a separate implementation, so the two can never drift.
    //
    // Exact, case-insensitive ClubName match + a true two-range interval
    // overlap test — EndYear == null means "ongoing," treated as
    // unbounded/open going forward, mirroring
    // PathCareerStintFilter.IsInferredLoan's own null-handling convention
    // for this codebase's StartYear/EndYear fields (adapted here into a
    // genuine two-range intersection, not a single-player containment
    // check). No club-name normalization is needed here — unlike a
    // player-typed value, both sides are already-persisted
    // PlayerCareerStint rows, canonicalized identically at ingest time by
    // the same writer (PlayerCareerStintRefreshService /
    // WikidataLookupService.PersistCareerStintsAsync), so an exact
    // OrdinalIgnoreCase match is correct and sufficient — same reasoning
    // this method's predecessor already relied on.
    public async Task<IReadOnlyList<SharedClubOverlap>> GetSharedClubOverlapsAsync(
        Guid playerAId, Guid playerBId, CancellationToken cancellationToken = default)
    {
        var (stintsA, stintsB) = await LoadBothPlayersStintsAsync(playerAId, playerBId, cancellationToken);

        var overlaps = new List<SharedClubOverlap>();
        foreach (var sa in stintsA)
        {
            foreach (var sb in stintsB)
            {
                if (!string.Equals(sa.ClubName, sb.ClubName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (sa.StartYear > (sb.EndYear ?? int.MaxValue) || sb.StartYear > (sa.EndYear ?? int.MaxValue))
                    continue;

                var overlapStartYear = Math.Max(sa.StartYear, sb.StartYear);
                int? overlapEndYear = (sa.EndYear, sb.EndYear) switch
                {
                    (null, null) => null,
                    (null, { } endB) => endB,
                    ({ } endA, null) => endA,
                    ({ } endA, { } endB) => Math.Min(endA, endB),
                };

                overlaps.Add(new SharedClubOverlap(sa.ClubName, overlapStartYear, overlapEndYear));
            }
        }

        return overlaps;
    }

    // Shared "always refresh both players' full career from Wikidata, then
    // read back their stints" logic — extracted (S-213) so
    // HaveSharedClubOverlapAsync and GetSharedClubOverlapsAsync (2026-09-04:
    // originally HaveOverlapAtClubAsync, see that method's own doc comment
    // for the design change) don't each duplicate the refresh/re-read
    // plumbing.
    //
    // Bug fix (2026-09-04, ADR-0105): no longer skips a player who already
    // has SOME cached PlayerCareerStint rows — see this class's own doc
    // comment for why "has any row" was never a safe proxy for "has a full
    // career fetched." Always refreshes both players unconditionally,
    // exactly matching XGPathGameModule.GenerateInstanceAsync's own
    // unconditional call to the same shared service (ADR-0054) — safe and
    // cheap because RefreshCareerStintsAsync's own reconciliation dedupes
    // against whatever rows already exist and persists only genuinely new
    // ones.
    private async Task<(IReadOnlyList<PlayerCareerStint> StintsA, IReadOnlyList<PlayerCareerStint> StintsB)> LoadBothPlayersStintsAsync(
        Guid playerAId, Guid playerBId, CancellationToken cancellationToken)
    {
        // One batched Wikidata call for both players — same "fetch once per
        // call" discipline PlayerCareerStintRefreshService applies per
        // generated xG Path instance, just scoped here to at most two
        // players, and now run on every call rather than only when a
        // player has zero cached rows (see this class's own doc comment).
        //
        // throwOnFailure: true — a genuine Wikidata technical failure must
        // surface as LiveLookupUnavailableException below, never be logged
        // and swallowed the way xG Path's own (default) call is — see
        // IPlayerCareerStintRefreshService's own doc comment.
        try
        {
            await playerCareerStintRefreshService.RefreshCareerStintsAsync(
                [playerAId, playerBId], throwOnFailure: true, cancellationToken);
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
                $"Live Wikidata career-stint lookup for player(s) {playerAId}, {playerBId} did not complete in time: {ex.Message}");
        }

        var stintsByPlayerId = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(
            [playerAId, playerBId], cancellationToken);

        var stintsA = stintsByPlayerId.TryGetValue(playerAId, out var a) ? a : (IReadOnlyList<PlayerCareerStint>)[];
        var stintsB = stintsByPlayerId.TryGetValue(playerBId, out var b) ? b : (IReadOnlyList<PlayerCareerStint>)[];

        return (stintsA, stintsB);
    }
}
