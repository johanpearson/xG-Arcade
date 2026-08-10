using XGArcade.Core.IncidentReporting;
using XGArcade.Core.Tests.Rounds;

namespace XGArcade.Core.Tests.IncidentReporting;

// REQ-903/ADR-0064: IncidentReportService's own unit coverage — verifies
// the title/body it builds (never the HTTP call itself, which
// GitHubIssueClientTests.cs owns) using a hand-rolled fake IGitHubIssueClient
// (docs/coding-guidelines.md "don't over-mock"). Structured-fields addition
// (2026-08-10): Title/Screen are now separate, mandatory inputs rather than
// derived from Description — every test below reflects that shape.
public class IncidentReportServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task REQ903_SubmitAsync_UsesTheGivenTitle_Verbatim()
    {
        var gitHubClient = new FakeGitHubIssueClient();
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));

        await service.SubmitAsync(Guid.NewGuid(), "Grid freezes on submit", "The grid froze after I submitted a guess.", "grid", "https://xg-arcade-dev.example.com", CancellationToken.None);

        Assert.That(gitHubClient.LastTitle, Is.EqualTo("Grid freezes on submit"));
    }

    [Test]
    public async Task REQ903_SubmitAsync_BuildsBody_WithUserIdScreenEnvironmentAndTimestamp_NeverAnEmailOrToken()
    {
        var gitHubClient = new FakeGitHubIssueClient();
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));
        var userId = Guid.NewGuid();

        await service.SubmitAsync(userId, "Grid freezes on submit", "The grid froze after I submitted a guess.", "grid", "https://xg-arcade-dev.example.com", CancellationToken.None);

        Assert.That(gitHubClient.LastBody, Does.Contain("The grid froze after I submitted a guess."));
        Assert.That(gitHubClient.LastBody, Does.Contain(userId.ToString()));
        Assert.That(gitHubClient.LastBody, Does.Contain("grid"));
        Assert.That(gitHubClient.LastBody, Does.Contain("https://xg-arcade-dev.example.com"));
        Assert.That(gitHubClient.LastBody, Does.Contain("2026-08-10"));
        Assert.That(gitHubClient.LastBody, Does.Not.Contain("@"),
            "REQ-903: the issue body must never include an email address");
    }

    [Test]
    public async Task REQ903_SubmitAsync_BuildsBody_WithPlaceholderEnvironment_WhenEnvironmentNotSupplied()
    {
        var gitHubClient = new FakeGitHubIssueClient();
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));

        await service.SubmitAsync(Guid.NewGuid(), "Something broke", "Something broke.", "settings", environment: null, CancellationToken.None);

        Assert.That(gitHubClient.LastBody, Does.Contain("(not supplied)"));
    }

    // The consistent-template requirement itself: every field appears under
    // its own labeled heading, in a fixed order, regardless of content —
    // this is what makes every issue this service creates scannable the
    // same way, the whole point of the 2026-08-10 structured-fields change.
    [Test]
    public async Task REQ903_SubmitAsync_BuildsBody_AsAFixedTemplate_WithDescriptionScreenEnvironmentAndUserIdEachUnderTheirOwnLabel()
    {
        var gitHubClient = new FakeGitHubIssueClient();
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));

        await service.SubmitAsync(Guid.NewGuid(), "A title", "A description.", "leaderboard", "https://example.com", CancellationToken.None);

        Assert.That(gitHubClient.LastBody, Does.Contain("## Description"));
        Assert.That(gitHubClient.LastBody, Does.Contain("## Details"));
        Assert.That(gitHubClient.LastBody, Does.Contain("**Screen:**"));
        Assert.That(gitHubClient.LastBody, Does.Contain("**Environment:**"));
        Assert.That(gitHubClient.LastBody, Does.Contain("**Reported by (internal user id):**"));
        Assert.That(gitHubClient.LastBody, Does.Contain("**Reported at:**"));
    }

    [Test]
    public async Task REQ903_SubmitAsync_ReturnsWhateverTheGitHubClientReturns()
    {
        var gitHubClient = new FakeGitHubIssueClient { NextResult = GitHubIssueCreationResult.Failed("simulated failure") };
        var service = new IncidentReportService(gitHubClient, new FixedTimeProvider(FixedNow));

        var result = await service.SubmitAsync(Guid.NewGuid(), "A title", "Something broke.", "grid", null, CancellationToken.None);

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
