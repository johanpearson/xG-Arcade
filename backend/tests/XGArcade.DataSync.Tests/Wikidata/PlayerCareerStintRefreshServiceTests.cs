using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// ADR-0054: same real-InMemory-repository-plus-FakeWikidataClient pattern as
// PlayerPhotoBackfillServiceTests (docs/coding-guidelines.md "don't
// over-mock").
public class PlayerCareerStintRefreshServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    // S-106/S-107 (pure refactor): AddPlayerAsync lives on IPlayerRepository;
    // GetCareerStintsByPlayerIdsAsync/AddCareerStintsBatchAsync/
    // GetCareerStintCandidatePlayerIdsAsync live on
    // IPlayerCareerStintRepository — see ADR-0067 for the full split of the
    // original, now-deleted IPlayerStoreRepository.
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private FakeWikidataClient _wikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerCareerStintRepository = new PlayerCareerStintRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerCareerStintRefreshService BuildService() =>
        new(_wikidataClient, _playerCareerStintRepository, _playerRepository, _categoryValueRepository, NullLogger<PlayerCareerStintRefreshService>.Instance);

    private async Task<Player> SeedPlayerAsync(string wikidataQid) =>
        await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {wikidataQid}", WikidataQid = wikidataQid });

    private async Task<ClubDefinition> SeedClubAsync(string name, string wikidataQid)
    {
        var club = new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid };
        await _categoryValueRepository.AddClubAsync(club);
        return club;
    }

    [Test]
    public async Task RefreshCareerStintsAsync_PlayerWithNoExistingStints_PersistsEveryFetchedStint()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Monaco", 1994, 1999, 105),
            new WikidataCareerStintEntry("Juventus", 1999, 1999, 16),
            new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Monaco", "Juventus", "Arsenal" }));
    }

    // The whole point of ADR-0054: a club xG Grid's own byproduct queries
    // never happened to discover (not in ClubDefinition, or simply never
    // queried yet) still gets picked up by the full-career fetch.
    [Test]
    public async Task RefreshCareerStintsAsync_ClubNotInAnyPriorByproductData_IsStillPersisted()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Celtic", 2007, 2008, 31));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Does.Contain("Celtic"));
    }

    [Test]
    public async Task RefreshCareerStintsAsync_StintAlreadyPersisted_IsNotDuplicated()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 }]);

        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1), "an already-stored, identical stint must not be duplicated");
    }

    [Test]
    public async Task RefreshCareerStintsAsync_PlayerWithNoWikidataQid_IsNeverQueried()
    {
        var player = await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "No QID Player" });

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        Assert.That(_wikidataClient.QueriedCareerStintBatches, Is.Empty);
    }

    [Test]
    public async Task RefreshCareerStintsAsync_EmptyPlayerIdList_DoesNothing()
    {
        await BuildService().RefreshCareerStintsAsync([]);

        Assert.That(_wikidataClient.QueriedCareerStintBatches, Is.Empty);
    }

    // ADR-0054's core safety property: a Wikidata failure here must never
    // propagate — it would fail the whole xG Path round-generation call it's
    // invoked from (XGPathGameModule.GenerateInstanceAsync), which REQ-103's
    // "never block generation on a Wikidata failure" reasoning forbids.
    [Test]
    public async Task RefreshCareerStintsAsync_WikidataQueryFails_DoesNotThrow_ExistingStintsUntouched()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);

        _wikidataClient.FailNextCareerStintBatches(1);

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshCareerStintsAsync([player.Id]));

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Arsenal" }),
            "a failed refresh must leave whatever data already existed untouched, not wipe it");
    }

    [Test]
    public async Task RefreshCareerStintsAsync_PlayerWithNoWikidataCareerData_PersistsNothing_IsNotTreatedAsAFailure()
    {
        var player = await SeedPlayerAsync("Q1519"); // No SetCareerStints call — genuinely no P54 data.

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshCareerStintsAsync([player.Id]));

        Assert.That(await _playerCareerStintRepository.GetCareerStintsAsync(player.Id), Is.Empty);
    }

    // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
    // ADR-0059): the core of the fix — a fetched stint whose ClubQid
    // matches a seeded ClubDefinition must be persisted under the seeded
    // ClubDefinition.Name, not Wikidata's own raw (suffix-normalized only)
    // label, even when that label differs from the seed by more than a
    // legal-suffix token.
    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_FetchedClubQidMatchesSeededClub_PersistsSeededClubDefinitionName()
    {
        await SeedClubAsync("Lyon", "Q704");
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Olympique Lyonnais", 2000, 2003, 90, ClubQid: "Q704"));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Lyon" }),
            "a stint whose ClubQid matches a seeded club must be canonicalized to ClubDefinition.Name");
    }

    // Fallback half of the same fix: a stint at a club with no matching
    // seeded ClubDefinition (either because ClubQid is null, or because it
    // doesn't match any seeded club's WikidataQid) keeps its best-effort,
    // suffix-normalized label — still useful for xG Path's own display and
    // ClubGapAuditService's gap detection.
    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_FetchedClubQidMatchesNoSeededClub_KeepsBestEffortLabel()
    {
        await SeedClubAsync("Lyon", "Q704"); // Seeded, but not the club this stint is at.
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Some Obscure Club", 2000, 2003, 12, ClubQid: "Q999999"));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Some Obscure Club" }));
    }

    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_FetchedStintHasNoClubQid_KeepsBestEffortLabel()
    {
        await SeedClubAsync("Lyon", "Q704");
        var player = await SeedPlayerAsync("Q1519");
        // No ClubQid supplied — defaults to null, same as a defensive
        // ParseCareerStintBindings row with no ?club binding.
        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Lyon Reserve", 2000, 2003, 5));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Lyon Reserve" }));
    }

    // The whole point of this fix: the same real stint, fetched via this
    // class's full-career path with Wikidata's own raw label, must
    // canonicalize to the exact same ClubName a byproduct xG Grid lookup
    // (WikidataLookupService.PersistCareerStintsAsync, which always writes
    // ClubDefinition.Name directly) would have written for the same real
    // club — so the "already persisted" dedup check treats them as the
    // same stint, not two.
    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_CanonicalizedClubName_MatchesGridByproductWriterConvention_DedupesAgainstIt()
    {
        await SeedClubAsync("Lyon", "Q704");
        var player = await SeedPlayerAsync("Q1519");
        // Simulates a stint xG Grid's own country x club intersection
        // lookup already recorded, using ClubDefinition.Name directly
        // (WikidataLookupService.PersistCareerStintsAsync's own writer
        // convention).
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Lyon", StartYear = 2000, EndYear = 2003, AppearanceCount = 90 }]);

        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Olympique Lyonnais", 2000, 2003, 90, ClubQid: "Q704"));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1),
            "the full-career fetch's 'Olympique Lyonnais' row must canonicalize to 'Lyon' and be recognized as the same stint already persisted, not duplicated");
    }

    // The second, "for free" correctness fix this change carries (see this
    // story's own root-cause notes): GetCareerStintCandidatePlayerIdsAsync
    // (XGPathGameModule's hot eligibility path) does an exact,
    // case-sensitive match against seeded ClubDefinition.Name. Before this
    // fix, a stint persisted under a non-canonical Wikidata label (e.g.
    // "Olympique Lyonnais") never matched "Lyon" and so never counted
    // toward eligibility, even though the player genuinely played for a
    // seeded club. This proves the canonicalized ClubName now satisfies
    // that exact-match check.
    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_CanonicalizedClubName_SatisfiesCareerStintCandidateEligibilityMatch()
    {
        var club = await SeedClubAsync("Lyon", "Q704");
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Olympique Lyonnais", 2000, 2003, 90, ClubQid: "Q704"),
            new WikidataCareerStintEntry("Juventus", 2003, 2006, 60),
            new WikidataCareerStintEntry("Arsenal", 2006, 2009, 40));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var seededClubNames = new HashSet<string> { club.Name };
        var candidateIds = await _playerCareerStintRepository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minStintCount: 3);

        Assert.That(candidateIds, Does.Contain(player.Id),
            "a stint originally labeled 'Olympique Lyonnais' must canonicalize to the seeded 'Lyon' and satisfy exact-match eligibility");
    }

    // ---- ADR-0069/REQ-1210: career-stint reconciliation --------------------
    // BuildNewStintsByPlayerId's shared reconciliation logic, exercised via
    // RefreshCareerStintsAsync (this class's own writer path) — see
    // WikidataLookupServiceTests/PlayerCareerPrefetchServiceTests for the
    // cross-writer "same outcome regardless of writer path" coverage
    // REQ-1210's fifth criterion requires.

    // The exact reported shape (issue #195, Kelechi Nwakali): an ongoing
    // stint whose EndYear a later fetch reports as concluded, alongside a
    // genuinely new subsequent club in the same response.
    [Test]
    public async Task REQ1210_RefreshCareerStintsAsync_ExistingOngoingStintFetchedWithEndDateAndNewClub_UpdatesOldRowInPlace_InsertsNewClub()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Huesca", StartYear = 2019, EndYear = null, AppearanceCount = 40 }]);

        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Huesca", 2019, 2022, 55),
            new WikidataCareerStintEntry("Ponferradina", 2022, null, 10));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(2),
            "the stale ongoing Huesca row must be updated in place, not left alongside a duplicate new row");
        var huesca = stints.Single(s => s.ClubName == "Huesca");
        Assert.That(huesca.EndYear, Is.EqualTo(2022));
        Assert.That(huesca.AppearanceCount, Is.EqualTo(55));
        Assert.That(stints.Single(s => s.ClubName == "Ponferradina").EndYear, Is.Null,
            "the genuinely new, still-ongoing subsequent club must still be inserted");
    }

    // ADR-0069's Consequences: an outstanding, not-yet-DuplicateCareerStintCleaner
    // -cleaned cross-writer duplicate pair (two rows sharing (ClubName,
    // StartYear), both EndYear: null) must be closed IDENTICALLY on both
    // rows, or the cleaner (which requires exact EndYear equality to merge
    // a pair) could never merge it again.
    [Test]
    public async Task REQ1210_RefreshCareerStintsAsync_TwoExistingRowsShareKeyBothOngoing_BothClosedIdentically()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Huesca", StartYear = 2019, EndYear = null, AppearanceCount = 40 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Huesca", StartYear = 2019, EndYear = null, AppearanceCount = 41 },
        ]);

        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Huesca", 2019, 2022, 55));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(2), "no new row must be inserted — both existing rows are closed in place");
        Assert.That(stints, Has.All.Matches<PlayerCareerStint>(s => s.EndYear == 2022 && s.AppearanceCount == 55),
            "BOTH rows sharing the key must be closed identically, or DuplicateCareerStintCleaner could never merge this pair again");
    }

    // ADR-0069 case 3: deliberately not auto-resolved.
    [Test]
    public async Task REQ1210_RefreshCareerStintsAsync_ExistingNonNullEndYearConflictsWithFetchedValue_LeavesRowUntouched()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Huesca", StartYear = 2019, EndYear = 2020, AppearanceCount = 50 }]);

        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Huesca", 2019, 2021, 60));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1), "an ambiguous conflict must never be resolved by inserting a second row");
        Assert.That(stints[0].EndYear, Is.EqualTo(2020), "an ambiguous conflict must never overwrite the existing row's EndYear");
        Assert.That(stints[0].AppearanceCount, Is.EqualTo(50), "an ambiguous conflict must never overwrite the existing row's AppearanceCount either");
    }

    // ADR-0069 case 1: idempotent re-fetch, unchanged from before this ADR.
    [Test]
    public async Task REQ1210_RefreshCareerStintsAsync_ExactMatchAlreadyStored_StaysNoOp()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 }]);

        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1));
        Assert.That(stints[0].EndYear, Is.EqualTo(2007));
        Assert.That(stints[0].AppearanceCount, Is.EqualTo(254));
    }

    // ADR-0069 case 4: unchanged from before this ADR.
    [Test]
    public async Task REQ1210_RefreshCareerStintsAsync_NoExistingRowSharesKey_InsertsNewStint()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 }]);

        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254),
            new WikidataCareerStintEntry("Barcelona", 2007, 2010, 90));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Arsenal", "Barcelona" }));
    }
}
