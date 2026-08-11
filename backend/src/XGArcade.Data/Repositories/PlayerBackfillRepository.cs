using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class PlayerBackfillRepository(XGArcadeDbContext dbContext) : IPlayerBackfillRepository
{
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
}
