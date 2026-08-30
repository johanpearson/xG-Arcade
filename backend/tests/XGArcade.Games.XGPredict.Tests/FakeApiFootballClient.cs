using XGArcade.DataSync.ApiFootball;

namespace XGArcade.Games.XGPredict.Tests;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as Games.XGPath.Tests'
// FakePlayerCareerStintRefreshService). Lets XGPredictGameModuleTests pin
// exactly which fixtures GenerateInstanceAsync (REQ-1301) sees, without any
// real HTTP/API-Football machinery. GetFixtureResultAsync (REQ-1305) is not
// exercised by this story at all — not implemented, throws if ever called,
// since nothing in this story's scope should reach it.
public class FakeApiFootballClient : IApiFootballClient
{
    public IReadOnlyList<ApiFootballFixture> Fixtures { get; set; } = [];

    public Task<IReadOnlyList<ApiFootballFixture>> GetUpcomingGameweekFixturesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Fixtures);

    public Task<ApiFootballFixtureResult> GetFixtureResultAsync(int fixtureId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("REQ-1305 grading is out of scope for this story — GetFixtureResultAsync should never be called here.");
}
