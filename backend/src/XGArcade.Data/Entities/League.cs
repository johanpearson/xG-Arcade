namespace XGArcade.Data.Entities;

// COMP-02 (Core.Leagues) entity — persisted here alongside every other
// entity in the single shared DbContext (ADR-0014). One row always exists
// with Type="global" (REQ-401); custom leagues (Type="custom", InviteCode,
// CreatedByUserId) are REQ-402/403, created via
// LeagueService.CreateCustomLeagueAsync (Core.Leagues) — pulled forward
// ahead of MVP-SCOPE.md's original Tier 1 placement for this specific
// story; see that story's own notes for the explicit scope call.
public class League
{
    public Guid Id { get; set; }

    public required string Name { get; set; }
    public required string Type { get; set; } // "global" | "custom"

    public string? InviteCode { get; set; }     // REQ-402: set for "custom", always null for "global"
    public Guid? CreatedByUserId { get; set; }   // REQ-402: set for "custom", always null for "global"
}
