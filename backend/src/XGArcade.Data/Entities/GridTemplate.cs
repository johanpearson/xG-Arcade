namespace XGArcade.Data.Entities;

// Games.XGGrid (COMP-05) entity — persisted here alongside every other
// entity in the single shared DbContext, same precedent as User/COMP-01
// (see NOTES.md/CHANGELOG S-004): "maps to XGArcade.Games.XGGrid" in
// architecture-document.md's component table describes where the grid
// generation/orchestration logic lives, not where the EF entity classes do.
// Tier 0: AllowedCategoryTypes is always ["country", "club"] (MVP-SCOPE.md
// restricts Tier 0 grid content to Country x Club and, as of
// docs/backlog.md S-030, Club x Club; Trophy is Tier 1, REQ-108) — the field
// exists now so the shape matches implementation-document.md §5 and doesn't
// need a later migration.
public class GridTemplate
{
    public Guid Id { get; set; }
    public required int Size { get; set; }             // 3, 4, 5 (REQ-102)
    public required string[] AllowedCategoryTypes { get; set; }
}
