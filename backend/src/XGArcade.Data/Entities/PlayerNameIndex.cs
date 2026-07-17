namespace XGArcade.Data.Entities;

// COMP-10 (Data.PlayerNameIndex) — see ADR-0007 and architecture-document.md
// boundary rule 5. Broad, bulk-imported (PlayerNameIndexImporter), refreshed
// periodically as a whole, deliberately separate from PlayerAttribute/
// PlayerOverride (COMP-06): used ONLY for autocomplete suggestions, never
// for correctness-checking. A PlayerNameIndex row existing for a player with
// zero PlayerAttribute rows is a normal, expected state, not a bug.
public class PlayerNameIndex
{
    // Same id space as Player.Id (COMP-06) — a suggestion resolves to the
    // same player identity guess-submission ultimately checks against, but
    // this table itself is never joined against PlayerAttribute/PlayerOverride
    // for the purpose of deciding what to suggest.
    public Guid PlayerId { get; set; }

    public required string PrimaryName { get; set; }

    // Lowercased, diacritics/punctuation stripped — PlayerNameNormalizer.Normalize,
    // reused rather than reimplemented (REQ-208's shared normalize()).
    public required string NormalizedName { get; set; }

    public int? BirthYear { get; set; }
    public string? PrimaryNationality { get; set; }
    public string? PhotoUrl { get; set; }
}
