namespace XGArcade.Core.Games;

// IGameModule.GetCellCategoryTypesAsync's return shape (REQ-215/ADR-0052,
// S-089 architecture-review fix). Plain strings, not Games.XGGrid's
// CategoryPairingRules-typed constants — Core never references a
// game-specific type (ADR-0003) — but the same vocabulary already stored on
// GridCell.RowCategoryType/ColCategoryType and, denormalized off it,
// PlayerSuggestion.RowCategoryType/ColCategoryType.
public record CellCategoryTypes(string RowCategoryType, string ColCategoryType);
