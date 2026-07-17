using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// S-032 (docs/backlog.md, ADR-0007/REQ-207): the bulk `import-player-name-index`
// CLI verb's paging-loop behavior — WikidataClientTests covers the query
// builder/pagination request shape itself (offset/pageSize sent), this
// covers PlayerNameIndexImporter's own "loop until a page comes back empty"
// contract and its write path into IPlayerNameIndexRepository.
public class PlayerNameIndexImporterTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerNameIndexRepository _repository = null!;
    private FakeWikidataClient _wikidataClient = null!;
    private PlayerNameIndexImporter _importer = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerNameIndexRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
        _importer = new PlayerNameIndexImporter(_wikidataClient, _repository, NullLogger<PlayerNameIndexImporter>.Instance);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    [Test]
    public async Task ImportAsync_StopsAtFirstEmptyPage_NotCallingBeyondIt()
    {
        _wikidataClient.SetPage(0, [new WikidataNameIndexEntry("Q1", "Player One", 1990, "France", null)]);
        _wikidataClient.SetPage(PlayerNameIndexImporter.PageSize, []);
        // A page at 2x PageSize is deliberately configured too, so this test
        // would fail loudly (by returning entries the assertions below don't
        // expect) if the loop kept going past the first empty page instead
        // of stopping there.
        _wikidataClient.SetPage(PlayerNameIndexImporter.PageSize * 2, [new WikidataNameIndexEntry("Q2", "Player Two", 1991, "Spain", null)]);

        var totalUpserted = await _importer.ImportAsync();

        Assert.That(totalUpserted, Is.EqualTo(1));
        Assert.That(_wikidataClient.CallCount, Is.EqualTo(2), "must stop after the first empty page, never call a third time");
        Assert.That(await _dbContext.PlayerNameIndexEntries.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task ImportAsync_FirstPageEmpty_UpsertsNothingAndMakesExactlyOneCall()
    {
        var totalUpserted = await _importer.ImportAsync();

        Assert.That(totalUpserted, Is.EqualTo(0));
        Assert.That(_wikidataClient.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ImportAsync_MultiplePages_UpsertsEntriesFromEachPage()
    {
        _wikidataClient.SetPage(0, [
            new WikidataNameIndexEntry("Q1", "Player One", 1990, "France", null),
            new WikidataNameIndexEntry("Q2", "Player Two", 1991, "Spain", null),
        ]);
        _wikidataClient.SetPage(PlayerNameIndexImporter.PageSize, [
            new WikidataNameIndexEntry("Q3", "Player Three", 1992, "Brazil", null),
        ]);
        _wikidataClient.SetPage(PlayerNameIndexImporter.PageSize * 2, []);

        var totalUpserted = await _importer.ImportAsync();

        Assert.That(totalUpserted, Is.EqualTo(3));
        Assert.That(await _dbContext.PlayerNameIndexEntries.CountAsync(), Is.EqualTo(3));
    }

    [Test]
    public async Task ImportAsync_StoresNormalizedNameAlongsidePrimaryName()
    {
        _wikidataClient.SetPage(0, [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France", null)]);

        await _importer.ImportAsync();

        var stored = await _dbContext.PlayerNameIndexEntries.SingleAsync();
        Assert.That(stored.PrimaryName, Is.EqualTo("Thierry Henry"));
        Assert.That(stored.NormalizedName, Is.EqualTo(PlayerNameNormalizer.Normalize("Thierry Henry")));
    }

    [Test]
    public async Task ImportAsync_SameQidAcrossTwoRuns_ProducesTheSamePlayerId_UpdatingInPlace()
    {
        _wikidataClient.SetPage(0, [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France", null)]);
        await _importer.ImportAsync();
        var firstRunPlayerId = (await _dbContext.PlayerNameIndexEntries.SingleAsync()).PlayerId;

        // A fresh importer/repository instance against the same DbContext —
        // simulates a second, independent CLI invocation of the same verb.
        var secondRunImporter = new PlayerNameIndexImporter(
            _wikidataClient, new PlayerNameIndexRepository(_dbContext), NullLogger<PlayerNameIndexImporter>.Instance);
        await secondRunImporter.ImportAsync();

        var rowCount = await _dbContext.PlayerNameIndexEntries.CountAsync();
        Assert.That(rowCount, Is.EqualTo(1), "re-running the import for the same Wikidata QID must never duplicate the row");
        var secondRunPlayerId = (await _dbContext.PlayerNameIndexEntries.SingleAsync()).PlayerId;
        Assert.That(secondRunPlayerId, Is.EqualTo(firstRunPlayerId), "the same QID must derive the same PlayerId across runs");
    }
}
