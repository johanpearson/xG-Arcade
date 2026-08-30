using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Predict;

// Tier 0 has no admin-driven PredictTemplate management (mirrors
// PathTemplate's own ADR-0096 §2 "config now, even if only one value is
// valid yet" precedent) — shared find-or-create-by-match-count helper,
// mirroring XGArcade.Api.Path.PathTemplateResolver's exact shape, used by
// /internal/generate-round so the endpoint can't drift on how a
// PredictTemplate gets resolved. See ADR-0051's 2026-08-30 amendment (xG
// Predict wiring) for the re-derivation confirming this pattern still holds
// for a third GameKey.
internal static class PredictTemplateResolver
{
    public static async Task<PredictTemplate> GetOrCreateByMatchCountAsync(
        IPredictInstanceRepository predictInstanceRepository, int matchCount, CancellationToken cancellationToken) =>
        await predictInstanceRepository.GetTemplateByMatchCountAsync(matchCount, cancellationToken)
            ?? await predictInstanceRepository.AddTemplateAsync(
                new PredictTemplate
                {
                    Id = Guid.NewGuid(),
                    MatchCount = matchCount,
                },
                cancellationToken);
}
