namespace XGArcade.Games.XGGrid;

// REQ-107: the category-type vocabulary grid generation and GridCell use
// ("country" | "club" | "trophy" — REQ-108's Trophy is Tier 1, unused until
// then). Distinct from PlayerAttribute.AttributeType's vocabulary
// ("nationality" | "club" | "trophy") — GridGameModule maps between the two
// where it queries Data.PlayerStore.
public static class CategoryPairingRules
{
    public const string Country = "country";
    public const string Club = "club";

    // The only categorical (not data-sparsity) pairing ban — checked before
    // any matching-count query, never as a late-stage filter. Every other
    // combination (Club x Club, Club x Country, and Trophy's Tier 1
    // pairings) is allowed; an overly narrow allowed pairing is handled by
    // REQ-101's ordinary minimum-match retry logic instead.
    public static bool IsAllowedPairing(string rowCategoryType, string colCategoryType) =>
        !(rowCategoryType == Country && colCategoryType == Country);
}
