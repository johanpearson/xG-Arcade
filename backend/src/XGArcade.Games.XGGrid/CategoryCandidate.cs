namespace XGArcade.Games.XGGrid;

// A row/column header candidate, abstracted away from which reference
// table (CountryDefinition/ClubDefinition/TrophyDefinition) it came from
// — REQ-107's generalized pairing selection (S-030, extended S-031)
// needs to treat all three uniformly.
//
// ADR-0089: `CategoryType` (CategoryPairingRules.Country/Club/Trophy) is
// what makes that uniform treatment possible now that each row/column
// header picks its own category type independently, rather than the whole
// grid instance sharing one axis-wide type via the now-removed
// GridGenerationService.SelectPairing/PoolFor. Every CategoryCandidate
// constructed anywhere in this project must carry the type it actually
// came from — GridGenerationService.BuildCells/CreateCell read it straight
// onto GridCell.RowCategoryType/ColCategoryType, per header, not per axis.
//
// REQ-114/ADR-0035: `UsesCountryForSportProperty` carries
// CountryDefinition's per-row query-property flag through generation
// and the guess-time fallback to the point
// IGridLiveLookupDispatcher.LookupMatchesAsync actually dispatches a live
// Wikidata call — the smaller, cleaner diff versus re-resolving the full
// CountryDefinition row by name at dispatch time (which PickHeadersAsync's
// hot loop would otherwise do once per GetMatchCountAsync call, a real
// extra query cost that ResolveCandidateAsync's single per-guess lookup
// doesn't have to justify). Meaningless for Club/Trophy candidates —
// always false there, never read for those types.
//
// ADR-0061: `IsTeamTrophy` carries TrophyDefinition's own per-row query-
// shape flag through the same path, for exactly the same reason —
// without it, the throwaway TrophyDefinition
// IGridLiveLookupDispatcher.LookupMatchesAsync reconstructs at dispatch
// time would always default IsTeamTrophy to false, silently routing every
// live lookup for a team trophy (World Cup, Champions League) through the
// individual-award P166 query, which structurally can never match a team
// competition. Meaningless for Country/Club candidates — always false
// there, never read for those types.
public readonly record struct CategoryCandidate(
    string Name, string CategoryType, string? WikidataQid, bool UsesCountryForSportProperty = false, bool IsTeamTrophy = false);
