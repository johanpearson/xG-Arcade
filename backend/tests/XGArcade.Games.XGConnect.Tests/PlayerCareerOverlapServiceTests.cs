using Microsoft.EntityFrameworkCore;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Games.XGConnect.Tests;

// REQ-1404 (docs/requirements-document.md §4.15)/S-211: the shared
// career-overlap check in isolation, independent of
// ConnectTargetPickService's own orchestration (covered separately in
// ConnectTargetPickServiceTests.cs). Same real-InMemory-repository-plus-fake
// pattern as XGArcade.DataSync.Tests.Wikidata.PlayerCareerStintRefreshServiceTests
// (ADR-0054, docs/coding-guidelines.md "don't over-mock") — as of the
// 2026-09-02 architecture-review follow-up, PlayerCareerOverlapService
// delegates its refresh to IPlayerCareerStintRefreshService rather than
// IWikidataClient directly, so this fixture uses
// FakePlayerCareerStintRefreshService instead of the pre-refactor
// FakeWikidataClient.
public class PlayerCareerOverlapServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private FakePlayerCareerStintRefreshService _careerStintRefreshService = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerCareerStintRepository = new PlayerCareerStintRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _careerStintRefreshService = new FakePlayerCareerStintRefreshService(_playerCareerStintRepository);
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerCareerOverlapService BuildService() =>
        new(_playerCareerStintRepository, _careerStintRefreshService);

    private async Task<Player> SeedPlayerAsync(string? wikidataQid = null) =>
        await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {Guid.NewGuid():N}", WikidataQid = wikidataQid });

    // ---- Overlap-detection logic itself ------------------------------------

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_SharedClubOverlappingYears_ReturnsTrue()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2003, EndYear = 2010 }]);

        var overlaps = await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.True);
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_SharedClubNonOverlappingYears_ReturnsFalse()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2003 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2010, EndYear = 2015 }]);

        var overlaps = await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.False);
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_DifferentClubs_ReturnsFalse()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Chelsea", StartYear = 1999, EndYear = 2007 }]);

        var overlaps = await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.False);
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_OngoingStintOverlapsWithLaterStint_ReturnsTrue()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        // Player A's stint is still ongoing (EndYear null) as of the data
        // snapshot — must still be treated as overlapping a later, bounded
        // stint at the same club, not as "unknown"/excluded.
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 2015, EndYear = null }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2018, EndYear = 2020 }]);

        var overlaps = await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.True);
    }

    // ---- Bug fix (2026-09-04, ADR-0105): always refresh both players,
    // ---- never skip based on "already has some cached rows" ---------------
    // Real, reported incident this closes: Reece James already had a
    // Chelsea-only PlayerCareerStint row from an earlier chain step (a
    // narrow, single-club result), so the old "any rows = trusted" gate
    // permanently hid his genuinely real Wigan Athletic loan — shared with
    // Jonas Olsson — from ever being discovered. Same bug shape ADR-0054
    // already found and fixed once for xG Path (Timothy Weah missing real
    // Juventus/Marseille stints); this class now follows that same
    // "always refresh unconditionally" precedent.

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_AlwaysRefreshesBothPlayers_EvenWhenBothAlreadyHaveCachedStints()
    {
        var playerA = await SeedPlayerAsync("Q1");
        var playerB = await SeedPlayerAsync("Q2");
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Chelsea", StartYear = 1999, EndYear = 2007 }]);

        await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id);

        Assert.That(_careerStintRefreshService.Calls, Has.Count.EqualTo(1));
        Assert.That(_careerStintRefreshService.Calls[0], Is.EquivalentTo(new[] { playerA.Id, playerB.Id }),
            "both players are always refreshed in one batched call, never skipped just because they already have some cached rows");
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_ExistingCacheIsNarrow_RefreshDiscoversTheGenuinelySharedClub()
    {
        var playerA = await SeedPlayerAsync("Q1");
        var playerB = await SeedPlayerAsync("Q2");
        // Both players already have SOME cached data (mirrors the real
        // incident: a narrow, single-club row from an earlier, unrelated
        // lookup) — under the old "any rows = trusted" behavior, this alone
        // would have permanently hidden the real shared club below.
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Chelsea", StartYear = 2012, EndYear = 2019 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Chelsea", StartYear = 2018, EndYear = 2023 }]);
        // The genuinely shared club a fresh refresh discovers — the
        // Azpilicueta/James-shaped "already matched at Chelsea" case,
        // followed by the real Wigan Athletic loan overlap this bug hid.
        _careerStintRefreshService.SetCareerStints(playerA.Id,
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Wigan Athletic", StartYear = 2019, EndYear = 2019 });
        _careerStintRefreshService.SetCareerStints(playerB.Id,
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Wigan Athletic", StartYear = 2019, EndYear = 2019 });

        var overlaps = await BuildService().GetSharedClubOverlapsAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps.Select(o => o.ClubName), Does.Contain("Wigan Athletic"),
            "existing narrow cached data must never block discovering a real, additional shared club via refresh");
    }

    [Test]
    public async Task REQ1404_HaveSharedClubOverlapAsync_WikidataQueryExceptionOnLiveLookup_ThrowsLiveLookupUnavailableException()
    {
        var playerA = await SeedPlayerAsync("Q1"); // Zero cached rows.
        var playerB = await SeedPlayerAsync("Q2"); // Zero cached rows.
        _careerStintRefreshService.FailNextBatches(1);

        Assert.ThrowsAsync<LiveLookupUnavailableException>(
            async () => await BuildService().HaveSharedClubOverlapAsync(playerA.Id, playerB.Id),
            "a Wikidata technical failure must surface as LiveLookupUnavailableException — genuinely unknown, never swallowed to false/true");
    }

    // ---- REQ-1406 (design change, 2026-09-04, ADR-0104): GetSharedClubOverlapsAsync
    // ---- — same interval-overlap math as HaveSharedClubOverlapAsync above
    // ---- (that method is now a thin wrapper over this one), but returns
    // ---- every shared club with its actual intersected year range instead
    // ---- of a single boolean. Supersedes the former HaveOverlapAtClubAsync
    // ---- (playerAId, playerBId, clubName), which required the CALLER to
    // ---- already know and type a specific club name — the direct cause of
    // ---- a real false-rejection bug (a genuinely correct "Chelsea FC" claim
    // ---- string-mismatched the canonically-stored "Chelsea"). With no
    // ---- player-typed club name reaching this service at all anymore, that
    // ---- entire bug class no longer applies here. ---------------------------

    [Test]
    public async Task REQ1406_GetSharedClubOverlapsAsync_OverlappingTimeAtSharedClub_ReturnsThatClubWithIntersectedYears()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2003, EndYear = 2010 }]);

        var overlaps = await BuildService().GetSharedClubOverlapsAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Has.Count.EqualTo(1));
        Assert.That(overlaps[0].ClubName, Is.EqualTo("Arsenal"));
        Assert.That(overlaps[0].OverlapStartYear, Is.EqualTo(2003), "the later of the two start years");
        Assert.That(overlaps[0].OverlapEndYear, Is.EqualTo(2007), "the earlier of the two end years");
    }

    [Test]
    public async Task REQ1406_GetSharedClubOverlapsAsync_BothStintsOngoing_ReturnsNullOverlapEndYear()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 2015, EndYear = null }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2018, EndYear = null }]);

        var overlaps = await BuildService().GetSharedClubOverlapsAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Has.Count.EqualTo(1));
        Assert.That(overlaps[0].OverlapStartYear, Is.EqualTo(2018));
        Assert.That(overlaps[0].OverlapEndYear, Is.Null, "both stints are still ongoing — the overlap has no known end yet");
    }

    [Test]
    public async Task REQ1406_GetSharedClubOverlapsAsync_OneStintOngoing_UsesTheOtherStintsEndYear()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 2015, EndYear = null }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2018, EndYear = 2020 }]);

        var overlaps = await BuildService().GetSharedClubOverlapsAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Has.Count.EqualTo(1));
        Assert.That(overlaps[0].OverlapEndYear, Is.EqualTo(2020), "the bounded stint's own end year, since the other side is still ongoing");
    }

    [Test]
    public async Task REQ1406_GetSharedClubOverlapsAsync_NonOverlappingPeriodAtSameClub_ReturnsEmpty()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2003 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2010, EndYear = 2015 }]);

        var overlaps = await BuildService().GetSharedClubOverlapsAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.Empty);
    }

    [Test]
    public async Task REQ1406_GetSharedClubOverlapsAsync_DifferentClubsOnly_ReturnsEmpty()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Chelsea", StartYear = 1999, EndYear = 2007 }]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Arsenal", StartYear = 2003, EndYear = 2010 }]);

        var overlaps = await BuildService().GetSharedClubOverlapsAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps, Is.Empty, "neither player has any stint at a shared club at all");
    }

    // The real scenario that motivated this design: two players who shared
    // more than one club (e.g. Maxwell and Zlatan Ibrahimović — Inter,
    // Barcelona, PSG) must get an entry for EACH genuinely-overlapping
    // club, not just the first one found.
    [Test]
    public async Task REQ1406_GetSharedClubOverlapsAsync_MultipleSharedClubs_ReturnsOneEntryPerGenuinelyOverlappingClub()
    {
        var playerA = await SeedPlayerAsync();
        var playerB = await SeedPlayerAsync();
        await _playerCareerStintRepository.AddCareerStintsAsync(playerA.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Inter", StartYear = 2009, EndYear = 2011 },
            // Deliberately non-overlapping at Barcelona (A: 2011-2012, B: 2008-2010) — proves this club is correctly excluded, not just "any stint at a shared club name."
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Barcelona", StartYear = 2011, EndYear = 2012 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerA.Id, ClubName = "Paris Saint-Germain", StartYear = 2012, EndYear = 2017 },
        ]);
        await _playerCareerStintRepository.AddCareerStintsAsync(playerB.Id,
        [
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Inter", StartYear = 2009, EndYear = 2010 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Barcelona", StartYear = 2008, EndYear = 2010 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = playerB.Id, ClubName = "Paris Saint-Germain", StartYear = 2012, EndYear = 2016 },
        ]);

        var overlaps = await BuildService().GetSharedClubOverlapsAsync(playerA.Id, playerB.Id);

        Assert.That(overlaps.Select(o => o.ClubName), Is.EquivalentTo(new[] { "Inter", "Paris Saint-Germain" }));
    }
}
