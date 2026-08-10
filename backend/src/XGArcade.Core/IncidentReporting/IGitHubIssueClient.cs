namespace XGArcade.Core.IncidentReporting;

// ADR-0064/COMP-12: the one and only path this backend uses to write to
// GitHub — deliberately narrow (create an issue, nothing else) so the
// fine-grained PAT's Issues:write scope is never asked to do more than
// this interface allows. Target repo and label are resolved server-side by
// the implementation (GitHubIssueClient), never a parameter here — REQ-903
// is explicit that neither is ever client-configurable.
public interface IGitHubIssueClient
{
    Task<GitHubIssueCreationResult> CreateIssueAsync(string title, string body, CancellationToken cancellationToken);
}

// FailureReason is always a client-safe summary (never GitHub's raw error
// body or the PAT itself) — GitHubIssueClient logs the full detail
// server-side before returning this (coding-guidelines.md's "log full
// exception server-side; return a client-appropriate summary" rule).
public record GitHubIssueCreationResult(bool Success, string? IssueUrl, string? FailureReason)
{
    public static GitHubIssueCreationResult Ok(string issueUrl) => new(true, issueUrl, null);

    public static GitHubIssueCreationResult Failed(string reason) => new(false, null, reason);
}
