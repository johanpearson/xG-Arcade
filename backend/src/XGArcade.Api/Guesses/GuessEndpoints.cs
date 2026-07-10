using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Scoring;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Guesses;

public static class GuessEndpoints
{
    // REQ-201: a logged-in player submits a guess for a specific cell within
    // an active round.
    public static void MapGuessEndpoints(this WebApplication app)
    {
        app.MapPost("/rounds/{roundId:guid}/cells/{cellId:guid}/guesses", async (
            Guid roundId,
            Guid cellId,
            SubmitGuessRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IGuessSubmissionService guessSubmissionService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.SubmittedName))
            {
                return Results.Problem(
                    title: "A guess is required",
                    detail: "submittedName must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            GuessSubmissionResult result;
            try
            {
                result = await guessSubmissionService.SubmitGuessAsync(
                    roundId, user.Id, cellId, request.SubmittedName, cancellationToken);
            }
            catch (GuessScoringException ex)
            {
                // The cellId didn't resolve to a real cell in this round's
                // grid instance — a malformed/stale request, not an ordinary
                // incorrect-guess outcome.
                return Results.Problem(
                    title: "Cell not found",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status404NotFound);
            }

            // REQ-202: every rejection reason is distinct and specific —
            // never a generic "can't change" message.
            return result.Outcome switch
            {
                GuessSubmissionOutcome.Accepted => Results.Ok(
                    new SubmitGuessResponse(result.IsCorrect, result.AttemptCount, result.Locked)),
                GuessSubmissionOutcome.RoundNotFound => Results.NotFound(),
                GuessSubmissionOutcome.RoundNotActive => Results.Problem(
                    title: "Round is not active",
                    detail: "Guesses can only be submitted while the round is active.",
                    statusCode: StatusCodes.Status409Conflict),
                GuessSubmissionOutcome.CellAlreadySolved => Results.Problem(
                    title: "Cell already solved",
                    detail: "This cell already has a correct guess and is locked.",
                    statusCode: StatusCodes.Status409Conflict),
                GuessSubmissionOutcome.NoAttemptsRemaining => Results.Problem(
                    title: "No attempts remaining",
                    detail: "Both guess attempts for this cell have already been used.",
                    statusCode: StatusCodes.Status409Conflict),
                GuessSubmissionOutcome.GuessChangeNotAllowed => Results.Problem(
                    title: "Guess changes are not allowed",
                    detail: "This round does not allow changing an already-submitted guess.",
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
            };
        }).RequireAuthorization();
    }
}

public record SubmitGuessRequest(string SubmittedName);

public record SubmitGuessResponse(bool IsCorrect, int AttemptCount, bool Locked);
