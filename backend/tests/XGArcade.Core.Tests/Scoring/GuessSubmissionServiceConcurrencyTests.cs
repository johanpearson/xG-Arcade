using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Core.Scoring;
using XGArcade.Core.Tests.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.TestSupport;

namespace XGArcade.Core.Tests.Scoring;

// REQ-603 (docs/requirements-document.md §4.8): "Uniqueness calculation must
// handle concurrent guesses correctly (no race conditions producing an
// incorrect percentage)." S-240 (docs/backlog.md, Epic 33).
//
// Unit level, not API/WebApplicationFactory: UniquenessCalculator.Calculate
// and UniquenessScoringStrategy.ScoreCorrectGuess are pure, DB-independent
// functions over an already-materialized snapshot of Guess rows (see their
// own doc comments) — they carry no shared mutable state and have no
// concurrency risk in isolation. The actual risk REQ-603 is about lives one
// layer up, in GuessSubmissionService.SubmitGuessAsync's read-then-write
// path (GetAsync existence check, then AddAsync), which is exactly what
// GuessSubmissionServiceTests.cs already unit-tests sequentially — this file
// extends that same pattern (EF Core InMemory provider, hand-rolled
// FakeGameModule, no mocking framework) to a genuinely concurrent run.
//
// Each concurrent submission below gets its own XGArcadeDbContext/
// repository/service instance (all pointed at the same InMemory database
// name) rather than one shared DbContext reused across Task.WhenAll
// branches — a shared DbContext instance would only prove EF Core's own
// single-context thread-safety guard fires, not exercise this service's
// actual concurrency behavior. Separate DbContext-per-task mirrors
// production: XGArcadeDbContext is registered Scoped (one instance per
// HTTP request; ServiceRegistration.cs), so many concurrent guess
// submissions in production are, in exactly this shape, many independent
// DbContexts writing against the same logical dataset.
public class GuessSubmissionServiceConcurrencyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    // 30 users total: 4 groups of 5 sharing one of 4 "popular" answers (20
    // users), plus 10 users each with their own distinct singleton answer —
    // a deliberately non-uniform mix of same-answer and different-answer
    // correct guesses, so the resulting uniqueness distribution is
    // meaningful (some guessers should score less than fully unique, some
    // fully unique) rather than a trivial all-tied or all-distinct case.
    private static List<(Guid UserId, Guid AnswerId)> BuildSubmissionPlan()
    {
        var plan = new List<(Guid UserId, Guid AnswerId)>();
        for (var group = 0; group < 4; group++)
        {
            var sharedAnswerId = Guid.NewGuid();
            for (var member = 0; member < 5; member++)
            {
                plan.Add((Guid.NewGuid(), sharedAnswerId));
            }
        }

        for (var i = 0; i < 10; i++)
        {
            plan.Add((Guid.NewGuid(), Guid.NewGuid()));
        }

        return plan;
    }

    private static async Task<Round> SeedActiveRoundAsync(XGArcadeDbContext dbContext)
    {
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = "xg-grid",
            GameInstanceId = Guid.NewGuid(),
            SequenceNumber = 1,
            StartTime = Now.UtcDateTime.AddDays(-1),
            EndTime = Now.UtcDateTime.AddDays(1),
            AllowGuessChange = false,
        };
        dbContext.Rounds.Add(round);
        await dbContext.SaveChangesAsync();
        return round;
    }

    private static FakeGameModule BuildGameModule(IReadOnlyDictionary<Guid, Guid> answerByUserId) =>
        new("xg-grid")
        {
            ScoreSubmissionResult = (_, userId, _) =>
                new ScoreResult { IsCorrect = true, PlayerAnswerId = answerByUserId[userId] },
        };

    private static GuessSubmissionAllowedGameKeys AllowedGameKeys() =>
        new() { GameKeys = ["xg-grid", "xg-path"] };

    private static async Task SubmitOneAsync(
        string databaseName, Guid roundId, Guid userId, Guid cellId, Guid answerId, FakeGameModule gameModule)
    {
        // A fresh DbContext/repository/service per submission, all backed by
        // the same InMemory database name — the "many independent scopes,
        // one logical dataset" shape described in this file's header.
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        await using var dbContext = new XGArcadeDbContext(options);
        var service = new GuessSubmissionService(
            new RoundRepository(dbContext), new GuessRepository(dbContext), new GameModuleResolver([gameModule]),
            new PlayerRepository(dbContext), new FixedTimeProvider(Now), AllowedGameKeys());

        var result = await service.SubmitGuessAsync(roundId, userId, cellId, $"Answer-{answerId}");

        if (result.Outcome != GuessSubmissionOutcome.Accepted)
        {
            throw new InvalidOperationException(
                $"Concurrent submission for user {userId} was rejected with {result.Outcome} instead of Accepted — the test's own assertions would be meaningless if any submission didn't actually land.");
        }
    }

    // Computes, for every user in the plan, the uniqueness score
    // UniquenessCalculator.Calculate produces given the full set of correct
    // Guess rows for the cell — a stable, order-independent view of the
    // resulting population (REQ-604's formula depends only on the
    // population's composition, never on write order), keyed by UserId so
    // the concurrent-run and sequential-run results can be compared
    // directly without depending on either run's completion order.
    private static Dictionary<Guid, double> ComputeUniquenessByUserId(IReadOnlyCollection<Guess> correctGuessesForCell) =>
        correctGuessesForCell.ToDictionary(
            g => g.UserId!.Value,
            g => UniquenessCalculator.Calculate(correctGuessesForCell, g.PlayerAnswerId!.Value));

    [Test]
    public async Task REQ603_SubmitGuessAsync_ConcurrentGuessesForSameCell_ProducesSameUniquenessAsSequentialSubmission()
    {
        var plan = BuildSubmissionPlan();
        var answerByUserId = plan.ToDictionary(p => p.UserId, p => p.AnswerId);

        // ---- Concurrent run: N distinct users, N genuinely independent
        // DbContexts, all racing to submit a guess for the same cell at once.
        var concurrentDatabaseName = Guid.NewGuid().ToString();
        var concurrentOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(concurrentDatabaseName)
            .Options;
        Round concurrentRound;
        await using (var seedContext = new XGArcadeDbContext(concurrentOptions))
        {
            concurrentRound = await SeedActiveRoundAsync(seedContext);
        }

        var concurrentCellId = Guid.NewGuid();
        var concurrentGameModule = BuildGameModule(answerByUserId);

        // Task.Run, not a bare SubmitOneAsync(...) call, is load-bearing here:
        // EF Core's InMemory provider has no real I/O, so every "async" step
        // in SubmitOneAsync's call chain (GetAsync/ScoreSubmissionAsync/
        // AddAsync/SaveChangesAsync) tends to complete synchronously. An
        // async method only yields back to its caller at its first
        // genuinely-incomplete await — if every await inside completes
        // synchronously, calling SubmitOneAsync(...) directly inside
        // Select(...) would run each submission to completion, one at a
        // time, on the single thread driving Task.WhenAll's enumeration,
        // before the next one is even started — sequential execution
        // disguised as "concurrent" code, which would make this test pass
        // even against a genuinely race-vulnerable version. Task.Run forces
        // each submission onto its own ThreadPool thread, giving real,
        // OS-scheduled parallel execution instead of relying on async
        // interleaving InMemory's synchronous implementation won't provide.
        await Task.WhenAll(plan.Select(p => Task.Run(() =>
            SubmitOneAsync(concurrentDatabaseName, concurrentRound.Id, p.UserId, concurrentCellId, p.AnswerId, concurrentGameModule))));

        List<Guess> concurrentGuesses;
        await using (var readContext = new XGArcadeDbContext(concurrentOptions))
        {
            concurrentGuesses = await readContext.Guesses
                .Where(g => g.CellId == concurrentCellId)
                .ToListAsync();
        }

        // No submission was lost and none double-counted: exactly one
        // correct Guess row per user in the plan.
        Assert.That(concurrentGuesses.Count, Is.EqualTo(plan.Count),
            "every concurrent submission must land exactly once — a lost update would show up as fewer rows than users");
        Assert.That(concurrentGuesses.All(g => g.IsCorrect), Is.True);
        Assert.That(concurrentGuesses.Select(g => g.UserId).Distinct().Count(), Is.EqualTo(plan.Count),
            "no user's row should be missing or duplicated");

        // ---- Sequential baseline: the exact same (user, answer) pairs,
        // submitted one at a time, against a fresh, independently-seeded
        // round/cell with the same shape.
        var sequentialDatabaseName = Guid.NewGuid().ToString();
        var sequentialOptions = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(sequentialDatabaseName)
            .Options;
        await using var sequentialContext = new XGArcadeDbContext(sequentialOptions);
        var sequentialRound = await SeedActiveRoundAsync(sequentialContext);
        var sequentialCellId = Guid.NewGuid();
        var sequentialGameModule = BuildGameModule(answerByUserId);
        var sequentialService = new GuessSubmissionService(
            new RoundRepository(sequentialContext), new GuessRepository(sequentialContext),
            new GameModuleResolver([sequentialGameModule]), new PlayerRepository(sequentialContext),
            new FixedTimeProvider(Now), AllowedGameKeys());

        foreach (var (userId, _) in plan)
        {
            var result = await sequentialService.SubmitGuessAsync(sequentialRound.Id, userId, sequentialCellId, "irrelevant");
            Assert.That(result.Outcome, Is.EqualTo(GuessSubmissionOutcome.Accepted));
        }

        var sequentialGuesses = await sequentialContext.Guesses
            .Where(g => g.CellId == sequentialCellId)
            .ToListAsync();
        Assert.That(sequentialGuesses.Count, Is.EqualTo(plan.Count));

        // ---- Compare: the uniqueness score each guesser gets from the
        // concurrently-produced population must be bit-for-bit identical to
        // the score the same (user, answer) submission gets from the
        // sequentially-produced population — no lost update means the two
        // populations have identical composition, and the formula is
        // order-independent given a complete population.
        var concurrentUniquenessByUserId = ComputeUniquenessByUserId(concurrentGuesses);
        var sequentialUniquenessByUserId = ComputeUniquenessByUserId(sequentialGuesses);

        Assert.That(concurrentUniquenessByUserId.Keys, Is.EquivalentTo(sequentialUniquenessByUserId.Keys));
        foreach (var (userId, answerId) in plan)
        {
            Assert.That(concurrentUniquenessByUserId[userId], Is.EqualTo(sequentialUniquenessByUserId[userId]),
                $"user {userId} (answer {answerId}) must score identically whether their guess was part of the concurrent or the sequential run");
        }

        // Sanity check that the plan's mix actually produced a meaningful,
        // non-trivial spread — not just N tied scores, which would make the
        // equality assertions above pass vacuously even under a real bug.
        Assert.That(concurrentUniquenessByUserId.Values.Distinct().Count(), Is.GreaterThan(1),
            "the shared-answer vs. singleton-answer mix must produce more than one distinct uniqueness value");
        Assert.That(concurrentUniquenessByUserId.Values, Has.Some.EqualTo(1.0),
            "each singleton answer's sole guesser must score fully unique");
        Assert.That(concurrentUniquenessByUserId.Values, Has.Some.LessThan(1.0),
            "each shared-answer guesser must score less than fully unique");
    }
}
