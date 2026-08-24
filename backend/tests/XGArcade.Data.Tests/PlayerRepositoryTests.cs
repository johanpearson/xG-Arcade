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
//
// S-129: GetOrCreatePlayersByWikidataQidAsync's DbUpdateException/unique-
// violation catch (the concurrent-insert recovery, matched on
// IX_Players_WikidataQid and Npgsql's PostgresException) is deliberately
// NOT covered here, same reason and same precedent as UserRepositoryTests
// .cs's own comment on UserRepository.AddAsync's identical catch shape: the
// InMemory provider used by every test in this project does not enforce
// unique indexes at all, so seeding two Players with the same WikidataQid
// simply succeeds (SaveChangesAsync never throws), making the catch branch
// impossible to exercise here in a way that could actually fail against
// wrong code. Unlike UserRepository.AddAsync's precedent (manually verified
// against a real local Postgres 16 instance when its migration was
// authored, S-017), this branch has NOT been manually verified against a
// real Postgres instance — no Docker daemon/local Postgres was available in
// the sandbox this fix was built in (see NOTES.md). Flagged for manual
// verification against real Postgres before this is treated as fully
// confirmed; would also need a real-Postgres-backed integration test tier
// to cover automatically, which does not exist yet in this project.
public class PlayerRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerRepository _repository = null!;
    // Captured so REQ-513's UpdatePlayerAsync tests below can open a SECOND,
    // independent DbContext against the same EF Core InMemory store (keyed
    // by name, shared per-process) to verify a mutation was genuinely
    // persisted via SaveChangesAsync, rather than just still sitting on the
    // same tracked in-memory instance _dbContext already holds.
    private string _inMemoryDatabaseName = null!;

    [SetUp]
    public void SetUp()
    {
        _inMemoryDatabaseName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: _inMemoryDatabaseName)
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
        Assert.That(result["Q1519"].Player.FullName, Is.EqualTo("Thierry Henry"));
        Assert.That(result["Q1519"].Player.PhotoUrl, Is.Null);
        Assert.That(result["Q1519"].WasCreated, Is.True, "S-129: no Player row existed for this WikidataQid before this call");
        Assert.That(result["Q182804"].Player.FullName, Is.EqualTo("Nicolas Anelka"));
        Assert.That(result["Q182804"].Player.PhotoUrl, Is.EqualTo("https://example.com/anelka.jpg"));
        Assert.That(result["Q182804"].WasCreated, Is.True);
        Assert.That(await _dbContext.Players.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task GetOrCreatePlayersByWikidataQidAsync_ExistingQid_ReusesExistingPlayer_NeverInserts()
    {
        var existing = await _repository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" });

        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync([new PlayerCreationRequest("Q1519", "Thierry Henry", null)]);

        Assert.That(result["Q1519"].Player.Id, Is.EqualTo(existing.Id));
        Assert.That(result["Q1519"].WasCreated, Is.False, "S-129: this WikidataQid already had a Player row");
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
        Assert.That(result["Q1519"].Player.Id, Is.EqualTo(existing.Id));
        Assert.That(result["Q1519"].WasCreated, Is.False, "S-129: the existing QID must be reported as reused, not created");
        Assert.That(result["Q182804"].WasCreated, Is.True, "S-129: the genuinely new QID must be reported as created");
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

        Assert.That(result["Q1519"].Player.Position, Is.EqualTo("forward"));
        Assert.That(result["Q1519"].Player.BirthYear, Is.EqualTo(1977));
    }

    [Test]
    public async Task REQ1207_GetOrCreatePlayersByWikidataQidAsync_UnknownQid_NoPositionOrBirthYearGiven_BothAreNull()
    {
        var result = await _repository.GetOrCreatePlayersByWikidataQidAsync(
            [new PlayerCreationRequest("Q1519", "Thierry Henry", null)]);

        Assert.That(result["Q1519"].Player.Position, Is.Null);
        Assert.That(result["Q1519"].Player.BirthYear, Is.Null);
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

        Assert.That(result["Q1519"].Player.Id, Is.EqualTo(existing.Id));
        Assert.That(result["Q1519"].WasCreated, Is.False);
        Assert.That(result["Q1519"].Player.Position, Is.EqualTo("forward"), "the new request's 'midfielder' must never overwrite the existing row");
        Assert.That(result["Q1519"].Player.BirthYear, Is.EqualTo(1977), "the new request's 1980 must never overwrite the existing row");
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

        Assert.That(result["Q1519"].Player.Id, Is.EqualTo(existing.Id));
        Assert.That(result["Q1519"].WasCreated, Is.False);
        Assert.That(result["Q1519"].Player.Position, Is.Null, "a null already on the existing row is never backfilled by a later request");
        Assert.That(result["Q1519"].Player.BirthYear, Is.Null, "a null already on the existing row is never backfilled by a later request");
    }

    // ---- REQ-513 (GitHub issue #239): admin refresh from Wikidata ----------
    // GetPlayerForRefreshAsync/UpdatePlayerAsync — see IPlayerRepository's own
    // doc comment for why this pair is a deliberate, narrow, TRACKED
    // exception to this class's otherwise-uniform AsNoTracking read pattern.

    [Test]
    public async Task GetPlayerForRefreshAsync_ReturnsMatchingPlayer()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Clarence Seedorf", WikidataQid = "Q188207" };
        await _repository.AddPlayerAsync(player);

        var found = await _repository.GetPlayerForRefreshAsync(player.Id);

        Assert.That(found, Is.Not.Null);
        Assert.That(found!.FullName, Is.EqualTo("Clarence Seedorf"));
    }

    [Test]
    public async Task GetPlayerForRefreshAsync_ReturnsNull_WhenNoPlayerMatches()
    {
        var found = await _repository.GetPlayerForRefreshAsync(Guid.NewGuid());

        Assert.That(found, Is.Null);
    }

    [Test]
    public async Task GetPlayerForRefreshAsync_ReturnsATrackedEntity_SoMutatingItInPlaceCanLaterBeSaved()
    {
        // AddPlayerAsync's own SaveChangesAsync already detaches nothing, but
        // this proves the SPECIFIC contract AdminEndpoints' refresh action
        // relies on: the entity instance returned by GetPlayerForRefreshAsync
        // is tracked by _dbContext (unlike every AsNoTracking read elsewhere
        // in this repository), so mutating its properties directly and later
        // calling SaveChangesAsync (via UpdatePlayerAsync) persists those
        // mutations without a separate Update()/Attach() call in between.
        var player = new Player { Id = Guid.NewGuid(), FullName = "Clarence Seedorf", WikidataQid = "Q188207" };
        await _repository.AddPlayerAsync(player);

        var tracked = await _repository.GetPlayerForRefreshAsync(player.Id);

        Assert.That(_dbContext.Entry(tracked!).State, Is.Not.EqualTo(EntityState.Detached));
    }

    [Test]
    public async Task UpdatePlayerAsync_PersistsMutationsMadeToTheEntityReturnedByGetPlayerForRefreshAsync()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Clarnce Seedorf", WikidataQid = "Q188207" };
        await _repository.AddPlayerAsync(player);
        var tracked = await _repository.GetPlayerForRefreshAsync(player.Id);
        tracked!.FullName = "Clarence Seedorf";

        await _repository.UpdatePlayerAsync(tracked);

        // A fresh, independent DbContext against the same InMemory store —
        // not just re-reading _dbContext's own identity-mapped instance —
        // proves the write actually went through SaveChangesAsync, the same
        // "second context, same database name" pattern
        // AdminSuggestionEndpointTests.cs's API-level tests use via a fresh
        // scope.
        await using var verifyContext = new XGArcadeDbContext(
            new DbContextOptionsBuilder<XGArcadeDbContext>().UseInMemoryDatabase(_inMemoryDatabaseName).Options);
        var persisted = await verifyContext.Players.AsNoTracking().SingleAsync(p => p.Id == player.Id);
        Assert.That(persisted.FullName, Is.EqualTo("Clarence Seedorf"));
    }
}
