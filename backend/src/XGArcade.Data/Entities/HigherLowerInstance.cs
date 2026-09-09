namespace XGArcade.Data.Entities;

// Games.XGHigherLower (COMP-18) entity. This Id is what Round
// (XGArcade.Core) stores as its opaque GameInstanceId — Core never
// references this type directly (ADR-0003), same precedent as
// GridInstance/PathInstance/PredictInstance.
//
// REQ-1501/1503/ADR-0110: StatCategory is the ONE eligible stat category
// selected and fixed for this Round's entire lifetime — always "trophy" or
// "club" today (a COUNT of that PlayerAttribute.AttributeType's effective
// distinct values per player, see XGHigherLowerGameModule's own doc comment
// for why "nationality" is permanently excluded as a candidate).
// BaselinePlayerId/BaselineValue are the one eligible starting player and
// their real recorded effective count for StatCategory, likewise fixed at
// generation time (REQ-1503). Comparators is the rest of the fixed,
// fully-ordered sequence (REQ-1502) — an owned, cascade-deleted child
// collection, same shape as PredictInstance.Matches/PathInstance.Puzzles/
// GridInstance.Cells.
//
// TemplateId mirrors every sibling *Instance entity's shape for
// consistency, but — unlike GridInstance/PathInstance/PredictInstance —
// nothing resolves it to a real template row yet: this story (S-224/
// ADR-0110) deliberately does not introduce a HigherLowerTemplate concept.
// There is no Tier 0 need for one — ComparatorCount is fixed generation
// config on HigherLowerGenerationOptions (a per-GameKey DI singleton,
// ADR-0051), not a per-template value the way GridSize/PuzzleCount/
// MatchCount are. GenerateInstanceAsync accepts whatever RoundConfig.
// TemplateId the (not-yet-built, S-226/227) scheduling wiring passes and
// stores it here unvalidated and unread, purely so this entity's shape
// doesn't need another migration if a real template concept is ever added
// later — the same reasoning kept in mind, not something this story's
// generation algorithm depends on.
public class HigherLowerInstance
{
    public Guid Id { get; set; }
    public required Guid TemplateId { get; set; }
    public required string StatCategory { get; set; } // "trophy" | "club" (REQ-1501)
    public required Guid BaselinePlayerId { get; set; }
    public required int BaselineValue { get; set; }
    public List<HigherLowerComparator> Comparators { get; set; } = [];
}
