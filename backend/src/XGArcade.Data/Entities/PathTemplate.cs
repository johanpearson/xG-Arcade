namespace XGArcade.Data.Entities;

// Games.XGPath (COMP-11) entity — mirrors GridTemplate's shape/precedent
// (see that entity's own doc comment for why every game module's entities
// live in this single shared DbContext, ADR-0014). RoundConfig.TemplateId
// (opaque to Core, ADR-0003) points at one of these; XGPathGameModule.
// GenerateInstanceAsync reads PuzzleCount as its target puzzle count N
// (REQ-1202, 3-5, configurable). S-081 had no seeding/admin/endpoint work
// for this type — S-084 added the find-or-create-by-PuzzleCount path
// (PathTemplateResolver, mirroring GridTemplateResolver) that
// /internal/generate-round now uses for "xg-path"; there is still no
// admin-driven template management. Tests can still construct PathTemplate
// fixtures directly.
public class PathTemplate
{
    public Guid Id { get; set; }
    public required int PuzzleCount { get; set; } // 3-5 (REQ-1202)
}
