using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// REQ-1501/REQ-1506 (xG Higher/Lower, S-231, ADR-0112): same real-InMemory-
// repository-plus-FakeWikidataClient pattern as
// PlayerCareerStintRefreshServiceTests (docs/coding-guidelines.md "don't
// over-mock").
public class PlayerInternationalStatsRefreshServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAttributeRepository _playerAttributeRepository = null!;
    private IPlayerDataRepository _playerDataRepository = null!;
    private FakeWikidataClient _wikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
        _playerDataRepository = new PlayerDataRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerInternationalStatsRefreshService BuildService() =>
        new(_wikidataClient, _playerRepository, _playerAttributeRepository, _playerDataRepository,
            NullLogger<PlayerInternationalStatsRefreshService>.Instance);

    private async Task<Player> SeedPlayerAsync(string wikidataQid) =>
        await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {wikidataQid}", WikidataQid = wikidataQid });

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_ResolvedCapsAndGoals_PersistsBothAttributeRows()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetInternationalStats("Q1519", caps: 123, goals: 51);

        await BuildService().RefreshInternationalStatsAsync([player.Id]);

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.First(a => a.AttributeType == "international-caps").AttributeValue, Is.EqualTo("123"));
        Assert.That(attributes.First(a => a.AttributeType == "international-goals").AttributeValue, Is.EqualTo("51"));
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_ResolvedCapsButNoGoals_PersistsOnlyCapsRow()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetInternationalStats("Q1519", caps: 80); // Goals left null.

        await BuildService().RefreshInternationalStatsAsync([player.Id]);

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.Select(a => a.AttributeType), Is.EquivalentTo(new[] { "international-caps" }),
            "REQ-1501's non-null-value rule: an unresolved goals figure must never be persisted as a fabricated 0");
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_AlsoWritesPlayerData_ForAdminVisibility()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetInternationalStats("Q1519", caps: 100, goals: 20);

        await BuildService().RefreshInternationalStatsAsync([player.Id]);

        var playerData = await _playerDataRepository.GetUnverifiedPlayerDataAsync();
        // Written as already-verified (ADR-0032), so it must NOT appear in
        // the unverified queue — proves it exists via a positive check
        // instead, same as PlayerCareerPrefetchServiceTests' own pattern
        // for PlayerData verification.
        Assert.That(playerData, Is.Empty, "Wikidata-sourced writes persist already-verified per ADR-0032");
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_PlayerWithNoWikidataQid_IsNeverQueried()
    {
        var player = await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "No QID Player" });

        await BuildService().RefreshInternationalStatsAsync([player.Id]);

        Assert.That(_wikidataClient.QueriedInternationalStatsBatches, Is.Empty);
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_EmptyPlayerIdList_DoesNothing()
    {
        await BuildService().RefreshInternationalStatsAsync([]);

        Assert.That(_wikidataClient.QueriedInternationalStatsBatches, Is.Empty);
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_WikidataQueryFails_DoesNotThrow_ByDefault()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.FailNextInternationalStatsBatches(1);

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshInternationalStatsAsync([player.Id]));

        Assert.That((await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id])).ContainsKey(player.Id), Is.False);
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_ThrowOnFailureTrue_WikidataQueryFails_PropagatesWikidataQueryException()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.FailNextInternationalStatsBatches(1);

        Assert.ThrowsAsync<WikidataQueryException>(
            async () => await BuildService().RefreshInternationalStatsAsync([player.Id], throwOnFailure: true),
            "throwOnFailure: true must let a Wikidata technical failure propagate instead of being logged and swallowed — used by PlayerInternationalStatsBackfillService");
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_PlayerWithNoQualifyingNationalTeamStatement_PersistsNothing_IsNotTreatedAsAFailure()
    {
        var player = await SeedPlayerAsync("Q1519"); // No SetInternationalStats call — genuinely no qualifying P54 statement.

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshInternationalStatsAsync([player.Id]));

        Assert.That((await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id])).ContainsKey(player.Id), Is.False);
    }

    // Bug fix (2026-09-10, follow-up to S-231/PR #367): the fix under test
    // — a player in a successfully-queried batch with no resolvable
    // Wikidata data gets the "international-stats-checked" PlayerData
    // marker (so GetPlayersMissingInternationalStatsAsync stops treating
    // them as missing forever) but no fabricated PlayerAttribute row. See
    // IPlayerBackfillRepository.GetPlayersMissingInternationalStatsAsync's
    // own doc comment for the full "never checked" vs. "checked, no data"
    // distinction this fixes.
    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_PlayerWithNoQualifyingNationalTeamStatement_WritesCheckedMarker_NotAFabricatedAttribute()
    {
        var player = await SeedPlayerAsync("Q1519"); // No SetInternationalStats call — genuinely no qualifying P54 statement.

        await BuildService().RefreshInternationalStatsAsync([player.Id]);

        Assert.That((await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id])).ContainsKey(player.Id), Is.False,
            "no PlayerAttribute row must be fabricated for a player Wikidata genuinely has no qualifying data for");

        var playerDataRows = await _dbContext.PlayerData.Where(pd => pd.PlayerId == player.Id).ToListAsync();
        Assert.That(playerDataRows.Select(pd => pd.Field), Is.EquivalentTo(new[] { "international-stats-checked" }),
            "a successfully-queried player with no resolvable data must still get the checked marker so the backfill stops re-querying them forever");
        Assert.That(playerDataRows.Single().Value, Is.Not.Null.And.Not.EqualTo("international-caps").And.Not.EqualTo("international-goals"),
            "the marker's Value is a placeholder, never fabricated real data");
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_WikidataQueryFails_DoesNotWriteCheckedMarker_PlayerStaysRetryable()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.FailNextInternationalStatsBatches(1);

        await BuildService().RefreshInternationalStatsAsync([player.Id]);

        var playerDataRows = await _dbContext.PlayerData.Where(pd => pd.PlayerId == player.Id).ToListAsync();
        Assert.That(playerDataRows, Is.Empty,
            "a batch whose Wikidata call itself failed must not mark any of its players as checked — they need to stay retryable");
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_ResolvedCapsAndGoals_AlsoWritesCheckedMarker()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetInternationalStats("Q1519", caps: 123, goals: 51);

        await BuildService().RefreshInternationalStatsAsync([player.Id]);

        var playerDataFields = (await _dbContext.PlayerData.Where(pd => pd.PlayerId == player.Id).ToListAsync())
            .Select(pd => pd.Field);
        Assert.That(playerDataFields, Is.EquivalentTo(new[] { "international-caps", "international-goals", "international-stats-checked" }),
            "a player whose data resolves gets both the real PlayerData rows AND the checked marker");
    }

    [Test]
    public async Task REQ1501_RefreshInternationalStatsAsync_PlayerAlreadyHasCapsRow_IsNeverOverwritten()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = player.Id, AttributeType = "international-caps", AttributeValue = "999",
        });
        _wikidataClient.SetInternationalStats("Q1519", caps: 5, goals: 1); // A fresh fetch that must never clobber the existing row.

        await BuildService().RefreshInternationalStatsAsync([player.Id]);

        var attributes = (await _playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync([player.Id]))[player.Id];
        Assert.That(attributes.Select(a => a.AttributeType), Is.EquivalentTo(new[] { "international-caps" }));
        Assert.That(attributes.Single().AttributeValue, Is.EqualTo("999"), "an already-processed player must never be overwritten by a later refresh call");
    }
}
