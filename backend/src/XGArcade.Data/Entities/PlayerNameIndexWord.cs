namespace XGArcade.Data.Entities;

// COMP-10 (Data.PlayerNameIndex) — see ADR-0007, ADR-0044, and
// architecture-document.md boundary rule 5. A child row of PlayerNameIndex,
// one per space-separated word in NormalizedName (e.g. "zlatan ibrahimovic"
// produces two rows: "zlatan" and "ibrahimovic"). Exists ONLY so
// SearchByPrefixAsync can run a genuine indexed StartsWith on a single word
// column instead of a leading-wildcard Contains()/LIKE '%query%' over
// NormalizedName, which Postgres cannot use a plain B-tree index for at any
// real scale (REQ-208's 2026-07-26 correction). Deliberately denormalized
// (a name with N words produces N rows) rather than reusing NormalizedName's
// own index with a different query shape — a per-word row keeps every
// candidate match expressible as a plain, index-backed prefix comparison.
//
// No FK to Player (same reasoning as PlayerNameIndex itself — this is a
// COMP-10 bulk-imported structure, entirely independent of COMP-06's Player
// id space). It DOES have a FK to PlayerNameIndex.PlayerId (cascade delete)
// since it's that row's own decomposition, not a separate bulk-imported fact.
public class PlayerNameIndexWord
{
    public Guid PlayerId { get; set; }

    // Already normalized (PlayerNameNormalizer.Normalize ran on the whole
    // name before this was split) — never re-normalized here, so a word
    // never needs stripping/lowercasing again.
    public required string Word { get; set; }
}
