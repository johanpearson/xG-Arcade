using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Suggestions;

// REQ-215/ADR-0052 (S-089): submission-only half of the player-suggestion
// pipeline — REQ-509/510's admin list/review/commit/reject endpoints are
// S-090, in the sibling XGArcade.Api.Admin.AdminSuggestionEndpoints, not
// this file. Mirrors XGArcade.Api.Guesses.
// GuessEndpoints's route shape (same {roundId}/{cellId} pair) and minimal-
// API conventions (ClaimsPrincipal + IUserRepository.
// GetByAuthProviderUserIdAsync to resolve the caller, Results.Problem for
// every rejection).
//
// Architecture-review fix (post-S-089): the original commit resolved a
// cell's row/col category types via a direct IGridInstanceRepository/
// GridCell read from this Api-layer file — a boundary violation, since
// every other business-logic path that needs cell data
// (GridGameModule.ScoreSubmissionAsync, ScoreLockingService.
// MaterializeUnansweredCellsAsync, GetMaxAttemptsForCellAsync) goes through
// IGameModule, resolved by Round.GameKey via IGameModuleResolver (ADR-0003).
// This file now follows that same resolution path — see GuessSubmissionService
// (XGArcade.Core.Scoring) for the identical roundId -> Round ->
// gameModuleResolver.Resolve(round.GameKey) shape this mirrors.
public static class SuggestionEndpoints
{
    public static void MapSuggestionEndpoints(this WebApplication app)
    {
        app.MapPost("/rounds/{roundId:guid}/cells/{cellId:guid}/suggestions", async (
            Guid roundId,
            Guid cellId,
            SubmitSuggestionRequest request,
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IRoundRepository roundRepository,
            IGameModuleResolver gameModuleResolver,
            IPlayerSuggestionRepository playerSuggestionRepository,
            ILogger<SuggestionEndpointsLogCategory> logger,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.PlayerName))
            {
                return Results.Problem(
                    title: "A player name is required",
                    detail: "playerName must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // REQ-215's own validation criteria: "at least one club... and
            // the nationality... rejected with a clear validation error if
            // either is missing." Blank entries (e.g. an accidental empty
            // string in the array) don't count as a real club.
            var clubs = (request.Clubs ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToList();
            if (clubs.Count == 0)
            {
                return Results.Problem(
                    title: "At least one club is required",
                    detail: "clubs must contain at least one non-empty value.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.IsNullOrWhiteSpace(request.Nationality))
            {
                return Results.Problem(
                    title: "A nationality is required",
                    detail: "nationality must not be empty.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var authProviderUserId = principal.GetAuthProviderUserId();
            if (authProviderUserId is null)
                return Results.Unauthorized();

            var user = await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
            if (user is null)
                return Results.Unauthorized();

            // REQ-215's "Guest vs. non-guest visibility" criteria: the
            // frontend advertises-but-disables the entry point for a guest,
            // but the restriction must be enforced here regardless of what
            // the client sent — a guest gets rejected even with a crafted
            // direct request. GuestRejectionResult.Problem
            // (XGArcade.Api.Auth.GuestRejectionProblem.cs) is the shared
            // minimal-API 403 shape this check, IncidentEndpoints.cs's
            // REQ-903 check, and AvatarEndpoints.cs's/AuthController.cs's
            // REQ-722/REQ-714 checks all use.
            if (user.IsGuest)
            {
                return GuestRejectionResult.Problem(
                    title: "Guest accounts cannot submit suggestions",
                    detail: "Register for a full account to suggest a correction.");
            }

            // Authoritative row/col category types, resolved server-side
            // rather than trusted from the request — same "never trust the
            // client for data that becomes a stored record" discipline
            // GuessEndpoints' ChosenPlayerId re-verification already
            // follows, applied here to context fields instead of a
            // correctness check.
            //
            // Resolved through IGameModule (ADR-0003), exactly the roundId ->
            // Round -> IGameModuleResolver.Resolve(round.GameKey) shape
            // GuessSubmissionService already uses — never a direct
            // IGridInstanceRepository/GridCell read from this Api-layer file
            // (see this class's own doc comment for why that was a boundary
            // violation in the original S-089 commit).
            var round = await roundRepository.GetByIdAsync(roundId, cancellationToken);
            if (round is null)
                return Results.NotFound();

            var gameModule = gameModuleResolver.Resolve(round.GameKey);

            CellCategoryTypes categoryTypes;
            try
            {
                categoryTypes = await gameModule.GetCellCategoryTypesAsync(round.GameInstanceId, cellId, cancellationToken);
            }
            catch (GameEntityNotFoundException ex)
            {
                // The cellId didn't resolve to a real cell within this
                // round's game instance — a malformed/stale request, not an
                // ordinary rejection outcome. Logged server-side (coding-
                // guidelines.md), same GameEntityNotFoundException catch
                // shape GuessEndpoints already uses for the identical
                // failure mode — see that file's own comment for why the
                // shared base type means no per-game `using` is needed here.
                logger.LogError(ex, "Suggestion submission failed: cell not found.");
                return Results.NotFound();
            }

            // No further validation of roundId/cellId's relationship to
            // each other beyond GetCellCategoryTypesAsync's own resolution,
            // and no check that a guess on this cell was actually incorrect
            // or timed out — REQ-215's trigger-condition gating is a
            // frontend concern (S-089's frontend half); this endpoint's job
            // is exactly: authenticated, non-guest, valid payload, persist
            // pending. No retroactive rescoring either (REQ-215's
            // 2026-08-01 decision) — this write never touches the Guess
            // table at all.
            var suggestionId = Guid.NewGuid();
            var suggestion = new PlayerSuggestion
            {
                Id = suggestionId,
                PlayerName = request.PlayerName,
                AssertedNationality = request.Nationality,
                SubmittingUserId = user.Id,
                CellId = cellId,
                RoundId = roundId,
                RowCategoryType = categoryTypes.RowCategoryType,
                ColCategoryType = categoryTypes.ColCategoryType,
                Status = PlayerSuggestionStatus.Pending,
                CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
                AssertedClubs = clubs
                    .Select(clubName => new PlayerSuggestionClub { Id = Guid.NewGuid(), PlayerSuggestionId = suggestionId, ClubName = clubName })
                    .ToList(),
            };

            // Deliberately no writes to PlayerAttribute/PlayerOverride/
            // PlayerNameIndex anywhere in this file — REQ-215 + ADR-0052
            // both require submission to persist only a Pending
            // PlayerSuggestion row. Deliberately no
            // IUserRepository.UpdateLastActiveAtAsync call either: REQ-718/
            // ADR-0038 scopes LastActiveAt updates to exactly four events
            // (login, guest provisioning, claim, a submitted guess) and a
            // suggestion submission isn't one of them.
            var created = await playerSuggestionRepository.AddAsync(suggestion, cancellationToken);

            return Results.Created(
                $"/rounds/{roundId}/cells/{cellId}/suggestions/{created.Id}",
                new SubmitSuggestionResponse(
                    created.Id,
                    created.PlayerName,
                    created.AssertedClubs.Select(c => c.ClubName).ToList(),
                    created.AssertedNationality,
                    created.Status.ToString(),
                    created.CreatedAt));
        }).RequireAuthorization();
    }
}

// PlayerName: the player name as typed in the triggering guess — already
// known to the frontend from that guess's own response, not re-entered by
// the player in the suggestion form itself (REQ-215).
public record SubmitSuggestionRequest(string PlayerName, IReadOnlyList<string> Clubs, string Nationality);

public record SubmitSuggestionResponse(
    Guid Id,
    string PlayerName,
    IReadOnlyList<string> AssertedClubs,
    string AssertedNationality,
    string Status,
    DateTime CreatedAt);

// Pure log-category marker for ILogger<T> — same pattern as
// GuessEndpoints.cs's GuessEndpointsLogCategory.
internal sealed class SuggestionEndpointsLogCategory;
