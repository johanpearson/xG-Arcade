namespace XGArcade.Games.XGGrid;

// REQ-107: the category-type vocabulary grid generation and GridCell use
// ("country" | "club" | "trophy" — REQ-108's Trophy, S-031). Distinct from
// PlayerAttribute.AttributeType's vocabulary ("nationality" | "club" |
// "trophy") — MapAttributeType below maps between the two for every caller
// that queries Data.PlayerStore.
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

    // PlayerAttribute.AttributeType's vocabulary ("nationality" | "club" |
    // "trophy") differs from GridCell's RowCategoryType/ColCategoryType
    // vocabulary ("country" | "club" | "trophy") only for Country — Trophy
    // happens to be spelled identically in both, per REQ-108's acceptance
    // text. Same mapping GridGenerationService.GetMatchCountAsync needs for
    // grid generation.
    //
    // S-119 (pure refactor): moved here from GridGameModule — a single
    // fixed, dependency-free vocabulary table with exactly one correct
    // implementation, shared post-split by GridNameMatcher,
    // GridGenerationService.GetMatchCountAsync, and
    // GridLiveLookupDispatcher.TryRefreshCellAsync. Unlike a stateful
    // per-entity helper (deliberately duplicated per class elsewhere in this
    // split), this table has no state and no per-caller variation, so
    // duplicating it three ways would only risk the copies silently
    // drifting.
    public static string MapAttributeType(string categoryType) => categoryType switch
    {
        Country => "nationality",
        Club => Club,
        Trophy => Trophy,
        _ => throw new GuessScoringException($"Unknown category type '{categoryType}'."),
    };
}
