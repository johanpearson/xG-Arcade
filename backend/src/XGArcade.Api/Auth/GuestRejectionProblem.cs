using Microsoft.AspNetCore.Mvc;

namespace XGArcade.Api.Auth;

// Shared "guest account can't do X" 403 shape — extracted once this diff's
// AuthController.cs (REQ-714) and AvatarEndpoints.cs (REQ-722) additions
// became the 4th/5th near-identical occurrence of
// `if (user.IsGuest) { return <403 Problem>; }` in this API (alongside
// SuggestionEndpoints.cs's REQ-215 check and IncidentEndpoints.cs's REQ-903
// check) — docs/coding-guidelines.md's Code health budget says not to wait
// for a 5th copy before extracting, and this diff crosses from 3 to 5 in
// one go. Deliberately keeps each call site's own title/detail wording,
// which differs per REQ/feature ("submit suggestions" vs. "edit their
// display name" vs. ...) — only the (title, detail) -> 403 Problem plumbing
// is shared, not the copy.
//
// Two shapes because this API mixes minimal-API endpoints (IResult,
// Results.Problem) with one MVC controller (IActionResult,
// ControllerBase.Problem): GuestRejectionResult.Problem is the minimal-API
// shape used by SuggestionEndpoints.cs/IncidentEndpoints.cs/
// AvatarEndpoints.cs; ControllerBase.GuestRejectionProblem below is the MVC
// shape used by AuthController.cs. The MVC extension calls the controller's
// own Problem(...) rather than constructing a ProblemDetails/ObjectResult
// by hand, so it goes through the exact same ProblemDetailsFactory/
// HttpContext plumbing every other Problem(...) call in that controller
// (e.g. DisplayNameConflictProblem) already uses — no risk of a subtly
// different response shape (missing "instance"/traceId/etc.).
public static class GuestRejectionResult
{
    public static IResult Problem(string title, string detail) =>
        Results.Problem(title: title, detail: detail, statusCode: StatusCodes.Status403Forbidden);
}

public static class GuestRejectionControllerExtensions
{
    public static ObjectResult GuestRejectionProblem(this ControllerBase controller, string title, string detail) =>
        controller.Problem(title: title, detail: detail, statusCode: StatusCodes.Status403Forbidden);
}
