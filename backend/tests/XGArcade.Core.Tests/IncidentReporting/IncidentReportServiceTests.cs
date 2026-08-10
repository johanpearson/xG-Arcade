using XGArcade.Core.IncidentReporting;
using XGArcade.Core.Tests.Rounds;

namespace XGArcade.Core.Tests.IncidentReporting;

// REQ-903/ADR-0064: IncidentReportService's own unit coverage — verifies
// the body/title it builds (never the HTTP call itself, which
// GitHubIssueClientTests.cs owns) using a hand-rolled fake IGitHubIssueClient
// (docs/coding-guidelines.md "don't over-mock").
public class IncidentReportServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task REQ903_SubmitAsync_BuildsBody_WithUserIdRouteAndTimestamp_NeverAnEmailOrToken()
    {
        var gitHubClient = new FakeGitHubIssueClient();
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));
        var userId = Guid.NewGuid();

        await service.SubmitAsync(userId, "The grid froze after I submitted a guess.", "/grid", CancellationToken.None);

        Assert.That(gitHubClient.LastBody, Does.Contain("The grid froze after I submitted a guess."));
        Assert.That(gitHubClient.LastBody, Does.Contain(userId.ToString()));
        Assert.That(gitHubClient.LastBody, Does.Contain("/grid"));
        Assert.That(gitHubClient.LastBody, Does.Contain("2026-08-10"));
        Assert.That(gitHubClient.LastBody, Does.Not.Contain("@"),
            "REQ-903: the issue body must never include an email address");
    }

    [Test]
    public async Task REQ903_SubmitAsync_BuildsBody_WithPlaceholderRoute_WhenRouteNotSupplied()
    {
        var gitHubClient = new FakeGitHubIssueClient();
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));

        await service.SubmitAsync(Guid.NewGuid(), "Something broke.", route: null, CancellationToken.None);

        Assert.That(gitHubClient.LastBody, Does.Contain("(not supplied)"));
    }

    [Test]
    public async Task REQ903_SubmitAsync_BuildsTitle_FromTheDescription()
    {
        var gitHubClient = new FakeGitHubIssueClient();
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));

        await service.SubmitAsync(Guid.NewGuid(), "Short description.", "/settings", CancellationToken.None);

        Assert.That(gitHubClient.LastTitle, Does.Contain("Short description."));
    }

    [Test]
    public async Task REQ903_SubmitAsync_TruncatesAnOverlongTitle_WithoutTruncatingTheStoredBody()
    {
        var gitHubClient = new FakeGitHubIssueClient();
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));
        var longDescription = new string('a', 500);

        await service.SubmitAsync(Guid.NewGuid(), longDescription, "/grid", CancellationToken.None);

        Assert.That(gitHubClient.LastTitle!.Length, Is.LessThan(longDescription.Length));
        Assert.That(gitHubClient.LastBody, Does.Contain(longDescription),
            "truncation is a title-only concern — the full description is always preserved in the body");
    }

    [Test]
    public async Task REQ903_SubmitAsync_ReturnsWhateverTheGitHubClientReturns()
    {
        var gitHubClient = new FakeGitHubIssueClient { NextResult = GitHubIssueCreationResult.Failed("simulated failure") };
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));

        var result = await service.SubmitAsync(Guid.NewGuid(), "Something broke.", null, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.FailureReason, Is.EqualTo("simulated failure"));
    }
}

internal sealed class FakeGitHubIssueClient : IGitHubIssueClient
{
    public string? LastTitle { get; private set; }
    public string? LastBody { get; private set; }
    public GitHubIssueCreationResult NextResult { get; set; } = GitHubIssueCreationResult.Ok("https://github.com/johanpearson/xg-arcade/issues/1");

    public Task<GitHubIssueCreationResult> CreateIssueAsync(string title, string body, CancellationToken cancellationToken)
    {
        LastTitle = title;
        LastBody = body;
        return Task.FromResult(NextResult);
    }
}
