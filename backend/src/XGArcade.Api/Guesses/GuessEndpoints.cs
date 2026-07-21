using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Games;
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
            ILogger<GuessEndpointsLogCategory> logger,
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
                    roundId, user.Id, cellId, request.SubmittedName, request.ChosenPlayerId, cancellationToken);
            }
            catch (GuessScoringException ex)
            {
                // The cellId didn't resolve to a real cell in this round's
                // grid instance — a malformed/stale request, not an ordinary
                // incorrect-guess outcome. Logged server-side (coding-
                // guidelines.md), same discipline InternalRoundEndpoints
                // uses for GridGenerationException; the client gets the
                // same bare 404 as RoundNotFound below — both are plain
                // "this id doesn't resolve to anything" outcomes, so they
                // share one response shape rather than one being a richer
                // Problem body than the other for no real reason.
                logger.LogError(ex, "Guess submission failed: cell not found.");
                return Results.NotFound();
            }

            // REQ-202: every rejection reason is distinct and specific —
            // never a generic "can't change" message.
            return result.Outcome switch
            {
                GuessSubmissionOutcome.Accepted => Results.Ok(
                    new SubmitGuessResponse(
                        result.IsCorrect, result.AttemptCount, result.Locked, result.ResolvedPlayerName, result.ResolvedPlayerPhotoUrl, Candidates: null)),
                // REQ-209/REQ-210: still a 200 (nothing about the request was
                // wrong — the guess just resolved to more than one fitting
                // candidate), but with Candidates populated and every other
                // field left at its default/false/0/null — nothing was
                // actually scored, and showing this prompt never consumed an
                // attempt. Candidates != null is the frontend's unambiguous
                // signal to render the picker instead of a normal result.
                GuessSubmissionOutcome.NeedsDisambiguation => Results.Ok(
                    new SubmitGuessResponse(
                        IsCorrect: false, AttemptCount: 0, Locked: false, ResolvedPlayerName: null, ResolvedPlayerPhotoUrl: null,
                        Candidates: result.DisambiguationCandidates!
                            .Select(c => new DisambiguationCandidateResponse(c.PlayerId, c.Name, c.DistinguishingAttributes))
                            .ToList())),
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

// ChosenPlayerId (REQ-209/REQ-210): null on an ordinary guess. Set only on a
// resubmission answering a disambiguation prompt from a prior response on
// this same cell (SubmitGuessResponse.Candidates) — the id of whichever
// candidate the player picked. Always re-verified server-side
// (GridGameModule), never trusted blindly.
public record SubmitGuessRequest(string SubmittedName, Guid? ChosenPlayerId = null);

// ResolvedPlayerName (frontend name-display fix): the canonical, properly-
// cased player name, only ever set when IsCorrect — never a substitute for
// SubmittedName on an incorrect guess, which the frontend now shows no name
// for at all.
//
// ResolvedPlayerPhotoUrl (REQ-214): the resolved player's Wikidata photo,
// additive alongside ResolvedPlayerName — same only-when-IsCorrect rule,
// plus null whenever no Wikidata photo exists for this player. Absent/null
// is the normal, error-free "no photo" case; the frontend falls back to
// today's text-only reveal (REQ-212) whenever this is null.
//
// Candidates (REQ-209): null on every ordinary (scored) response — this is
// the frontend's unambiguous signal to distinguish "this response means:
// show a picker" (Candidates != null) from "this response means: scored,
// render normally" (Candidates == null). Non-null and non-empty only when
// the guess resolved to more than one fitting candidate; in that case
// IsCorrect/AttemptCount/Locked/ResolvedPlayerName/ResolvedPlayerPhotoUrl are
// all left at their default/false/0/null values — nothing was actually
// scored yet, and REQ-210 guarantees showing this prompt never consumed an
// attempt. The player must resubmit with ChosenPlayerId set to one of these
// candidates' PlayerId to actually score the guess.
public record SubmitGuessResponse(
    bool IsCorrect,
    int AttemptCount,
    bool Locked,
    string? ResolvedPlayerName,
    string? ResolvedPlayerPhotoUrl,
    IReadOnlyList<DisambiguationCandidateResponse>? Candidates);

// REQ-209: one entry per candidate the player must choose between — mirrors
// Core.Games.DisambiguationCandidate at the API boundary (DTOs at the API
// boundary, never a domain/Core type serialized directly).
public record DisambiguationCandidateResponse(Guid PlayerId, string Name, IReadOnlyList<string> DistinguishingAttributes);

// Pure log-category marker for ILogger<T> — same pattern as
// InternalRoundEndpoints.RoundGenerationLogCategory.
internal sealed class GuessEndpointsLogCategory;
