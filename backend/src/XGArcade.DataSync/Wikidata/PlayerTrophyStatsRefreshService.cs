using Microsoft.Extensions.Logging;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.DataSync.Wikidata;

// REQ-1501 (xG Higher/Lower, S-232, ADR-0113): see
// IPlayerTrophyStatsRefreshService's own doc comment for the full "what this
// is for / how it's driven" reasoning — this class only fetches and writes a
// GIVEN batch of already-known player IDs, it never discovers which players
// to process.
//
// AttributeType literal ("trophy") — the SAME literal
// WikidataLookupService's existing byproduct path already writes (ADR-0111,
// unchanged). Kept as a plain string literal, matching this codebase's own
// established convention for PlayerAttribute.AttributeType values (see
// PlayerInternationalStatsRefreshService's own comment on why those aren't
// shared constants across projects either) — UNLIKE PlayerData.TrophyStatsCheckedField
// below, which IS a shared constant for the reasons that field's own doc
// comment gives.
public class PlayerTrophyStatsRefreshService(
    IWikidataClient wikidataClient,
    IPlayerRepository playerRepository,
    IPlayerAttributeRepository playerAttributeRepository,
    IPlayerDataRepository playerDataRepository,
    ICategoryValueRepository categoryValueRepository,
    ILogger<PlayerTrophyStatsRefreshService> logger) : IPlayerTrophyStatsRefreshService
{
    internal const string TrophyAttributeType = "trophy";

    // Reuses WikidataLookupService's own WikidataSource/VerifiedConfidence
    // (made internal for exactly this) instead of redeclaring a second
    // private copy — same reuse PlayerInternationalStatsRefreshService's own
    // constants already establish. Every row this service writes to
    // PlayerData is Wikidata-sourced and "verified" by default (ADR-0032).
    private const string WikidataDataSource = WikidataLookupService.WikidataSource;
    private const string VerifiedConfidence = WikidataLookupService.VerifiedConfidence;

    public async Task RefreshTrophyStatsAsync(
        IReadOnlyList<Guid> playerIds, bool throwOnFailure = false, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return;

        var players = await playerRepository.GetPlayersByIdsAsync(playerIds, cancellationToken);

        // REQ-109's "an unresolved QID isn't an error" reasoning, applied
        // here the same way PlayerInternationalStatsRefreshService's own
        // RefreshInternationalStatsAsync applies it — a Player row with no
        // resolvable WikidataQid is skipped rather than failing the whole
        // batch.
        var qidToPlayerId = players.Values
            .Where(p => p.WikidataQid is not null && WikidataQid.IsValid(p.WikidataQid))
            .ToDictionary(p => p.WikidataQid!, p => p.Id);

        if (qidToPlayerId.Count == 0)
            return;

        // ADR-0113 Decision: split the seeded TrophyDefinition pool by
        // IsTeamTrophy — one VALUES batch per query shape
        // (BuildIndividualTrophyStatsByQidsQuery/BuildTeamTrophyStatsByQidsQuery).
        // A TrophyDefinition with no WikidataQid (never seeded with one, or
        // an admin-entered placeholder row) cannot be queried at all and is
        // excluded from both batches — same "nothing to look up" reasoning
        // WikidataLookupService.LookupAndPersistTrophyCountryAsync's own
        // `if (trophy.WikidataQid is null ...) return [];` guard applies to
        // the existing byproduct path.
        var trophies = await categoryValueRepository.GetTrophiesAsync(cancellationToken);
        var individualTrophyNameByQid = trophies
            .Where(t => !t.IsTeamTrophy && t.WikidataQid is not null)
            .ToDictionary(t => t.WikidataQid!, t => t.Name);
        var teamTrophyNameByQid = trophies
            .Where(t => t.IsTeamTrophy && t.WikidataQid is not null)
            .ToDictionary(t => t.WikidataQid!, t => t.Name);

        var playerQids = qidToPlayerId.Keys.ToList();

        IReadOnlyDictionary<string, IReadOnlyList<string>> individualResultsByPlayerQid;
        IReadOnlyDictionary<string, IReadOnlyList<string>> teamResultsByPlayerQid;
        try
        {
            // Sequential, not Task.WhenAll — two independent Wikidata HTTP
            // calls, not a shared-DbContext concurrency concern, but kept
            // sequential for the same reason every other multi-step refresh
            // in this codebase is: simpler to reason about, and neither call
            // depends on request-latency budget the way a guess-time lookup
            // would. Either call failing fails this WHOLE batch attempt
            // (caught together below) — a player batch is only ever
            // reported "checked" once BOTH the individual-award and
            // team-competition sweeps have actually run for it, never a
            // partial sweep silently treated as complete.
            individualResultsByPlayerQid = individualTrophyNameByQid.Count > 0
                ? await wikidataClient.QueryIndividualTrophyStatsByQidsAsync(
                    playerQids, individualTrophyNameByQid.Keys.ToList(), cancellationToken)
                : new Dictionary<string, IReadOnlyList<string>>();
            teamResultsByPlayerQid = teamTrophyNameByQid.Count > 0
                ? await wikidataClient.QueryTeamTrophyStatsByQidsAsync(
                    playerQids, teamTrophyNameByQid.Keys.ToList(), cancellationToken)
                : new Dictionary<string, IReadOnlyList<string>>();
        }
        catch (WikidataQueryException ex)
        {
            // throwOnFailure: see IPlayerTrophyStatsRefreshService's own doc
            // comment. The default (false) preserves the "never propagates"
            // contract: affected players simply keep whatever "trophy" data
            // they already had (none, or only the byproduct path's, for a
            // first run).
            if (throwOnFailure)
                throw;

            logger.LogWarning(ex,
                "xg-higher-lower trophy-stats refresh: batch of {PlayerCount} player(s) failed; " +
                "these players keep whatever trophy data they already had.",
                qidToPlayerId.Count);
            return;
        }

        // The whole batch's Wikidata call(s) succeeded (no
        // WikidataQueryException) — every player whose QID was actually
        // sent gets the "checked" marker written below, even when neither
        // sweep resolved any trophy for them. Same bug-avoidance reasoning
        // as PlayerInternationalStatsRefreshService's own "checked" marker
        // (ADR-0112's Amendment, retroactively fixed there — built in here
        // from the first commit per ADR-0113's own "For AI agents" section):
        // without this, "never checked" and "checked, won nothing" would
        // both look identical (no "trophy" row), and the backfill would
        // re-query the large majority of the pool (players who won none of
        // the 3 seeded trophies) on every future run, forever.
        var checkedPlayerIds = qidToPlayerId.Values.ToList();

        // Never overwrites an already-processed (player, trophy) pair — see
        // IPlayerTrophyStatsRefreshService's own doc comment for why this
        // defensive re-check exists even though the primary caller
        // (PlayerTrophyStatsBackfillService) already filters its own
        // candidates via GetPlayersMissingTrophyStatsAsync. Loaded for every
        // checked player, not just the ones either sweep resolved data for.
        var existingAttributesByPlayerId = await playerAttributeRepository.GetPlayerAttributesByPlayerIdsAsync(
            checkedPlayerIds, cancellationToken);

        var attributesToAdd = new List<PlayerAttribute>();
        var playerDataToAdd = new List<PlayerData>();
        var syncedAt = DateTime.UtcNow;

        foreach (var (qid, playerId) in qidToPlayerId)
        {
            // The "checked" marker: written unconditionally for every player
            // in this successfully-queried batch, in addition to (not
            // instead of) the real PlayerAttribute/PlayerData writes below
            // for players whose data actually resolved. See
            // PlayerInternationalStatsRefreshService's own comment on the
            // equivalent marker write for why no already-marked defensive
            // re-check is needed here either — this method's only
            // production caller already filters through
            // GetPlayersMissingTrophyStatsAsync first.
            playerDataToAdd.Add(new PlayerData
            {
                Id = Guid.NewGuid(),
                PlayerId = playerId,
                Field = PlayerData.TrophyStatsCheckedField,
                Value = PlayerData.TrophyStatsCheckedValue,
                Source = WikidataDataSource,
                Confidence = VerifiedConfidence,
                SyncedAt = syncedAt,
            });

            // A player can legitimately hold more than one trophy (unlike
            // caps/goals' "at most one winning statement" shape) — collect
            // every distinct trophy NAME this player won across both
            // sweeps, deduplicated by name (not by QID), since the
            // downstream "trophy" PlayerAttribute row IS the trophy's name
            // (WikidataLookupService's own existing byproduct-path row
            // shape, ADR-0111/ADR-0113 — no change to that derivation).
            var wonTrophyNames = new HashSet<string>();
            if (individualResultsByPlayerQid.TryGetValue(qid, out var wonIndividualTrophyQids))
            {
                foreach (var trophyQid in wonIndividualTrophyQids)
                {
                    if (individualTrophyNameByQid.TryGetValue(trophyQid, out var trophyName))
                        wonTrophyNames.Add(trophyName);
                }
            }
            if (teamResultsByPlayerQid.TryGetValue(qid, out var wonTeamTrophyQids))
            {
                foreach (var trophyQid in wonTeamTrophyQids)
                {
                    if (teamTrophyNameByQid.TryGetValue(trophyQid, out var trophyName))
                        wonTrophyNames.Add(trophyName);
                }
            }

            if (wonTrophyNames.Count == 0)
                continue; // Wikidata responded but this player won none of the seeded trophies — not a failure.

            HashSet<string> existingTrophyNames = existingAttributesByPlayerId.TryGetValue(playerId, out var existingRows)
                ? existingRows.Where(row => row.AttributeType == TrophyAttributeType).Select(row => row.AttributeValue).ToHashSet()
                : [];

            foreach (var trophyName in wonTrophyNames)
            {
                // Avoids duplicating a (player, trophy) row the byproduct
                // path (WikidataLookupService) already wrote — that path
                // predates this marker entirely, and REQ-1501's own text
                // (ADR-0113 Context) confirms exactly 20 such rows already
                // exist in the real pool.
                if (existingTrophyNames.Contains(trophyName))
                    continue;

                attributesToAdd.Add(new PlayerAttribute { PlayerId = playerId, AttributeType = TrophyAttributeType, AttributeValue = trophyName });
                playerDataToAdd.Add(new PlayerData
                {
                    Id = Guid.NewGuid(),
                    PlayerId = playerId,
                    Field = TrophyAttributeType,
                    Value = trophyName,
                    Source = WikidataDataSource,
                    Confidence = VerifiedConfidence,
                    SyncedAt = syncedAt,
                });
            }
        }

        if (attributesToAdd.Count > 0)
            await playerAttributeRepository.AddPlayerAttributesBatchAsync(attributesToAdd, cancellationToken);

        // playerDataToAdd always has at least the checked markers at this
        // point (checkedPlayerIds.Count > 0, guaranteed by the
        // qidToPlayerId.Count == 0 early return above), so this is
        // unconditional — unlike attributesToAdd, which can legitimately be
        // empty for a batch where nobody won anything new.
        await playerDataRepository.AddPlayerDataBatchAsync(playerDataToAdd, cancellationToken);
    }
}
