namespace XGArcade.Games.XGPath;

// S-154 (pure refactor, no behavior change, docs/backlog.md Epic 17): split
// out of XGPathGameModule — owns REQ-1201's whole target-player eligibility
// pipeline (candidate narrowing, stint sanitization, the three structural
// checks, the BirthYear/Position floors, and ADR-0056's familiarity filter).
// Mirrors the "narrow interface, one public method" shape
// docs/decisions/0068-grid-game-module-responsibility-split.md established
// for IGridGenerationService.
public interface IPathEligibilityService
{
    Task<IReadOnlyList<Guid>> GetEligiblePlayerIdsAsync(CancellationToken cancellationToken = default);
}
