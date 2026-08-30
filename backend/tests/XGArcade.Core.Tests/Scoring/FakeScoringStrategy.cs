using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Scoring;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as FakeGameModule in
// XGArcade.Core.Tests/Rounds/FakeGameModule.cs). Lets
// ScoringStrategyResolverTests exercise resolution/registration behavior
// without depending on the real UniquenessScoringStrategy's math.
internal class FakeScoringStrategy(string gameKey) : IScoringStrategy
{
    public string GameKey { get; } = gameKey;

    // ADR-0095: defaults to true (ADR-0021's golf-style default, matching
    // every real strategy except xG Predict) — settable so a test can
    // exercise LeaderboardService's descending branch without depending on
    // the real XGPredictScoringStrategy.
    public bool LowerIsBetter { get; set; } = true;

    public Func<Guess, IReadOnlyCollection<Guess>, int, ScoringResult> ScoreCorrectGuessResult { get; set; } =
        (_, _, _) => throw new NotImplementedException("Not exercised by resolver tests.");

    public ScoringResult ScoreCorrectGuess(Guess guess, IReadOnlyCollection<Guess> correctGuessesForCell, int maxAttemptsForCell) =>
        ScoreCorrectGuessResult(guess, correctGuessesForCell, maxAttemptsForCell);
}
