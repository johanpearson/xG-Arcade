using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-10 (Data.PlayerNameIndex): a deliberately separate interface from
// IPlayerStoreRepository (COMP-06) — see ADR-0007 and architecture-
// document.md boundary rule 5. This is the ONLY path to PlayerNameIndex.
// Never merge this interface with IPlayerStoreRepository, and never call
// IPlayerStoreRepository from this repository's implementation — doing so
// would reintroduce the exact autocomplete-leaks-correctness problem
// ADR-0007 exists to prevent.
public interface IPlayerNameIndexRepository
{
    // REQ-207: autocomplete's only read path. normalizedQuery is expected to
    // already be normalized (PlayerNameNormalizer.Normalize) by the caller —
    // this repository does not normalize itself, so it's obvious from the
    // call site that normalization happened exactly once.
    Task<IReadOnlyList<PlayerNameIndex>> SearchByPrefixAsync(
        string normalizedQuery, int limit, CancellationToken cancellationToken = default);

    // PlayerNameIndexImporter's bulk-refresh write path. Upserts keyed on
    // PlayerId — an entry for a player already in the index is corrected in
    // place, not duplicated (same "correct in place, don't just blindly
    // insert" discipline as ReferenceDataSeeder.SeedAsync, see its own doc
    // comment / S-037's CHANGELOG entry for the precedent this follows).
    // Note this PlayerId is PlayerNameIndex's own synthetic, QID-derived key
    // (see the entity's doc comment) — it has no guaranteed relationship to
    // any separately-created Player.Id/PlayerAttribute.PlayerId (COMP-06) for
    // the same real person; reconciling the two is unbuilt and out of scope
    // here.
    Task UpsertManyAsync(IEnumerable<PlayerNameIndex> entries, CancellationToken cancellationToken = default);
}
