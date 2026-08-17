using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid.Tests;

// COMP-05: IGameModule implementation for the xG Grid game.
//
// S-119 (pure refactor, no behavior change): GridGameModule split into
// GridGenerationService/GridNameMatcher/GridLiveLookupDispatcher — see
// GridGenerationServiceTests.cs, GridNameMatcherTests.cs, and
// GridLiveLookupDispatcherTests.cs for coverage of each. This file now only
// exercises GridGameModule's own remaining responsibility as a thin
// IGameModule adapter: the GenerateInstanceAsync/GetCellIdsAsync/
// GetCellCategoryTypesAsync passthroughs (GetMaxAttemptsForCellAsync is a
// trivial constant return with no dedicated test, same as before this
// split), and ScoreSubmissionAsync's gate/retry wiring end-to-end (REQ-211's
// "only worth a live-lookup retry when the guess matched a real
// PlayerNameIndex candidate," the single-retry bound, and "the fallback is
// never entered when the cache already answers the guess") — orchestration
// across the three split-out classes that isn't any one of them's own
// concern alone.
public class GridGameModuleTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IGridInstanceRepository _gridInstanceRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private IPlayerOverrideRepository _playerOverrideRepository = null!;
    private IPlayerDataQualityRepository _playerDataQualityRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAliasRepository _playerAliasRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerNameIndexRepository _playerNameIndexRepository = null!;
    private FakeWikidataLookupService _wikidataLookupService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _gridInstanceRepository = new GridInstanceRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _playerOverrideRepository = new PlayerOverrideRepository(_dbContext);
        _playerDataQualityRepository = new PlayerDataQualityRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAliasRepository = new PlayerAliasRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
        _playerNameIndexRepository = new PlayerNameIndexRepository(_dbContext);
        _wikidataLookupService = new FakeWikidataLookupService(_playerOverrideRepository, _playerRepository, _playerAttributeRepository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // Composes a real GridGenerationService/GridNameMatcher/
    // GridLiveLookupDispatcher (never fakes of the three split-out classes
    // themselves — this file's whole point is exercising the adapter's own
    // wiring across the real thing) behind a GridGameModule under test.
    // Every retained test in this file only needs minValidAnswers/maxAttempts
    // tuning — the override flexibility (custom Random/TimeProvider/
    // FakeWikidataLookupService/spy repositories) the original, pre-split
    // BuildModule exposed now lives on GridGenerationServiceTests'/
    // GridNameMatcherTests'/GridLiveLookupDispatcherTests' own BuildXxx
    // helpers, since every test that needed it moved there.
    //
    // liveLookupOptions/playerNameIndexRepositoryOverride/
    // liveLookupDispatcherOverride (ADR-0070/S-128): optional so every
    // existing call site above — which doesn't pass them — keeps exercising
    // the real IPlayerNameIndexRepository/GridLiveLookupDispatcher wired to
    // the enabled-by-default GridLiveLookupOptions, unchanged. Only the
    // REQ211/S-128 "flag disabled" tests below need to substitute a
    // call-counting spy for either dependency, or a non-default
    // GridLiveLookupOptions.
    private GridGameModule BuildModule(
        int minValidAnswers, int maxAttempts,
        GridLiveLookupOptions? liveLookupOptions = null,
        IPlayerNameIndexRepository? playerNameIndexRepositoryOverride = null,
        IGridLiveLookupDispatcher? liveLookupDispatcherOverride = null)
    {
        var dispatcher = liveLookupDispatcherOverride ?? new GridLiveLookupDispatcher(
            _categoryValueRepository, _wikidataLookupService, _playerDataQualityRepository, NullLogger<GridLiveLookupDispatcher>.Instance);
        var generationService = new GridGenerationService(
            _gridInstanceRepository, _categoryValueRepository, _playerAttributeRepository, dispatcher,
            new GridGenerationOptions { MinValidAnswers = minValidAnswers, MaxAttempts = maxAttempts, MaxDuration = TimeSpan.FromMinutes(10) },
            NullLogger<GridGenerationService>.Instance);
        var nameMatcher = new GridNameMatcher(
            _playerRepository, _playerAliasRepository, _playerAttributeRepository, _playerOverrideRepository, _playerNameIndexRepository,
            NullLogger<GridNameMatcher>.Instance, new FakeWikidataClient());

        return new GridGameModule(
            _gridInstanceRepository, playerNameIndexRepositoryOverride ?? _playerNameIndexRepository, generationService, nameMatcher, dispatcher,
            liveLookupOptions ?? new GridLiveLookupOptions(), NullLogger<GridGameModule>.Instance);
    }

    // ADR-0070/S-128: wraps a real IPlayerNameIndexRepository, counting calls
    // to ExistsByNormalizedNameAsync only — the one method REQ-211's
    // guess-time gate calls, and the one a disabled GridLiveLookupOptions
    // flag must never reach. Same "spy wraps the real thing" shape as
    // GridNameMatcherTests.cs's CallCountingPlayerAliasRepository/
    // CallCountingPlayerAttributeRepository.
    private sealed class CallCountingPlayerNameIndexRepository(IPlayerNameIndexRepository inner) : IPlayerNameIndexRepository
    {
        public int ExistsByNormalizedNameAsyncCallCount { get; private set; }

        public Task<IReadOnlyList<PlayerNameIndex>> SearchByPrefixAsync(
            string normalizedQuery, int limit, CancellationToken cancellationToken = default) =>
            inner.SearchByPrefixAsync(normalizedQuery, limit, cancellationToken);

        public Task<bool> ExistsByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default)
        {
            ExistsByNormalizedNameAsyncCallCount++;
            return inner.ExistsByNormalizedNameAsync(normalizedName, cancellationToken);
        }

        public Task<PlayerNameIndex?> FindByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default) =>
            inner.FindByNormalizedNameAsync(normalizedName, cancellationToken);

        public Task UpsertManyAsync(IEnumerable<PlayerNameIndex> entries, CancellationToken cancellationToken = default) =>
            inner.UpsertManyAsync(entries, cancellationToken);
    }

    // ADR-0070/S-128: wraps a real IGridLiveLookupDispatcher, counting calls
    // to TryRefreshCellAsync only — REQ-211's guess-time fallback's own call,
    // distinct from LookupMatchesAsync (REQ-103's grid-generation-time path,
    // which S-128's flag must never affect and this spy never counts).
    private sealed class CallCountingGridLiveLookupDispatcher(IGridLiveLookupDispatcher inner) : IGridLiveLookupDispatcher
    {
        public int TryRefreshCellAsyncCallCount { get; private set; }

        public Task<IReadOnlyList<Player>?> LookupMatchesAsync(
            string rowCategoryType, CategoryCandidate row,
            string colCategoryType, CategoryCandidate col,
            WikidataLookupOrigin origin,
            CancellationToken cancellationToken) =>
            inner.LookupMatchesAsync(rowCategoryType, row, colCategoryType, col, origin, cancellationToken);

        public Task<bool> TryRefreshCellAsync(GridCell cell, CancellationToken cancellationToken)
        {
            TryRefreshCellAsyncCallCount++;
            return inner.TryRefreshCellAsync(cell, cancellationToken);
        }
    }

    // REQ-211 (2026-07-27 fix): seeds a PlayerNameIndex row so
    // ExistsByNormalizedNameAsync's gate lets a test's guess through to the
    // live-lookup fallback — every REQ211_ScoreSubmissionAsync_* test below
    // that exercises the fallback needs this, now that it's no longer
    // triggered unconditionally for any unresolved guess. Only
    // PrimaryName/NormalizedName matter for the gate itself;
    // BirthYear/PrimaryNationality/PlayerId are irrelevant to it.
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

    private GridTemplate SeedTemplate(int size)
    {
        var template = new GridTemplate { Id = Guid.NewGuid(), Size = size, AllowedCategoryTypes = ["country", "club"] };
        _dbContext.GridTemplates.Add(template);
        _dbContext.SaveChanges();
        return template;
    }

    private CountryDefinition SeedCountry(string name, string? wikidataQid = "unset", bool usesCountryForSportProperty = false)
    {
        var country = new CountryDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            WikidataQid = wikidataQid == "unset" ? $"Qcountry-{name}" : wikidataQid,
            UsesCountryForSportProperty = usesCountryForSportProperty,
        };
        _dbContext.CountryDefinitions.Add(country);
        _dbContext.SaveChanges();
        return country;
    }

    private ClubDefinition SeedClub(string name, string? wikidataQid = "unset")
    {
        var club = new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid == "unset" ? $"Qclub-{name}" : wikidataQid };
        _dbContext.ClubDefinitions.Add(club);
        _dbContext.SaveChanges();
        return club;
    }

    private TrophyDefinition SeedTrophy(string name, string? wikidataQid = "unset", bool isTeamTrophy = false)
    {
        var trophy = new TrophyDefinition
        {
            Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid == "unset" ? $"Qtrophy-{name}" : wikidataQid, IsTeamTrophy = isTeamTrophy,
        };
        _dbContext.TrophyDefinitions.Add(trophy);
        _dbContext.SaveChanges();
        return trophy;
    }

    // Seeds `count` distinct players in the local cache, each satisfying
    // both (nationality = countryName) and (club = clubName), so
    // CountPlayersWithBothAttributesAsync(countryName, clubName) == count.
    private void SeedCachedMatches(string countryName, string clubName, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var player = new Player
            {
                Id = Guid.NewGuid(),
                FullName = $"{countryName}-{clubName}-Player{i}",
                WikidataQid = $"Qplayer-{countryName}-{clubName}-{i}",
            };
            _dbContext.Players.Add(player);
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = countryName });
            _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = clubName });
        }
        _dbContext.SaveChanges();
    }

    // Seeds a single-cell GridInstance directly (bypassing GenerateInstanceAsync
    // entirely) — S-009's ScoreSubmissionAsync tests only need a fixed cell to
    // score guesses against, not a whole generated grid.
    private async Task<(Guid InstanceId, Guid CellId)> SeedGridInstanceAsync(
        string rowCategoryValue, string colCategoryValue,
        string rowCategoryType = CategoryPairingRules.Country, string colCategoryType = CategoryPairingRules.Club)
    {
        var instanceId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        var instance = new GridInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Cells =
            [
                new GridCell
                {
                    Id = cellId,
                    GridInstanceId = instanceId,
                    Row = 0,
                    Col = 0,
                    RowCategoryType = rowCategoryType,
                    RowCategoryValue = rowCategoryValue,
                    ColCategoryType = colCategoryType,
                    ColCategoryValue = colCategoryValue,
                },
            ],
        };
        await _gridInstanceRepository.AddInstanceAsync(instance);
        return (instanceId, cellId);
    }

    // Seeds a Player with cached PlayerAttribute rows for both nationality and
    // club — the "effective data" ScoreSubmissionAsync's guess-checking reads
    // via HasEffectiveAttributeAsync (REQ-203).
    private async Task<Player> SeedPlayerAsync(string fullName, string nationality, string club)
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName, WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        _dbContext.Players.Add(player);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = nationality });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = club });
        await _dbContext.SaveChangesAsync();
        return player;
    }

    // ---- GenerateInstanceAsync passthrough ---------------------------------

    [Test]
    public async Task GenerateInstanceAsync_UnknownTemplateId_ThrowsGridGenerationException()
    {
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var ex = Assert.ThrowsAsync<GridGenerationException>(async () =>
            await module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));

        Assert.That(ex!.Message, Does.Contain("not found"),
            "proves GenerateInstanceAsync forwards to IGridGenerationService and its exception crosses the adapter boundary unchanged");
    }

    // ---- ADR-0021: cell ids for round-close's unanswered-cell penalty -----

    [Test]
    public async Task REQ206_GetCellIdsAsync_GeneratedInstance_ReturnsEveryCellId()
    {
        var template = SeedTemplate(size: 1);
        SeedCountry("France");
        SeedClub("Arsenal");
        SeedCachedMatches("France", "Arsenal", 3);
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);
        var result = await module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var instance = await _gridInstanceRepository.GetInstanceByIdAsync(result.Id);

        var cellIds = await module.GetCellIdsAsync(result.Id);

        Assert.That(cellIds, Is.EquivalentTo(instance!.Cells.Select(c => c.Id)));
    }

    [Test]
    public void GetCellIdsAsync_UnknownInstanceId_ThrowsGuessScoringException()
    {
        var module = BuildModule(minValidAnswers: 3, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(async () =>
            await module.GetCellIdsAsync(Guid.NewGuid()));
    }

    // ---- ScoreSubmissionAsync: instance/cell lookup ------------------------

    [Test]
    public void ScoreSubmissionAsync_UnknownInstanceId_ThrowsGuessScoringException()
    {
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(async () =>
            await module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new GuessSubmission(Guid.NewGuid(), "Anyone")));
    }

    [Test]
    public async Task ScoreSubmissionAsync_UnknownCellId_ThrowsGuessScoringException()
    {
        var (instanceId, _) = await SeedGridInstanceAsync("France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(async () =>
            await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(Guid.NewGuid(), "Anyone")));
    }

    // ---- REQ-211: guess-time live verification, gate/retry wiring ---------
    // Reproduces the reported bug: grid generation's cache-based validity
    // check (REQ-101/MinValidAnswers) only ever needs to prove a cell has
    // *some* cached matches, never to catalog every one — ADR-0010's
    // documented gap. A genuinely correct player (e.g. Messi for
    // Barcelona x Argentina) can have no PlayerAttribute data at all for
    // this specific cell and get wrongly marked incorrect, even though a
    // live Wikidata lookup would confirm the guess. These tests pin down
    // ScoreSubmissionAsync's own orchestration of that fallback — whether
    // IGridNameMatcher/IGridLiveLookupDispatcher even get called, and how
    // many times — not either of those classes' own internal behavior (see
    // GridNameMatcherTests/GridLiveLookupDispatcherTests for that).

    // 2026-07-27 fix: the actual bug-bundle regression test — before this
    // fix, the live-lookup fallback ran unconditionally for every guess that
    // didn't already resolve from cache, including a name that never matched
    // anything in PlayerNameIndex at all (CLAUDE.md's own boundary rule:
    // "only trigger a live lookup when the guess matched a real
    // PlayerNameIndex candidate"). No PlayerNameIndex row is seeded here at
    // all — the guessed name genuinely can't be a real player, so the live
    // lookup must never even be attempted, regardless of whether Wikidata
    // would have found something (the fake is deliberately configured WITH a
    // match it must never be allowed to reach).
    [Test]
    public async Task REQ211_ScoreSubmissionAsync_GuessNotInPlayerNameIndex_NeverTriggersLiveLookup()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        // Configured on the fake but must never be reached — proves the gate
        // itself blocks the call, not merely that Wikidata found nothing.
        _wikidataLookupService.SetMatches(
            "France", "Arsenal", [new Player { Id = Guid.NewGuid(), FullName = "Should Never Be Reached", WikidataQid = "Qunreached" }]);
        // Deliberately no SeedNameIndexEntry call.
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nobody Real"));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a guess that matched nothing in PlayerNameIndex must never trigger a live Wikidata lookup at all");
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        SeedCountry("Argentina");
        SeedClub("Barcelona");
        var (instanceId, cellId) = await SeedGridInstanceAsync("Argentina", "Barcelona");
        // Some other player already satisfies this cell in the cache — this
        // is what let grid generation accept the pairing in the first place
        // (REQ-101) — but the guessed player himself was never synced, so
        // nothing cached confirms or denies him.
        await SeedPlayerAsync("Javier Mascherano", "Argentina", "Barcelona");
        var messi = new Player { Id = Guid.NewGuid(), FullName = "Lionel Messi", WikidataQid = "Qmessi" };
        _wikidataLookupService.SetMatches("Argentina", "Barcelona", [messi]);
        SeedNameIndexEntry("Lionel Messi");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Lionel Messi"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(messi.Id));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_LiveLookupFallback_NeverTriggeredWhenCachedDataAlreadyAnswersTheGuess()
    {
        // The fallback must be narrow (ADR-0010) — a guess that already
        // resolves from cached data must never trigger a live call at all.
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var player = await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        // Configured but must never be consulted, since the cache already
        // answers this guess correctly.
        _wikidataLookupService.SetMatches("France", "Arsenal", [new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Qother" }]);
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Thierry Henry"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_GenuinelyIncorrectGuess_LiveLookupFindsNoMatch_StaysIncorrect()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        // No matches configured on the fake at all — mirrors a genuine
        // Wikidata no-match, not merely an untried combination.
        SeedNameIndexEntry("Nicolas Anelka");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nicolas Anelka"));

        Assert.That(result.IsCorrect, Is.False);
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_GenuinelyIncorrectGuess_LiveLookupFindsNoMatch_OnlyCallsLiveLookupOnce()
    {
        // ADR-0018: the fallback is a single re-run, never a loop/recursion —
        // bounded by REQ-210's 2-attempts-per-cell cap, same as every other
        // guess-time cost. Even when the re-run still can't answer the
        // guess, LookupAndPersistAsync must be invoked exactly once for this
        // cell's country/club pair, not retried further within the same call.
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        SeedNameIndexEntry("Nicolas Anelka");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Nicolas Anelka"));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetLastOrigin("France", "Arsenal"), Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
        Assert.That(_wikidataLookupService.GetLastTimeoutTier("France", "Arsenal"), Is.EqualTo(WikidataQueryTimeoutTier.Default));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_PlayerAlreadyCachedFromUnrelatedCell_LiveLookupFillsOnlyMissingCategory()
    {
        // The bug report's exact repro shape (ADR-0018): the guessed player
        // is not new to the store — they already have this cell's ROW
        // category (nationality) cached from an entirely unrelated
        // country/club pairing (e.g. a different club cell for the same
        // country) — but nothing yet confirms this cell's COLUMN category
        // (club). This must be distinguished from "player doesn't exist at
        // all yet": the live lookup's upsert (by WikidataQid) must find the
        // existing player row and add only the missing club attribute,
        // never create a duplicate Player.
        SeedCountry("Argentina");
        SeedClub("Barcelona");
        var (instanceId, cellId) = await SeedGridInstanceAsync("Argentina", "Barcelona");
        var messi = new Player { Id = Guid.NewGuid(), FullName = "Lionel Messi", WikidataQid = "Qmessi" };
        _dbContext.Players.Add(messi);
        // Cached from some other cell (e.g. Argentina x PSG) — confirms the
        // row category alone, nothing about this cell's club.
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = messi.Id, AttributeType = "nationality", AttributeValue = "Argentina" });
        await _dbContext.SaveChangesAsync();
        _wikidataLookupService.SetMatches("Argentina", "Barcelona", [messi]);
        SeedNameIndexEntry("Lionel Messi");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Lionel Messi"));

        Assert.That(result.IsCorrect, Is.True,
            "a live lookup must resolve a player who already exists with one category cached from an unrelated cell, " +
            "not just a player who is entirely new to the store");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(messi.Id));
        Assert.That(await _dbContext.Players.CountAsync(p => p.WikidataQid == "Qmessi"), Is.EqualTo(1),
            "the live lookup upserts by WikidataQid — it must never create a duplicate Player row for a player already known");
        Assert.That(await _playerOverrideRepository.HasEffectiveAttributeAsync(messi.Id, "club", "Barcelona"), Is.True);
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_ClubClubCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        // S-030: the fallback's Club x Club branch — same reproduction shape
        // as the Country x Club test above, but for a cell whose row AND
        // column are both category type "club".
        SeedClub("Barcelona");
        SeedClub("Paris Saint-Germain");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "Barcelona", "Paris Saint-Germain",
            rowCategoryType: CategoryPairingRules.Club, colCategoryType: CategoryPairingRules.Club);
        // Some other player already satisfies this Club x Club cell in the
        // cache — this is what let grid generation accept the pairing in
        // the first place (REQ-101) — but the guessed player himself was
        // never synced, so nothing cached confirms or denies him.
        var otherPlayer = new Player { Id = Guid.NewGuid(), FullName = "Some Other Player", WikidataQid = "Qother-clubclub" };
        _dbContext.Players.Add(otherPlayer);
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = otherPlayer.Id, AttributeType = "club", AttributeValue = "Barcelona" });
        _dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = otherPlayer.Id, AttributeType = "club", AttributeValue = "Paris Saint-Germain" });
        await _dbContext.SaveChangesAsync();
        var neymar = new Player { Id = Guid.NewGuid(), FullName = "Neymar Jr", WikidataQid = "Qneymar" };
        _wikidataLookupService.SetClubClubMatches("Barcelona", "Paris Saint-Germain", [neymar]);
        SeedNameIndexEntry("Neymar Jr");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Neymar Jr"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata Club x Club lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(neymar.Id));
        Assert.That(_wikidataLookupService.GetClubClubCallCount("Barcelona", "Paris Saint-Germain"), Is.EqualTo(1));
        Assert.That(
            _wikidataLookupService.GetClubClubLastOrigin("Barcelona", "Paris Saint-Germain"),
            Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_TrophyCountryCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        SeedCountry("France");
        SeedTrophy("Ballon d'Or");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "France", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var zidane = new Player { Id = Guid.NewGuid(), FullName = "Zinedine Zidane", WikidataQid = "Qzidane" };
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "France", [zidane]);
        SeedNameIndexEntry("Zinedine Zidane");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Zinedine Zidane"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata Trophy x Country lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(zidane.Id));
        Assert.That(_wikidataLookupService.GetTrophyCountryCallCount("Ballon d'Or", "France"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetTrophyCountryLastOrigin("Ballon d'Or", "France"), Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
    }

    [Test]
    public async Task REQ211_ScoreSubmissionAsync_TrophyClubCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        SeedClub("Real Madrid");
        SeedTrophy("Ballon d'Or");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "Real Madrid", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Club, colCategoryType: CategoryPairingRules.Trophy);
        var modric = new Player { Id = Guid.NewGuid(), FullName = "Luka Modric", WikidataQid = "Qmodric" };
        _wikidataLookupService.SetTrophyClubMatches("Ballon d'Or", "Real Madrid", [modric]);
        SeedNameIndexEntry("Luka Modric");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Luka Modric"));

        Assert.That(result.IsCorrect, Is.True,
            "a live Wikidata Trophy x Club lookup must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(modric.Id));
        Assert.That(_wikidataLookupService.GetTrophyClubCallCount("Ballon d'Or", "Real Madrid"), Is.EqualTo(1));
        Assert.That(_wikidataLookupService.GetTrophyClubLastOrigin("Ballon d'Or", "Real Madrid"), Is.EqualTo(WikidataLookupOrigin.GuessTimeFallback));
    }

    [Test]
    public async Task REQ108_ScoreSubmissionAsync_NationalTeamCountryTrophyCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupWithUsesCountryForSportPropertyTrue()
    {
        // REQ-211's guess-time fallback dispatching through the right query
        // path for a national-team x trophy cell — mirrors
        // REQ114_ScoreSubmissionAsync_NationalTeamCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess
        // below, but the column category is Trophy.
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedTrophy("Ballon d'Or");
        var (instanceId, cellId) = await SeedGridInstanceAsync(
            "England", "Ballon d'Or", rowCategoryType: CategoryPairingRules.Country, colCategoryType: CategoryPairingRules.Trophy);
        var kane = new Player { Id = Guid.NewGuid(), FullName = "Harry Kane", WikidataQid = "Qkane" };
        _wikidataLookupService.SetTrophyCountryMatches("Ballon d'Or", "England", [kane]);
        SeedNameIndexEntry("Harry Kane");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Harry Kane"));

        Assert.That(result.IsCorrect, Is.True,
            "a live lookup for a national-team x trophy cell must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(kane.Id));
        Assert.That(_wikidataLookupService.GetTrophyCountryLastUsesCountryForSportProperty("Ballon d'Or", "England"), Is.True,
            "the guess-time fallback (IGridLiveLookupDispatcher.TryRefreshCellAsync -> ResolveCandidateAsync) must re-resolve the full " +
            "CountryDefinition row, including its UsesCountryForSportProperty flag, not just Name/WikidataQid");
    }

    [Test]
    public async Task REQ114_ScoreSubmissionAsync_NationalTeamCell_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess()
    {
        // REQ-211's guess-time fallback dispatching through the right query
        // path for a national-team cell — mirrors
        // REQ211_ScoreSubmissionAsync_NoCachedCandidateSatisfiesCell_FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess
        // above, but the row category is a flagged national team.
        SeedCountry("England", usesCountryForSportProperty: true);
        SeedClub("Tottenham Hotspur");
        var (instanceId, cellId) = await SeedGridInstanceAsync("England", "Tottenham Hotspur");
        // Some other player already satisfies this cell in the cache (what
        // let grid generation accept the pairing in the first place) — but
        // the guessed player himself was never synced.
        await SeedPlayerAsync("Some Other Spur", "England", "Tottenham Hotspur");
        var kane = new Player { Id = Guid.NewGuid(), FullName = "Harry Kane", WikidataQid = "Qkane" };
        _wikidataLookupService.SetMatches("England", "Tottenham Hotspur", [kane]);
        SeedNameIndexEntry("Harry Kane");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Harry Kane"));

        Assert.That(result.IsCorrect, Is.True,
            "a live lookup for a national-team cell must be able to confirm a genuinely correct guess even when nothing cached yet supports it");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(kane.Id));
        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("England", "Tottenham Hotspur"), Is.True,
            "the guess-time fallback (IGridLiveLookupDispatcher.TryRefreshCellAsync -> ResolveCandidateAsync) must re-resolve the full " +
            "CountryDefinition row, including its UsesCountryForSportProperty flag, not just Name/WikidataQid");
    }

    // ---- S-128/ADR-0070: GridLiveLookupOptions.Enabled = false gates
    // REQ-211's guess-time fallback only -------------------------------------
    // The product owner wants an operational off switch to validate S-127's
    // proactively-built cache on its own — these tests pin down that
    // disabling it produces byte-for-byte the same outcome an unresolved
    // guess had before REQ-211 existed at all (fail closed, no
    // PlayerNameIndex query, no live-lookup dispatch), not a new error/UX.
    // Every REQ211_* test above passes GridLiveLookupOptions unset
    // (BuildModule's own default), proving Enabled=true — the default — is
    // unaffected by this flag's existence.

    [Test]
    public async Task ADR0070_ScoreSubmissionAsync_LiveLookupDisabled_UnresolvedGuess_NeverCallsPlayerNameIndexOrLiveLookupDispatcher()
    {
        SeedCountry("Argentina");
        SeedClub("Barcelona");
        var (instanceId, cellId) = await SeedGridInstanceAsync("Argentina", "Barcelona");
        // Some other player already satisfies this cell in the cache (what
        // let grid generation accept the pairing in the first place) — but
        // the guessed player himself was never synced, so nothing cached
        // confirms or denies him. With the flag enabled this would be exactly
        // REQ211_ScoreSubmissionAsync_NoCachedCandidateSatisfiesCell_
        // FallsBackToLiveLookupAndAcceptsGenuinelyCorrectGuess's setup.
        await SeedPlayerAsync("Javier Mascherano", "Argentina", "Barcelona");
        var messi = new Player { Id = Guid.NewGuid(), FullName = "Lionel Messi", WikidataQid = "Qmessi" };
        // Configured on both the underlying Wikidata fake and the
        // PlayerNameIndex — a genuinely correct, indexed guess that the
        // enabled-flag path would confirm — but the flag being off must
        // block the gate before either dependency is ever consulted.
        _wikidataLookupService.SetMatches("Argentina", "Barcelona", [messi]);
        SeedNameIndexEntry("Lionel Messi");
        var nameIndexSpy = new CallCountingPlayerNameIndexRepository(_playerNameIndexRepository);
        var dispatcherSpy = new CallCountingGridLiveLookupDispatcher(
            new GridLiveLookupDispatcher(
                _categoryValueRepository, _wikidataLookupService, _playerDataQualityRepository, NullLogger<GridLiveLookupDispatcher>.Instance));
        var module = BuildModule(
            minValidAnswers: 1, maxAttempts: 5,
            liveLookupOptions: new GridLiveLookupOptions { Enabled = false },
            playerNameIndexRepositoryOverride: nameIndexSpy,
            liveLookupDispatcherOverride: dispatcherSpy);

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Lionel Messi"));

        Assert.That(result.IsCorrect, Is.False,
            "with the flag off, a guess that only a live lookup could confirm must fail closed, exactly as it would have before REQ-211 existed");
        Assert.That(nameIndexSpy.ExistsByNormalizedNameAsyncCallCount, Is.EqualTo(0),
            "the flag must gate before the PlayerNameIndex existence check — no point spending that query when the fallback is off");
        Assert.That(dispatcherSpy.TryRefreshCellAsyncCallCount, Is.EqualTo(0),
            "the flag must gate before any live-lookup dispatch at all");
        Assert.That(_wikidataLookupService.GetCallCount("Argentina", "Barcelona"), Is.EqualTo(0),
            "no Wikidata call of any kind should result from a disabled guess-time fallback");
    }

    [Test]
    public async Task ADR0070_ScoreSubmissionAsync_LiveLookupDisabled_CachedDataAlreadyAnswersTheGuess_StillResolvesCorrectFromCache()
    {
        // The flag only gates the fallback branch — a guess that already
        // resolves from cached data must be completely unaffected, same as
        // REQ211_ScoreSubmissionAsync_LiveLookupFallback_
        // NeverTriggeredWhenCachedDataAlreadyAnswersTheGuess above.
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var player = await SeedPlayerAsync("Thierry Henry", "France", "Arsenal");
        var module = BuildModule(
            minValidAnswers: 1, maxAttempts: 5,
            liveLookupOptions: new GridLiveLookupOptions { Enabled = false });

        var result = await module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(cellId, "Thierry Henry"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(player.Id));
    }

    // ---- REQ-215/ADR-0052 (S-089, architecture-review fix):
    // GetCellCategoryTypesAsync ---------------------------------------------

    [Test]
    public async Task REQ215_GetCellCategoryTypesAsync_ReturnsTheSeededCellsRowAndColCategoryTypes()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var (instanceId, cellId) = await SeedGridInstanceAsync("France", "Arsenal");
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        var categoryTypes = await module.GetCellCategoryTypesAsync(instanceId, cellId);

        Assert.That(categoryTypes.RowCategoryType, Is.EqualTo(CategoryPairingRules.Country));
        Assert.That(categoryTypes.ColCategoryType, Is.EqualTo(CategoryPairingRules.Club));
    }

    [Test]
    public void REQ215_GetCellCategoryTypesAsync_UnknownCellId_ThrowsGuessScoringException()
    {
        var module = BuildModule(minValidAnswers: 1, maxAttempts: 5);

        Assert.ThrowsAsync<GuessScoringException>(
            async () => await module.GetCellCategoryTypesAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
