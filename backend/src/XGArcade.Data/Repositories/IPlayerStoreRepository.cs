using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore): the only path to PlayerData/PlayerOverride/
// PlayerAttribute/PlayerAlias. Games.XGGrid (COMP-05) and any future game
// module must reach player data only through this interface — see
// architecture-document.md boundary rule 1.
public interface IPlayerStoreRepository
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

    // REQ-208 (Tier 0's simple half, MVP-SCOPE.md): guess-time name matching
    // queries Player.NormalizedFullName directly — no PlayerNameIndex/COMP-10
    // (deferred, Tier 1) and no PlayerAlias (also deferred for matching
    // purposes — "defer the alias table" in MVP-SCOPE.md's Tier 0 scoping).
    Task<IReadOnlyList<Player>> GetPlayersByNormalizedFullNameAsync(
        string normalizedFullName, CancellationToken cancellationToken = default);

    Task AddPlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default);

    // REQ-503 (S-012): the admin review view's candidate list — every
    // PlayerData row still awaiting an admin's approve/correct/remove
    // decision.
    Task<IReadOnlyList<PlayerData>> GetUnverifiedPlayerDataAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerAttribute>> GetPlayerAttributesAsync(
        string attributeType, string attributeValue, CancellationToken cancellationToken = default);
    Task AddPlayerAttributeAsync(PlayerAttribute attribute, CancellationToken cancellationToken = default);

    // Grid generation's candidate-matching query (REQ-101): how many
    // players satisfy both category values at once. A single indexed join
    // rather than fetching both attribute lists and intersecting in memory.
    Task<int> CountPlayersWithBothAttributesAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerAlias>> GetPlayerAliasesAsync(Guid playerId, CancellationToken cancellationToken = default);
    Task AddPlayerAliasAsync(PlayerAlias alias, CancellationToken cancellationToken = default);

    Task<PlayerOverride?> GetOverrideAsync(Guid playerId, string field, CancellationToken cancellationToken = default);
    Task<PlayerOverride?> GetOverrideByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default);
    Task UpdateOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default);
    Task<bool> DeleteOverrideAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-203: "an override always takes precedence over synced/unverified
    // data" — the single effective-data check every correctness path
    // (grid-generation's cache read is count-only and doesn't need this;
    // guess-checking, S-009, does) must use, so override precedence is
    // enforced in exactly one place (architecture-document.md's Data
    // integrity row).
    Task<bool> HasEffectiveAttributeAsync(
        Guid playerId, string attributeType, string attributeValue, CancellationToken cancellationToken = default);

    // REQ-214 backfill (S-045): PlayerPhotoBackfillService's read cursor.
    // Every `Player` row created before REQ-214 shipped has PhotoUrl
    // permanently NULL — WikidataLookupService.GetOrCreatePlayerAsync only
    // ever sets it at row-creation time, never on a later lookup — so this
    // is the query that finds the backlog. excludingPlayerIds accumulates
    // every player ID the caller has already attempted THIS RUN (whether
    // that attempt succeeded or failed) so repeated calls make guaranteed
    // progress toward an empty result and the caller's loop terminates:
    // Guid has no LINQ-translatable ordering operator to keyset-paginate
    // on the way PlayerNameIndex's string-keyed queries can, and a plain
    // Skip/Take here would silently skip untouched rows on the next page —
    // each successfully-backfilled batch removes rows from this query's own
    // WHERE PhotoUrl IS NULL filter, shrinking the underlying set between
    // calls. Fine at Tier 0's player-pool scale (a few thousand rows);
    // revisit if the pool grows enough that a SQL IN list of that size
    // becomes a real cost. Never loads the whole table — bounded by
    // batchSize per call.
    Task<IReadOnlyList<Player>> GetPlayersMissingPhotoAsync(
        IReadOnlyCollection<Guid> excludingPlayerIds, int batchSize, CancellationToken cancellationToken = default);

    // REQ-214 backfill (S-045): batch write, one SaveChangesAsync call for
    // the whole dictionary — not one round-trip per player. Load-then-
    // SaveChangesAsync (docs/coding-guidelines.md), never
    // ExecuteUpdateAsync — the InMemory test provider can't translate it.
    // A playerId with no matching row (already deleted, e.g. by
    // purge-player-pool, between the read and this write) is silently
    // skipped rather than throwing — this is a best-effort backfill of
    // already-cached data, not a correctness-critical write.
    Task UpdatePlayerPhotosAsync(
        IReadOnlyDictionary<Guid, string> photoUrlByPlayerId, CancellationToken cancellationToken = default);
}
