using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerStoreRepository(XGArcadeDbContext dbContext) : IPlayerStoreRepository
{
    public async Task<PlayerOverride?> GetOverrideAsync(Guid playerId, string field, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerOverrides
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.PlayerId == playerId && o.Field == field, cancellationToken);

    public async Task<PlayerOverride?> GetOverrideByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerOverrides.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task AddOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerOverrides.Add(playerOverride);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateOverrideAsync(PlayerOverride playerOverride, CancellationToken cancellationToken = default)
    {
        dbContext.PlayerOverrides.Update(playerOverride);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteOverrideAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var playerOverride = await dbContext.PlayerOverrides.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (playerOverride is null)
            return false;

        dbContext.PlayerOverrides.Remove(playerOverride);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> HasEffectiveAttributeAsync(
        Guid playerId, string attributeType, string attributeValue, CancellationToken cancellationToken = default)
    {
        // REQ-203/REQ-501: a PlayerOverride for this field always wins,
        // replacing every cached PlayerAttribute row of that type for this
        // player — not merged/added to them.
        var overrideRecord = await GetOverrideAsync(playerId, attributeType, cancellationToken);
        if (overrideRecord is not null)
            return overrideRecord.Value == attributeValue;

        return await dbContext.PlayerAttributes
            .AsNoTracking()
            .AnyAsync(pa => pa.PlayerId == playerId && pa.AttributeType == attributeType && pa.AttributeValue == attributeValue, cancellationToken);
    }

    public async Task<IReadOnlyList<Player>> GetPlayersMissingPhotoAsync(
        IReadOnlyCollection<Guid> excludingPlayerIds, int batchSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Players
            .AsNoTracking()
            .Where(p => p.WikidataQid != null && p.PhotoUrl == null);

        if (excludingPlayerIds.Count > 0)
            query = query.Where(p => !excludingPlayerIds.Contains(p.Id));

        return await query
            .OrderBy(p => p.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdatePlayerPhotosAsync(
        IReadOnlyDictionary<Guid, string> photoUrlByPlayerId, CancellationToken cancellationToken = default)
    {
        if (photoUrlByPlayerId.Count == 0)
            return;

        var playerIds = photoUrlByPlayerId.Keys.ToList();
        var players = await dbContext.Players
            .Where(p => playerIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var player in players)
            player.PhotoUrl = photoUrlByPlayerId[player.Id];

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Bug fix (2026-08-10, bug-bundle): before WikidataClient's 2026-08-02 fix
    // (see that file's BuildPlayerPositionsAndBirthYearsByQidsQuery/
    // BuildIntersectionQuery comments), Player.Position was populated with the
    // raw Wikidata P413 entity URI (e.g.
    // "http://www.wikidata.org/entity/Q8025128") instead of a resolved
    // position label. Those rows are NOT NULL, so the plain "is it null"
    // candidate query below was silently and permanently skipping them —
    // every future backfill run re-selected only genuinely-empty rows and
    // never touched the already-bad ones, which is exactly what xG Path
    // testers saw as a raw QID URI rendered in the position clue. Treat that
    // shape as "missing" too so PlayerPositionBirthYearBackfillService
    // re-fetches and resolves it. No equivalent bad-sentinel state exists for
    // BirthYear — it's parsed from an xsd:dateTime binding into an int, never
    // carried through as a raw URI or other placeholder string — so its
    // condition is left as a plain null check; don't invent a fix for a
    // failure mode that doesn't exist there.
    private const string WikidataEntityUriPrefix = "http://www.wikidata.org/entity/";

    public async Task<IReadOnlyList<Player>> GetPlayersMissingPositionOrBirthYearAsync(
        IReadOnlyCollection<Guid> excludingPlayerIds, int batchSize, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Players
            .AsNoTracking()
            .Where(p => p.WikidataQid != null
                && (p.Position == null || p.Position.StartsWith(WikidataEntityUriPrefix) || p.BirthYear == null));

        if (excludingPlayerIds.Count > 0)
            query = query.Where(p => !excludingPlayerIds.Contains(p.Id));

        return await query
            .OrderBy(p => p.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdatePlayerPositionsAndBirthYearsAsync(
        IReadOnlyDictionary<Guid, PlayerPositionBirthYearUpdate> updatesByPlayerId, CancellationToken cancellationToken = default)
    {
        if (updatesByPlayerId.Count == 0)
            return;

        var playerIds = updatesByPlayerId.Keys.ToList();
        var players = await dbContext.Players
            .Where(p => playerIds.Contains(p.Id))
            .ToListAsync(cancellationToken);

        foreach (var player in players)
        {
            var update = updatesByPlayerId[player.Id];

            // REQ-1207's "set once" contract, extended to this backfill: a
            // null on the update means "this run found nothing new for this
            // field," never "clear the existing value," and an already-set
            // field is never overwritten even if the update did resolve a
            // (necessarily identical, since Wikidata's data doesn't change
            // underneath us mid-run) value for it — same defensive posture
            // as PlayerCreationRequest's own "set only at creation, never
            // revisited" rule.
            //
            // Bug fix (2026-08-10, bug-bundle): one deliberate exception to
            // "already-set is never overwritten" — a Position that's still
            // the raw Wikidata entity URI (see
            // GetPlayersMissingPositionOrBirthYearAsync's own comment above)
            // is not a genuine value, it's the pre-2026-08-02 write-path bug
            // frozen in place. GetPlayersMissingPositionOrBirthYearAsync
            // already selects those rows back into this backfill's candidate
            // set, so the write side has to actually replace them or the
            // fix is a no-op — this is the only case where a non-null
            // Position is overwritten.
            if (update.Position is not null
                && (player.Position is null || player.Position.StartsWith(WikidataEntityUriPrefix)))
                player.Position = update.Position;

            if (update.BirthYear is not null && player.BirthYear is null)
                player.BirthYear = update.BirthYear;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PlayerCareerStint>> GetCareerStintsAsync(
        Guid playerId, CancellationToken cancellationToken = default) =>
        await dbContext.PlayerCareerStints
            .AsNoTracking()
            .Where(s => s.PlayerId == playerId)
            .ToListAsync(cancellationToken);

    public async Task AddCareerStintsAsync(
        Guid playerId, IReadOnlyList<PlayerCareerStint> newStints, CancellationToken cancellationToken = default)
    {
        if (newStints.Count == 0)
            return;

        var existing = await dbContext.PlayerCareerStints
            .Where(s => s.PlayerId == playerId)
            .ToListAsync(cancellationToken);

        dbContext.PlayerCareerStints.AddRange(newStints);

        // ADR-0042/S-079: SequenceOrder is resolved here, across the
        // player's FULL stint set (existing rows + newStints), not just the
        // newly-added ones — a stint discovered later that chronologically
        // precedes existing rows must still shift everyone else's
        // SequenceOrder. Ongoing stints (EndYear null) sort last among
        // stints sharing the same StartYear.
        var chronological = existing
            .Concat(newStints)
            .OrderBy(s => s.StartYear)
            .ThenBy(s => s.EndYear ?? int.MaxValue)
            .ToList();

        for (var i = 0; i < chronological.Count; i++)
            chronological[i].SequenceOrder = i;

        // One SaveChangesAsync call for the whole batch — load-then-
        // SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync (the InMemory test provider can't translate it).
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetCareerStintCandidatePlayerIdsAsync(
        IReadOnlySet<string> seededClubNames, int minStintCount, CancellationToken cancellationToken = default)
    {
        // Same "materialize via ToListAsync, then GroupBy/filter as
        // LINQ-to-Objects" convention as GroupByPlayerIdAsync/
        // GetUnseededClubCandidatesAsync above — but only a (PlayerId,
        // ClubName) projection here, not the full entity, since this is a
        // hot path (every xG Path round generation) rather than an
        // occasional manual diagnostic job. Exact ordinal/case-sensitive
        // Contains — matches IsEligible's own comparison exactly, NOT
        // GetUnseededClubCandidatesAsync's OrdinalIgnoreCase (a
        // deliberately different, diagnostic-only choice for that method).
        var stints = await dbContext.PlayerCareerStints
            .AsNoTracking()
            .Select(s => new { s.PlayerId, s.ClubName })
            .ToListAsync(cancellationToken);

        return stints
            .GroupBy(s => s.PlayerId)
            .Where(g => g.Count() >= minStintCount && g.Any(s => seededClubNames.Contains(s.ClubName)))
            .Select(g => g.Key)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>>> GetCareerStintsByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default)
    {
        if (playerIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>();

        var idList = playerIds.ToList();
        return await GroupByPlayerIdAsync(
            dbContext.PlayerCareerStints.Where(s => idList.Contains(s.PlayerId)),
            stint => stint.PlayerId,
            cancellationToken);
    }

    // Duplicated from PlayerAliasRepository/PlayerAttributeRepository
    // (S-106, per that story's own explicit instruction) rather than shared
    // across repository classes — repositories shouldn't depend on each
    // other. Only GetCareerStintsByPlayerIdsAsync above still uses this copy
    // (GetPlayerAliasesByPlayerIdsAsync/GetPlayerAttributesByPlayerIdsAsync,
    // the other two former callers, moved to their own repositories along
    // with their own copy of this helper).
    //
    // Originally factored out (quality-architect review, 2026-07-21) because
    // GetPlayerAliasesByPlayerIdsAsync/GetPlayerAttributesByPlayerIdsAsync/
    // GetCareerStintsByPlayerIdsAsync were the identical "fetch rows already
    // filtered to a set of player ids, then group into one dictionary entry
    // per player id" shape, differing only in which entity/DbSet they
    // queried — that boilerplate is factored out here so each caller keeps
    // only its entity-specific query, not the AsNoTracking/GroupBy/
    // ToDictionary ceremony duplicated a second time. The caller supplies
    // its own already-filtered IQueryable<TEntity>
    // (Where(x => idList.Contains(x.PlayerId))) since each source DbSet is
    // different; this helper only owns the materialize-then-group step that
    // was genuinely identical between them.
    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TEntity>>> GroupByPlayerIdAsync<TEntity>(
        IQueryable<TEntity> filteredQuery,
        Func<TEntity, Guid> playerIdSelector,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var rows = await filteredQuery.AsNoTracking().ToListAsync(cancellationToken);

        return rows
            .GroupBy(playerIdSelector)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TEntity>)g.ToList());
    }

    public async Task AddCareerStintsBatchAsync(
        IReadOnlyDictionary<Guid, IReadOnlyList<PlayerCareerStint>> newStintsByPlayerId, CancellationToken cancellationToken = default)
    {
        var playerIds = newStintsByPlayerId.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
        if (playerIds.Count == 0)
            return;

        var existingByPlayer = (await dbContext.PlayerCareerStints
                .Where(s => playerIds.Contains(s.PlayerId))
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.PlayerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var playerId in playerIds)
        {
            var newStints = newStintsByPlayerId[playerId];
            dbContext.PlayerCareerStints.AddRange(newStints);

            // ADR-0042/S-079: SequenceOrder is resolved here, across the
            // player's FULL stint set (existing rows + newStints), not just
            // the newly-added ones — same chronological re-sequencing rule
            // as AddCareerStintsAsync's own comment, just applied to every
            // affected player in this call rather than one at a time.
            var chronological = existingByPlayer.GetValueOrDefault(playerId, [])
                .Concat(newStints)
                .OrderBy(s => s.StartYear)
                .ThenBy(s => s.EndYear ?? int.MaxValue)
                .ToList();

            for (var i = 0; i < chronological.Count; i++)
                chronological[i].SequenceOrder = i;
        }

        // One SaveChangesAsync call for the whole batch, across every
        // affected player — load-then-SaveChangesAsync
        // (docs/coding-guidelines.md), never ExecuteUpdateAsync.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> IsConfirmedLowAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default) =>
        await dbContext.ConfirmedLowMatchPairs
            .AsNoTracking()
            .AnyAsync(c =>
                c.FirstAttributeType == firstAttributeType && c.FirstAttributeValue == firstAttributeValue &&
                c.SecondAttributeType == secondAttributeType && c.SecondAttributeValue == secondAttributeValue,
                cancellationToken);

    public async Task RecordConfirmedLowAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        int matchCount, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.ConfirmedLowMatchPairs.FirstOrDefaultAsync(c =>
            c.FirstAttributeType == firstAttributeType && c.FirstAttributeValue == firstAttributeValue &&
            c.SecondAttributeType == secondAttributeType && c.SecondAttributeValue == secondAttributeValue,
            cancellationToken);

        var confirmedAt = DateTime.UtcNow;

        if (existing is not null)
        {
            // Re-confirmation of an already-marked pair (see this method's
            // own interface doc comment) — update in place rather than
            // inserting a duplicate composite-key row.
            existing.MatchCount = matchCount;
            existing.ConfirmedAt = confirmedAt;
        }
        else
        {
            dbContext.ConfirmedLowMatchPairs.Add(new ConfirmedLowMatchPair
            {
                FirstAttributeType = firstAttributeType,
                FirstAttributeValue = firstAttributeValue,
                SecondAttributeType = secondAttributeType,
                SecondAttributeValue = secondAttributeValue,
                MatchCount = matchCount,
                ConfirmedAt = confirmedAt,
            });
        }

        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync/upsert — the InMemory test provider can't
        // translate either.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> IsPersistentTechnicalFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        int threshold, CancellationToken cancellationToken = default) =>
        await dbContext.PairLookupFailures
            .AsNoTracking()
            .AnyAsync(f =>
                f.FirstAttributeType == firstAttributeType && f.FirstAttributeValue == firstAttributeValue &&
                f.SecondAttributeType == secondAttributeType && f.SecondAttributeValue == secondAttributeValue &&
                f.ConsecutiveFailureCount >= threshold,
                cancellationToken);

    public async Task RecordTechnicalFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.PairLookupFailures.FirstOrDefaultAsync(f =>
            f.FirstAttributeType == firstAttributeType && f.FirstAttributeValue == firstAttributeValue &&
            f.SecondAttributeType == secondAttributeType && f.SecondAttributeValue == secondAttributeValue,
            cancellationToken);

        var failedAt = DateTime.UtcNow;

        if (existing is not null)
        {
            existing.ConsecutiveFailureCount++;
            existing.LastFailedAt = failedAt;
        }
        else
        {
            dbContext.PairLookupFailures.Add(new PairLookupFailure
            {
                FirstAttributeType = firstAttributeType,
                FirstAttributeValue = firstAttributeValue,
                SecondAttributeType = secondAttributeType,
                SecondAttributeValue = secondAttributeValue,
                ConsecutiveFailureCount = 1,
                LastFailedAt = failedAt,
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearTechnicalFailureAsync(
        string firstAttributeType, string firstAttributeValue,
        string secondAttributeType, string secondAttributeValue,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.PairLookupFailures.FirstOrDefaultAsync(f =>
            f.FirstAttributeType == firstAttributeType && f.FirstAttributeValue == firstAttributeValue &&
            f.SecondAttributeType == secondAttributeType && f.SecondAttributeValue == secondAttributeValue,
            cancellationToken);

        if (existing is null)
            return;

        dbContext.PairLookupFailures.Remove(existing);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UnseededClubCandidate>> GetUnseededClubCandidatesAsync(
        int top, CancellationToken cancellationToken = default)
    {
        // Case-insensitive comparison against ClubDefinition.Name — a
        // Wikidata-sourced PlayerCareerStint.ClubName (a P54 qualifier
        // label) and a hand-seeded ClubDefinition.Name come from two
        // different paths and could plausibly differ only in case even when
        // they denote the same club; an exact-case comparison would then
        // wrongly surface an already-seeded club as a "gap." This is
        // diagnostic-only output for a human to review — if this
        // normalization assumption ever hides a genuinely distinct club
        // that happens to share a case-insensitive name with a seeded one
        // (unlikely for real football clubs), the human review step this
        // verb's own workflow doc comment requires (manually verifying each
        // candidate's Wikidata QID before adding anything) is exactly the
        // safety net that catches it.
        var seededClubNames = new HashSet<string>(
            await dbContext.ClubDefinitions.AsNoTracking().Select(c => c.Name).ToListAsync(cancellationToken),
            StringComparer.OrdinalIgnoreCase);

        // Loads every PlayerCareerStint's (ClubName, PlayerId) pair — a
        // full-table-scale read, tolerated here because this is an
        // occasional manual diagnostic job, not a hot path (contrast
        // GetCareerStintCandidatePlayerIdsAsync below, which is a hot path
        // and narrows further to avoid loading unrelated columns). The
        // grouping/distinct-count/case-insensitive filter below all happen
        // in memory after materializing, not translated to SQL, so there's
        // no provider-specific LOWER()-translation risk to worry about.
        var stints = await dbContext.PlayerCareerStints
            .AsNoTracking()
            .Select(s => new { s.ClubName, s.PlayerId })
            .ToListAsync(cancellationToken);

        return stints
            .Where(s => !seededClubNames.Contains(s.ClubName))
            .GroupBy(s => s.ClubName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UnseededClubCandidate(g.Key, g.Select(s => s.PlayerId).Distinct().Count()))
            .OrderByDescending(c => c.PlayerCount)
            .Take(top)
            .ToList();
    }
}
