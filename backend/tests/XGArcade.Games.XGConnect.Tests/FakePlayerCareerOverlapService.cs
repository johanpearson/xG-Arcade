using XGArcade.Core.Games;

namespace XGArcade.Games.XGConnect.Tests;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as FakePlayerFamiliarityService in
// XGArcade.Games.XGPath.Tests). ConnectTargetPickServiceTests uses this to
// control HaveSharedClubOverlapAsync's outcome directly (true/false/throws)
// without needing to drive PlayerCareerOverlapService's own Wikidata/
// PlayerCareerStint machinery — that machinery gets its own dedicated,
// direct unit tests in PlayerCareerOverlapServiceTests.cs. Defaults every
// unconfigured pair to "no overlap" (false/empty), matching the real
// service's own "genuinely no shared/overlapping club" outcome for two
// players with no configured relationship, so tests that don't care about
// the overlap outcome (e.g. the free pre-lock resubmission case) don't need
// to configure anything.
public class FakePlayerCareerOverlapService : IPlayerCareerOverlapService
{
    private readonly Dictionary<(Guid, Guid), List<SharedClubOverlap>> _overlapsByPair = new();
    private readonly HashSet<(Guid, Guid)> _liveLookupUnavailablePairs = new();

    // A single, arbitrary, non-empty overlap — enough to make
    // HaveSharedClubOverlapAsync/GetSharedClubOverlapsAsync report "yes,
    // they overlap" for a test that only cares about the boolean outcome
    // (SetOverlap below), not the specific club/years.
    private static readonly SharedClubOverlap DefaultOverlap = new("Configured Club", 2000, 2010);

    // Every HaveSharedClubOverlapAsync call this fake received, in call
    // order, exactly as (playerAId, playerBId) was passed — lets a test
    // assert both that the check ran at all and which two player IDs it was
    // asked to compare.
    public List<(Guid PlayerAId, Guid PlayerBId)> Calls { get; } = [];

    // Every GetSharedClubOverlapsAsync call this fake received, in call
    // order.
    public List<(Guid PlayerAId, Guid PlayerBId)> OverlapCalls { get; } = [];

    // Order-independent — a real overlap relationship between two players
    // doesn't care which one is "A" and which is "B" for a given test's
    // setup, so this configures both orderings at once. A convenience for
    // tests that only care about true/false, not the specific club/years —
    // see SetSharedClubOverlaps below for tests that do.
    public void SetOverlap(Guid playerAId, Guid playerBId, bool overlaps) =>
        SetSharedClubOverlaps(playerAId, playerBId, overlaps ? [DefaultOverlap] : []);

    // Order-independent, same reasoning as SetOverlap above. Configures the
    // exact list GetSharedClubOverlapsAsync returns for this pair — an
    // empty list (the default for any unconfigured pair) means "never
    // played together."
    public void SetSharedClubOverlaps(Guid playerAId, Guid playerBId, params SharedClubOverlap[] overlaps)
    {
        _overlapsByPair[(playerAId, playerBId)] = overlaps.ToList();
        _overlapsByPair[(playerBId, playerAId)] = overlaps.ToList();
    }

    public void SetLiveLookupUnavailable(Guid playerAId, Guid playerBId)
    {
        _liveLookupUnavailablePairs.Add((playerAId, playerBId));
        _liveLookupUnavailablePairs.Add((playerBId, playerAId));
    }

    public Task<bool> HaveSharedClubOverlapAsync(
        Guid playerAId, Guid playerBId, CancellationToken cancellationToken = default)
    {
        Calls.Add((playerAId, playerBId));

        if (_liveLookupUnavailablePairs.Contains((playerAId, playerBId)))
            throw new LiveLookupUnavailableException(
                $"simulated live-lookup failure for player pair ({playerAId}, {playerBId})");

        var overlaps = _overlapsByPair.TryGetValue((playerAId, playerBId), out var configured) && configured.Count > 0;
        return Task.FromResult(overlaps);
    }

    public Task<IReadOnlyList<SharedClubOverlap>> GetSharedClubOverlapsAsync(
        Guid playerAId, Guid playerBId, CancellationToken cancellationToken = default)
    {
        OverlapCalls.Add((playerAId, playerBId));

        if (_liveLookupUnavailablePairs.Contains((playerAId, playerBId)))
            throw new LiveLookupUnavailableException(
                $"simulated live-lookup failure for player pair ({playerAId}, {playerBId})");

        IReadOnlyList<SharedClubOverlap> overlaps = _overlapsByPair.TryGetValue((playerAId, playerBId), out var configured)
            ? configured
            : [];
        return Task.FromResult(overlaps);
    }
}
