using XGArcade.Core.Games;

namespace XGArcade.Games.XGGrid;

// S-119 (pure refactor, no behavior change): split out of GridGameModule —
// owns REQ-101/102/107/108/109's whole grid-generation pipeline (pairing
// selection, header picking with its retry/abort logic, cell construction).
public interface IGridGenerationService
{
    Task<GameInstance> GenerateInstanceAsync(RoundConfig config, CancellationToken cancellationToken = default);
}
