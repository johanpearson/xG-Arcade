namespace XGArcade.Data.Entities;

public class Player
{
    public Guid Id { get; set; }

    private string _fullName = string.Empty;

    public required string FullName
    {
        get => _fullName;
        set
        {
            _fullName = value;
            // Kept in lockstep with FullName rather than computed by each
            // caller separately — S-009's guess-time name matching (REQ-208's
            // Tier 0 "simple half": lowercase/diacritics/punctuation only, no
            // PlayerNameIndex/COMP-10, MVP-SCOPE.md) queries this column
            // directly, and every existing Player construction site (S-006's
            // WikidataLookupService, plus test seeding) already sets FullName,
            // so nothing else needs to change to keep it populated.
            NormalizedFullName = PlayerNameNormalizer.Normalize(value);
        }
    }

    public string NormalizedFullName { get; private set; } = string.Empty;

    // Dedup identity: the same player returned by two different intersection
    // queries (France×Arsenal and Brazil×Barcelona, say) must upsert into ONE
    // row, keyed on this — never insert-blindly per query. Nullable only for
    // a future non-Wikidata source (Tier 1); unique index where not null.
    public string? WikidataQid { get; set; }
}
