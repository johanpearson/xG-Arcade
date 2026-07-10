using XGArcade.Core.Games;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Rounds;

// COMP-03: realizes REQ-301 — generation always runs one round ahead.
public interface IRoundGenerationService
{
    // config.TemplateId is opaque to Core (RoundConfig's own doc comment) —
    // the caller (currently XGArcade.Api's internal endpoint) resolves it
    // ahead of time via the owning game module's own repository, the same
    // way S-007's /internal/grid/generate already does.
    Task<Round> GenerateNextRoundIfNeededAsync(RoundConfig config, CancellationToken cancellationToken = default);
}
