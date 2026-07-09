namespace XGArcade.Data.Entities;

// Effective, denormalized for fast querying — grid generation's candidate
// matching query (REQ-101) reads only this, never PlayerData/PlayerOverride
// directly. Composite key (PlayerId, AttributeType, AttributeValue) — a
// player has one row per distinct attribute (e.g. one per career club).
public class PlayerAttribute
{
    public Guid PlayerId { get; set; }
    public required string AttributeType { get; set; }  // "club" | "nationality" | "trophy"
    public required string AttributeValue { get; set; }
}
