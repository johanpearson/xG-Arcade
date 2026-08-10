using XGArcade.Core.IncidentReporting;

namespace XGArcade.Api.Tests;

// REQ-903/ADR-0064: stands in for the real GitHubIssueClient in every
// API-level test — this suite must never call the real GitHub API
// (requirements-document.md REQ-903's own "Test level" note). Hand-rolled,
// not a mocking framework (docs/coding-guidelines.md).
internal sealed class FakeGitHubIssueClient : IGitHubIssueClient
{
    public List<(string Title, string Body)> Calls { get; } = [];
    public GitHubIssueCreationResult NextResult { get; set; } = GitHubIssueCreationResult.Ok("https://github.com/johanpearson/xg-arcade/issues/1");

    public Task<GitHubIssueCreationResult> CreateIssueAsync(string title, string body, CancellationToken cancellationToken)
    {
        Calls.Add((title, body));
        return Task.FromResult(NextResult);
    }
}
