using Microsoft.Extensions.Logging;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync;

// `dotnet run -- audit-club-gaps`'s own service (Program.cs) — a one-off,
// read-only diagnostic to help scope a future seed-list widening decision.
// Not tied to a REQ/ADR (internal ops tooling, no user-facing behavior
// change — same category as verify-wikidata-player-data's own CLI-verb-only
// logic, which also has no dedicated REQ).
//
// Surfaces PlayerCareerStint.ClubName values with no matching
// ClubDefinition.Name (IPlayerStoreRepository.GetUnseededClubCandidatesAsync
// — see that method's own doc comment for the case-insensitive-comparison
// assumption), ranked by distinct player count, as candidates for a human to
// manually review and verify against Wikidata before adding anything to
// ReferenceDataSeeder. This class deliberately never queries Wikidata itself
// and never touches ReferenceDataSeeder — it only reads whatever
// PlayerCareerStint data ADR-0054 (guess-time byproduct)/ADR-0055
// (prefetch-player-careers) has already populated. No new table, no
// persistence, no side effects whatsoever: output goes only to the log, for
// a human to read.
//
// Deliberately a `dotnet run -- audit-club-gaps` CLI verb, not an HTTP
// endpoint or background task — same ADR-0024 "long-running/bulk job is a
// CLI verb" reasoning as this project's other bulk Wikidata jobs (e.g.
// PlayerCareerPrefetchService), even though this one is read-only and fast:
// keeping every bulk/diagnostic job on the same dispatch mechanism means
// there's exactly one pattern to look for in Program.cs, not two.
public class ClubGapAuditService(
    IPlayerStoreRepository playerStoreRepository,
    ILogger<ClubGapAuditService> logger)
{
    // Matches this verb's own spec ("logs the top 30 results") — a plain
    // constant rather than a caller-supplied parameter, since this is a
    // one-off diagnostic with no other caller that would ever want a
    // different depth.
    public const int TopCandidateCount = 30;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await playerStoreRepository.GetUnseededClubCandidatesAsync(TopCandidateCount, cancellationToken);

        if (candidates.Count == 0)
        {
            logger.LogInformation(
                "audit-club-gaps: no unseeded club candidates found — every PlayerCareerStint.ClubName already matches a seeded ClubDefinition.");
            return;
        }

        logger.LogInformation(
            "audit-club-gaps: top {Count} unseeded club candidate(s) by distinct player count (highest first):", candidates.Count);

        foreach (var candidate in candidates)
            logger.LogInformation("{ClubName}: {PlayerCount} players", candidate.ClubName, candidate.PlayerCount);
    }
}
