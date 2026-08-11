using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore), split from IPlayerStoreRepository (S-106, pure
// refactor — see that story's own doc comment/CHANGELOG entry for why): the
// core Player CRUD concern. Games.XGGrid (COMP-05) and any future game
// module must still reach Player rows only through this interface (or one of
// its S-106/S-107 siblings — IPlayerDataRepository, IPlayerAttributeRepository,
// IPlayerAliasRepository, and the still-undivided IPlayerStoreRepository) —
// see architecture-document.md boundary rule 1. Never merge back into a
// single facade — S-106's own "no facade unless real need" rule.
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
    Task<IReadOnlyDictionary<string, Player>> GetOrCreatePlayersByWikidataQidAsync(
        IReadOnlyList<PlayerCreationRequest> requests, CancellationToken cancellationToken = default);

    // REQ-208: guess-time name matching's primary-name path — queries
    // Player.NormalizedFullName directly. Still never PlayerNameIndex/
    // COMP-10 (ADR-0007's autocomplete/correctness separation, permanent,
    // not a Tier boundary).
    Task<IReadOnlyList<Player>> GetPlayersByNormalizedFullNameAsync(
        string normalizedFullName, CancellationToken cancellationToken = default);
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
