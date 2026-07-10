namespace XGArcade.Data.Entities;

// COMP-02 (Core.Leagues) entity — persisted here alongside every other
// entity in the single shared DbContext (ADR-0014). Tier 0 only ever
// creates one row here, Type="global" (REQ-401) — custom leagues
// (Type="custom", InviteCode, CreatedByUserId) are REQ-402-404, deferred
// per MVP-SCOPE.md, but the shape is included now so a Tier 1 custom
// league doesn't need a schema migration to grow into it.
public class League
{
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public required string Type { get; set; } // "global" | "custom" — Tier 0 only ever writes "global"

    public string? InviteCode { get; set; }     // Tier 1 (REQ-402), always null for "global"
    public Guid? CreatedByUserId { get; set; }   // Tier 1 (REQ-402), always null for "global"
}
