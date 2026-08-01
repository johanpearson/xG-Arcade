using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Suggestions;

// REQ-215/ADR-0052 (S-089): submission-only half of the player-suggestion
// pipeline — REQ-509's admin list/review/commit/reject endpoints are S-090,
// a separate future story, not built here. Mirrors XGArcade.Api.Guesses.
// GuessEndpoints's route shape (same {roundId}/{cellId} pair) and minimal-
// API conventions (ClaimsPrincipal + IUserRepository.
// GetByAuthProviderUserIdAsync to resolve the caller, Results.Problem for
// every rejection).
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
            IGridInstanceRepository gridInstanceRepository,
            IPlayerSuggestionRepository playerSuggestionRepository,
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
            // direct request.
            if (user.IsGuest)
            {
                return Results.Problem(
                    title: "Guest accounts cannot submit suggestions",
                    detail: "Register for a full account to suggest a correction.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // Authoritative row/col category types, resolved server-side
            // rather than trusted from the request — same "never trust the
            // client for data that becomes a stored record" discipline
            // GuessEndpoints' ChosenPlayerId re-verification already
            // follows, applied here to context fields instead of a
            // correctness check.
            var cell = await gridInstanceRepository.GetCellByIdAsync(cellId, cancellationToken);
            if (cell is null)
                return Results.NotFound();

            // No further validation of roundId/cellId's relationship to
            // each other, and no check that a guess on this cell was
            // actually incorrect or timed out — REQ-215's trigger-condition
            // gating is a frontend concern (S-089's frontend half); this
            // endpoint's job is exactly: authenticated, non-guest, valid
            // payload, persist pending. No retroactive rescoring either
            // (REQ-215's 2026-08-01 decision) — this write never touches
            // the Guess table at all.
            var suggestionId = Guid.NewGuid();
            var suggestion = new PlayerSuggestion
            {
                Id = suggestionId,
                PlayerName = request.PlayerName,
                AssertedNationality = request.Nationality,
                SubmittingUserId = user.Id,
                CellId = cellId,
                RoundId = roundId,
                RowCategoryType = cell.RowCategoryType,
                ColCategoryType = cell.ColCategoryType,
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
