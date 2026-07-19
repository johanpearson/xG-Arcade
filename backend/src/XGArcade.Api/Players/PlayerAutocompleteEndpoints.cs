using XGArcade.Data;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Players;

// COMP-10 (Data.PlayerNameIndex): REQ-207's autocomplete suggestion list.
// Queries IPlayerNameIndexRepository ONLY — never IPlayerStoreRepository
// (COMP-06), for any reason. See ADR-0007 and architecture-document.md
// boundary rule 5: a name appearing here implies nothing about whether it's
// a valid answer for the cell currently being guessed.
public static class PlayerAutocompleteEndpoints
{
    // Below this, "no query yet" — don't run a near-full-table-scan prefix
    // search on every single keystroke.
    private const int MinQueryLength = 2;

    private const int DefaultLimit = 10;

    // Clamped, not rejected — unlike LeaderboardEndpoints' pageSize (a 400 on
    // an out-of-range value), a caller asking for more suggestions than
    // sensible just gets fewer than requested, since this is a UX nicety,
    // not a paged data contract a client needs to reason about precisely.
    private const int MaxLimit = 25;

    public static void MapPlayerAutocompleteEndpoints(this WebApplication app)
    {
        app.MapGet("/players/autocomplete", async (
            string? query,
            int? limit,
            IPlayerNameIndexRepository playerNameIndexRepository,
            CancellationToken cancellationToken) =>
        {
            var trimmedQuery = query?.Trim() ?? string.Empty;
            if (trimmedQuery.Length < MinQueryLength)
                return Results.Ok(Array.Empty<PlayerAutocompleteSuggestion>());

            var effectiveLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

            var normalizedQuery = PlayerNameNormalizer.Normalize(trimmedQuery);
            if (normalizedQuery.Length < MinQueryLength)
                return Results.Ok(Array.Empty<PlayerAutocompleteSuggestion>());

            var matches = await playerNameIndexRepository.SearchByPrefixAsync(normalizedQuery, effectiveLimit, cancellationToken);

            var suggestions = matches
                .Select(m => new PlayerAutocompleteSuggestion(m.PlayerId, m.PrimaryName, m.BirthYear, m.PrimaryNationality))
                .ToList();

            return Results.Ok(suggestions);
        }).RequireAuthorization();
    }
}

public record PlayerAutocompleteSuggestion(Guid PlayerId, string Name, int? BirthYear, string? Nationality);
