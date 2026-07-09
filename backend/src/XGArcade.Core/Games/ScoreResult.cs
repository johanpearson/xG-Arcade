namespace XGArcade.Core.Games;

// Placeholder shape for IGameModule.ScoreSubmissionAsync's return value —
// guess scoring itself is S-009's job (docs/backlog.md), not implemented by
// any game module yet. Declared now only so IGameModule's full signature
// (implementation-document.md §3) compiles.
public class ScoreResult
{
    public bool IsCorrect { get; set; }
}
