using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGPath.Tests;

// Hand-rolled fake, same "don't over-mock" pattern as
// FakePlayerCareerStintRefreshService in this same test project. ADR-0056:
// everyone passed in is treated as familiar by default (an opt-in
// MarkUnfamiliar per player), so every existing eligibility test in
// XGPathGameModuleTests.cs — none of which care about familiarity — keeps
// passing unchanged; only the tests that specifically exercise ADR-0056's
// filter need to call MarkUnfamiliar.
public class FakePlayerFamiliarityService : IPlayerFamiliarityService
{
    private readonly HashSet<Guid> _unfamiliarPlayerIds = [];

    public List<IReadOnlyList<Guid>> Calls { get; } = [];

    public void MarkUnfamiliar(Guid playerId) => _unfamiliarPlayerIds.Add(playerId);

    public Task<IReadOnlySet<Guid>> FilterFamiliarAsync(
        IReadOnlyList<Guid> candidatePlayerIds, CancellationToken cancellationToken = default)
    {
        Calls.Add(candidatePlayerIds);
        IReadOnlySet<Guid> familiar = candidatePlayerIds.Where(id => !_unfamiliarPlayerIds.Contains(id)).ToHashSet();
        return Task.FromResult(familiar);
    }
}
