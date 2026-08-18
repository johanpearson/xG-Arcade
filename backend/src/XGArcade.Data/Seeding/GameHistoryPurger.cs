using Microsoft.EntityFrameworkCore;

namespace XGArcade.Data.Seeding;

// S-152 (Epic 16, docs/backlog.md): manually-triggered, one-off operational
// tool (Program.cs/CliVerbDispatcher's `purge-game-history` CLI verb) that
// wipes every historical game-play row so the platform starts with zero
// game history once Epics 10-15 are settled. See the backlog story's own
// text for the full "why now, why this scope" reasoning; no REQ/ADR exists
// for this — it is a self-contained operational tool, same precedent as
// PathTargetCycleResetter/StaleClubAttributeCleaner/PairLookupFailureCleaner/
// DuplicateCareerStintCleaner in this same folder.
//
// Scope, confirmed against XGArcadeDbContext.cs's actual OnModelCreating
// (not assumed):
//   - Round -> Guess (Guess.RoundId, DeleteBehavior.Cascade) and
//     Round -> PlayerSuggestion (PlayerSuggestion.RoundId, same cascade).
//     PlayerSuggestion -> PlayerSuggestionClub is its own owned-collection
//     cascade (PlayerSuggestionClub.PlayerSuggestionId).
//   - GridInstance -> GridCell (GridCell.GridInstanceId, cascade).
//   - PathInstance -> PathPuzzle (PathPuzzle.PathInstanceId, cascade).
//   - PathTargetCycle (no FK at all — a singleton row keyed only by its own
//     Id) and PathCycleTargetUsage (its only FK is to Player, not Round/
//     PathInstance) are NOT reachable via any of the above cascades, so
//     they're deleted explicitly, same as PathTargetCycleResetter already
//     does for the `reset-path-target-cycle` verb.
//
// Deliberately does NOT rely on EF Core's configured cascade-delete
// behavior at runtime — that only fires automatically for entities already
// TRACKED in the DbContext when SaveChangesAsync is called (client cascade)
// or via real database-level ON DELETE CASCADE (relational providers only,
// e.g. Npgsql in production). The InMemory test provider this class's own
// test suite runs against has no database-level cascade, so every table is
// loaded and removed explicitly here — correct and provable under both
// providers, not just production.
//
// One SaveChangesAsync call for everything (not one per table) so the
// whole purge lands in a single database transaction under a real
// relational provider, matching this story's own "one transaction"
// acceptance criterion — a bare SaveChangesAsync already gets that for
// free, no explicit BeginTransactionAsync needed (and the InMemory
// provider used by this class's own tests doesn't support real
// transactions at all, so an explicit one would break them).
//
// Explicitly OUT of scope, never touched here: User, League,
// LeagueMembership, every Player/reference table (Player, PlayerData,
// PlayerAttribute, PlayerOverride, PlayerAlias, PlayerCareerStint,
// PlayerNameIndex, PlayerNameIndexWord, ClubDefinition, CountryDefinition,
// TrophyDefinition, GridTemplate, PathTemplate) — see the backlog story's
// own "explicitly out of scope" list, restated in GameHistoryPurgerTests.
public static class GameHistoryPurger
{
    public record PurgeResult(
        int RoundCount,
        int GuessCount,
        int PlayerSuggestionCount,
        int GridInstanceCount,
        int GridCellCount,
        int PathInstanceCount,
        int PathPuzzleCount,
        int PathCycleTargetUsageCount,
        bool PathTargetCycleRowExisted);

    public static async Task<PurgeResult> PurgeAsync(
        XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        // Children before parents (never required for the InMemory provider,
        // which has no FK enforcement at all, but also correct against a
        // real, FK-constrained Npgsql database regardless of whether ON
        // DELETE CASCADE would have handled it) — see this class's own doc
        // comment above for why cascade isn't relied on here.
        var gridCells = await dbContext.GridCells.ToListAsync(cancellationToken);
        dbContext.GridCells.RemoveRange(gridCells);

        var gridInstances = await dbContext.GridInstances.ToListAsync(cancellationToken);
        dbContext.GridInstances.RemoveRange(gridInstances);

        var pathPuzzles = await dbContext.PathPuzzles.ToListAsync(cancellationToken);
        dbContext.PathPuzzles.RemoveRange(pathPuzzles);

        var pathInstances = await dbContext.PathInstances.ToListAsync(cancellationToken);
        dbContext.PathInstances.RemoveRange(pathInstances);

        var playerSuggestionClubs = await dbContext.PlayerSuggestionClubs.ToListAsync(cancellationToken);
        dbContext.PlayerSuggestionClubs.RemoveRange(playerSuggestionClubs);

        var playerSuggestions = await dbContext.PlayerSuggestions.ToListAsync(cancellationToken);
        dbContext.PlayerSuggestions.RemoveRange(playerSuggestions);

        var guesses = await dbContext.Guesses.ToListAsync(cancellationToken);
        dbContext.Guesses.RemoveRange(guesses);

        var rounds = await dbContext.Rounds.ToListAsync(cancellationToken);
        dbContext.Rounds.RemoveRange(rounds);

        // Same PathTargetCycle/PathCycleTargetUsage wipe PathTargetCycleResetter
        // performs for `reset-path-target-cycle` — inlined here (rather than
        // calling that class's own ResetAsync) so this whole purge lands in
        // ONE SaveChangesAsync call/transaction; ResetAsync calls
        // SaveChangesAsync itself, which would split this purge into two
        // separate transactions instead of the single one this story's
        // acceptance criteria calls for.
        var pathCycleTargetUsages = await dbContext.PathCycleTargetUsages.ToListAsync(cancellationToken);
        dbContext.PathCycleTargetUsages.RemoveRange(pathCycleTargetUsages);

        var pathTargetCycle = await dbContext.PathTargetCycles.FirstOrDefaultAsync(cancellationToken);
        if (pathTargetCycle is not null)
            dbContext.PathTargetCycles.Remove(pathTargetCycle);

        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteDeleteAsync — the InMemory test provider can't translate
        // it, and this story's own acceptance criteria require an
        // InMemory-provider test with row-count assertions, unlike
        // purge-player-pool's ExecuteDeleteAsync (which is why that verb has
        // zero tests — see CliVerbDispatcher.HandlePurgePlayerPoolAsync's own
        // doc comment).
        await dbContext.SaveChangesAsync(cancellationToken);

        return new PurgeResult(
            RoundCount: rounds.Count,
            GuessCount: guesses.Count,
            PlayerSuggestionCount: playerSuggestions.Count,
            GridInstanceCount: gridInstances.Count,
            GridCellCount: gridCells.Count,
            PathInstanceCount: pathInstances.Count,
            PathPuzzleCount: pathPuzzles.Count,
            PathCycleTargetUsageCount: pathCycleTargetUsages.Count,
            PathTargetCycleRowExisted: pathTargetCycle is not null);
    }
}
