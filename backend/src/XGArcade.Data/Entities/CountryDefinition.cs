namespace XGArcade.Data.Entities;

// Category value reference table (ADR-0012, REQ-109) — grid generation
// picks candidate values from this table directly, never derives them ad
// hoc from PlayerAttribute. Tier 0: hand-seeded (~15-20 rows, MVP-SCOPE.md),
// not the full bulk-import path described in ADR-0012.
public class CountryDefinition
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? WikidataQid { get; set; }   // nullable until resolved
}
