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

    // REQ-209: non-null and non-empty exactly when more than one candidate
    // satisfies both of the cell's categories — mutually exclusive with
    // IsCorrect = true (a genuinely ambiguous guess is never "correct" on
    // its own; the player must pick one first). Core.Scoring
    // (GuessSubmissionService) reads this to decide whether to persist a
    // Guess row at all — showing the prompt must never consume an attempt
    // (REQ-210).
    public IReadOnlyList<DisambiguationCandidate>? DisambiguationCandidates { get; set; }
}

// REQ-209: one entry per candidate the player must choose between.
// DistinguishingAttributes holds each candidate's OTHER known
// PlayerAttribute values (nationality/club/trophy) — excluding whichever of
// the cell's own two categories every candidate already satisfies (showing
// those again wouldn't distinguish anything). Can be empty for a candidate
// with no other known attributes at all — the player still picks based on
// name alone in that rare case.
public record DisambiguationCandidate(Guid PlayerId, string Name, IReadOnlyList<string> DistinguishingAttributes);
