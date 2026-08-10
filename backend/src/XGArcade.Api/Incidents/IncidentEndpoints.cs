using System.Security.Claims;
using System.Threading.RateLimiting;
using XGArcade.Api.Auth;
using XGArcade.Core.IncidentReporting;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Incidents;

// REQ-903/ADR-0064/COMP-12: POST /incidents — a logged-in, non-guest
// player's in-app bug report, turned into a real GitHub issue in this repo
// server-side. Mirrors XGArcade.Api.Suggestions.SuggestionEndpoints's
// resolve-caller/reject-guest shape (ClaimsPrincipal + IUserRepository
// .GetByAuthProviderUserIdAsync, Results.Problem for every rejection) — the
// same REQ-215 precedent ADR-0064 explicitly names.
//
// Structured-fields addition (2026-08-10, same day as the original build,
// requested directly): Title/Screen are now mandatory, separate fields
// rather than folded into free-text Description — so every issue this
// endpoint creates follows the same shape regardless of what the player
// typed. IncidentReportService (XGArcade.Core.IncidentReporting) owns
// turning these fields into the actual formatted GitHub issue body — this
// file only validates the raw request shape.
public static class IncidentEndpoints
{
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 4000;
    public const int ScreenMaxLength = 50;
    public const int EnvironmentMaxLength = 200;

    public static void MapIncidentEndpoints(this WebApplication app)
    {
        app.MapPost("/incidents", async (
            SubmitIncidentReportRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IIncidentReportService incidentReportService,
            PartitionedRateLimiter<Guid> incidentReportRateLimiter,
            ILogger<IncidentEndpointsLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.Problem(
                    title: "A title is required",
                    detail: "title must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request.Title.Length > TitleMaxLength)
            {
                return Results.Problem(
                    title: "Title is too long",
                    detail: $"title must be at most {TitleMaxLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.IsNullOrWhiteSpace(request.Description))
            {
                return Results.Problem(
                    title: "A description is required",
                    detail: "description must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request.Description.Length > DescriptionMaxLength)
            {
                return Results.Problem(
                    title: "Description is too long",
                    detail: $"description must be at most {DescriptionMaxLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Screen is mandatory (a dropdown, client-side — see
            // IncidentReportDialog.tsx) but this is a plain string field on
            // the wire, so it's validated the same as any other client
            // input, never trusted just because the UI is a <select>.
            if (string.IsNullOrWhiteSpace(request.Screen))
            {
                return Results.Problem(
                    title: "A screen is required",
                    detail: "screen must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request.Screen.Length > ScreenMaxLength)
            {
                return Results.Problem(
                    title: "Screen is too long",
                    detail: $"screen must be at most {ScreenMaxLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (request.Environment is { Length: > EnvironmentMaxLength })
            {
                return Results.Problem(
                    title: "Environment is too long",
                    detail: $"environment must be at most {EnvironmentMaxLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            // REQ-903's guest boundary — enforced here server-side
            // regardless of what the client UI shows, the same "advertised-
            // but-disabled, never trusted from the client" rule REQ-215
            // already established for a different write path.
            if (user.IsGuest)
            {
                return Results.Problem(
                    title: "Guest accounts cannot file incident reports",
                    detail: "Register for a full account to report a problem.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // REQ-903's per-user rate limit, checked only once the caller is
            // known — a plain PartitionedRateLimiter<Guid> (Program.cs),
            // not a global named RateLimiter policy like auth-signup/
            // auth-login/auth-guest. Those three are IP-partitioned and
            // deliberately evaluated by the UseRateLimiter() middleware
            // *before* UseAuthentication() runs (see Program.cs's REQ-606
            // comment on that ordering) — this endpoint needs the caller's
            // own User.Id as the partition key, which isn't resolved until
            // the two lookups above have already run, well past where that
            // middleware would evaluate a partition key function. Checking
            // it directly here, after the caller is known, avoids
            // reordering that global pipeline (which the anonymous auth-*
            // policies depend on) just for this one endpoint. Same
            // FixedWindowRateLimiterOptions shape (fixed window, no
            // queueing) and 429 rejection as those policies otherwise.
            using var lease = incidentReportRateLimiter.AttemptAcquire(user.Id);
            if (!lease.IsAcquired)
            {
                return Results.Problem(
                    title: "Too many reports",
                    detail: "You've submitted several reports recently. Please wait a bit before submitting another.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var result = await incidentReportService.SubmitAsync(
                user.Id,
                request.Title.Trim(),
                request.Description.Trim(),
                request.Screen.Trim(),
                request.Environment?.Trim(),
                cancellationToken);

            if (!result.Success)
            {
                // ADR-0064: never leak GitHub's own error detail to the
                // client — GitHubIssueClient already logged the full detail
                // server-side; FailureReason here is already a client-safe
                // summary. No partial/duplicate issue exists at this point
                // either way (GitHubIssueClient's own contract).
                logger.LogError("Incident report failed for user {UserId}: {FailureReason}", user.Id, result.FailureReason);
                return Results.Problem(
                    title: "Could not submit your report",
                    detail: result.FailureReason ?? "Please try again later.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // IssueUrl is never null here: GitHubIssueCreationResult.Ok
            // always sets it, and result.Success is only true via that
            // factory (GitHubIssueClient's own contract).
            return Results.Ok(new SubmitIncidentReportResponse(result.IssueUrl!));
        }).RequireAuthorization();
    }
}

// Title: a short, player-written summary — used as the created GitHub
// issue's own title verbatim (IncidentReportService), so every issue this
// endpoint creates is scannable in GitHub's issue list, not just inside
// the body.
//
// Screen: which screen the problem happened on — a mandatory, closed set
// of values on the client (a <select>, IncidentReportDialog.tsx's
// INCIDENT_REPORT_SCREEN_OPTIONS) pre-filled from wherever the report was
// opened, but re-validated here as an ordinary string; an unrecognized
// value is still accepted (this endpoint doesn't hardcode the option
// list — the client owns that), just bounded in length.
//
// Environment: which deployed environment the player was using (e.g. the
// frontend's own origin URL) — computed by the client automatically
// (IncidentReportDialog.tsx), never typed by the player, optional here
// only because a caller other than the shipped UI might omit it.
public record SubmitIncidentReportRequest(string Title, string Description, string Screen, string? Environment);

// IssueUrl: the created GitHub issue's own URL — not secret, safe to
// return (REQ-903's explicit acceptance criterion).
public record SubmitIncidentReportResponse(string IssueUrl);

// Pure log-category marker for ILogger<T> — same pattern as
// SuggestionEndpoints.cs's SuggestionEndpointsLogCategory.
internal sealed class IncidentEndpointsLogCategory;
