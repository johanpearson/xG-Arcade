using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect;

// REQ-1408 (docs/requirements-document.md §4.15), S-214: pure, stateless
// scoring calculation, deliberately kept as its own small service rather
// than folded into ConnectMatchLifecycleService's resolution logic —
// mirrors Core.Scoring's IScoringStrategy shape (a pure calculation
// injected into whatever orchestrates resolution) without actually
// depending on Core.Scoring itself, since xG Connect never uses
// Core.Rounds/Core.Scoring (ADR-0103).
public interface IConnectScoringService
{
    // stepsForOnePlayerInOneMatch is every ConnectChainStep row for one
    // (ConnectMatchId, UserId) pair — the caller (ConnectMatchLifecycleService)
    // is responsible for only calling this for a player whose chain actually
    // closed (a row with IsValid && ClosesChain exists); this method itself
    // has no way to check that from the rows alone versus "hasn't reached
    // that position yet," so it trusts its caller per REQ-1408's own "only a
    // completed chain produces a comparable score" rule.
    int CalculateScore(IReadOnlyList<ConnectChainStep> stepsForOnePlayerInOneMatch);
}
