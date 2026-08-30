namespace XGArcade.Data.Entities;

// Games.XGPredict (COMP-15) entity — mirrors PathTemplate's shape/precedent
// (see that entity's own doc comment for why every game module's entities
// live in this single shared DbContext, ADR-0014). RoundConfig.TemplateId
// (opaque to Core, ADR-0003) points at one of these; XGPredictGameModule.
// GenerateInstanceAsync reads MatchCount as the target match count for one
// round (REQ-1301, currently always 5). ADR-0096 §2's "config now, even if
// only one value is valid yet" precedent — same reasoning PathTemplate.
// PuzzleCount already established. No seeding/admin/endpoint work for this
// type in this story; tests can construct PredictTemplate fixtures directly.
public class PredictTemplate
{
    public Guid Id { get; set; }
    public required int MatchCount { get; set; } // 5 (REQ-1301)
}
