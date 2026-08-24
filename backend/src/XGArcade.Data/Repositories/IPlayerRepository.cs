using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore), split from the original, now-deleted
// IPlayerStoreRepository (S-106/S-107, pure refactor — see ADR-0067 for the
// full split): the core Player CRUD concern. Games.XGGrid (COMP-05) and any
// future game module must still reach Player rows only through this
// interface (or one of its siblings — IPlayerDataRepository,
// IPlayerAttributeRepository, IPlayerAliasRepository, IPlayerOverrideRepository,
// IPlayerBackfillRepository, IPlayerCareerStintRepository,
// IPlayerDataQualityRepository) — see architecture-document.md boundary
// rule 1. Never merge back into a single facade — ADR-0067's own "no facade
// unless real need" rule.
public interface IPlayerRepository
{
    Task<Player?> GetPlayerByWikidataQidAsync(string wikidataQid, CancellationToken cancellationToken = default);
    Task<Player?> GetPlayerByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-201/REQ-303 display fix: a correct guess's canonical, properly-cased
    // name (Player.FullName) for a batch of PlayerAnswerIds in one query,
    // rather than one GetPlayerByIdAsync call per correctly-guessed cell —
    // same bulk-lookup shape as GetCorrectByCellIdsAsync's caller uses.
    Task<IReadOnlyDictionary<Guid, Player>> GetPlayersByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);

    Task<Player> AddPlayerAsync(Player player, CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): batched counterpart to
    // GetPlayerByWikidataQidAsync + AddPlayerAsync's old "check-then-insert"
    // pattern — one indexed SELECT for every WikidataQid already known, plus
    // one INSERT for whichever ones aren't, instead of one round-trip pair
    // PER PLAYER (WikidataLookupService.PersistMatchesAsync's own doc
    // comment has the full "why this mattered" story: intersection queries
    // never LIMIT, so a popular cell's dozens of matches made the old
    // per-player loop the dominant cost of a slow guess). Same upsert-by-
    // WikidataQid semantics as the single-item path: a QID already known
    // reuses its existing Player row, never a duplicate. Keyed by
    // WikidataQid in the returned dictionary, not Player.Id — that's the
    // natural join key every caller already has on hand
    // (WikidataPlayerMatch.WikidataQid). One SaveChangesAsync call for the
    // whole batch (load-then-SaveChangesAsync, coding-guidelines.md).
    //
    // S-129 concurrency fix: a concurrent caller (REQ-211's guess-time
    // fallback, PlayerCareerPrefetchService's own batch sweep, or a second
    // admin commit) can race this method for the same brand-new
    // WikidataQid — Player.WikidataQid's filtered unique index
    // (XGArcadeDbContext.cs) lets only one INSERT win. This method now
    // handles that the same way LeagueRepository.GetOrCreateGlobalLeagueAsync
    // / PathInstanceRepository.GetOrCreateCycleStateAsync already do for
    // their own singleton rows: catch the DbUpdateException, detach the
    // loser(s), and re-fetch the winner instead of letting a raw
    // DbUpdateException/500 escape. PlayerCreationResult.WasCreated is
    // computed atomically at the point of insert (true only for a QID whose
    // row this call itself actually persisted) — never derived from a
    // separate, racy pre-read.
    Task<IReadOnlyDictionary<string, PlayerCreationResult>> GetOrCreatePlayersByWikidataQidAsync(
        IReadOnlyList<PlayerCreationRequest> requests, CancellationToken cancellationToken = default);

    // REQ-208: guess-time name matching's primary-name path — queries
    // Player.NormalizedFullName directly. Still never PlayerNameIndex/
    // COMP-10 (ADR-0007's autocomplete/correctness separation, permanent,
    // not a Tier boundary).
    Task<IReadOnlyList<Player>> GetPlayersByNormalizedFullNameAsync(
        string normalizedFullName, CancellationToken cancellationToken = default);

    // REQ-513 (GitHub issue #239): this pair is a DELIBERATE, NARROW
    // EXCEPTION to this repository's own "Player fields are set only at
    // creation, never touched again" rule (see
    // GetOrCreatePlayersByWikidataQidAsync's own comment, and Player.cs's
    // FullName/Position/BirthYear/PhotoUrl doc comments, all of which
    // describe that set-once contract) — NOT a reversal of it. Every
    // AUTOMATIC path (grid generation, cache warming, the guess-time live
    // fallback, the position/birth-year/photo backfills) is completely
    // unaffected and still never overwrites an existing Player value.
    // GetPlayerForRefreshAsync/UpdatePlayerAsync exist ONLY for the
    // explicit, single-player, ADMIN-TRIGGERED refresh action in
    // AdminEndpoints (POST /admin/players/{id}/refresh-from-wikidata) — an
    // admin re-applying already-trusted Wikidata source data against the
    // player's own already-stored WikidataQid, never an admin-typed value
    // (contrast REQ-501's PlayerOverride path).
    //
    // GetPlayerForRefreshAsync returns a TRACKED entity — unlike
    // GetPlayerByIdAsync's AsNoTracking read — because its only caller needs
    // to mutate the fields that differ from a fresh Wikidata fetch and then
    // persist exactly that same entity instance via UpdatePlayerAsync
    // (load-then-SaveChangesAsync, docs/coding-guidelines.md; never
    // ExecuteUpdateAsync, which the InMemory test provider can't translate).
    Task<Player?> GetPlayerForRefreshAsync(Guid id, CancellationToken cancellationToken = default);

    Task UpdatePlayerAsync(Player player, CancellationToken cancellationToken = default);
}

// Bug-bundle fix (2026-07-27): one match's worth of the data needed to
// create a Player row if GetOrCreatePlayersByWikidataQidAsync doesn't
// already have one for this WikidataQid — mirrors the three Player fields
// WikidataLookupService's old per-player GetOrCreatePlayerAsync used to set
// at creation time.
//
// Position/BirthYear (REQ-1207/S-082): trailing optional params, defaulted
// to null so every existing 3-arg call site keeps compiling — same
// set-once-at-creation contract as PhotoUrl (GetOrCreatePlayersByWikidataQidAsync
// never writes these on a Player row that already exists).
public record PlayerCreationRequest(
    string WikidataQid, string FullName, string? PhotoUrl, string? Position = null, int? BirthYear = null);

// S-129: GetOrCreatePlayersByWikidataQidAsync's per-QID result — WasCreated
// is true only for a QID whose Player row THIS call actually inserted
// (computed atomically inside the method, including its own concurrent-
// insert recovery, never via a separate pre-read by the caller). Most
// existing callers (WikidataLookupService, PlayerCareerPrefetchService)
// only ever need Player and can ignore WasCreated entirely.
public record PlayerCreationResult(Player Player, bool WasCreated);
