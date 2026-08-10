namespace XGArcade.Core.Rounds;

// ADR-0040-equivalent for round scheduling: mirrors
// IScoringStrategyResolver's exact resolution shape (see
// Scoring/IScoringStrategyResolver.cs) for RoundSchedulingOptions keyed by
// Round.GameKey instead of scoring strategies. S-084 (REQ-1202): a second
// game (xg-path) needs its own RoundDuration/AllowGuessChange, independent
// of xG Grid's — this is what lets RoundGenerationService resolve the right
// one per call instead of a GameKey conditional branch inline anywhere in
// Core.Rounds, and instead of a single directly-injected singleton that
// could only ever serve one GameKey.
public interface IRoundSchedulingOptionsResolver
{
    RoundSchedulingOptions Resolve(string gameKey);
}

public class RoundSchedulingOptionsResolver(IEnumerable<RoundSchedulingOptions> schedulingOptions) : IRoundSchedulingOptionsResolver
{
    public RoundSchedulingOptions Resolve(string gameKey) =>
        schedulingOptions.FirstOrDefault(o => o.GameKey == gameKey)
            ?? throw new InvalidOperationException($"No RoundSchedulingOptions registered for GameKey '{gameKey}'.");
}
