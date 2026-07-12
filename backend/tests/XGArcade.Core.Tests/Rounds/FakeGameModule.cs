using XGArcade.Core.Games;

namespace XGArcade.Core.Tests.Rounds;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same no-Moq/no-NSubstitute pattern as
// XGArcade.Games.XGGrid.Tests/FakeWikidataLookupService.cs). Lets
// RoundGenerationServiceTests exercise REQ-301's "one round ahead" branching
// without depending on GridGameModule/XGArcade.Games.XGGrid at all — Core
// must never reference a game module directly (ADR-0003).
internal class FakeGameModule(string gameKey) : IGameModule
{
    public string GameKey { get; } = gameKey;

    public int GenerateInstanceAsyncCallCount { get; private set; }

    public Func<RoundConfig, GameInstance> GenerateInstanceResult { get; set; } =
        _ => new GameInstance { Id = Guid.NewGuid() };

    public Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default)
    {
        GenerateInstanceAsyncCallCount++;
        return Task.FromResult(GenerateInstanceResult(config));
    }

    // REQ-210's "checked before any name resolution work, not after" ordering
    // requirement (GuessSubmissionServiceTests) is asserted by reading this
    // count after a rejected submission — it must stay zero.
    public int ScoreSubmissionAsyncCallCount { get; private set; }

    public Func<Guid, Guid, object, ScoreResult> ScoreSubmissionResult { get; set; } =
        (_, _, _) => throw new NotImplementedException("Not exercised by round-generation/close tests.");

    public Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default)
    {
        ScoreSubmissionAsyncCallCount++;
        return Task.FromResult(ScoreSubmissionResult(instanceId, userId, submission));
    }

    // ADR-0021: defaults to no cells, since most existing tests using this
    // fake predate the unanswered-cell penalty and don't exercise it —
    // RoundCloseServiceScoringTests sets GetCellIdsResult explicitly where
    // it matters.
    public Func<Guid, IReadOnlyList<Guid>> GetCellIdsResult { get; set; } = _ => [];

    public Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetCellIdsResult(instanceId));
}
