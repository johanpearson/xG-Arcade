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
                .Select(m => new PlayerAutocompleteSuggestion(m.PlayerId, m.PrimaryName, m.BirthYear))
                .ToList();

            return Results.Ok(suggestions);
        }).RequireAuthorization();

        // S-151/REQ-207: cold-start warm-up call, fired best-effort from
        // GridScreen/PathScreen on mount (never on every app load — that's
        // /health's job). Unlike /health (EndpointMapping.cs), which is a
        // static Results.Ok with no DB access, this deliberately runs the
        // exact same IPlayerNameIndexRepository.SearchByPrefixAsync path the
        // real /players/autocomplete route uses, so the Postgres connection
        // open and EF Core query-plan compilation happen here instead of on
        // the player's first real keystroke. The 1-character query is
        // server-side only and must never be reachable via the client's
        // MinQueryLength = 2 contract on the real route above. Result is
        // discarded; 204 tells the caller only that the round-trip happened.
        app.MapGet("/players/autocomplete/warmup", async (
            IPlayerNameIndexRepository playerNameIndexRepository,
            CancellationToken cancellationToken) =>
        {
            var normalizedQuery = PlayerNameNormalizer.Normalize("a");
            await playerNameIndexRepository.SearchByPrefixAsync(normalizedQuery, limit: 1, cancellationToken);

            return Results.NoContent();
        }).RequireAuthorization();
    }
}

// Nationality removed (bug-bundle fix, 2026-07-27): PlayerNameIndex.
// PrimaryNationality must never reach this response. When the current grid
// cell's category is nationality-based (Country x Club), showing which
// suggested names carry the target nationality tells the player who's
// eligible before they even guess — exactly the "autocomplete implies
// correctness" leak ADR-0007/REQ-207 exists to prevent (architecture-
// document.md boundary rule 5, this file's own doc comment above).
// BirthYear stays: no xG Grid category is birth-year-based (categories are
// Country/Club/Trophy only — CategoryPairingRules), so it can't leak a
// category match the same way.
public record PlayerAutocompleteSuggestion(Guid PlayerId, string Name, int? BirthYear);
