namespace XGArcade.Core.IncidentReporting;

// ADR-0064/ADR-0066/COMP-12: the one and only path this backend uses to
// call GitHub's REST API — create an issue, or (REQ-904/ADR-0066) list
// this repo's currently-open issues carrying the fixed triage label,
// nothing else. Target repo and label are resolved server-side by the
// implementation (GitHubIssueClient), never a parameter here — REQ-903/
// REQ-904 are both explicit that neither is ever client-configurable.
// ADR-0066 confirms the existing Issues:write-scoped PAT already covers
// this read; no scope change, no second GitHub-calling class.
public interface IGitHubIssueClient
{
    Task<GitHubIssueCreationResult> CreateIssueAsync(string title, string body, CancellationToken cancellationToken);

    Task<GitHubIssueListResult> ListOpenIssuesByLabelAsync(CancellationToken cancellationToken);
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

// REQ-904: title/number/URL is all the admin UI needs per open issue (this
// endpoint has no in-app list/detail view — ADR-0066's "out of scope" list
// — but the count's own source rows are carried through in case the cached
// provider or a future change wants them).
public record GitHubIssueSummary(int Number, string Title, string HtmlUrl);

// Same "client-safe FailureReason, full detail logged server-side only"
// discipline as GitHubIssueCreationResult above.
public record GitHubIssueListResult(bool Success, IReadOnlyList<GitHubIssueSummary>? Issues, string? FailureReason)
{
    public static GitHubIssueListResult Ok(IReadOnlyList<GitHubIssueSummary> issues) => new(true, issues, null);

    public static GitHubIssueListResult Failed(string reason) => new(false, null, reason);
}
