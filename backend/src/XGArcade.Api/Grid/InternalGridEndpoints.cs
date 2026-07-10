using XGArcade.Core.Games;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Grid;

// S-007: an internal endpoint that actually triggers grid generation, so it
// can be exercised end to end in dev before Core.Rounds (S-008) exists to
// call IGameModule as part of real round scheduling. Gated to non-Production
// the same way ADR-0006 requires for COMP-09: checked here in Program.cs
// before the route is even registered, never guarded only by an attribute.
public static class InternalGridEndpoints
{
    public static void MapInternalGridEndpoints(this WebApplication app)
    {
        if (app.Environment.IsProduction())
            return;

        app.MapPost("/internal/grid/generate", async (
            GenerateGridRequest request,
            IGridInstanceRepository gridInstanceRepository,
            IGameModule gameModule,
            ILogger<GridGenerationLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            if (request.Size is < 3 or > 5)
            {
                return Results.Problem(
                    title: "Invalid grid size",
                    detail: "Size must be 3, 4, or 5 (REQ-102).",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // Tier 0 has no admin-driven template management (REQ-102's full
            // scope) — find-or-create a template for this size on demand,
            // fixed to Tier 0's only allowed pairing (MVP-SCOPE.md).
            var template = await GridTemplateResolver.GetOrCreateBySizeAsync(gridInstanceRepository, request.Size, cancellationToken);

            GameInstance instance;
            try
            {
                instance = await gameModule.GenerateInstanceAsync(
                    new RoundConfig { TemplateId = template.Id }, cancellationToken);
            }
            catch (GridGenerationException ex)
            {
                // REQ-101: "generation aborts and logs an error."
                logger.LogError(ex, "Grid generation aborted for GridTemplate {TemplateId} (size {Size}).", template.Id, template.Size);

                return Results.Problem(
                    title: "Grid generation failed",
                    // detail is the exception's own hand-authored message, not
                    // stack-trace text, and this is a non-Production-only
                    // debug endpoint (see the gating above) — a deliberate
                    // exception to "never leak raw exception text to the
                    // client", not an oversight.
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            var persisted = await gridInstanceRepository.GetInstanceByIdAsync(instance.Id, cancellationToken)
                ?? throw new InvalidOperationException($"GridInstance '{instance.Id}' was created but could not be re-read.");

            return Results.Ok(new GenerateGridResponse(
                persisted.Id,
                template.Size,
                [.. persisted.Cells
                    .OrderBy(c => c.Row).ThenBy(c => c.Col)
                    .Select(c => new GenerateGridCellResponse(c.Row, c.Col, c.RowCategoryValue, c.ColCategoryValue))]));
        });
    }
}

public record GenerateGridRequest(int Size);

public record GenerateGridCellResponse(int Row, int Col, string RowCategoryValue, string ColCategoryValue);

public record GenerateGridResponse(Guid GridInstanceId, int Size, IReadOnlyList<GenerateGridCellResponse> Cells);

// Pure log-category marker for ILogger<T> — a static class can't be used as
// a generic type argument, so this stands in for InternalGridEndpoints.
internal sealed class GridGenerationLogCategory;
