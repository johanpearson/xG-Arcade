namespace XGArcade.Data.Entities;

// Games.XGGrid (COMP-05) entity. This Id is what a future Round
// (XGArcade.Core, once S-008 builds it) stores as its opaque
// GameInstanceId — Core never references this type directly (ADR-0003).
public class GridInstance
{
    public Guid Id { get; set; }
    public required Guid TemplateId { get; set; }
    public List<GridCell> Cells { get; set; } = [];
}
