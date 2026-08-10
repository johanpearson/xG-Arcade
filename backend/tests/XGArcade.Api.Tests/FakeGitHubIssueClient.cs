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

    // REQ-904/ADR-0066: ListCallCount lets a test assert the cache is
    // actually preventing 1:1 GitHub calls per admin request (this
    // suite's own "repeated requests within the cache TTL do not each
    // trigger a new call to the stubbed GitHub client" acceptance
    // criterion) — a plain counter, same minimal-fake style as Calls above.
    public int ListCallCount { get; private set; }
    public GitHubIssueListResult NextListResult { get; set; } = GitHubIssueListResult.Ok([]);

    public Task<GitHubIssueListResult> ListOpenIssuesByLabelAsync(CancellationToken cancellationToken)
    {
        ListCallCount++;
        return Task.FromResult(NextListResult);
    }
}
