using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect;

// REQ-1408: see IConnectScoringService's own doc comment.
public class ConnectScoringService : IConnectScoringService
{
    public int CalculateScore(IReadOnlyList<ConnectChainStep> stepsForOnePlayerInOneMatch)
    {
        // Every connector actually used in the final chain, including the
        // closing step.
        var validCount = stepsForOnePlayerInOneMatch.Count(s => s.IsValid);

        // Only a FIRST-attempt failure counts as a +1 penalty
        // (REQ-1407/1408) — a second, consecutive failure at the same
        // position (AttemptNumber == 2) means the player busted, and a
        // busted player never reaches this calculation at all (their caller,
        // ConnectMatchLifecycleService.TryResolveMatchIfBothTerminalAsync,
        // never calls this method for them — see REQ-1408's own "no valid
        // score" clause).
        var penaltyCount = stepsForOnePlayerInOneMatch.Count(s => !s.IsValid && s.AttemptNumber == 1);

        // REQ-1408: a single connector with zero penalties is the lowest
        // possible completed-chain score — 1, never 0.
        return Math.Max(1, validCount + penaltyCount);
    }
}
