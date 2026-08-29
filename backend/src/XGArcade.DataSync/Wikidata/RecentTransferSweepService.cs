using Microsoft.Extensions.Logging;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// S-188 (docs/backlog.md, Epic 26 — Supabase free-tier egress remediation):
// `dotnet run -- sweep-recent-transfers`'s own service — see
// IRecentTransferSweepService's own doc comment for the full "what this is
// for / how it differs from PlayerCareerPrefetchService" summary, including
// the deliberate PlayerPoolSweptAt scope boundary.
//
// A CLI verb, not an HTTP endpoint or background task, same ADR-0024
// reasoning as every other bulk Wikidata job in this codebase.
//
// Iterates every seeded ClubDefinition (CategoryValueRepository.GetClubsAsync)
// — never a broader/unfiltered set, same MVP-SCOPE.md-driven scope every
// other bulk Wikidata job in this codebase already uses. A club with no
// resolved WikidataQid simply contributes nothing (REQ-109's "an unresolved
// QID isn't an error" convention), same as PlayerCareerPrefetchService's own
// per-row null-QID skip.
//
// S-189 (ADR-0093, "targeted Grid answer-key freshness" follow-up to
// ADR-0092/S-188): PersistClubTransfersAsync now ALSO writes a
// PlayerAttribute+PlayerData row for every genuinely NEW arrival, and
// invalidates any now-stale ConfirmedLowMatchPair/PairLookupFailure row for
// the pairs that arrival just affected — see
// PersistClubAttributesForArrivalsAsync's own doc comment for the full
// "why this is safe" reasoning ADR-0092 originally deferred. A departure
// still never writes or removes a PlayerAttribute row — see this class's
// own IRecentTransferSweepService doc comment for why.
public class RecentTransferSweepService(
    ICategoryValueRepository categoryValueRepository,
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerRepository playerRepository,
    IPlayerAttributeRepository playerAttributeRepository,
    IPlayerDataRepository playerDataRepository,
    IPlayerDataQualityRepository playerDataQualityRepository,
    IWikidataClient wikidataClient,
    ILogger<RecentTransferSweepService> logger) : IRecentTransferSweepService
{
    // S-188: no clubNameByClubQid canonicalization map is needed here, unlike
    // PlayerCareerPrefetchService/PlayerCareerStintRefreshService — every
    // WikidataCareerStintEntry this service ever reconciles already carries
    // the caller-known ClubDefinition.Name directly (threaded through
    // IWikidataClient.QueryRecentClubTransfersAsync's own clubName parameter,
    // see SparqlQueryBuilders.BuildRecentClubArrivalsQuery's doc comment for
    // why), so BuildNewStintsByPlayerId's own QID-based canonicalization step
    // has nothing to do and is passed this permanently-empty map.
    private static readonly Dictionary<string, string> NoClubNameCanonicalization = new();

    // S-189: reuses WikidataLookupService's own constants (made internal for
    // exactly this — see PlayerCareerPrefetchService's identical reuse) so
    // there is exactly one definition of the "club" AttributeType spelling
    // and the "wikidata"/"verified" PlayerData Source/Confidence values, not
    // a second copy kept in sync only by comment discipline.
    private const string ClubAttributeType = WikidataLookupService.ClubAttributeType;
    private const string WikidataDataSource = WikidataLookupService.WikidataSource;
    private const string VerifiedConfidence = WikidataLookupService.VerifiedConfidence;

    public async Task<RecentTransferSweepResult> SweepAsync(int lookbackDays, CancellationToken cancellationToken = default)
    {
        if (lookbackDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(lookbackDays), lookbackDays, "lookbackDays must be positive.");

        // Computed once per call, reused for every seeded club — same
        // "one cutoff for the whole run" shape as
        // PlayerCareerPrefetchService's own clubNameByClubQid, which is also
        // built once per run rather than re-derived per row.
        var sinceUtc = DateTime.UtcNow.AddDays(-lookbackDays);
        var clubs = await categoryValueRepository.GetClubsAsync(cancellationToken);

        var clubsProcessed = 0;
        var clubsFailed = 0;
        var failedClubNames = new List<string>();
        var playersTouched = 0;
        var stintsAdded = 0;
        var stintsCompleted = 0;
        var attributesAdded = 0;

        foreach (var club in clubs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (club.WikidataQid is null)
                continue;

            RecentClubTransferLookupResult transfers;
            try
            {
                transfers = await wikidataClient.QueryRecentClubTransfersAsync(
                    club.WikidataQid, club.Name, sinceUtc, cancellationToken);
            }
            catch (WikidataQueryException ex)
            {
                clubsFailed++;
                failedClubNames.Add(club.Name);
                logger.LogWarning(ex,
                    "sweep-recent-transfers: {Club} failed; continuing with the remaining clubs. " +
                    "This run WILL fail at the end, but the job is idempotent — re-run it to retry the failed club(s).",
                    club.Name);
                continue;
            }

            clubsProcessed++;
            var (touched, added, completed, attrsAdded) = await PersistClubTransfersAsync(transfers, cancellationToken);
            playersTouched += touched;
            stintsAdded += added;
            stintsCompleted += completed;
            attributesAdded += attrsAdded;

            if (touched > 0)
                logger.LogInformation(
                    "sweep-recent-transfers: {Club} done — {PlayersTouched} player(s) touched, " +
                    "{StintsAdded} stint(s) added, {StintsCompleted} stint(s) completed, " +
                    "{AttributesAdded} attribute(s) added.",
                    club.Name, touched, added, completed, attrsAdded);
        }

        if (clubsFailed > 0)
        {
            throw new InvalidOperationException(
                $"sweep-recent-transfers: {clubsFailed} club(s) failed to fetch recent transfers " +
                $"({string.Join(", ", failedClubNames)}). {playersTouched} player(s) were still touched, " +
                $"{stintsAdded} stint(s) added, {stintsCompleted} stint(s) completed, and {attributesAdded} " +
                "attribute(s) added from what succeeded; the job is idempotent — re-run it to retry what failed.");
        }

        return new RecentTransferSweepResult(clubsProcessed, clubsFailed, playersTouched, stintsAdded, stintsCompleted, attributesAdded);
    }

    // S-188: reuses PlayerCareerStintRefreshService.BuildNewStintsByPlayerId
    // (ADR-0054/ADR-0091) verbatim — the exact same reconciliation
    // PlayerCareerPrefetchService/PlayerCareerStintRefreshService/
    // WikidataLookupService already share, never reimplemented here. An
    // arrival with no existing (ClubName, StartYear) match inserts as a new
    // PlayerCareerStint row (AddCareerStintsBatchAsync); a departure whose
    // (ClubName, StartYear) already matches an existing row COMPLETES it in
    // place via CareerStintReconciler.Reconcile
    // (UpdateCareerStintCompletionsAsync) instead of inserting a duplicate.
    //
    // S-189: reconciliation.NewStintsByPlayerId is also exactly the "which
    // players just got a genuinely new arrival" signal
    // PersistNewArrivalAttributesAsync needs — a departure only ever shows
    // up in CompletionsByStintId, never here, so this naturally excludes
    // departures from the attribute write without any extra branching.
    private async Task<(int PlayersTouched, int StintsAdded, int StintsCompleted, int AttributesAdded)> PersistClubTransfersAsync(
        RecentClubTransferLookupResult transfers, CancellationToken cancellationToken)
    {
        if (transfers.StintsByQid.Count == 0)
            return (0, 0, 0, 0);

        // REQ-214/REQ-1207's existing "set only at creation, never
        // overwritten on a later lookup" contract applies here unchanged —
        // PhotoUrl/Position/BirthYear are left null for a brand-new arrival;
        // the existing backfill services (PlayerPhotoBackfillService/
        // PlayerPositionBirthYearBackfillService) pick those up later, same
        // as every other Wikidata-derived player-creation path in this
        // codebase that doesn't already have that data on hand.
        var requests = transfers.StintsByQid.Keys
            .Select(qid => new PlayerCreationRequest(
                qid,
                transfers.PlayerNamesByQid.TryGetValue(qid, out var name) ? name : qid,
                PhotoUrl: null))
            .ToList();
        var playersByQid = await playerRepository.GetOrCreatePlayersByWikidataQidAsync(requests, cancellationToken);

        var qidToPlayerId = playersByQid.ToDictionary(kv => kv.Key, kv => kv.Value.Player.Id);
        var existingStintsByPlayerId = await playerCareerStintRepository.GetCareerStintsByPlayerIdsAsync(
            qidToPlayerId.Values.ToList(), cancellationToken);

        var reconciliation = PlayerCareerStintRefreshService.BuildNewStintsByPlayerId(
            transfers.StintsByQid, qidToPlayerId, existingStintsByPlayerId, NoClubNameCanonicalization);

        if (reconciliation.NewStintsByPlayerId.Count > 0)
            await playerCareerStintRepository.AddCareerStintsBatchAsync(reconciliation.NewStintsByPlayerId, cancellationToken);

        if (reconciliation.CompletionsByStintId.Count > 0)
            await playerCareerStintRepository.UpdateCareerStintCompletionsAsync(reconciliation.CompletionsByStintId, cancellationToken);

        var attributesAdded = await PersistNewArrivalAttributesAsync(reconciliation.NewStintsByPlayerId, cancellationToken);

        return (
            playersByQid.Count,
            reconciliation.NewStintsByPlayerId.Sum(kv => kv.Value.Count),
            reconciliation.CompletionsByStintId.Count,
            attributesAdded);
    }

    // S-189 (ADR-0093): newStintsByPlayerId is BuildNewStintsByPlayerId's own
    // arrival-only output (PlayerId -> the brand-new PlayerCareerStint rows
    // just inserted for them) — grouped here by ClubName so the dedup read
    // below (GetPlayerAttributesAsync) runs once per distinct club value. In
    // practice this whole call is always scoped to ONE club (every
    // WikidataCareerStintEntry this service reconciles carries the
    // caller-known ClubDefinition.Name — see this class's own top comment),
    // so this loop runs exactly once per PersistClubTransfersAsync call, but
    // grouping keeps this method correct even if that per-call invariant
    // ever changes. Distinct(): a player who legitimately gets two NEW stint
    // rows at the SAME club in one call (e.g. two separate re-signings
    // within the lookback window, each with a different StartYear) must
    // still only ever get ONE PlayerAttribute row for that club, since
    // PlayerAttribute carries no StartYear of its own.
    private async Task<int> PersistNewArrivalAttributesAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>> newStintsByPlayerId,
        CancellationToken cancellationToken)
    {
        if (newStintsByPlayerId.Count == 0)
            return 0;

        var arrivalPairs = newStintsByPlayerId
            .SelectMany(kv => kv.Value.Select(stint => (PlayerId: kv.Key, stint.ClubName)))
            .Distinct()
            .ToList();

        var attributesAdded = 0;
        foreach (var group in arrivalPairs.GroupBy(p => p.ClubName))
        {
            attributesAdded += await PersistClubAttributesForArrivalsAsync(
                group.Key, group.Select(p => p.PlayerId).Distinct().ToList(), cancellationToken);
        }

        return attributesAdded;
    }

    // S-189 (ADR-0093): mirrors PlayerCareerPrefetchService
    // .FetchAndPersistBatchAsync's own "query existing dedup set once, write
    // PlayerAttribute+PlayerData together" shape — see that method's own
    // comment for the precedent this follows.
    //
    // The invalidation this method also performs is the piece ADR-0092
    // originally deferred: writing a fresh PlayerAttribute(club, clubName)
    // row for a player can make a previously-recorded
    // ConfirmedLowMatchPair/PairLookupFailure row for (club, clubName) x
    // (one of this player's OTHER existing attribute values) stale — the
    // locally-cached match count that marker was based on has now grown by
    // one, and if both sides of that pair were already
    // PlayerPoolSweptAt-swept and being trusted as "final" (ADR-0078), the
    // stale marker would otherwise silently suppress the real re-check that
    // growth deserves.
    //
    // ADR-0093's own trace of xG Grid's live read paths found two DIFFERENT
    // pictures for the two tables this clears, not one uniform one:
    //  - ConfirmedLowMatchPair.IsConfirmedLowAsync is consulted ONLY inside
    //    PlayerCacheWarmingService.WarmAsync's maintenance heuristic, and
    //    only as a secondary check after cachedCount >= MinValidAnswers is
    //    already checked first — GridGenerationService/
    //    PlayerOverrideRepository's own live correctness-checking paths
    //    never consult it at all. A stale row here is purely a missed
    //    opportunity for warm-grid-cache to discover more matches sooner,
    //    never a live wrong-answer risk.
    //  - PairLookupFailure.IsPersistentTechnicalFailureAsync is ALSO
    //    consulted at GUESS TIME, by GridLiveLookupDispatcher
    //    .TryRefreshCellAsync (REQ-211's live-lookup fallback) — a real live
    //    path, not just a maintenance one. Clearing a stale row there is
    //    still never a CORRECTNESS risk (ADR-0046: a live-lookup
    //    timeout/failure always fails closed as "unknown," consuming no
    //    guess attempt, never becomes a wrong "incorrect" verdict), but it
    //    does mean a guess against that pair can end up paying a live
    //    Wikidata round trip (and its ~28s timeout, if the underlying
    //    failure was genuinely structural rather than caused by staleness)
    //    that an un-cleared marker would have short-circuited — a latency
    //    trade-off, not a correctness one, and self-healing: the next
    //    PlayerCacheWarmingService run that still fails re-records the
    //    marker. Clearing both is still the right call (cheap, bounded, and
    //    ADR-0078's own "For AI agents" section treats invalidation as not
    //    optional) — but this latency nuance is real and belongs in
    //    ADR-0093's own text, not silently smoothed over.
    // Bounded by however many OTHER attributes this one player already has
    // (a handful at most) — never a club-wide sweep the way
    // StaleClubAttributeCleaner's own broader "delete every row involving
    // this club" shape is.
    private async Task<int> PersistClubAttributesForArrivalsAsync(
        string clubName, IReadOnlyList<Guid> arrivalPlayerIds, CancellationToken cancellationToken)
    {
        // Dedup gate: every player already known to have (club, clubName) —
        // same "fetch once, HashSet as the dedup gate" pattern
        // PlayerCareerPrefetchService.FetchAndPersistBatchAsync's own
        // playerIdsWithAttribute uses, built fresh per call here rather than
        // shared across a whole pool, since arrivalPlayerIds is already
        // small and bounded by real transfer activity, not squad size.
        var playerIdsWithAttribute = (await playerAttributeRepository.GetPlayerAttributesAsync(
                ClubAttributeType, clubName, cancellationToken))
            .Select(a => a.PlayerId)
            .ToHashSet();

        var newlyAttributedPlayerIds = arrivalPlayerIds.Where(id => !playerIdsWithAttribute.Contains(id)).ToList();
        if (newlyAttributedPlayerIds.Count == 0)
            return 0;

        // S-189: every OTHER attribute value each newly-attributed player
        // already has, queried BEFORE the new (club, clubName) attribute is
        // written below — this naturally excludes the attribute this call
        // is about to add (these players don't have it yet, by
        // construction of the filter above), no extra filtering needed.
        // This is what tells the invalidation step below which specific
        // pairs are now stale.
        var otherAttributesByPlayerId = await playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync(
            newlyAttributedPlayerIds, cancellationToken);

        var attributesToAdd = new List<PlayerAttribute>();
        var playerDataToAdd = new List<PlayerData>();
        var syncedAt = DateTime.UtcNow;
        foreach (var playerId in newlyAttributedPlayerIds)
        {
            attributesToAdd.Add(new PlayerAttribute { PlayerId = playerId, AttributeType = ClubAttributeType, AttributeValue = clubName });
            playerDataToAdd.Add(new PlayerData
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                Field = ClubAttributeType,
                Value = clubName,
                Source = WikidataDataSource,
                Confidence = VerifiedConfidence,
                SyncedAt = syncedAt,
            });
        }

        await playerAttributeRepository.AddPlayerAttributesBatchAsync(attributesToAdd, cancellationToken);
        await playerDataRepository.AddPlayerDataBatchAsync(playerDataToAdd, cancellationToken);

        // Targeted invalidation (S-189/ADR-0093) — see this method's own
        // top comment for the full "why this is safe" reasoning.
        foreach (var playerId in newlyAttributedPlayerIds)
        {
            if (!otherAttributesByPlayerId.TryGetValue(playerId, out var otherAttributes))
                continue;

            foreach (var otherAttribute in otherAttributes)
            {
                await playerDataQualityRepository.ClearMatchPairAsync(
                    ClubAttributeType, clubName, otherAttribute.AttributeType, otherAttribute.AttributeValue, cancellationToken);
            }
        }

        return attributesToAdd.Count;
    }
}
