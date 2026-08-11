using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore), split from IPlayerStoreRepository (S-107, pure
// refactor — see docs/decisions/0067-player-store-repository-split.md for
// the full "why" shared with S-106's four sibling interfaces): Player's own
// photo/position/birth-year backfill cursors — every read/write cursor for
// PlayerPhotoBackfillService/PlayerPositionBirthYearBackfillService. See
// IPlayerRepository's own doc comment for the shared "no facade" boundary
// note that applies identically here.
public interface IPlayerBackfillRepository
{
    // REQ-214 backfill (S-045): PlayerPhotoBackfillService's read cursor.
    // Every `Player` row created before REQ-214 shipped has PhotoUrl
    // permanently NULL — GetOrCreatePlayersByWikidataQidAsync only ever
    // sets it at row-creation time, never on a later lookup — so this
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

    // REQ-1207 backfill (bug-bundle fix, 2026-08-02): PlayerPositionBirthYearBackfillService's
    // read cursor — the exact mirror of GetPlayersMissingPhotoAsync above,
    // just for Position/BirthYear instead of PhotoUrl. Every `Player` row
    // created before migration 20260727140000_AddPlayerPositionAndBirthYear
    // shipped has both permanently NULL —
    // GetOrCreatePlayersByWikidataQidAsync only ever sets them at
    // row-creation time, never on a later lookup (REQ-1207's own "set once"
    // contract) — so this is the query that finds the backlog.
    //
    // WHERE clause is "either is null," not "both": a Player row could in
    // principle already have one of the two fields set (e.g. a future
    // partial correction) with the other still missing, and re-querying
    // costs nothing extra since this is one combined SPARQL call either way
    // — same reasoning GetPlayersMissingPhotoAsync's own single-field OR
    // would have used if PhotoUrl had a sibling field to combine with.
    //
    // Widened (bug fix, 2026-08-10, bug-bundle): "missing" also includes a
    // Position that's NOT NULL but is still the raw, unresolved Wikidata
    // entity URI a pre-2026-08-02 write-path bug left behind on rows
    // created before that fix — those rows are otherwise permanently
    // invisible to this backfill. No equivalent bad-sentinel state exists
    // for BirthYear, so its half of the WHERE clause is unchanged. See the
    // implementation's own comment for the full reasoning.
    //
    // excludingPlayerIds/batchSize: same "guaranteed run-termination via a
    // this-run-attempted set, no Skip/Take" reasoning as
    // GetPlayersMissingPhotoAsync's own doc comment — see that method's
    // comment for the full "why not Skip/Take" explanation, which applies
    // identically here.
    Task<IReadOnlyList<Player>> GetPlayersMissingPositionOrBirthYearAsync(
        IReadOnlyCollection<Guid> excludingPlayerIds, int batchSize, CancellationToken cancellationToken = default);

    // REQ-1207 backfill (bug-bundle fix, 2026-08-02): batch write, one
    // SaveChangesAsync call for the whole dictionary — mirrors
    // UpdatePlayerPhotosAsync above exactly, including its "a playerId with
    // no matching row is silently skipped rather than throwing" contract
    // (best-effort backfill of already-cached data, not a correctness-
    // critical write). Each entry only overwrites the field(s) the caller
    // actually resolved a value for — a player whose batch response only
    // had a Position (no BirthYear, or vice versa) must not have its
    // already-null-and-still-unresolved other field clobbered with a value
    // that was never looked up this call, and must never overwrite a field
    // that was already set (REQ-1207's "set once" contract extends to this
    // backfill too, not just row-creation) with an OLDER value from a
    // stale/duplicate entry in the same batch. ONE deliberate exception
    // (bug fix, 2026-08-10, bug-bundle): a Position that's still the raw,
    // unresolved Wikidata entity URI (a pre-2026-08-02 write-path bug, not
    // a genuine value) IS overwritten — see the implementation's own
    // comment and GetPlayersMissingPositionOrBirthYearAsync's above for the
    // full reasoning.
    Task UpdatePlayerPositionsAndBirthYearsAsync(
        IReadOnlyDictionary<Guid, PlayerPositionBirthYearUpdate> updatesByPlayerId, CancellationToken cancellationToken = default);
}

// REQ-1207 backfill (bug-bundle fix, 2026-08-02): one player's worth of what
// PlayerPositionBirthYearBackfillService resolved from a
// QueryPlayerPositionsAndBirthYearsByQidsAsync batch. Both nullable, and both
// interpreted by UpdatePlayerPositionsAndBirthYearsAsync as "no update for
// this field" when null — NOT "clear this field to null." A caller that
// resolved only a Position for a given player passes BirthYear: null here to
// mean "leave BirthYear exactly as it already is," never to blank it out.
public record PlayerPositionBirthYearUpdate(string? Position, int? BirthYear);
