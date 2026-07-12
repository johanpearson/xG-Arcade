namespace XGArcade.Data.Entities;

// Games.XGGrid (COMP-05) entity.
//
// RowCategoryType/ColCategoryType ("country" | "club") are an addition
// beyond implementation-document.md §5's illustrative GridCell shape: Tier 0
// generates either Country x Club or, as of docs/backlog.md S-030, Club x
// Club (MVP-SCOPE.md) — recording the type per cell (rather than assuming a
// fixed axis) is what lets guess-checking (S-009) know whether to query
// PlayerAttribute's "nationality" or "club" AttributeType for a given cell
// without re-deriving it, and keeps the schema correct if a future Tier 1
// grid mixes in further category types (REQ-108's Trophy) across an axis.
public class GridCell
{
    public Guid Id { get; set; }
    public required Guid GridInstanceId { get; set; }
    public required int Row { get; set; }
    public required int Col { get; set; }
    public required string RowCategoryType { get; set; }
    public required string RowCategoryValue { get; set; }
    public required string ColCategoryType { get; set; }
    public required string ColCategoryValue { get; set; }
}
