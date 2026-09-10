using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGHigherLower.Tests;

// REQ-1501 (stat-category/player-value eligibility), REQ-1502 (comparator
// eligibility — no exact ties, no repeated player, fail closed), REQ-1503
// (Round generation — one fixed category/baseline/comparator sequence) —
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
        // ties possible at all.
        await SeedPlayersWithAttributeCountsAsync("trophy", [1, 2, 3, 4, 5]);

        var result = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(result, Is.Not.Null);
        var instance = await _instanceRepository.GetInstanceByIdAsync(result.Id);
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

        var first = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });
        var second = await _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() });

        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null);
        Assert.That(second.Id, Is.Not.EqualTo(first.Id));
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
        // seeded for either candidate category ("trophy"/"club") — neither
        // category can possibly work.

        Assert.ThrowsAsync<HigherLowerGenerationException>(
            () => _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
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
            () => _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    [Test]
    public async Task REQ1502_GenerateInstanceAsync_TieExclusion_AbortedGeneration_NeverPersistsAnything()
    {
        await SeedPlayersWithAttributeCountsAsync("trophy", [2, 2, 2, 2, 2]);

        Assert.ThrowsAsync<HigherLowerGenerationException>(
            () => _service.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));

        Assert.That(await _dbContext.HigherLowerInstances.CountAsync(), Is.EqualTo(0),
            "an aborted generation must not persist a degraded (shorter-than-configured or otherwise invalid) instance");
    }

    // ---- helpers --------------------------------------------------

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
