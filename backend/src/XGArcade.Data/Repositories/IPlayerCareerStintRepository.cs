using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore), split from IPlayerStoreRepository (S-107, pure
// refactor — see docs/decisions/0067-player-store-repository-split.md for
// the full "why" shared with S-106's four sibling interfaces): the
// PlayerCareerStint concern — xG Path's (COMP-11) full stint log, plus the
// eligibility/candidate queries built on top of it. See IPlayerRepository's
// own doc comment for the shared "no facade" boundary note that applies
// identically here. GetUnseededClubCandidatesAsync also reads
// PlayerCareerStint but lives on IPlayerDataQualityRepository instead — it's
// a diagnostic-only query grouped with that interface's other data-quality
// tooling, not part of xG Path's own read/write path.
public interface IPlayerCareerStintRepository
{
    // ADR-0042/S-079: xG Path's (COMP-11) read of a player's full,
    // chronologically-ordered career-stint log — never called from any
    // correctness-checking path (xG Grid continues to read only
    // PlayerAttribute/PlayerOverride via IPlayerOverrideRepository.HasEffectiveAttributeAsync).
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
    // thousand rows' full-table-read assumption is now stale"), narrowing
    // rule updated for REQ-1201/ADR-0074/S-138: the cheap first pass of
    // xG Path's puzzle-generation eligibility check — narrows the full
    // player pool down to real candidates using only a (PlayerId, ClubName)
    // projection (skips StartYear/EndYear/SequenceOrder/AppearanceCount
    // entirely), never the full 5-column PlayerCareerStint entity. Returns
    // the PlayerIds whose stints include at least minSeededClubCount
    // DISTINCT ClubName values that are in seededClubNames (exact ordinal/
    // case-sensitive match — matches XGPathGameModule.IsEligible's own
    // seededClubNames.Contains(s.ClubName) check exactly; deliberately NOT
    // the case-insensitive comparison IPlayerDataQualityRepository.GetUnseededClubCandidatesAsync
    // uses, since that was a diagnostic-only choice for a different method
    // and copying it here would silently change REQ-1201's real eligibility
    // semantics). Two stint rows at the SAME seeded club (e.g. a loan, then
    // a later permanent return) count once toward minSeededClubCount, not
    // twice — this method groups by distinct ClubName, not by row.
    //
    // This is a true SUPERSET of "possibly eligible" — the condition
    // checked here (>= minSeededClubCount distinct seeded club names,
    // ignoring the per-club appearance-count sub-condition, since that
    // sub-condition can only narrow further, never widen) is
    // necessary-but-not-sufficient for IsEligible's own two checks. It
    // never excludes a player IsEligible would have accepted; it can only
    // include some players IsEligible later rejects on order-determinable
    // stint dates or a seeded club's own appearance-count threshold —
    // both of which need full stint rows to check and are handled by
    // loading full data (via GetCareerStintsByPlayerIdsAsync) only for the
    // narrowed set this method returns.
    Task<IReadOnlyList<Guid>> GetCareerStintCandidatePlayerIdsAsync(
        IReadOnlySet<string> seededClubNames, int minSeededClubCount, CancellationToken cancellationToken = default);

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
}
