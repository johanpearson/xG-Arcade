namespace XGArcade.Data.Entities;

// Games.XGPath (COMP-11) entity. This Id is what Round (XGArcade.Core)
// stores as its opaque GameInstanceId — Core never references this type
// directly (ADR-0003), same precedent as GridInstance.
public class PathInstance
{
    public Guid Id { get; set; }
    public required Guid TemplateId { get; set; }
    public List<PathPuzzle> Puzzles { get; set; } = [];
}
