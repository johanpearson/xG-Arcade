using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Path;

// Tier 0 has no admin-driven PathTemplate management (REQ-1202's full scope)
// — shared find-or-create-by-puzzle-count helper, mirroring
// XGArcade.Api.Grid.GridTemplateResolver's exact shape, used by
// /internal/generate-round (S-084) so the endpoint can't drift on how a
// PathTemplate gets resolved.
internal static class PathTemplateResolver
{
    public static async Task<PathTemplate> GetOrCreateByPuzzleCountAsync(
        IPathInstanceRepository pathInstanceRepository, int puzzleCount, CancellationToken cancellationToken) =>
        await pathInstanceRepository.GetTemplateByPuzzleCountAsync(puzzleCount, cancellationToken)
            ?? await pathInstanceRepository.AddTemplateAsync(
                new PathTemplate
                {
                    Id = Guid.NewGuid(),
                    PuzzleCount = puzzleCount,
                },
                cancellationToken);
}
