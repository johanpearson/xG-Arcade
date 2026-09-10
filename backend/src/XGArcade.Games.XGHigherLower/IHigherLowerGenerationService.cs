using XGArcade.Core.Games;

namespace XGArcade.Games.XGHigherLower;

// Delegation-pattern refactor (2026-09-10, no behavior/REQ change): split
// out of XGHigherLowerGameModule, mirroring GridGameModule's own S-119 split
// into IGridGenerationService — the same "independently registered, no
// facade" convention docs/decisions/0068-grid-game-module-responsibility-split.md
// established. See NOTES.md's 2026-09-10 entry for the quality-architect
// finding this closes (raised during S-229's close-out) and
// HigherLowerGenerationService's own doc comment for the REQ-1501/1502/1503
// algorithm this owns.
public interface IHigherLowerGenerationService
{
    Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default);
}
