using Microsoft.Extensions.Logging;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// REQ-1501/REQ-1506 (xG Higher/Lower, S-231, ADR-0112): see
// IPlayerInternationalStatsRefreshService's own doc comment for the full
// "what this is for / how it's driven" reasoning — this class only fetches
// and writes a GIVEN batch of already-known player IDs, it never discovers
// which players to process.
//
// AttributeType literals ("international-caps"/"international-goals",
// ADR-0112 Decision point 1) — these two exact strings must stay in sync
// with HigherLowerGenerationService's CandidateStatCategories,
// IPlayerBackfillRepository.GetPlayersMissingInternationalStatsAsync's own
// "international-caps" missing-signal, and REQ-1506's floor read. Kept as
// plain string literals rather than a shared constant, matching this
// codebase's own established convention for PlayerAttribute.AttributeType
// values (see PlayerAttribute.cs's own "club" | "nationality" | "trophy" |
// "international-caps" | "international-goals" doc comment — none of those
// are shared constants across projects either).
public class PlayerInternationalStatsRefreshService(
    IWikidataClient wikidataClient,
    IPlayerRepository playerRepository,
    IPlayerAttributeRepository playerAttributeRepository,
    IPlayerDataRepository playerDataRepository,
    ILogger<PlayerInternationalStatsRefreshService> logger) : IPlayerInternationalStatsRefreshService
{
    internal const string CapsAttributeType = "international-caps";
    internal const string GoalsAttributeType = "international-goals";

    // Reuses WikidataLookupService's own WikidataSource/VerifiedConfidence
    // (made internal for exactly this) instead of redeclaring a second
    // private copy — same reuse PlayerCareerPrefetchService's own constants
    // already establish. Every row this service writes to PlayerData is
    // Wikidata-sourced and "verified" by default (ADR-0032).
    private const string WikidataDataSource = WikidataLookupService.WikidataSource;
    private const string VerifiedConfidence = WikidataLookupService.VerifiedConfidence;

    public async Task RefreshInternationalStatsAsync(
        IReadOnlyList<Guid> playerIds, bool throwOnFailure = false, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return;

        var players = await playerRepository.GetPlayersByIdsAsync(playerIds, cancellationToken);

        // REQ-109's "an unresolved QID isn't an error" reasoning, applied
        // here the same way PlayerCareerStintRefreshService's own
        // RefreshCareerStintsAsync applies it — a Player row with no
        // resolvable WikidataQid is skipped rather than failing the whole
        // batch.
        var qidToPlayerId = players.Values
            .Where(p => p.WikidataQid is not null && WikidataQid.IsValid(p.WikidataQid))
            .ToDictionary(p => p.WikidataQid!, p => p.Id);

        if (qidToPlayerId.Count == 0)
            return;

        IReadOnlyDictionary<string, WikidataInternationalStatsEntry> statsByQid;
        try
        {
            statsByQid = await wikidataClient.QueryInternationalStatsByQidsAsync(qidToPlayerId.Keys.ToList(), cancellationToken);
        }
        catch (WikidataQueryException ex)
        {
            // throwOnFailure: see IPlayerInternationalStatsRefreshService's
            // own doc comment. The default (false) preserves the
            // "never propagates" contract: affected players simply keep
            // whatever international-caps/international-goals data they
            // already had (none, for a first run).
            if (throwOnFailure)
                throw;

            logger.LogWarning(ex,
                "xg-higher-lower international-stats refresh: batch of {PlayerCount} player(s) failed; " +
                "these players keep whatever international-stats data they already had.",
                qidToPlayerId.Count);
            return;
        }

        if (statsByQid.Count == 0)
            return;

        var affectedPlayerIds = statsByQid.Keys.Select(qid => qidToPlayerId[qid]).ToList();

        // Never overwrites an already-processed player — see
        // IPlayerInternationalStatsRefreshService's own doc comment for why
        // this defensive re-check exists even though the primary caller
        // (PlayerInternationalStatsBackfillService) already filters its
        // own candidates via GetPlayersMissingInternationalStatsAsync.
        var existingAttributesByPlayerId = await playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync(
            affectedPlayerIds, cancellationToken);

        var attributesToAdd = new List<PlayerAttribute>();
        var playerDataToAdd = new List<PlayerData>();
        var syncedAt = DateTime.UtcNow;

        foreach (var (qid, entry) in statsByQid)
        {
            var playerId = qidToPlayerId[qid];
            var alreadyHasCaps = existingAttributesByPlayerId.TryGetValue(playerId, out var existingRows)
                && existingRows.Any(row => row.AttributeType == CapsAttributeType);
            if (alreadyHasCaps)
                continue;

            var capsValue = entry.Caps.ToString();
            attributesToAdd.Add(new PlayerAttribute { PlayerId = playerId, AttributeType = CapsAttributeType, AttributeValue = capsValue });
            playerDataToAdd.Add(new PlayerData
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                Field = CapsAttributeType,
                Value = capsValue,
                Source = WikidataDataSource,
                Confidence = VerifiedConfidence,
                SyncedAt = syncedAt,
            });

            // Goals is independently nullable (WikidataInternationalStatsEntry's
            // own doc comment) — only written when the winning statement
            // actually resolved a pq:P1351 value; REQ-1501's "non-null
            // recorded value" eligibility rule means a player with no
            // resolved goals figure must be absent from
            // GetEffectivePlayerValuesByAttributeTypeAsync("international-goals")'s
            // result, not present with a fabricated 0.
            if (entry.Goals is not null)
            {
                var goalsValue = entry.Goals.Value.ToString();
                attributesToAdd.Add(new PlayerAttribute { PlayerId = playerId, AttributeType = GoalsAttributeType, AttributeValue = goalsValue });
                playerDataToAdd.Add(new PlayerData
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    Field = GoalsAttributeType,
                    Value = goalsValue,
                    Source = WikidataDataSource,
                    Confidence = VerifiedConfidence,
                    SyncedAt = syncedAt,
                });
            }
        }

        if (attributesToAdd.Count > 0)
        {
            await playerAttributeRepository.AddPlayerAttributesBatchAsync(attributesToAdd, cancellationToken);
            await playerDataRepository.AddPlayerDataBatchAsync(playerDataToAdd, cancellationToken);
        }
    }
}
