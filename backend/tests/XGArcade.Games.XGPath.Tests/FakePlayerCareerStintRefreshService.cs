using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGPath.Tests;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as Games.XGGrid.Tests' FakeWikidataLookupService).
// ADR-0054: lets XGPathGameModuleTests assert GenerateInstanceAsync calls the
// refresh service with exactly the picked target ids, without any real
// Wikidata/DataSync machinery. Never actually writes PlayerCareerStint rows —
// no test here needs the refreshed data to be visible afterward, only that
// the call happened with the right arguments (the write path itself is
// PlayerCareerStintRefreshServiceTests' own concern, in XGArcade.DataSync.Tests).
public class FakePlayerCareerStintRefreshService : IPlayerCareerStintRefreshService
{
    public List<IReadOnlyList<Guid>> Calls { get; } = [];

    public Task RefreshCareerStintsAsync(IReadOnlyList<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        Calls.Add(playerIds);
        return Task.CompletedTask;
    }
}
