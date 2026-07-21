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

    // REQ-114/ADR-0035: none of the four home nations (England, Scotland,
    // Wales, Northern Ireland) are sovereign states, so they can't be
    // queried via P27 ("country of citizenship") the way every other
    // seeded country can — English/Scottish/Welsh/Northern Irish players'
    // P27 is uniformly United Kingdom (Q145, already seeded). Wikidata's
    // P1532 ("country for sport") is the property that actually means
    // "country represented in international competition." This flag is a
    // per-row query-property switch, not a new category type: it stays
    // false (the default) for every ordinary sovereign-state country,
    // seeded true only for the four home nations, and lets WikidataClient/
    // WikidataLookupService pick P1532 over P27 for exactly those rows
    // while everything downstream (PlayerAttribute.AttributeType
    // "nationality", grid generation's pairing logic, CategoryPairingRules)
    // stays completely unaware of the distinction — a national-team value
    // like "England" is just another value in the same vocabulary as
    // "United Kingdom", not a conceptually different attribute type. See
    // ADR-0035 for the alternative considered (a separate
    // NationalTeamDefinition table/category type) and why it was rejected.
    public bool UsesCountryForSportProperty { get; set; }
}
