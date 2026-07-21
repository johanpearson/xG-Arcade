namespace XGArcade.Games.XGGrid;

// REQ-107: the category-type vocabulary grid generation and GridCell use
// ("country" | "club" | "trophy" — REQ-108's Trophy, S-031). Distinct from
// PlayerAttribute.AttributeType's vocabulary ("nationality" | "club" |
// "trophy") — GridGameModule maps between the two where it queries
// Data.PlayerStore.
public static class CategoryPairingRules
{
    public const string Country = "country";
    public const string Club = "club";
    public const string Trophy = "trophy";

    // The only categorical (not data-sparsity) pairing ban — checked before
    // any matching-count query, never as a late-stage filter. Every other
    // combination (Club x Club, Club x Country, and every Trophy pairing —
    // Trophy x Country, Trophy x Club, Trophy x Trophy) is allowed; an
    // overly narrow allowed pairing is handled by REQ-101's ordinary
    // minimum-match retry logic instead.
    public static bool IsAllowedPairing(string rowCategoryType, string colCategoryType) =>
        !(rowCategoryType == Country && colCategoryType == Country);
}
