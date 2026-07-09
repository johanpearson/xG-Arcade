namespace XGArcade.Data.Entities;

// Manual correction, always wins over PlayerData — see REQ-501.
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
