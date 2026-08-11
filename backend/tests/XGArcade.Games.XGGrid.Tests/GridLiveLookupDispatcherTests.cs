using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Games.XGGrid.Tests;

// S-119 (pure refactor, no behavior change): split out of
// GridGameModuleTests.cs alongside GridLiveLookupDispatcher itself — owns
// REQ-211's guess-time fallback dispatch (TryRefreshCellAsync, née
// RefreshCellFromLiveLookupAsync), including its persistent-failure
// short-circuit and WikidataQueryException -> LiveLookupUnavailableException
// translation. Tests that need the FULL "gate -> refresh -> re-match ->
// accept a genuinely correct guess" pipeline stayed in
// GridGameModuleTests.cs (that's the adapter's own orchestration, not this
// dispatcher's concern alone) — these tests exercise TryRefreshCellAsync
// directly, against a freshly-constructed GridLiveLookupDispatcher, the same
// "fakes/mocks only construct the one class under test" convention S-106/
// S-107 established for the IPlayerStoreRepository split (ADR-0067).
public class GridLiveLookupDispatcherTests
{
    private XGArcadeDbContext _dbContext = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private IPlayerDataQualityRepository _playerDataQualityRepository = null!;
    private FakeWikidataLookupService _wikidataLookupService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _playerDataQualityRepository = new PlayerDataQualityRepository(_dbContext);
        // No playerOverrideRepository/playerRepository/playerAttributeRepository
        // supplied — none of this file's tests assert on persisted match
        // data, only on TryRefreshCellAsync's own return value/exceptions
        // and the underlying wikidataLookupService's call counts/flags.
        _wikidataLookupService = new FakeWikidataLookupService();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private GridLiveLookupDispatcher BuildDispatcher(IWikidataLookupService? wikidataLookupService = null) =>
        new(_categoryValueRepository, wikidataLookupService ?? _wikidataLookupService, _playerDataQualityRepository,
            NullLogger<GridLiveLookupDispatcher>.Instance);

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

    // TryRefreshCellAsync takes a GridCell directly — unlike
    // GridGameModuleTests' SeedGridInstanceAsync, no GridInstance/repository
    // round-trip is needed, since the dispatcher never looks the cell up
    // itself.
    private static GridCell BuildCell(string rowCategoryType, string rowCategoryValue, string colCategoryType, string colCategoryValue) =>
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

    // 2026-08-10 fix: PlayerCacheWarmingService may already know, from its
    // own independent runs, that this exact pair's Wikidata query
    // structurally fails (PairLookupFailure.ConsecutiveFailureCount >=
    // PersistentFailureThreshold, ADR-0052) - before this fix, a guess
    // against such a pair still paid the full guess-time-fallback timeout
    // live, every guess, only to land on the same
    // LiveLookupUnavailableException anyway. Proves the new check
    // short-circuits before ever calling the live lookup at all, not merely
    // that it eventually throws the same exception.
    [Test]
    public async Task REQ211_TryRefreshCellAsync_PairAlreadyKnownPersistentFailure_ThrowsLiveLookupUnavailableException_WithoutCallingWikidata()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var cell = BuildCell(CategoryPairingRules.Country, "France", CategoryPairingRules.Club, "Arsenal");
        // PlayerCacheWarmingService.PersistentFailureThreshold consecutive
        // failures, recorded independently of this guess (as cache-warming
        // would) — simulates a pair already confirmed doomed.
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Arsenal", CancellationToken.None);
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Arsenal", CancellationToken.None);
        // Configured on the fake but must never be reached — proves the
        // short-circuit skips the live call entirely rather than racing it
        // to the same exception.
        _wikidataLookupService.SetMatches(
            "France", "Arsenal", [new Player { Id = Guid.NewGuid(), FullName = "Should Never Be Reached", WikidataQid = "Qunreached" }]);
        var dispatcher = BuildDispatcher();

        Assert.ThrowsAsync<LiveLookupUnavailableException>(async () =>
            await dispatcher.TryRefreshCellAsync(cell, CancellationToken.None));
        Assert.That(_wikidataLookupService.GetCallCount("France", "Arsenal"), Is.EqualTo(0),
            "a pair already known as a persistent technical failure must skip the live call entirely, not just race it to the same exception");
    }

    // A pair with only 1 recorded failure (below PersistentFailureThreshold)
    // must still get a real, independent live-lookup chance — proves the
    // short-circuit only trips at the threshold, not on any recorded failure.
    [Test]
    public async Task REQ211_TryRefreshCellAsync_PairBelowPersistentFailureThreshold_StillAttemptsLiveLookup()
    {
        SeedCountry("Argentina");
        SeedClub("Barcelona");
        var cell = BuildCell(CategoryPairingRules.Country, "Argentina", CategoryPairingRules.Club, "Barcelona");
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "Argentina", "club", "Barcelona", CancellationToken.None);
        var messi = new Player { Id = Guid.NewGuid(), FullName = "Lionel Messi", WikidataQid = "Qmessi" };
        _wikidataLookupService.SetMatches("Argentina", "Barcelona", [messi]);
        var dispatcher = BuildDispatcher();

        var refreshed = await dispatcher.TryRefreshCellAsync(cell, CancellationToken.None);

        Assert.That(refreshed, Is.True,
            "a pair with only 1 recorded failure (below PersistentFailureThreshold) must still get a real, independent live-lookup chance");
        Assert.That(_wikidataLookupService.GetCallCount("Argentina", "Barcelona"), Is.EqualTo(1));
    }

    // 2026-07-27 fix: a timeout here means this cell's correctness is
    // genuinely UNKNOWN, not "no match" — TryRefreshCellAsync must catch
    // WikidataQueryException and re-throw Core.Games.LiveLookupUnavailableException
    // instead of letting the DataSync-specific exception escape or silently
    // treating it as an ordinary incorrect guess.
    [Test]
    public async Task REQ211_TryRefreshCellAsync_TimesOut_ThrowsLiveLookupUnavailableException()
    {
        SeedCountry("France");
        SeedClub("Arsenal");
        var cell = BuildCell(CategoryPairingRules.Country, "France", CategoryPairingRules.Club, "Arsenal");
        _wikidataLookupService.FailWithTimeout("France", "Arsenal");
        var dispatcher = BuildDispatcher();

        Assert.ThrowsAsync<LiveLookupUnavailableException>(async () =>
            await dispatcher.TryRefreshCellAsync(cell, CancellationToken.None));
    }

    [Test]
    public async Task REQ211_TryRefreshCellAsync_CellCategoryTypeUnhandled_SkipsLiveLookup_DoesNotThrow()
    {
        // TryRefreshCellAsync's guard: Tier 0's live fallback knows how to
        // re-run Country(rows) x Club(cols) (S-007) and, as of S-030,
        // Club x Club — but the mirrored Club(rows) x Country(cols) shape
        // (never produced by GridGenerationService's SelectPairing, but not
        // otherwise impossible for a cell to have) isn't special-cased
        // either, and must gracefully skip the fallback (return false)
        // rather than throw. Neither "SomeRow" nor "SomeCol" is seeded in
        // any reference table either, so ResolveCandidateAsync alone would
        // already fail this pairing closed — this test's point is that
        // either way, the result is a clean false, never a throw.
        var cell = BuildCell(CategoryPairingRules.Club, "SomeRow", CategoryPairingRules.Country, "SomeCol");
        var dispatcher = BuildDispatcher();

        var refreshed = true;
        Assert.DoesNotThrowAsync(async () => refreshed = await dispatcher.TryRefreshCellAsync(cell, CancellationToken.None));

        Assert.That(refreshed, Is.False);
        Assert.That(_wikidataLookupService.GetCallCount("SomeRow", "SomeCol"), Is.EqualTo(0),
            "the live lookup must never be called for a pairing/value the fallback can't resolve");
    }

    [Test]
    public async Task REQ211_TryRefreshCellAsync_RowCategoryValueNotInReferenceTable_SkipsLiveLookup_DoesNotThrow()
    {
        // ResolveCandidateAsync's guard: a RowCategoryValue with no matching
        // seeded CountryDefinition (shouldn't happen in practice, since grid
        // generation only ever picks from that table — REQ-109) must still
        // fail closed rather than throw.
        SeedClub("Arsenal");
        var cell = BuildCell(CategoryPairingRules.Country, "Wakanda", CategoryPairingRules.Club, "Arsenal");
        var dispatcher = BuildDispatcher();

        var refreshed = true;
        Assert.DoesNotThrowAsync(async () => refreshed = await dispatcher.TryRefreshCellAsync(cell, CancellationToken.None));

        Assert.That(refreshed, Is.False);
    }

    [Test]
    public async Task REQ211_TryRefreshCellAsync_ColCategoryValueNotInReferenceTable_SkipsLiveLookup_DoesNotThrow()
    {
        // Same guard as above, for the column/club side.
        SeedCountry("France");
        var cell = BuildCell(CategoryPairingRules.Country, "France", CategoryPairingRules.Club, "PhantomClub");
        var dispatcher = BuildDispatcher();

        var refreshed = true;
        Assert.DoesNotThrowAsync(async () => refreshed = await dispatcher.TryRefreshCellAsync(cell, CancellationToken.None));

        Assert.That(refreshed, Is.False);
    }

    [Test]
    public async Task REQ211_TryRefreshCellAsync_TrophyTrophyCellUnhandledByFallback_SkipsLiveLookup_DoesNotThrow()
    {
        // Trophy x Trophy has no dedicated IWikidataLookupService method
        // (never generated in practice — see GridGenerationService's own
        // SelectPairing comment — but not otherwise impossible for a cell
        // to have) and must gracefully skip the fallback (return false)
        // rather than throw.
        SeedTrophy("Ballon d'Or");
        SeedTrophy("Golden Boot");
        var cell = BuildCell(CategoryPairingRules.Trophy, "Ballon d'Or", CategoryPairingRules.Trophy, "Golden Boot");
        var dispatcher = BuildDispatcher();

        var refreshed = true;
        Assert.DoesNotThrowAsync(async () => refreshed = await dispatcher.TryRefreshCellAsync(cell, CancellationToken.None));

        Assert.That(refreshed, Is.False);
    }

    // ---- REQ-114/ADR-0035: national teams as distinct footballing entities

    [Test]
    public async Task REQ114_TryRefreshCellAsync_OrdinaryCountryCell_StillDispatchesWithFlagFalse()
    {
        // The guess-time fallback's existing P27 path must stay completely
        // unaffected for every ordinary country.
        SeedCountry("France");
        SeedClub("Arsenal");
        var cell = BuildCell(CategoryPairingRules.Country, "France", CategoryPairingRules.Club, "Arsenal");
        var dispatcher = BuildDispatcher();

        await dispatcher.TryRefreshCellAsync(cell, CancellationToken.None);

        Assert.That(_wikidataLookupService.GetLastUsesCountryForSportProperty("France", "Arsenal"), Is.False);
    }
}
