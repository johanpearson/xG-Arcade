namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore), split from IPlayerStoreRepository (S-107, pure
// refactor — see docs/decisions/0067-player-store-repository-split.md for
// the full "why" shared with S-106's four sibling interfaces): confirmed-low/
// technical-failure match-pair tracking (ConfirmedLowMatchPair/
// PairLookupFailure) plus the one-off unseeded-club diagnostic — grouped
// together as "data quality tooling" rather than split further, since none
// of these three tables/queries is large enough on its own to justify its
// own interface, and all three exist to answer "is this cached/reference
// data trustworthy," not to serve a game module's own read/write path. See
// IPlayerRepository's own doc comment for the shared "no facade" boundary
// note that applies identically here.
public interface IPlayerDataQualityRepository
{
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

// One-off diagnostic (audit-club-gaps): one candidate club — a
// PlayerCareerStint.ClubName with no matching ClubDefinition.Name — and how
// many distinct players already have a recorded stint there. Not itself a
// claim that ClubName is a "real," canonical club name (it's whatever string
// Wikidata's P54 qualifier label produced) — that's exactly why this is a
// candidate for human review, not an automatic seed.
public record UnseededClubCandidate(string ClubName, int PlayerCount);
