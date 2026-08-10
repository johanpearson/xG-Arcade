namespace XGArcade.Core.IncidentReporting;

// COMP-12/REQ-903/ADR-0064: builds the issue title/body from a submitted
// report and delegates the actual GitHub call to IGitHubIssueClient, which
// owns the fixed target-repo/label/credential (never client-configurable).
// Body includes only non-PII triage context — the reporting user's
// internal UserId, the route/screen the client supplied (if any), and a
// timestamp — never an email address and never the GitHub token itself
// (REQ-903's explicit "never" list).
public class IncidentReportService(IGitHubIssueClient gitHubIssueClient, TimeProvider timeProvider) : IIncidentReportService
{
    private const int TitleMaxLength = 80;

    public Task<GitHubIssueCreationResult> SubmitAsync(
        Guid userId, string description, string? route, CancellationToken cancellationToken)
    {
        var reportedAt = timeProvider.GetUtcNow();
        var title = BuildTitle(description);
        var body = BuildBody(userId, description, route, reportedAt);
        return gitHubIssueClient.CreateIssueAsync(title, body, cancellationToken);
    }

    private static string BuildTitle(string description)
    {
        var singleLine = description.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= TitleMaxLength
            ? $"Player report: {singleLine}"
            : $"Player report: {singleLine[..TitleMaxLength]}…";
    }

    private static string BuildBody(Guid userId, string description, string? route, DateTimeOffset reportedAt) =>
        $"""
        {description}

        ---
        **Triage context** (non-PII, REQ-903)
        - Reporting user id: {userId}
        - Route/screen: {(string.IsNullOrWhiteSpace(route) ? "(not supplied)" : route)}
        - Reported at: {reportedAt:O}
        """;
}
