namespace XGArcade.Data.Entities;

// COMP-10 (Data.PlayerNameIndex) — see ADR-0007 and architecture-document.md
// boundary rule 5. Broad, bulk-imported (PlayerNameIndexImporter), refreshed
// periodically as a whole, deliberately separate from PlayerAttribute/
// PlayerOverride (COMP-06): used ONLY for autocomplete suggestions, never
// for correctness-checking. A PlayerNameIndex row existing for a player with
// zero PlayerAttribute rows is a normal, expected state, not a bug.
public class PlayerNameIndex
{
    // A synthetic key local to PlayerNameIndex/COMP-10 — a deterministic hash
    // of the Wikidata QID (see PlayerNameIndexImporter.DeterministicPlayerId),
    // NOT the same id space as Player.Id (COMP-06), which is a plain
    // Guid.NewGuid() with no relationship to the QID (WikidataLookupService).
    // For the same real person these two GUIDs will practically always
    // differ, and nothing today reconciles them — there is no lookup that
    // maps a PlayerNameIndex row to its corresponding Player/PlayerAttribute
    // row for the same person. If a future story (e.g. REQ-208's name
    // resolution) ever needs that reconciliation, it must be built
    // deliberately; do not assume or wire up an implicit relationship
    // between the two id spaces — see ADR-0007 and the note this correction
    // itself responds to (S-032 quality-gate review, 2026-07-17).
    public Guid PlayerId { get; set; }

    public required string PrimaryName { get; set; }

    // Lowercased, diacritics/punctuation stripped — PlayerNameNormalizer.Normalize,
    // reused rather than reimplemented (REQ-208's shared normalize()).
    public required string NormalizedName { get; set; }

    public int? BirthYear { get; set; }
    public string? PrimaryNationality { get; set; }
    public string? PhotoUrl { get; set; }
}
