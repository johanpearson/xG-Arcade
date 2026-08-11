using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid.Tests;

// REQ-207/208/209 (name resolution/disambiguation) and REQ-216
// (wrong-guess identity resolution) — docs/requirements-document.md §4.1.
// S-119 (pure refactor, no behavior change): split out of
// GridGameModuleTests.cs alongside GridNameMatcher itself. Every test here
// exercises IGridNameMatcher directly (FindMatchAsync/
// ResolveWrongGuessPlayerAsync) against a freshly-constructed
// GridNameMatcher, rather than going through GridGameModule.ScoreSubmissionAsync
// — the "fakes/mocks only construct the one class under test" convention
// S-106/S-107 established for the IPlayerStoreRepository split (ADR-0067).
// Follows this repo's no-mocking-framework pattern (docs/coding-guidelines.md
// "don't over-mock"): real, InMemory-backed repositories, same setup as
// GridGameModuleTests/GridGenerationServiceTests.
public class GridNameMatcherTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAliasRepository _playerAliasRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerOverrideRepository _playerOverrideRepository = null!;
    private IPlayerNameIndexRepository _playerNameIndexRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAliasRepository = new PlayerAliasRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
        _playerOverrideRepository = new PlayerOverrideRepository(_dbContext);
        _playerNameIndexRepository = new PlayerNameIndexRepository(_dbContext);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private GridNameMatcher BuildMatcher(
        IPlayerAliasRepository? playerAliasRepository = null,
        IPlayerAttributeRepository? playerAttributeRepository = null,
        IWikidataClient? wikidataClient = null) =>
        new(_playerRepository, playerAliasRepository ?? _playerAliasRepository, playerAttributeRepository ?? _playerAttributeRepository,
            _playerOverrideRepository, _playerNameIndexRepository, NullLogger<GridNameMatcher>.Instance,
            wikidataClient ?? new FakeWikidataClient());

    // FindMatchAsync takes a GridCell directly — unlike GridGameModuleTests'
    // SeedGridInstanceAsync, no GridInstance/repository round-trip is
    // needed, since the matcher never looks the cell up itself.
    private static GridCell BuildCell(
        string rowCategoryValue, string colCategoryValue,
        string rowCategoryType = CategoryPairingRules.Country, string colCategoryType = CategoryPairingRules.Club) =>
        new()
        {
            Id = Guid.NewGuid(),
            GridInstanceId = Guid.NewGuid(),
            Row = 0,
            Col = 0,
            RowCategoryType = rowCategoryType,
            RowCategoryValue = rowCategoryValue,
            ColCategoryType = colCategoryType,
            ColCategoryValue = colCategoryValue,
        };

    // Normalizes submittedName the same way GridGameModule.ScoreSubmissionAsync
    // does before calling IGridNameMatcher.FindMatchAsync (REQ-208) — kept as
    // a small local helper so every test below can call FindMatchAsync with a
    // raw, unnormalized guess string, same as the original ScoreSubmissionAsync
    // call sites did.
    private static Task<ScoreResult> FindMatchAsync(
        IGridNameMatcher matcher, GridCell cell, string submittedName, Guid? chosenPlayerId = null) =>
        matcher.FindMatchAsync(cell, PlayerNameNormalizer.Normalize(submittedName), chosenPlayerId, Guid.NewGuid(), CancellationToken.None);

    private void SeedNameIndexEntry(string primaryName)
    {
        _dbContext.PlayerNameIndexEntries.Add(new PlayerNameIndex
        {
            PlayerId = Guid.NewGuid(),
            PrimaryName = primaryName,
            NormalizedName = PlayerNameNormalizer.Normalize(primaryName),
        });
        _dbContext.SaveChanges();
    }

    // Seeds a Player with cached PlayerAttribute rows for both nationality and
    // club — the "effective data" FindMatchAsync's guess-checking reads via
    // HasEffectiveAttributeAsync (REQ-203).
    private async Task<Player> SeedPlayerAsync(string fullName, string nationality, string club)
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName, WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = nationality });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = club });
        await _dbContext.SaveChangesAsync();
        return player;
    }

    // ---- REQ-108/S-031: Trophy category, category-fit half -----------------

    [Test]
    public async Task REQ108_FindMatchAsync_TrophyCountryCell_CandidateSatisfiesBothCategories_ReturnsCorrect()
    {
        var cell = BuildCell("France", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var player = new Player { Id = Guid.NewGuid(), FullName = "Zinedine Zidane", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "trophy", AttributeValue = "Ballon d'Or" });
        await _dbContext.SaveChangesAsync();
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Zinedine Zidane");

        Assert.That(result.IsCorrect, Is.True, "a PlayerAttribute record of type 'trophy' must satisfy a Trophy category cell");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ108_FindMatchAsync_TrophyClubCell_CandidateSatisfiesBothCategories_ReturnsCorrect()
    {
        var cell = BuildCell("Real Madrid", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Club, colCategoryType: CategoryPairingRules.Trophy);
        var player = new Player { Id = Guid.NewGuid(), FullName = "Luka Modric", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Real Madrid" });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "trophy", AttributeValue = "Ballon d'Or" });
        await _dbContext.SaveChangesAsync();
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Luka Modric");

        Assert.That(result.IsCorrect, Is.True, "a PlayerAttribute record of type 'trophy' must satisfy a Trophy category cell");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ108_FindMatchAsync_TrophyCell_PlayerLacksTrophyAttribute_ReturnsIncorrect()
    {
        // Right nationality, but no "trophy"/"Ballon d'Or" PlayerAttribute —
        // must satisfy BOTH categories, not just the non-Trophy one.
        var cell = BuildCell("France", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var player = new Player { Id = Guid.NewGuid(), FullName = "Some Frenchman", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });
        await _dbContext.SaveChangesAsync();
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Some Frenchman");

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ108_FindMatchAsync_TrophyOverride_WinsOverConflictingCachedPlayerAttribute()
    {
        // Mirrors REQ203_FindMatchAsync_OverridePresent_WinsOverConflictingCachedPlayerAttribute_EndToEnd
        // for the Trophy category — "a PlayerAttribute (or override) record
        // of type trophy" (REQ-108's acceptance text) explicitly includes
        // PlayerOverride, not just the raw cached attribute.
        var cell = BuildCell("France", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var player = new Player { Id = Guid.NewGuid(), FullName = "Zinedine Zidane", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });
        // Cached (unverified) data has no trophy attribute at all — an
        // admin override supplies it instead.
        await _dbContext.SaveChangesAsync();
        await _playerOverrideRepository.AddOverrideAsync(new PlayerOverride
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Field = "trophy",
            Value = "Ballon d'Or",
            Reason = "Manual correction",
            LockedByAdminId = Guid.NewGuid(),
            LockedAt = DateTime.UtcNow,
        });
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Zinedine Zidane");

        Assert.That(result.IsCorrect, Is.True, "the override must be effective even though nothing cached confirms the trophy category");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    // ---- REQ-203/210: guess correctness validation (FindMatchAsync) --------

    [Test]
    public async Task REQ203_FindMatchAsync_CandidateSatisfiesBothCategories_ReturnsCorrectWithPlayerAnswerId()
    {
        var cell = BuildCell("France", "Arsenal");
        var player = await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Thierry Henry");

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ203_FindMatchAsync_NoCandidateWithThatName_ReturnsIncorrectWithNullPlayerAnswerId()
    {
        var cell = BuildCell("France", "Arsenal");
        await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Someone Else");

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.PlayerAnswerId, Is.Null);
    }

    [Test]
    public async Task REQ203_FindMatchAsync_CandidateSatisfiesOnlyRowCategory_ReturnsIncorrect()
    {
        var cell = BuildCell("France", "Arsenal");
        // Right nationality, wrong club — must satisfy BOTH the row and
        // column categories, not just one.
        await SeedPlayerAsync("Thierry Henry", "France", "Barcelona");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Thierry Henry");

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ203_FindMatchAsync_OverridePresent_WinsOverConflictingCachedPlayerAttribute_EndToEnd()
    {
        // Cached (unverified) data says Barcelona, but an admin override for
        // the same field corrects it to Arsenal — the override must be what
        // guess-checking sees, exercised here through the full FindMatchAsync
        // path (unit-level coverage of the same rule lives in
        // XGArcade.Data.Tests/PlayerOverrideRepositoryTests).
        var cell = BuildCell("France", "Arsenal");
        var player = await SeedPlayerAsync("Thierry Henry", "France", "Barcelona");
        await _playerOverrideRepository.AddOverrideAsync(new PlayerOverride
        {
            Id = Guid.NewGuid(),
            PlayerId = player.Id,
            Field = "club",
            Value = "Arsenal",
            Reason = "Manual correction",
            LockedByAdminId = Guid.NewGuid(),
            LockedAt = DateTime.UtcNow,
        });
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Thierry Henry");

        Assert.That(result.IsCorrect, Is.True, "the override must be effective even though the cached PlayerAttribute alone would fail the club category");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    // ---- REQ-208: name normalization and matching --------------------------

    // Thin call-counting wrappers around the real, InMemory-backed
    // IPlayerAliasRepository/IPlayerAttributeRepository (never a hand-rolled
    // reimplementation of their behavior — every method just delegates)
    // used only to verify REQ-208's "exact match first, then alias, then
    // fuzzy — fuzzy only runs when the first two produced nothing" ordering:
    // the alias/fuzzy repository calls must never happen once an earlier
    // stage already resolved a fit.
    private sealed class CallCountingPlayerAliasRepository(IPlayerAliasRepository inner) : IPlayerAliasRepository
    {
        public int GetPlayersByNormalizedAliasAsyncCallCount { get; private set; }

        public Task<IReadOnlyList<PlayerAlias>> GetPlayerAliasesAsync(Guid playerId, CancellationToken cancellationToken = default) =>
            inner.GetPlayerAliasesAsync(playerId, cancellationToken);

        public Task AddPlayerAliasAsync(PlayerAlias alias, CancellationToken cancellationToken = default) =>
            inner.AddPlayerAliasAsync(alias, cancellationToken);

        public Task AddPlayerAliasesBatchAsync(IReadOnlyList<PlayerAlias> aliases, CancellationToken cancellationToken = default) =>
            inner.AddPlayerAliasesBatchAsync(aliases, cancellationToken);

        public Task<IReadOnlyList<Player>> GetPlayersByNormalizedAliasAsync(
            string normalizedAlias, CancellationToken cancellationToken = default)
        {
            GetPlayersByNormalizedAliasAsyncCallCount++;
            return inner.GetPlayersByNormalizedAliasAsync(normalizedAlias, cancellationToken);
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAlias>>> GetPlayerAliasesByPlayerIdsAsync(
            IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default) =>
            inner.GetPlayerAliasesByPlayerIdsAsync(playerIds, cancellationToken);
    }

    private sealed class CallCountingPlayerAttributeRepository(IPlayerAttributeRepository inner) : IPlayerAttributeRepository
    {
        public int GetPlayersWithEitherAttributeAsyncCallCount { get; private set; }

        public Task<IReadOnlyList<PlayerAttribute>> GetPlayerAttributesAsync(
            string attributeType, string attributeValue, CancellationToken cancellationToken = default) =>
            inner.GetPlayerAttributesAsync(attributeType, attributeValue, cancellationToken);

        public Task AddPlayerAttributeAsync(PlayerAttribute attribute, CancellationToken cancellationToken = default) =>
            inner.AddPlayerAttributeAsync(attribute, cancellationToken);

        public Task AddPlayerAttributesBatchAsync(IReadOnlyList<PlayerAttribute> attributes, CancellationToken cancellationToken = default) =>
            inner.AddPlayerAttributesBatchAsync(attributes, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAttribute>>> GetPlayerAttributesByPlayerIdsAsync(
            IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default) =>
            inner.GetPlayerAttributesByPlayerIdsAsync(playerIds, cancellationToken);

        public Task<int> CountPlayersWithBothAttributesAsync(
            string firstAttributeType, string firstAttributeValue,
            string secondAttributeType, string secondAttributeValue,
            CancellationToken cancellationToken = default) =>
            inner.CountPlayersWithBothAttributesAsync(firstAttributeType, firstAttributeValue, secondAttributeType, secondAttributeValue, cancellationToken);

        public Task<IReadOnlyList<Player>> GetPlayersWithEitherAttributeAsync(
            string firstAttributeType, string firstAttributeValue,
            string secondAttributeType, string secondAttributeValue,
            CancellationToken cancellationToken = default)
        {
            GetPlayersWithEitherAttributeAsyncCallCount++;
            return inner.GetPlayersWithEitherAttributeAsync(
                firstAttributeType, firstAttributeValue, secondAttributeType, secondAttributeValue, cancellationToken);
        }
    }

    [TestCase("Kaká", "Kaka", TestName = "REQ208_FindMatchAsync_DiacriticsIgnored")]
    [TestCase("thierry henry", "Thierry Henry", TestName = "REQ208_FindMatchAsync_CaseIgnored")]
    [TestCase("Thierry   Henry", "Thierry Henry", TestName = "REQ208_FindMatchAsync_ExtraWhitespaceIgnored")]
    [TestCase("  Thierry Henry  ", "Thierry Henry", TestName = "REQ208_FindMatchAsync_LeadingAndTrailingWhitespaceIgnored")]
    public async Task REQ208_FindMatchAsync_NormalizedVariant_StillMatches(string submittedName, string storedFullName)
    {
        var cell = BuildCell("France", "Arsenal");
        var player = await SeedPlayerAsync(storedFullName, "France", "Arsenal");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, submittedName);

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ208_FindMatchAsync_GenuinelyDifferentName_DoesNotMatch()
    {
        var cell = BuildCell("France", "Arsenal");
        await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Nicolas Anelka");

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_FindMatchAsync_AliasExactMatch_ScoresCorrect()
    {
        // Known aliases/stage names are matched via PlayerAlias, not just
        // the primary name field — a guess that only matches a recorded
        // alias, with no exact Player.FullName match, must still score
        // correct if that player fits the cell's categories.
        var cell = BuildCell("Brazil", "AC Milan");
        var player = await SeedPlayerAsync("Ricardo Izecson dos Santos Leite", "Brazil", "AC Milan");
        await _playerAliasRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Kaka");

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ208_FindMatchAsync_AliasMatch_RequiresCategoryFit_JustLikeAPrimaryNameMatch()
    {
        // An alias match is handled by exactly the same category-fit check
        // as a primary-name match (REQ-203/REQ-209) — an alias belonging to
        // a player who doesn't satisfy this cell's categories must not score
        // correct just because the name string matched.
        var cell = BuildCell("Brazil", "AC Milan");
        var player = await SeedPlayerAsync("Ricardo Izecson dos Santos Leite", "England", "Chelsea");
        await _playerAliasRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Kaka");

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_FindMatchAsync_ExactPrimaryNameMatch_AliasAndFuzzyStagesNeverConsulted()
    {
        // REQ-208's ordering: exact match first, then alias, then fuzzy —
        // the alias/fuzzy repository calls must never happen once the exact
        // primary-name stage already resolved a fit. A distinct player whose
        // name is one edit away from the guess (would fuzzy-match if the
        // fuzzy stage ran) is deliberately seeded to prove this isn't just
        // "no alias/fuzzy data exists to find."
        var cell = BuildCell("France", "Arsenal");
        var exactPlayer = await SeedPlayerAsync("Henry", "France", "Arsenal");
        await SeedPlayerAsync("Henri", "France", "Arsenal"); // distance 1 from "henry" — would fuzzy-match if reached
        var aliasSpy = new CallCountingPlayerAliasRepository(_playerAliasRepository);
        var attributeSpy = new CallCountingPlayerAttributeRepository(_playerAttributeRepository);
        var matcher = BuildMatcher(playerAliasRepository: aliasSpy, playerAttributeRepository: attributeSpy);

        var result = await FindMatchAsync(matcher, cell, "Henry");

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(exactPlayer.Id));
        Assert.That(aliasSpy.GetPlayersByNormalizedAliasAsyncCallCount, Is.EqualTo(0),
            "the alias stage must never be consulted once the exact primary-name stage already resolved a fit");
        Assert.That(attributeSpy.GetPlayersWithEitherAttributeAsyncCallCount, Is.EqualTo(0),
            "the fuzzy stage must never be consulted once the exact primary-name stage already resolved a fit");
    }

    [Test]
    public async Task REQ208_FindMatchAsync_AliasMatch_FuzzyStageNeverConsulted()
    {
        // Same ordering guarantee as above, one stage later: once the alias
        // stage resolves a fit, the fuzzy stage must never run either.
        var cell = BuildCell("Brazil", "AC Milan");
        var aliasPlayer = await SeedPlayerAsync("Ricardo Izecson dos Santos Leite", "Brazil", "AC Milan");
        await _playerAliasRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = aliasPlayer.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var aliasSpy = new CallCountingPlayerAliasRepository(_playerAliasRepository);
        var attributeSpy = new CallCountingPlayerAttributeRepository(_playerAttributeRepository);
        var matcher = BuildMatcher(playerAliasRepository: aliasSpy, playerAttributeRepository: attributeSpy);

        var result = await FindMatchAsync(matcher, cell, "Kaka");

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(aliasSpy.GetPlayersByNormalizedAliasAsyncCallCount, Is.EqualTo(1),
            "the alias stage must be consulted once the exact primary-name stage found nothing");
        Assert.That(attributeSpy.GetPlayersWithEitherAttributeAsyncCallCount, Is.EqualTo(0),
            "the fuzzy stage must never be consulted once the alias stage already resolved a fit");
    }

    [TestCase("Zidane", "Zidan", TestName = "REQ208_FindMatchAsync_FuzzyTypo_SingleDroppedLetter_MatchesViaPrimaryName")]
    [TestCase("Ronaldinho", "Ronaldinoh", TestName = "REQ208_FindMatchAsync_FuzzyTypo_TrailingTransposition_MatchesViaPrimaryName_LongerName")]
    [TestCase("Zinedine Zidane", "Zinedine Zidence", TestName = "REQ208_FindMatchAsync_FuzzyTypo_ExactlyAtThreshold_Matches")]
    public async Task REQ208_FindMatchAsync_FuzzyTypo_MatchesViaPrimaryName(string storedFullName, string submittedName)
    {
        var cell = BuildCell("France", "Arsenal");
        var player = await SeedPlayerAsync(storedFullName, "France", "Arsenal");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, submittedName);

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ208_FindMatchAsync_FuzzyTypo_MatchesViaAlias()
    {
        // A typo of a known alias deserves the same tolerance as a typo of
        // the primary name — "Kaeka" is one edit away from the alias "Kaka",
        // not from the player's full legal name.
        var cell = BuildCell("Brazil", "AC Milan");
        var player = await SeedPlayerAsync("Ricardo Izecson dos Santos Leite", "Brazil", "AC Milan");
        await _playerAliasRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = player.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Kaeka");

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ208_FindMatchAsync_FuzzyMatch_CandidateMustStillSatisfyBothCategories_DoesNotMatch()
    {
        // The fuzzy pass's bounded candidate pool is "satisfies at least one
        // of the cell's two categories" (never a full-table scan) — but a
        // name being fuzzy-close is not enough on its own: the same
        // both-categories check as every other stage still applies
        // afterwards. This player satisfies the row category (France) only,
        // so a fuzzy name match must not score correct.
        var cell = BuildCell("France", "Arsenal");
        await SeedPlayerAsync("Zidane", "France", "Chelsea");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Zidan");

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_FindMatchAsync_SimilarButDistinctPlayerName_DoesNotMatch()
    {
        // "Ronaldo" and "Rivaldo" are two different real players, seven
        // characters each, edit distance 2 apart — this codebase's chosen
        // tolerance for that length tier is 1, so this must NOT match. Guards
        // against an edit-distance threshold loose enough to make guessing
        // trivially easy by accepting a similarly-shaped but wrong name.
        var cell = BuildCell("Brazil", "Barcelona");
        await SeedPlayerAsync("Rivaldo", "Brazil", "Barcelona");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Ronaldo");

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_FindMatchAsync_ShortNickname_NoFuzzyToleranceForDistanceOne_DoesNotMatch()
    {
        // Names of 4 normalized characters or fewer get zero fuzzy
        // tolerance — "Pele" and "Dele" (Dele Alli's own nickname) are one
        // edit apart but are two different real players; at this length any
        // fuzzy pass would already have been an exact/alias hit if it were
        // really the same name.
        var cell = BuildCell("Brazil", "Santos");
        await SeedPlayerAsync("Pele", "Brazil", "Santos");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Dele");

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ208_FindMatchAsync_FuzzyTypo_DistanceExceedsThreshold_DoesNotMatch()
    {
        // One edit past this length tier's threshold (2) — "Zinedin
        // Zidence" is distance 3 from "Zinedine Zidane" — must not match,
        // confirming the threshold has a real ceiling rather than silently
        // accepting anything vaguely similar.
        var cell = BuildCell("France", "Real Madrid");
        await SeedPlayerAsync("Zinedine Zidane", "France", "Real Madrid");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Zinedin Zidence");

        Assert.That(result.IsCorrect, Is.False);
    }

    // ---- REQ-209: disambiguating multiple players with a matching name -----

    [Test]
    public async Task REQ209_FindMatchAsync_ExactlyOneCandidateSatisfiesBothCategories_AcceptedAutomatically()
    {
        var cell = BuildCell("France", "Arsenal");
        var fittingPlayer = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        // Same name, but doesn't satisfy the cell's categories — the
        // categories themselves must disambiguate.
        await SeedPlayerAsync("John Smith", "England", "Chelsea");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "John Smith");

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(fittingPlayer.Id));
        Assert.That(result.DisambiguationCandidates, Is.Null, "a single fitting candidate never needs a disambiguation prompt");
    }

    [Test]
    public async Task REQ209_FindMatchAsync_NoCandidateSatisfiesBothCategories_ReturnsIncorrect_RegardlessOfSameNamedPlayersElsewhere()
    {
        var cell = BuildCell("France", "Arsenal");
        await SeedPlayerAsync("John Smith", "England", "Chelsea");
        await SeedPlayerAsync("John Smith", "Spain", "Barcelona");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "John Smith");

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.DisambiguationCandidates, Is.Null,
            "no candidate satisfying both categories at all is a plain incorrect guess, not a disambiguation case");
    }

    [Test]
    public async Task REQ209_FindMatchAsync_MultipleCandidatesSatisfyBothCategories_ReturnsDisambiguationCandidates_NotAutoAccepted()
    {
        var cell = BuildCell("France", "Arsenal");
        var first = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var second = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        // Each candidate also has an "other" club, distinct from the cell's
        // own two categories (France/Arsenal) — these are what should
        // surface as DistinguishingAttributes, never France/Arsenal again.
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = first.Id, AttributeType = "club", AttributeValue = "Monaco" });
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = second.Id, AttributeType = "club", AttributeValue = "Lyon" });
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "John Smith");

        Assert.That(result.IsCorrect, Is.False, "an ambiguous guess is never auto-accepted on the player's behalf");
        Assert.That(result.PlayerAnswerId, Is.Null);
        Assert.That(result.DisambiguationCandidates, Is.Not.Null.And.Count.EqualTo(2));
        Assert.That(result.DisambiguationCandidates!.Select(c => c.PlayerId), Is.EquivalentTo(new[] { first.Id, second.Id }));
        var firstCandidate = result.DisambiguationCandidates!.Single(c => c.PlayerId == first.Id);
        var secondCandidate = result.DisambiguationCandidates!.Single(c => c.PlayerId == second.Id);
        Assert.That(firstCandidate.Name, Is.EqualTo("John Smith"));
        Assert.That(firstCandidate.DistinguishingAttributes, Is.EquivalentTo(new[] { "Monaco" }),
            "must show the candidate's OTHER attributes, never the cell's own France/Arsenal categories again");
        Assert.That(secondCandidate.DistinguishingAttributes, Is.EquivalentTo(new[] { "Lyon" }));
    }

    [Test]
    public async Task REQ209_FindMatchAsync_MultipleCandidatesWithNoOtherKnownAttributes_ReturnsEmptyDistinguishingAttributes_NotBlocked()
    {
        var cell = BuildCell("France", "Arsenal");
        var first = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var second = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "John Smith");

        Assert.That(result.DisambiguationCandidates, Is.Not.Null.And.Count.EqualTo(2));
        Assert.That(result.DisambiguationCandidates!.Select(c => c.PlayerId), Is.EquivalentTo(new[] { first.Id, second.Id }));
        Assert.That(result.DisambiguationCandidates!, Has.All.Matches<DisambiguationCandidate>(c => c.DistinguishingAttributes.Count == 0),
            "a candidate with no other known attributes must still appear, just with an empty list — never blocking the feature");
    }

    // ---- REQ-209/REQ-210: the ChosenPlayerId resubmission fast path -------

    [Test]
    public async Task REQ209_FindMatchAsync_ChosenPlayerIdMatchesAFittingCandidate_AcceptsIt()
    {
        var cell = BuildCell("France", "Arsenal");
        var first = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var second = await SeedPlayerAsync("John Smith", "France", "Arsenal");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "John Smith", chosenPlayerId: second.Id);

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(second.Id));
        Assert.That(result.DisambiguationCandidates, Is.Null, "a resolved ChosenPlayerId submission is a real scored guess, not another prompt");
    }

    [Test]
    public async Task REQ209_FindMatchAsync_ChosenPlayerIdRealPlayerButNoLongerSatisfiesBothCategories_TreatedAsOrdinaryIncorrectGuess_DoesNotThrow()
    {
        // staleChoice is a real player matching the submitted name, but only
        // satisfies ONE of the cell's two categories (e.g. an admin
        // correction landed between the disambiguation prompt and this
        // resubmission) — never trust the client-supplied id blindly, always
        // re-verify server-side.
        var cell = BuildCell("France", "Arsenal");
        var staleChoice = await SeedPlayerAsync("John Smith", "France", "Chelsea");
        var matcher = BuildMatcher();

        ScoreResult? result = null;
        Assert.DoesNotThrowAsync(async () => result = await FindMatchAsync(matcher, cell, "John Smith", chosenPlayerId: staleChoice.Id));

        Assert.That(result!.IsCorrect, Is.False);
        Assert.That(result.PlayerAnswerId, Is.Null);
        Assert.That(result.DisambiguationCandidates, Is.Null, "a failed ChosenPlayerId resubmission is a plain incorrect guess, not another prompt");
    }

    [Test]
    public async Task REQ209_FindMatchAsync_ChosenPlayerIdSuppliedButNothingMatchesAtAll_TreatedAsOrdinaryIncorrectGuess()
    {
        var cell = BuildCell("France", "Arsenal");
        var matcher = BuildMatcher();

        var result = await FindMatchAsync(matcher, cell, "Nobody At All", chosenPlayerId: Guid.NewGuid());

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.DisambiguationCandidates, Is.Null);
    }

    // ---- REQ-216/ADR-0057: ResolveWrongGuessPlayerAsync -------------------
    // Called by GridGameModule's own ResolveWrongGuessPlayerAsync exactly
    // once, only once it has already determined a cell just locked with its
    // final guess still incorrect — these tests exercise GridNameMatcher's
    // own implementation directly, independent of that caller-side trigger
    // condition (which is GridGameModuleTests' own responsibility to pin
    // down).

    [Test]
    public async Task REQ216_ResolveWrongGuessPlayerAsync_NoPlayerNameIndexMatch_ReturnsNull()
    {
        var matcher = BuildMatcher();

        var result = await matcher.ResolveWrongGuessPlayerAsync("Not A Real Player At All", CancellationToken.None);

        Assert.That(result, Is.Null,
            "a guess string matching no real PlayerNameIndex candidate at all has no identity to show (REQ-216)");
    }

    [Test]
    public async Task REQ216_ResolveWrongGuessPlayerAsync_PlayerNameIndexMatch_WithCachedPlayerRow_ReturnsCachedNameAndPhoto_WithoutCallingWikidata()
    {
        SeedNameIndexEntry("Clarence Seedorf");
        var cached = new Player { Id = Guid.NewGuid(), FullName = "Clarence Seedorf", PhotoUrl = "https://example.org/seedorf.jpg" };
        _dbContext.Players.Add(cached);
        await _dbContext.SaveChangesAsync();
        var fakeWikidataClient = new FakeWikidataClient();
        var matcher = BuildMatcher(wikidataClient: fakeWikidataClient);

        var result = await matcher.ResolveWrongGuessPlayerAsync("Clarence Seedorf", CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.PlayerName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(result.PhotoUrl, Is.EqualTo("https://example.org/seedorf.jpg"));
        Assert.That(fakeWikidataClient.QueriedNames, Is.Empty,
            "a wrong-but-real guess already cached from resolving some other cell must never pay for a live Wikidata round-trip");
    }

    [Test]
    public async Task REQ216_ResolveWrongGuessPlayerAsync_PlayerNameIndexMatch_WithCachedPlayerRowViaAlias_ReturnsCachedNameAndPhoto()
    {
        SeedNameIndexEntry("Kaka");
        var cached = new Player { Id = Guid.NewGuid(), FullName = "Ricardo Izecson dos Santos Leite", PhotoUrl = "https://example.org/kaka.jpg" };
        _dbContext.Players.Add(cached);
        await _dbContext.SaveChangesAsync();
        await _playerAliasRepository.AddPlayerAliasAsync(new PlayerAlias { PlayerId = cached.Id, Alias = "Kaka", NormalizedAlias = "kaka" });
        var matcher = BuildMatcher();

        var result = await matcher.ResolveWrongGuessPlayerAsync("Kaka", CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.PlayerName, Is.EqualTo("Ricardo Izecson dos Santos Leite"));
        Assert.That(result.PhotoUrl, Is.EqualTo("https://example.org/kaka.jpg"));
    }

    [Test]
    public async Task REQ216_ResolveWrongGuessPlayerAsync_NoCachedPlayerRow_ResolvesNameAndPhotoViaWikidataOnly()
    {
        SeedNameIndexEntry("Clarence Seedorf");
        var fakeWikidataClient = new FakeWikidataClient();
        fakeWikidataClient.SetResult("Clarence Seedorf", "Clarence Seedorf", "https://commons.example.org/seedorf.jpg");
        var matcher = BuildMatcher(wikidataClient: fakeWikidataClient);

        var result = await matcher.ResolveWrongGuessPlayerAsync("Clarence Seedorf", CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.PlayerName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(result.PhotoUrl, Is.EqualTo("https://commons.example.org/seedorf.jpg"));
        Assert.That(fakeWikidataClient.QueriedNames, Is.EqualTo(new[] { "Clarence Seedorf" }));
    }

    [Test]
    public async Task REQ216_ResolveWrongGuessPlayerAsync_WikidataLookupThrows_FallsBackToPlayerNameIndexPrimaryName_WithNullPhoto()
    {
        SeedNameIndexEntry("Clarence Seedorf");
        var fakeWikidataClient = new FakeWikidataClient();
        fakeWikidataClient.FailNextCalls(1);
        var matcher = BuildMatcher(wikidataClient: fakeWikidataClient);

        var result = await matcher.ResolveWrongGuessPlayerAsync("Clarence Seedorf", CancellationToken.None);

        Assert.That(result, Is.Not.Null,
            "ADR-0057: a failed live lookup must never remove the canonical name, only the photo — there is no " +
            "correctness verdict left to compute for a guess already known to be wrong, so this must never behave " +
            "like a fail-closed/incorrect outcome");
        Assert.That(result!.PlayerName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(result.PhotoUrl, Is.Null);
    }

    [Test]
    public async Task REQ216_ResolveWrongGuessPlayerAsync_WikidataLookupFindsNoMatch_FallsBackToPlayerNameIndexPrimaryName_WithNullPhoto()
    {
        SeedNameIndexEntry("Clarence Seedorf");
        // FakeWikidataClient with no SetResult configured returns null,
        // simulating a genuine "Wikidata answered, found nothing" outcome.
        var matcher = BuildMatcher(wikidataClient: new FakeWikidataClient());

        var result = await matcher.ResolveWrongGuessPlayerAsync("Clarence Seedorf", CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.PlayerName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(result.PhotoUrl, Is.Null);
    }

    [Test]
    public async Task REQ216_ResolveWrongGuessPlayerAsync_NullWikidataClient_FallsBackToPlayerNameIndexPrimaryName_WithoutThrowing()
    {
        // wikidataClient is nullable purely so tests/callers that don't wire
        // one up don't crash — production DI always supplies the real
        // client (Program.cs). Constructed directly here (bypassing
        // BuildMatcher, which always substitutes a FakeWikidataClient
        // default) so this genuinely exercises the null-wikidataClient
        // branch in GridNameMatcher.ResolveWrongGuessPlayerAsync.
        SeedNameIndexEntry("Clarence Seedorf");
        var matcher = new GridNameMatcher(
            _playerRepository, _playerAliasRepository, _playerAttributeRepository, _playerOverrideRepository, _playerNameIndexRepository,
            NullLogger<GridNameMatcher>.Instance, wikidataClient: null);

        var result = await matcher.ResolveWrongGuessPlayerAsync("Clarence Seedorf", CancellationToken.None);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.PlayerName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(result.PhotoUrl, Is.Null);
    }
}
