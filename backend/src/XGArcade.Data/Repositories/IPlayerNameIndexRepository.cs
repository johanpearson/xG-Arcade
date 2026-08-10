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

    // REQ-211 (2026-07-27 fix): the guess-time live-lookup fallback's own
    // narrow trigger condition — CLAUDE.md's boundary rule ("only trigger a
    // live lookup when the guess matched a real PlayerNameIndex candidate")
    // — needs a cheap, index-backed EXACT match against NormalizedName, not
    // SearchByPrefixAsync's prefix scan (a correctness-narrowing gate like
    // this must never itself become a source of false positives from a
    // partial prefix hit that isn't the guessed name at all).
    // normalizedQuery/normalizedName is expected to already be normalized by
    // the caller — same convention as SearchByPrefixAsync's own parameter.
    Task<bool> ExistsByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);

    // REQ-216/ADR-0057: the wrong-guess photo lookup's own read path — unlike
    // ExistsByNormalizedNameAsync's plain bool (REQ-211's gate only ever
    // needed a yes/no), this needs the matched entry's own PrimaryName: the
    // one canonical-name source REQ-216 can ALWAYS show for a locked,
    // final-incorrect guess that matched a real player, even when
    // ADR-0057's separate Wikidata-only photo lookup times out, errors, or
    // finds nothing (REQ-216's own acceptance criteria: a resolved name with
    // no photo is a normal, silent outcome; a resolved name never depends on
    // that photo lookup succeeding). Same exact-match-against-NormalizedName
    // contract as ExistsByNormalizedNameAsync (never SearchByPrefixAsync's
    // looser prefix scan) — normalizedName is expected to already be
    // normalized by the caller, same convention as every other method here.
    // Returns null when no entry matches at all — REQ-216's "no identity to
    // show" case.
    Task<PlayerNameIndex?> FindByNormalizedNameAsync(string normalizedName, CancellationToken cancellationToken = default);

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
