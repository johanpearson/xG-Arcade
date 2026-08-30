using XGArcade.Data.Entities;

namespace XGArcade.Core.Scoring;

// REQ-1304/ADR-0095/ADR-0096: xG Predict's per-match scoring formula.
//
// GameKey is supplied by the composition root (Program.cs/ServiceRegistration.cs)
// at registration time, never hardcoded here — same boundary reason as
// UniquenessScoringStrategy.GameKey/ClueEfficiencyScoringStrategy.GameKey
// (ADR-0003): XGArcade.Core must not reference
// XGArcade.Games.XGPredict.XGPredictGameModule.XGPredictGameKey directly.
//
// Unlike UniquenessScoringStrategy/ClueEfficiencyScoringStrategy,
// ScoreCorrectGuess (below) is never actually reachable in production for
// this GameKey — see that method's own doc comment. REQ-1304's real
// computation lives in ScorePrediction, a separate public method on this
// same class, exercised directly by this story's own unit tests (per
// REQ-1304's "Test level: Unit" note) and left for whichever future story
// builds REQ-1305's asynchronous grading job to decide how it actually gets
// called (ADR-0096's own "not decided here" scope).
public class XGPredictScoringStrategy : IScoringStrategy
{
    public required string GameKey { get; init; }

    // ADR-0095's named, single exception to ADR-0021's platform-wide
    // golf-style direction: xG Predict is conventional higher-is-better —
    // more correct components produce a bigger, better total. Every other
    // registered IScoringStrategy still returns true; do not extend this
    // exception to any other GameKey without that game having its own
    // equivalent ADR (ADR-0095's own "For AI agents" instruction).
    public bool LowerIsBetter => false;

    // ADR-0096: xG Predict does NOT use the generic Guess entity at all —
    // predictions are stored in the separate PredictMatchPrediction entity
    // (HomeGoals/AwayGoals/UserId/SubmittedAt), because Guess's shape
    // (string SubmittedName, capped AttemptCount, synchronously-known
    // IsCorrect) doesn't fit a two-integer, uncapped, asynchronously-graded
    // prediction. ScoreLockingService.LockRoundScoresAsync only ever calls
    // an IScoringStrategy's ScoreCorrectGuess for guesses it fetched via
    // IGuessRepository.GetByRoundIdAsync(roundId) — since an xG Predict
    // round never has any Guess rows, this method is architecturally
    // unreachable in production today, the same permanently-N/A shape as
    // XGPredictGameModule.GetCellCategoryTypesAsync/
    // ResolveWrongGuessPlayerAsync. Use ScorePrediction below instead.
    public ScoringResult ScoreCorrectGuess(Guess guess, IReadOnlyCollection<Guess> correctGuessesForCell, int maxAttemptsForCell) =>
        throw new NotSupportedException(
            "XGPredictScoringStrategy.ScoreCorrectGuess is unreachable: xG Predict never writes Guess rows " +
            "(ADR-0096) — predictions live in PredictMatchPrediction instead. Use ScorePrediction for REQ-1304's " +
            "actual per-match scoring formula, called by REQ-1305's future grading job.");

    // REQ-1304: three independent point components, each awarding
    // ScoringRules.PredictPointsPerComponent on a match, 0 on a miss:
    //   1. Outcome — predicted 1X2 result (derived by comparing predicted
    //      home/away goals) matches the actual result (derived the same
    //      way from the real final score).
    //   2. Home-goals — predicted home-team goal count exactly matches the
    //      actual home-team goal count.
    //   3. Away-goals — predicted away-team goal count exactly matches the
    //      actual away-team goal count.
    // These are independent, not all-or-nothing against the exact
    // scoreline — REQ-1304's own example: predicting 2-1 for an actual 3-1
    // result earns the outcome and away-goals components but not the
    // home-goals component. FinalPoints is the sum of whichever components
    // matched (0 to 3 * PredictPointsPerComponent). FinalUniquenessScore is
    // always null — xG Predict has no uniqueness concept, same "no concept
    // at all, not merely not-yet-computed" precedent as
    // ClueEfficiencyScoringStrategy.
    public ScoringResult ScorePrediction(int predictedHomeGoals, int predictedAwayGoals, int actualHomeGoals, int actualAwayGoals)
    {
        var points = 0;

        if (DeriveOutcome(predictedHomeGoals, predictedAwayGoals) == DeriveOutcome(actualHomeGoals, actualAwayGoals))
            points += ScoringRules.PredictPointsPerComponent;

        if (predictedHomeGoals == actualHomeGoals)
            points += ScoringRules.PredictPointsPerComponent;

        if (predictedAwayGoals == actualAwayGoals)
            points += ScoringRules.PredictPointsPerComponent;

        return new ScoringResult(null, points);
    }

    private enum MatchOutcome
    {
        HomeWin,
        Draw,
        AwayWin,
    }

    private static MatchOutcome DeriveOutcome(int homeGoals, int awayGoals) =>
        homeGoals == awayGoals ? MatchOutcome.Draw
        : homeGoals > awayGoals ? MatchOutcome.HomeWin
        : MatchOutcome.AwayWin;
}
