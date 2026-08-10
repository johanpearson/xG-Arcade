using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace XGArcade.Core.IncidentReporting;

// ADR-0064: the fine-grained GitHub PAT (Issues:write on this one repo
// only) — kept as its own tiny type, not a bare string, for the same
// reason SupabaseServiceRoleKey (XGArcade.Core.Auth) is: DI's typed-client
// activation for AddHttpClient<IGitHubIssueClient, GitHubIssueClient> can't
// resolve an unwrapped `string` constructor parameter unambiguously.
// Value is nullable/optional (unlike SupabaseServiceRoleKey, which throws
// at startup if unset) because this feature's manual setup (CLAUDE.md's
// REQ-903 handoff — creating and naming the GITHUB_INCIDENT_REPORT_PAT
// secret) is not guaranteed to have happened in every environment yet.
// CreateIssueAsync's own null/blank check below turns an unset token into
// a clean per-request failure, never a startup crash.
public record GitHubIncidentReportToken(string? Value);

// ADR-0064: the fixed target repo/label — never accepted from the client
// (see this file's own interface doc comment). Not secret, unlike
// GitHubIncidentReportToken above; resolved once from configuration in
// Program.cs (XGArcade.Api, which references Microsoft.Extensions
// .Configuration via the ASP.NET Core shared framework) and passed in here
// as plain values, rather than XGArcade.Core taking a direct dependency on
// IConfiguration itself — this project is a plain class library with no
// existing reason to reference that package.
public record GitHubIncidentReportOptions(string Owner, string Repo, string Label);

// ADR-0064/COMP-12: the one and only path this backend uses to call
// GitHub's REST API. BaseAddress and the User-Agent/Accept/API-version
// headers are set once at registration time (Program.cs's
// AddHttpClient<IGitHubIssueClient, GitHubIssueClient>); the bearer token
// is set per-request here, never on httpClient's own DefaultRequestHeaders
// — the same "a request's own header always wins, and this is the only
// place the credential is even read" shape SupabaseAuthClient
// .DeleteUserAsync already established for its own service_role key.
public class GitHubIssueClient(
    HttpClient httpClient,
    GitHubIncidentReportToken token,
    GitHubIncidentReportOptions options,
    ILogger<GitHubIssueClient> logger) : IGitHubIssueClient
{
    public async Task<GitHubIssueCreationResult> CreateIssueAsync(string title, string body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token.Value))
        {
            // Never a stack trace/exception here — an unconfigured token is
            // an expected state until the manual GITHUB_INCIDENT_REPORT_PAT
            // setup (CLAUDE.md's handoff for this story) has happened in
            // this environment.
            logger.LogError("Incident report failed: GitHub:IncidentReportToken is not configured.");
            return GitHubIssueCreationResult.Failed("Incident reporting is not configured on this environment yet.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"repos/{options.Owner}/{options.Repo}/issues")
        {
            Content = JsonContent.Create(new GitHubCreateIssueRequest(title, body, [options.Label])),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Full detail (status + GitHub's own response body) logged
                // server-side only — REQ-903's "no GitHub-side error detail
                // leaked to the client" — the caller gets a generic,
                // client-safe summary instead.
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Incident report failed: GitHub API returned {StatusCode}. {ResponseBody}",
                    response.StatusCode, responseBody);
                return GitHubIssueCreationResult.Failed("Could not create the issue on GitHub. Please try again later.");
            }

            var created = await response.Content.ReadFromJsonAsync<GitHubIssueResponse>(cancellationToken: cancellationToken);
            if (created?.HtmlUrl is null)
            {
                logger.LogError("Incident report failed: GitHub API returned a success status with no issue URL.");
                return GitHubIssueCreationResult.Failed("Could not create the issue on GitHub. Please try again later.");
            }

            return GitHubIssueCreationResult.Ok(created.HtmlUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Network failure/timeout: if the POST above never got a
            // response back, GitHub never created the issue either — no
            // retry, no partial/duplicate issue (REQ-903's own acceptance
            // criterion for this failure mode).
            logger.LogError(ex, "Incident report failed: could not reach the GitHub API.");
            return GitHubIssueCreationResult.Failed("Could not reach GitHub. Please try again later.");
        }
    }

    private record GitHubCreateIssueRequest(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels);

    private record GitHubIssueResponse([property: JsonPropertyName("html_url")] string? HtmlUrl);
}
