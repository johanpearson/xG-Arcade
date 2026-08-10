namespace XGArcade.Data.Entities;

// v1 category types are Country, Club, Trophy (REQ-108). Trophy is
// reference data, not hardcoded — adding a new recognized trophy is a row
// insert, not a code change. Shipped for individual awards in S-031 and
// extended to team competitions in ADR-0061 — grid generation, guess-
// scoring, and REQ-211's guess-time live-lookup fallback all treat Trophy as
// a full third category type, not a dormant placeholder.
public class TrophyDefinition
{
    public Guid Id { get; set; }
    public required string Name { get; set; }        // e.g. "FIFA World Cup", "Ballon d'Or"

    // Team competition (e.g. FIFA World Cup, UEFA Champions League) vs.
    // individual award (e.g. Ballon d'Or). ADR-0061: this now DRIVES query
    // dispatch, not just display copy — WikidataLookupService.
    // LookupAndPersistTrophyCountryAsync/LookupAndPersistTrophyClubAsync
    // branch on it to pick between the individual-award P166 query (S-031)
    // and the team-competition P1344/P3450/P1346 join (ADR-0061); see that
    // ADR for why a team trophy has no P166-equivalent statement at all.
    public bool IsTeamTrophy { get; set; }

    // Nullable; resolved manually, small table (ADR-0012). ADR-0061: this
    // field has a DUAL meaning depending on IsTeamTrophy — for an individual
    // award (IsTeamTrophy = false) it's the award item itself, queried
    // directly via P166. For a team competition (IsTeamTrophy = true) it
    // must be the competition SERIES item (e.g. "FIFA World Cup," "UEFA
    // Champions League"), never a specific edition — the query joins
    // editions to it via P3450, so a per-edition QID would silently match
    // nothing. See ADR-0061's "Decision" section for the full reasoning.
    public string? WikidataQid { get; set; }
}
