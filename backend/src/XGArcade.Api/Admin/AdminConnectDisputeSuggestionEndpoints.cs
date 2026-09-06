using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Admin;

// REQ-1414/ADR-0053: a durable, admin-readable record of every Approved xG
// Connect dispute (REQ-1412/1413) — read-only, no approve/reject/act-on
// workflow (REQ-1414's own text leaves the actual future data-correction
// mechanism to a later decision, deliberately out of scope here). Its own
// new file/registration, never folded into MapAdminSuggestionEndpoints'
// PlayerSuggestion queue above — ADR-0053's own "never becomes a row type
// in that queue" rule, applied here for the same reason: this is a
// club-overlap fact discovered via a match, not a cell-guess candidate.
public static class AdminConnectDisputeSuggestionEndpoints
{
    // REQ-1414: "every recorded suggestion is visible to an admin somewhere
    // in the product" — no filtering/paging/approve/reject, by design.
    public static void MapAdminConnectDisputeSuggestionEndpoints(this WebApplication app)
    {
        app.MapGet("/admin/connect-dispute-suggestions", async (
            IConnectMatchRepository connectMatchRepository,
            IPlayerRepository playerRepository,
            CancellationToken cancellationToken) =>
        {
            var suggestions = await connectMatchRepository.GetAllDataCorrectionSuggestionsAsync(cancellationToken);

            var playerIds = suggestions
                .SelectMany(s => new[] { s.CandidatePlayerId, s.PrecedingPlayerId })
                .Distinct()
                .ToList();
            var players = await playerRepository.GetPlayersByIdsAsync(playerIds, cancellationToken);

            var responses = suggestions
                .Select(s => new ConnectDisputeDataCorrectionSuggestionResponse(
                    s.Id,
                    s.ConnectMatchId,
                    s.ConnectChainStepId,
                    s.ConnectChainStepDisputeId,
                    s.CandidatePlayerId,
                    ResolvePlayerName(players, s.CandidatePlayerId),
                    s.PrecedingPlayerId,
                    ResolvePlayerName(players, s.PrecedingPlayerId),
                    s.ClaimedClubName,
                    s.CreatedAt))
                .OrderByDescending(r => r.CreatedAt)
                .ToList();

            return Results.Ok(responses);
        }).RequireAuthorization("Admin");
    }

    // Same "should never actually happen" defensive fallback as
    // ConnectMatchQueryService.ResolvePlayerName/AdminSuggestionEndpoints'
    // own equivalents — every Player id stored here came from a real
    // ConnectChainStep/ConnectTargetPick row at approval time.
    private static string ResolvePlayerName(IReadOnlyDictionary<Guid, Player> players, Guid playerId) =>
        players.TryGetValue(playerId, out var player) ? player.FullName : "Unknown player";
}

public record ConnectDisputeDataCorrectionSuggestionResponse(
    Guid Id,
    Guid ConnectMatchId,
    Guid ConnectChainStepId,
    Guid ConnectChainStepDisputeId,
    Guid CandidatePlayerId,
    string CandidatePlayerName,
    Guid PrecedingPlayerId,
    string PrecedingPlayerName,
    string ClaimedClubName,
    DateTime CreatedAt);
