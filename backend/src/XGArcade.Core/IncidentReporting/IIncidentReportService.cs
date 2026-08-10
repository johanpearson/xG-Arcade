namespace XGArcade.Core.IncidentReporting;

// COMP-12/REQ-903: the orchestration layer between "a player typed a
// description" (XGArcade.Api.Incidents.IncidentEndpoints) and "an HTTP call
// to GitHub happens" (IGitHubIssueClient) — owns building the non-PII
// triage body, nothing else. Auth/guest rejection and rate limiting stay in
// the endpoint (same split GuessEndpoints/GuessSubmissionService already
// use), since neither is specific to incident reporting's own business
// logic.
public interface IIncidentReportService
{
    Task<GitHubIssueCreationResult> SubmitAsync(
        Guid userId, string title, string description, string screen, string? environment, CancellationToken cancellationToken);
}
