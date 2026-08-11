using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Tests;

// Thin test — this class is a thin pass-through over
// IPlayerStoreRepository.GetUnseededClubCandidatesAsync's own tested logic
// (PlayerStoreRepositoryTests.cs), so these tests only assert that RunAsync
// calls through and logs what the repository returned, not the ranking
// logic itself. Real InMemory-backed PlayerStoreRepository, not a mock —
// same "don't over-mock" precedent as PlayerCareerPrefetchServiceTests.
public class ClubGapAuditServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerStoreRepository _playerStoreRepository = null!;
    // S-106 (pure refactor): AddPlayerAsync moved to IPlayerRepository —
    // used only for this file's own seed helper; BuildService's
    // RunAsync target, GetUnseededClubCandidatesAsync, hasn't moved
    // (S-107 territory), so _playerStoreRepository stays the service's
    // own dependency (also still used here for AddCareerStintsAsync,
    // which hasn't moved either).
    private IPlayerRepository _playerRepository = null!;
    private CapturingLogger<ClubGapAuditService> _logger = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerStoreRepository = new PlayerStoreRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _logger = new CapturingLogger<ClubGapAuditService>();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private ClubGapAuditService BuildService() => new(_playerStoreRepository, _logger);

    [Test]
    public async Task RunAsync_UnseededClubExists_LogsClubNameAndPlayerCount()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Someone", WikidataQid = "Q1" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerStoreRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Napoli", StartYear = 2010, EndYear = 2015 }]);

        await BuildService().RunAsync();

        Assert.That(_logger.Messages, Has.Some.Matches<string>(m => m.Contains("Napoli") && m.Contains("1")));
    }

    [Test]
    public async Task RunAsync_NoCandidates_LogsNoGapsFoundMessage()
    {
        await BuildService().RunAsync();

        Assert.That(_logger.Messages, Has.Some.Matches<string>(m => m.Contains("no unseeded club candidates found")));
    }

    [Test]
    public async Task RunAsync_SeededClub_IsNeverLogged()
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
        await _dbContext.SaveChangesAsync();

        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerStoreRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);

        await BuildService().RunAsync();

        Assert.That(_logger.Messages, Has.None.Matches<string>(m => m.Contains("Arsenal")));
    }

    // Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
    // "don't over-mock") — same shape as WikidataClientTests' own
    // CapturingLogger<T>, captures only the formatted message text, which is
    // all this file's tests need to assert against.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
