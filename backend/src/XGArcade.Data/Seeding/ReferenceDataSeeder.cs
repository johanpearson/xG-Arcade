using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Seeding;

// S-005 (docs/backlog.md): the hand-curated Tier 0 reference data — the
// original 15 clubs and 20 countries whose Wikidata QIDs were looked up and
// verified in MVP-SCOPE.md, plus a 2026-07-13 expansion (S-036, ADR-0023's
// "revisit if S-014's threshold bump makes grid generation struggle in
// practice" follow-up — it did), plus an S-037 correction/second expansion
// the same day. Pure data entry, no live research performed here or in the
// original pass either.
//
// S-036's expansion entries were NOT verified against a live Wikidata
// endpoint from this sandbox (network policy blocks wikidata.org — see
// NOTES.md's 2026-07-09/11 entries for the same limitation elsewhere in
// this codebase); they were well-known, stable Wikidata QIDs from training
// knowledge, not freshly looked up. That risk was real, not theoretical:
// manual verification against live Wikidata (S-037) found 4 of the 6 new
// club QIDs were wrong — each happened to be some *other* real Wikidata
// entity, so WikidataClient's SPARQL queries against them didn't error or
// return empty, they silently returned real-but-wrong player data. See
// NOTES.md's 2026-07-13 entry and StaleClubAttributeCleaner (which purges
// whatever got persisted under a corrected club's name so it gets a clean
// re-fetch, since nothing in the persisted data can tell a wrong-QID row
// from a correct one after the fact). S-037's 11 further new clubs use
// QIDs someone actually checked, not training-knowledge guesses.
//
// Idempotent by (Name) — CountryDefinition/ClubDefinition/TrophyDefinition's
// unique index — but *not* purely additive: an existing row whose seeded
// WikidataQid no longer matches this file (a correction, not a new entry)
// gets its QID updated in place, not skipped — otherwise fixing a wrong QID
// here would silently do nothing against an already-seeded database,
// exactly the gap that let S-037's correction not take effect on its own.
//
// S-031 (2026-07-20, REQ-108/ADR-0012): added the Trophies array below,
// seeding exactly one value, Ballon d'Or (Q166177). Like S-036's expansion
// above, this QID was **not verified against a live Wikidata endpoint from
// this sandbox** (same network policy block — wikidata.org is unreachable
// here) — it's a training-knowledge value, not a freshly looked-up one.
// Given S-036/S-037's own history (4 of 6 guessed club QIDs turned out
// wrong, silently returning real-but-wrong player data until a live check
// caught it), **a human must verify Q166177 against the live Wikidata page
// for Ballon d'Or before this is relied on in a real deployment.** If it
// turns out wrong, correct it here the same way S-037 corrected the club
// QIDs — this class's idempotent-by-Name upsert will apply the fix on the
// next seed run without any separate migration.
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
        // S-036 expansion. Napoli/AS Roma/Sevilla/Porto's QIDs were wrong
        // (training-knowledge guesses, never checked against live Wikidata)
        // — corrected in S-037 against verified Wikidata pages. See this
        // file's own doc comment and NOTES.md's 2026-07-13 entry.
        ("Tottenham Hotspur", "Q18741"),
        ("Atletico Madrid", "Q8701"),
        ("Napoli", "Q2641"),
        ("AS Roma", "Q2739"),
        ("Sevilla", "Q10329"),
        ("Porto", "Q128446"),
        // S-037 expansion — QIDs verified against live Wikidata pages, not
        // training-knowledge guesses (unlike S-036's above).
        ("RB Leipzig", "Q702455"),
        ("Bayer Leverkusen", "Q104761"),
        ("Marseille", "Q132885"),
        ("Lyon", "Q704"),
        ("Monaco", "Q180305"),
        ("Lille", "Q19516"),
        ("Lazio", "Q2609"),
        ("Valencia", "Q10333"),
        ("Real Sociedad", "Q10315"),
        ("Newcastle United", "Q18716"),
        ("West Ham United", "Q18747"),
    ];

    // S-031: v1 seeds exactly one trophy (individual awards only, REQ-108) —
    // IsTeamTrophy = false. Q166177 is a training-knowledge QID, NOT
    // verified against a live Wikidata page this session — see this class's
    // own doc comment above.
    private static readonly (string Name, string WikidataQid, bool IsTeamTrophy)[] Trophies =
    [
        ("Ballon d'Or", "Q166177", false),
    ];

    public static async Task SeedAsync(XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        // Keyed by Name (CountryDefinition/ClubDefinition's unique index) so
        // an existing row can be corrected in place, not just skipped —
        // see this class's own doc comment (S-037) for why that matters.
        var existingCountries = await dbContext.CountryDefinitions.ToDictionaryAsync(c => c.Name, cancellationToken);
        foreach (var (name, wikidataQid) in Countries)
        {
            if (existingCountries.TryGetValue(name, out var existing))
                existing.WikidataQid = wikidataQid;
            else
                dbContext.CountryDefinitions.Add(new CountryDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid });
        }

        var existingClubs = await dbContext.ClubDefinitions.ToDictionaryAsync(c => c.Name, cancellationToken);
        foreach (var (name, wikidataQid) in Clubs)
        {
            if (existingClubs.TryGetValue(name, out var existing))
                existing.WikidataQid = wikidataQid;
            else
                dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid });
        }

        var existingTrophies = await dbContext.TrophyDefinitions.ToDictionaryAsync(t => t.Name, cancellationToken);
        foreach (var (name, wikidataQid, isTeamTrophy) in Trophies)
        {
            if (existingTrophies.TryGetValue(name, out var existing))
            {
                existing.WikidataQid = wikidataQid;
                existing.IsTeamTrophy = isTeamTrophy;
            }
            else
            {
                dbContext.TrophyDefinitions.Add(new TrophyDefinition
                {
                    Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid, IsTeamTrophy = isTeamTrophy,
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
