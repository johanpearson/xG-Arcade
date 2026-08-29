using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// audit-club-gaps diagnostic (GetUnseededClubCandidatesAsync) plus the
// confirmed-low/technical-failure match-pair tracking (REQ-110). Split out
// of PlayerStoreRepositoryTests.cs (S-107, docs/backlog.md Epic 8, pure
// refactor — see ADR-0067 for the full split) — test bodies/assertions for
// GetUnseededClubCandidatesAsync are unchanged from their original
// PlayerStoreRepositoryTests.cs form, this is a structural move only.
// _playerRepository/_playerCareerStintRepository below are only used to
// seed fixtures — AddPlayerAsync/AddCareerStintsAsync themselves are
// covered directly in PlayerRepositoryTests.cs/PlayerCareerStintRepositoryTests.cs.
//
// IsConfirmedLowAsync/RecordConfirmedLowAsync/IsPersistentTechnicalFailureAsync/
// RecordTechnicalFailureAsync/ClearTechnicalFailureAsync (S-122, direct
// coverage added to close the gap this file's own comment used to flag) are
// still also exercised indirectly, through the real repository, by
// GridGameModuleTests.cs/PlayerCacheWarmingServiceTests.cs — that indirect
// coverage is unchanged and not duplicated here.
public class PlayerDataQualityRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerDataQualityRepository _repository = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new PlayerDataQualityRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerCareerStintRepository = new PlayerCareerStintRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ---- confirmed-low match pairs (IsConfirmedLowAsync/RecordConfirmedLowAsync) ----

    [Test]
    public async Task IsConfirmedLowAsync_ReturnsFalse_WhenNoMatchingRowExists()
    {
        var result = await _repository.IsConfirmedLowAsync("Nationality", "France", "Club", "Arsenal");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsConfirmedLowAsync_ReturnsTrue_WhenExactCompositeKeyMatchExists()
    {
        await _repository.RecordConfirmedLowAsync("Nationality", "France", "Club", "Arsenal", matchCount: 0);

        var result = await _repository.IsConfirmedLowAsync("Nationality", "France", "Club", "Arsenal");

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsConfirmedLowAsync_ReturnsFalse_WhenOnlyPartialKeyMatches()
    {
        // Same first attribute pair, different second attribute value — must
        // not match, the composite key is all four columns.
        await _repository.RecordConfirmedLowAsync("Nationality", "France", "Club", "Arsenal", matchCount: 0);

        var result = await _repository.IsConfirmedLowAsync("Nationality", "France", "Club", "Napoli");

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task RecordConfirmedLowAsync_InsertsNewRow_WhenNoneExists()
    {
        await _repository.RecordConfirmedLowAsync("Nationality", "France", "Club", "Arsenal", matchCount: 2);

        var stored = await _dbContext.ConfirmedLowMatchPairs.SingleAsync(c =>
            c.FirstAttributeType == "Nationality" && c.FirstAttributeValue == "France" &&
            c.SecondAttributeType == "Club" && c.SecondAttributeValue == "Arsenal");
        Assert.That(stored.MatchCount, Is.EqualTo(2));
        Assert.That(stored.ConfirmedAt, Is.Not.EqualTo(default(DateTime)));
        Assert.That(await _repository.IsConfirmedLowAsync("Nationality", "France", "Club", "Arsenal"), Is.True);
    }

    [Test]
    public async Task RecordConfirmedLowAsync_UpsertsInPlace_WhenCalledTwiceForSameKey()
    {
        await _repository.RecordConfirmedLowAsync("Nationality", "France", "Club", "Arsenal", matchCount: 0);
        var firstConfirmedAt = (await _dbContext.ConfirmedLowMatchPairs.SingleAsync()).ConfirmedAt;

        await _repository.RecordConfirmedLowAsync("Nationality", "France", "Club", "Arsenal", matchCount: 3);

        var rows = await _dbContext.ConfirmedLowMatchPairs.Where(c =>
            c.FirstAttributeType == "Nationality" && c.FirstAttributeValue == "France" &&
            c.SecondAttributeType == "Club" && c.SecondAttributeValue == "Arsenal").ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1), "a second call for the same composite key must update in place, not insert a duplicate row");
        Assert.That(rows[0].MatchCount, Is.EqualTo(3), "MatchCount must reflect the second call's value");
        Assert.That(rows[0].ConfirmedAt, Is.GreaterThanOrEqualTo(firstConfirmedAt), "ConfirmedAt must be refreshed by the second call");
    }

    [Test]
    public async Task RecordConfirmedLowAsync_LeavesOtherCompositeKeysUntouched()
    {
        await _repository.RecordConfirmedLowAsync("Nationality", "France", "Club", "Arsenal", matchCount: 2);

        await _repository.RecordConfirmedLowAsync("Nationality", "Spain", "Club", "Napoli", matchCount: 5);

        var rows = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(2), "recording a confirmed-low pair for one composite key must not overwrite or merge with an unrelated key");
        Assert.That(rows.Single(r => r.SecondAttributeValue == "Arsenal").MatchCount, Is.EqualTo(2));
        Assert.That(rows.Single(r => r.SecondAttributeValue == "Napoli").MatchCount, Is.EqualTo(5));
    }

    // ---- technical-failure tracking (IsPersistentTechnicalFailureAsync/RecordTechnicalFailureAsync/ClearTechnicalFailureAsync) ----

    [Test]
    public async Task IsPersistentTechnicalFailureAsync_ReturnsFalse_WhenNoRowExists()
    {
        var result = await _repository.IsPersistentTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal", threshold: 3);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsPersistentTechnicalFailureAsync_ReturnsFalse_WhenConsecutiveFailureCountBelowThreshold()
    {
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");

        var result = await _repository.IsPersistentTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal", threshold: 3);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task REQ110_IsPersistentTechnicalFailureAsync_ReturnsTrue_WhenConsecutiveFailureCountEqualsThreshold()
    {
        // Boundary case: exactly equal to threshold must already count as
        // persistent, per this method's own ">=" contract.
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");

        var result = await _repository.IsPersistentTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal", threshold: 3);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task IsPersistentTechnicalFailureAsync_ReturnsFalse_WhenOnlyPartialKeyMatches()
    {
        // Same first attribute pair, different second attribute value — must
        // not match, the composite key is all four columns.
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");

        var result = await _repository.IsPersistentTechnicalFailureAsync("Nationality", "France", "Club", "Napoli", threshold: 3);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task RecordTechnicalFailureAsync_InsertsNewRow_WithFailureCountOfOne_WhenNoneExists()
    {
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");

        var stored = await _dbContext.PairLookupFailures.SingleAsync(f =>
            f.FirstAttributeType == "Nationality" && f.FirstAttributeValue == "France" &&
            f.SecondAttributeType == "Club" && f.SecondAttributeValue == "Arsenal");
        Assert.That(stored.ConsecutiveFailureCount, Is.EqualTo(1));
        Assert.That(stored.LastFailedAt, Is.Not.EqualTo(default(DateTime)));
    }

    [Test]
    public async Task RecordTechnicalFailureAsync_IncrementsExistingRow_RatherThanInsertingDuplicate()
    {
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        var firstFailedAt = (await _dbContext.PairLookupFailures.SingleAsync()).LastFailedAt;
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");

        var rows = await _dbContext.PairLookupFailures.Where(f =>
            f.FirstAttributeType == "Nationality" && f.FirstAttributeValue == "France" &&
            f.SecondAttributeType == "Club" && f.SecondAttributeValue == "Arsenal").ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1), "repeated failures for the same composite key must increment in place, not insert duplicate rows");
        Assert.That(rows[0].ConsecutiveFailureCount, Is.EqualTo(3));
        Assert.That(rows[0].LastFailedAt, Is.GreaterThanOrEqualTo(firstFailedAt), "LastFailedAt must be refreshed on each failure");
    }

    [Test]
    public async Task RecordTechnicalFailureAsync_TracksConsecutiveFailureCountIndependently_PerCompositeKey()
    {
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        await _repository.RecordTechnicalFailureAsync("Nationality", "Spain", "Club", "Napoli");

        var rows = await _dbContext.PairLookupFailures.ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(2), "failures for one pair must not bleed into another pair's counter");
        Assert.That(rows.Single(r => r.SecondAttributeValue == "Arsenal").ConsecutiveFailureCount, Is.EqualTo(2));
        Assert.That(rows.Single(r => r.SecondAttributeValue == "Napoli").ConsecutiveFailureCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ClearTechnicalFailureAsync_DeletesExistingRow_ForTheKey()
    {
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");

        await _repository.ClearTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");

        Assert.That(await _dbContext.PairLookupFailures.AnyAsync(), Is.False);
        Assert.That(await _repository.IsPersistentTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal", threshold: 1), Is.False);
    }

    [Test]
    public async Task ClearTechnicalFailureAsync_IsNoOp_WhenNoRowExistsForTheKey()
    {
        Assert.DoesNotThrowAsync(async () =>
            await _repository.ClearTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal"));
        Assert.That(await _dbContext.PairLookupFailures.AnyAsync(), Is.False);
    }

    [Test]
    public async Task ClearTechnicalFailureAsync_LeavesOtherCompositeKeysUntouched()
    {
        await _repository.RecordTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");
        await _repository.RecordTechnicalFailureAsync("Nationality", "Spain", "Club", "Napoli");

        await _repository.ClearTechnicalFailureAsync("Nationality", "France", "Club", "Arsenal");

        var rows = await _dbContext.PairLookupFailures.ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(1), "clearing one pair's failure row must not touch another pair's real one");
        Assert.That(rows[0].SecondAttributeValue, Is.EqualTo("Napoli"));
        Assert.That(rows[0].ConsecutiveFailureCount, Is.EqualTo(1));
    }

    // ---- S-189/ADR-0093: targeted match-pair invalidation (ClearMatchPairAsync) ----

    [Test]
    public async Task REQ110_ClearMatchPairAsync_DeletesConfirmedLowMatchPair_WhenStoredInTheGivenOrder()
    {
        await _repository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);

        await _repository.ClearMatchPairAsync("nationality", "France", "club", "Arsenal");

        Assert.That(await _repository.IsConfirmedLowAsync("nationality", "France", "club", "Arsenal"), Is.False);
    }

    [Test]
    public async Task REQ110_ClearMatchPairAsync_DeletesConfirmedLowMatchPair_WhenStoredInTheReversedOrder()
    {
        // Recorded with club First/nationality Second — the opposite of
        // PlayerCacheWarmingService's own Country-x-Club convention.
        // ClearMatchPairAsync's caller (RecentTransferSweepService) has no
        // way to know which order a given pair was originally recorded
        // under, so both must be checked.
        await _repository.RecordConfirmedLowAsync("club", "Arsenal", "nationality", "France", matchCount: 1);

        await _repository.ClearMatchPairAsync("nationality", "France", "club", "Arsenal");

        Assert.That(await _repository.IsConfirmedLowAsync("club", "Arsenal", "nationality", "France"), Is.False);
    }

    [Test]
    public async Task REQ110_ClearMatchPairAsync_DeletesPairLookupFailure_InEitherOrder()
    {
        await _repository.RecordTechnicalFailureAsync("club", "Arsenal", "nationality", "France");

        await _repository.ClearMatchPairAsync("nationality", "France", "club", "Arsenal");

        Assert.That(
            await _repository.IsPersistentTechnicalFailureAsync("club", "Arsenal", "nationality", "France", threshold: 1),
            Is.False);
    }

    [Test]
    public async Task REQ110_ClearMatchPairAsync_ClearsBothTablesInOneCall()
    {
        await _repository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        await _repository.RecordTechnicalFailureAsync("nationality", "France", "club", "Arsenal");

        await _repository.ClearMatchPairAsync("nationality", "France", "club", "Arsenal");

        Assert.That(await _dbContext.ConfirmedLowMatchPairs.AnyAsync(), Is.False);
        Assert.That(await _dbContext.PairLookupFailures.AnyAsync(), Is.False);
    }

    [Test]
    public async Task REQ110_ClearMatchPairAsync_IsNoOp_WhenNoMatchingRowExistsInEitherTable()
    {
        Assert.DoesNotThrowAsync(async () =>
            await _repository.ClearMatchPairAsync("nationality", "France", "club", "Arsenal"));
        Assert.That(await _dbContext.ConfirmedLowMatchPairs.AnyAsync(), Is.False);
        Assert.That(await _dbContext.PairLookupFailures.AnyAsync(), Is.False);
    }

    [Test]
    public async Task REQ110_ClearMatchPairAsync_LeavesUnrelatedPairsUntouched()
    {
        await _repository.RecordConfirmedLowAsync("nationality", "France", "club", "Arsenal", matchCount: 1);
        // Same nationality, different club — must survive.
        await _repository.RecordConfirmedLowAsync("nationality", "France", "club", "Barcelona", matchCount: 2);
        // Same club, different nationality — must survive.
        await _repository.RecordConfirmedLowAsync("nationality", "Spain", "club", "Arsenal", matchCount: 3);

        await _repository.ClearMatchPairAsync("nationality", "France", "club", "Arsenal");

        var rows = await _dbContext.ConfirmedLowMatchPairs.ToListAsync();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Select(r => r.SecondAttributeValue), Is.EquivalentTo(new[] { "Barcelona", "Arsenal" }));
        Assert.That(await _repository.IsConfirmedLowAsync("nationality", "France", "club", "Barcelona"), Is.True);
        Assert.That(await _repository.IsConfirmedLowAsync("nationality", "Spain", "club", "Arsenal"), Is.True);
    }

    // ---- audit-club-gaps diagnostic (GetUnseededClubCandidatesAsync) -------

    [Test]
    public async Task GetUnseededClubCandidatesAsync_ExcludesClubsAlreadyInClubDefinition()
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
        await _dbContext.SaveChangesAsync();

        var seededClubPlayer = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        var unseededClubPlayer = new Player { Id = Guid.NewGuid(), FullName = "Someone Else", WikidataQid = "Q999" };
        await _playerRepository.AddPlayerAsync(seededClubPlayer);
        await _playerRepository.AddPlayerAsync(unseededClubPlayer);
        await _playerCareerStintRepository.AddCareerStintsAsync(seededClubPlayer.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = seededClubPlayer.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(unseededClubPlayer.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = unseededClubPlayer.Id, ClubName = "Napoli", StartYear = 2010, EndYear = 2015 }]);

        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates.Select(c => c.ClubName), Is.EquivalentTo(new[] { "Napoli" }),
            "Arsenal already has a matching ClubDefinition and must not be surfaced as a gap");
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_CountsDistinctPlayers_NotStints()
    {
        var playerWithTwoStints = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "Q1" };
        await _playerRepository.AddPlayerAsync(playerWithTwoStints);
        // Two separate stints at the same unseeded club (e.g. a loan then a
        // later permanent return) — must still count as ONE distinct player.
        await _playerCareerStintRepository.AddCareerStintsAsync(playerWithTwoStints.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerWithTwoStints.Id, ClubName = "Napoli", StartYear = 2005, EndYear = 2007 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerWithTwoStints.Id, ClubName = "Napoli", StartYear = 2010, EndYear = 2012 },
        ]);

        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates, Has.Count.EqualTo(1));
        Assert.That(candidates[0].ClubName, Is.EqualTo("Napoli"));
        Assert.That(candidates[0].PlayerCount, Is.EqualTo(1), "two stints for the same player at the same club must count as one distinct player");
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_OrdersByDistinctPlayerCountDescending()
    {
        var playerA = new Player { Id = Guid.NewGuid(), FullName = "Player A", WikidataQid = "Q1" };
        var playerB = new Player { Id = Guid.NewGuid(), FullName = "Player B", WikidataQid = "Q2" };
        var playerC = new Player { Id = Guid.NewGuid(), FullName = "Player C", WikidataQid = "Q3" };
        await _playerRepository.AddPlayerAsync(playerA);
        await _playerRepository.AddPlayerAsync(playerB);
        await _playerRepository.AddPlayerAsync(playerC);

        // "Popular Unseeded Club": 2 distinct players. "Rare Unseeded Club": 1.
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Popular Unseeded Club", StartYear = 2000, EndYear = 2005 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Popular Unseeded Club", StartYear = 2001, EndYear = 2006 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerC.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerC.Id, ClubName = "Rare Unseeded Club", StartYear = 2002, EndYear = 2004 }]);

        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates.Select(c => c.ClubName), Is.EqualTo(new[] { "Popular Unseeded Club", "Rare Unseeded Club" }));
        Assert.That(candidates[0].PlayerCount, Is.EqualTo(2));
        Assert.That(candidates[1].PlayerCount, Is.EqualTo(1));
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_RespectsTopLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            var player = new Player { Id = Guid.NewGuid(), FullName = $"Player {i}", WikidataQid = $"Q{i}" };
            await _playerRepository.AddPlayerAsync(player);
            await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
                [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = $"Unseeded Club {i}", StartYear = 2000, EndYear = 2005 }]);
        }

        var candidates = await _repository.GetUnseededClubCandidatesAsync(3);

        Assert.That(candidates, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_CaseInsensitiveMatch_ExcludesClubDespiteCaseDifference()
    {
        // Flagged assumption (see GetUnseededClubCandidatesAsync's own doc
        // comment): a case-only difference between a Wikidata-sourced
        // ClubName and a hand-seeded ClubDefinition.Name is treated as the
        // same club, not a gap.
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
        await _dbContext.SaveChangesAsync();

        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = "Q1519" };
        await _playerRepository.AddPlayerAsync(player);
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "ARSENAL", StartYear = 1999, EndYear = 2007 }]);

        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates, Is.Empty, "a case-only difference from a seeded ClubDefinition.Name must not be surfaced as a gap");
    }

    [Test]
    public async Task GetUnseededClubCandidatesAsync_ReturnsEmpty_WhenNoCareerStintsExist()
    {
        var candidates = await _repository.GetUnseededClubCandidatesAsync(30);

        Assert.That(candidates, Is.Empty);
    }
}
