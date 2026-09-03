using XGArcade.Core.Games;

namespace XGArcade.Games.XGConnect.Tests;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as FakePlayerFamiliarityService in
// XGArcade.Games.XGPath.Tests). ConnectTargetPickServiceTests uses this to
// control HaveSharedClubOverlapAsync's outcome directly (true/false/throws)
// without needing to drive PlayerCareerOverlapService's own Wikidata/
// PlayerCareerStint machinery — that machinery gets its own dedicated,
// direct unit tests in PlayerCareerOverlapServiceTests.cs. Defaults every
// unconfigured pair to "no overlap" (false), matching the real service's own
// "genuinely no shared/overlapping club" outcome for two players with no
// configured relationship, so tests that don't care about the overlap
// outcome (e.g. the free pre-lock resubmission case) don't need to
// configure anything.
public class FakePlayerCareerOverlapService : IPlayerCareerOverlapService
{
    private readonly Dictionary<(Guid, Guid), bool> _overlapByPair = new();
    private readonly HashSet<(Guid, Guid)> _liveLookupUnavailablePairs = new();

    // S-213/REQ-1406: per-pair-per-club configuration for
    // HaveOverlapAtClubAsync — separate from _overlapByPair above since a
    // pair can genuinely overlap at one club but not another. Club names
    // are compared case-insensitively, mirroring the real service's own
    // ClubName comparison.
    private readonly Dictionary<(Guid, Guid, string), bool> _overlapByPairAndClub =
        new(new PairAndClubComparer());
    private readonly HashSet<(Guid, Guid, string)> _liveLookupUnavailablePairsAndClubs =
        new(new PairAndClubComparer());

    // Every call this fake received, in call order, exactly as
    // (playerAId, playerBId) was passed — lets a test assert both that the
    // check ran at all and which two player IDs it was asked to compare.
    public List<(Guid PlayerAId, Guid PlayerBId)> Calls { get; } = [];

    // Every HaveOverlapAtClubAsync call this fake received, in call order.
    public List<(Guid PlayerAId, Guid PlayerBId, string ClubName)> ClubCalls { get; } = [];

    // Order-independent — a real overlap relationship between two players
    // doesn't care which one is "A" and which is "B" for a given test's
    // setup, so this configures both orderings at once.
    public void SetOverlap(Guid playerAId, Guid playerBId, bool overlaps)
    {
        _overlapByPair[(playerAId, playerBId)] = overlaps;
        _overlapByPair[(playerBId, playerAId)] = overlaps;
    }

    public void SetLiveLookupUnavailable(Guid playerAId, Guid playerBId)
    {
        _liveLookupUnavailablePairs.Add((playerAId, playerBId));
        _liveLookupUnavailablePairs.Add((playerBId, playerAId));
    }

    // Order-independent, same reasoning as SetOverlap above.
    public void SetOverlapAtClub(Guid playerAId, Guid playerBId, string clubName, bool overlaps)
    {
        _overlapByPairAndClub[(playerAId, playerBId, clubName)] = overlaps;
        _overlapByPairAndClub[(playerBId, playerAId, clubName)] = overlaps;
    }

    public void SetLiveLookupUnavailableAtClub(Guid playerAId, Guid playerBId, string clubName)
    {
        _liveLookupUnavailablePairsAndClubs.Add((playerAId, playerBId, clubName));
        _liveLookupUnavailablePairsAndClubs.Add((playerBId, playerAId, clubName));
    }

    public Task<bool> HaveSharedClubOverlapAsync(
        Guid playerAId, Guid playerBId, CancellationToken cancellationToken = default)
    {
        Calls.Add((playerAId, playerBId));

        if (_liveLookupUnavailablePairs.Contains((playerAId, playerBId)))
            throw new LiveLookupUnavailableException(
                $"simulated live-lookup failure for player pair ({playerAId}, {playerBId})");

        var overlaps = _overlapByPair.TryGetValue((playerAId, playerBId), out var configured) && configured;
        return Task.FromResult(overlaps);
    }

    public Task<bool> HaveOverlapAtClubAsync(
        Guid playerAId, Guid playerBId, string clubName, CancellationToken cancellationToken = default)
    {
        ClubCalls.Add((playerAId, playerBId, clubName));

        if (_liveLookupUnavailablePairsAndClubs.Contains((playerAId, playerBId, clubName)))
            throw new LiveLookupUnavailableException(
                $"simulated live-lookup failure for player pair ({playerAId}, {playerBId}) at club {clubName}");

        var overlaps = _overlapByPairAndClub.TryGetValue((playerAId, playerBId, clubName), out var configured) && configured;
        return Task.FromResult(overlaps);
    }

    private class PairAndClubComparer : IEqualityComparer<(Guid, Guid, string)>
    {
        public bool Equals((Guid, Guid, string) x, (Guid, Guid, string) y) =>
            x.Item1 == y.Item1 && x.Item2 == y.Item2 &&
            string.Equals(x.Item3, y.Item3, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid, Guid, string) obj) =>
            HashCode.Combine(obj.Item1, obj.Item2, obj.Item3.ToUpperInvariant());
    }
}
