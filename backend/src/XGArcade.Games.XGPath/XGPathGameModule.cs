using XGArcade.Core.Games;

namespace XGArcade.Games.XGPath;

// COMP-11: IGameModule implementation for xG Path, the second game hosted
// on the platform. S-080 scaffolds only the module boundary — GameKey
// registration and discoverability through IGameModuleResolver — so a
// second game module can prove ADR-0002/ADR-0003's IGameModule boundary
// holds in practice before any real xG Path logic exists. Every method
// below throws NotImplementedException on purpose: puzzle generation,
// guess scoring, and the per-puzzle attempt cap are all real gameplay
// decisions described in docs/requirements-document.md §4.12
// (REQ-1201-REQ-1206) that haven't been implemented yet (S-081 onward),
// and a stub that silently returned fake data would misrepresent that.
public class XGPathGameModule : IGameModule
{
    public const string XGPathGameKey = "xg-path";

    public string GameKey => XGPathGameKey;

    public Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default) =>
        // REQ-1201 (target-player eligibility) / REQ-1202 (round structure:
        // a small, fixed set of puzzles) — see S-081.
        Task.FromException<GameInstance>(
            new NotImplementedException("xG Path puzzle generation not yet implemented — see REQ-1201/REQ-1202 (S-081)."));

    public Task<ScoreResult> ScoreSubmissionAsync(
        Guid instanceId, Guid userId, object submission, CancellationToken cancellationToken = default) =>
        // REQ-1204 (guess correctness resolution) — see S-082.
        Task.FromException<ScoreResult>(
            new NotImplementedException("xG Path guess scoring not yet implemented — see REQ-1204 (S-082)."));

    public Task<IReadOnlyList<Guid>> GetCellIdsAsync(Guid instanceId, CancellationToken cancellationToken = default) =>
        // REQ-1202 — depends on puzzle instances existing at all (S-081).
        Task.FromException<IReadOnlyList<Guid>>(
            new NotImplementedException("xG Path puzzle/cell lookup not yet implemented — see REQ-1202 (S-081)."));

    public Task<int> GetMaxAttemptsForCellAsync(Guid instanceId, Guid cellId, CancellationToken cancellationToken = default) =>
        // REQ-1205 (per-puzzle attempt cap, min(stints, 5) + 4) — see S-082.
        Task.FromException<int>(
            new NotImplementedException("xG Path per-puzzle attempt cap not yet implemented — see REQ-1205 (S-082)."));
}
