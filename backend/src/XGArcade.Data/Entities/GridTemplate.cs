namespace XGArcade.Data.Entities;

// Games.XGGrid (COMP-05) entity — persisted here alongside every other
// entity in the single shared DbContext, same precedent as User/COMP-01
// (see NOTES.md/CHANGELOG S-004): "maps to XGArcade.Games.XGGrid" in
// architecture-document.md's component table describes where the grid
// generation/orchestration logic lives, not where the EF entity classes do.
// Tier 0: AllowedCategoryTypes is always ["country", "club"] — the field
// exists so the shape matches implementation-document.md §5 and doesn't
// need a later migration, but it's not actually read by GridGameModule's
// SelectPairing (which decides row/column category types itself, now
// including Trophy pairings as of docs/backlog.md S-030/S-031, REQ-107/
// REQ-108) — a known, harmless gap between this field and what's actually
// generated, not something this story's scope covers closing.
public class GridTemplate
{
    public Guid Id { get; set; }
    public required int Size { get; set; }             // 3, 4, 5 (REQ-102)
    public required string[] AllowedCategoryTypes { get; set; }
}
