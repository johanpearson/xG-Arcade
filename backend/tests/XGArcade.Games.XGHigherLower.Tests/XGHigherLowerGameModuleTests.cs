using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGHigherLower.Tests;

// COMP-18/ADR-0110: REQ-1504 (guess submission/streak progression, S-225),
// plus the trivial GetCellIdsAsync derivative and GenerateInstanceAsync's own
// passthrough wiring. Follows this repo's no-mocking-framework pattern
// (docs/coding-guidelines.md "don't over-mock") — real, InMemory-backed
// HigherLowerInstanceRepository/PlayerOverrideRepository/
// PlayerAttributeRepository/PlayerRepository, same "compose the real thing"
// shape XGPredictGameModuleTests/XGPathGameModuleTests already use.
//
// Delegation-pattern refactor (2026-09-10, pure refactor, no behavior
// change): REQ-1501/1502/1503's category-selection/sequence-building
// coverage moved to HigherLowerGenerationServiceTests.cs, alongside
// HigherLowerGenerationService itself — mirroring GridGameModuleTests.cs's
// own split (S-119) into GridGenerationServiceTests.cs. See NOTES.md's
// 2026-09-10 entry for the quality-architect finding this closes. This file
// now only exercises XGHigherLowerGameModule's own remaining responsibility
// as a thin IGameModule adapter: GenerateInstanceAsync's one-line delegation
// to IHigherLowerGenerationService (proven end-to-end via a real
// HigherLowerGenerationService composed behind the module under test, same
// as GridGameModuleTests' own BuildModule composes a real
// GridGenerationService), plus everything that stayed inline —
// ScoreSubmissionAsync (REQ-1504), GetCellIdsAsync,
// GetMaxAttemptsForCellAsync, GetCellCategoryTypesAsync,
// ResolveWrongGuessPlayerAsync, and PurgeUserDataAsync (REQ-710).
//
// REQ-1504's own tests seed a HigherLowerInstance directly (via
// _instanceRepository.AddInstanceAsync, bypassing GenerateInstanceAsync's
// randomized category/sequence selection) with hand-picked baseline/
// comparator values, so each scenario's correctness outcome is deterministic
// and legible from the test body itself, rather than depending on the fixed
// Random seed the generation passthrough test below uses.
//
// GetMaxAttemptsForCellAsync remains NotImplementedException (still a
// deliberately resolved "doesn't apply" decision — see that method's own
// doc comment) — that test is unchanged from the original scaffold.
public class XGHigherLowerGameModuleTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IHigherLowerInstanceRepository _instanceRepository = null!;
    private IPlayerOverrideRepository _overrideRepository = null!;
    private IPlayerAttributeRepository _attributeRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private HigherLowerGenerationOptions _options = null!;
    private XGHigherLowerGameModule _module = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _instanceRepository = new HigherLowerInstanceRepository(_dbContext);
        _overrideRepository = new PlayerOverrideRepository(_dbContext);
        _attributeRepository = new PlayerAttributeRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        // Small ComparatorCount/deterministic-ish attempt budget so tests
        // don't need hundreds of seeded players — mirrors
        // GridGenerationServiceTests' own "tighter values than production
        // defaults" precedent. Only used by the generation passthrough test
        // below now — every REQ-1501/1502/1503 scenario that used to need
        // these values moved to HigherLowerGenerationServiceTests.cs.
        _options = new HigherLowerGenerationOptions { ComparatorCount = 3, MaxAttemptsPerCategory = 20 };
        // A real HigherLowerGenerationService, same seeded Random as before
        // this split, composed behind the module under test — mirrors
        // GridGameModuleTests.cs's own BuildModule composing a real
        // GridGenerationService behind GridGameModule.
        var generationService = new HigherLowerGenerationService(_instanceRepository, _overrideRepository, _options, new Random(12345));
        _module = new XGHigherLowerGameModule(_instanceRepository, generationService);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public void GameKey_ReturnsXgHigherLower()
    {
        Assert.That(_module.GameKey, Is.EqualTo("xg-higher-lower"));
        Assert.That(_module.GameKey, Is.EqualTo(XGHigherLowerGameModule.XGHigherLowerGameKey));
    }

    // ---- GenerateInstanceAsync passthrough ---------------------------------
    // REQ-1501/1502/1503's own category-selection/sequence-building coverage
    // moved to HigherLowerGenerationServiceTests.cs (delegation-pattern
    // refactor, 2026-09-10) — this one test proves the module's
    // one-line delegation actually forwards to IHigherLowerGenerationService
    // and lets its exception cross the adapter boundary unchanged, mirroring
    // GridGameModuleTests.GenerateInstanceAsync_UnknownTemplateId_ThrowsGridGenerationException's
    // own shape.

    [Test]
    public void GenerateInstanceAsync_NoCategoryHasEnoughEligiblePlayers_ThrowsHigherLowerGenerationException()
    {
        // ComparatorCount=3 needs 4 eligible players; nothing at all is
        // seeded for either candidate category ("trophy"/"club") — neither
        // category can possibly work, so IHigherLowerGenerationService
        // throws and this proves the module forwards that call/exception
        // unchanged rather than swallowing or wrapping it.
        Assert.ThrowsAsync<HigherLowerGenerationException>(
            async () => await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    // ---- REQ-1504: guess submission and streak progression -------------

    [Test]
    public async Task REQ1504_ScoreSubmissionAsync_CorrectHigherGuess_NotLastComparator_AdvancesStreakAndBaseline()
    {
        var instanceId = await SeedInstanceAsync(baselineValue: 10, comparatorValues: [20, 5]);
        var instance = await _instanceRepository.GetInstanceByIdAsync(instanceId);
        var firstComparator = instance!.Comparators.Single(c => c.SequencePosition == 0);
        var userId = Guid.NewGuid();

        var result = await _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Higher));

        Assert.That(result.IsCorrect, Is.True, "20 is strictly greater than baseline 10");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(firstComparator.PlayerId));

        var attempt = await _instanceRepository.GetAttemptAsync(instanceId, userId);
        Assert.That(attempt, Is.Not.Null);
        Assert.That(attempt!.StreakLength, Is.EqualTo(1));
        Assert.That(attempt.CurrentBaselinePlayerId, Is.EqualTo(firstComparator.PlayerId));
        Assert.That(attempt.CurrentBaselineValue, Is.EqualTo(20));
        Assert.That(attempt.HasEnded, Is.False);
    }

    [Test]
    public async Task REQ1504_ScoreSubmissionAsync_CorrectLowerGuess_NotLastComparator_AdvancesStreakAndBaseline()
    {
        var instanceId = await SeedInstanceAsync(baselineValue: 10, comparatorValues: [3, 20]);
        var instance = await _instanceRepository.GetInstanceByIdAsync(instanceId);
        var firstComparator = instance!.Comparators.Single(c => c.SequencePosition == 0);
        var userId = Guid.NewGuid();

        var result = await _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Lower));

        Assert.That(result.IsCorrect, Is.True, "3 is strictly less than baseline 10");
        Assert.That(result.PlayerAnswerId, Is.EqualTo(firstComparator.PlayerId));

        var attempt = await _instanceRepository.GetAttemptAsync(instanceId, userId);
        Assert.That(attempt, Is.Not.Null);
        Assert.That(attempt!.StreakLength, Is.EqualTo(1));
        Assert.That(attempt.CurrentBaselinePlayerId, Is.EqualTo(firstComparator.PlayerId));
        Assert.That(attempt.CurrentBaselineValue, Is.EqualTo(3));
        Assert.That(attempt.HasEnded, Is.False);
    }

    [Test]
    public async Task REQ1504_ScoreSubmissionAsync_LastComparatorGuessedCorrectly_EndsAttemptAtFullConfiguredLength()
    {
        // _options.ComparatorCount == 3 (SetUp) — a strictly increasing
        // baseline+3-comparator sequence, guessed "Higher" every time.
        var instanceId = await SeedInstanceAsync(baselineValue: 1, comparatorValues: [2, 3, 4]);
        var userId = Guid.NewGuid();

        await _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Higher));
        await _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Higher));
        var finalResult = await _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Higher));

        Assert.That(finalResult.IsCorrect, Is.True);

        var attempt = await _instanceRepository.GetAttemptAsync(instanceId, userId);
        Assert.That(attempt, Is.Not.Null);
        Assert.That(attempt!.StreakLength, Is.EqualTo(_options.ComparatorCount),
            "REQ-1504: every comparator guessed correctly ends the attempt at the Round's full configured length");
        Assert.That(attempt.HasEnded, Is.True);
    }

    [Test]
    public async Task REQ1504_ScoreSubmissionAsync_IncorrectGuess_EndsAttemptAtPreGuessStreakLength()
    {
        var instanceId = await SeedInstanceAsync(baselineValue: 10, comparatorValues: [20, 25]);
        var instance = await _instanceRepository.GetInstanceByIdAsync(instanceId);
        var secondComparator = instance!.Comparators.Single(c => c.SequencePosition == 1);
        var userId = Guid.NewGuid();

        // First guess correct (20 > 10) — streak advances to 1, baseline becomes 20.
        await _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Higher));

        // Second guess: comparator value 25 is actually Higher than the new
        // baseline (20), so guessing "Lower" is incorrect.
        var result = await _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Lower));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(secondComparator.PlayerId), "the actual value is still revealed on an incorrect guess");

        var attempt = await _instanceRepository.GetAttemptAsync(instanceId, userId);
        Assert.That(attempt, Is.Not.Null);
        Assert.That(attempt!.StreakLength, Is.EqualTo(1), "REQ-1504: streak is counted at the length reached BEFORE the incorrect guess");
        Assert.That(attempt.HasEnded, Is.True);
    }

    [Test]
    public async Task REQ1504_ScoreSubmissionAsync_AgainstAlreadyEndedAttempt_IncorrectEnded_ThrowsHigherLowerAttemptEndedException()
    {
        var instanceId = await SeedInstanceAsync(baselineValue: 10, comparatorValues: [5]);
        var userId = Guid.NewGuid();

        // Incorrect guess (5 is Lower, not Higher) ends the attempt.
        await _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Higher));

        Assert.ThrowsAsync<HigherLowerAttemptEndedException>(
            () => _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Lower)));
    }

    [Test]
    public async Task REQ1504_ScoreSubmissionAsync_AgainstAlreadyEndedAttempt_FullLengthEnded_ThrowsHigherLowerAttemptEndedException()
    {
        var instanceId = await SeedInstanceAsync(baselineValue: 1, comparatorValues: [2]); // ComparatorCount irrelevant here — instance has exactly 1 comparator
        var userId = Guid.NewGuid();

        // Only comparator guessed correctly — full-length terminal outcome.
        await _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Higher));

        Assert.ThrowsAsync<HigherLowerAttemptEndedException>(
            () => _module.ScoreSubmissionAsync(instanceId, userId, new HigherLowerSubmission(HigherLowerDirection.Higher)));
    }

    [Test]
    public void REQ1504_ScoreSubmissionAsync_InstanceNotFound_ThrowsHigherLowerScoringException()
    {
        Assert.ThrowsAsync<HigherLowerScoringException>(
            () => _module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new HigherLowerSubmission(HigherLowerDirection.Higher)));
    }

    [Test]
    public void GetMaxAttemptsForCellAsync_ThrowsNotImplementedException()
    {
        Assert.ThrowsAsync<NotImplementedException>(
            () => _module.GetMaxAttemptsForCellAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    // ---- GetCellIdsAsync (implemented for real this story) -------------

    [Test]
    public async Task GetCellIdsAsync_ReturnsEveryComparatorId()
    {
        await SeedPlayersWithAttributeCountsAsync("trophy", [1, 2, 3, 4]);
        var result = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        var cellIds = await _module.GetCellIdsAsync(result!.Id);

        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(cellIds, Is.EquivalentTo(instance!.Comparators.Select(c => c.Id)));
    }

    [Test]
    public void GetCellIdsAsync_InstanceNotFound_ThrowsHigherLowerScoringException()
    {
        Assert.ThrowsAsync<HigherLowerScoringException>(() => _module.GetCellIdsAsync(Guid.NewGuid()));
    }

    [Test]
    public void GetCellCategoryTypesAsync_ThrowsNotSupportedException()
    {
        Assert.ThrowsAsync<NotSupportedException>(
            () => _module.GetCellCategoryTypesAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Test]
    public async Task ResolveWrongGuessPlayerAsync_ReturnsNull()
    {
        var result = await _module.ResolveWrongGuessPlayerAsync(Guid.NewGuid(), "Some Player");

        Assert.That(result, Is.Null);
    }

    // ---- REQ-710: PurgeUserDataAsync -----------------------------------

    [Test]
    public void REQ710_PurgeUserDataAsync_CompletesWithoutThrowing_WhenUserHasNoAttempts()
    {
        Assert.DoesNotThrowAsync(() => _module.PurgeUserDataAsync(Guid.NewGuid()));
    }

    [Test]
    public async Task REQ710_PurgeUserDataAsync_AnonymizesUsersHigherLowerAttempts_LeavesOtherUsersUntouched()
    {
        var instanceId = await SeedInstanceAsync(baselineValue: 10, comparatorValues: [20]);
        var targetUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        await _module.ScoreSubmissionAsync(instanceId, targetUserId, new HigherLowerSubmission(HigherLowerDirection.Higher));
        await _module.ScoreSubmissionAsync(instanceId, otherUserId, new HigherLowerSubmission(HigherLowerDirection.Higher));

        await _module.PurgeUserDataAsync(targetUserId);

        Assert.That(await _instanceRepository.GetAttemptAsync(instanceId, targetUserId), Is.Null,
            "the anonymized row no longer matches a lookup by the deleted user's id");
        var otherAttempt = await _instanceRepository.GetAttemptAsync(instanceId, otherUserId);
        Assert.That(otherAttempt, Is.Not.Null, "another user's attempt must be untouched by anonymizing a different user");
        Assert.That(otherAttempt!.StreakLength, Is.EqualTo(1));
    }

    // ---- helpers --------------------------------------------------

    // Seeds a HigherLowerInstance directly (bypassing GenerateInstanceAsync's
    // randomized category/sequence selection), with a fixed baseline value
    // and an explicit, ordered list of comparator values — so REQ-1504's
    // correctness/streak-progression tests above have a fully deterministic
    // sequence to guess against. Real Player rows are created for the
    // baseline and each comparator, mirroring every other test helper's own
    // "seed real entities" pattern in this file.
    private async Task<Guid> SeedInstanceAsync(int baselineValue, params int[] comparatorValues)
    {
        var baselinePlayer = new Player { Id = Guid.NewGuid(), FullName = $"Baseline {Guid.NewGuid()}" };
        await _playerRepository.AddPlayerAsync(baselinePlayer);

        var instanceId = Guid.NewGuid();
        var comparators = new List<HigherLowerComparator>();
        for (var i = 0; i < comparatorValues.Length; i++)
        {
            var comparatorPlayer = new Player { Id = Guid.NewGuid(), FullName = $"Comparator {i} {Guid.NewGuid()}" };
            await _playerRepository.AddPlayerAsync(comparatorPlayer);
            comparators.Add(new HigherLowerComparator
            {
                Id = Guid.NewGuid(),
                HigherLowerInstanceId = instanceId,
                SequencePosition = i,
                PlayerId = comparatorPlayer.Id,
                Value = comparatorValues[i],
            });
        }

        var instance = new HigherLowerInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            StatCategory = "trophy",
            BaselinePlayerId = baselinePlayer.Id,
            BaselineValue = baselineValue,
            Comparators = comparators,
        };

        await _instanceRepository.AddInstanceAsync(instance);
        return instanceId;
    }

    // Seeds one player per count in `counts`, each with exactly that many
    // distinct raw PlayerAttribute rows of `attributeType` — e.g. counts
    // [1, 2, 3] seeds 3 players with 1, 2, and 3 distinct attribute values
    // respectively. Returns the seeded players' ids.
    private async Task<List<Guid>> SeedPlayersWithAttributeCountsAsync(string attributeType, int[] counts)
    {
        var playerIds = new List<Guid>();
        foreach (var count in counts)
        {
            var player = new Player { Id = Guid.NewGuid(), FullName = $"Player {Guid.NewGuid()}" };
            await _playerRepository.AddPlayerAsync(player);
            for (var i = 0; i < count; i++)
            {
                await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
                {
                    PlayerId = player.Id,
                    AttributeType = attributeType,
                    AttributeValue = $"{attributeType}-{i}-{player.Id}",
                });
            }
            playerIds.Add(player.Id);
        }
        return playerIds;
    }
}
