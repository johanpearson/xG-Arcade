using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Grid;

// Tier 0 has no admin-driven GridTemplate management (REQ-102's full scope)
// — shared find-or-create-by-size helper, used by both
// /internal/grid/generate (S-007) and /internal/generate-round (S-008) so
// the two endpoints can't drift on how a template gets resolved.
internal static class GridTemplateResolver
{
    public static async Task<GridTemplate> GetOrCreateBySizeAsync(
        IGridInstanceRepository gridInstanceRepository, int size, CancellationToken cancellationToken) =>
        await gridInstanceRepository.GetTemplateBySizeAsync(size, cancellationToken)
            ?? await gridInstanceRepository.AddTemplateAsync(
                new GridTemplate
                {
                    Id = Guid.NewGuid(),
                    Size = size,
                    AllowedCategoryTypes = [CategoryPairingRules.Country, CategoryPairingRules.Club],
                },
                cancellationToken);
}
