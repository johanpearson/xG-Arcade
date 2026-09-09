using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// REQ-1505/docs/requirements-document.md §4.16: xG Higher/Lower's scoring
// rule — FinalPoints is simply the streak length reached, an identity
// mapping rather than xg-predict's multi-component sum.
//
// GameKey is supplied by the composition root (Program.cs/
// CompositionRoot/ServiceRegistration.cs) at registration time, never
// hardcoded here — same boundary reason as UniquenessScoringStrategy.
// GameKey/ClueEfficiencyScoringStrategy.GameKey/XGPredictScoringStrategy.
// GameKey (ADR-0003): XGArcade.Core must not reference
// XGArcade.Games.XGHigherLower.XGHigherLowerGameModule.
// XGHigherLowerGameKey directly.
//
// Like XGPredictScoringStrategy, ScoreCorrectGuess (below) is never
// actually reachable in production for this GameKey — see that method's
// own doc comment. REQ-1505's real computation lives in ScoreAttempt, a
// separate public method on this same class, exercised directly by this
// story's own unit tests (per REQ-1505's "Test level: Unit" note) and
// left for whichever future story computes/persists a per-round
// leaderboard total for this GameKey to decide how it actually gets
// called — the same "not decided here" scope XGPredictScoringStrategy.
// ScorePrediction left for REQ-1305's grading job (that wiring itself
// landed even later, in S-199/ADR-0100, not the story that added
// ScorePrediction). This story (S-226) does not add an
// IRoundScoreSource/HigherLowerRoundScoreSource — that's explicitly out
// of scope here.
public class HigherLowerScoringStrategy : IScoringStrategy
{
    public required string GameKey { get; init; }

    // Same "higher is better" exception ADR-0095 established for
    // xg-predict, not ADR-0021's default golf-style direction: a longer
    // streak is better. Every other registered IScoringStrategy still
    // returns true; do not extend this exception to any other GameKey
    // without that game having its own equivalent decision on record.
    public bool LowerIsBetter => false;

    // xG Higher/Lower does NOT use the generic Guess entity at all —
    // attempts are stored in the separate HigherLowerAttempt entity
    // (StreakLength/CurrentBaselinePlayerId/CurrentBaselineValue/
    // HasEnded), because Guess's shape (string SubmittedName, capped
    // AttemptCount, synchronously-known IsCorrect) doesn't fit a
    // whole-attempt, terminal-streak-length outcome the way it fits a
    // single per-cell name guess. ScoreLockingService.LockRoundScoresAsync
    // only ever calls an IScoringStrategy's ScoreCorrectGuess for guesses
    // it fetched via IGuessRepository.GetByRoundIdAsync(roundId) — since
    // an xG Higher/Lower round never has any Guess rows, this method is
    // architecturally unreachable in production today, the same
    // permanently-N/A shape as XGHigherLowerGameModule.
    // GetCellCategoryTypesAsync/ResolveWrongGuessPlayerAsync. Use
    // ScoreAttempt below instead.
    public ScoringResult ScoreCorrectGuess(Guess guess, IReadOnlyCollection<Guess> correctGuessesForCell, int maxAttemptsForCell) =>
        throw new NotSupportedException(
            "HigherLowerScoringStrategy.ScoreCorrectGuess is unreachable: xG Higher/Lower never writes Guess rows " +
            "— attempts live in HigherLowerAttempt instead. Use ScoreAttempt for REQ-1505's actual scoring rule, " +
            "called by whichever future story computes/persists this GameKey's leaderboard total.");

    // REQ-1505: FinalPoints is the streak length reached, unmodified — an
    // identity mapping, unlike xg-predict's multi-component sum.
    // FinalUniquenessScore is always null — xG Higher/Lower has no
    // uniqueness concept, same "no concept at all, not merely
    // not-yet-computed" precedent as ClueEfficiencyScoringStrategy/
    // XGPredictScoringStrategy.
    public ScoringResult ScoreAttempt(int streakLength) => new(null, streakLength);
}
