using XGArcade.DataSync.ApiFootball;

namespace XGArcade.Games.XGPredict.Tests;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as Games.XGPath.Tests'
// FakePlayerCareerStintRefreshService). Lets XGPredictGameModuleTests pin
// exactly which fixtures GenerateInstanceAsync (REQ-1301) sees, and lets
// PredictGradingServiceTests (REQ-1305/ADR-0097) pin exactly what
// GetFixtureResultAsync returns (or throws) per fixture id, without any
// real HTTP/API-Football machinery.
public class FakeApiFootballClient : IApiFootballClient
{
    public IReadOnlyList<ApiFootballFixture> Fixtures { get; set; } = [];

    // REQ-1305: keyed by ExternalFixtureId. A test wires up exactly the
    // fixtures its scenario needs; any fixture id with no configured
    // entry (and no configured exception below) throws
    // NotImplementedException — the same "should never be called unless a
    // test wires it" guard XGPredictGameModuleTests already relies on
    // implicitly by never touching this dictionary at all.
    public Dictionary<int, ApiFootballFixtureResult> Results { get; } = [];

    // REQ-1305: lets a test simulate a per-fixture ApiFootballClientException
    // (or any other exception) without needing a second fake type —
    // PredictGradingServiceTests' "one match's failure doesn't abort the
    // rest of the run" case.
    public Dictionary<int, Exception> ExceptionsToThrow { get; } = [];

    // REQ-1305: every fixture id GetFixtureResultAsync was actually asked
    // about, across every call this fake has ever received — lets a test
    // assert idempotency (a Graded/Voided match's fixture id is never
    // re-requested on a later run) without needing a mocking framework's
    // call-verification feature.
    public List<int> RequestedFixtureIds { get; } = [];

    public Task<IReadOnlyList<ApiFootballFixture>> GetUpcomingGameweekFixturesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Fixtures);

    public Task<ApiFootballFixtureResult> GetFixtureResultAsync(int fixtureId, CancellationToken cancellationToken = default)
    {
        RequestedFixtureIds.Add(fixtureId);

        if (ExceptionsToThrow.TryGetValue(fixtureId, out var exception))
            throw exception;

        if (Results.TryGetValue(fixtureId, out var result))
            return Task.FromResult(result);

        throw new NotImplementedException(
            $"FakeApiFootballClient has no configured Results/ExceptionsToThrow entry for fixture {fixtureId} — " +
            "set one before calling GetFixtureResultAsync.");
    }
}
