namespace XGArcade.Data.Entities;

// Category value reference table (ADR-0012, REQ-109) — grid generation
// picks candidate values from this table directly, never derives them ad
// hoc from PlayerAttribute. Tier 0 scope only: Name + WikidataQid, hand
// seeded (~15 rows, MVP-SCOPE.md); ApiFootballTeamId and the incremental
// admin-add resolution flow are Tier 1 (ADR-0012) — not added until then.
public class ClubDefinition
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? WikidataQid { get; set; }   // nullable until resolved
}
