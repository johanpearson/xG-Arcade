namespace XGArcade.Core.Games;

// IGameModule.ScoreSubmissionAsync's return value (S-009). REQ-210's
// lock/attempt-cap checks and REQ-202's guess-change-policy check both
// happen in Core.Scoring *before* a game module is ever called (see
// GuessSubmissionService) — this shape only carries back what the game
// module alone can determine: whether the submitted name resolved to a
// player satisfying the cell's categories.
public class ScoreResult
{
    public bool IsCorrect { get; set; }

    // Null when IsCorrect is false and no candidate matched the cell at all
    // — an incorrect guess has no real player to point at.
    public Guid? PlayerAnswerId { get; set; }
}
