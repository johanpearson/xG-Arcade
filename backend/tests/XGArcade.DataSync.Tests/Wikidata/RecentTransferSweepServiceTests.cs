using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// S-188 (docs/backlog.md, Epic 26 — Supabase free-tier egress remediation):
// same real-InMemory-repository-plus-FakeWikidataClient pattern as
// PlayerCareerPrefetchServiceTests/PlayerCareerStintRefreshServiceTests
// (docs/coding-guidelines.md "don't over-mock").
//
// S-189: _playerAttributeRepository/_playerDataRepository/
// _playerDataQualityRepository are new here — real InMemory-backed
// repositories, same "don't over-mock" pattern as every other repository
// in this fixture, needed now that PersistClubTransfersAsync also writes
// PlayerAttribute/PlayerData and invalidates ConfirmedLowMatchPair/
// PairLookupFailure rows.
public class RecentTransferSweepServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerDataRepository _playerDataRepository = null!;
    private IPlayerDataQualityRepository _playerDataQualityRepository = null!;
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
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
        _playerDataRepository = new PlayerDataRepository(_dbContext);
        _playerDataQualityRepository = new PlayerDataQualityRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private RecentTransferSweepService BuildService() =>
        new(_categoryValueRepository, _playerCareerStintRepository, _playerRepository,
            _playerAttributeRepository, _playerDataRepository, _playerDataQualityRepository, _wikidataClient,
            NullLogger<RecentTransferSweepService>.Instance);

    private async Task<ClubDefinition> SeedClubAsync(string name, string wikidataQid)
    {
        var club = new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid };
        await _categoryValueRepository.AddClubAsync(club);
        return club;
    }

    private async Task<Player> SeedPlayerAsync(string wikidataQid, string fullName) =>
        await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = fullName, WikidataQid = wikidataQid });

    [Test]
    public void REQ110_SweepAsync_NonPositiveLookbackDays_ThrowsArgumentOutOfRangeException()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BuildService().SweepAsync(0));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BuildService().SweepAsync(-1));
    }

    [Test]
    public async Task REQ110_SweepAsync_ClubWithNoWikidataQid_IsSkippedEntirely()
    {
        await _categoryValueRepository.AddClubAsync(new ClubDefinition { Id = Guid.NewGuid(), Name = "Unresolved FC", WikidataQid = null });

        var result = await BuildService().SweepAsync(30);

        Assert.That(result.ClubsProcessed, Is.EqualTo(0));
        Assert.That(result.ClubsFailed, Is.EqualTo(0));
        Assert.That(_wikidataClient.QueriedRecentTransferClubQids, Is.Empty, "a club with no resolved QID must never trigger a Wikidata call");
    }

    // Arrival: a player never seen by this codebase before must be
    // get-or-created (same GetOrCreatePlayersByWikidataQidAsync pattern
    // PlayerCareerPrefetchService.FetchAndPersistBatchAsync uses) and get a
    // NEW PlayerCareerStint row.
    [Test]
    public async Task REQ110_SweepAsync_ArrivalForBrandNewPlayer_CreatesPlayerAndInsertsStint()
    {
        var club = await SeedClubAsync("Arsenal", "Q9617");
        _wikidataClient.SetRecentClubTransfers("Q9617", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club.Name, 2026, null, null, club.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));

        var result = await BuildService().SweepAsync(30);

        Assert.That(result.ClubsProcessed, Is.EqualTo(1));
        Assert.That(result.PlayersTouched, Is.EqualTo(1));
        Assert.That(result.StintsAdded, Is.EqualTo(1));
        Assert.That(result.StintsCompleted, Is.EqualTo(0));

        var player = await _playerRepository.GetPlayerByWikidataQidAsync("Q1519");
        Assert.That(player, Is.Not.Null, "a player never seen by this codebase before must still get a Player row");
        Assert.That(player!.FullName, Is.EqualTo("Thierry Henry"));

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1));
        Assert.That(stints[0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(stints[0].StartYear, Is.EqualTo(2026));
    }

    // Departure: an existing stint whose EndYear was previously unknown gets
    // COMPLETED in place via CareerStintReconciler (through
    // PlayerCareerStintRefreshService.BuildNewStintsByPlayerId), never
    // duplicated — the exact ADR-0091 machinery, reused, not reimplemented.
    [Test]
    public async Task REQ110_SweepAsync_DepartureCompletingExistingOngoingStint_UpdatesInPlace_NeverDuplicates()
    {
        var club = await SeedClubAsync("Arsenal", "Q9617");
        var player = await SeedPlayerAsync("Q1519", "Thierry Henry");
        var existingStintId = Guid.NewGuid();
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = existingStintId, PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = null, AppearanceCount = null }]);

        _wikidataClient.SetRecentClubTransfers("Q9617", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club.Name, 1999, 2007, 254, club.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));

        var result = await BuildService().SweepAsync(30);

        Assert.That(result.StintsAdded, Is.EqualTo(0), "a completion is not a new stint");
        Assert.That(result.StintsCompleted, Is.EqualTo(1));

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1), "a departure completing an existing row must update it in place, not duplicate it");
        Assert.That(stints[0].Id, Is.EqualTo(existingStintId), "the SAME row must be updated, not a new one");
        Assert.That(stints[0].EndYear, Is.EqualTo(2007));
        Assert.That(stints[0].AppearanceCount, Is.EqualTo(254));
    }

    [Test]
    public async Task REQ110_SweepAsync_FetchedStintIdenticalToStored_RemainsANoOp()
    {
        var club = await SeedClubAsync("Arsenal", "Q9617");
        var player = await SeedPlayerAsync("Q1519", "Thierry Henry");
        var existingStintId = Guid.NewGuid();
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = existingStintId, PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 }]);

        _wikidataClient.SetRecentClubTransfers("Q9617", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club.Name, 1999, 2007, 254, club.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));

        var result = await BuildService().SweepAsync(30);

        Assert.That(result.StintsAdded, Is.EqualTo(0));
        Assert.That(result.StintsCompleted, Is.EqualTo(0));

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1));
        Assert.That(stints[0].Id, Is.EqualTo(existingStintId));
    }

    // Cutoff threading: SweepAsync must compute sinceUtc as (now - lookbackDays)
    // and pass it through to IWikidataClient.QueryRecentClubTransfersAsync
    // unchanged, per seeded club.
    [Test]
    public async Task REQ110_SweepAsync_ThreadsLookbackDaysAsCutoffDate_ToEveryClub()
    {
        await SeedClubAsync("Arsenal", "Q9617");
        await SeedClubAsync("Barcelona", "Q7156");
        var before = DateTime.UtcNow.AddDays(-14);

        await BuildService().SweepAsync(14);

        var after = DateTime.UtcNow.AddDays(-14);
        Assert.That(_wikidataClient.QueriedRecentTransferClubQids, Is.EquivalentTo(new[] { "Q9617", "Q7156" }));
        foreach (var sinceUtc in _wikidataClient.QueriedRecentTransferSinceUtc)
            Assert.That(sinceUtc, Is.InRange(before, after), "cutoff must be ~14 days before now, per lookbackDays");
    }

    [Test]
    public async Task REQ110_SweepAsync_PassesEachClubsOwnNameAndQid_ToTheWikidataClient()
    {
        var club = await SeedClubAsync("Arsenal", "Q9617");

        await BuildService().SweepAsync(30);

        Assert.That(_wikidataClient.QueriedRecentTransferClubQids, Is.EqualTo(new[] { club.WikidataQid }));
        Assert.That(_wikidataClient.QueriedRecentTransferClubNames, Is.EqualTo(new[] { club.Name }));
    }

    [Test]
    public async Task REQ110_SweepAsync_OneClubFails_ContinuesWithRemainingClubs_ThenThrowsAtTheEnd()
    {
        // Both clubs are configured with data for the SAME player QID —
        // GetClubsAsync's own row order is unspecified (no ORDER BY), so
        // this test must not depend on which of the two clubs happens to be
        // processed (and thus fail) first; either way, exactly one succeeds
        // and persists Q1519.
        var club1 = await SeedClubAsync("Arsenal", "Q9617");
        var club2 = await SeedClubAsync("Barcelona", "Q7156");
        _wikidataClient.SetRecentClubTransfers("Q9617", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club1.Name, 2026, null, null, club1.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));
        _wikidataClient.SetRecentClubTransfers("Q7156", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club2.Name, 2026, null, null, club2.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));
        _wikidataClient.FailNextRecentTransferCalls(1);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => BuildService().SweepAsync(30));

        Assert.That(ex!.Message, Does.Contain("1 club(s) failed"));
        Assert.That(_wikidataClient.QueriedRecentTransferClubQids, Is.EquivalentTo(new[] { "Q9617", "Q7156" }),
            "a failed club must not stop the remaining clubs from being attempted");

        var player = await _playerRepository.GetPlayerByWikidataQidAsync("Q1519");
        Assert.That(player, Is.Not.Null, "whatever succeeded before the failure must still be persisted");
    }

    [Test]
    public async Task REQ110_SweepAsync_NoClubsHaveTransfers_ReturnsZeroedResult_NoWikidataCallsWasted()
    {
        await SeedClubAsync("Arsenal", "Q9617");

        var result = await BuildService().SweepAsync(30);

        Assert.That(result.ClubsProcessed, Is.EqualTo(1));
        Assert.That(result.ClubsFailed, Is.EqualTo(0));
        Assert.That(result.PlayersTouched, Is.EqualTo(0));
        Assert.That(result.StintsAdded, Is.EqualTo(0));
        Assert.That(result.StintsCompleted, Is.EqualTo(0));
    }

    // S-188's own deliberate scope boundary: this service must NEVER touch
    // PlayerPoolSweptAt — writing it here would incorrectly tell ADR-0088's
    // skip-forever check that this club's FULL pool was re-verified, when
    // only a narrow recent-activity slice actually was.
    [Test]
    public async Task REQ110_SweepAsync_NeverTouchesPlayerPoolSweptAt()
    {
        var club = await SeedClubAsync("Arsenal", "Q9617");
        _wikidataClient.SetRecentClubTransfers("Q9617", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club.Name, 2026, null, null, club.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));

        await BuildService().SweepAsync(30);

        var clubs = await _categoryValueRepository.GetClubsAsync();
        Assert.That(clubs.Single(c => c.WikidataQid == "Q9617").PlayerPoolSweptAt, Is.Null);
    }

    // ---- S-189/ADR-0093: arrivals also write PlayerAttribute/PlayerData ----

    [Test]
    public async Task REQ110_SweepAsync_ArrivalForNewPlayer_AlsoWritesPlayerAttributeAndPairedPlayerData()
    {
        var club = await SeedClubAsync("Arsenal", "Q9617");
        _wikidataClient.SetRecentClubTransfers("Q9617", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club.Name, 2026, null, null, club.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));

        var result = await BuildService().SweepAsync(30);

        // S-188's own existing behavior must still hold unchanged.
        Assert.That(result.StintsAdded, Is.EqualTo(1));
        // S-189's new behavior.
        Assert.That(result.AttributesAdded, Is.EqualTo(1));

        var player = await _playerRepository.GetPlayerByWikidataQidAsync("Q1519");
        var attributes = await _playerAttributeRepository.GetPlayerAttributesAsync("club", "Arsenal");
        Assert.That(attributes.Select(a => a.PlayerId), Is.EquivalentTo(new[] { player!.Id }));

        var playerData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id && d.Field == "club").ToListAsync();
        Assert.That(playerData, Has.Count.EqualTo(1));
        Assert.That(playerData[0].Value, Is.EqualTo("Arsenal"));
        Assert.That(playerData[0].Source, Is.EqualTo("wikidata"));
        Assert.That(playerData[0].Confidence, Is.EqualTo("verified"));
    }

    [Test]
    public async Task REQ110_SweepAsync_ArrivalForPlayerAlreadyHavingTheClubAttribute_DoesNotWriteDuplicateAttribute()
    {
        var club = await SeedClubAsync("Arsenal", "Q9617");
        var player = await SeedPlayerAsync("Q1519", "Thierry Henry");
        // Simulates the attribute already being known from an earlier
        // prefetch-player-careers run, with no matching PlayerCareerStint
        // row yet for THIS (later) spell — a genuinely new stint for an
        // already-attributed club.
        await _playerAttributeRepository.AddPlayerAttributeAsync(
            new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });

        _wikidataClient.SetRecentClubTransfers("Q9617", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club.Name, 2026, null, null, club.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));

        var result = await BuildService().SweepAsync(30);

        Assert.That(result.StintsAdded, Is.EqualTo(1), "the new stint itself must still be inserted");
        Assert.That(result.AttributesAdded, Is.EqualTo(0), "the already-existing attribute must not be counted as newly added");

        var attributes = await _playerAttributeRepository.GetPlayerAttributesAsync("club", "Arsenal");
        Assert.That(attributes, Has.Count.EqualTo(1), "the already-existing attribute must not be duplicated");

        var playerData = await _dbContext.PlayerData.Where(d => d.PlayerId == player.Id && d.Field == "club").ToListAsync();
        Assert.That(playerData, Is.Empty, "no new PlayerData row is written when the paired PlayerAttribute is a duplicate");
    }

    // A departure alone (no accompanying arrival in the same run) must
    // never write OR remove a PlayerAttribute row — Grid's "ever played for
    // this club" answer semantics mean a player who left is still correctly
    // a valid answer forever, so a departure has nothing to do here.
    [Test]
    public async Task REQ110_SweepAsync_DepartureCompletingExistingStint_NeverWritesOrRemovesPlayerAttribute()
    {
        var club = await SeedClubAsync("Arsenal", "Q9617");
        var player = await SeedPlayerAsync("Q1519", "Thierry Henry");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = null, AppearanceCount = null }]);

        _wikidataClient.SetRecentClubTransfers("Q9617", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club.Name, 1999, 2007, 254, club.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));

        var result = await BuildService().SweepAsync(30);

        Assert.That(result.StintsCompleted, Is.EqualTo(1));
        Assert.That(result.AttributesAdded, Is.EqualTo(0));

        var attributes = await _dbContext.PlayerAttributes.Where(a => a.PlayerId == player.Id).ToListAsync();
        Assert.That(attributes, Is.Empty, "a departure alone must never create a PlayerAttribute row");
    }

    // ---- S-189/ADR-0093: targeted ConfirmedLowMatchPair/PairLookupFailure invalidation ----

    // A new (club, "Arsenal") attribute for a player who already has
    // nationality "France" must clear the STALE (nationality=France) x
    // (club=Arsenal) marker in both tables, while leaving pairs that don't
    // involve both this player's OTHER attribute AND this club untouched —
    // a different club paired with the same nationality, and a different
    // nationality paired with the same club, must both survive.
    [Test]
    public async Task REQ110_SweepAsync_NewArrivalAttribute_ClearsStaleMatchPairsForPlayersOtherAttributes_LeavesUnrelatedPairsUntouched()
    {
        var club = await SeedClubAsync("Arsenal", "Q9617");
        var player = await SeedPlayerAsync("Q1519", "Thierry Henry");
        await _playerAttributeRepository.AddPlayerAttributeAsync(
            new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });

        // The pair this new arrival's attribute must invalidate.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        await _playerDataQualityRepository.RecordTechnicalFailureAsync("nationality", "France", "club", "Arsenal");

        // Unrelated pairs that must survive: same nationality, different
        // club; same club, different (unheld) nationality.
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "France", "club", "Barcelona", matchCount: 2);
        await _playerDataQualityRepository.RecordConfirmedLowAsync("nationality", "Spain", "club", "Arsenal", matchCount: 3);

        _wikidataClient.SetRecentClubTransfers("Q9617", new RecentClubTransferLookupResult(
            new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
            {
                ["Q1519"] = [new WikidataCareerStintEntry(club.Name, 2026, null, null, club.WikidataQid)],
            },
            new Dictionary<string, string> { ["Q1519"] = "Thierry Henry" }));

        var result = await BuildService().SweepAsync(30);

        Assert.That(result.AttributesAdded, Is.EqualTo(1));

        Assert.That(
            await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"),
            Is.False, "the stale confirmed-low marker for the exact pair this arrival affected must be cleared");
        Assert.That(
            await _playerDataQualityRepository.IsPersistentTechnicalFailureAsync("nationality", "France", "club", "Arsenal", threshold: 1),
            Is.False, "the stale technical-failure marker for the exact pair this arrival affected must be cleared");

        Assert.That(
            await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "France", "club", "Barcelona"),
            Is.True, "a different club paired with the same nationality must be untouched");
        Assert.That(
            await _playerDataQualityRepository.IsConfirmedLowAsync("nationality", "Spain", "club", "Arsenal"),
            Is.True, "a different nationality (not held by this player) paired with the same club must be untouched");
    }
}
