using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Seeding;

// S-005 (docs/backlog.md): the hand-curated Tier 0 reference data — the
// original 15 clubs and 20 countries whose Wikidata QIDs were looked up and
// verified in MVP-SCOPE.md, plus a 2026-07-13 expansion (S-036, ADR-0023's
// "revisit if S-014's threshold bump makes grid generation struggle in
// practice" follow-up — it did). Pure data entry, no live research
// performed here or in the original pass either.
//
// The expansion entries below were NOT verified against a live Wikidata
// endpoint from this sandbox (network policy blocks wikidata.org — see
// NOTES.md's 2026-07-09/11 entries for the same limitation elsewhere in
// this codebase); they're well-known, stable Wikidata QIDs from training
// knowledge, not freshly looked up. A wrong QID here is self-limiting, not
// dangerous: WikidataClient's SPARQL queries against a nonexistent or
// mismatched QID just return zero bindings, identical to a real "no shared
// players" result — REQ-110's cache-warming job will surface any entry
// that consistently resolves zero matches across every pairing it's tried
// against, which is the practical way to catch a bad QID here. Spot-check
// before fully trusting this data, but it is safe to run either way.
// Idempotent by (Name) — CountryDefinition/ClubDefinition's unique index —
// so re-running against an already-seeded database inserts nothing new.
public static class ReferenceDataSeeder
{
    private static readonly (string Name, string WikidataQid)[] Countries =
    [
        // Original 20 (S-005).
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
        // S-036 expansion — widens which row headers have a realistic
        // chance of clearing MinValidAnswers=5 against the club pool below.
        ("Ghana", "Q117"),
        ("Cameroon", "Q1009"),
        ("Egypt", "Q79"),
        ("Morocco", "Q1028"),
        ("Algeria", "Q262"),
        ("Tunisia", "Q948"),
        ("Japan", "Q17"),
        ("South Korea", "Q884"),
        ("Australia", "Q408"),
        ("United States of America", "Q30"),
        ("Mexico", "Q96"),
        ("Austria", "Q40"),
        ("Switzerland", "Q39"),
        ("Turkey", "Q43"),
        ("Greece", "Q41"),
        ("Russia", "Q159"),
        ("Ukraine", "Q212"),
        ("Czech Republic", "Q213"),
        ("Ireland", "Q27"),
        ("Canada", "Q16"),
        ("Norway", "Q20"),
        ("Finland", "Q33"),
        ("Hungary", "Q28"),
        ("Chile", "Q298"),
        ("South Africa", "Q258"),
    ];

    private static readonly (string Name, string WikidataQid)[] Clubs =
    [
        // Original 15 (S-005).
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
        // S-036 expansion. Deliberately smaller than the country expansion
        // above — club QIDs carry more risk of a training-knowledge
        // misremember than the very well-known country ones, so this list
        // stays conservative rather than padded; widen it further only with
        // QIDs someone has actually confirmed, not more guesses.
        ("Tottenham Hotspur", "Q18741"),
        ("Atletico Madrid", "Q8701"),
        ("Napoli", "Q1176"),
        ("AS Roma", "Q2483"),
        ("Sevilla", "Q10360"),
        ("Porto", "Q182982"),
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
