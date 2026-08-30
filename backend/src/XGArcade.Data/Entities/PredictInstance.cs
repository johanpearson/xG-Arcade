namespace XGArcade.Data.Entities;

// Games.XGPredict (COMP-15) entity. This Id is what Round (XGArcade.Core)
// stores as its opaque GameInstanceId — Core never references this type
// directly (ADR-0003), same precedent as GridInstance/PathInstance.
// ADR-0096 §1: Matches is an owned collection, cascade-deleted with its
// parent, same shape as PathInstance.Puzzles/GridInstance.Cells.
public class PredictInstance
{
    public Guid Id { get; set; }
    public required Guid TemplateId { get; set; }
    public List<PredictMatch> Matches { get; set; } = [];
}
