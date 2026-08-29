using Microsoft.Extensions.Logging;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// S-188 (docs/backlog.md, Epic 26 — Supabase free-tier egress remediation):
// `dotnet run -- sweep-recent-transfers`'s own service — see
// IRecentTransferSweepService's own doc comment for the full "what this is
// for / how it differs from PlayerCareerPrefetchService" summary, including
// the deliberate PlayerAttribute/PlayerPoolSweptAt scope boundary.
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
public class RecentTransferSweepService(
    ICategoryValueRepository categoryValueRepository,
    IPlayerCareerStintRepository playerCareerStintRepository,
    IPlayerRepository playerRepository,
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
            var (touched, added, completed) = await PersistClubTransfersAsync(transfers, cancellationToken);
            playersTouched += touched;
            stintsAdded += added;
            stintsCompleted += completed;

            if (touched > 0)
                logger.LogInformation(
                    "sweep-recent-transfers: {Club} done — {PlayersTouched} player(s) touched, " +
                    "{StintsAdded} stint(s) added, {StintsCompleted} stint(s) completed.",
                    club.Name, touched, added, completed);
        }

        if (clubsFailed > 0)
        {
            throw new InvalidOperationException(
                $"sweep-recent-transfers: {clubsFailed} club(s) failed to fetch recent transfers " +
                $"({string.Join(", ", failedClubNames)}). {playersTouched} player(s) were still touched, " +
                $"{stintsAdded} stint(s) added, and {stintsCompleted} stint(s) completed from what succeeded; " +
                "the job is idempotent — re-run it to retry what failed.");
        }

        return new RecentTransferSweepResult(clubsProcessed, clubsFailed, playersTouched, stintsAdded, stintsCompleted);
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
    private async Task<(int PlayersTouched, int StintsAdded, int StintsCompleted)> PersistClubTransfersAsync(
        RecentClubTransferLookupResult transfers, CancellationToken cancellationToken)
    {
        if (transfers.StintsByQid.Count == 0)
            return (0, 0, 0);

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

        return (
            playersByQid.Count,
            reconciliation.NewStintsByPlayerId.Sum(kv => kv.Value.Count),
            reconciliation.CompletionsByStintId.Count);
    }
}
