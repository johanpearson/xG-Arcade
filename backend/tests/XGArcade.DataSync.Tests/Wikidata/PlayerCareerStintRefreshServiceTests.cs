using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.DataSync.Tests.Wikidata;

// ADR-0054: same real-InMemory-repository-plus-FakeWikidataClient pattern as
// PlayerPhotoBackfillServiceTests (docs/coding-guidelines.md "don't
// over-mock").
public class PlayerCareerStintRefreshServiceTests
{
    private XGArcadeDbContext _dbContext = null!;
    // S-106/S-107 (pure refactor): AddPlayerAsync lives on IPlayerRepository;
    // GetCareerStintsByPlayerIdsAsync/AddCareerStintsBatchAsync/
    // GetCareerStintCandidatePlayerIdsAsync live on
    // IPlayerCareerStintRepository — see ADR-0067 for the full split of the
    // original, now-deleted IPlayerStoreRepository.
    private IPlayerCareerStintRepository _playerCareerStintRepository = null!;
    private IPlayerRepository _playerRepository = null!;
    private ICategoryValueRepository _categoryValueRepository = null!;
    private FakeWikidataClient _wikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _playerCareerStintRepository = new PlayerCareerStintRepository(_dbContext);
        _playerRepository = new PlayerRepository(_dbContext);
        _categoryValueRepository = new CategoryValueRepository(_dbContext);
        _wikidataClient = new FakeWikidataClient();
    }

    [TearDown]
    public void TearDown() => _dbContext.Dispose();

    private PlayerCareerStintRefreshService BuildService() =>
        new(_wikidataClient, _playerCareerStintRepository, _playerRepository, _categoryValueRepository, NullLogger<PlayerCareerStintRefreshService>.Instance);

    private async Task<Player> SeedPlayerAsync(string wikidataQid) =>
        await _playerRepository.AddPlayerAsync(
            new Player { Id = Guid.NewGuid(), FullName = $"Player {wikidataQid}", WikidataQid = wikidataQid });

    private async Task<ClubDefinition> SeedClubAsync(string name, string wikidataQid)
    {
        var club = new ClubDefinition { Id = Guid.NewGuid(), Name = name, WikidataQid = wikidataQid };
        await _categoryValueRepository.AddClubAsync(club);
        return club;
    }

    [Test]
    public async Task RefreshCareerStintsAsync_PlayerWithNoExistingStints_PersistsEveryFetchedStint()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Monaco", 1994, 1999, 105),
            new WikidataCareerStintEntry("Juventus", 1999, 1999, 16),
            new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Monaco", "Juventus", "Arsenal" }));
    }

    // The whole point of ADR-0054: a club xG Grid's own byproduct queries
    // never happened to discover (not in ClubDefinition, or simply never
    // queried yet) still gets picked up by the full-career fetch.
    [Test]
    public async Task RefreshCareerStintsAsync_ClubNotInAnyPriorByproductData_IsStillPersisted()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Celtic", 2007, 2008, 31));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Does.Contain("Celtic"));
    }

    [Test]
    public async Task RefreshCareerStintsAsync_StintAlreadyPersisted_IsNotDuplicated()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 }]);

        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1), "an already-stored, identical stint must not be duplicated");
    }

    [Test]
    public async Task RefreshCareerStintsAsync_PlayerWithNoWikidataQid_IsNeverQueried()
    {
        var player = await _playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "No QID Player" });

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        Assert.That(_wikidataClient.QueriedCareerStintBatches, Is.Empty);
    }

    [Test]
    public async Task RefreshCareerStintsAsync_EmptyPlayerIdList_DoesNothing()
    {
        await BuildService().RefreshCareerStintsAsync([]);

        Assert.That(_wikidataClient.QueriedCareerStintBatches, Is.Empty);
    }

    // ADR-0054's core safety property: a Wikidata failure here must never
    // propagate — it would fail the whole xG Path round-generation call it's
    // invoked from (XGPathGameModule.GenerateInstanceAsync), which REQ-103's
    // "never block generation on a Wikidata failure" reasoning forbids.
    [Test]
    public async Task RefreshCareerStintsAsync_WikidataQueryFails_DoesNotThrow_ExistingStintsUntouched()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007 }]);

        _wikidataClient.FailNextCareerStintBatches(1);

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshCareerStintsAsync([player.Id]));

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Arsenal" }),
            "a failed refresh must leave whatever data already existed untouched, not wipe it");
    }

    // REQ-1404 (S-211 architecture-review follow-up, 2026-09-02): the
    // throwOnFailure=true opt-in this test proves exists specifically so
    // PlayerCareerOverlapService (Games.XGConnect) can distinguish a
    // genuine Wikidata technical failure from "this player really has no
    // career data" — see throwOnFailure's own doc comment on
    // IPlayerCareerStintRefreshService. Deliberately the mirror image of
    // RefreshCareerStintsAsync_WikidataQueryFails_DoesNotThrow_ExistingStintsUntouched
    // above, which proves the DEFAULT (false) still swallows.
    [Test]
    public async Task RefreshCareerStintsAsync_ThrowOnFailureTrue_WikidataQueryFails_PropagatesWikidataQueryException()
    {
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.FailNextCareerStintBatches(1);

        Assert.ThrowsAsync<WikidataQueryException>(
            async () => await BuildService().RefreshCareerStintsAsync([player.Id], throwOnFailure: true),
            "throwOnFailure: true must let a Wikidata technical failure propagate instead of being logged and swallowed");
    }

    [Test]
    public async Task RefreshCareerStintsAsync_PlayerWithNoWikidataCareerData_PersistsNothing_IsNotTreatedAsAFailure()
    {
        var player = await SeedPlayerAsync("Q1519"); // No SetCareerStints call — genuinely no P54 data.

        Assert.DoesNotThrowAsync(async () => await BuildService().RefreshCareerStintsAsync([player.Id]));

        Assert.That(await _playerCareerStintRepository.GetCareerStintsAsync(player.Id), Is.Empty);
    }

    // Bug fix (2026-08-04, xG Path duplicate-node bug, REQ-1203 follow-up,
    // ADR-0059): the core of the fix — a fetched stint whose ClubQid
    // matches a seeded ClubDefinition must be persisted under the seeded
    // ClubDefinition.Name, not Wikidata's own raw (suffix-normalized only)
    // label, even when that label differs from the seed by more than a
    // legal-suffix token.
    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_FetchedClubQidMatchesSeededClub_PersistsSeededClubDefinitionName()
    {
        await SeedClubAsync("Lyon", "Q704");
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Olympique Lyonnais", 2000, 2003, 90, ClubQid: "Q704"));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Lyon" }),
            "a stint whose ClubQid matches a seeded club must be canonicalized to ClubDefinition.Name");
    }

    // Fallback half of the same fix: a stint at a club with no matching
    // seeded ClubDefinition (either because ClubQid is null, or because it
    // doesn't match any seeded club's WikidataQid) keeps its best-effort,
    // suffix-normalized label — still useful for xG Path's own display and
    // ClubGapAuditService's gap detection.
    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_FetchedClubQidMatchesNoSeededClub_KeepsBestEffortLabel()
    {
        await SeedClubAsync("Lyon", "Q704"); // Seeded, but not the club this stint is at.
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Some Obscure Club", 2000, 2003, 12, ClubQid: "Q999999"));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Some Obscure Club" }));
    }

    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_FetchedStintHasNoClubQid_KeepsBestEffortLabel()
    {
        await SeedClubAsync("Lyon", "Q704");
        var player = await SeedPlayerAsync("Q1519");
        // No ClubQid supplied — defaults to null, same as a defensive
        // ParseCareerStintBindings row with no ?club binding.
        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Lyon Reserve", 2000, 2003, 5));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Lyon Reserve" }));
    }

    // The whole point of this fix: the same real stint, fetched via this
    // class's full-career path with Wikidata's own raw label, must
    // canonicalize to the exact same ClubName a byproduct xG Grid lookup
    // (WikidataLookupService.PersistCareerStintsAsync, which always writes
    // ClubDefinition.Name directly) would have written for the same real
    // club — so the "already persisted" dedup check treats them as the
    // same stint, not two.
    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_CanonicalizedClubName_MatchesGridByproductWriterConvention_DedupesAgainstIt()
    {
        await SeedClubAsync("Lyon", "Q704");
        var player = await SeedPlayerAsync("Q1519");
        // Simulates a stint xG Grid's own country x club intersection
        // lookup already recorded, using ClubDefinition.Name directly
        // (WikidataLookupService.PersistCareerStintsAsync's own writer
        // convention).
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Lyon", StartYear = 2000, EndYear = 2003, AppearanceCount = 90 }]);

        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Olympique Lyonnais", 2000, 2003, 90, ClubQid: "Q704"));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1),
            "the full-career fetch's 'Olympique Lyonnais' row must canonicalize to 'Lyon' and be recognized as the same stint already persisted, not duplicated");
    }

    // The second, "for free" correctness fix this change carries (see this
    // story's own root-cause notes): GetCareerStintCandidatePlayerIdsAsync
    // (XGPathGameModule's hot eligibility path) does an exact,
    // case-sensitive match against seeded ClubDefinition.Name. Before this
    // fix, a stint persisted under a non-canonical Wikidata label (e.g.
    // "Olympique Lyonnais") never matched "Lyon" and so never counted
    // toward eligibility, even though the player genuinely played for a
    // seeded club. This proves the canonicalized ClubName now satisfies
    // that exact-match check.
    [Test]
    public async Task REQ1203_RefreshCareerStintsAsync_CanonicalizedClubName_SatisfiesCareerStintCandidateEligibilityMatch()
    {
        var club = await SeedClubAsync("Lyon", "Q704");
        var player = await SeedPlayerAsync("Q1519");
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Olympique Lyonnais", 2000, 2003, 90, ClubQid: "Q704"),
            new WikidataCareerStintEntry("Juventus", 2003, 2006, 60),
            new WikidataCareerStintEntry("Arsenal", 2006, 2009, 40));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var seededClubNames = new HashSet<string> { club.Name };
        // minSeededClubCount: 1 — this test is only proving the exact-match/
        // canonicalization behavior, not REQ-1201/ADR-0074/S-138's own
        // "≥2 distinct seeded clubs" threshold, and only one seeded club is
        // registered here. minTotalStintCount: 3 — satisfied by this
        // fixture's 3 real stints (Lyon/Juventus/Arsenal), REQ-1203's own
        // unrelated floor.
        var candidateIds = await _playerCareerStintRepository.GetCareerStintCandidatePlayerIdsAsync(seededClubNames, minTotalStintCount: 3, minSeededClubCount: 1);

        Assert.That(candidateIds, Does.Contain(player.Id),
            "a stint originally labeled 'Olympique Lyonnais' must canonicalize to the seeded 'Lyon' and satisfy exact-match eligibility");
    }

    // ---- S-187/REQ-1203: BuildNewStintsByPlayerId's narrowed (ClubName,
    // StartYear) matching key ------------------------------------------------
    // Problem this closes: a stored "ongoing" stint (EndYear null) that
    // Wikidata later completes with a real end date — the player transferred
    // away — previously failed the OLD full-tuple match (stored EndYear null
    // != fetched EndYear) and was inserted as a SECOND row, a duplicate-
    // looking entry in xG Path's clue-reveal timeline for one real stint.

    [Test]
    public async Task REQ1203_S187_RefreshCareerStintsAsync_FetchedStintCompletesStoredOngoingStint_UpdatesInPlace_NotDuplicated()
    {
        var player = await SeedPlayerAsync("Q1519");
        var existingStintId = Guid.NewGuid();
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = existingStintId, PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = null, AppearanceCount = null }]);

        // Wikidata now records the transfer away that hadn't happened yet
        // the last time this stint was fetched.
        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1), "completing an ongoing stint's end date must update the existing row, not duplicate it");
        Assert.That(stints[0].Id, Is.EqualTo(existingStintId), "the SAME row must be updated in place, not a new one");
        Assert.That(stints[0].EndYear, Is.EqualTo(2007));
        Assert.That(stints[0].AppearanceCount, Is.EqualTo(254));
    }

    [Test]
    public async Task REQ1203_S187_RefreshCareerStintsAsync_FetchedStintAtGenuinelyDifferentClub_StillInsertsNewRow()
    {
        var player = await SeedPlayerAsync("Q1519");
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Monaco", StartYear = 1994, EndYear = 1999, AppearanceCount = 105 }]);

        // Monaco is unchanged; Arsenal is a genuinely new, later stint —
        // narrowing the match key must not make this look like a completion.
        _wikidataClient.SetCareerStints("Q1519",
            new WikidataCareerStintEntry("Monaco", 1994, 1999, 105),
            new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints.Select(s => s.ClubName), Is.EquivalentTo(new[] { "Monaco", "Arsenal" }),
            "a genuinely new club/start-year combination must still be inserted as a new row");
    }

    [Test]
    public async Task REQ1203_S187_RefreshCareerStintsAsync_FetchedStintIdenticalToStored_RemainsANoOp()
    {
        var player = await SeedPlayerAsync("Q1519");
        var existingStintId = Guid.NewGuid();
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = existingStintId, PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254 }]);

        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1), "an identical re-fetch must remain a no-op — no completion, no new row");
        Assert.That(stints[0].Id, Is.EqualTo(existingStintId));
        Assert.That(stints[0].EndYear, Is.EqualTo(2007));
        Assert.That(stints[0].AppearanceCount, Is.EqualTo(254));
    }

    [Test]
    public async Task REQ1203_S187_RefreshCareerStintsAsync_FetchedStintCompletesAppearanceCountOnly_UpdatesInPlace()
    {
        // Same narrow completion path, just for AppearanceCount alone
        // (EndYear already matches) — not the story's headline scenario, but
        // the same (ClubName, StartYear) match key drives both fields.
        var player = await SeedPlayerAsync("Q1519");
        var existingStintId = Guid.NewGuid();
        await _playerCareerStintRepository.AddCareerStintsAsync(player.Id,
            [new PlayerCareerStint { Id = existingStintId, PlayerId = player.Id, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = null }]);

        _wikidataClient.SetCareerStints("Q1519", new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254));

        await BuildService().RefreshCareerStintsAsync([player.Id]);

        var stints = await _playerCareerStintRepository.GetCareerStintsAsync(player.Id);
        Assert.That(stints, Has.Count.EqualTo(1));
        Assert.That(stints[0].Id, Is.EqualTo(existingStintId));
        Assert.That(stints[0].AppearanceCount, Is.EqualTo(254));
    }

    // S-187 follow-up (REQ-1203, 2026-08-29, quality-architect finding —
    // "add a direct no-op proof test"): a direct unit test on
    // BuildNewStintsByPlayerId itself (not routed through
    // RefreshCareerStintsAsync's full repository round trip) proving an
    // identical re-fetch is a TRUE no-op — both output collections empty —
    // so no repository write would even be attempted, not just "the end
    // state looks unchanged after a write" (which
    // REQ1203_S187_RefreshCareerStintsAsync_FetchedStintIdenticalToStored_RemainsANoOp
    // above already proves the end-to-end version of). Requires
    // XGArcade.DataSync's AssemblyInfo.cs InternalsVisibleTo grant — added
    // alongside this test, since BuildNewStintsByPlayerId is internal and no
    // test previously called it directly.
    [Test]
    public void REQ1203_BuildNewStintsByPlayerId_IdenticalRefetchInput_ReturnsTrueNoOp()
    {
        const string qid = "Q1519";
        var playerId = Guid.NewGuid();
        var existingStintId = Guid.NewGuid();

        var stintsByQid = new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>
        {
            [qid] = [new WikidataCareerStintEntry("Arsenal", 1999, 2007, 254)],
        };
        var qidToPlayerId = new Dictionary<string, Guid> { [qid] = playerId };
        var existingStintsByPlayerId = new Dictionary<Guid, IReadOnlyList<PlayerCareerStint>>
        {
            [playerId] = [new PlayerCareerStint
            {
                Id = existingStintId, PlayerId = playerId, ClubName = "Arsenal", StartYear = 1999, EndYear = 2007, AppearanceCount = 254,
            }],
        };
        IReadOnlyDictionary<string, string> clubNameByClubQid = new Dictionary<string, string>();

        var reconciliation = PlayerCareerStintRefreshService.BuildNewStintsByPlayerId(
            stintsByQid, qidToPlayerId, existingStintsByPlayerId, clubNameByClubQid);

        Assert.That(reconciliation.NewStintsByPlayerId, Is.Empty, "an identical re-fetch must queue zero new-row inserts");
        Assert.That(reconciliation.CompletionsByStintId, Is.Empty, "an identical re-fetch must queue zero completions — a true no-op, no write attempted");
    }
}
