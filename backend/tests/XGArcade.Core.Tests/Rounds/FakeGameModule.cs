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

    public Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Not exercised by round-generation/close tests.");
}
