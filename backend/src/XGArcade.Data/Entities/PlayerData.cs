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

    // REQ-503 (2026-07-20 extension): set together, only by the admin
    // "approve" action (POST /admin/player-data/approve) flipping this row's
    // Confidence to "verified" — never by a routine sync/live-lookup write
    // (WikidataLookupService always leaves both null). Same
    // "who and when, on the row itself" shape as
    // PlayerOverride.LockedByAdminId/LockedAt — no separate audit-log table
    // here either.
    public Guid? ApprovedByAdminId { get; set; }
    public DateTime? ApprovedAt { get; set; }
}
