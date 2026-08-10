namespace XGArcade.Data.Entities;

// ADR-0042/S-079 (COMP-06, alongside PlayerAttribute/PlayerAlias/
// PlayerOverride): xG Path's (COMP-11, REQ-1201-REQ-1206) ordered, dated
// career-stint log — structurally different from PlayerAttribute's flat
// membership set (see PlayerAttribute's own doc comment for why that shape
// is deliberately date/order/count-less). Populated by
// WikidataLookupService.LookupAndPersistAsync ALONGSIDE (never instead of)
// PlayerAttribute's "club" rows, from the qualifiers (P580/P582/P1350) on
// the same P54 statement that query already fetches — no new SPARQL query
// shape, no new external call.
//
// Surrogate Id primary key, unlike PlayerAttribute/PlayerAlias's natural
// composite keys: this is an ordered, potentially-repeating log, not a
// membership set deduplicated by its own natural key — a player can have
// two separate stints at the same club (e.g. a loan, then a later
// permanent return), which must be two distinct rows.
//
// xG Grid's correctness-checking path (HasEffectiveAttributeAsync or
// anything upstream of it) must NEVER read this table — it continues to
// read only PlayerAttribute/PlayerOverride. Only xG Path's puzzle
// generation/clue-reveal reads PlayerCareerStint, and never reads
// PlayerAttribute for club data. See ADR-0042's "For AI agents" section.
public class PlayerCareerStint
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public required string ClubName { get; set; }
    public int StartYear { get; set; }

    // Null = an ongoing stint (no Wikidata P582 "end time" qualifier on
    // this statement yet).
    public int? EndYear { get; set; }

    // Chronological position among this player's FULL stint set (0-based),
    // resolved at write time (WikidataLookupService/
    // IPlayerStoreRepository.AddCareerStintsAsync) so no reader needs to
    // re-sort by date — re-numbered across every row for the player
    // (existing and newly-added) whenever a new stint is persisted, not
    // just the new ones.
    public int SequenceOrder { get; set; }

    // Null, NEVER 0, when Wikidata's P1350 ("number of matches played")
    // qualifier isn't present for this statement — REQ-1201-REQ-1206
    // renders this as "count unknown," which a placeholder 0 would
    // misleadingly contradict (zero real appearances vs. no recorded
    // count).
    public int? AppearanceCount { get; set; }
}
