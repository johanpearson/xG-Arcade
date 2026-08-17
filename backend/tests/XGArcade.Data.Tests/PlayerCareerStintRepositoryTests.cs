using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// ADR-0042/S-079: PlayerCareerStint (xG Path's own read/write path) plus
// REQ-1201's narrowing candidate query. Split out of
// PlayerStoreRepositoryTests.cs (S-107, docs/backlog.md Epic 8, pure
// refactor — see ADR-0067 for the full split) — test bodies/assertions are
// unchanged from their original PlayerStoreRepositoryTests.cs form, this is
// a structural move only.
// _playerRepository below is only used to seed fixtures — AddPlayerAsync
// itself is covered directly in PlayerRepositoryTests.cs.
public class PlayerCareerStintRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerCareerStintRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerCareerStintRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ---- ADR-0042/S-079: PlayerCareerStint (GetCareerStintsAsync/AddCareerStintsAsync) ----

    [Test]
    public async Task AddCareerStintsAsync_ThenGetCareerStintsAsync_ReturnsAddedStints()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.AddCareerStintsAsync(player.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 },
        ]);

        var stints = await _repository.GetCareerStintsAsync(player.Id);

        Assert.That(stints, Has.Count.EqualTo(1));
        Assert.That(stints[0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(stints[0].SequenceOrder, Is.EqualTo(0));
    }

    [Test]
    public async Task AddCareerStintsAsync_ResequencesExistingStints_WhenNewStintIsChronologicallyEarlier()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _repository.AddCareerStintsAsync(player.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Barcelona", StartYear = 2010, EndYear = 2015 },
        ]);

        await _repository.AddCareerStintsAsync(player.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 },
        ]);

        var stints = (await _repository.GetCareerStintsAsync(player.Id)).OrderBy(s => s.SequenceOrder).ToList();

        Assert.That(stints, Has.Count.EqualTo(2));
        Assert.That(stints[0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(stints[0].SequenceOrder, Is.EqualTo(0));
        Assert.That(stints[1].ClubName, Is.EqualTo("Barcelona"));
        Assert.That(stints[1].SequenceOrder, Is.EqualTo(1),
            "the pre-existing Barcelona row must be re-sequenced to make room for the chronologically earlier Arsenal stint");
    }

    [Test]
    public async Task AddCareerStintsAsync_OngoingStint_SortsLastAmongStintsSharingTheSameStartYear()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        await _repository.AddCareerStintsAsync(player.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Loan Club", StartYear = 2020, EndYear = 2021 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Parent Club", StartYear = 2020, EndYear = null },
        ]);

        var stints = (await _repository.GetCareerStintsAsync(player.Id)).OrderBy(s => s.SequenceOrder).ToList();

        Assert.That(stints[0].ClubName, Is.EqualTo("Loan Club"));
        Assert.That(stints[1].ClubName, Is.EqualTo("Parent Club"), "an ongoing (null EndYear) stint sorts last among stints sharing the same StartYear");
    }

    [Test]
    public void AddCareerStintsAsync_EmptyNewStintsList_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repository.AddCareerStintsAsync(Guid.NewGuid(), []));
    }

    [Test]
    public async Task GetCareerStintsAsync_ReturnsEmpty_WhenPlayerHasNoStints()
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);

        var stints = await _repository.GetCareerStintsAsync(player.Id);

        Assert.That(stints, Is.Empty);
    }

    // ---- Bug-bundle fix (2026-07-27): batched career-stint methods ---------

    [Test]
    public async Task GetCareerStintsByPlayerIdsAsync_ReturnsOnlyRequestedPlayersStints_GroupedByPlayerId()
    {
        var playerA = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QplayerA" };
        var playerB = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QplayerB" };
        await _playerRepository.AddPlayerAsync(playerA);
        await _playerRepository.AddPlayerAsync(playerB);
        await _repository.AddCareerStintsAsync(playerA.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 },
        ]);
        await _repository.AddCareerStintsAsync(playerB.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Barcelona", StartYear = 2004, EndYear = 2014 },
        ]);

        var result = await _repository.GetCareerStintsByPlayerIdsAsync([playerA.Id]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[playerA.Id], Has.Count.EqualTo(1));
        Assert.That(result[playerA.Id][0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(result.ContainsKey(playerB.Id), Is.False, "a player not requested must be absent from the result");
    }

    [Test]
    public async Task GetCareerStintsByPlayerIdsAsync_EmptyIdList_ReturnsEmptyDictionary()
    {
        var result = await _repository.GetCareerStintsByPlayerIdsAsync([]);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task AddCareerStintsBatchAsync_PersistsStintsForMultiplePlayers_InOneCall()
    {
        var playerA = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QplayerA" };
        var playerB = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QplayerB" };
        await _playerRepository.AddPlayerAsync(playerA);
        await _playerRepository.AddPlayerAsync(playerB);

        await _repository.AddCareerStintsBatchAsync(new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>
        {
            [playerA.Id] = [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }],
            [playerB.Id] = [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Barcelona", StartYear = 2004, EndYear = 2014 }],
        });

        var stintsA = await _repository.GetCareerStintsAsync(playerA.Id);
        var stintsB = await _repository.GetCareerStintsAsync(playerB.Id);
        Assert.That(stintsA, Has.Count.EqualTo(1));
        Assert.That(stintsA[0].SequenceOrder, Is.EqualTo(0));
        Assert.That(stintsB, Has.Count.EqualTo(1));
        Assert.That(stintsB[0].SequenceOrder, Is.EqualTo(0));
    }

    [Test]
    public async Task AddCareerStintsBatchAsync_ResequencesEachPlayersExistingStints_Independently()
    {
        // Same re-sequencing rule as AddCareerStintsAsync's own test above,
        // proven here across two DIFFERENT players in the SAME batch call —
        // one player's chronologically-earlier new stint must never affect
        // another player's own SequenceOrder.
        var playerA = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "QplayerA" };
        var playerB = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "QplayerB" };
        await _playerRepository.AddPlayerAsync(playerA);
        await _playerRepository.AddPlayerAsync(playerB);
        await _repository.AddCareerStintsAsync(playerA.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Barcelona", StartYear = 2010, EndYear = 2015 },
        ]);
        await _repository.AddCareerStintsAsync(playerB.Id, [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Chelsea", StartYear = 2005, EndYear = 2010 },
        ]);

        await _repository.AddCareerStintsBatchAsync(new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>
        {
            // Chronologically earlier than playerA's existing Barcelona stint.
            [playerA.Id] = [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }],
            // playerB gets no new stints in this batch call.
            [playerB.Id] = [],
        });

        var stintsA = (await _repository.GetCareerStintsAsync(playerA.Id)).OrderBy(s => s.SequenceOrder).ToList();
        var stintsB = await _repository.GetCareerStintsAsync(playerB.Id);
        Assert.That(stintsA, Has.Count.EqualTo(2));
        Assert.That(stintsA[0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(stintsA[1].ClubName, Is.EqualTo("Barcelona"), "playerA's pre-existing stint must be re-sequenced");
        Assert.That(stintsB, Has.Count.EqualTo(1));
        Assert.That(stintsB[0].SequenceOrder, Is.EqualTo(0), "playerB's own stint must be untouched by playerA's re-sequencing");
    }

    [Test]
    public void AddCareerStintsBatchAsync_EmptyDictionary_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repository.AddCareerStintsBatchAsync(new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>()));
    }

    [Test]
    public void AddCareerStintsBatchAsync_EveryEntryHasEmptyNewStintsList_DoesNotThrow()
    {
        Assert.DoesNotThrowAsync(() => _repository.AddCareerStintsBatchAsync(new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>
        {
            [Guid.NewGuid()] = [],
        }));
    }

    // ---- REQ-1201/ADR-0074/S-138 perf fix (GetCareerStintCandidatePlayerIdsAsync) --
    // Same "narrow read" testing shape as
    // PlayerDataQualityRepositoryTests.GetUnseededClubCandidatesAsync tests,
    // but proving the narrower "at least minSeededClubCount DISTINCT seeded
    // club names" superset filter this hot path relies on (distinct club
    // NAMES, not stint rows — see the method's own doc comment), rather
    // than the diagnostic method's own case-insensitive club-name grouping.

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_ExcludesPlayersWithFewerThanMinSeededClubCount()
    {
        var seededClubNames = new HashSet<string> { "Seeded FC", "Seeded FC 2" };
        var tooFew = new Player { Id = Guid.NewGuid(), FullName = "Too Few", WikidataQid = "Q1" };
        await _playerRepository.AddPlayerAsync(tooFew);
        await _repository.AddCareerStintsAsync(tooFew.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = tooFew.Id, ClubName = "Seeded FC", StartYear = 2010, EndYear = 2013 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = tooFew.Id, ClubName = "Other FC", StartYear = 2013, EndYear = null },
        ]);

        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minSeededClubCount: 2);

        Assert.That(candidateIds, Does.Not.Contain(tooFew.Id));
    }

    // REQ-1201/ADR-0074/S-138: repeat stints at the SAME seeded club (a
    // loan, then a later permanent return) must only count once toward
    // minSeededClubCount — this narrowing pass groups by distinct ClubName,
    // not by row, so a player with many rows at one seeded club and none at
    // any other seeded club must still be excluded here.
    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_ExcludesPlayerWithMultipleStintsAtOnlyOneSeededClub()
    {
        var seededClubNames = new HashSet<string> { "Seeded FC", "Seeded FC 2" };
        var sameClubTwice = new Player { Id = Guid.NewGuid(), FullName = "Same Club Twice", WikidataQid = "Q1" };
        await _playerRepository.AddPlayerAsync(sameClubTwice);
        await _repository.AddCareerStintsAsync(sameClubTwice.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = sameClubTwice.Id, ClubName = "Seeded FC", StartYear = 2010, EndYear = 2012 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = sameClubTwice.Id, ClubName = "Other FC", StartYear = 2012, EndYear = 2014 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = sameClubTwice.Id, ClubName = "Seeded FC", StartYear = 2014, EndYear = null },
        ]);

        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minSeededClubCount: 2);

        Assert.That(candidateIds, Does.Not.Contain(sameClubTwice.Id),
            "two stint rows at the same seeded club must count as ONE distinct club, not two");
    }

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_ExcludesPlayersWithNoStintAtSeededClub()
    {
        var seededClubNames = new HashSet<string> { "Seeded FC", "Seeded FC 2" };
        var noSeededClub = new Player { Id = Guid.NewGuid(), FullName = "No Seeded Club", WikidataQid = "Q1" };
        await _playerRepository.AddPlayerAsync(noSeededClub);
        await _repository.AddCareerStintsAsync(noSeededClub.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = noSeededClub.Id, ClubName = "Unseeded A", StartYear = 2010, EndYear = 2013 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = noSeededClub.Id, ClubName = "Unseeded B", StartYear = 2013, EndYear = 2016 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = noSeededClub.Id, ClubName = "Unseeded C", StartYear = 2016, EndYear = null },
        ]);

        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minSeededClubCount: 2);

        Assert.That(candidateIds, Does.Not.Contain(noSeededClub.Id));
    }

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_IncludesPlayerWithTwoDistinctSeededClubs()
    {
        var seededClubNames = new HashSet<string> { "Seeded FC", "Seeded FC 2" };
        var eligible = new Player { Id = Guid.NewGuid(), FullName = "Eligible", WikidataQid = "Q1" };
        await _playerRepository.AddPlayerAsync(eligible);
        await _repository.AddCareerStintsAsync(eligible.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = eligible.Id, ClubName = "Seeded FC", StartYear = 2010, EndYear = 2013 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = eligible.Id, ClubName = "Seeded FC 2", StartYear = 2013, EndYear = 2016 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = eligible.Id, ClubName = "Unseeded B", StartYear = 2016, EndYear = null },
        ]);

        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minSeededClubCount: 2);

        Assert.That(candidateIds, Does.Contain(eligible.Id));
    }

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_CaseSensitiveMatch_ExcludesPlayerWhoseOnlySeededClubStintDiffersOnlyInCase()
    {
        // Deliberately diverges from PlayerDataQualityRepository.
        // GetUnseededClubCandidatesAsync's own OrdinalIgnoreCase precedent:
        // this method must match IsEligible's exact
        // seededClubNames.Contains(s.ClubName) behavior, so a stint at a
        // club differing only in case from a seeded name must NOT count.
        var seededClubNames = new HashSet<string> { "Seeded FC", "Seeded FC 2" };
        var caseMismatch = new Player { Id = Guid.NewGuid(), FullName = "Case Mismatch", WikidataQid = "Q1" };
        await _playerRepository.AddPlayerAsync(caseMismatch);
        await _repository.AddCareerStintsAsync(caseMismatch.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = caseMismatch.Id, ClubName = "SEEDED FC", StartYear = 2010, EndYear = 2013 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = caseMismatch.Id, ClubName = "seeded fc 2", StartYear = 2013, EndYear = 2016 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = caseMismatch.Id, ClubName = "Unseeded B", StartYear = 2016, EndYear = null },
        ]);

        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minSeededClubCount: 2);

        Assert.That(candidateIds, Does.Not.Contain(caseMismatch.Id),
            "a club name differing only in case from a seeded name must NOT count, matching IsEligible's own exact-match behavior");
    }

    [Test]
    public async Task GetCareerStintCandidatePlayerIdsAsync_EmptyTable_ReturnsEmpty()
    {
        var candidateIds = await _repository.GetCareerStintCandidatePlayerIdsAsync(new HashSet<string> { "Seeded FC" }, minSeededClubCount: 2);

        Assert.That(candidateIds, Is.Empty);
    }
}
