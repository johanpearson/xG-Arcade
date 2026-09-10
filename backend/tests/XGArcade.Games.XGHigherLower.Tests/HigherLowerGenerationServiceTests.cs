using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGHigherLower.Tests;

// REQ-1501 (stat-category/player-value eligibility), REQ-1502 (comparator
// eligibility — no exact ties, no repeated player, fail closed), REQ-1503
// (Round generation — one fixed category/baseline/comparator sequence),
// REQ-1506 (S-231, ADR-0112 — pool-wide international-caps>=10 floor) —
// docs/requirements-document.md §4.16. Delegation-pattern refactor
// (2026-09-10, pure refactor, no behavior change): split out of
// XGHigherLowerGameModuleTests.cs alongside HigherLowerGenerationService
// itself, mirroring GridGenerationServiceTests.cs's own split from
// GridGameModuleTests.cs (S-119) — see NOTES.md's 2026-09-10 entry for the
// quality-architect finding this closes. Every test here exercises
// IHigherLowerGenerationService directly against a freshly-constructed
// HigherLowerGenerationService, rather than going through
// XGHigherLowerGameModule.GenerateInstanceAsync — the "fakes/mocks only
// construct the one class under test" convention S-106/S-107 established
// for the IPlayerStoreRepository split (ADR-0067). Follows this repo's
// no-mocking-framework pattern (docs/coding-guidelines.md "don't
// over-mock"): real, InMemory-backed HigherLowerInstanceRepository/
// PlayerOverrideRepository/PlayerAttributeRepository/PlayerRepository, same
// "compose the real thing" shape XGHigherLowerGameModuleTests.cs already
// used.
//
// S-231 (ADR-0112): REQ-1506's caps>=10 floor now applies regardless of
// active category, so every fixture below that expects a player to remain
// SELECTABLE (for any REQ-1501/1502/1503 assertion unrelated to REQ-1506
// itself) must also give that player a qualifying "international-caps"
// PlayerAttribute row — see SeedPlayersWithTrophyCountsAsync's own
// `capsForEach` parameter, defaulted to satisfy the floor so every
// pre-existing test below keeps testing what it originally tested, not
// accidentally exercising REQ-1506's exclusion instead.
public class HigherLowerGenerationServiceTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IHigherLowerInstanceRepository _instanceRepository = null!;
    private IPlayerOverrideRepository _overrideRepository = null!;
    private IPlayerAttributeRepository _attributeRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private HigherLowerGenerationOptions _options = null!;
    private HigherLowerGenerationService _service = null!;

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
        // own documented reasoning for why it does NOT try to pin
        // Random.Shuffle output does not apply here — this service never
        // needs a SPECIFIC category/order, only "some full-length valid
        // sequence", so a fixed seed is safe and simpler).
        _service = new HigherLowerGenerationService(_instanceRepository, _overrideRepository, _options, new Random(12345));
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ---- REQ-1501/1503: category selection and full-instance shape -----

    [Test]
    public async Task REQ1503_GenerateInstanceAsync_PersistsOneFixedCategoryBaselineAndComparatorSequenceOfConfiguredLength()
    {
        // 5 players, all with distinct trophy counts (1..5) — enough to
        // build a full 4-player (baseline + 3 comparators) sequence with no
        // ties possible at all. A uniform caps=10 for every player (S-231
        // REQ-1506) satisfies the pool-wide floor without introducing a
        // second distinct-value category — "international-caps" itself
        // ties out (every player shares caps=10) and "international-goals"
        // has no data at all, so "trophy" is the only category this
        // fixture can ever actually complete a sequence for.
        await SeedPlayersWithTrophyCountsAsync([1, 2, 3, 4, 5], capsForEach: 10);

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result, Is.Not.Null);
        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance, Is.Not.Null);
        Assert.That(instance!.StatCategory, Is.EqualTo("trophy"));
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
        await SeedPlayersWithTrophyCountsAsync([1, 2, 3, 4, 5, 6, 7], capsForEach: 10);

        var first = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });
        var second = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null);
        Assert.That(second.Id, Is.Not.EqualTo(first.Id));
        Assert.That(await _dbContext.HigherLowerInstances.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task S231_GenerateInstanceAsync_ClubIsNoLongerACandidateCategory_RichClubOnlyPoolStillFailsClosed()
    {
        // A rich, otherwise-perfectly-valid "club" pool (distinct counts,
        // plenty of players, every one caps-eligible) must never be usable
        // — "club" was removed from CandidateStatCategories entirely
        // (S-231, ADR-0111's own pre-approved rollback). No trophy/caps/
        // goals data exists at all, so generation must fail closed.
        var players = await SeedPlayersWithTrophyCountsAsync([1, 2, 3, 4, 5], attributeType: "club", capsForEach: 10);
        Assert.That(players, Has.Count.EqualTo(5)); // sanity: fixture actually seeded

        Assert.ThrowsAsync<HigherLowerGenerationException>(
            () => _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }),
            "'club' must never be usable as a stat category, regardless of how rich its data is");
    }

    // ---- REQ-1501: stat category and player-value eligibility ----------

    [Test]
    public async Task REQ1501_GenerateInstanceAsync_PlayerWithNoRecordedValueForCategory_NeverSelected()
    {
        // 4 players with a real trophy count (enough for ComparatorCount=3
        // + baseline = 4), plus one extra player with NO trophy attribute
        // rows at all — that extra player must never appear anywhere in the
        // generated sequence.
        var eligiblePlayerIds = await SeedPlayersWithTrophyCountsAsync([1, 2, 3, 4], capsForEach: 10);
        var ineligiblePlayer = new Player { Id = Guid.NewGuid(), FullName = "No Trophies" };
        await _playerRepository.AddPlayerAsync(ineligiblePlayer);

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result, Is.Not.Null);
        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
        var usedPlayerIds = new[] { instance!.BaselinePlayerId }.Concat(instance.Comparators.Select(c => c.PlayerId)).ToList();
        Assert.That(usedPlayerIds, Does.Not.Contain(ineligiblePlayer.Id));
        Assert.That(usedPlayerIds, Is.EquivalentTo(eligiblePlayerIds));
    }

    [Test]
    public async Task REQ1501_GenerateInstanceAsync_PlayerOverride_MakesEffectiveCountExactlyOne_RegardlessOfRawRowCount()
    {
        // Baseline pool: 3 players with distinct raw trophy counts (2, 3, 4)
        // PLUS one player with THREE raw trophy rows but an override for
        // "trophy" — per ADR-0015 extended to counting, that player's
        // EFFECTIVE count must be exactly 1 (the override wins), not 3.
        await SeedPlayersWithTrophyCountsAsync([2, 3, 4], capsForEach: 10);
        var overriddenPlayer = new Player { Id = Guid.NewGuid(), FullName = "Overridden Player" };
        await _playerRepository.AddPlayerAsync(overriddenPlayer);
        foreach (var club in new[] { "Club A", "Club B", "Club C" })
            await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = overriddenPlayer.Id, AttributeType = "trophy", AttributeValue = club });
        // REQ-1506 (S-231): this player must also clear the caps floor, or
        // they'd be excluded from the pool before this test's own override
        // assertion ever gets a chance to matter.
        await SeedSingleValueAttributeAsync(overriddenPlayer.Id, "international-caps", 10);
        await _overrideRepository.AddOverrideAsync(new PlayerOverride
        {
            Id = Guid.NewGuid(), PlayerId = overriddenPlayer.Id, Field = "trophy", Value = "Override Trophy",
            Reason = "test", LockedByAdminId = Guid.NewGuid(), LockedAt = DateTime.UtcNow,
        });

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result, Is.Not.Null);
        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
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
        // seeded for any candidate category ("trophy"/"international-caps"/
        // "international-goals") — none can possibly work.

        Assert.ThrowsAsync<HigherLowerGenerationException>(
            () => _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    [Test]
    public async Task REQ1501_GenerateInstanceAsync_InternationalCapsCategory_GeneratesCorrectly_UsingSingleRecordedValue()
    {
        // Distinct caps values (all >= the floor, so REQ-1506 never excludes
        // any of them) — proves ADR-0112's single-recorded-value derivation
        // shape works end to end as an active category, not just as the
        // pool-wide floor read.
        await SeedPlayersWithSingleValueAttributeAsync("international-caps", [10, 25, 50, 80, 120]);

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance!.StatCategory, Is.EqualTo("international-caps"));
        Assert.That(instance.Comparators, Has.Count.EqualTo(_options.ComparatorCount));
    }

    [Test]
    public async Task REQ1501_GenerateInstanceAsync_InternationalGoalsCategory_GeneratesCorrectly_UsingSingleRecordedValue()
    {
        // "international-goals" is not itself subject to any floor (REQ-1506
        // only ever reads "international-caps") — but every player still
        // needs a qualifying caps value or the pool-wide floor excludes them
        // regardless of the active category.
        var playerIds = await SeedPlayersWithSingleValueAttributeAsync("international-goals", [0, 5, 12, 30, 44]);
        foreach (var playerId in playerIds)
            await SeedSingleValueAttributeAsync(playerId, "international-caps", 10);

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance!.StatCategory, Is.EqualTo("international-goals"));
        Assert.That(instance.Comparators, Has.Count.EqualTo(_options.ComparatorCount));
    }

    // ---- REQ-1502: tie exclusion / repeated-player exclusion / fail-closed ----

    [Test]
    public async Task REQ1502_GenerateInstanceAsync_EveryEligiblePlayerSharesSameValue_ThrowsHigherLowerGenerationException_NoInstancePersisted()
    {
        // 5 players, all with the SAME trophy count (2) — enough players by
        // count, but every possible pair is an exact tie, so no valid
        // 2-player (let alone 4-player) sequence can ever be built. Every
        // player also clears the caps floor (uniformly, so
        // "international-caps" itself is equally tied-out) and no
        // "international-goals" data exists at all — every candidate
        // category fails, so this remains a genuine REQ-1502 tie-exclusion
        // proof, not an accidental REQ-1506 exclusion.
        await SeedPlayersWithTrophyCountsAsync([2, 2, 2, 2, 2], capsForEach: 10);

        Assert.ThrowsAsync<HigherLowerGenerationException>(
            () => _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    [Test]
    public async Task REQ1502_GenerateInstanceAsync_TieExclusion_AbortedGeneration_NeverPersistsAnything()
    {
        await SeedPlayersWithTrophyCountsAsync([2, 2, 2, 2, 2], capsForEach: 10);

        Assert.ThrowsAsync<HigherLowerGenerationException>(
            () => _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));

        Assert.That(await _dbContext.HigherLowerInstances.CountAsync(), Is.EqualTo(0),
            "an aborted generation must not persist a degraded (shorter-than-configured or otherwise invalid) instance");
    }

    // ---- REQ-1506 (S-231, ADR-0112): pool-wide international-caps>=10 floor ----

    [Test]
    public async Task REQ1506_GenerateInstanceAsync_PlayerBelowCapsFloor_NeverSelected_EvenWhenOtherwiseEligibleForActiveCategory()
    {
        // 4 players with distinct, valid trophy counts AND caps >= 10
        // (genuinely eligible), plus one extra player with a distinct
        // trophy count too but caps of only 5 — REQ-1501's own trophy
        // eligibility rule would happily select this player; REQ-1506 must
        // still exclude them.
        var eligiblePlayerIds = await SeedPlayersWithTrophyCountsAsync([1, 2, 3, 4], capsForEach: 10);
        var belowFloorPlayer = new Player { Id = Guid.NewGuid(), FullName = "Below Floor" };
        await _playerRepository.AddPlayerAsync(belowFloorPlayer);
        // An otherwise-valid, distinct 5-row trophy count (would be
        // eligible under REQ-1501 alone).
        for (var i = 0; i < 5; i++)
            await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = belowFloorPlayer.Id, AttributeType = "trophy", AttributeValue = $"extra-trophy-{i}" });
        await SeedSingleValueAttributeAsync(belowFloorPlayer.Id, "international-caps", 5); // Below the default floor of 10.

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
        var usedPlayerIds = new[] { instance!.BaselinePlayerId }.Concat(instance.Comparators.Select(c => c.PlayerId)).ToList();
        Assert.That(usedPlayerIds, Does.Not.Contain(belowFloorPlayer.Id),
            "a player below REQ-1506's caps>=10 floor must never be selected, even with an otherwise-valid trophy value");
        Assert.That(usedPlayerIds, Is.EquivalentTo(eligiblePlayerIds));
    }

    [Test]
    public async Task REQ1506_GenerateInstanceAsync_PlayerWithNoCapsRecordedAtAll_IsExcluded_NotTreatedAsZero()
    {
        var eligiblePlayerIds = await SeedPlayersWithTrophyCountsAsync([1, 2, 3, 4], capsForEach: 10);
        var noCapsPlayer = new Player { Id = Guid.NewGuid(), FullName = "No Caps Recorded" };
        await _playerRepository.AddPlayerAsync(noCapsPlayer);
        await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = noCapsPlayer.Id, AttributeType = "trophy", AttributeValue = "t1" });
        await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = noCapsPlayer.Id, AttributeType = "trophy", AttributeValue = "t2" });
        await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = noCapsPlayer.Id, AttributeType = "trophy", AttributeValue = "t3" });
        await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = noCapsPlayer.Id, AttributeType = "trophy", AttributeValue = "t4" });
        await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute { PlayerId = noCapsPlayer.Id, AttributeType = "trophy", AttributeValue = "t5" });
        // No "international-caps" row at all — absent, not zero.

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
        var usedPlayerIds = new[] { instance!.BaselinePlayerId }.Concat(instance.Comparators.Select(c => c.PlayerId)).ToList();
        Assert.That(usedPlayerIds, Does.Not.Contain(noCapsPlayer.Id));
        Assert.That(usedPlayerIds, Is.EquivalentTo(eligiblePlayerIds));
    }

    [Test]
    public async Task REQ1506_GenerateInstanceAsync_FloorAppliesWhenActiveCategoryIsInternationalCapsItself()
    {
        // Two players with a distinct, otherwise-valid caps value below the
        // floor, plus enough players at/above the floor to complete a
        // sequence — the below-floor players must never appear even though
        // "international-caps" is itself the active category (REQ-1506's
        // own explicit "even when the active category is itself
        // international caps" rule).
        var eligiblePlayerIds = await SeedPlayersWithSingleValueAttributeAsync("international-caps", [10, 25, 50, 80]);
        var belowFloorIds = await SeedPlayersWithSingleValueAttributeAsync("international-caps", [3, 7]);

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance!.StatCategory, Is.EqualTo("international-caps"));
        var usedPlayerIds = new[] { instance.BaselinePlayerId }.Concat(instance.Comparators.Select(c => c.PlayerId)).ToList();
        foreach (var belowFloorId in belowFloorIds)
            Assert.That(usedPlayerIds, Does.Not.Contain(belowFloorId));
        Assert.That(usedPlayerIds, Is.EquivalentTo(eligiblePlayerIds));
    }

    [Test]
    public async Task REQ1506_GenerateInstanceAsync_FloorAppliesWhenActiveCategoryIsInternationalGoalsItself()
    {
        // The active category here is goals, not caps — REQ-1506's floor
        // must still be read from "international-caps" and applied.
        var eligiblePlayerIds = await SeedPlayersWithSingleValueAttributeAsync("international-goals", [1, 5, 10, 20]);
        foreach (var playerId in eligiblePlayerIds)
            await SeedSingleValueAttributeAsync(playerId, "international-caps", 10);

        var belowFloorPlayer = new Player { Id = Guid.NewGuid(), FullName = "Below Floor Goals" };
        await _playerRepository.AddPlayerAsync(belowFloorPlayer);
        await SeedSingleValueAttributeAsync(belowFloorPlayer.Id, "international-goals", 99); // Otherwise a perfectly valid, distinct goals value.
        await SeedSingleValueAttributeAsync(belowFloorPlayer.Id, "international-caps", 2); // Below the floor.

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
        Assert.That(instance!.StatCategory, Is.EqualTo("international-goals"));
        var usedPlayerIds = new[] { instance.BaselinePlayerId }.Concat(instance.Comparators.Select(c => c.PlayerId)).ToList();
        Assert.That(usedPlayerIds, Does.Not.Contain(belowFloorPlayer.Id));
        Assert.That(usedPlayerIds, Is.EquivalentTo(eligiblePlayerIds));
    }

    // ---- helpers --------------------------------------------------

    // Seeds one player per count in `counts`, each with exactly that many
    // distinct raw PlayerAttribute rows of `attributeType` (a "count"-shaped
    // category, ADR-0111 — "trophy", or "club" for the S231 removal proof
    // above) — e.g. counts [1, 2, 3] seeds 3 players with 1, 2, and 3
    // distinct attribute values respectively.
    //
    // capsForEach (S-231, REQ-1506): when non-null (the default, 10 —
    // exactly HigherLowerGenerationOptions.MinimumInternationalCaps'
    // default), every seeded player ALSO gets a single "international-caps"
    // PlayerAttribute row of this value, so the pool-wide floor added in
    // S-231 doesn't exclude fixtures that predate it and aren't testing
    // REQ-1506 themselves. Pass null to seed no caps at all (for a fixture
    // that deliberately wants every player excluded by the floor).
    // Returns the seeded players' ids.
    private async Task<List<Guid>> SeedPlayersWithTrophyCountsAsync(
        int[] counts, string attributeType = "trophy", int? capsForEach = 10)
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
            if (capsForEach is not null)
                await SeedSingleValueAttributeAsync(player.Id, "international-caps", capsForEach.Value);
            playerIds.Add(player.Id);
        }
        return playerIds;
    }

    // Seeds one player per value in `values`, each with exactly ONE raw
    // PlayerAttribute row of `attributeType` carrying that numeric value as
    // a string — the single-recorded-value shape ADR-0112 establishes for
    // "international-caps"/"international-goals" (never the multi-row
    // "count" shape SeedPlayersWithTrophyCountsAsync above uses). Returns
    // the seeded players' ids, in the same order as `values`.
    private async Task<List<Guid>> SeedPlayersWithSingleValueAttributeAsync(string attributeType, int[] values)
    {
        var playerIds = new List<Guid>();
        foreach (var value in values)
        {
            var player = new Player { Id = Guid.NewGuid(), FullName = $"Player {Guid.NewGuid()}" };
            await _playerRepository.AddPlayerAsync(player);
            await SeedSingleValueAttributeAsync(player.Id, attributeType, value);
            playerIds.Add(player.Id);
        }
        return playerIds;
    }

    private async Task SeedSingleValueAttributeAsync(Guid playerId, string attributeType, int value) =>
        await _attributeRepository.AddPlayerAttributeAsync(new PlayerAttribute
        {
            PlayerId = playerId,
            AttributeType = attributeType,
            AttributeValue = value.ToString(),
        });
}
