namespace XGArcade.Core.IncidentReporting;

// COMP-12/REQ-903/ADR-0064: builds a consistently-formatted GitHub issue
// title/body from a submitted report and delegates the actual GitHub call
// to IGitHubIssueClient, which owns the fixed target-repo/label/credential
// (never client-configurable). Every issue this service creates follows
// the exact same template (Title/Screen/Environment/Description) — added
// 2026-08-10, same day as the original build, requested directly, so
// triage never depends on whatever ad-hoc shape a player happened to type.
// Body includes only non-PII triage context beyond what the player wrote —
// the reporting user's internal UserId and a timestamp — never an email
// address and never the GitHub token itself (REQ-903's explicit "never"
// list).
public class IncidentReportService(IGitHubIssueClient gitHubIssueClient, TimeProvider timeProvider) : IIncidentReportService
{
    public Task<GitHubIssueCreationResult> SubmitAsync(
        Guid userId, string title, string description, string screen, string? environment, CancellationToken cancellationToken)
    {
        var reportedAt = timeProvider.GetUtcNow();
        var body = BuildBody(userId, description, screen, environment, reportedAt);
        return gitHubIssueClient.CreateIssueAsync(title, body, cancellationToken);
    }

    // Fixed template — every field always appears in the same order, even
    // when Environment is missing ("(not supplied)"), so a triager scanning
    // several issues never has to hunt for where a given field landed.
    private static string BuildBody(Guid userId, string description, string screen, string? environment, DateTimeOffset reportedAt) =>
        $"""
        ## Description

        {description}

        ## Details

        - **Screen:** {screen}
        - **Environment:** {(string.IsNullOrWhiteSpace(environment) ? "(not supplied)" : environment)}
        - **Reported by (internal user id):** {userId}
        - **Reported at:** {reportedAt:O}
        """;
}
