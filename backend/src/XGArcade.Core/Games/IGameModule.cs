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

    // ADR-0021: the full set of cell ids for a generated instance — round
    // close uses this to find, for each round participant, any cell they
    // never submitted a guess for, so it can be penalized the same as an
    // incorrect guess rather than silently scoring 0 (the best possible
    // score under the lowest-wins model). Core.Scoring resolves this only
    // through IGameModule, never by reaching into a game-specific instance
    // table directly (ADR-0003).
    Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default);

    // ADR-0041: a given cell's own max-attempts value, replacing the old
    // single `GuessRules.MaxAttemptsPerCell` global constant. Not every game
    // has a fixed, uniform attempt cap (e.g. xG Path's cap varies
    // target-player to target-player within the same round), so this is
    // resolved per cell through the owning module rather than assumed
    // platform-wide. GuessSubmissionService (REQ-210's lock/cap check),
    // LiveRoundContributionService, and RoundEndpoints all call this instead
    // of reading a shared constant. xG Grid's implementation returns a fixed
    // 2 for every cell, unconditionally — REQ-210's existing behavior,
    // unchanged, now reported through this method instead of the deleted
    // GuessRules.MaxAttemptsPerCell.
    Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default);
}
