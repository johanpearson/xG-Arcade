namespace XGArcade.Data.Entities;

// REQ-215: one row per club a PlayerSuggestion asserts the player is
// eligible for. Free text (the club name as the submitting player typed/
// selected it), not a ClubDefinition FK — a suggestion exists precisely to
// flag a genuine gap in the data (REQ-215's own framing), so a player must
// be able to assert a club that isn't already in ClubDefinition's small,
// hand-curated Tier 0 list (MVP-SCOPE.md). Same "category value stored as a
// plain string, not a ClubDefinition FK" shape GridCell.RowCategoryValue/
// ColCategoryValue already use, for the identical reason.
public class PlayerSuggestionClub
{
    public Guid Id { get; set; }
    public required Guid PlayerSuggestionId { get; set; }
    public required string ClubName { get; set; }
}
