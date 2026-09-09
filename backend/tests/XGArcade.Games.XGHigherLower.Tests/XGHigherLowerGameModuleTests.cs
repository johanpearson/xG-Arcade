using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGHigherLower.Tests;

// COMP-18/ADR-0110: REQ-1501 (stat-category/player-value eligibility),
// REQ-1502 (comparator eligibility — no exact ties, no repeated player, fail
// closed), REQ-1503 (Round generation — one fixed category/baseline/
// comparator sequence), REQ-1504 (guess submission/streak progression, this
// story, S-225), plus the trivial GetCellIdsAsync derivative. Follows this
// repo's no-mocking-framework pattern (docs/coding-guidelines.md "don't
// over-mock") — real, InMemory-backed HigherLowerInstanceRepository/
// PlayerOverrideRepository/PlayerAttributeRepository/PlayerRepository, same
// "compose the real thing" shape XGPredictGameModuleTests/XGPathGameModuleTests
// already use.
//
// REQ-1504's own tests seed a HigherLowerInstance directly (via
// _instanceRepository.AddInstanceAsync, bypassing GenerateInstanceAsync's
// randomized category/sequence selection) with hand-picked baseline/
// comparator values, so each scenario's correctness outcome is deterministic
// and legible from the test body itself, rather than depending on the fixed
// Random seed used by the REQ-1501/1502/1503 tests above.
//
// GetMaxAttemptsForCellAsync remains NotImplementedException (still a
// deliberately resolved "doesn't apply" decision — see that method's own
// doc comment) — that test is unchanged from the original scaffold.
// ScoreSubmissionAsync_ThrowsNotImplementedException was removed — that
// method is no longer a stub (this story). REQ710_PurgeUserDataAsync_...
// was rewritten — PurgeUserDataAsync is no longer a no-op either.
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
        // defaults" precedent.
        _options = new HigherLowerGenerationOptions { ComparatorCount = 3, MaxAttemptsPerCategory = 20 };
        // A fixed seed makes GenerateInstanceAsync's own shuffling
        // deterministic across test runs without needing to control .NET's
        // internal Random algorithm directly (GridGenerationServiceTests'
        // own documented reasoning for why it does NOT try to pin Random.Shuffle
        // output does not apply here — this module never needs a SPECIFIC
        // category/order, only "some full-length valid sequence", so a
        // fixed seed is safe and simpler).
        _module = new XGHigherLowerGameModule(_instanceRepository, _overrideRepository, _options, new Random(12345));
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

    // ---- REQ-1501/1503: category selection and full-instance shape -----

    [Test]
    public async Task REQ1503_GenerateInstanceAsync_PersistsOneFixedCategoryBaselineAndComparatorSequenceOfConfiguredLength()
    {
        // 5 players, all with distinct trophy counts (1..5) — enough to
        // build a full 4-player (baseline + 3 comparators) sequence with no
        // ties possible at all.
        await SeedPlayersWithAttributeCountsAsync("trophy", [1, 2, 3, 4, 5]);

        var result = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result, Is.Not.Null);
        var instance = await _instanceRepository.GetInstanceByIdAsync(result!.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.StatCategory, Is.EqualTo("trophy").Or.EqualTo("club"));
        Assert.That(instance.Comparators, Has.Count.EqualTo(_options.ComparatorCount),
            "REQ-1503: baseline + configured ComparatorCount comparators");
        // Every position 0..ComparatorCount-1 present exactly once.
        var positions = instance.Comparators.Select(c => c.SequencePosition).OrderBy(p => p).ToList();
        Assert.That(positions, Is.EqualTo(Enumerable.Range(0, _options.ComparatorCount).ToList()));
        // REQ-1502: baseline + every comparator's player is distinct.
        var allPlayerIds = new[] { instance.BaselinePlayerId }.Concat(instance.Comparators.Select(c => c.PlayerId)).ToList();
        Assert.That(allPlayerIds.Distinct().Count(), Is.EqualTo(allPlayerIds.Count), "no player may repeat within the sequence");
        // REQ-1502: no two adjacent values (baseline -> comparator 0 -> comparator 1 -> ...) are an exact tie.
        var orderedValues = new[] { instance.BaselineValue }
            .Concat(instance.Comparators.OrderBy(c => c.SequencePosition).Select(c => c.Value))
            .ToList();
        for (var i = 1; i < orderedValues.Count; i++)
            Assert.That(orderedValues[i], Is.Not.EqualTo(orderedValues[i - 1]), "no exact tie between consecutive positions");
    }

    [Test]
    public async Task REQ1503_GenerateInstanceAsync_CalledTwice_ProducesTwoIndependentInstances()
    {
        await SeedPlayersWithAttributeCountsAsync("trophy", [1, 2, 3, 4, 5, 6, 7]);

        var first = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });
        var second = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null);
        Assert.That(second!.Id, Is.Not.EqualTo(first!.Id));
        Assert.That(await _dbContext.HigherLowerInstances.CountAsync(), Is.EqualTo(2));
    }

    // ---- REQ-1501: stat category and player-value eligibility ----------

    [Test]
    public async Task REQ1501_GenerateInstanceAsync_PlayerWithNoRecordedValueForCategory_NeverSelected()
    {
        // 4 players with a real trophy count (enough for ComparatorCount=3
        // + baseline = 4), plus one extra player with NO trophy attribute
        // rows at all — that extra player must never appear anywhere in the
        // generated sequence.
        var eligiblePlayerIds = await SeedPlayersWithAttributeCountsAsync("trophy", [1, 2, 3, 4]);
        var ineligiblePlayer = new Player { Id = Guid.NewGuid(), FullName = "No Trophies" };
        await _playerRepository.AddPlayerAsync(ineligiblePlayer);

        var result = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result, Is.Not.Null);
        var instance = await _instanceRepository.GetInstanceByIdAsync(result!.Id);
        var usedPlayerIds = new[] { instance!.BaselinePlayerId }.Concat(instance.Comparators.Select(c => c.PlayerId)).ToList();
        Assert.That(usedPlayerIds, Does.Not.Contain(ineligiblePlayer.Id));
        Assert.That(usedPlayerIds, Is.EquivalentTo(eligiblePlayerIds));
    }

    [Test]
    public async Task REQ1501_GenerateInstanceAsync_PlayerOverride_MakesEffectiveCountExactlyOne_RegardlessOfRawRowCount()
    {
        // Baseline pool: 3 players with distinct raw trophy counts (1, 2, 3)
        // PLUS one player with THREE raw trophy rows but an override for
        // "trophy" — per ADR-0015 extended to counting, that player's
        // EFFECTIVE count must be exactly 1 (the override wins), not 3.
        await SeedPlayersWithAttributeCountsAsync("trophy", [2, 3, 4]);
        var overriddenPlayer = new Player { Id = Guid.NewGuid(), FullName = "Overridden Player" };
        await _playerRepository.AddPlayerAsync(overriddenPlayer);
        foreach (var club in new[] { "Club A", "Club B", "Club C" })
            await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = overriddenPlayer.Id, AttributeType = "trophy", AttributeValue = club });
        await _overrideRepository.AddOverrideAsync(new PlayerOverride
        {
            Id = Guid.NewGuid(), PlayerId = overriddenPlayer.Id, Field = "trophy", Value = "Override Trophy",
            Reason = "test", LockedByAdminId = Guid.NewGuid(), LockedAt = DateTime.UtcNow,
        });

        var result = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result, Is.Not.Null);
        var instance = await _instanceRepository.GetInstanceByIdAsync(result!.Id);
        var usedValuesByPlayer = new Dictionary<Guid, int> { [instance!.BaselinePlayerId] = instance.BaselineValue };
        foreach (var comparator in instance.Comparators)
            usedValuesByPlayer[comparator.PlayerId] = comparator.Value;

        if (usedValuesByPlayer.TryGetValue(overriddenPlayer.Id, out var effectiveValue))
            Assert.That(effectiveValue, Is.EqualTo(1), "an override for (PlayerId, attributeType) replaces the whole effective value set with exactly 1");
    }

    [Test]
    public void REQ1501_GenerateInstanceAsync_NoCategoryHasEnoughEligiblePlayers_ThrowsHigherLowerGenerationException_NoInstancePersisted()
    {
        // ComparatorCount=3 needs 4 eligible players; nothing at all is
        // seeded for either candidate category ("trophy"/"club") — neither
        // category can possibly work.

        Assert.ThrowsAsync<HigherLowerGenerationException>(
            () => _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    // ---- REQ-1502: tie exclusion / repeated-player exclusion / fail-closed ----

    [Test]
    public async Task REQ1502_GenerateInstanceAsync_EveryEligiblePlayerSharesSameValue_ThrowsHigherLowerGenerationException_NoInstancePersisted()
    {
        // 5 players, all with the SAME trophy count (2) — enough players by
        // count, but every possible pair is an exact tie, so no valid
        // 2-player (let alone 4-player) sequence can ever be built.
        await SeedPlayersWithAttributeCountsAsync("trophy", [2, 2, 2, 2, 2]);

        Assert.ThrowsAsync<HigherLowerGenerationException>(
            () => _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    [Test]
    public async Task REQ1502_GenerateInstanceAsync_TieExclusion_AbortedGeneration_NeverPersistsAnything()
    {
        await SeedPlayersWithAttributeCountsAsync("trophy", [2, 2, 2, 2, 2]);

        Assert.ThrowsAsync<HigherLowerGenerationException>(
            () => _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));

        Assert.That(await _dbContext.HigherLowerInstances.CountAsync(), Is.EqualTo(0),
            "an aborted generation must not persist a degraded (shorter-than-configured or otherwise invalid) instance");
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
