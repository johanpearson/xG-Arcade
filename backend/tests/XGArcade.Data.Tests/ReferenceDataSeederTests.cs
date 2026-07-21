using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Seeding;

namespace XGArcade.Data.Tests;

// S-005 (docs/backlog.md): seeds the Tier 0 hand-curated reference data
// (MVP-SCOPE.md's verified QID tables) — pure data entry, so these tests
// check the seeder's mechanics (idempotency, row counts) rather than
// re-verifying the QIDs themselves.
public class ReferenceDataSeederTests
{
    private XGArcadeDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task REQ109_SeedAsync_PopulatesAllCountriesAndClubsFromMvpScope()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        // S-036 widened 20/15 to 45/21, S-037 widened clubs again to 32
        // (45/32), REQ-114/ADR-0035 (2026-07-21) added 4 home-nation rows
        // to CountryDefinitions (49/32) — these counts intentionally stay
        // hardcoded (not read back from ReferenceDataSeeder itself) so a
        // future accidental change to the seed data is caught here, not
        // silently accepted.
        Assert.That(await _dbContext.CountryDefinitions.CountAsync(), Is.EqualTo(49));
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(), Is.EqualTo(32));
    }

    [Test]
    public async Task REQ109_SeedAsync_SeededRows_HaveNonEmptyWikidataQids()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        Assert.That(await _dbContext.CountryDefinitions.AnyAsync(c => string.IsNullOrEmpty(c.WikidataQid)), Is.False);
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => string.IsNullOrEmpty(c.WikidataQid)), Is.False);
    }

    [Test]
    public async Task REQ109_SeedAsync_RunTwice_IsIdempotent_CreatesNoDuplicateRows()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        // See REQ109_SeedAsync_PopulatesAllCountriesAndClubsFromMvpScope's
        // own comment for why these counts stay hardcoded.
        Assert.That(await _dbContext.CountryDefinitions.CountAsync(), Is.EqualTo(49));
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(), Is.EqualTo(32));
    }

    [Test]
    public async Task REQ109_SeedAsync_DoesNotDuplicate_WhenSomeRowsAlreadyExist()
    {
        _dbContext.CountryDefinitions.Add(new CountryDefinition { Id = Guid.NewGuid(), Name = "France", WikidataQid = "Q142" });
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
        await _dbContext.SaveChangesAsync();

        await ReferenceDataSeeder.SeedAsync(_dbContext);

        Assert.That(await _dbContext.CountryDefinitions.CountAsync(c => c.Name == "France"), Is.EqualTo(1));
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(c => c.Name == "Arsenal"), Is.EqualTo(1));
        // See REQ109_SeedAsync_PopulatesAllCountriesAndClubsFromMvpScope's
        // own comment for why these counts stay hardcoded.
        Assert.That(await _dbContext.CountryDefinitions.CountAsync(), Is.EqualTo(49));
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(), Is.EqualTo(32));
    }

    // REQ-114/ADR-0035 (2026-07-21): supersedes the old
    // "UnitedKingdom_IsSeeded_NotEngland" assertion — England is now seeded
    // too, as a SECOND, distinct CountryDefinition row alongside United
    // Kingdom, never a replacement for it (citizenship and "country
    // represented" genuinely differ for dual nationals/naturalized
    // players, ADR-0035).
    [Test]
    public async Task REQ114_SeedAsync_UnitedKingdomAndEngland_BothSeeded_AsDistinctRows()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        var unitedKingdom = await _dbContext.CountryDefinitions.SingleAsync(c => c.Name == "United Kingdom");
        Assert.That(unitedKingdom.WikidataQid, Is.EqualTo("Q145"));
        Assert.That(unitedKingdom.UsesCountryForSportProperty, Is.False,
            "an ordinary sovereign-state country must never be flagged for the P1532 query path");

        var england = await _dbContext.CountryDefinitions.SingleAsync(c => c.Name == "England");
        Assert.That(england.WikidataQid, Is.EqualTo("Q21"));
        Assert.That(england.UsesCountryForSportProperty, Is.True);
    }

    // ---- REQ-114/ADR-0035: home-nation seeding --------------------------

    [Test]
    public async Task REQ114_SeedAsync_PopulatesAllFourHomeNations_WithUsesCountryForSportPropertyTrue()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        // Q21/Q22/Q25/Q26 are training-knowledge QIDs, NOT independently
        // verified against live Wikidata pages this session — see this
        // file's own doc comment for why that caveat matters and what to do
        // if any turns out wrong.
        var homeNations = new (string Name, string WikidataQid)[]
        {
            ("England", "Q21"),
            ("Scotland", "Q22"),
            ("Wales", "Q25"),
            ("Northern Ireland", "Q26"),
        };

        foreach (var (name, wikidataQid) in homeNations)
        {
            var country = await _dbContext.CountryDefinitions.SingleAsync(c => c.Name == name);
            Assert.That(country.WikidataQid, Is.EqualTo(wikidataQid), $"{name}'s seeded QID");
            Assert.That(country.UsesCountryForSportProperty, Is.True, $"{name} must be flagged for the P1532 query path");
        }
    }

    [Test]
    public async Task REQ114_SeedAsync_OrdinaryCountries_UsesCountryForSportPropertyIsFalse()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        // Every one of the original 45 sovereign-state countries must stay
        // on the default P27 query path — this feature must never widen
        // beyond exactly the four home nations without a deliberate code
        // change.
        var ordinaryCountries = await _dbContext.CountryDefinitions
            .Where(c => c.Name != "England" && c.Name != "Scotland" && c.Name != "Wales" && c.Name != "Northern Ireland")
            .ToListAsync();
        Assert.That(ordinaryCountries, Has.Count.EqualTo(45));
        Assert.That(ordinaryCountries, Has.All.Matches<CountryDefinition>(c => c.UsesCountryForSportProperty == false));
    }

    [Test]
    public async Task REQ114_SeedAsync_RunTwice_NationalTeamsAreIdempotent_CreatesNoDuplicateRows()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        Assert.That(await _dbContext.CountryDefinitions.CountAsync(c => c.Name == "England"), Is.EqualTo(1));
        Assert.That(await _dbContext.CountryDefinitions.CountAsync(), Is.EqualTo(49));
    }

    [Test]
    public async Task REQ114_SeedAsync_CorrectsExistingNationalTeamRow_WhenSeededQidOrFlagHasChanged()
    {
        // Same S-037-style correction-in-place proof as
        // REQ109_SeedAsync_CorrectsExistingRow_WhenSeededQidHasChanged above
        // — a stale row (wrong QID, and/or seeded before this flag existed
        // so still false) must be corrected in place, not left stale and
        // not duplicated.
        _dbContext.CountryDefinitions.Add(new CountryDefinition
        {
            Id = Guid.NewGuid(), Name = "England", WikidataQid = "Qstale", UsesCountryForSportProperty = false,
        });
        await _dbContext.SaveChangesAsync();

        await ReferenceDataSeeder.SeedAsync(_dbContext);

        var england = await _dbContext.CountryDefinitions.AsNoTracking().SingleAsync(c => c.Name == "England");
        Assert.That(england.WikidataQid, Is.EqualTo("Q21"));
        Assert.That(england.UsesCountryForSportProperty, Is.True);
        Assert.That(await _dbContext.CountryDefinitions.CountAsync(c => c.Name == "England"), Is.EqualTo(1));
    }

    // S-037: the actual bug that motivated this — SeedAsync used to only
    // ever add a missing row, never correct an existing one, so fixing a
    // wrong QID in this file alone would have silently done nothing against
    // an already-seeded database (exactly Napoli/AS Roma/Sevilla/Porto's
    // situation after S-036's deploy). This proves an existing row with a
    // stale QID actually gets corrected, not skipped, by the same by-Name
    // idempotency check that prevents duplicates.
    [Test]
    public async Task REQ109_SeedAsync_CorrectsExistingRow_WhenSeededQidHasChanged()
    {
        _dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Napoli", WikidataQid = "Q1176" });
        await _dbContext.SaveChangesAsync();

        await ReferenceDataSeeder.SeedAsync(_dbContext);

        var napoli = await _dbContext.ClubDefinitions.AsNoTracking().SingleAsync(c => c.Name == "Napoli");
        Assert.That(napoli.WikidataQid, Is.EqualTo("Q2641"), "the corrected QID (S-037) must overwrite the stale one, not coexist with a skipped duplicate");
        Assert.That(await _dbContext.ClubDefinitions.CountAsync(c => c.Name == "Napoli"), Is.EqualTo(1), "correcting a row must never create a second row for the same name");
    }

    [Test]
    public async Task REQ109_SeedAsync_CorrectedClubQids_MatchTheirLiveVerifiedValues()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        // S-037: the 4 club QIDs manual verification against live Wikidata
        // pages found wrong in S-036 — asserted individually (not just via
        // the aggregate "no empty QID" check above) so a future accidental
        // revert of just one of these four is caught here specifically.
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Name == "Napoli" && c.WikidataQid == "Q2641"), Is.True);
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Name == "AS Roma" && c.WikidataQid == "Q2739"), Is.True);
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Name == "Sevilla" && c.WikidataQid == "Q10329"), Is.True);
        Assert.That(await _dbContext.ClubDefinitions.AnyAsync(c => c.Name == "Porto" && c.WikidataQid == "Q128446"), Is.True);
    }

    // ---- S-031/REQ-108: Trophy seeding ----------------------------------

    [Test]
    public async Task REQ108_SeedAsync_PopulatesExactlyOneTrophy_BallonDor_AsIndividualAward()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        // v1 seeds exactly one trophy (REQ-108's narrower S-031 scope) —
        // hardcoded, not read back from ReferenceDataSeeder itself, same
        // "catch a future accidental change" reasoning as the Country/Club
        // counts above.
        Assert.That(await _dbContext.TrophyDefinitions.CountAsync(), Is.EqualTo(1));
        var ballonDor = await _dbContext.TrophyDefinitions.SingleAsync(t => t.Name == "Ballon d'Or");
        // Q166177 is a training-knowledge QID, not independently verified
        // against a live Wikidata page this session — see this file's own
        // doc comment for why that caveat matters and what to do if it
        // turns out wrong.
        Assert.That(ballonDor.WikidataQid, Is.EqualTo("Q166177"));
        Assert.That(ballonDor.IsTeamTrophy, Is.False, "REQ-108/S-031: v1 is individual awards only");
    }

    [Test]
    public async Task REQ108_SeedAsync_RunTwice_TrophiesAreIdempotent_CreatesNoDuplicateRows()
    {
        await ReferenceDataSeeder.SeedAsync(_dbContext);
        await ReferenceDataSeeder.SeedAsync(_dbContext);

        Assert.That(await _dbContext.TrophyDefinitions.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ108_SeedAsync_CorrectsExistingTrophyRow_WhenSeededQidHasChanged()
    {
        // Same S-037-style correction-in-place proof as
        // REQ109_SeedAsync_CorrectsExistingRow_WhenSeededQidHasChanged above,
        // for TrophyDefinition's own by-Name upsert.
        _dbContext.TrophyDefinitions.Add(new TrophyDefinition { Id = Guid.NewGuid(), Name = "Ballon d'Or", WikidataQid = "Qstale", IsTeamTrophy = true });
        await _dbContext.SaveChangesAsync();

        await ReferenceDataSeeder.SeedAsync(_dbContext);

        var ballonDor = await _dbContext.TrophyDefinitions.AsNoTracking().SingleAsync(t => t.Name == "Ballon d'Or");
        Assert.That(ballonDor.WikidataQid, Is.EqualTo("Q166177"));
        Assert.That(ballonDor.IsTeamTrophy, Is.False);
        Assert.That(await _dbContext.TrophyDefinitions.CountAsync(t => t.Name == "Ballon d'Or"), Is.EqualTo(1));
    }
}
