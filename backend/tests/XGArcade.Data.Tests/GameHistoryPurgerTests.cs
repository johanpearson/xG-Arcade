using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// S-152 (Epic 16, docs/backlog.md): GameHistoryPurger wipes every historical
// game-play row (Round, Guess, PlayerSuggestion+PlayerSuggestionClub,
// GridInstance+GridCell, PathInstance+PathPuzzle, PathTargetCycle,
// PathCycleTargetUsage) so the platform starts with zero game history once
// Epics 10-15 are settled — see that class's own doc comment for the full
// scope reasoning. No REQ/ADR exists for this (self-contained operational
// tool, same as PathTargetCycleResetter/StaleClubAttributeCleaner/
// PairLookupFailureCleaner/DuplicateCareerStintCleaner), so these test names
// have no REQ prefix — same precedent as UserDisplayNameBackfillerTests/
// PlayerNormalizedFullNameBackfillerTests/PlayerNameIndexWordBackfillerTests/
// PlayerAliasNormalizedAliasBackfillerTests in this same folder.
public class GameHistoryPurgerTests
{
    private XGArcadeDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // ---- Seed helpers for every purged table -------------------------------

    private async Task<Round> SeedRoundAsync()
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = "xg-grid",
            GameInstanceId = Guid.NewGuid(),
            SequenceNumber = 1,
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
        };
        _dbContext.Rounds.Add(round);
        await _dbContext.SaveChangesAsync();
        return round;
    }

    private async Task<Guess> SeedGuessAsync(Guid roundId)
    {
        var guess = new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = roundId,
            CellId = Guid.NewGuid(),
            SubmittedName = "Test Player",
            IsCorrect = true,
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.Guesses.Add(guess);
        await _dbContext.SaveChangesAsync();
        return guess;
    }

    private async Task<PlayerSuggestion> SeedPlayerSuggestionAsync(Guid roundId)
    {
        var suggestion = new PlayerSuggestion
        {
            Id = Guid.NewGuid(),
            PlayerName = "Test Player",
            AssertedNationality = "France",
            SubmittingUserId = Guid.NewGuid(),
            CellId = Guid.NewGuid(),
            RoundId = roundId,
            RowCategoryType = "nationality",
            ColCategoryType = "club",
            CreatedAt = DateTime.UtcNow,
        };
        _dbContext.PlayerSuggestions.Add(suggestion);
        await _dbContext.SaveChangesAsync();

        _dbContext.PlayerSuggestionClubs.Add(new PlayerSuggestionClub
        {
            Id = Guid.NewGuid(),
            PlayerSuggestionId = suggestion.Id,
            ClubName = "Arsenal",
        });
        await _dbContext.SaveChangesAsync();

        return suggestion;
    }

    private async Task<GridInstance> SeedGridInstanceAsync()
    {
        var gridInstance = new GridInstance { Id = Guid.NewGuid(), TemplateId = Guid.NewGuid() };
        _dbContext.GridInstances.Add(gridInstance);
        await _dbContext.SaveChangesAsync();

        _dbContext.GridCells.Add(new GridCell
        {
            Id = Guid.NewGuid(),
            GridInstanceId = gridInstance.Id,
            Row = 0,
            Col = 0,
            RowCategoryType = "country",
            RowCategoryValue = "France",
            ColCategoryType = "club",
            ColCategoryValue = "Arsenal",
        });
        await _dbContext.SaveChangesAsync();

        return gridInstance;
    }

    private async Task<Player> SeedPlayerAsync(string fullName = "Test Player")
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName, WikidataQid = $"Q{Guid.NewGuid():N}" };
        _dbContext.Players.Add(player);
        await _dbContext.SaveChangesAsync();
        return player;
    }

    private async Task<PathInstance> SeedPathInstanceAsync(Guid targetPlayerId)
    {
        var pathInstance = new PathInstance { Id = Guid.NewGuid(), TemplateId = Guid.NewGuid() };
        _dbContext.PathInstances.Add(pathInstance);
        await _dbContext.SaveChangesAsync();

        _dbContext.PathPuzzles.Add(new PathPuzzle
        {
            Id = Guid.NewGuid(),
            PathInstanceId = pathInstance.Id,
            TargetPlayerId = targetPlayerId,
        });
        await _dbContext.SaveChangesAsync();

        return pathInstance;
    }

    private static readonly Guid SingletonCycleId = new("00000000-0000-0000-0000-000000000001");

    private async Task SeedPathTargetCycleAsync()
    {
        _dbContext.PathTargetCycles.Add(new PathTargetCycle
        {
            Id = SingletonCycleId,
            CycleNumber = 3,
            ObservedPoolSize = 120,
            UsedInCycleCount = 40,
            LastCycleCompletedAt = null,
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedPathCycleTargetUsageAsync(Guid playerId)
    {
        _dbContext.PathCycleTargetUsages.Add(new PathCycleTargetUsage
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            CycleNumber = 3,
        });
        await _dbContext.SaveChangesAsync();
    }

    // ---- Seed helpers for every explicitly out-of-scope table --------------

    private async Task SeedUserAsync()
    {
        _dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "player@example.com",
            DisplayName = "Player One",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            LastActiveAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task<League> SeedLeagueAsync()
    {
        var league = new League { Id = Guid.NewGuid(), Name = "Global", Type = "global" };
        _dbContext.Leagues.Add(league);
        await _dbContext.SaveChangesAsync();
        return league;
    }

    private async Task SeedLeagueMembershipAsync(Guid leagueId, Guid userId)
    {
        _dbContext.LeagueMemberships.Add(new LeagueMembership { LeagueId = leagueId, UserId = userId });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedPlayerDataAsync(Guid playerId)
    {
        _dbContext.PlayerData.Add(new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Field = "club",
            Value = "Arsenal",
            Source = "wikidata",
            Confidence = "unverified",
            SyncedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedPlayerAttributeAsync(Guid playerId)
    {
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = playerId, AttributeType = "club", AttributeValue = "Arsenal" });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedPlayerOverrideAsync(Guid playerId)
    {
        _dbContext.PlayerOverrides.Add(new PlayerOverride
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            Field = "nationality",
            Value = "France",
            Reason = "manual correction",
            LockedByAdminId = Guid.NewGuid(),
            LockedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedPlayerAliasAsync(Guid playerId)
    {
        _dbContext.PlayerAliases.Add(new PlayerAlias { PlayerId = playerId, Alias = "Nickname", NormalizedAlias = "nickname" });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedPlayerCareerStintAsync(Guid playerId)
    {
        _dbContext.PlayerCareerStints.Add(new PlayerCareerStint
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            ClubName = "Arsenal",
            StartYear = 2020,
            EndYear = null,
            SequenceOrder = 0,
            AppearanceCount = 50,
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedPlayerNameIndexAsync(Guid playerId)
    {
        _dbContext.PlayerNameIndexEntries.Add(new PlayerNameIndex
        {
            PlayerId = playerId,
            PrimaryName = "Test Player",
            NormalizedName = "test player",
        });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedClubDefinitionAsync()
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedCountryDefinitionAsync()
    {
        _dbContext.CountryDefinitions.Add(new CountryDefinition { Id = Guid.NewGuid(), Name = "France", WikidataQid = "Q142" });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedTrophyDefinitionAsync()
    {
        _dbContext.TrophyDefinitions.Add(new TrophyDefinition { Id = Guid.NewGuid(), Name = "Ballon d'Or", IsTeamTrophy = false });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedGridTemplateAsync()
    {
        _dbContext.GridTemplates.Add(new GridTemplate { Id = Guid.NewGuid(), Size = 3, AllowedCategoryTypes = ["country", "club"] });
        await _dbContext.SaveChangesAsync();
    }

    private async Task SeedPathTemplateAsync()
    {
        _dbContext.PathTemplates.Add(new PathTemplate { Id = Guid.NewGuid(), PuzzleCount = 3 });
        await _dbContext.SaveChangesAsync();
    }

    // ---- In-scope tables are fully wiped ------------------------------------

    [Test]
    public async Task PurgeAsync_RoundGuessAndPlayerSuggestion_AllRemoved()
    {
        var round = await SeedRoundAsync();
        await SeedGuessAsync(round.Id);
        await SeedPlayerSuggestionAsync(round.Id);

        var result = await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(result.RoundCount, Is.EqualTo(1));
        Assert.That(result.GuessCount, Is.EqualTo(1));
        Assert.That(result.PlayerSuggestionCount, Is.EqualTo(1));
        Assert.That(await _dbContext.Rounds.CountAsync(), Is.EqualTo(0));
        Assert.That(await _dbContext.Guesses.CountAsync(), Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerSuggestions.CountAsync(), Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerSuggestionClubs.CountAsync(), Is.EqualTo(0),
            "PlayerSuggestionClub is PlayerSuggestion's own owned-collection row and must be wiped alongside it");
    }

    [Test]
    public async Task PurgeAsync_GridInstanceAndGridCell_AllRemoved()
    {
        await SeedGridInstanceAsync();

        var result = await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(result.GridInstanceCount, Is.EqualTo(1));
        Assert.That(result.GridCellCount, Is.EqualTo(1));
        Assert.That(await _dbContext.GridInstances.CountAsync(), Is.EqualTo(0));
        Assert.That(await _dbContext.GridCells.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task PurgeAsync_PathInstanceAndPathPuzzle_AllRemoved()
    {
        var player = await SeedPlayerAsync();
        await SeedPathInstanceAsync(player.Id);

        var result = await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(result.PathInstanceCount, Is.EqualTo(1));
        Assert.That(result.PathPuzzleCount, Is.EqualTo(1));
        Assert.That(await _dbContext.PathInstances.CountAsync(), Is.EqualTo(0));
        Assert.That(await _dbContext.PathPuzzles.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task PurgeAsync_PathTargetCycleAndUsage_AllRemoved()
    {
        var player = await SeedPlayerAsync();
        await SeedPathTargetCycleAsync();
        await SeedPathCycleTargetUsageAsync(player.Id);

        var result = await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(result.PathTargetCycleRowExisted, Is.True);
        Assert.That(result.PathCycleTargetUsageCount, Is.EqualTo(1));
        Assert.That(await _dbContext.PathTargetCycles.CountAsync(), Is.EqualTo(0));
        Assert.That(await _dbContext.PathCycleTargetUsages.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task PurgeAsync_NoPathTargetCycleRow_ReportsFalse_DoesNotThrow()
    {
        var result = await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(result.PathTargetCycleRowExisted, Is.False,
            "no PathTargetCycle row is xG Path's own 'never generated a round yet' state — this must succeed, not error");
        Assert.That(result.PathCycleTargetUsageCount, Is.EqualTo(0));
    }

    [Test]
    public async Task PurgeAsync_EveryPurgedTableEmpty_IsANoOp_DoesNotThrow()
    {
        var result = await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(result.RoundCount, Is.EqualTo(0));
        Assert.That(result.GuessCount, Is.EqualTo(0));
        Assert.That(result.PlayerSuggestionCount, Is.EqualTo(0));
        Assert.That(result.GridInstanceCount, Is.EqualTo(0));
        Assert.That(result.GridCellCount, Is.EqualTo(0));
        Assert.That(result.PathInstanceCount, Is.EqualTo(0));
        Assert.That(result.PathPuzzleCount, Is.EqualTo(0));
        Assert.That(result.PathCycleTargetUsageCount, Is.EqualTo(0));
        Assert.That(result.PathTargetCycleRowExisted, Is.False);
    }

    [Test]
    public async Task PurgeAsync_IsSafeToRunAgain_WhenNothingIsLeftToPurge()
    {
        var round = await SeedRoundAsync();
        await SeedGuessAsync(round.Id);
        await GameHistoryPurger.PurgeAsync(_dbContext);

        var secondRunResult = await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(secondRunResult.RoundCount, Is.EqualTo(0));
        Assert.That(secondRunResult.GuessCount, Is.EqualTo(0));
    }

    // ---- Every explicitly out-of-scope table survives untouched ------------
    // Row counts before/after, not just "no exception thrown" — the story's
    // own explicit acceptance bar.

    [Test]
    public async Task PurgeAsync_UserLeagueAndLeagueMembership_RowCountsUnchanged()
    {
        await SeedUserAsync();
        var user = await _dbContext.Users.SingleAsync();
        var league = await SeedLeagueAsync();
        await SeedLeagueMembershipAsync(league.Id, user.Id);
        // Also seed something to actually purge, so this isn't a vacuous no-op run.
        var round = await SeedRoundAsync();
        await SeedGuessAsync(round.Id);

        await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(await _dbContext.Users.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.Leagues.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.LeagueMemberships.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task PurgeAsync_EveryPlayerAndReferenceTable_RowCountsUnchanged()
    {
        var player = await SeedPlayerAsync();
        await SeedPlayerDataAsync(player.Id);
        await SeedPlayerAttributeAsync(player.Id);
        await SeedPlayerOverrideAsync(player.Id);
        await SeedPlayerAliasAsync(player.Id);
        await SeedPlayerCareerStintAsync(player.Id);
        await SeedPlayerNameIndexAsync(player.Id);
        await SeedClubDefinitionAsync();
        await SeedCountryDefinitionAsync();
        await SeedTrophyDefinitionAsync();
        await SeedGridTemplateAsync();
        await SeedPathTemplateAsync();
        // Also seed something to actually purge, so this isn't a vacuous no-op run.
        var round = await SeedRoundAsync();
        await SeedGuessAsync(round.Id);

        await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.PlayerData.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.PlayerAttributes.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.PlayerOverrides.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.PlayerAliases.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.PlayerCareerStints.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.PlayerNameIndexEntries.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.CountryDefinitions.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.TrophyDefinitions.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.GridTemplates.CountAsync(), Is.EqualTo(1));
        Assert.That(await _dbContext.PathTemplates.CountAsync(), Is.EqualTo(1));
    }

    // ---- PathPuzzle's Player FK survives even though PathPuzzle itself is --
    // wiped: purging game history must never cascade into Player, despite
    // PathPuzzle.TargetPlayerId's own DeleteBehavior.Cascade FK to Player
    // (XGArcadeDbContext.OnModelCreating) — that cascade only fires in the
    // OTHER direction (deleting a Player would cascade into PathPuzzle),
    // never this one (deleting a PathPuzzle must never reach back to delete
    // the Player it targeted).

    [Test]
    public async Task PurgeAsync_RemovesPathPuzzle_LeavesItsTargetPlayerUntouched()
    {
        var player = await SeedPlayerAsync();
        await SeedPathInstanceAsync(player.Id);

        await GameHistoryPurger.PurgeAsync(_dbContext);

        Assert.That(await _dbContext.PathPuzzles.CountAsync(), Is.EqualTo(0));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(1),
            "purging game history must never reach back into the Player table via PathPuzzle's FK");
    }
}
