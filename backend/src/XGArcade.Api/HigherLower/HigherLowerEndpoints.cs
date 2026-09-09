using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Games;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGHigherLower;

namespace XGArcade.Api.HigherLower;

// REQ-1504/1505: xG Higher/Lower's own read/write surface — GET
// /higher-lower/current (mirrors XGArcade.Api.Predict.PredictEndpoints'
// shape/auth exactly, same per-game direct-repository-read pattern
// ADR-0016/ADR-0048 already established for a second game module), plus one
// write endpoint. Deliberately NOT routed through
// POST /rounds/{roundId}/cells/{cellId}/guesses (GuessEndpoints) —
// ADR-0096 already establishes the underlying structural-incompatibility
// reasoning (HigherLowerSubmission has no CellId/submitted-name concept at
// all, see that record's own doc comment, so there is nothing
// GuessSubmissionService's per-cell Guess-row shape could even represent);
// the actual exclusion mechanism, GuessSubmissionAllowedGameKeys, was
// introduced later (S-200/ADR-0098, see ServiceRegistration.cs) and omits
// "xg-higher-lower" the same way it already omits "xg-predict". Instead,
// the write endpoint below calls
// IGameModuleResolver.Resolve("xg-higher-lower").ScoreSubmissionAsync
// directly, the same way XGHigherLowerGameModule itself already expects to
// be called (that class's own doc comment).
//
// REQ-1504's gameplay contract this GET endpoint must respect: at any point
// mid-attempt a player sees the current baseline player (value revealed)
// and the next comparator in the fixed sequence (identity revealed, value
// hidden until guessed) — HigherLowerNextComparatorResponse below
// deliberately carries no Value field to enforce that at the DTO shape
// level, not just by convention.
public static class HigherLowerEndpoints
{
    public static void MapHigherLowerEndpoints(this WebApplication app)
    {
        app.MapGet("/higher-lower/current", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IRoundRepository roundRepository,
            IHigherLowerInstanceRepository higherLowerInstanceRepository,
            IPlayerRepository playerRepository,
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
            var round = await roundRepository.GetActiveByGameKeyAsync(XGHigherLowerGameModule.XGHigherLowerGameKey, now, cancellationToken);
            if (round is null)
            {
                return Results.Problem(
                    title: "No active round",
                    detail: "There is no active round to play right now.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            // Reads HigherLowerInstance/HigherLowerComparator/HigherLowerAttempt
            // directly, bypassing IGameModule — same ADR-0016/ADR-0048 scope
            // PredictEndpoints'/PathEndpoints' own doc comments already
            // establish for a per-game read.
            var instance = await higherLowerInstanceRepository.GetInstanceByIdAsync(round.GameInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Round '{round.Id}' references HigherLowerInstance '{round.GameInstanceId}' which does not exist.");

            var attempt = await higherLowerInstanceRepository.GetAttemptAsync(instance.Id, user.Id, cancellationToken);

            // Same implicit "streak 0, baseline = the instance's own fixed
            // starting baseline, not ended" defaulting
            // XGHigherLowerGameModule.ScoreSubmissionAsync itself uses when no
            // attempt row exists yet (HigherLowerAttempt's own doc comment) —
            // never re-derived differently here.
            var streakLength = attempt?.StreakLength ?? 0;
            var currentBaselinePlayerId = attempt?.CurrentBaselinePlayerId ?? instance.BaselinePlayerId;
            var currentBaselineValue = attempt?.CurrentBaselineValue ?? instance.BaselineValue;
            var hasEnded = attempt?.HasEnded ?? false;

            // Comparators is NOT guaranteed pre-sorted by SequencePosition on
            // read (HigherLowerComparator's own doc comment / XGHigherLowerGameModule.
            // ScoreSubmissionAsync's identical sort) — sorted explicitly before
            // indexing into it.
            var orderedComparators = instance.Comparators.OrderBy(c => c.SequencePosition).ToList();
            var nextComparator = hasEnded ? null : orderedComparators[streakLength];

            // Batch-fetch every player name needed for this response in one
            // call, never one per player (docs/coding-guidelines.md).
            var playerIdsToFetch = new List<Guid> { currentBaselinePlayerId };
            if (nextComparator is not null)
                playerIdsToFetch.Add(nextComparator.PlayerId);
            var players = await playerRepository.GetPlayersByIdsAsync(playerIdsToFetch.Distinct().ToList(), cancellationToken);

            var baseline = new HigherLowerBaselineResponse(
                currentBaselinePlayerId, players[currentBaselinePlayerId].FullName, currentBaselineValue);

            // Deliberately no Value populated here — REQ-1504's "next
            // comparator shown, value hidden" contract, enforced at the DTO
            // shape level (HigherLowerNextComparatorResponse has no Value
            // field at all).
            var nextComparatorResponse = nextComparator is null
                ? null
                : new HigherLowerNextComparatorResponse(nextComparator.PlayerId, players[nextComparator.PlayerId].FullName);

            return Results.Ok(new CurrentHigherLowerResponse(
                round.Id,
                round.SequenceNumber,
                round.StartTime,
                round.EndTime,
                instance.StatCategory,
                instance.Comparators.Count,
                streakLength,
                hasEnded,
                baseline,
                nextComparatorResponse));
        }).RequireAuthorization();

        app.MapPost("/higher-lower/guesses", async (
            SubmitHigherLowerGuessRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IRoundRepository roundRepository,
            IHigherLowerInstanceRepository higherLowerInstanceRepository,
            IPlayerRepository playerRepository,
            IGameModuleResolver gameModuleResolver,
            TimeProvider timeProvider,
            ILogger<HigherLowerEndpointsLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            var now = timeProvider.GetUtcNow().UtcDateTime;
            // No separate id in this endpoint's own route — progression is
            // always against the caller's own current attempt against the
            // active round's instance, mirroring how HigherLowerSubmission
            // itself has no CellId (that record's own doc comment).
            var round = await roundRepository.GetActiveByGameKeyAsync(XGHigherLowerGameModule.XGHigherLowerGameKey, now, cancellationToken);
            if (round is null)
            {
                return Results.Problem(
                    title: "No active round",
                    detail: "There is no active round to play right now.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            ScoreResult result;
            try
            {
                result = await gameModuleResolver.Resolve(XGHigherLowerGameModule.XGHigherLowerGameKey)
                    .ScoreSubmissionAsync(round.GameInstanceId, user.Id, new HigherLowerSubmission(request.Direction), cancellationToken);
            }
            catch (HigherLowerAttemptEndedException ex)
            {
                return Results.Problem(title: "Attempt has ended", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (HigherLowerScoringException ex)
            {
                // instanceId didn't resolve to a real HigherLowerInstance — a
                // malformed/stale request, not an ordinary gameplay outcome.
                // Logged server-side (coding-guidelines.md), same discipline
                // PredictEndpoints/GuessEndpoints use for their own
                // GameEntityNotFoundException-derived not-found cases.
                logger.LogError(ex, "xG Higher/Lower guess submission failed: instance not found.");
                return Results.NotFound();
            }

            // Re-read the now-persisted attempt for the resulting
            // StreakLength/HasEnded (guaranteed non-null immediately after a
            // successful submission — HigherLowerInstanceRepository.
            // SaveAttemptAsync always creates the row on a participant's
            // first-ever guess).
            var newAttempt = await higherLowerInstanceRepository.GetAttemptAsync(round.GameInstanceId, user.Id, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"HigherLowerAttempt for instance '{round.GameInstanceId}', user '{user.Id}' was not persisted by ScoreSubmissionAsync.");

            // The comparator just guessed is identified by PlayerId ==
            // result.PlayerAnswerId — the identity the authoritative
            // ScoreSubmissionAsync call itself just resolved from its own
            // internal, unlocked read of the attempt — rather than by a
            // SequencePosition snapshotted before the call. instance.
            // Comparators is a fixed, immutable sequence set once at
            // generation time (never mutated), and REQ-1502's no-repeat-
            // within-sequence rule guarantees this lookup is unique. Looking
            // up by a pre-call position instead would be race-prone: two
            // overlapping requests for the same user (a double-tap, or a
            // client retry after a timeout) could otherwise pair a correct
            // RevealedPlayerId with a stale (wrong) RevealedValue.
            var instance = await higherLowerInstanceRepository.GetInstanceByIdAsync(round.GameInstanceId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Round '{round.Id}' references HigherLowerInstance '{round.GameInstanceId}' which does not exist.");
            // Always set here: ScoreSubmissionAsync's PlayerAnswerId is
            // always non-null for this game (XGHigherLowerGameModule's own
            // doc comment — unlike xG Grid/xG Path, there is no
            // no-candidate-matched case).
            var guessedComparator = instance.Comparators.Single(c => c.PlayerId == result.PlayerAnswerId!.Value);

            // Safe: always sets PlayerAnswerId (ScoreResult's own doc
            // comment / XGHigherLowerGameModule.ScoreSubmissionAsync's own
            // doc comment).
            var players = await playerRepository.GetPlayersByIdsAsync([result.PlayerAnswerId!.Value], cancellationToken);

            return Results.Ok(new SubmitHigherLowerGuessResponse(
                result.IsCorrect,
                // Safe: always sets PlayerAnswerId (ScoreResult's own doc
                // comment).
                result.PlayerAnswerId!.Value,
                players[result.PlayerAnswerId.Value].FullName,
                guessedComparator.Value,
                newAttempt.StreakLength,
                newAttempt.HasEnded));
        }).RequireAuthorization();
    }
}

// DTOs at the API boundary (coding-guidelines.md) — HigherLowerInstance/
// HigherLowerComparator/HigherLowerAttempt (XGArcade.Data.Entities) are
// never serialized directly.

public record CurrentHigherLowerResponse(
    Guid RoundId,
    int SequenceNumber,
    DateTime StartTime,
    DateTime EndTime,
    string StatCategory,
    int ComparatorCount,
    int StreakLength,
    bool HasEnded,
    HigherLowerBaselineResponse Baseline,
    HigherLowerNextComparatorResponse? NextComparator);

// The current baseline's value is always revealed — REQ-1504.
public record HigherLowerBaselineResponse(Guid PlayerId, string Name, int Value);

// Deliberately no Value field — REQ-1504's "next comparator shown, value
// hidden until guessed" contract, enforced at the DTO shape level rather
// than by convention alone. Null on CurrentHigherLowerResponse when the
// attempt has already ended (nothing left to guess).
public record HigherLowerNextComparatorResponse(Guid PlayerId, string Name);

public record SubmitHigherLowerGuessRequest(HigherLowerDirection Direction);

// RevealedPlayerId/RevealedPlayerName/RevealedValue: the just-guessed
// comparator's real identity/value, revealed on both a correct AND an
// incorrect guess (REQ-1504) — never withheld either way.
public record SubmitHigherLowerGuessResponse(
    bool IsCorrect,
    Guid RevealedPlayerId,
    string RevealedPlayerName,
    int RevealedValue,
    int StreakLength,
    bool HasEnded);

// Pure log-category marker for ILogger<T> — same pattern as
// PredictEndpointsLogCategory/GuessEndpointsLogCategory.
internal sealed class HigherLowerEndpointsLogCategory;
