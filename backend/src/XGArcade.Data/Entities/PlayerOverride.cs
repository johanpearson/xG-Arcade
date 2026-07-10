namespace XGArcade.Data.Entities;

// Manual correction, always wins over PlayerData — see REQ-501. A row here
// replaces the *entire* attribute type for correctness-checking, not just
// the one cached value it corrects — see ADR-0015 for why, and
// IPlayerStoreRepository.HasEffectiveAttributeAsync for the read side.
public class PlayerOverride
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public required string Field { get; set; }
    public required string Value { get; set; }
    public required string Reason { get; set; }
    public Guid LockedByAdminId { get; set; }
    public DateTime LockedAt { get; set; }
}
