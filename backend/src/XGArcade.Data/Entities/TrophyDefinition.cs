namespace XGArcade.Data.Entities;

// v1 category types are Country, Club, Trophy (REQ-108). Trophy is
// reference data, not hardcoded — adding a new recognized trophy is a row
// insert, not a code change. Exists in the schema per S-003's scope, but
// unused by grid generation until the Trophy category ships (Tier 1,
// MVP-SCOPE.md).
public class TrophyDefinition
{
    public Guid Id { get; set; }
    public required string Name { get; set; }        // e.g. "FIFA World Cup", "Ballon d'Or"
    public bool IsTeamTrophy { get; set; }            // team competition vs. individual award —
                                                        // informs display copy, not matching logic
    public string? WikidataQid { get; set; }          // nullable; resolved manually, small table (ADR-0012)
}
