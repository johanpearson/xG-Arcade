using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// REQ-214 (S-045) / REQ-1207 (bug-bundle fix, 2026-08-02): Player's own
// photo/position/birth-year backfill cursors. Split out of
// PlayerStoreRepositoryTests.cs (S-107, docs/backlog.md Epic 8, pure
// refactor — see ADR-0067 for the full split) — test bodies/assertions are
// unchanged from their original PlayerStoreRepositoryTests.cs form, this is
// a structural move only.
// _playerRepository below is only used to seed/assert fixtures —
// AddPlayerAsync/GetPlayerByIdAsync themselves are covered directly in
// PlayerRepositoryTests.cs.
public class PlayerBackfillRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerBackfillRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerBackfillRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ---- GetPlayersMissingPhotoAsync / UpdatePlayerPhotosAsync -------------
    // REQ-214 backfill (S-045): PlayerPhotoBackfillService's read/write pair.

    [Test]
    public async Task REQ214_GetPlayersMissingPhotoAsync_ReturnsOnlyPlayersWithQidAndNoPhoto()
    {
        var missingPhoto = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var alreadyHasPhoto = new Player { Id = Guid.NewGuid(), FullName = "Didier Drogba", WikidataQid = "Q42233", PhotoUrl = "https://example.com/drogba.jpg" };
        var noQid = new Player { Id = Guid.NewGuid(), FullName = "No QID Player" };
        await _playerRepository.AddPlayerAsync(missingPhoto);
        await _playerRepository.AddPlayerAsync(alreadyHasPhoto);
        await _playerRepository.AddPlayerAsync(noQid);

        var result = await _repository.GetPlayersMissingPhotoAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { missingPhoto.Id }));
    }

    [Test]
    public async Task REQ214_GetPlayersMissingPhotoAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 5; i++)
            await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = $"Player {i}", WikidataQid = $"Q{i}" });

        var result = await _repository.GetPlayersMissingPhotoAsync([], batchSize: 3);

        Assert.That(result, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task REQ214_GetPlayersMissingPhotoAsync_ExcludesGivenPlayerIds()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _playerRepository.AddPlayerAsync(first);
        await _playerRepository.AddPlayerAsync(second);

        var result = await _repository.GetPlayersMissingPhotoAsync([first.Id], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { second.Id }));
    }

    [Test]
    public async Task REQ214_GetPlayersMissingPhotoAsync_NoMissingPhotoPlayers_ReturnsEmpty()
    {
        var result = await _repository.GetPlayersMissingPhotoAsync([], batchSize: 200);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task REQ214_UpdatePlayerPhotosAsync_SetsPhotoUrl_ForEveryGivenPlayer()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _playerRepository.AddPlayerAsync(first);
        await _playerRepository.AddPlayerAsync(second);

        await _repository.UpdatePlayerPhotosAsync(new Dictionary<Guid, string>
        {
            [first.Id] = "https://example.com/a.jpg",
            [second.Id] = "https://example.com/b.jpg",
        });

        Assert.That((await _playerRepository.GetPlayerByIdAsync(first.Id))!.PhotoUrl, Is.EqualTo("https://example.com/a.jpg"));
        Assert.That((await _playerRepository.GetPlayerByIdAsync(second.Id))!.PhotoUrl, Is.EqualTo("https://example.com/b.jpg"));
    }

    [Test]
    public async Task REQ214_UpdatePlayerPhotosAsync_EmptyDictionary_DoesNothing()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.UpdatePlayerPhotosAsync(new Dictionary<Guid, string>());

        Assert.That((await _playerRepository.GetPlayerByIdAsync(player.Id))!.PhotoUrl, Is.Null);
    }

    [Test]
    public async Task REQ214_UpdatePlayerPhotosAsync_UnknownPlayerId_IsSilentlySkipped()
    {
        // Best-effort backfill of already-cached data, not a
        // correctness-critical write — a player deleted between the read
        // and this write (e.g. by purge-player-pool) must not fail the
        // whole batch.
        Assert.DoesNotThrowAsync(() => _repository.UpdatePlayerPhotosAsync(new Dictionary<Guid, string>
        {
            [Guid.NewGuid()] = "https://example.com/unknown.jpg",
        }));
    }

    // ---- GetPlayersMissingPositionOrBirthYearAsync / UpdatePlayerPositionsAndBirthYearsAsync ----
    // REQ-1207 backfill (bug-bundle fix, 2026-08-02): PlayerPositionBirthYearBackfillService's
    // read/write pair — mirrors GetPlayersMissingPhotoAsync/UpdatePlayerPhotosAsync's
    // own coverage above, adapted for the two-field "either is missing" shape.

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_ReturnsPlayersMissingEitherField()
    {
        var missingBoth = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var missingPositionOnly = new Player { Id = Guid.NewGuid(), FullName = "Didier Drogba", WikidataQid = "Q42233", BirthYear = 1978 };
        var missingBirthYearOnly = new Player { Id = Guid.NewGuid(), FullName = "Kaká", WikidataQid = "Q11571", Position = "midfielder" };
        var hasBoth = new Player { Id = Guid.NewGuid(), FullName = "Pelé", WikidataQid = "Q80956", Position = "forward", BirthYear = 1940 };
        var noQid = new Player { Id = Guid.NewGuid(), FullName = "No QID Player" };
        await _playerRepository.AddPlayerAsync(missingBoth);
        await _playerRepository.AddPlayerAsync(missingPositionOnly);
        await _playerRepository.AddPlayerAsync(missingBirthYearOnly);
        await _playerRepository.AddPlayerAsync(hasBoth);
        await _playerRepository.AddPlayerAsync(noQid);

        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { missingBoth.Id, missingPositionOnly.Id, missingBirthYearOnly.Id }));
    }

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_IncludesPlayersWithRawWikidataUriPosition()
    {
        // Bug fix (2026-08-10, bug-bundle): rows created before the
        // 2026-08-02 WikidataClient fix hold the raw P413 entity URI as
        // Position, not a resolved label. Position is NOT NULL on these
        // rows, so they must still surface as backfill candidates or they're
        // permanently skipped.
        var rawUriPosition = new Player
        {
            Id = Guid.NewGuid(),
            FullName = "Raw URI Player",
            WikidataQid = "Q8025128",
            Position = "http://www.wikidata.org/entity/Q8025128",
            BirthYear = 1990,
        };
        var resolvedPosition = new Player
        {
            Id = Guid.NewGuid(),
            FullName = "Resolved Position Player",
            WikidataQid = "Q42233",
            Position = "midfielder",
            BirthYear = 1978,
        };
        await _playerRepository.AddPlayerAsync(rawUriPosition);
        await _playerRepository.AddPlayerAsync(resolvedPosition);

        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { rawUriPosition.Id }));
    }

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 5; i++)
            await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = $"Player {i}", WikidataQid = $"Q{i}" });

        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([], batchSize: 3);

        Assert.That(result, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_ExcludesGivenPlayerIds()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _playerRepository.AddPlayerAsync(first);
        await _playerRepository.AddPlayerAsync(second);

        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([first.Id], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { second.Id }));
    }

    [Test]
    public async Task REQ1207_GetPlayersMissingPositionOrBirthYearAsync_NoMissingFieldPlayers_ReturnsEmpty()
    {
        var result = await _repository.GetPlayersMissingPositionOrBirthYearAsync([], batchSize: 200);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_SetsBothFields_ForEveryGivenPlayer()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _playerRepository.AddPlayerAsync(first);
        await _playerRepository.AddPlayerAsync(second);

        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [first.Id] = new PlayerPositionBirthYearUpdate("forward", 1990),
            [second.Id] = new PlayerPositionBirthYearUpdate("goalkeeper", 1985),
        });

        var reloadedFirst = await _playerRepository.GetPlayerByIdAsync(first.Id);
        Assert.That(reloadedFirst!.Position, Is.EqualTo("forward"));
        Assert.That(reloadedFirst.BirthYear, Is.EqualTo(1990));
        var reloadedSecond = await _playerRepository.GetPlayerByIdAsync(second.Id);
        Assert.That(reloadedSecond!.Position, Is.EqualTo("goalkeeper"));
        Assert.That(reloadedSecond.BirthYear, Is.EqualTo(1985));
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_NullFieldOnUpdate_LeavesThatFieldUntouched()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA", BirthYear = 1990 };
        await _playerRepository.AddPlayerAsync(player);

        // Position resolved this run, BirthYear didn't (null means "no
        // update," never "clear the existing value") — the already-set
        // BirthYear must survive unchanged.
        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [player.Id] = new PlayerPositionBirthYearUpdate("forward", null),
        });

        var reloaded = await _playerRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("forward"));
        Assert.That(reloaded.BirthYear, Is.EqualTo(1990));
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_AlreadySetField_IsNeverOverwritten()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA", Position = "defender" };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [player.Id] = new PlayerPositionBirthYearUpdate("forward", 1990),
        });

        var reloaded = await _playerRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("defender"), "REQ-1207's set-once contract must hold for this backfill too");
        Assert.That(reloaded.BirthYear, Is.EqualTo(1990));
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_RawWikidataUriPosition_IsOverwrittenWithResolvedLabel()
    {
        // Bug fix (2026-08-10, bug-bundle): the raw-URI shape is the one
        // deliberate exception to the "already-set field is never
        // overwritten" rule above — otherwise widening the read-side
        // candidate query would be a no-op.
        var player = new Player
        {
            Id = Guid.NewGuid(),
            FullName = "Raw URI Player",
            WikidataQid = "Q8025128",
            Position = "http://www.wikidata.org/entity/Q8025128",
        };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [player.Id] = new PlayerPositionBirthYearUpdate("midfielder", 1990),
        });

        var reloaded = await _playerRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.EqualTo("midfielder"));
        Assert.That(reloaded.BirthYear, Is.EqualTo(1990));
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_EmptyDictionary_DoesNothing()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>());

        var reloaded = await _playerRepository.GetPlayerByIdAsync(player.Id);
        Assert.That(reloaded!.Position, Is.Null);
        Assert.That(reloaded.BirthYear, Is.Null);
    }

    [Test]
    public async Task REQ1207_UpdatePlayerPositionsAndBirthYearsAsync_UnknownPlayerId_IsSilentlySkipped()
    {
        // Best-effort backfill of already-cached data, not a
        // correctness-critical write — a player deleted between the read
        // and this write (e.g. by purge-player-pool) must not fail the
        // whole batch.
        Assert.DoesNotThrowAsync(() => _repository.UpdatePlayerPositionsAndBirthYearsAsync(new Dictionary<Guid, PlayerPositionBirthYearUpdate>
        {
            [Guid.NewGuid()] = new PlayerPositionBirthYearUpdate("forward", 1990),
        }));
    }
}
