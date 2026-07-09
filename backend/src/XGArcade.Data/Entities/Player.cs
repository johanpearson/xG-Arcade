namespace XGArcade.Data.Entities;

public class Player
{
    public Guid Id { get; set; }

    public required string FullName { get; set; }

    // Dedup identity: the same player returned by two different intersection
    // queries (France×Arsenal and Brazil×Barcelona, say) must upsert into ONE
    // row, keyed on this — never insert-blindly per query. Nullable only for
    // a future non-Wikidata source (Tier 1); unique index where not null.
    public string? WikidataQid { get; set; }
}
