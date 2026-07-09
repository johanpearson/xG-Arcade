using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Seeding;

// S-005 (docs/backlog.md): the hand-curated Tier 0 reference data — the 15
// clubs and 20 countries whose Wikidata QIDs were already looked up and
// verified in MVP-SCOPE.md. Pure data entry, no research performed here.
// Idempotent by (Name) — CountryDefinition/ClubDefinition's unique index —
// so re-running against an already-seeded database inserts nothing new.
public static class ReferenceDataSeeder
{
    private static readonly (string Name, string WikidataQid)[] Countries =
    [
        ("Brazil", "Q155"),
        ("Argentina", "Q414"),
        ("France", "Q142"),
        ("Germany", "Q183"),
        ("Spain", "Q29"),
        ("United Kingdom", "Q145"),
        ("Italy", "Q38"),
        ("Netherlands", "Q55"),
        ("Portugal", "Q45"),
        ("Belgium", "Q31"),
        ("Croatia", "Q224"),
        ("Uruguay", "Q77"),
        ("Colombia", "Q739"),
        ("Nigeria", "Q1033"),
        ("Senegal", "Q1041"),
        ("Ivory Coast", "Q1008"),
        ("Serbia", "Q403"),
        ("Poland", "Q36"),
        ("Sweden", "Q34"),
        ("Denmark", "Q35"),
    ];

    private static readonly (string Name, string WikidataQid)[] Clubs =
    [
        ("Real Madrid", "Q8682"),
        ("Barcelona", "Q7156"),
        ("Manchester United", "Q18656"),
        ("Manchester City", "Q50602"),
        ("Liverpool", "Q1130849"),
        ("Arsenal", "Q9617"),
        ("Chelsea", "Q9616"),
        ("Bayern Munich", "Q15789"),
        ("Borussia Dortmund", "Q41420"),
        ("Juventus", "Q1422"),
        ("AC Milan", "Q1543"),
        ("Inter Milan", "Q631"),
        ("Paris Saint-Germain", "Q483020"),
        ("Ajax", "Q81888"),
        ("Benfica", "Q131499"),
    ];

    public static async Task SeedAsync(XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var existingCountryNames = await dbContext.CountryDefinitions.Select(c => c.Name).ToListAsync(cancellationToken);
        foreach (var (name, wikidataQid) in Countries)
        {
            if (!existingCountryNames.Contains(name))
                dbContext.CountryDefinitions.Add(new CountryDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid });
        }

        var existingClubNames = await dbContext.ClubDefinitions.Select(c => c.Name).ToListAsync(cancellationToken);
        foreach (var (name, wikidataQid) in Clubs)
        {
            if (!existingClubNames.Contains(name))
                dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
