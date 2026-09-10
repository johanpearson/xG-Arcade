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

    // Bug fix (2026-09-10, follow-up to S-231/PR #367, quality-architect
    // finding): a shared constant, deliberately UNLIKE every other
    // Field/AttributeType literal in this codebase (see PlayerAttribute.cs's
    // own "no shared constants across projects" doc comment) — those are
    // stable domain vocabulary exercised by many independent test files over
    // months, where a divergence would likely already be caught. This one is
    // a pure writer/reader bookkeeping coordination flag between exactly two
    // classes (PlayerInternationalStatsRefreshService writes it,
    // PlayerBackfillRepository reads it), invented in the same diff that
    // fixed a real production incident (NOTES.md's 2026-09-10 entry) caused
    // by "checked" vs. "not checked" being indistinguishable. A typo
    // divergence between two independently-typed string literals would
    // silently reproduce that exact bug class one level down — sharing this
    // one via a compile-time reference instead (XGArcade.DataSync already
    // references XGArcade.Data, so this introduces no new dependency
    // direction) removes that risk entirely rather than relying on test
    // coverage to catch it.
    public const string InternationalStatsCheckedField = "international-stats-checked";
    public const string InternationalStatsCheckedValue = "checked";
}
