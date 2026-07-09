namespace XGArcade.Data.Entities;

// Raw, per-source data. Never read directly for correctness-checking — see
// PlayerAttribute for the effective, denormalized view. COMP-06 owns this
// table; see ADR-0007 for why PlayerNameIndex must never merge with it.
public class PlayerData
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public required string Field { get; set; }     // e.g. "nationality", "club"
    public required string Value { get; set; }
    public required string Source { get; set; }     // "wikidata" | "api_football" | "live_lookup"
    public required string Confidence { get; set; } // "verified" | "unverified"
    public DateTime SyncedAt { get; set; }
}
