using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Games;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGPredict;

namespace XGArcade.Api.Predict;

// REQ-1302/1303/1306: xG Predict's own read/write surface — GET
// /predict/current (mirrors XGArcade.Api.Path.PathEndpoints.MapPathEndpoints's
// shape/auth exactly, ADR-0016/ADR-0048's per-game direct-repository-read
// pattern), plus two write endpoints. Unlike xG Grid/xG Path,
// POST /rounds/{roundId}/cells/{cellId}/guesses (GuessEndpoints) is NOT the
// write path here — ADR-0096 already rules out routing predictions through
// Guess/IGuessSubmissionService (structurally incompatible: two integers,
// no attempt cap, no synchronous correctness). Instead, the two write
// endpoints below call IGameModuleResolver.Resolve("xg-predict").
// ScoreSubmissionAsync directly, the same way XGPredictGameModule itself
// already expects to be called (ADR-0096 §3's own Context note).
//
// ADR-0098: the POST /predict/confirm endpoint below is the entire
// implementation of REQ-1306's per-player "confirm and lock" action — see
// that ADR for why the check lives here (API layer) rather than inside
// XGPredictGameModule.ScoreSubmissionAsync, and why it's backed by a new
// PredictPlayerLock table rather than a column on PredictMatchPrediction.
public static class PredictEndpoints
{
    public static void MapPredictEndpoints(this WebApplication app)
    {
        app.MapGet("/predict/current", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IRoundRepository roundRepository,
            IPredictInstanceRepository predictInstanceRepository,
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
            var round = await roundRepository.GetActiveByGameKeyAsync(XGPredictGameModule.XGPredictGameKey, now, cancellationToken);
            if (round is null)
            {
                return Results.Problem(
                    title: "No active round",
                    detail: "There is no active round to play right now.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            // Reads PredictInstance/PredictMatch directly, bypassing
            // IGameModule — same ADR-0016/ADR-0048 scope PathEndpoints'
            // own doc comment already establishes for a second game module.
            var instance = await predictInstanceRepository.GetInstanceByIdAsync(round.GameInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Round '{round.Id}' references PredictInstance '{round.GameInstanceId}' which does not exist.");

            // REQ-1303: PredictInstance.LockInstant is the single shared
            // formula XGPredictGameModule.ScoreSubmissionAsync also reads —
            // never re-derived differently here — so this flag is always
            // consistent with what a submission attempt would actually see.
            var locked = now >= instance.LockInstant;

            // REQ-1306/ADR-0098: independent of the round-wide `locked`
            // flag above — a player can have this true well before
            // `locked` becomes true.
            var confirmedLocked = await predictInstanceRepository.IsPlayerLockedAsync(instance.Id, user.Id, cancellationToken);

            // Bulk fetch, once for the whole instance — same "one query for
            // the batch, never one per cell" discipline PathEndpoints
            // already follows for its own per-instance reads.
            var predictions = await predictInstanceRepository.GetPredictionsForInstanceAndUserAsync(instance.Id, user.Id, cancellationToken);
            var predictionByMatchId = predictions.ToDictionary(p => p.PredictMatchId);

            var matches = instance.Matches
                .OrderBy(m => m.KickoffUtc)
                .Select(m =>
                {
                    predictionByMatchId.TryGetValue(m.Id, out var prediction);
                    // Only ever the requesting player's own prediction —
                    // same "never another player's state" contract
                    // PathEndpoints/RoundEndpoints already establish.
                    return new CurrentPredictMatchResponse(
                        m.Id, m.HomeTeamName, m.AwayTeamName, m.KickoffUtc,
                        prediction?.HomeGoals, prediction?.AwayGoals);
                })
                .ToList();

            return Results.Ok(new CurrentPredictResponse(
                round.Id, round.SequenceNumber, round.StartTime, round.EndTime, locked, confirmedLocked, matches));
        }).RequireAuthorization();

        app.MapPost("/predict/matches/{matchId:guid}/predictions", async (
            Guid matchId,
            SubmitPredictionRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IRoundRepository roundRepository,
            IPredictInstanceRepository predictInstanceRepository,
            IGameModuleResolver gameModuleResolver,
            TimeProvider timeProvider,
            ILogger<PredictEndpointsLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            var now = timeProvider.GetUtcNow().UtcDateTime;
            // The active xg-predict round is this endpoint's only anchor
            // from a bare matchId back to a PredictInstanceId — a match
            // belonging to any other (already-closed or not-yet-active)
            // round simply resolves to "no active round" here, matching
            // GET /predict/current's own not-found convention rather than
            // adding a second, parallel matchId->instance lookup path.
            var round = await roundRepository.GetActiveByGameKeyAsync(XGPredictGameModule.XGPredictGameKey, now, cancellationToken);
            if (round is null)
            {
                return Results.Problem(
                    title: "No active round",
                    detail: "There is no active round to play right now.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            // REQ-1306/ADR-0098: this per-player gate has no knowledge
            // inside XGPredictGameModule.ScoreSubmissionAsync (which only
            // ever enforces REQ-1303's round-wide lock) — this endpoint is
            // the only place it can be enforced, checked before ever
            // attempting to submit.
            if (await predictInstanceRepository.IsPlayerLockedAsync(round.GameInstanceId, user.Id, cancellationToken))
            {
                return Results.Problem(
                    title: "Predictions already confirmed and locked",
                    detail: "You have already confirmed and locked your predictions for this round and can no longer change them.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                await gameModuleResolver.Resolve(XGPredictGameModule.XGPredictGameKey).ScoreSubmissionAsync(
                    round.GameInstanceId, user.Id, new PredictionSubmission(matchId, request.HomeGoals, request.AwayGoals), cancellationToken);
            }
            catch (PredictInvalidSubmissionException ex)
            {
                // REQ-1302: negative goal counts — an ordinary rejected
                // submission, not an id-resolution failure.
                return Results.Problem(
                    title: "Invalid prediction",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
            catch (PredictRoundLockedException ex)
            {
                // REQ-1303: the round-wide automatic lock at the earliest
                // match's kickoff — rejected for every match in the round,
                // including one whose own individual kickoff hasn't
                // happened yet (that's the whole point of this exception).
                return Results.Problem(
                    title: "Round is locked",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (PredictScoringException ex)
            {
                // matchId didn't resolve to a real PredictMatch in this
                // instance — a malformed/stale request, not an ordinary
                // gameplay outcome. Logged server-side (coding-guidelines.md),
                // same discipline GuessEndpoints uses for
                // GameEntityNotFoundException.
                logger.LogError(ex, "Prediction submission failed: match not found in the active instance.");
                return Results.NotFound();
            }

            return Results.Ok(new PredictionSubmissionResponse(matchId, request.HomeGoals, request.AwayGoals));
        }).RequireAuthorization();

        app.MapPost("/predict/confirm", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IRoundRepository roundRepository,
            IPredictInstanceRepository predictInstanceRepository,
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
            var round = await roundRepository.GetActiveByGameKeyAsync(XGPredictGameModule.XGPredictGameKey, now, cancellationToken);
            if (round is null)
            {
                return Results.Problem(
                    title: "No active round",
                    detail: "There is no active round to play right now.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var instance = await predictInstanceRepository.GetInstanceByIdAsync(round.GameInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Round '{round.Id}' references PredictInstance '{round.GameInstanceId}' which does not exist.");

            if (await predictInstanceRepository.IsPlayerLockedAsync(instance.Id, user.Id, cancellationToken))
            {
                return Results.Problem(
                    title: "Predictions already confirmed and locked",
                    detail: "You have already confirmed and locked your predictions for this round.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // REQ-1306's own precondition: "the round has not yet locked"
            // (REQ-1303) — same PredictInstance.LockInstant GET
            // /predict/current uses.
            if (now >= instance.LockInstant)
            {
                return Results.Problem(
                    title: "Round is locked",
                    detail: "This round has already locked automatically — there is nothing left to confirm.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // REQ-1306's other precondition: a stored prediction for every
            // one of this instance's matches.
            var predictions = await predictInstanceRepository.GetPredictionsForInstanceAndUserAsync(instance.Id, user.Id, cancellationToken);
            var predictedMatchIds = predictions.Select(p => p.PredictMatchId).ToHashSet();
            var missingCount = instance.Matches.Count(m => !predictedMatchIds.Contains(m.Id));
            if (missingCount > 0)
            {
                return Results.Problem(
                    title: "Not all predictions submitted",
                    detail: $"You must submit a prediction for all {instance.Matches.Count} matches before confirming " +
                        $"({missingCount} still missing).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            await predictInstanceRepository.LockPlayerPredictionsAsync(instance.Id, user.Id, now, cancellationToken);

            return Results.Ok(new ConfirmPredictionsResponse(round.Id, now));
        }).RequireAuthorization();
    }
}

// DTOs at the API boundary (coding-guidelines.md) — PredictMatch/
// PredictMatchPrediction (XGArcade.Data.Entities) are never serialized
// directly.

// Locked (REQ-1303): the round-wide automatic lock, true from the earliest
// match's own kickoff onward — the same instant XGPredictGameModule.
// ScoreSubmissionAsync itself locks at. ConfirmedLocked (REQ-1306):
// independent per-player early lock; can be true while Locked is still
// false, but never the other way around in practice (Locked eventually
// becomes true for everyone regardless of ConfirmedLocked).
public record CurrentPredictResponse(
    Guid RoundId,
    int SequenceNumber,
    DateTime StartTime,
    DateTime EndTime,
    bool Locked,
    bool ConfirmedLocked,
    IReadOnlyList<CurrentPredictMatchResponse> Matches);

// HomeGoals/AwayGoals are null when the requesting player has not yet
// predicted this match — never another player's stored prediction.
public record CurrentPredictMatchResponse(
    Guid MatchId,
    string HomeTeamName,
    string AwayTeamName,
    DateTime KickoffUtc,
    int? HomeGoals,
    int? AwayGoals);

public record SubmitPredictionRequest(int HomeGoals, int AwayGoals);

// A small, deliberately game-specific confirmation DTO — NOT ScoreResult/
// SubmitGuessResponse (see XGPredictGameModule.ScoreSubmissionAsync's own
// doc comment: those carry Grid/Path-specific fields, like IsCorrect, that
// would be actively misleading for a prediction that is never "correct" or
// "wrong" at submission time). Simply echoes back what was persisted.
public record PredictionSubmissionResponse(Guid MatchId, int HomeGoals, int AwayGoals);

public record ConfirmPredictionsResponse(Guid RoundId, DateTime LockedAt);

// Pure log-category marker for ILogger<T> — same pattern as
// GuessEndpointsLogCategory/InternalRoundEndpoints.RoundGenerationLogCategory.
internal sealed class PredictEndpointsLogCategory;
