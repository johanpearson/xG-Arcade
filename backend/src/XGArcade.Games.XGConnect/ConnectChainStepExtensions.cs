using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect;

public static class ConnectChainStepExtensions
{
    // REQ-1406/1407/1408/S-214: "has this player closed their chain for
    // this match" — a single valid (IsValid), chain-closing (ClosesChain)
    // row anywhere in their steps. Shared by every call site that needs
    // this exact check (ConnectChainStepService's own
    // ChainAlreadyComplete precondition, and ConnectMatchLifecycleService's
    // RunForfeitSweepAsync/TryResolveMatchIfBothTerminalAsync) so the
    // predicate itself can't drift between them — extracted once it hit
    // four independent copies (coding-guidelines.md's "Code health budget",
    // ADR-0084).
    public static bool HasClosedChain(this IReadOnlyList<ConnectChainStep> stepsForOnePlayerInOneMatch) =>
        stepsForOnePlayerInOneMatch.Any(s => s.IsValid && s.ClosesChain);
}
