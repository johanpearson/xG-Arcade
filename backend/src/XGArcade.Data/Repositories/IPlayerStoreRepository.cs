using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore): the only path to PlayerOverride/PlayerCareerStint/
// ConfirmedLowMatchPair/PairLookupFailure, plus Player's own photo/position/
// birth-year backfill cursor. Games.XGGrid (COMP-05) and any future game
// module must reach player data only through this interface or one of its
// S-106 siblings — IPlayerRepository (core Player CRUD),
// IPlayerDataRepository (PlayerData), IPlayerAttributeRepository
// (PlayerAttribute), IPlayerAliasRepository (PlayerAlias) — see
// architecture-document.md boundary rule 1.
//
// S-106 (docs/backlog.md, Epic 8, pure refactor, no behavior change) split
// the four interfaces above out of what was originally one 43-method
// interface — the four sibling interfaces now own the concerns their own
// doc comments describe; this interface keeps only the Override/backfill/
// CareerStint/data-quality-tracking methods S-107 (independent, not yet
// landed) will split out next. Do not delete this interface/its
// implementation until S-107 lands — see that story's own scope note.
public interface IPlayerStoreRepository
{
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

    // ADR-0042/S-079: xG Path's (COMP-11) read of a player's full,
    // chronologically-ordered career-stint log — never called from any
    // correctness-checking path (xG Grid continues to read only
    // PlayerAttribute/PlayerOverride via HasEffectiveAttributeAsync).
    Task<IReadOnlyList<PlayerCareerStint>> GetCareerStintsAsync(
        Guid playerId, CancellationToken cancellationToken = default);

    // ADR-0042/S-079: adds newStints (each one's SequenceOrder is ignored —
    // overwritten here) and re-sequences the player's FULL stint set
    // (existing rows + newStints) chronologically by (StartYear ascending,
    // then EndYear ascending with an ongoing/null stint sorted last), 0..N-1
    // — a stint discovered later that chronologically precedes existing
    // ones must still produce a correctly-ordered SequenceOrder for every
    // row, not just the newly-added ones. One SaveChangesAsync call for the
    // whole re-sequenced set (load-then-SaveChangesAsync,
    // coding-guidelines.md), never ExecuteUpdateAsync. No-op (no
    // SaveChangesAsync call at all) when newStints is empty — callers
    // (WikidataLookupService) are expected to have already filtered out
    // stints that already exist for this player via GetCareerStintsAsync,
    // so idempotency is the caller's responsibility, same discipline
    // PersistMatchesAsync/QueueAttribute already follow for
    // PlayerData/PlayerAttribute/PlayerAlias.
    Task AddCareerStintsAsync(
        Guid playerId, IReadOnlyList<PlayerCareerStint> newStints, CancellationToken cancellationToken = default);

    // REQ-1201 perf fix (2026-08-03, NOTES.md "PlayerCareerStint's 'few
    // thousand rows' full-table-read assumption is now stale"): the cheap
    // first pass of xG Path's puzzle-generation eligibility check —
    // narrows the full player pool down to real candidates using only a
    // (PlayerId, ClubName) projection (skips StartYear/EndYear/
    // SequenceOrder/AppearanceCount entirely), never the full 5-column
    // PlayerCareerStint entity. Returns the PlayerIds whose stint-row
    // group has at least minStintCount rows AND at least one row whose
    // ClubName is in seededClubNames (exact ordinal/case-sensitive match —
    // matches XGPathGameModule.IsEligible's own
    // seededClubNames.Contains(s.ClubName) check exactly; deliberately NOT
    // the case-insensitive comparison GetUnseededClubCandidatesAsync uses,
    // since that was a diagnostic-only choice for a different method and
    // copying it here would silently change REQ-1201's real eligibility
    // semantics).
    //
    // This is a true SUPERSET of "possibly eligible" — both conditions
    // checked here are necessary-but-not-sufficient for IsEligible's own
    // three checks (a >=3-stint-row count, and "any stint at a seeded
    // club" with the appearance-count sub-condition ignored, since that
    // sub-condition can only narrow further, never widen). It never
    // excludes a player IsEligible would have accepted; it can only
    // include some players IsEligible later rejects on order-determinable
    // stint dates or the appearance-count threshold — both of which need
    // full stint rows to check and are handled by loading full data (via
    // GetCareerStintsByPlayerIdsAsync) only for the narrowed set this
    // method returns.
    Task<IReadOnlyList<Guid>> GetCareerStintCandidatePlayerIdsAsync(
        IReadOnlySet<string> seededClubNames, int minStintCount, CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): bulk counterpart to GetCareerStintsAsync
    // — every existing stint for a batch of players in one query, used by
    // WikidataLookupService's batched PersistCareerStintsAsync to dedupe
    // candidate stints against what's already stored before calling
    // AddCareerStintsBatchAsync, instead of one GetCareerStintsAsync round
    // trip per player. Same "playerId absent = no rows" shape as
    // IPlayerAliasRepository.GetPlayerAliasesByPlayerIdsAsync/
    // IPlayerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>>> GetCareerStintsByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): batched counterpart to
    // AddCareerStintsAsync — resolves every affected player's FULL
    // chronological SequenceOrder (existing rows + this call's newStints)
    // via one existing-stint SELECT plus one SaveChangesAsync for the whole
    // dictionary, instead of one round-trip pair per player. A player entry
    // with an empty newStints list is a no-op for that player, same
    // idempotency-is-the-caller's-job contract as AddCareerStintsAsync.
    Task AddCareerStintsBatchAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>> newStintsByPlayerId, CancellationToken cancellationToken = default);

    // REQ-110 (2026-07-28 "persisted confirmed-low signal" extension):
    // PlayerCacheWarmingService.WarmAsync's skip check, alongside the
    // existing cachedCount >= MinValidAnswers check — see
    // ConfirmedLowMatchPair's own doc comment for the full "why" and why
    // this shares IPlayerAttributeRepository.CountPlayersWithBothAttributesAsync's
    // exact parameter shape. A straight composite-PK lookup, no join.
    Task<bool> IsConfirmedLowAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default);

    // REQ-110: the write side of IsConfirmedLowAsync above — called only
    // after a live Wikidata lookup returns a real (possibly zero-match)
    // answer below MinValidAnswers, never after a technical failure (the
    // caller — PlayerCacheWarmingService — is responsible for that
    // distinction; this method has no way to tell a genuine zero from a
    // swallowed failure itself). Upserts: re-confirming an already-marked
    // pair (e.g. a later run finds the same pair still below threshold with
    // a different real count) updates MatchCount/ConfirmedAt in place rather
    // than throwing on a duplicate key, since the composite key already
    // uniquely identifies "this pair," not "this specific confirmation
    // event."
    Task RecordConfirmedLowAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        int matchCount, CancellationToken cancellationToken = default);

    // REQ-110 (2026-08-01 "persistent technical-failure tracking"
    // extension): PlayerCacheWarmingService.WarmAsync's second skip check,
    // alongside IsConfirmedLowAsync — true once a pair's
    // PairLookupFailure.ConsecutiveFailureCount has reached the caller's
    // threshold. See PairLookupFailure's own doc comment for the full "why
    // a separate table from ConfirmedLowMatchPair" reasoning. threshold is
    // caller-supplied (not a repository-level constant) so this stays a
    // plain read, same as IsConfirmedLowAsync — PlayerCacheWarmingService
    // owns the policy decision of how many consecutive run-failures before
    // skipping.
    Task<bool> IsPersistentTechnicalFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        int threshold, CancellationToken cancellationToken = default);

    // Upserts: increments ConsecutiveFailureCount on an existing row (and
    // refreshes LastFailedAt), inserts a new row at count 1 otherwise.
    // Called once per pair per run that ends in a technical failure — never
    // after a genuine (possibly zero-match) answer, which goes through
    // ClearTechnicalFailureAsync below instead. The caller is responsible
    // for that distinction (same split of responsibility as
    // RecordConfirmedLowAsync's own doc comment describes for its
    // technical-failure/genuine-answer split).
    Task RecordTechnicalFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default);

    // Deletes the pair's PairLookupFailure row, if any — called once a pair
    // gets a real answer (a match, or a genuine confirmed-low), so a pair
    // that recovers after a transient outage doesn't stay silently skipped
    // once Wikidata/WDQS is healthy again. A no-op, not an error, when no
    // row exists (the common case — most pairs never fail at all).
    Task ClearTechnicalFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default);

    // One-off diagnostic (`dotnet run -- audit-club-gaps`,
    // XGArcade.DataSync.ClubGapAuditService — see that class's own doc
    // comment for the full "why"): every PlayerCareerStint.ClubName that
    // doesn't match any already-seeded ClubDefinition.Name, ranked by
    // distinct PlayerId count descending. Read-only, no side effects — never
    // writes anything, never touches ReferenceDataSeeder. `top` bounds how
    // many candidates are returned; the caller decides how deep a ranked
    // list it wants, this method doesn't hardcode a count itself.
    Task<IReadOnlyList<UnseededClubCandidate>> GetUnseededClubCandidatesAsync(
        int top, CancellationToken cancellationToken = default);
}

// REQ-1207 backfill (bug-bundle fix, 2026-08-02): one player's worth of what
// PlayerPositionBirthYearBackfillService resolved from a
// QueryPlayerPositionsAndBirthYearsByQidsAsync batch. Both nullable, and both
// interpreted by UpdatePlayerPositionsAndBirthYearsAsync as "no update for
// this field" when null — NOT "clear this field to null." A caller that
// resolved only a Position for a given player passes BirthYear: null here to
// mean "leave BirthYear exactly as it already is," never to blank it out.
public record PlayerPositionBirthYearUpdate(string? Position, int? BirthYear);

// One-off diagnostic (audit-club-gaps): one candidate club — a
// PlayerCareerStint.ClubName with no matching ClubDefinition.Name — and how
// many distinct players already have a recorded stint there. Not itself a
// claim that ClubName is a "real," canonical club name (it's whatever string
// Wikidata's P54 qualifier label produced) — that's exactly why this is a
// candidate for human review, not an automatic seed.
public record UnseededClubCandidate(string ClubName, int PlayerCount);
