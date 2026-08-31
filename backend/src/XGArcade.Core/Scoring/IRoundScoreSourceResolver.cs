namespace XGArcade.Core.Scoring;

// ADR-0100: resolves the IRoundScoreSource that owns a given Round.GameKey's
// totals, mirroring IScoringStrategyResolver's per-GameKey resolution shape
// (ADR-0040) — a new game adds a new IRoundScoreSource implementation and
// registers it against its GameKey(s) at the composition root, never a
// GameKey conditional branch inline anywhere in Core.Leagues/Core.Scoring.
//
// Unlike IScoringStrategy, IRoundScoreSource carries no GameKey property of
// its own (see that interface's own doc comment for why), so
// RoundScoreSourceResolver can't do ScoringStrategyResolver's own
// FirstOrDefault(s => s.GameKey == ...) lookup — it's constructed with an
// already-built IReadOnlyDictionary<string, IRoundScoreSource> instead,
// keyed by every GameKey each registered source serves at the composition
// root. This is what lets one GuessRoundScoreSource type answer for both
// "xg-grid" and "xg-path" without the interface itself needing to expose a
// GameKey.
public interface IRoundScoreSourceResolver
{
    IRoundScoreSource Resolve(string gameKey);
}

public class RoundScoreSourceResolver(IReadOnlyDictionary<string, IRoundScoreSource> sourcesByGameKey) : IRoundScoreSourceResolver
{
    public IRoundScoreSource Resolve(string gameKey) =>
        sourcesByGameKey.TryGetValue(gameKey, out var source)
            ? source
            : throw new InvalidOperationException($"No IRoundScoreSource registered for GameKey '{gameKey}'.");
}
