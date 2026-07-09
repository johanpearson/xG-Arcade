namespace XGArcade.Data.Entities;

// Known nicknames/alternate names (e.g. "Pele" for "Edson Arantes do
// Nascimento") fetched for free from Wikidata's skos:altLabel in the same
// intersection query that resolves a cell's matching players (REQ-103,
// implementation-document.md §6a) — no separate alias-curation system
// needed for Tier 0.
public class PlayerAlias
{
    public Guid PlayerId { get; set; }
    public required string Alias { get; set; }
    public required string NormalizedAlias { get; set; }
}
