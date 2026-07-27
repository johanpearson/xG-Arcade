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
// ScoreSubmissionAsync_ThrowsNotImplemented/GetMaxAttemptsForCellAsync_
// ThrowsNotImplemented/GameKey_IsXgPath below are unchanged from S-080's
// scaffold — REQ-1204/REQ-1205 are still S-082, not this story.
// GenerateInstanceAsync_ThrowsNotImplemented/GetCellIdsAsync_
// ThrowsNotImplemented are replaced with real REQ1201/REQ1202-named tests,
// since those two methods are no longer stubs.
//
// REQ-112 pool-membership scope note: `Player` has no BirthYear/Gender
// field at all (see Player.cs and XGPathGameModule.
// GetEligiblePlayerIdsAsync's own comment) — every Player/PlayerCareerStint
// row already satisfies REQ-112 by construction, enforced upstream at
// Wikidata-query time (ADR-0025). There is no runtime branch to exercise
// here, and no "outside the pool" fixture this schema can even represent —
// this criterion is confirmed by inspection, not by a test case, the same
// scope-note precedent S-079's own CHANGELOG entry used.
public class XGPathGameModuleTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IPathInstanceRepository _pathInstanceRepository = null!;
    private IPlayerStoreRepository _playerStoreRepository = null!;
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
        _playerStoreRepository = new PlayerStoreRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _module = BuildModule();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    // Deterministic stand-in for Random.Shared: always picks the first
    // remaining candidate. PickDistinct removes each pick from its working
    // list before the next call, so this still yields distinct results —
    // it just removes any dependency on RNG behavior from every test below,
    // the same "pin selection for tests" precedent GridGameModuleTests'
    // FixedChoiceRandom sets for GridGameModule's own Random? param.
    private sealed class SequentialRandom : Random
    {
        public override int Next(int maxValue) => 0;
    }

    private XGPathGameModule BuildModule(Random? random = null) =>
        new(_pathInstanceRepository, _playerStoreRepository, _categoryValueRepository, random ?? new SequentialRandom());

    private PathTemplate SeedTemplate(int puzzleCount)
    {
        var template = new PathTemplate { Id = Guid.NewGuid(), PuzzleCount = puzzleCount };
        _dbContext.PathTemplates.Add(template);
        _dbContext.SaveChanges();
        return template;
    }

    private void SeedClub(string name)
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = $"Qclub-{name}" });
        _dbContext.SaveChanges();
    }

    private Player SeedPlayer(string name)
    {
        var player = new Player { Id = Guid.NewGuid(), FullName = name, WikidataQid = $"Qplayer-{name}" };
        _dbContext.Players.Add(player);
        _dbContext.SaveChanges();
        return player;
    }

    // Seeds `stints` PlayerCareerStint rows for playerId. SequenceOrder is
    // irrelevant to eligibility (IsEligible reads only StartYear/EndYear/
    // ClubName), so every fixture row is left at 0 rather than replicating
    // AddCareerStintsAsync's own re-sequencing logic here.
    private void SeedStints(Guid playerId, params (int StartYear, int? EndYear, string ClubName)[] stints)
    {
        foreach (var (startYear, endYear, clubName) in stints)
        {
            _dbContext.PlayerCareerStints.Add(new PlayerCareerStint
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                ClubName = clubName,
                StartYear = startYear,
                EndYear = endYear,
                SequenceOrder = 0,
            });
        }
        _dbContext.SaveChanges();
    }

    // Baseline "definitely eligible" fixture: 3 well-ordered stints, one at
    // a seeded club.
    private Player SeedEligiblePlayer(string name, string seededClubName)
    {
        var player = SeedPlayer(name);
        SeedStints(player.Id,
            (2010, 2013, seededClubName),
            (2013, 2016, "Some Unseeded Club"),
            (2016, null, "Another Unseeded Club"));
        return player;
    }

    private async Task<List<Guid>> GetTargetPlayerIdsAsync(Guid instanceId)
    {
        var instance = await _pathInstanceRepository.GetInstanceByIdAsync(instanceId);
        return instance!.Puzzles.Select(p => p.TargetPlayerId).ToList();
    }

    // Every rejection test below uses the same technique: seed exactly
    // (baselineCount) genuinely-eligible players plus ONE additional
    // candidate carrying the single violation under test, then set
    // PuzzleCount to baselineCount + 1 (i.e. exactly the pool size IF the
    // violating candidate were wrongly counted as eligible). This makes the
    // assertion independent of PickDistinct's random selection order: if
    // the module correctly excludes the violator, the eligible pool is one
    // short of PuzzleCount and generation throws; if it incorrectly
    // includes the violator, the pool exactly matches PuzzleCount and every
    // candidate (violator included) would have to be selected, so a bug
    // can't "get lucky" and pass by chance.
    [Test]
    public void REQ1201_GenerateInstanceAsync_CandidateWithFewerThanThreeStints_NeverSelected()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");

        var tooFewStints = SeedPlayer("TwoStints");
        SeedStints(tooFewStints.Id, (2010, 2013, "Seeded FC"), (2013, null, "Other FC")); // only 2 rows

        var template = SeedTemplate(3);

        Assert.ThrowsAsync<PathGenerationException>(
            async () => await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));
    }

    [Test]
    public void REQ1201_GenerateInstanceAsync_CandidateWithUndeterminableStintOrder_NeverSelected()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");

        // Two stints share the identical (StartYear=2010, EndYear=2013)
        // pair — their relative chronological order can't be derived from
        // the dates themselves, only from write-order SequenceOrder.
        var undeterminable = SeedPlayer("DuplicateDates");
        SeedStints(undeterminable.Id,
            (2010, 2013, "Seeded FC"),
            (2010, 2013, "Some Other Club"),
            (2016, null, "Yet Another Club"));

        var template = SeedTemplate(3);

        Assert.ThrowsAsync<PathGenerationException>(
            async () => await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));
    }

    [Test]
    public void REQ1201_GenerateInstanceAsync_CandidateWithTwoSimultaneouslyOngoingStints_NeverSelected()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");

        // Both stints start in 2010 and are still "ongoing" (EndYear null)
        // — an identical (StartYear, EndYear) pair even though EndYear is
        // null on both sides (design decision: null must compare equal to
        // null here, not be treated as "never a duplicate").
        var twoOngoing = SeedPlayer("TwoOngoingStints");
        SeedStints(twoOngoing.Id,
            (2010, null, "Seeded FC"),
            (2010, null, "Some Other Club"),
            (2016, 2018, "Yet Another Club"));

        var template = SeedTemplate(3);

        Assert.ThrowsAsync<PathGenerationException>(
            async () => await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));
    }

    [Test]
    public void REQ1201_GenerateInstanceAsync_CandidateWithNoStintAtSeededClub_NeverSelected()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");

        var noSeededClub = SeedPlayer("NoSeededClub");
        SeedStints(noSeededClub.Id,
            (2010, 2013, "Unseeded Club A"),
            (2013, 2016, "Unseeded Club B"),
            (2016, null, "Unseeded Club C"));

        var template = SeedTemplate(3);

        Assert.ThrowsAsync<PathGenerationException>(
            async () => await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id }));
    }

    // Positive control for the "3 rows, not 3 distinct clubs" reading of
    // REQ-1201: PlayerCareerStint's own doc comment explicitly allows two
    // rows at the same club (e.g. a loan then a later return), and
    // REQ-1201's text never requires 3 *different* clubs — only 3 stint
    // rows, with at least one at a seeded club. A candidate whose 3 stints
    // are all at the SAME seeded club must still be eligible.
    [Test]
    public async Task REQ1201_GenerateInstanceAsync_CandidateWithThreeStintsAtSameSeededClub_IsEligible()
    {
        SeedClub("Seeded FC");
        SeedEligiblePlayer("Eligible1", "Seeded FC");
        SeedEligiblePlayer("Eligible2", "Seeded FC");

        var sameClubThreeTimes = SeedPlayer("SameClubThrice");
        SeedStints(sameClubThreeTimes.Id,
            (2010, 2012, "Seeded FC"),
            (2013, 2015, "Seeded FC"),
            (2016, null, "Seeded FC"));

        var template = SeedTemplate(3);

        var instance = await _module.GenerateInstanceAsync(new RoundConfig { TemplateId = template.Id });
        var targets = await GetTargetPlayerIdsAsync(instance.Id);

        Assert.That(targets, Has.Count.EqualTo(3));
        Assert.That(targets, Does.Contain(sameClubThreeTimes.Id));
    }

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

    [Test]
    public void GameKey_IsXgPath()
    {
        Assert.That(_module.GameKey, Is.EqualTo("xg-path"));
        Assert.That(XGPathGameModule.XGPathGameKey, Is.EqualTo("xg-path"));
    }

    [Test]
    public void ScoreSubmissionAsync_ThrowsNotImplemented()
    {
        Assert.ThrowsAsync<NotImplementedException>(
            async () => await _module.ScoreSubmissionAsync(Guid.NewGuid(), Guid.NewGuid(), new object()));
    }

    [Test]
    public void GetMaxAttemptsForCellAsync_ThrowsNotImplemented()
    {
        Assert.ThrowsAsync<NotImplementedException>(
            async () => await _module.GetMaxAttemptsForCellAsync(Guid.NewGuid(), Guid.NewGuid()));
    }
}
