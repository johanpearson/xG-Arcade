using Microsoft.EntityFrameworkCore;

namespace XGArcade.Data.Seeding;

// S-037: manually-triggered, one-off maintenance tool (Program.cs's
// `clean-stale-club-attributes` CLI verb) for recovering from club data
// fetched while something upstream was wrong — originally a wrong
// Wikidata QID in ReferenceDataSeeder.cs discovered only after real
// PlayerAttribute/PlayerData rows were already fetched under it; later
// also the recovery path for club rows fetched under WikidataClient's
// truthy wdt:P54 query, which silently omitted historical clubs (see
// CleanAllSeededClubsAsync below). A wrong
// club QID is silent and hard to detect: WikidataClient's SPARQL queries
// have no way to know a QID doesn't actually correspond to the intended
// club — if it happens to be some *other* real Wikidata entity that also
// satisfies the query pattern, real players get returned and persisted
// under the intended club's name, indistinguishable from correct data by
// inspection. This happened for real: 4 of S-036's club QIDs (Napoli,
// AS Roma, Sevilla, Porto) were wrong, caught only by manual verification
// against live Wikidata pages — see NOTES.md's 2026-07-13 entry.
//
// Deliberately NOT idempotent-forever the way the other Seeding backfillers
// are (PlayerNormalizedFullNameBackfiller, UserDisplayNameBackfiller,
// LeagueMembershipBackfiller). Those detect "is this row still in the old
// broken state" from current data alone and naturally stop matching once
// fixed, so they're safe to leave wired into every migrate-and-seed run
// forever. A wrong-QID row has no such marker — a PlayerAttribute(club=
// "Napoli") row looks identical whether it came from the wrong QID or the
// corrected one — so there is no way to make this self-limiting the same
// way. It must be run manually, once, for the specific club name(s) just
// corrected in ReferenceDataSeeder.cs, and *before* the next
// warm-player-cache run — running it after a fresh warm-player-cache pass
// would incorrectly wipe the new, correct data too, since nothing here can
// tell old from new. Running it again afterward (before any fresh re-fetch
// happens) is harmless: it just finds nothing left to delete.
public static class StaleClubAttributeCleaner
{
    private const string ClubAttributeType = "club";

    // Returns both row counts removed, for a caller-visible summary
    // (Program.cs's CLI verb prints it) — PlayerData is the larger, more
    // load-bearing number (see below) and reporting only PlayerAttribute's
    // count would hide that.
    public static async Task<(int PlayerAttributeCount, int PlayerDataCount)> CleanAsync(
        XGArcadeDbContext dbContext,
        IReadOnlyCollection<string> clubNames,
        CancellationToken cancellationToken = default)
    {
        // PlayerData is the raw per-source append log (COMP-06) — every
        // live lookup ever made for these club names, correct or not,
        // needs to go, not just the most recent one.
        var stalePlayerData = await dbContext.PlayerData
            .Where(d => d.Field == ClubAttributeType && clubNames.Contains(d.Value))
            .ToListAsync(cancellationToken);
        dbContext.PlayerData.RemoveRange(stalePlayerData);

        // PlayerAttribute is the effective, denormalized view grid
        // generation and guess-checking actually query (REQ-101/REQ-203) —
        // this is what actually needs to be gone for a clean re-fetch.
        var staleAttributes = await dbContext.PlayerAttributes
            .Where(a => a.AttributeType == ClubAttributeType && clubNames.Contains(a.AttributeValue))
            .ToListAsync(cancellationToken);
        dbContext.PlayerAttributes.RemoveRange(staleAttributes);

        await dbContext.SaveChangesAsync(cancellationToken);

        return (staleAttributes.Count, stalePlayerData.Count);
    }

    // All-clubs mode (the CLI verb's `--all-clubs` argument): resolves the
    // club-name list from the ClubDefinition reference table at runtime
    // instead of requiring the operator to hand-type every seeded club
    // name. Added for the wdt:P54 truthy-query incident (the preferred-
    // rank bug fixed in WikidataClient's query builders — see the comment
    // on BuildCountryClubIntersectionQuery): every club row ever fetched
    // under the truthy query is suspect-incomplete for ALL seeded clubs, and
    // a hand-typed ~32-name list is exactly the typo surface where one
    // misspelled club silently stays stale (CleanAsync can't tell a typo
    // from a club with nothing to clean — both remove zero rows). Same
    // manual, deliberate-friction character as the named mode: still a
    // one-off CLI verb run before the next warm-player-cache pass, still
    // never wired into migrate-and-seed.
    //
    // Returns the resolved names alongside the counts so the CLI summary
    // can show exactly which clubs were swept — an operator should be able
    // to eyeball that the resolved list matches the seeded reference data.
    public static async Task<(int PlayerAttributeCount, int PlayerDataCount, IReadOnlyList<string> ClubNames)> CleanAllSeededClubsAsync(
        XGArcadeDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        var clubNames = await dbContext.ClubDefinitions
            .Select(c => c.Name)
            .ToListAsync(cancellationToken);

        // Zero seeded clubs means the premise of this mode ("the reference
        // table knows the club list") doesn't hold — almost certainly a
        // wrong connection string or a never-seeded database, not a real
        // "nothing to clean" case. Fail loudly, same as the verb's own
        // malformed-invocation handling in Program.cs, rather than
        // printing a plausible-looking "removed 0 rows" success.
        if (clubNames.Count == 0)
        {
            throw new InvalidOperationException(
                "clean-stale-club-attributes --all-clubs found no ClubDefinition rows to resolve club names from — " +
                "is this the right database, and has migrate-and-seed run against it?");
        }

        var (playerAttributeCount, playerDataCount) = await CleanAsync(dbContext, clubNames, cancellationToken);
        return (playerAttributeCount, playerDataCount, clubNames);
    }
}
