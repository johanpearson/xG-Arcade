using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// S-106 (docs/backlog.md, Epic 8, pure refactor): moved out of
// PlayerStoreRepositoryTests.cs, which originally covered every
// IPlayerStoreRepository method before that interface was split into 5
// narrower repositories (S-003's own "no REQ-xxx exists yet for this
// repository's own behavior — foundational plumbing" naming rationale still
// applies unchanged here, hence no REQ-prefixed names). Test bodies/
// assertions are unchanged from their original PlayerStoreRepositoryTests.cs
// form — this is a structural move only.
public class PlayerRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task GetPlayerByWikidataQidAsync_ReturnsMatchingPlayer()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

        var found = await _repository.GetPlayerByWikidataQidAsync("Q1519");

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.FullName, Is.EqualTo("Thierry Henry"));
    }

    [Test]
    public async Task GetPlayerByWikidataQidAsync_ReturnsNull_WhenNoPlayerMatches()
    {
        var found = await _repository.GetPlayerByWikidataQidAsync("Q999999");

        Assert.That(found, Is.Null);
    }

    // ---- S-012: admin data correction (GetPlayerByIdAsync) -----------------

    [Test]
    public async Task GetPlayerByIdAsync_ReturnsMatchingPlayer()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _repository.AddPlayerAsync(player);

        var found = await _repository.GetPlayerByIdAsync(player.Id);

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.FullName, Is.EqualTo("Thierry Henry"));
    }

    [Test]
    public async Task GetPlayerByIdAsync_ReturnsNull_WhenNoPlayerMatches()
    {
        var found = await _repository.GetPlayerByIdAsync(Guid.NewGuid());

        Assert.That(found, Is.Null);
    }

    // ---- Bug-bundle fix (2026-07-27): batched player-persist methods -------
    // (WikidataLookupService.PersistMatchesAsync/PersistCareerStintsAsync's
    // new O(1)-round-trips shape.)

    [Test]
    public async Task GetOrCreatePlayersByWikidataQidAsync_UnknownQids_CreatesOnePlayerPerRequest()
    {
        var requests = new List<PlayerCreationRequest>
        {
            new("Q1519", "Thierry Henry", null),
            new("Q182804", "Nicolas Anelka", "https://example.com/anelka.jpg"),
        };

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(requests);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result["Q1519"].FullName, Is.EqualTo("Thierry Henry"));
        Assert.That(result["Q1519"].PhotoUrl, Is.Null);
        Assert.That(result["Q182804"].FullName, Is.EqualTo("Nicolas Anelka"));
        Assert.That(result["Q182804"].PhotoUrl, Is.EqualTo("https://example.com/anelka.jpg"));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetOrCreatePlayersByWikidataQidAsync_ExistingQid_ReusesExistingPlayer_NeverInserts()
    {
        var existing = await _repository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync([new PlayerCreationRequest("Q1519", "Thierry Henry", null)]);

        Assert.That(result["Q1519"].Id, Is.EqualTo(existing.Id));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task GetOrCreatePlayersByWikidataQidAsync_MixOfExistingAndNewQids_HandlesBothInOneCall()
    {
        var existing = await _repository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync([
            new PlayerCreationRequest("Q1519", "Thierry Henry", null),
            new PlayerCreationRequest("Q182804", "Nicolas Anelka", null),
        ]);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result["Q1519"].Id, Is.EqualTo(existing.Id));
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetOrCreatePlayersByWikidataQidAsync_EmptyRequestList_ReturnsEmptyDictionary_DoesNotThrow()
    {
        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync([]);

        Assert.That(result, Is.Empty);
    }

    // ---- REQ-1207/S-082: Position/BirthYear, set once at Player creation --

    [Test]
    public async Task REQ1207_GetOrCreatePlayersByWikidataQidAsync_UnknownQid_SetsPositionAndBirthYearFromTheRequest()
    {
        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest("Q1519", "Thierry Henry", null, "forward", 1977)]);

        Assert.That(result["Q1519"].Position, Is.EqualTo("forward"));
        Assert.That(result["Q1519"].BirthYear, Is.EqualTo(1977));
    }

    [Test]
    public async Task REQ1207_GetOrCreatePlayersByWikidataQidAsync_UnknownQid_NoPositionOrBirthYearGiven_BothAreNull()
    {
        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest("Q1519", "Thierry Henry", null)]);

        Assert.That(result["Q1519"].Position, Is.Null);
        Assert.That(result["Q1519"].BirthYear, Is.Null);
    }

    [Test]
    public async Task REQ1207_GetOrCreatePlayersByWikidataQidAsync_ExistingQid_LeavesItsPositionAndBirthYearCompletelyUntouched_EvenWhenTheNewRequestDisagrees()
    {
        // Set-once contract, direct at the repository level (this method
        // never touches an existing Player row at all — see its own
        // "if (result.ContainsKey(...)) continue;" comment) — a second call
        // for the same QID carrying different Position/BirthYear values must
        // have zero effect on the already-persisted row.
        var existing = await _repository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519", Position = "forward", BirthYear = 1977 });

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest("Q1519", "Thierry Henry", null, "midfielder", 1980)]);

        Assert.That(result["Q1519"].Id, Is.EqualTo(existing.Id));
        Assert.That(result["Q1519"].Position, Is.EqualTo("forward"), "the new request's 'midfielder' must never overwrite the existing row");
        Assert.That(result["Q1519"].BirthYear, Is.EqualTo(1977), "the new request's 1980 must never overwrite the existing row");
    }

    [Test]
    public async Task REQ1207_GetOrCreatePlayersByWikidataQidAsync_ExistingQidWithNullPositionAndBirthYear_LaterRequestWithRealValues_LeavesThemNull()
    {
        // The set-once rule applies regardless of whether the existing row's
        // CURRENT value is null or already set (REQ-1207's own text) — not
        // just the "overwriting a real value" case above.
        var existing = await _repository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest("Q1519", "Thierry Henry", null, "forward", 1977)]);

        Assert.That(result["Q1519"].Id, Is.EqualTo(existing.Id));
        Assert.That(result["Q1519"].Position, Is.Null, "a null already on the existing row is never backfilled by a later request");
        Assert.That(result["Q1519"].BirthYear, Is.Null, "a null already on the existing row is never backfilled by a later request");
    }
}
