using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGPath.Tests;

// REQ-1201 (target-player eligibility) / REQ-1202 (round structure: N
// distinct-target puzzles) — docs/requirements-document.md §4.12. Follows
// this repo's no-mocking-framework pattern (docs/coding-guidelines.md
// "don't over-mock"), same as GridGameModuleTests: real, InMemory-backed
// repositories, no fakes.
//
// S-154 (pure refactor, no behavior change, docs/backlog.md Epic 17):
// REQ-1201's whole eligibility-rule coverage (the REQ1201_*/REQ1203_*/
// ADR0056_* tests directly exercising eligibility rules) moved to
// PathEligibilityServiceTests.cs alongside PathEligibilityService itself —
// see that file's own doc comment. This file now only exercises
// XGPathGameModule's own remaining responsibility as a thin IGameModule
// adapter: REQ-1202's puzzle-count/insufficient-pool orchestration,
// REQ-1208's cycle-rollover orchestration, ADR-0054's career-stint-refresh
// orchestration, REQ-1204's guess correctness resolution, REQ-1205's
// attempt cap, and the REQ-215/REQ-216 no-op passthroughs — none of which
// are themselves eligibility rules, so none of them moved. BuildModule
// composes a real PathEligibilityService (never a fake of the split-out
// class itself — this file's whole point is exercising the adapter's own
// wiring across the real thing), same "compose the real thing" precedent
// GridGameModuleTests.cs's own BuildModule follows post-S-119.
//
// GameKey_IsXgPath below is unchanged from S-080's scaffold.
// ScoreSubmissionAsync_ThrowsNotImplemented/GetMaxAttemptsForCellAsync_
// ThrowsNotImplemented were removed by S-082, which implements both methods
// for real (REQ-1204/REQ-1205) — this class carries the REQ1204-/
// REQ1205-named test coverage for them directly, below.
// GenerateInstanceAsync_ThrowsNotImplemented/GetCellIdsAsync_
// ThrowsNotImplemented were likewise replaced with real REQ1201/REQ1202-named
// tests by S-081, since those two methods are no longer stubs.
//
// REQ-112 pool-membership scope note: `Player` has no Gender field at all,
// and while Player.BirthYear now exists (REQ-1207/S-082, for xG Path's own
// age/birth-year clue, not for pool filtering — see Player.cs and
// PathEligibilityService.GetEligiblePlayerIdsAsync's own comment), every
// Player/PlayerCareerStint row already satisfies REQ-112 by construction,
// enforced upstream at Wikidata-query time (ADR-0025) — there is no runtime
// branch to exercise here, and no "outside the pool" fixture this schema
// can even represent. This criterion is confirmed by inspection, not by a
// test case, the same scope-note precedent S-079's own CHANGELOG entry
// used.
public class XGPathGameModuleTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPathInstanceRepository _pathInstanceRepository = null!;
    // S-106/S-107 (pure refactor): the sibling repositories carrying the
    // methods split out of the original, now-deleted IPlayerStoreRepository
    // — see ADR-0067. _playerCareerStintRepository carries
    // GetCareerStintCandidatePlayerIdsAsync/GetCareerStintsByPlayerIdsAsync.
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private IPlayerAliasRepository _playerAliasRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private XGPathGameModule _module = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _pathInstanceRepository = new PathInstanceRepository(_dbContext);
        _playerCareerStintRepository = new PlayerCareerStintRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _playerAliasRepository = new PlayerAliasRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _module = BuildModule();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // Deterministic stand-in for Random.Shared: always picks the first
    // remaining candidate. PickDistinct removes each pick from its working
    // list before the next call, so this still yields distinct results —
    // it just removes any dependency on RNG behavior from every test below.
    // Safe here because PickDistinct calls Next(maxValue) directly to pick
    // an index (a documented, always-overridable Random primitive) —
    // unlike GridGenerationServiceTests, which deliberately avoids pinning
    // Random.Shuffle's output this way, since Shuffle's internal algorithm
    // isn't part of Random's documented override contract the same way
    // Next(int) is (see that file's own header comment, ADR-0089).
    private sealed class SequentialRandom : Random
    {
        public override int Next(int maxValue) => 0;
    }

    // ADR-0054: field, not a local in BuildModule, so tests can inspect
    // Calls after GenerateInstanceAsync runs — same pattern the class-level
    // fields above already establish for the real repositories.
    private FakePlayerCareerStintRefreshService _careerStintRefreshService = null!;

    // ADR-0056: same "field, not a local" reasoning as
    // _careerStintRefreshService above — REQ1208_GenerateInstanceAsync_
    // PlayerDropsFromLiveEligiblePoolBetweenGenerations_StaleUsageRowNeverBlocksRolloverOrCausesError
    // below calls MarkUnfamiliar on it to simulate a player dropping out of
    // the live eligible pool between generations. The REQ-1201-named tests
    // that directly exercise ADR-0056's familiarity filter itself moved to
    // PathEligibilityServiceTests.cs (S-154) — see this file's own doc
    // comment.
    private FakePlayerFamiliarityService _playerFamiliarityService = null!;

    // REQ-1208/ADR-0058: optional timeProvider param, same "field, not a
    // local" precedent as _careerStintRefreshService/_playerFamiliarityService
    // above — mirrors GridGameModule's own injectable-TimeProvider
    // constructor param. Defaults to null (XGPathGameModule falls back to
    // TimeProvider.System itself), so every existing pre-REQ-1208 test in
    // this file is unaffected.
    private XGPathGameModule BuildModule(Random? random = null, TimeProvider? timeProvider = null)
    {
        _careerStintRefreshService = new FakePlayerCareerStintRefreshService();
        _playerFamiliarityService = new FakePlayerFamiliarityService();
        var eligibilityService = new PathEligibilityService(
            _playerCareerStintRepository, _playerRepository, _categoryValueRepository, _playerFamiliarityService);
        return new(_pathInstanceRepository, _playerRepository, _playerAliasRepository, eligibilityService,
            _careerStintRefreshService, random ?? new SequentialRandom(), timeProvider);
    }

    private PathTemplate SeedTemplate(int puzzleCount)
    {
        var template = new PathTemplate { Id = Guid.NewGuid(), PuzzleCount = puzzleCount };
        _dbContext.PathTemplates.Add(template);
        _dbContext.SaveChanges();
        return template;
    }

    // Idempotent (S-138): a no-op if `name` is already registered, rather
    // than throwing on ClubDefinition.Name's unique index — SeedEligiblePlayer
    // below now registers its own second seeded club on every call, and
    // many tests call it several times against the same base club name
    // (e.g. Enumerable.Range(0, 5).Select(i => SeedEligiblePlayer(...))), so
    // this must tolerate being asked to (re-)register the same name.
    private void SeedClub(string name)
    {
        if (_dbContext.ClubDefinitions.Any(c => c.Name == name))
            return;

        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = $"Qclub-{name}" });
        _dbContext.SaveChanges();
    }

    // REQ-1201/ADR-0073/S-137: birthYear defaults to 1990 (safely >=
    // PathEligibilityService.MinBirthYear's 1975 floor), not left at Player.
    // BirthYear's own null default — every pre-existing "this candidate
    // should be eligible" fixture in this file was written before the
    // BirthYear>=1975 filter existed and relies on SeedPlayer/
    // SeedEligiblePlayer producing an eligible player by default; leaving
    // BirthYear null here would fail-closed-exclude every one of them.
    // Overridable per test for the BirthYear-specific cases (1975 boundary,
    // 1974, null) this default is designed to keep untouched.
    //
    // REQ-1201/ADR-0079/S-161: position defaults to "Forward" for the exact
    // same reason birthYear defaults to 1990 above — every pre-existing
    // "this candidate should be eligible" fixture predates the
    // Position != null/empty floor and relies on this helper producing an
    // eligible player by default; leaving Position at its own null default
    // would fail-closed-exclude every one of them. Overridable per test for
    // the Position-specific cases (non-null control, null) this default is
    // designed to keep untouched.
    private Player SeedPlayer(string name, int? birthYear = 1990, string? position = "Forward")
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = name, WikidataQid = $"Qplayer-{name}", BirthYear = birthYear, Position = position };
        _dbContext.Players.Add(player);
        _dbContext.SaveChanges();
        return player;
    }

    // Seeds `stints` PlayerCareerStint rows for playerId. SequenceOrder is
    // irrelevant to eligibility (IsEligible reads only StartYear/EndYear/
    // ClubName/AppearanceCount), so every fixture row is left at 0 rather
    // than replicating AddCareerStintsAsync's own re-sequencing logic here.
    // AppearanceCount defaults to null ("unknown"), which ADR-0047 treats
    // as passing the appearance-count check — most fixtures don't need to
    // set it explicitly.
    private void SeedStints(Guid playerId, params (int StartYear, int? EndYear, string ClubName)[] stints)
    {
        SeedStints(playerId, stints.Select(s => (s.StartYear, s.EndYear, s.ClubName, (int?)null)).ToArray());
    }

    private void SeedStints(Guid playerId, params (int StartYear, int? EndYear, string ClubName, int? AppearanceCount)[] stints)
    {
        foreach (var (startYear, endYear, clubName, appearanceCount) in stints)
        {
            _dbContext.PlayerCareerStints.Add(new PlayerCareerStint
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                ClubName = clubName,
                StartYear = startYear,
                EndYear = endYear,
                SequenceOrder = 0,
                AppearanceCount = appearanceCount,
            });
        }
        _dbContext.SaveChanges();
    }

    // S-162/ADR-0081: same shape as SeedStints above, but with explicit,
    // caller-controlled SequenceOrder values (0, 1, 2, ... matching each
    // tuple's position in the params array) rather than SeedStints' fixed
    // SequenceOrder=0 for every row. PathCareerStintFilter.CollapseAdjacentSameClub
    // defines "adjacent" purely as "next to each other after sorting by
    // SequenceOrder" (its own doc comment's precondition) — the plain
    // SeedStints helper's "SequenceOrder is irrelevant to eligibility" claim
    // stopped being true the moment collapse joined
    // GetEligiblePlayerIdsAsync's filter chain, so collapse-specific
    // fixtures need real, distinct SequenceOrder values to make "adjacent in
    // this array" and "adjacent after the module's own OrderBy(SequenceOrder)"
    // the same thing, rather than relying on the InMemory provider's
    // undocumented (and previously irrelevant) row-return order.
    private void SeedStintsOrdered(Guid playerId, params (int StartYear, int? EndYear, string ClubName, int? AppearanceCount)[] stints)
    {
        for (var i = 0; i < stints.Length; i++)
        {
            var (startYear, endYear, clubName, appearanceCount) = stints[i];
            _dbContext.PlayerCareerStints.Add(new PlayerCareerStint
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                ClubName = clubName,
                StartYear = startYear,
                EndYear = endYear,
                SequenceOrder = i,
                AppearanceCount = appearanceCount,
            });
        }
        _dbContext.SaveChanges();
    }

    // Baseline "definitely eligible" fixture (REQ-1201/ADR-0074/S-138): 3
    // well-ordered stints, at 2 DISTINCT seeded clubs (seededClubName and a
    // second club derived from it, "{seededClubName} 2") plus 1 unseeded
    // club — satisfies the new "≥2 distinct qualifying seeded clubs" rule.
    // The second club is registered here, not by the caller, via the now-
    // idempotent SeedClub above — every existing call site in this file
    // only ever registers `seededClubName` itself (usually once, via a
    // single top-of-test SeedClub("Seeded FC") call reused across several
    // SeedEligiblePlayer calls), so deriving and self-registering the
    // second club here keeps every one of those call sites unchanged.
    // birthYear forwards to SeedPlayer's own default/override
    // (REQ-1201/ADR-0073/S-137) — same overridable-per-case shape. position
    // likewise forwards to SeedPlayer's own default/override
    // (REQ-1201/ADR-0079/S-161).
    private Player SeedEligiblePlayer(string name, string seededClubName, int? birthYear = 1990, string? position = "Forward")
    {
        var secondSeededClubName = $"{seededClubName} 2";
        SeedClub(secondSeededClubName);

        var player = SeedPlayer(name, birthYear, position);
        SeedStints(player.Id,
            (2010, 2013, seededClubName),
            (2013, 2016, secondSeededClubName),
            (2016, null, "Another Unseeded Club"));
        return player;
    }

    private async Task<List<Guid>> GetTargetPlayerIdsAsync(Guid instanceId)
    {
        var instance = await _pathInstanceRepository.GetInstanceByIdAsync(instanceId);
        return instance!.Puzzles.Select(p => p.TargetPlayerId).ToList();
    }

    // S-154 (pure refactor, docs/backlog.md Epic 17): every REQ-1201/
    // REQ-1203/ADR-0056 test directly exercising an eligibility rule
    // (structural checks, BirthYear/Position floors, familiarity filter,
    // including the "baseline + 1 violating candidate" rejection-test
    // technique those tests used) moved to PathEligibilityServiceTests.cs,
    // reshaped to assert directly on PathEligibilityService.
    // GetEligiblePlayerIdsAsync's own returned id list — see that file's
    // own doc comment. What remains below is orchestration that isn't
    // itself an eligibility rule: REQ-1202's puzzle-count/insufficient-pool
    // behavior, ADR-0054's career-stint-refresh call, and REQ-1208's cycle
    // rollover.

    [Test]
    public async Task REQ1202_GenerateInstanceAsync_GeneratesExactlyNDistinctTargetPuzzles()
    {
        SeedClub("Seeded FC");
        var players = Enumerable.Range(0, 5)
            .Select(i => SeedEligiblePlayer($"Eligible{i}", "Seeded FC"))
            .ToList();

        var template = SeedTemplate(3);

        var instance = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var targets = await GetTargetPlayerIdsAsync(instance.Id);

        Assert.That(targets, Has.Count.EqualTo(3));
        Assert.That(targets.Distinct().Count(), Is.EqualTo(3));
        Assert.That(targets, Is.SubsetOf(players.Select(p => p.Id)));
    }

    // ADR-0054: GenerateInstanceAsync must refresh exactly the puzzles' own
    // target players' career data — not the whole eligible pool, not zero
    // players — and must do so with the SAME ids GetTargetPlayerIdsAsync
    // reports, confirming the refresh happens before/independent of which
    // ids end up on the persisted PathPuzzle rows.
    [Test]
    public async Task ADR0054_GenerateInstanceAsync_RefreshesCareerStintsForExactlyTheSelectedTargets()
    {
        SeedClub("Seeded FC");
        var players = Enumerable.Range(0, 5)
            .Select(i => SeedEligiblePlayer($"Eligible{i}", "Seeded FC"))
            .ToList();

        var template = SeedTemplate(3);

        var instance = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var targets = await GetTargetPlayerIdsAsync(instance.Id);

        Assert.That(_careerStintRefreshService.Calls, Has.Count.EqualTo(1),
            "exactly one refresh call per generated instance, not one per target player");
        Assert.That(_careerStintRefreshService.Calls[0], Is.EquivalentTo(targets));
        Assert.That(_careerStintRefreshService.Calls[0], Has.Count.EqualTo(3),
            "only the selected targets, never the whole 5-player eligible pool");
    }

    [Test]
    public void REQ1202_GenerateInstanceAsync_InsufficientEligiblePool_ThrowsPathGenerationException()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");

        var template = SeedTemplate(3); // only 2 eligible players exist

        var ex = Assert.ThrowsAsync<PathGenerationException>(
            async () => await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));
        Assert.That(ex!.Message, Does.Contain("Not enough eligible target players"));
    }

    [Test]
    public void REQ1202_GenerateInstanceAsync_UnknownTemplateId_ThrowsPathGenerationException()
    {
        Assert.ThrowsAsync<PathGenerationException>(
            async () => await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = Guid.NewGuid() }));
    }

    [Test]
    public async Task REQ1202_GetCellIdsAsync_ReturnsOnePuzzleIdPerGeneratedPuzzle()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");
        SeedEligiblePlayer("Eligible3", "Seeded FC");

        var template = SeedTemplate(3);
        var instance = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });

        var cellIds = await _module.GetCellIdsAsync(instance.Id);
        var persistedPuzzleIds = (await _pathInstanceRepository.GetInstanceByIdAsync(instance.Id))!
            .Puzzles.Select(p => p.Id).ToList();

        Assert.That(cellIds, Has.Count.EqualTo(3));
        Assert.That(cellIds, Is.EquivalentTo(persistedPuzzleIds));
    }

    [Test]
    public void REQ1202_GetCellIdsAsync_UnknownInstanceId_ThrowsPathScoringException()
    {
        Assert.ThrowsAsync<PathScoringException>(async () => await _module.GetCellIdsAsync(Guid.NewGuid()));
    }

    // ---- REQ-1208/ADR-0058: target selection does not repeat until the ----
    // eligible pool has cycled ------------------------------------------

    [Test]
    public async Task REQ1208_GenerateInstanceAsync_SelectedTargetsRecordedAsUsedInCurrentCycle()
    {
        SeedClub("Seeded FC");
        var players = Enumerable.Range(0, 5)
            .Select(i => SeedEligiblePlayer($"Eligible{i}", "Seeded FC"))
            .ToList();
        var template = SeedTemplate(3);

        var instance = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var targets = await GetTargetPlayerIdsAsync(instance.Id);

        var cycleState = await _pathInstanceRepository.GetCycleStateAsync();
        Assert.That(cycleState, Is.Not.Null, "GetOrCreateCycleStateAsync must create the very first cycle row (CycleNumber 1) on this, the first-ever generation");
        Assert.That(cycleState!.CycleNumber, Is.EqualTo(1));
        Assert.That(cycleState.ObservedPoolSize, Is.EqualTo(5), "the eligible pool size as observed at THIS generation");
        Assert.That(cycleState.UsedInCycleCount, Is.EqualTo(3));

        var usedPlayerIds = await _pathInstanceRepository.GetUsedPlayerIdsInCycleAsync(1);
        Assert.That(usedPlayerIds, Is.EquivalentTo(targets),
            "each selected target must be recorded as used in the current cycle at the same time it's persisted as a puzzle's target");
        Assert.That(players.Select(p => p.Id), Is.SupersetOf(usedPlayerIds));
    }

    [Test]
    public async Task REQ1208_GenerateInstanceAsync_PlayerAlreadyUsedInCurrentCycle_ExcludedFromSelectionOnLaterGenerationWithinSameCycle()
    {
        // 7 eligible players, PuzzleCount 3: after the first generation uses
        // 3, 4 remain unused-in-cycle — still >= PuzzleCount, so the second
        // generation below must NOT trigger a rollover, isolating this test
        // to the "excluded within the same cycle" behavior only.
        SeedClub("Seeded FC");
        var players = Enumerable.Range(0, 7)
            .Select(i => SeedEligiblePlayer($"Eligible{i}", "Seeded FC"))
            .ToList();
        var template = SeedTemplate(3);

        var instance1 = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var targets1 = await GetTargetPlayerIdsAsync(instance1.Id);

        var instance2 = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var targets2 = await GetTargetPlayerIdsAsync(instance2.Id);

        Assert.That(targets2, Has.Count.EqualTo(3));
        Assert.That(targets2.Intersect(targets1), Is.Empty,
            "a player already used as a target in the current cycle must never be reselected by a later generation in the same cycle");
        Assert.That(targets1.Concat(targets2), Is.SubsetOf(players.Select(p => p.Id)));

        var cycleState = await _pathInstanceRepository.GetCycleStateAsync();
        Assert.That(cycleState!.CycleNumber, Is.EqualTo(1), "7 eligible players, 2x3 used — never drops below PuzzleCount, so no rollover here");
        Assert.That(cycleState.UsedInCycleCount, Is.EqualTo(6));
    }

    [Test]
    public async Task REQ1208_GenerateInstanceAsync_CycleRollsOverWhenUnusedInCycleCountDropsBelowPuzzleCount_MakingEveryEligiblePlayerSelectableAgain()
    {
        // Only 4 eligible players, PuzzleCount 3: after the first generation
        // uses 3, only 1 remains unused-in-cycle — below PuzzleCount(3) — so
        // the second generation below must roll the cycle over rather than
        // aborting for lack of candidates.
        var manualTimeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
        _module = BuildModule(timeProvider: manualTimeProvider);

        SeedClub("Seeded FC");
        var players = Enumerable.Range(0, 4)
            .Select(i => SeedEligiblePlayer($"Eligible{i}", "Seeded FC"))
            .ToList();
        var template = SeedTemplate(3);

        var instance1 = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var targets1 = await GetTargetPlayerIdsAsync(instance1.Id);

        manualTimeProvider.Advance(TimeSpan.FromHours(1));
        var rolloverMoment = manualTimeProvider.GetUtcNow();

        var instance2 = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var targets2 = await GetTargetPlayerIdsAsync(instance2.Id);

        Assert.That(targets2, Has.Count.EqualTo(3));
        Assert.That(targets2.Distinct().Count(), Is.EqualTo(3), "REQ-1202's distinctness guarantee within one instance is unaffected by a rollover");
        Assert.That(targets2, Is.SubsetOf(players.Select(p => p.Id)), "every eligible player, not just the previously-unused one, must be selectable again post-rollover");
        // Pigeonhole: only 1 of the 4 eligible players was NOT used in cycle
        // 1 — any 3-of-4 selection after rollover must therefore include at
        // least one player used moments ago in the just-completed cycle,
        // regardless of PickDistinct's own random order.
        Assert.That(targets2.Intersect(targets1), Is.Not.Empty,
            "a player used moments ago in the just-completed cycle must be selectable again once the cycle rolls over");

        var cycleState = await _pathInstanceRepository.GetCycleStateAsync();
        Assert.That(cycleState!.CycleNumber, Is.EqualTo(2), "the remaining-unused-in-cycle count (1) dropped below PuzzleCount(3), so this generation must roll the cycle over");
        Assert.That(cycleState.UsedInCycleCount, Is.EqualTo(3), "reset to 0 by the rollover, then incremented by this generation's own 3 selections");
        Assert.That(cycleState.ObservedPoolSize, Is.EqualTo(4));
        Assert.That(cycleState.LastCycleCompletedAt, Is.EqualTo(rolloverMoment.UtcDateTime));
    }

    [Test]
    public async Task REQ1208_GenerateInstanceAsync_PlayerDropsFromLiveEligiblePoolBetweenGenerations_StaleUsageRowNeverBlocksRolloverOrCausesError()
    {
        // 5 eligible players, PuzzleCount 3. After the first generation, one
        // of the 3 just-used targets drops out of the live eligible pool
        // (e.g. no longer meets ADR-0056's familiarity threshold) — leaving
        // 4 total eligible players, of which 2 are already recorded as used
        // in the current cycle, so only 2 remain unused-in-cycle: below
        // PuzzleCount(3), so the second generation below must still roll the
        // cycle over correctly despite the dropped player's now-stale usage
        // row, and must never throw.
        SeedClub("Seeded FC");
        var players = Enumerable.Range(0, 5)
            .Select(i => SeedEligiblePlayer($"Eligible{i}", "Seeded FC"))
            .ToList();
        var template = SeedTemplate(3);

        var instance1 = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var targets1 = await GetTargetPlayerIdsAsync(instance1.Id);
        Assert.That(targets1, Has.Count.EqualTo(3));

        var droppedPlayerId = targets1[0];
        _playerFamiliarityService.MarkUnfamiliar(droppedPlayerId);

        GameInstance instance2 = null!;
        List<Guid> targets2 = null!;
        Assert.DoesNotThrowAsync(async () =>
        {
            instance2 = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
            targets2 = await GetTargetPlayerIdsAsync(instance2.Id);
        }, "a stale usage row for a player who has since left the live eligible pool must never cause a generation failure");

        Assert.That(targets2, Has.Count.EqualTo(3));
        Assert.That(targets2.Distinct().Count(), Is.EqualTo(3));
        Assert.That(targets2, Does.Not.Contain(droppedPlayerId), "the now-ineligible player can never be selected again");

        var cycleState = await _pathInstanceRepository.GetCycleStateAsync();
        Assert.That(cycleState!.CycleNumber, Is.EqualTo(2),
            "of the 4 still-eligible players, 2 were already used this cycle, leaving 2 unused-in-cycle — below PuzzleCount(3) — so this must roll over correctly even though the dropped player's own usage row is now stale");
        Assert.That(cycleState.ObservedPoolSize, Is.EqualTo(4), "the dropped player is excluded from the pool size observed at this generation");
    }

    // REQ-1208's own "does not change REQ-1202's existing insufficient-total-
    // pool abort" criterion: a second generation whose template needs more
    // targets than the total live eligible pool still throws
    // PathGenerationException, and does so BEFORE any cycle-state mutation —
    // proven here by seeding an already-existing cycle (from a first,
    // successful generation) and asserting it is completely untouched by the
    // second, aborted attempt.
    [Test]
    public async Task REQ1202_GenerateInstanceAsync_InsufficientTotalEligiblePool_ThrowsPathGenerationException_UnaffectedByExistingCycleState()
    {
        SeedClub("Seeded FC");
        var players = Enumerable.Range(0, 5)
            .Select(i => SeedEligiblePlayer($"Eligible{i}", "Seeded FC"))
            .ToList();
        Assert.That(players, Has.Count.EqualTo(5), "sanity check: exactly 5 eligible players total, regardless of cycle state");
        var smallTemplate = SeedTemplate(3);
        await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = smallTemplate.Id });

        var cycleStateBefore = await _pathInstanceRepository.GetCycleStateAsync();
        Assert.That(cycleStateBefore!.CycleNumber, Is.EqualTo(1));
        Assert.That(cycleStateBefore.UsedInCycleCount, Is.EqualTo(3));

        var tooLargeTemplate = SeedTemplate(10); // only 5 eligible players exist in total, regardless of cycle state
        Assert.ThrowsAsync<PathGenerationException>(
            async () => await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = tooLargeTemplate.Id }));

        var cycleStateAfter = await _pathInstanceRepository.GetCycleStateAsync();
        Assert.That(cycleStateAfter!.CycleNumber, Is.EqualTo(1), "an aborted generation must never mutate the persisted cycle state");
        Assert.That(cycleStateAfter.UsedInCycleCount, Is.EqualTo(3));
    }

    [Test]
    public void GameKey_IsXgPath()
    {
        Assert.That(_module.GameKey, Is.EqualTo("xg-path"));
        Assert.That(XGPathGameModule.XGPathGameKey, Is.EqualTo("xg-path"));
    }

    // ---- REQ-1204/REQ-1205 (S-082) fixtures --------------------------------
    // Builds a PathInstance/PathPuzzle directly via the repository (like
    // GridGameModuleTests' own SeedGridInstanceAsync), rather than going
    // through GenerateInstanceAsync — gives these tests exact, explicit
    // control over which player is the puzzle's target, independent of
    // REQ-1201's eligibility rules or REQ-1202's random selection.
    private async Task<(Guid InstanceId, Guid PuzzleId, Guid TargetPlayerId)> SeedPathInstanceAsync(Guid targetPlayerId)
    {
        var instanceId = Guid.NewGuid();
        var puzzleId = Guid.NewGuid();
        var instance = new PathInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Puzzles = [new PathPuzzle { Id = puzzleId, PathInstanceId = instanceId, TargetPlayerId = targetPlayerId }],
        };
        await _pathInstanceRepository.AddInstanceAsync(instance);
        return (instanceId, puzzleId, targetPlayerId);
    }

    // ---- REQ-1204: guess correctness resolution ----------------------------
    // "correctness is a direct PlayerId match, not a category check" —
    // xG Path has no category concept at all, unlike GridGameModule's own
    // REQ203_ScoreSubmissionAsync_* tests, which always seed a cell with two
    // categories a candidate must satisfy.

    [Test]
    public async Task REQ1204_ScoreSubmissionAsync_ResolvedCandidateIsTheTarget_ReturnsCorrectWithPlayerAnswerId()
    {
        var target = SeedPlayer("Kylian Mbappe");
        var (instanceId, puzzleId, targetPlayerId) = await SeedPathInstanceAsync(target.Id);

        var result = await _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(puzzleId, "Kylian Mbappe"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(targetPlayerId));
    }

    [Test]
    public async Task REQ1204_ScoreSubmissionAsync_ResolvedCandidateIsARealPlayerButNotTheTarget_ReturnsIncorrect()
    {
        // No category-membership check exists for this game (unlike
        // GridGameModule's REQ-203 check) — a guess that resolves to a real,
        // known player is still incorrect unless that player IS this
        // puzzle's own target.
        var target = SeedPlayer("Kylian Mbappe");
        var someoneElse = SeedPlayer("Erling Haaland");
        var (instanceId, puzzleId, _) = await SeedPathInstanceAsync(target.Id);

        var result = await _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(puzzleId, "Erling Haaland"));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.PlayerAnswerId, Is.Null);
        Assert.That(someoneElse.Id, Is.Not.EqualTo(target.Id), "sanity check: the two seeded players must be genuinely distinct");
    }

    [Test]
    public async Task REQ1204_ScoreSubmissionAsync_GuessDoesNotResolveToAnyPlayerNameIndexCandidate_ReturnsIncorrect()
    {
        var target = SeedPlayer("Kylian Mbappe");
        var (instanceId, puzzleId, _) = await SeedPathInstanceAsync(target.Id);

        var result = await _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(puzzleId, "Nobody Real At All"));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.PlayerAnswerId, Is.Null);
    }

    [Test]
    public async Task REQ1204_ScoreSubmissionAsync_AliasMatchingTheTarget_ReturnsCorrect()
    {
        // REQ-1204: "using the same name-matching/autocomplete pipeline
        // (PlayerNameIndex, ADR-0007) xG Grid guesses already use" — the
        // alias path is checked only when the primary-name path finds no
        // candidate (IPlayerStoreRepository.GetPlayersByNormalizedAliasAsync's
        // own doc comment).
        var target = SeedPlayer("Edson Arantes do Nascimento");
        await _playerAliasRepository.AddPlayerAliasAsync(new PlayerAlias
        {
            PlayerId = target.Id,
            Alias = "Pele",
            NormalizedAlias = PlayerNameNormalizer.Normalize("Pele"),
        });
        var (instanceId, puzzleId, targetPlayerId) = await SeedPathInstanceAsync(target.Id);

        var result = await _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(puzzleId, "Pele"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(targetPlayerId));
    }

    [Test]
    public async Task REQ1204_ScoreSubmissionAsync_NameResolvesToMultiplePlayersAndTargetIsOneOfThem_ReturnsCorrectWithTargetPlayerAnswerId()
    {
        // Pins down XGPathGameModule.ScoreSubmissionAsync's own structural
        // claim, in its comment above: unlike GridGameModule's REQ-209
        // disambiguation, an xG Path puzzle's correctness only ever cares
        // whether the ONE specific target is among the name-matched
        // candidates — a second, unrelated real player who happens to
        // share the target's exact name must not trigger a disambiguation
        // prompt (there is no DisambiguationCandidates equivalent set here)
        // and must not change the outcome.
        var target = SeedPlayer("Alex Multi");
        // A second real Player row sharing the identical FullName/
        // normalized name as the target, but NOT the target itself.
        // Constructed directly here (rather than via SeedPlayer, which
        // keys WikidataQid off `name` and would collide on a repeat call)
        // so it gets its own distinct WikidataQid — the same "two players,
        // one shared name" fixture technique GridGameModuleTests' own
        // REQ-209 tests use via SeedPlayerAsync's per-call Guid.NewGuid()
        // WikidataQid suffix.
        _dbContext.Players.Add(new Player { Id = Guid.NewGuid(), FullName = "Alex Multi", WikidataQid = "Qplayer-Alex-Multi-2" });
        _dbContext.SaveChanges();
        var (instanceId, puzzleId, targetPlayerId) = await SeedPathInstanceAsync(target.Id);

        var result = await _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(puzzleId, "Alex Multi"));

        Assert.That(result.IsCorrect, Is.True);
        Assert.That(result.PlayerAnswerId, Is.EqualTo(targetPlayerId));
        Assert.That(result.DisambiguationCandidates, Is.Null,
            "xG Path has no REQ-209-style disambiguation — a same-named non-target candidate resolving alongside the real target changes nothing about the outcome");
    }

    [Test]
    public async Task REQ1204_ScoreSubmissionAsync_NameResolvesToMultiplePlayersAndTargetIsNoneOfThem_ReturnsIncorrect()
    {
        var target = SeedPlayer("Kylian Mbappe");
        // Two same-named candidates, neither of which is this puzzle's
        // target — same duplicate-FullName fixture technique as the test
        // above.
        SeedPlayer("Alex Multi");
        _dbContext.Players.Add(new Player { Id = Guid.NewGuid(), FullName = "Alex Multi", WikidataQid = "Qplayer-Alex-Multi-2" });
        _dbContext.SaveChanges();
        var (instanceId, puzzleId, _) = await SeedPathInstanceAsync(target.Id);

        var result = await _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(puzzleId, "Alex Multi"));

        Assert.That(result.IsCorrect, Is.False);
        Assert.That(result.PlayerAnswerId, Is.Null);
    }

    [Test]
    public void ScoreSubmissionAsync_UnknownInstanceId_ThrowsPathScoringException()
    {
        Assert.ThrowsAsync<PathScoringException>(
            async () => await _module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new GuessSubmission(Guid.NewGuid(), "Anyone")));
    }

    [Test]
    public async Task ScoreSubmissionAsync_UnknownPuzzleId_ThrowsPathScoringException()
    {
        var target = SeedPlayer("Kylian Mbappe");
        var (instanceId, _, _) = await SeedPathInstanceAsync(target.Id);

        Assert.ThrowsAsync<PathScoringException>(
            async () => await _module.ScoreSubmissionAsync(instanceId, Guid.NewGuid(), new GuessSubmission(Guid.NewGuid(), "Anyone")));
    }

    // ---- REQ-1205: per-puzzle attempt cap, fixed at 7 ----------------------

    [Test]
    public async Task REQ1205_GetMaxAttemptsForCellAsync_ReturnsSeven()
    {
        var target = SeedPlayer("Kylian Mbappe");
        var (instanceId, puzzleId, _) = await SeedPathInstanceAsync(target.Id);

        var maxAttempts = await _module.GetMaxAttemptsForCellAsync(instanceId, puzzleId);

        Assert.That(maxAttempts, Is.EqualTo(7));
    }

    [Test]
    public async Task REQ1205_GetMaxAttemptsForCellAsync_ReturnsSeven_ForPuzzlesWithDifferentTargetStintCounts_NeverGridsFixedTwo()
    {
        // REQ-1205: the resolved cap is a fixed 7 for every xG Path puzzle,
        // regardless of its target's own stint count N — unlike
        // GridGameModule's fixed 2, and unlike a value that would vary by N.
        var shortCareerPlayer = SeedPlayer("ShortCareer");
        SeedStints(shortCareerPlayer.Id,
            (2010, 2013, "Club A"), (2013, 2016, "Club B"), (2016, (int?)null, "Club C"));
        var longCareerPlayer = SeedPlayer("LongCareer");
        SeedStints(longCareerPlayer.Id,
            Enumerable.Range(0, 11).Select(i => (2000 + i, (int?)(2001 + i), $"Club {i}")).ToArray());

        var (shortInstanceId, shortPuzzleId, _) = await SeedPathInstanceAsync(shortCareerPlayer.Id);
        var (longInstanceId, longPuzzleId, _) = await SeedPathInstanceAsync(longCareerPlayer.Id);

        var shortCareerMaxAttempts = await _module.GetMaxAttemptsForCellAsync(shortInstanceId, shortPuzzleId);
        var longCareerMaxAttempts = await _module.GetMaxAttemptsForCellAsync(longInstanceId, longPuzzleId);

        Assert.That(shortCareerMaxAttempts, Is.EqualTo(7));
        Assert.That(longCareerMaxAttempts, Is.EqualTo(7));
        Assert.That(shortCareerMaxAttempts, Is.Not.EqualTo(2), "must never be xG Grid's fixed cap of 2");
    }

    // ---- REQ-215/ADR-0052 (S-089, architecture-review fix): xG Path has no
    // row/col category concept -------------------------------------------

    [Test]
    public async Task REQ215_GetCellCategoryTypesAsync_ThrowsNotSupportedException_XGPathHasNoCategoryConcept()
    {
        var target = SeedPlayer("Kylian Mbappe");
        var (instanceId, puzzleId, _) = await SeedPathInstanceAsync(target.Id);

        // A puzzle's correctness is a single fixed TargetPlayerId, not two
        // independent category axes — this game genuinely has nothing
        // meaningful to return, even for a puzzleId that does resolve to a
        // real PathPuzzle within the given instance (see this method's own
        // doc comment on XGPathGameModule for the full reasoning).
        Assert.ThrowsAsync<NotSupportedException>(
            async () => await _module.GetCellCategoryTypesAsync(instanceId, puzzleId));
    }

    // ---- REQ-216/ADR-0057: xG Path is out of scope for this feature ------

    [Test]
    public async Task REQ216_ResolveWrongGuessPlayerAsync_ReturnsNull_XGPathIsOutOfScope()
    {
        // docs/backlog.md S-094 scopes REQ-216 to xG Grid only — xG Path's
        // own incorrect-guess display must be completely unaffected.
        var result = await _module.ResolveWrongGuessPlayerAsync(Guid.NewGuid(), "Some Wrong Guess");

        Assert.That(result, Is.Null);
    }
}
