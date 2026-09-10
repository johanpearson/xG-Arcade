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
    private IPlayerAttributeRepository _playerAttributeRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerBackfillRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAttributeRepository = new PlayerAttributeRepository(_dbContext);
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

    // ---- GetPlayersMissingInternationalStatsAsync ----
    // REQ-1501/REQ-1506 (xG Higher/Lower, S-231): PlayerInternationalStatsBackfillService's
    // read cursor — mirrors GetPlayersMissingPhotoAsync's own coverage
    // above, adapted for the "presence of an 'international-caps'
    // PlayerAttribute row" missing-signal (see that method's own doc
    // comment on IPlayerBackfillRepository for why caps alone, not goals).

    [Test]
    public async Task REQ1501_GetPlayersMissingInternationalStatsAsync_ReturnsOnlyPlayersWithQidAndNoCapsRow()
    {
        var missingStats = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var alreadyHasStats = new Player { Id = Guid.NewGuid(), FullName = "Didier Drogba", WikidataQid = "Q42233" };
        var noQid = new Player { Id = Guid.NewGuid(), FullName = "No QID Player" };
        await _playerRepository.AddPlayerAsync(missingStats);
        await _playerRepository.AddPlayerAsync(alreadyHasStats);
        await _playerRepository.AddPlayerAsync(noQid);
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = alreadyHasStats.Id, AttributeType = "international-caps", AttributeValue = "50",
        });

        var result = await _repository.GetPlayersMissingInternationalStatsAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { missingStats.Id }));
    }

    [Test]
    public async Task REQ1501_GetPlayersMissingInternationalStatsAsync_OtherAttributeTypesDoNotCountAsAlreadyProcessed()
    {
        // A player with "trophy"/"club" rows but no "international-caps" row
        // yet must still surface as a backfill candidate.
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = player.Id, AttributeType = "trophy", AttributeValue = "Premier League",
        });

        var result = await _repository.GetPlayersMissingInternationalStatsAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { player.Id }));
    }

    [Test]
    public async Task REQ1501_GetPlayersMissingInternationalStatsAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 5; i++)
            await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = $"Player {i}", WikidataQid = $"Q{i}" });

        var result = await _repository.GetPlayersMissingInternationalStatsAsync([], batchSize: 3);

        Assert.That(result, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task REQ1501_GetPlayersMissingInternationalStatsAsync_ExcludesGivenPlayerIds()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _playerRepository.AddPlayerAsync(first);
        await _playerRepository.AddPlayerAsync(second);

        var result = await _repository.GetPlayersMissingInternationalStatsAsync([first.Id], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { second.Id }));
    }

    [Test]
    public async Task REQ1501_GetPlayersMissingInternationalStatsAsync_NoMissingStatsPlayers_ReturnsEmpty()
    {
        var result = await _repository.GetPlayersMissingInternationalStatsAsync([], batchSize: 200);

        Assert.That(result, Is.Empty);
    }

    // Bug fix (2026-09-10, follow-up to S-231/PR #367): the fix under
    // test — a player with the "international-stats-checked" PlayerData
    // marker (written by PlayerInternationalStatsRefreshService for every
    // player in a successfully-queried batch, even when nothing resolved)
    // must be excluded here just like one with a real "international-caps"
    // row, or the backfill re-queries Wikidata's "no data" population
    // forever. See IPlayerBackfillRepository's own doc comment on this
    // method for the full "why both signals" reasoning.
    [Test]
    public async Task REQ1501_GetPlayersMissingInternationalStatsAsync_ExcludesPlayersWithCheckedMarker_EvenWithoutACapsRow()
    {
        var neverChecked = new Player { Id = Guid.NewGuid(), FullName = "Never Checked", WikidataQid = "Q1519" };
        var checkedNoData = new Player { Id = Guid.NewGuid(), FullName = "Checked, No Data", WikidataQid = "Q42233" };
        await _playerRepository.AddPlayerAsync(neverChecked);
        await _playerRepository.AddPlayerAsync(checkedNoData);
        _dbContext.PlayerData.Add(new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = checkedNoData.Id,
            Field = "international-stats-checked",
            Value = "checked",
            Source = "wikidata",
            Confidence = "verified",
            SyncedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetPlayersMissingInternationalStatsAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { neverChecked.Id }));
    }

    // Backward compatibility: a player who already has a real
    // "international-caps" row from BEFORE this fix shipped (so no
    // matching marker exists) must stay excluded too — otherwise every
    // already-resolved player in production would look "missing" again the
    // moment this fix ships, and get needlessly re-queried.
    [Test]
    public async Task REQ1501_GetPlayersMissingInternationalStatsAsync_ExcludesPlayersWithCapsRow_EvenWithoutACheckedMarker()
    {
        var alreadyHasStats = new Player { Id = Guid.NewGuid(), FullName = "Pre-Fix Resolved Player", WikidataQid = "Q42233" };
        await _playerRepository.AddPlayerAsync(alreadyHasStats);
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = alreadyHasStats.Id, AttributeType = "international-caps", AttributeValue = "50",
        });

        var result = await _repository.GetPlayersMissingInternationalStatsAsync([], batchSize: 200);

        Assert.That(result, Is.Empty);
    }

    // ---- GetPlayersMissingTrophyStatsAsync ----
    // REQ-1501 (xG Higher/Lower, S-232, ADR-0113): PlayerTrophyStatsBackfillService's
    // read cursor — mirrors GetPlayersMissingInternationalStatsAsync's own
    // coverage above, adapted for the "presence of ANY 'trophy'
    // PlayerAttribute row" missing-signal (a player can hold zero-to-N
    // trophies, unlike caps' at-most-one-row shape — see that method's own
    // doc comment on IPlayerBackfillRepository for the full "why an EXISTS
    // check either way" reasoning).

    [Test]
    public async Task REQ1501_GetPlayersMissingTrophyStatsAsync_ReturnsOnlyPlayersWithQidAndNoTrophyRow()
    {
        var missingStats = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var alreadyHasStats = new Player { Id = Guid.NewGuid(), FullName = "Didier Drogba", WikidataQid = "Q42233" };
        var noQid = new Player { Id = Guid.NewGuid(), FullName = "No QID Player" };
        await _playerRepository.AddPlayerAsync(missingStats);
        await _playerRepository.AddPlayerAsync(alreadyHasStats);
        await _playerRepository.AddPlayerAsync(noQid);
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = alreadyHasStats.Id, AttributeType = "trophy", AttributeValue = "Ballon d'Or",
        });

        var result = await _repository.GetPlayersMissingTrophyStatsAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { missingStats.Id }));
    }

    [Test]
    public async Task REQ1501_GetPlayersMissingTrophyStatsAsync_OtherAttributeTypesDoNotCountAsAlreadyProcessed()
    {
        // A player with "international-caps"/"club" rows but no "trophy" row
        // yet must still surface as a backfill candidate.
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = player.Id, AttributeType = "international-caps", AttributeValue = "50",
        });

        var result = await _repository.GetPlayersMissingTrophyStatsAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { player.Id }));
    }

    [Test]
    public async Task REQ1501_GetPlayersMissingTrophyStatsAsync_RespectsBatchSize()
    {
        for (var i = 0; i < 5; i++)
            await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = $"Player {i}", WikidataQid = $"Q{i}" });

        var result = await _repository.GetPlayersMissingTrophyStatsAsync([], batchSize: 3);

        Assert.That(result, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task REQ1501_GetPlayersMissingTrophyStatsAsync_ExcludesGivenPlayerIds()
    {
        var first = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QA" };
        var second = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QB" };
        await _playerRepository.AddPlayerAsync(first);
        await _playerRepository.AddPlayerAsync(second);

        var result = await _repository.GetPlayersMissingTrophyStatsAsync([first.Id], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { second.Id }));
    }

    [Test]
    public async Task REQ1501_GetPlayersMissingTrophyStatsAsync_NoMissingStatsPlayers_ReturnsEmpty()
    {
        var result = await _repository.GetPlayersMissingTrophyStatsAsync([], batchSize: 200);

        Assert.That(result, Is.Empty);
    }

    // ADR-0113's own reason for existing — built in from the first commit,
    // not retrofitted after a production incident the way
    // GetPlayersMissingInternationalStatsAsync's own marker check had to be
    // (see that method's own comment above for the full incident this ADR
    // exists to avoid repeating).
    [Test]
    public async Task REQ1501_GetPlayersMissingTrophyStatsAsync_ExcludesPlayersWithCheckedMarker_EvenWithoutATrophyRow()
    {
        var neverChecked = new Player { Id = Guid.NewGuid(), FullName = "Never Checked", WikidataQid = "Q1519" };
        var checkedNoData = new Player { Id = Guid.NewGuid(), FullName = "Checked, No Data", WikidataQid = "Q42233" };
        await _playerRepository.AddPlayerAsync(neverChecked);
        await _playerRepository.AddPlayerAsync(checkedNoData);
        _dbContext.PlayerData.Add(new PlayerData
        {
            Id = Guid.NewGuid(),
            PlayerId = checkedNoData.Id,
            Field = PlayerData.TrophyStatsCheckedField,
            Value = PlayerData.TrophyStatsCheckedValue,
            Source = "wikidata",
            Confidence = "verified",
            SyncedAt = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _repository.GetPlayersMissingTrophyStatsAsync([], batchSize: 200);

        Assert.That(result.Select(p => p.Id), Is.EquivalentTo(new[] { neverChecked.Id }));
    }

    // Symmetry with the ~20 players whose "trophy" row already exists from
    // WikidataLookupService's pre-existing byproduct path, which predates
    // this marker entirely and has no corresponding marker row — they must
    // stay excluded too, or every already-resolved player would look
    // "missing" again the moment this backfill first runs.
    [Test]
    public async Task REQ1501_GetPlayersMissingTrophyStatsAsync_ExcludesPlayersWithTrophyRow_EvenWithoutACheckedMarker()
    {
        var alreadyHasStats = new Player { Id = Guid.NewGuid(), FullName = "Byproduct-Path Player", WikidataQid = "Q42233" };
        await _playerRepository.AddPlayerAsync(alreadyHasStats);
        await _playerAttributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = alreadyHasStats.Id, AttributeType = "trophy", AttributeValue = "Ballon d'Or",
        });

        var result = await _repository.GetPlayersMissingTrophyStatsAsync([], batchSize: 200);

        Assert.That(result, Is.Empty);
    }
}
