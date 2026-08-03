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

    // REQ-208: guess-time name matching's alias path, checked only when the
    // primary-name path above found no candidate satisfying the cell's
    // categories — PlayerAlias.NormalizedAlias is populated at persist time
    // (WikidataLookupService.PersistMatchesAsync) with the same
    // PlayerNameNormalizer.Normalize used here, so an exact string match is
    // enough. Never PlayerNameIndex — same boundary as the method above.
    Task<IReadOnlyList<Player>> GetPlayersByNormalizedAliasAsync(
        string normalizedAlias, CancellationToken cancellationToken = default);

    // REQ-208: the bounded candidate pool for fuzzy/edit-distance matching
    // (GridGameModule.FindFuzzyCandidatesAsync) — every player already known
    // (via a cached PlayerAttribute row) to satisfy at least one of the
    // cell's two categories. A player satisfying neither category can never
    // be a correct answer for this cell regardless of how close their name
    // is, so this never excludes a genuine match while keeping the fuzzy
    // pass's cost bounded by this cell's own category population rather
    // than a full-table scan.
    Task<IReadOnlyList<Player>> GetPlayersWithEitherAttributeAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default);

    // REQ-208: bulk alias fetch for the fuzzy pass's bounded candidate pool
    // above — one query for every candidate's aliases rather than one
    // GetPlayerAliasesAsync call per candidate. A playerId with no aliases
    // is simply absent from the result (not present with an empty list).
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAlias>>> GetPlayerAliasesByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default);

    Task AddPlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): batched counterpart to AddPlayerDataAsync
    // — one SaveChangesAsync call for the whole list (docs/coding-
    // guidelines.md), not one round trip per row. No dedup here: PlayerData
    // is a raw, per-source append log (see AddPlayerDataAsync's own
    // comment) — every row is recorded unconditionally, same contract as
    // the single-item method.
    Task AddPlayerDataBatchAsync(IReadOnlyList<PlayerData> data, CancellationToken cancellationToken = default);

    // REQ-503 (S-012): the admin review view's candidate list — every
    // PlayerData row still awaiting an admin's approve/correct/remove
    // decision.
    Task<IReadOnlyList<PlayerData>> GetUnverifiedPlayerDataAsync(CancellationToken cancellationToken = default);

    // REQ-503 (2026-07-20 extension): the "approve" action — flips one or
    // more PlayerData rows' Confidence to "verified" in a single call,
    // logging each row individually via ApprovedByAdminId/ApprovedAt (same
    // "who and when, on the row itself" shape as
    // PlayerOverride.LockedByAdminId/LockedAt). Bulk includes single-row as
    // the N=1 case. Each id is evaluated independently and never fails the
    // rest of the batch — a row that no longer exists, or whose Confidence
    // is no longer "unverified" (deleted or changed by another admin
    // between selection and submission), is reported as a failed outcome
    // for that id only, per this REQ's partial-failure reporting
    // requirement. One SaveChangesAsync call for the whole batch
    // (load-then-SaveChangesAsync, coding-guidelines.md), not one
    // round-trip per row.
    Task<IReadOnlyList<PlayerDataApprovalOutcome>> ApprovePlayerDataAsync(
        IReadOnlyCollection<Guid> playerDataIds, Guid adminId, CancellationToken cancellationToken = default);

    // REQ-503 (2026-07-20 extension): the "remove" action — hard-deletes one
    // or more PlayerData rows in a single call. Unlike
    // ApprovePlayerDataAsync, there is no "must still be unverified"
    // precondition: removing a data point is a general corrective action,
    // not exclusively tied to the review queue's current state, so a row
    // already flipped to "verified" (by another admin, between selection
    // and submission) can still be removed. Bulk includes single-row as the
    // N=1 case. Each id is evaluated independently and never fails the rest
    // of the batch — a row that no longer exists (already removed by
    // another admin between selection and submission) is reported as a
    // failed outcome for that id only. One SaveChangesAsync call for the
    // whole batch (load-then-SaveChangesAsync, coding-guidelines.md).
    //
    // No ApprovedByAdminId/ApprovedAt-style audit columns for removal: once
    // a row is deleted there's nothing left in this table to attach
    // "who/when" to. Nothing else in the schema references a PlayerData
    // row by its own Id (PlayerOverride keys on (PlayerId, Field), not a
    // PlayerData row id; PlayerAttribute has no PlayerData reference at
    // all), so a hard delete is safe here without a soft-delete flag to
    // protect some other table's foreign key. The "who and when" REQ-503
    // requires ("the action is logged with admin_id and a timestamp") is
    // satisfied by a structured ILogger line at the call site
    // (AdminEndpoints.cs) instead — matching this codebase's established
    // preference (PlayerOverride/PlayerData's own audit columns) for not
    // introducing a general-purpose audit-log table.
    Task<IReadOnlyList<PlayerDataRemovalOutcome>> RemovePlayerDataAsync(
        IReadOnlyCollection<Guid> playerDataIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerAttribute>> GetPlayerAttributesAsync(
        string attributeType, string attributeValue, CancellationToken cancellationToken = default);
    Task AddPlayerAttributeAsync(PlayerAttribute attribute, CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): batched counterpart to
    // AddPlayerAttributeAsync — one SaveChangesAsync call for the whole
    // list. Callers are responsible for dedup (same "caller already knows
    // which player/attribute pairs are new" discipline
    // WikidataLookupService's PersistMatchesAsync/QueueAttribute already
    // follows) — this never checks the composite
    // (PlayerId, AttributeType, AttributeValue) key itself.
    Task AddPlayerAttributesBatchAsync(IReadOnlyList<PlayerAttribute> attributes, CancellationToken cancellationToken = default);

    // REQ-209: disambiguation-prompt candidate building
    // (GridGameModule.BuildDisambiguationCandidatesAsync) — every cached
    // PlayerAttribute row for a batch of candidate players in one query,
    // rather than one GetPlayerAttributesAsync-shaped call per candidate.
    // Same bulk-by-player-ids shape as GetPlayerAliasesByPlayerIdsAsync; a
    // playerId with no attribute rows at all is simply absent from the
    // result (not present with an empty list).
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAttribute>>> GetPlayerAttributesByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default);

    // Grid generation's candidate-matching query (REQ-101): how many
    // players satisfy both category values at once. A single indexed join
    // rather than fetching both attribute lists and intersecting in memory.
    Task<int> CountPlayersWithBothAttributesAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PlayerAlias>> GetPlayerAliasesAsync(Guid playerId, CancellationToken cancellationToken = default);
    Task AddPlayerAliasAsync(PlayerAlias alias, CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): batched counterpart to AddPlayerAliasAsync
    // — one SaveChangesAsync call for the whole list. Same caller-dedups-
    // first discipline as AddPlayerAttributesBatchAsync above.
    Task AddPlayerAliasesBatchAsync(IReadOnlyList<PlayerAlias> aliases, CancellationToken cancellationToken = default);

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
    // stale/duplicate entry in the same batch.
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

    // REQ-1201 (S-081): xG Path's puzzle-generation eligibility check reads
    // every player's full stint set in one bulk read, grouped by PlayerId —
    // same "tolerate a full-table-scale read at Tier 0's player-pool size (a
    // few thousand rows)" precedent GetPlayersMissingPhotoAsync's own doc
    // comment already establishes, rather than a per-candidate query or a
    // SQL-side eligibility filter. A playerId with no stint rows at all is
    // simply absent from the result (not present with an empty list) — same
    // "absent means none" shape as GetPlayerAliasesByPlayerIdsAsync/
    // GetPlayerAttributesByPlayerIdsAsync above.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>>> GetAllCareerStintsByPlayerAsync(
        CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): bulk counterpart to GetCareerStintsAsync
    // — every existing stint for a batch of players in one query, used by
    // WikidataLookupService's batched PersistCareerStintsAsync to dedupe
    // candidate stints against what's already stored before calling
    // AddCareerStintsBatchAsync, instead of one GetCareerStintsAsync round
    // trip per player. Same "playerId absent = no rows" shape as
    // GetPlayerAliasesByPlayerIdsAsync/GetPlayerAttributesByPlayerIdsAsync
    // above.
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
    // this shares CountPlayersWithBothAttributesAsync's exact parameter
    // shape. A straight composite-PK lookup, no join.
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

// REQ-1207 backfill (bug-bundle fix, 2026-08-02): one player's worth of what
// PlayerPositionBirthYearBackfillService resolved from a
// QueryPlayerPositionsAndBirthYearsByQidsAsync batch. Both nullable, and both
// interpreted by UpdatePlayerPositionsAndBirthYearsAsync as "no update for
// this field" when null — NOT "clear this field to null." A caller that
// resolved only a Position for a given player passes BirthYear: null here to
// mean "leave BirthYear exactly as it already is," never to blank it out.
public record PlayerPositionBirthYearUpdate(string? Position, int? BirthYear);

// REQ-503 (2026-07-20 extension): per-row outcome of
// IPlayerStoreRepository.ApprovePlayerDataAsync — the shape that lets a
// bulk approve report which rows succeeded and which failed rather than
// treating the whole batch as one all-or-nothing unit.
public record PlayerDataApprovalOutcome(Guid PlayerDataId, bool Approved, PlayerDataApprovalFailureReason? FailureReason);

public enum PlayerDataApprovalFailureReason
{
    // The id didn't match any PlayerData row — already deleted between
    // selection and submission (or never existed).
    NotFound,
    // The row exists but its Confidence was no longer "unverified" at
    // write time — already approved, or otherwise changed, by another
    // admin between selection and submission.
    NotUnverified,
}

// REQ-503 (2026-07-20 extension): per-row outcome of
// IPlayerStoreRepository.RemovePlayerDataAsync.
public record PlayerDataRemovalOutcome(Guid PlayerDataId, bool Removed, PlayerDataRemovalFailureReason? FailureReason);

public enum PlayerDataRemovalFailureReason
{
    // The id didn't match any PlayerData row — already removed (or never
    // existed) between selection and submission.
    NotFound,
}

// One-off diagnostic (audit-club-gaps): one candidate club — a
// PlayerCareerStint.ClubName with no matching ClubDefinition.Name — and how
// many distinct players already have a recorded stint there. Not itself a
// claim that ClubName is a "real," canonical club name (it's whatever string
// Wikidata's P54 qualifier label produced) — that's exactly why this is a
// candidate for human review, not an automatic seed.
public record UnseededClubCandidate(string ClubName, int PlayerCount);
