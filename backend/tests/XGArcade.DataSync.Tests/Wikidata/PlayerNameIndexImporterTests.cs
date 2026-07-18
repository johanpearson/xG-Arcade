using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// S-032 (docs/backlog.md, ADR-0007/REQ-207), revised with the 2026-07-18
// birth-year-slicing fix: WikidataClientTests covers the slice query's
// shape/parsing, this covers PlayerNameIndexImporter's own iteration
// (1939 → current year), the empty-year-vs-failed-slice distinction, the
// retry-then-fail-the-run-loudly contract (the original loop-until-empty-page
// design read a swallowed WDQS timeout as end-of-data and exited 0 having
// imported nothing), and its write path into IPlayerNameIndexRepository.
public class PlayerNameIndexImporterTests
{
    // Pins the importer's year-range upper bound: slices run 1939..2026
    // inclusive under this clock, regardless of the real wall clock.
    private static readonly DateTimeOffset FixedNow = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
    private const int CurrentYear = 2026;

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
        _importer = BuildImporter(_wikidataClient, _repository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // retryBackoff: zero so retry tests don't sleep out real backoff delays.
    private static PlayerNameIndexImporter BuildImporter(
        FakeWikidataClient client, IPlayerNameIndexRepository repository) =>
        new(client, repository, NullLogger<PlayerNameIndexImporter>.Instance,
            timeProvider: new FixedTimeProvider(FixedNow), retryBackoff: TimeSpan.Zero);

    // Minimal hand-rolled TimeProvider fake, same pattern as
    // XGArcade.Core.Tests.Rounds.FixedTimeProvider (that one is internal to
    // its own test project, so this is a sibling, not a duplicate to unify).
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Test]
    public async Task ImportAsync_QueriesEveryBirthYearFromPoolFloorThroughCurrentYear_ExactlyOnceEach()
    {
        await _importer.ImportAsync();

        var expectedYears = Enumerable.Range(
            WikidataClient.FirstEligibleBirthYear,
            CurrentYear - WikidataClient.FirstEligibleBirthYear + 1);
        Assert.That(_wikidataClient.QueriedYears, Is.EqualTo(expectedYears),
            "every birth year from ADR-0025's 1939 floor through the clock's current year must be sliced exactly once, in order");
    }

    [Test]
    public async Task ImportAsync_EmptyYear_ContinuesToLaterYears_NotTreatedAsEndOfDataOrFailure()
    {
        // 1939..1989 and 1991..1999 all return [] (sparse years) — the run
        // must keep going and still import the later, populated years. Under
        // the old loop-until-empty-page design an empty result terminated
        // the whole import; an empty year must never do that.
        _wikidataClient.SetYear(1990, [new WikidataNameIndexEntry("Q1", "Player One", 1990, "France")]);
        _wikidataClient.SetYear(2000, [new WikidataNameIndexEntry("Q2", "Player Two", 2000, "Spain")]);

        var totalUpserted = await _importer.ImportAsync();

        Assert.That(totalUpserted, Is.EqualTo(2));
        Assert.That(await _dbContext.PlayerNameIndexEntries.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task ImportAsync_NoYearHasAnyPlayers_ReturnsZeroWithoutThrowing()
    {
        var totalUpserted = await _importer.ImportAsync();

        Assert.That(totalUpserted, Is.EqualTo(0));
        Assert.That(await _dbContext.PlayerNameIndexEntries.CountAsync(), Is.EqualTo(0));
    }

    [Test]
    public async Task ImportAsync_SliceFailsOnceThenSucceeds_RetriesAndImportsIt_WithoutFailingTheRun()
    {
        _wikidataClient.FailFor(1990, attempts: 1);
        _wikidataClient.SetYear(1990, [new WikidataNameIndexEntry("Q1", "Player One", 1990, "France")]);

        var totalUpserted = await _importer.ImportAsync();

        Assert.That(totalUpserted, Is.EqualTo(1));
        Assert.That(_wikidataClient.CallCountFor(1990), Is.EqualTo(2), "one failed attempt plus one successful retry");
        Assert.That(await _dbContext.PlayerNameIndexEntries.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public void ImportAsync_SliceFailsAllAttempts_ThrowsAfterFinishingRemainingYears_KeepingSuccessfulSlices()
    {
        // The second half of the 2026-07-18 bug: a slice that keeps failing
        // must fail the CLI run (nonzero exit, red workflow), never silently
        // produce a partial import behind an exit-0 — but only AFTER the
        // remaining years have run, so a re-run only has to redo the failed
        // year, not everything after it.
        _wikidataClient.FailFor(1990, attempts: int.MaxValue);
        _wikidataClient.SetYear(1991, [new WikidataNameIndexEntry("Q2", "Player Two", 1991, "Spain")]);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await _importer.ImportAsync());

        Assert.That(ex!.Message, Does.Contain("1990"), "the failure must name the failed birth year");
        Assert.That(_wikidataClient.CallCountFor(1990), Is.EqualTo(PlayerNameIndexImporter.MaxAttemptsPerYear));
        Assert.That(_wikidataClient.CallCountFor(1991), Is.EqualTo(1),
            "years after a failed slice must still run before the run fails");
        Assert.That(_dbContext.PlayerNameIndexEntries.Count(), Is.EqualTo(1),
            "successful slices stay upserted even when the run fails — the job is idempotent and re-run");
    }

    [Test]
    public async Task ImportAsync_SameQidInTwoBirthYearSlices_UpsertsOneRowViaDeterministicPlayerId()
    {
        // A player with two P569 statements in different years legitimately
        // appears in both years' slices — the deterministic QID-derived
        // PlayerId makes the second slice update the first's row in place.
        _wikidataClient.SetYear(1976, [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1976, "France")]);
        _wikidataClient.SetYear(1977, [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);

        await _importer.ImportAsync();

        var stored = await _dbContext.PlayerNameIndexEntries.SingleAsync();
        Assert.That(stored.PrimaryName, Is.EqualTo("Thierry Henry"));
        Assert.That(stored.BirthYear, Is.EqualTo(1977), "the later slice's upsert corrects the row in place");
    }

    [Test]
    public async Task ImportAsync_StoresNormalizedNameAlongsidePrimaryName()
    {
        _wikidataClient.SetYear(1977, [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);

        await _importer.ImportAsync();

        var stored = await _dbContext.PlayerNameIndexEntries.SingleAsync();
        Assert.That(stored.PrimaryName, Is.EqualTo("Thierry Henry"));
        Assert.That(stored.NormalizedName, Is.EqualTo(PlayerNameNormalizer.Normalize("Thierry Henry")));
    }

    [Test]
    public async Task ImportAsync_SameQidAcrossTwoRuns_ProducesTheSamePlayerId_UpdatingInPlace()
    {
        _wikidataClient.SetYear(1977, [new WikidataNameIndexEntry("Q1519", "Thierry Henry", 1977, "France")]);
        await _importer.ImportAsync();
        var firstRunPlayerId = (await _dbContext.PlayerNameIndexEntries.SingleAsync()).PlayerId;

        // A fresh importer/repository instance against the same DbContext —
        // simulates a second, independent CLI invocation of the same verb.
        var secondRunImporter = BuildImporter(_wikidataClient, new PlayerNameIndexRepository(_dbContext));
        await secondRunImporter.ImportAsync();

        var rowCount = await _dbContext.PlayerNameIndexEntries.CountAsync();
        Assert.That(rowCount, Is.EqualTo(1), "re-running the import for the same Wikidata QID must never duplicate the row");
        var secondRunPlayerId = (await _dbContext.PlayerNameIndexEntries.SingleAsync()).PlayerId;
        Assert.That(secondRunPlayerId, Is.EqualTo(firstRunPlayerId), "the same QID must derive the same PlayerId across runs");
    }

    // Distinct from the slice-fetch failure path above: a *write* failure
    // (e.g. a real Postgres outage under UpsertManyAsync) is not part of the
    // retry-per-slice machinery and must propagate immediately — this CLI
    // job should fail loudly so the GitHub Actions run is visibly red, not
    // quietly report a partial success.
    [Test]
    public void ImportAsync_RepositoryUpsertThrows_PropagatesException_NotSwallowed()
    {
        _wikidataClient.SetYear(1990, [new WikidataNameIndexEntry("Q1", "Player One", 1990, "France")]);
        var importer = BuildImporter(_wikidataClient, new ThrowingPlayerNameIndexRepository());

        Assert.ThrowsAsync<InvalidOperationException>(async () => await importer.ImportAsync());
    }

    // Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
    // "don't over-mock") — only used by the test above, so SearchByPrefixAsync
    // is left unimplemented rather than speculatively fleshed out.
    private sealed class ThrowingPlayerNameIndexRepository : IPlayerNameIndexRepository
    {
        public Task<IReadOnlyList<PlayerNameIndex>> SearchByPrefixAsync(
            string normalizedQuery, int limit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("not exercised by ImportAsync_RepositoryUpsertThrows_PropagatesException_NotSwallowed");

        public Task UpsertManyAsync(IEnumerable<PlayerNameIndex> entries, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated DB write failure");
    }
}
