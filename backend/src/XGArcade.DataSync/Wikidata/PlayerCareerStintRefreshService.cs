using Microsoft.Extensions.Logging;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// ADR-0054: called from XGPathGameModule.GenerateInstanceAsync, once per
// generated instance, for exactly the N players it just picked as targets —
// deliberately NOT a bulk/background job like PlayerCacheWarmingService or
// PlayerPhotoBackfillService, and deliberately NOT a widening of xG Path's
// candidate POOL (which still comes entirely from GetEligiblePlayerIdsAsync's
// existing PlayerCareerStint-byproduct read, unchanged by this class). This
// only makes an already-selected target's OWN puzzle data complete — it
// cannot make a previously-ineligible player newly eligible for THIS
// generation, since eligibility is decided before this service ever runs.
// Widening the candidate pool itself (so players who never triggered an xG
// Grid lookup can become targets at all) is ADR-0055's job
// (PlayerCareerPrefetchService, which shares this class's reconciliation
// logic below — see BuildNewStintsByPlayerId's own doc comment).
//
// categoryValueRepository (bug fix, 2026-08-04, xG Path duplicate-node bug,
// REQ-1203 follow-up, see ADR-0059): added purely to build the QID -> seeded
// ClubDefinition.Name canonicalization map BuildNewStintsByPlayerId now
// needs — see that method's own doc comment for the full "why." ClubDefinition
// is a small, hand-seeded (~15 rows, MVP-SCOPE.md) reference table, so
// re-reading it once per refresh call is not a perf concern the way
// PlayerCareerStint's ~608K rows are.
// S-106/S-107 (pure refactor): playerRepository carries
// GetPlayersByIdsAsync (split out of the original, now-deleted
// IPlayerStoreRepository); playerCareerStintRepository carries
// GetCareerStintsByPlayerIdsAsync/AddCareerStintsBatchAsync — see ADR-0067.
public class PlayerCareerStintRefreshService(
    IWikidataClient wikidataClient,
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerRepository playerRepository,
    ICategoryValueRepository categoryValueRepository,
    ILogger<PlayerCareerStintRefreshService> logger) : IPlayerCareerStintRefreshService
{
    public async Task RefreshCareerStintsAsync(IReadOnlyList<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return;

        var players = await playerRepository.GetPlayersByIdsAsync(playerIds, cancellationToken);

        // REQ-109's "an unresolved QID isn't an error" reasoning, applied
        // here: a Player row created before REQ-214/WikidataQid existed, or
        // one whose QID is simply missing, is skipped rather than failing
        // the whole batch — same "one bad row costs only that row" shape as
        // PlayerPhotoBackfillService's malformed-QID filtering.
        var qidToPlayerId = players.Values
            .Where(p => p.WikidataQid is not null && WikidataQid.IsValid(p.WikidataQid))
            .ToDictionary(p => p.WikidataQid!, p => p.Id);

        if (qidToPlayerId.Count == 0)
            return;

        IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> stintsByQid;
        try
        {
            stintsByQid = await wikidataClient.QueryPlayerCareerStintsByQidsAsync(qidToPlayerId.Keys.ToList(), cancellationToken);
        }
        catch (WikidataQueryException ex)
        {
            // Never propagates — see IPlayerCareerStintRefreshService's own
            // doc comment. The affected players simply keep whatever
            // PlayerCareerStint rows they already had (xG Grid byproduct
            // data, possibly incomplete) for this round; the next round that
            // happens to pick them as a target tries again.
            logger.LogWarning(ex,
                "xg-path career-stint refresh: batch of {PlayerCount} player(s) failed; " +
                "these players keep whatever career data they already had for this round.",
                qidToPlayerId.Count);
            return;
        }

        if (stintsByQid.Count == 0)
            return;

        var affectedPlayerIds = stintsByQid.Keys.Select(qid => qidToPlayerId[qid]).ToList();
        var existingStintsByPlayerId = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(affectedPlayerIds, cancellationToken);
        var clubNameByClubQid = await BuildClubNameByClubQidAsync(categoryValueRepository, cancellationToken);

        var (newStintsByPlayerId, closuresByPlayerId) =
            BuildNewStintsByPlayerId(stintsByQid, qidToPlayerId, existingStintsByPlayerId, clubNameByClubQid, logger);

        if (newStintsByPlayerId.Count > 0 || closuresByPlayerId.Count > 0)
            await playerCareerStintRepository.AddCareerStintsBatchAsync(newStintsByPlayerId, closuresByPlayerId, cancellationToken);
    }

    // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
    // ADR-0059): QID -> seeded ClubDefinition.Name, built once per call
    // (this class's own RefreshCareerStintsAsync) or once per prefetch run
    // (PlayerCareerPrefetchService.PrefetchAsync — see that class's own
    // comment for why it's hoisted out of the per-batch loop there) rather
    // than re-derived per stint. Static (takes ICategoryValueRepository as a
    // parameter, not instance state) and internal, not private, for the
    // same shared-assembly reason as BuildNewStintsByPlayerId below — both
    // callers need this without instantiating PlayerCareerStintRefreshService
    // itself. WikidataQid is nullable on ClubDefinition (not yet resolved
    // for every seeded club) and is not guaranteed unique by a database
    // constraint, so the last club wins on a QID collision — not expected
    // in practice for a hand-seeded ~15-row table, and no worse than
    // silently picking one arbitrarily.
    internal static async Task<IReadOnlyDictionary<string, string>> BuildClubNameByClubQidAsync(
        ICategoryValueRepository categoryValueRepository, CancellationToken cancellationToken)
    {
        var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);
        return clubs
            .Where(c => c.WikidataQid is not null)
            .ToDictionary(c => c.WikidataQid!, c => c.Name);
    }

    // ADR-0055/ADR-0069: extracted so PlayerCareerPrefetchService's bulk job
    // AND WikidataLookupService.PersistCareerStintsAsync (ADR-0069's
    // refactor — it used to carry its own, inline-duplicated copy of this
    // comparison logic) share the exact same reconciliation-against-what's-
    // already-stored logic RefreshCareerStintsAsync uses above, rather than
    // a second/third, easy-to-drift-apart copy. Internal, not private:
    // XGArcade.DataSync is a shared assembly between all three callers.
    //
    // ADR-0069: the per-stint match key is (ClubName, StartYear) — narrower
    // than the full (ClubName, StartYear, EndYear, AppearanceCount) 4-tuple
    // this method used pre-ADR-0069. For each fetched entry matched against
    // existing row(s) sharing that key:
    //   1. Exact match on EndYear/AppearanceCount too -> no-op (idempotent
    //      re-fetch, unchanged from before this ADR).
    //   2. An existing row's EndYear is null, fetched entry's EndYear is
    //      non-null -> that row is queued to CLOSE (EndYear/AppearanceCount
    //      overwritten to the fetched values). Applies to EVERY existing row
    //      sharing the key with EndYear: null, not just the first found — an
    //      outstanding, not-yet-DuplicateCareerStintCleaner-cleaned
    //      cross-writer duplicate pair (ADR-0059/ADR-0063) must be closed
    //      identically on both rows, or the cleaner (which requires exact
    //      EndYear equality to merge a pair) can never merge it again. See
    //      ADR-0069's Decision and Consequences sections — do not simplify
    //      this back down to "close the first/only matching row."
    //   3. Existing row's EndYear is already non-null and disagrees with the
    //      fetched value (or any other shape not covered by 1/2/4 above,
    //      e.g. an existing null-EndYear row whose AppearanceCount alone
    //      differs from a fetched, still-null-EndYear entry) -> deliberately
    //      NOT auto-resolved. Neither updated nor inserted; logged as a
    //      warning and left untouched — could be a genuine Wikidata
    //      correction or two distinct real stints sharing a
    //      (ClubName, StartYear), and guessing wrong in either direction
    //      risks corrupting a real historical record.
    //   4. No existing row shares the key -> inserted as a new row,
    //      unchanged from before this ADR.
    // Do NOT widen the match key further (e.g. to ClubName alone) or
    // auto-resolve case 3 without a fresh ADR — ADR-0069 explicitly
    // considered and rejected both.
    //
    // clubNameByClubQid (bug fix, 2026-08-04, xG Path duplicate-node bug,
    // REQ-1203 follow-up, ADR-0059): canonicalizes each fetched stint's
    // ClubName to the matching seeded ClubDefinition.Name whenever the
    // stint's ClubQid resolves in this map — the same club, reached via
    // xG Grid's byproduct persistence (WikidataLookupService.PersistCareerStintsAsync,
    // which already writes ClubDefinition.Name directly and needs no change)
    // vs. this class's own full-career fetch (raw ?clubLabel, only ever
    // suffix-normalized), previously could and did diverge whenever
    // Wikidata's own preferred label for a QID differs from this codebase's
    // hand-picked seed name by more than a legal-suffix token (e.g. "Lyon"
    // vs. "Olympique Lyonnais", both valid labels for the same QID) —
    // producing two differently-named PlayerCareerStint rows for what is
    // really one real stint, surfaced as two xG Path club-reveal nodes. A
    // stint whose ClubQid is null (defensive-only case, see
    // WikidataClient.ParseCareerStintBindings) or doesn't match any seeded
    // club (a genuinely unseeded club) keeps its best-effort, suffix-
    // normalized label unchanged — still useful for xG Path's own display
    // and for ClubGapAuditService's gap-detection query, which this
    // canonicalization does not touch.
    //
    // logger (ADR-0069): optional so existing callers/tests that never
    // needed to observe case 3's warning don't have to change — a null
    // logger simply means case 3 is silently skipped rather than logged
    // (still correctly left untouched either way).
    internal static (
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>> NewStintsByPlayerId,
        IReadOnlyDictionary<Guid, IReadOnlyList<CareerStintClosure>> ClosuresByPlayerId) BuildNewStintsByPlayerId(
        IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>> stintsByQid,
        IReadOnlyDictionary<string, Guid> qidToPlayerId,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>> existingStintsByPlayerId,
        IReadOnlyDictionary<string, string> clubNameByClubQid,
        ILogger? logger = null)
    {
        var newStintsByPlayerId = new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>();
        var closuresByPlayerId = new Dictionary<Guid, IReadOnlyList<CareerStintClosure>>();

        foreach (var (qid, fetchedStints) in stintsByQid)
        {
            var playerId = qidToPlayerId[qid];
            var existingStints = existingStintsByPlayerId.TryGetValue(playerId, out var existing) ? existing : [];

            // ADR-0069: grouped by the narrowed (ClubName, StartYear) key —
            // more than one existing row can legitimately share a key (an
            // outstanding, not-yet-cleaned cross-writer duplicate pair; see
            // this method's own doc comment above).
            var existingByKey = existingStints
                .GroupBy(s => (s.ClubName, s.StartYear))
                .ToDictionary(g => g.Key, g => g.ToList());

            // Guards against re-processing the exact same fetched entry
            // twice within one player's own fetchedStints (defensive —
            // WikidataClient's own binding parsers already dedupe on the
            // full tuple before this method ever sees the data, but this
            // keeps the method correct even if a caller doesn't).
            var handledFetchedTuples = new HashSet<(string ClubName, int StartYear, int? EndYear, int? AppearanceCount)>();

            var newStints = new List<PlayerCareerStint>();
            var closures = new List<CareerStintClosure>();

            foreach (var s in fetchedStints)
            {
                var clubName = s.ClubQid is not null && clubNameByClubQid.TryGetValue(s.ClubQid, out var canonicalName)
                    ? canonicalName
                    : s.ClubName;

                if (!handledFetchedTuples.Add((clubName, s.StartYear, s.EndYear, s.AppearanceCount)))
                    continue;

                if (!existingByKey.TryGetValue((clubName, s.StartYear), out var matchingExisting))
                {
                    // Case 4: no existing row shares this key -> insert.
                    newStints.Add(new PlayerCareerStint
                    {
                        Id = Guid.NewGuid(),
                        PlayerId = playerId,
                        ClubName = clubName,
                        StartYear = s.StartYear,
                        EndYear = s.EndYear,
                        AppearanceCount = s.AppearanceCount,
                        // Resolved by AddCareerStintsBatchAsync across the
                        // player's full stint set — this placeholder is
                        // always overwritten before SaveChangesAsync.
                        SequenceOrder = 0,
                    });
                    continue;
                }

                // Case 1: at least one existing row under this key already
                // exactly matches the fetched entry -> no-op.
                if (matchingExisting.Any(e => e.EndYear == s.EndYear && e.AppearanceCount == s.AppearanceCount))
                    continue;

                var nullEndYearRows = matchingExisting.Where(e => e.EndYear is null).ToList();
                if (s.EndYear is not null && nullEndYearRows.Count > 0)
                {
                    // Case 2: close EVERY existing row sharing this key with
                    // EndYear: null — not just the first found. See this
                    // method's own doc comment for why.
                    closures.AddRange(nullEndYearRows.Select(row => new CareerStintClosure(row.Id, s.EndYear.Value, s.AppearanceCount)));
                    continue;
                }

                // Case 3 (and any other shape not covered by 1/2/4 above):
                // deliberately not auto-resolved. Log and leave untouched.
                logger?.LogWarning(
                    "career-stint reconciliation: player {PlayerId}'s existing PlayerCareerStint row(s) for " +
                    "({ClubName}, {StartYear}) conflict with a freshly-fetched entry (EndYear={FetchedEndYear}, " +
                    "AppearanceCount={FetchedAppearanceCount}) in a way ADR-0069 does not auto-resolve — leaving " +
                    "the existing row(s) untouched.",
                    playerId, clubName, s.StartYear, s.EndYear, s.AppearanceCount);
            }

            if (newStints.Count > 0)
                newStintsByPlayerId[playerId] = newStints;
            if (closures.Count > 0)
                closuresByPlayerId[playerId] = closures;
        }

        return (newStintsByPlayerId, closuresByPlayerId);
    }
}
