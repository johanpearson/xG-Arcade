namespace XGArcade.Core.Scoring;

// ADR-0040: mirrors IGameModuleResolver's resolution shape exactly (see
// Games/IGameModuleResolver.cs) for scoring strategies keyed by
// Round.GameKey instead of game modules. A new game adds a new
// IScoringStrategy implementation and registers it against its GameKey —
// never a GameKey conditional branch inline anywhere in Core.Scoring.
public interface IScoringStrategyResolver
{
    IScoringStrategy Resolve(string gameKey);
}

public class ScoringStrategyResolver(IEnumerable<IScoringStrategy> scoringStrategies) : IScoringStrategyResolver
{
    public IScoringStrategy Resolve(string gameKey) =>
        scoringStrategies.FirstOrDefault(s => s.GameKey == gameKey)
            ?? throw new InvalidOperationException($"No IScoringStrategy registered for GameKey '{gameKey}'.");
}
