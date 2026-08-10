using XGArcade.Core.IncidentReporting;

namespace XGArcade.Api.Admin;

// REQ-904/ADR-0066: admin-only read of currently-open, `user-reported`-
// labeled GitHub issues in this repo — reads exclusively through
// ICachedIncidentIssueSummaryProvider, never IGitHubIssueClient directly
// (ADR-0066's "the cache is the only caller GET /admin/incident-reports is
// allowed to use" requirement). No request body or query parameters: the
// target repo/label/token are all fixed server-side (ADR-0064), same
// "never client-configurable" boundary POST /incidents already enforces
// for issue creation — this file only ever reads.
public static class AdminIncidentReportEndpoints
{
    public static void MapAdminIncidentReportEndpoints(this WebApplication app)
    {
        // REQ-904: three renderable states from one response —
        // Available=false is the explicit "no successful poll has ever
        // happened" failure state (never conflated with a real zero);
        // Available=true + OpenCount=0 is "no open incidents" (REQ-904:
        // rendered as no badge at all, same convention as REQ-512's
        // suggestion badge); Available=true + OpenCount>0 is the normal
        // count case. This always returns 200 — the request itself always
        // succeeds (it's a read of server memory); what can be degraded is
        // the upstream GitHub poll the cache is fronting, which is exactly
        // what Available communicates. REQ-904's own acceptance criteria
        // only call for 401/403 as HTTP-level failures for this endpoint
        // (the Authorization boundary section) — a GitHub-side failure is
        // framed throughout REQ-904/ADR-0066 as a distinct in-body state,
        // not an HTTP error.
        app.MapGet("/admin/incident-reports", async (
            ICachedIncidentIssueSummaryProvider cachedIncidentIssueSummaryProvider,
            CancellationToken cancellationToken) =>
        {
            var result = await cachedIncidentIssueSummaryProvider.GetAsync(cancellationToken);
            if (!result.Available || result.Issues is null)
                return Results.Ok(IncidentReportsResponse.Unavailable);

            var issues = result.Issues
                .Select(i => new IncidentReportIssueResponse(i.Number, i.Title, i.HtmlUrl))
                .ToList();

            return Results.Ok(new IncidentReportsResponse(true, issues.Count, issues));
        }).RequireAuthorization("Admin");
    }
}

// REQ-904: Available=false is the "no successful poll has ever happened"
// failure state (cold start during a GitHub outage, or the token has never
// been configured) — the frontend must render this as a distinct failure/
// unknown state, never as OpenCount=0 ("never a false zero-count", REQ-904's
// own wording). Available=true + OpenCount=0 is the legitimate "no open
// incidents" state, which REQ-904 says renders as no badge/entry-point
// count at all. Issues carries each open issue's number/title/url per
// REQ-904 ("the response may also include each open issue's title, number,
// and URL") even though the admin UI itself only needs the aggregate count
// plus a single "view on GitHub" link — REQ-904 explicitly allows this and
// it costs nothing extra from GitHub's own response.
public record IncidentReportsResponse(bool Available, int OpenCount, IReadOnlyList<IncidentReportIssueResponse> Issues)
{
    public static readonly IncidentReportsResponse Unavailable = new(false, 0, []);
}

public record IncidentReportIssueResponse(int Number, string Title, string Url);
