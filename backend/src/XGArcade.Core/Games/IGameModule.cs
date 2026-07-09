namespace XGArcade.Core.Games;

// COMP-03 (Core.Rounds) resolves a Round's GameKey to exactly one
// IGameModule implementation and delegates instance generation/scoring to
// it — Core never references a game-specific type directly. See ADR-0003
// and architecture-document.md boundary rule 2.
public interface IGameModule
{
    string GameKey { get; }

    Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default);

    Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default);
}
