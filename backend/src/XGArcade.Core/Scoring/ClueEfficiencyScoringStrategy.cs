using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// REQ-1206/S-083: xG Path has no uniqueness concept at all (unlike xG
// Grid's REQ-204/205 formula) — a correct guess is scored purely on how
// many clues it took, golf-style (fewer clues used = fewer points = better,
// same lowest-wins direction as ScoringRules/ADR-0021).
//
// GameKey is supplied by the composition root (Program.cs) at registration
// time, never hardcoded here — same boundary reason as
// UniquenessScoringStrategy.GameKey (ADR-0003): XGArcade.Core must not
// reference XGArcade.Games.XGPath's XGPathGameModule.XGPathGameKey constant
// directly.
public class ClueEfficiencyScoringStrategy : IScoringStrategy
{
    public required string GameKey { get; init; }

    // correctGuessesForCell: no use for it — xG Path scores each correct
    // guess purely against its own clues-used/max-attempts ratio, never
    // against how other players answered the same puzzle. Ignored.
    //
    // guess.AttemptCount *is* cluesUsed for the winning guess:
    // XGPathGameModule/GuessSubmissionService maintain exactly one Guess
    // row per (round, user, cell), incrementing AttemptCount by 1 per
    // submission, so the correct guess's AttemptCount at the moment it was
    // submitted already equals the number of clues that had been revealed
    // (PathClueSequenceBuilder.GetRevealedTurnCount's own doc comment
    // confirms this equivalence) — no separate "clues used" input needed.
    //
    // maxAttemptsForCell (ADR-0041's per-cell concept, resolved by
    // ScoreLockingService via IGameModule.GetMaxAttemptsForCellAsync) is
    // this puzzle's maxCluesForThisPuzzle.
    //
    // FinalUniquenessScore is always null — xG Path has no uniqueness
    // concept to report, not merely "not yet computed" (see
    // IScoringStrategy/ScoringResult's own doc comments).
    //
    // A puzzle never solved before its attempt cap is exhausted already
    // scores MaxPointsPerCell via ScoreLockingService's existing
    // !guess.IsCorrect branch (ADR-0021) — this method is only ever
    // invoked for guess.IsCorrect == true, so that case isn't
    // special-cased here.
    public ScoringResult ScoreCorrectGuess(Guess guess, IReadOnlyCollection<Guess> correctGuessesForCell, int maxAttemptsForCell)
    {
        var cluesUsed = guess.AttemptCount;
        var points = (int)Math.Round((double)cluesUsed / maxAttemptsForCell * ScoringRules.MaxPointsPerCell);
        return new ScoringResult(null, points);
    }
}
