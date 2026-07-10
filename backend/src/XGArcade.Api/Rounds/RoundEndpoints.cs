using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Scoring;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Rounds;

// REQ-303: the client-facing counterpart to InternalRoundEndpoints — the
// read path a logged-in player needs to actually see and play the current
// round's grid without already knowing its id. Nothing here writes
// anything; POST /rounds/{roundId}/cells/{cellId}/guesses
// (XGArcade.Api.Guesses.GuessEndpoints) remains the only write path.
public static class RoundEndpoints
{
    public static void MapRoundEndpoints(this WebApplication app)
    {
        app.MapGet("/rounds/current", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IRoundRepository roundRepository,
            IGridInstanceRepository gridInstanceRepository,
            IGuessRepository guessRepository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var round = await roundRepository.GetActiveByGameKeyAsync(GridGameModule.XGGridGameKey, now, cancellationToken);
            if (round is null)
            {
                return Results.Problem(
                    title: "No active round",
                    detail: "There is no active round to play right now.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            // Reads GridInstance/GridCell directly, bypassing IGameModule —
            // NOT the same precedent as GridTemplateResolver (that one is
            // about GridTemplate, resolved before generation even runs, and
            // is unrelated to ADR-0003's boundary rule 2). This is a
            // narrower, explicitly documented exception: ADR-0016, scoped to
            // read-only display queries only — generation/scoring must still
            // always go through IGameModule.
            var instance = await gridInstanceRepository.GetInstanceByIdAsync(round.GameInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Round '{round.Id}' references GridInstance '{round.GameInstanceId}' which does not exist.");

            var guesses = await guessRepository.GetByRoundAndUserAsync(round.Id, user.Id, cancellationToken);
            var guessByCellId = guesses.ToDictionary(g => g.CellId);

            var cells = instance.Cells
                .OrderBy(c => c.Row).ThenBy(c => c.Col)
                .Select(cell =>
                {
                    guessByCellId.TryGetValue(cell.Id, out var guess);
                    var guessResponse = guess is null
                        ? null
                        : new CurrentRoundGuessResponse(
                            guess.IsCorrect,
                            guess.AttemptCount,
                            guess.IsCorrect || guess.AttemptCount >= GuessRules.MaxAttemptsPerCell,
                            guess.SubmittedName);

                    return new CurrentRoundCellResponse(
                        cell.Id,
                        cell.Row,
                        cell.Col,
                        cell.RowCategoryType,
                        cell.RowCategoryValue,
                        cell.ColCategoryType,
                        cell.ColCategoryValue,
                        guessResponse);
                })
                .ToList();

            return Results.Ok(new CurrentRoundResponse(round.Id, round.StartTime, round.EndTime, round.AllowGuessChange, cells));
        }).RequireAuthorization();
    }
}

public record CurrentRoundResponse(
    Guid RoundId,
    DateTime StartTime,
    DateTime EndTime,
    bool AllowGuessChange,
    IReadOnlyList<CurrentRoundCellResponse> Cells);

// Guess is null when the requesting player hasn't attempted this cell yet —
// this response only ever carries the requesting player's own guess, never
// another player's (REQ-303).
public record CurrentRoundCellResponse(
    Guid CellId,
    int Row,
    int Col,
    string RowCategoryType,
    string RowCategoryValue,
    string ColCategoryType,
    string ColCategoryValue,
    CurrentRoundGuessResponse? Guess);

public record CurrentRoundGuessResponse(bool IsCorrect, int AttemptCount, bool Locked, string SubmittedName);
