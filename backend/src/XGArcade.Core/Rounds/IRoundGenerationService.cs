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
    //
    // roundDurationOverride is deliberately a parameter here, not a field on
    // RoundConfig: RoundConfig is opaque/game-owned (ADR-0003), while round
    // duration is a Core.Rounds scheduling concern. When supplied, it wins
    // over RoundSchedulingOptions.RoundDuration for this one generation call
    // only — it never mutates the shared singleton, so it has no effect on
    // any other round.
    Task<Round> GenerateNextRoundIfNeededAsync(RoundConfig config, TimeSpan? roundDurationOverride = null, CancellationToken cancellationToken = default);
}
