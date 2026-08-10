using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Core.IncidentReporting;

namespace XGArcade.Core.Tests.IncidentReporting;

// REQ-904/ADR-0066: CachedIncidentIssueSummaryProvider is the component
// this ADR's decision actually lives in — thorough coverage of all four
// documented behaviors (cache hit, cache miss/repopulate, stale-fallback-
// on-failure, and cold-start-failure-returns-Unavailable). Never calls the
// real GitHub API — always IncidentReportServiceTests.cs's shared, hand-
// rolled FakeGitHubIssueClient (internal, so visible assembly-wide; no
// mocking framework per docs/coding-guidelines.md), and a real in-process
// IMemoryCache (this is the exact type Program.cs registers, not a fake —
// ADR-0066's whole point is how this class behaves against a real
// IMemoryCache's own TTL eviction).
public class CachedIncidentIssueSummaryProviderTests
{
    private static readonly IReadOnlyList<GitHubIssueSummary> OneIssue =
        [new GitHubIssueSummary(42, "Grid freezes on submit", "https://github.com/johanpearson/xg-arcade/issues/42")];

    private static readonly IReadOnlyList<GitHubIssueSummary> AnotherIssue =
        [new GitHubIssueSummary(43, "Autocomplete is slow", "https://github.com/johanpearson/xg-arcade/issues/43")];

    private static CachedIncidentIssueSummaryProvider BuildProvider(
        FakeGitHubIssueClient client, TimeSpan ttl, out IMemoryCache memoryCache)
    {
        memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new CachedIncidentIssueSummaryProvider(
            client, memoryCache, new IncidentReportCacheTtl(ttl), NullLogger<CachedIncidentIssueSummaryProvider>.Instance);
    }

    [Test]
    public async Task REQ904_GetAsync_ColdStart_CallsGitHubOnce_AndReturnsAvailableWithIssues()
    {
        var client = new FakeGitHubIssueClient { NextListResult = GitHubIssueListResult.Ok(OneIssue) };
        var provider = BuildProvider(client, TimeSpan.FromMinutes(5), out _);

        var result = await provider.GetAsync(CancellationToken.None);

        Assert.That(result.Available, Is.True);
        Assert.That(result.Issues, Is.EqualTo(OneIssue));
        Assert.That(client.ListCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ904_GetAsync_SecondCallWithinTtl_ServesFromCache_WithoutCallingGitHubAgain()
    {
        var client = new FakeGitHubIssueClient { NextListResult = GitHubIssueListResult.Ok(OneIssue) };
        var provider = BuildProvider(client, TimeSpan.FromMinutes(5), out _);
        await provider.GetAsync(CancellationToken.None);

        // Change what the client WOULD return, to prove the second call
        // never actually reaches it.
        client.NextListResult = GitHubIssueListResult.Ok(AnotherIssue);
        var second = await provider.GetAsync(CancellationToken.None);

        Assert.That(client.ListCallCount, Is.EqualTo(1), "a request within the cache TTL must not trigger a new GitHub call");
        Assert.That(second.Available, Is.True);
        Assert.That(second.Issues, Is.EqualTo(OneIssue), "the cached (first) result is served, not a fresh one");
    }

    [Test]
    public async Task REQ904_GetAsync_CallAfterTtlExpiry_CallsGitHubAgain_AndRepopulatesCache()
    {
        var client = new FakeGitHubIssueClient { NextListResult = GitHubIssueListResult.Ok(OneIssue) };
        var provider = BuildProvider(client, TimeSpan.FromMilliseconds(30), out _);
        await provider.GetAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(150));
        client.NextListResult = GitHubIssueListResult.Ok(AnotherIssue);
        var second = await provider.GetAsync(CancellationToken.None);

        Assert.That(client.ListCallCount, Is.EqualTo(2), "a request after the TTL expires must trigger a fresh GitHub call");
        Assert.That(second.Available, Is.True);
        Assert.That(second.Issues, Is.EqualTo(AnotherIssue), "the cache must be repopulated from the fresh result");

        // A third call immediately after must be served from the newly
        // repopulated cache, not trigger yet another GitHub call.
        var third = await provider.GetAsync(CancellationToken.None);
        Assert.That(client.ListCallCount, Is.EqualTo(2));
        Assert.That(third.Issues, Is.EqualTo(AnotherIssue));
    }

    [Test]
    public async Task REQ904_GetAsync_GitHubFailureWithPriorSuccess_FallsBackToStaleResult_NeverUnavailable()
    {
        var client = new FakeGitHubIssueClient { NextListResult = GitHubIssueListResult.Ok(OneIssue) };
        var provider = BuildProvider(client, TimeSpan.FromMilliseconds(30), out _);
        await provider.GetAsync(CancellationToken.None);

        // Let the cache entry itself expire so the failure path is
        // genuinely exercised (a within-TTL failure would never even reach
        // GitHub, per the cache-hit test above) — ADR-0066's own wording:
        // "even if its TTL has technically expired."
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        client.NextListResult = GitHubIssueListResult.Failed("Could not list open issues on GitHub. Please try again later.");

        var result = await provider.GetAsync(CancellationToken.None);

        Assert.That(result.Available, Is.True, "ADR-0066: a transient GitHub failure must never flip a working admin UI to Unavailable while a prior successful result exists");
        Assert.That(result.Issues, Is.EqualTo(OneIssue), "the stale-but-still-valid last successful result is served");
    }

    [Test]
    public async Task REQ904_GetAsync_GitHubFailureWithNoPriorSuccess_ReturnsUnavailable()
    {
        var client = new FakeGitHubIssueClient
        {
            NextListResult = GitHubIssueListResult.Failed("Incident reporting is not configured on this environment yet."),
        };
        var provider = BuildProvider(client, TimeSpan.FromMinutes(5), out _);

        var result = await provider.GetAsync(CancellationToken.None);

        Assert.That(result.Available, Is.False, "REQ-904: never a false zero-count when there has never been a successful poll");
        Assert.That(result.Issues, Is.Null);
    }

    [Test]
    public async Task REQ904_GetAsync_RepeatedFailures_WithNoPriorSuccess_KeepReturningUnavailable_NeverCachingAFailure()
    {
        var client = new FakeGitHubIssueClient { NextListResult = GitHubIssueListResult.Failed("Could not reach GitHub. Please try again later.") };
        var provider = BuildProvider(client, TimeSpan.FromMinutes(5), out _);

        var first = await provider.GetAsync(CancellationToken.None);
        var second = await provider.GetAsync(CancellationToken.None);

        Assert.That(first.Available, Is.False);
        Assert.That(second.Available, Is.False);
        Assert.That(client.ListCallCount, Is.EqualTo(2), "a failure must never be cached — each request must retry GitHub until a poll actually succeeds");
    }
}
