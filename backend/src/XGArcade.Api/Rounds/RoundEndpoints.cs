using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;
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

            // REQ-204: live unique_percent, recalculated on every read. Only
            // fetched for cells this player has actually solved — an
            // unattempted or still-incorrect cell has nothing to show yet.
            var correctlyGuessedCellIds = guesses.Where(g => g.IsCorrect).Select(g => g.CellId).ToList();
            var correctGuessesByCell = correctlyGuessedCellIds.Count == 0
                ? new Dictionary<Guid, List<Guess>>()
                : (await guessRepository.GetCorrectByCellIdsAsync(correctlyGuessedCellIds, cancellationToken))
                    .GroupBy(g => g.CellId)
                    .ToDictionary(group => group.Key, group => group.ToList());

            var cells = instance.Cells
                .OrderBy(c => c.Row).ThenBy(c => c.Col)
                .Select(cell =>
                {
                    guessByCellId.TryGetValue(cell.Id, out var guess);
                    CurrentRoundGuessResponse? guessResponse = null;
                    if (guess is not null)
                    {
                        // Safe: only correct guesses land in
                        // correctGuessesByCell, and a correct ScoreResult
                        // always sets PlayerAnswerId (ScoreResult's own doc
                        // comment).
                        double? uniquePercent = guess.IsCorrect
                            ? UniquenessCalculator.Calculate(correctGuessesByCell[cell.Id], guess.PlayerAnswerId!.Value)
                            : null;

                        // S-018 (REQ-204 extension): ScoringRules.PointsFromUniqueScore
                        // is the exact same call ScoreLockingService makes to lock
                        // FinalPoints at round-close (REQ-205), computed live here
                        // instead so it can only ever drift with uniquePercent
                        // itself, never as a second formula. Still provisional —
                        // see LivePoints' own doc comment below.
                        int? livePoints = uniquePercent is not null
                            ? ScoringRules.PointsFromUniqueScore(uniquePercent.Value)
                            : null;

                        guessResponse = new CurrentRoundGuessResponse(
                            guess.IsCorrect,
                            guess.AttemptCount,
                            guess.IsCorrect || guess.AttemptCount >= GuessRules.MaxAttemptsPerCell,
                            guess.SubmittedName,
                            uniquePercent,
                            livePoints);
                    }

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

// UniquePercent (REQ-204) is null until the guess is correct — an
// incorrect guess has no answer to measure rarity against, and it is
// re-derived on every request (never persisted) until the round closes.
//
// LivePoints (S-018, REQ-204 extension) is likewise null until correct, and
// recomputed on every request from the current UniquePercent — it is an
// estimate that can still move before the round closes, never a preview or
// promise of REQ-205's locked FinalPoints. Callers must not treat this as
// interchangeable with FinalPoints; the naming ("Live"/provisional vs.
// "Final"/locked) is deliberate and must be preserved wherever this value
// is surfaced (API and UI alike).
public record CurrentRoundGuessResponse(bool IsCorrect, int AttemptCount, bool Locked, string SubmittedName, double? UniquePercent, int? LivePoints);
