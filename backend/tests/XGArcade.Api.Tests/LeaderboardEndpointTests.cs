using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Leagues;
using XGArcade.Core.Scoring;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Tests;

// REQ-607/S-034 (docs/backlog.md): API-level coverage for
// GET /leagues/global/leaderboard, added alongside pagination. Every other
// endpoint has a file here (GuessEndpointTests, RoundEndpointTests,
// GridEndpointTests, AdminEndpointTests, CurrentRoundEndpointTests,
// AuthEndpointTests) but this one had none before this change —
// implementation-document.md §7 explicitly names GET /leaderboard as an
// API-test-level example. The Core-level LeaderboardService scenarios
// (sorting, capping, cursor slicing, off-page requesting-user row, cursor
// beyond membership) are already covered by
// XGArcade.Core.Tests/Leagues/LeaderboardServiceTests.cs and are
// deliberately NOT re-proven here — this file only covers what only the
// real HTTP pipeline can prove: query-string binding/validation and JSON
// response shape round-tripping. Same WebApplicationFactory<Program> +
// in-memory-DbContext-swap pattern as GuessEndpointTests.
//
// REQ-406/407/408 (2026-07-19, ADR-0031/backlog S-053/S-054) added three
// more routes under /leagues/global/leaderboard/* — same "only what the real
// HTTP pipeline proves" split applies: the live-contribution formula's three
// cell cases and the closed-round total/ordering are already covered by
// LeaderboardServiceTests; this file covers routing, the "no active round"
// 404, and the not-found/not-closed-yet 404/409 distinction for REQ-408.
public class LeaderboardEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // See GuessEndpointTests' SetUp comment: local-e2e auth mode
                // avoids any live-network JWKS dependency in this test host.
                builder.UseSetting("Auth:Mode", "local-e2e");

                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as every other
                    // XGArcade.Api.Tests file — see AuthEndpointTests'
                    // SetUp comment for why every XGArcadeDbContext-closed
                    // descriptor must be removed, not just the two obvious
                    // ones.
                    var xgArcadeDbContextDescriptors = services
                        .Where(d => d.ServiceType == typeof(XGArcadeDbContext)
                            || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(XGArcadeDbContext))))
                        .ToList();
                    foreach (var descriptor in xgArcadeDbContextDescriptors)
                    {
                        services.Remove(descriptor);
                    }

                    var inMemoryDatabaseName = Guid.NewGuid().ToString();
                    services.AddDbContext<XGArcadeDbContext>(options =>
                        options.UseInMemoryDatabase(inMemoryDatabaseName));
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    // ---- Seeding helpers ----------------------------------------------

    // Creates a User and enrolls them in the global league, mirroring what
    // the real signup flow does (REQ-401) without going through it — same
    // shape as LeaderboardServiceTests' SeedMemberAsync, just via a fresh
    // DbContext scope off the test host instead of a directly-constructed
    // repository.
    private async Task<Guid> SeedMemberAsync(Guid authProviderUserId, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = $"{authProviderUserId}@example.com",
            DisplayName = displayName,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var globalLeague = await dbContext.Leagues.SingleOrDefaultAsync(l => l.Type == LeagueTypes.Global);
        if (globalLeague is null)
        {
            globalLeague = new League { Id = Guid.NewGuid(), Name = "Global", Type = LeagueTypes.Global };
            dbContext.Leagues.Add(globalLeague);
            await dbContext.SaveChangesAsync();
        }

        dbContext.LeagueMemberships.Add(new LeagueMembership { LeagueId = globalLeague.Id, UserId = user.Id });
        await dbContext.SaveChangesAsync();

        return user.Id;
    }

    private async Task SeedLockedGuessAsync(Guid userId, int finalPoints)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = Guid.NewGuid(),
            UserId = userId,
            CellId = Guid.NewGuid(),
            SubmittedName = "Someone",
            IsCorrect = true,
            AttemptCount = 1,
            FinalUniquenessScore = finalPoints / 100.0,
            FinalPoints = finalPoints,
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    // REQ-406/407: a real GridInstance/GridCell pair backing an active
    // Round's GameInstanceId — GridGameModule.GetCellIdsAsync (the real,
    // registered IGameModule in this WebApplicationFactory host) reads
    // GridInstance directly, so the live-contribution path throws if no
    // instance exists for the round's GameInstanceId. Same
    // seed-a-real-GridInstance pattern CurrentRoundEndpointTests'
    // SeedRoundWithCellsAsync already establishes.
    private async Task<(Guid RoundId, Guid CellId)> SeedActiveRoundWithOneCellAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var instanceId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        dbContext.GridInstances.Add(new GridInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Cells =
            [
                new GridCell
                {
                    Id = cellId,
                    GridInstanceId = instanceId,
                    Row = 0,
                    Col = 0,
                    RowCategoryType = CategoryPairingRules.Country,
                    RowCategoryValue = "France",
                    ColCategoryType = CategoryPairingRules.Club,
                    ColCategoryValue = "Arsenal",
                },
            ],
        });

        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GridGameModule.XGGridGameKey,
            GameInstanceId = instanceId,
            StartTime = DateTime.UtcNow.AddHours(-1),
            EndTime = DateTime.UtcNow.AddHours(1),
            AllowGuessChange = true,
        };
        dbContext.Rounds.Add(round);

        await dbContext.SaveChangesAsync();
        return (round.Id, cellId);
    }

    // REQ-408: a closed round with no cells needed — GetClosedRoundLeaderboardAsync
    // never touches IGameModule at all (it's locked-only, REQ-206's
    // SUM(final_points)), so a plain Round row (no backing GridInstance) is
    // enough here, unlike the active-round helper above.
    private async Task<Guid> SeedClosedRoundAsync(DateTime closedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GridGameModule.XGGridGameKey,
            GameInstanceId = Guid.NewGuid(),
            StartTime = closedAt.AddDays(-2),
            EndTime = closedAt,
            AllowGuessChange = true,
            ClosedAt = closedAt,
        };
        dbContext.Rounds.Add(round);
        await dbContext.SaveChangesAsync();
        return round.Id;
    }

    private async Task<Guid> SeedRoundNotYetClosedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GridGameModule.XGGridGameKey,
            GameInstanceId = Guid.NewGuid(),
            StartTime = DateTime.UtcNow.AddHours(-1),
            EndTime = DateTime.UtcNow.AddHours(1),
            AllowGuessChange = true,
        };
        dbContext.Rounds.Add(round);
        await dbContext.SaveChangesAsync();
        return round.Id;
    }

    private async Task SeedGuessAsync(
        Guid roundId, Guid userId, Guid cellId, bool isCorrect, int attemptCount, Guid? playerAnswerId = null, int? finalPoints = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = roundId,
            UserId = userId,
            CellId = cellId,
            SubmittedName = "Someone",
            PlayerAnswerId = playerAnswerId,
            IsCorrect = isCorrect,
            AttemptCount = attemptCount,
            FinalPoints = finalPoints,
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- REQ-607/REQ-606: server-side query-param validation --------------

    [Test]
    public async Task REQ607_LeaderboardGet_NegativeCursor_ReturnsBadRequestWithInvalidCursorTitle()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedMemberAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/leagues/global/leaderboard?cursor=-1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid cursor"));
    }

    [TestCase(0, TestName = "REQ607_LeaderboardGet_PageSizeZero_ReturnsBadRequestWithInvalidPageSizeTitle")]
    [TestCase(101, TestName = "REQ607_LeaderboardGet_PageSizeAboveMax_ReturnsBadRequestWithInvalidPageSizeTitle")]
    public async Task REQ607_LeaderboardGet_PageSizeOutOfRange_ReturnsBadRequestWithInvalidPageSizeTitle(int pageSize)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedMemberAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/leagues/global/leaderboard?pageSize={pageSize}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid pageSize"));
    }

    // ---- REQ-607: happy path, real HTTP pipeline round-trip ----------------

    [Test]
    public async Task REQ607_LeaderboardGet_NoQueryParams_ReturnsOkWithDefaultedPageAndExpectedShape()
    {
        var requestingAuthProviderUserId = Guid.NewGuid();
        var requestingUserId = await SeedMemberAsync(requestingAuthProviderUserId, "You");
        var otherUserId = await SeedMemberAsync(Guid.NewGuid(), "Alex");
        await SeedLockedGuessAsync(requestingUserId, 50);
        await SeedLockedGuessAsync(otherUserId, 90);
        var client = CreateAuthenticatedClient(requestingAuthProviderUserId);

        var response = await client.GetAsync("/leagues/global/leaderboard");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(body, Is.Not.Null);

        // Ascending by TotalPoints (ADR-0021): You (50) ranks before Alex (90).
        Assert.That(body!.Rows, Has.Count.EqualTo(2));
        Assert.That(body.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "You", "Alex" }));
        Assert.That(body.Rows.Select(r => r.TotalPoints), Is.EqualTo(new[] { 50, 90 }));
        Assert.That(body.Rows.Select(r => r.Rank), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(body.Rows[0].UserId, Is.EqualTo(requestingUserId));
        Assert.That(body.Rows[0].IsRequestingUser, Is.True);
        Assert.That(body.Rows[1].IsRequestingUser, Is.False);

        Assert.That(body.RequestingUserRow, Is.Not.Null);
        Assert.That(body.RequestingUserRow!.UserId, Is.EqualTo(requestingUserId));
        Assert.That(body.RequestingUserRow.Rank, Is.EqualTo(1));

        // Both members fit on the default pageSize=50 page, so there's no next page.
        Assert.That(body.HasMore, Is.False);
        Assert.That(body.NextCursor, Is.Null);
    }

    [Test]
    public async Task REQ607_LeaderboardGet_PageSizeSmallerThanMembership_ReturnsCappedPageWithUsableNextCursor()
    {
        var authProviderUserId = Guid.NewGuid();
        var seededUserIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var userId = i == 0
                ? await SeedMemberAsync(authProviderUserId, "Member0")
                : await SeedMemberAsync(Guid.NewGuid(), $"Member{i}");
            seededUserIds.Add(userId);
            await SeedLockedGuessAsync(userId, i * 10);
        }
        var client = CreateAuthenticatedClient(authProviderUserId);

        var firstResponse = await client.GetAsync("/leagues/global/leaderboard?pageSize=2");

        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(firstPage, Is.Not.Null);
        Assert.That(firstPage!.Rows, Has.Count.EqualTo(2), "REQ-607: pageSize must cap the response, never return the full membership");
        Assert.That(firstPage.HasMore, Is.True);
        Assert.That(firstPage.NextCursor, Is.Not.Null);

        // The NextCursor value must actually be usable as a query-string
        // cursor on a follow-up request — proves the query-param binding and
        // JSON round-trip work end-to-end, not just that the service method
        // returns the right in-memory value (already covered at Core level).
        var secondResponse = await client.GetAsync($"/leagues/global/leaderboard?cursor={firstPage.NextCursor}&pageSize=2");

        Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var secondPage = await secondResponse.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(secondPage, Is.Not.Null);
        Assert.That(secondPage!.Rows, Has.Count.EqualTo(1));
        Assert.That(secondPage.HasMore, Is.False);
        Assert.That(secondPage.NextCursor, Is.Null);

        var allUserIdsAcrossPages = firstPage.Rows.Concat(secondPage.Rows).Select(r => r.UserId).ToList();
        Assert.That(allUserIdsAcrossPages, Is.EquivalentTo(seededUserIds), "no overlap or gap across the two pages");
    }

    // ---- REQ-607: pageSize boundary values (min=1, max=MaxPageSize=100) are accepted ----
    // The parameterized bad-request test above pins the invalid boundary
    // (0 and 101 rejected); these pin the valid boundary (1 and 100
    // accepted) precisely, rather than leaving "somewhere between 0/101 and
    // 1/100" unproven.

    [Test]
    public async Task REQ607_LeaderboardGet_PageSizeOne_ReturnsOkWithExactlyOneRowAndHasMoreTrue()
    {
        var authProviderUserId = Guid.NewGuid();
        var firstUserId = await SeedMemberAsync(authProviderUserId, "Member0");
        await SeedLockedGuessAsync(firstUserId, 0);
        var secondUserId = await SeedMemberAsync(Guid.NewGuid(), "Member1");
        await SeedLockedGuessAsync(secondUserId, 10);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/leagues/global/leaderboard?pageSize=1");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Rows, Has.Count.EqualTo(1), "REQ-607: pageSize=1 is the documented minimum and must be honored exactly");
        Assert.That(body.HasMore, Is.True, "a 2-member league with pageSize=1 must report a further page");
        Assert.That(body.NextCursor, Is.Not.Null);
    }

    [Test]
    public async Task REQ607_LeaderboardGet_PageSizeMax_ReturnsOkWithAllMembersAndHasMoreFalse()
    {
        var authProviderUserId = Guid.NewGuid();
        var firstUserId = await SeedMemberAsync(authProviderUserId, "Member0");
        await SeedLockedGuessAsync(firstUserId, 0);
        var secondUserId = await SeedMemberAsync(Guid.NewGuid(), "Member1");
        await SeedLockedGuessAsync(secondUserId, 10);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/leagues/global/leaderboard?pageSize=100");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Rows, Has.Count.EqualTo(2), "REQ-607: pageSize=100 is the documented maximum and must be accepted, not rejected");
        Assert.That(body.HasMore, Is.False, "membership is well under 100, so all rows fit on one page");
        Assert.That(body.NextCursor, Is.Null);
    }

    // ---- REQ-406: active-round live contribution folded into the shared total ----

    [Test]
    public async Task REQ406_LeaderboardGet_ActiveRoundLockedIncorrectCell_AddsMaxPointsPerCellToSharedTotal()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedMemberAsync(authProviderUserId, "You");
        var (roundId, cellId) = await SeedActiveRoundWithOneCellAsync();
        await SeedGuessAsync(roundId, userId, cellId, isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/leagues/global/leaderboard");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(body!.Rows.Single().TotalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell));
    }

    [Test]
    public async Task REQ406_LeaderboardGet_TwoSuccessiveRequestsAfterAnotherPlayersGuessChangesUniqueness_ReflectDifferentTotalsWithNoInvalidationStep()
    {
        // ADR-0031: recomputed fresh on every read — no cache to invalidate.
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedMemberAsync(authProviderUserId, "You");
        var (roundId, cellId) = await SeedActiveRoundWithOneCellAsync();
        var sharedAnswerId = Guid.NewGuid();
        await SeedGuessAsync(roundId, userId, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: sharedAnswerId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var firstResponse = await client.GetAsync("/leagues/global/leaderboard");
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(firstBody!.Rows.Single().TotalPoints, Is.EqualTo(0), "lone correct guesser is 100% unique (ADR-0020)");

        // Another player now shares the same answer on the same cell — with
        // exactly 2 correct guessers total and both sharing one answer,
        // that answer is now fully common (ADR-0020: 1 of 1 other correct
        // guesser shares it), the worst-case uniqueness.
        await SeedGuessAsync(roundId, Guid.NewGuid(), cellId, isCorrect: true, attemptCount: 1, playerAnswerId: sharedAnswerId);

        var secondResponse = await client.GetAsync("/leagues/global/leaderboard");
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(secondBody!.Rows.Single(r => r.UserId == userId).TotalPoints, Is.EqualTo(ScoringRules.MaxPointsPerCell),
            "recomputed fresh on this second read, no explicit invalidation step");
    }

    // ---- REQ-407: standalone active-round leaderboard -----------------------

    [Test]
    public async Task REQ407_ActiveRoundLeaderboardGet_NoActiveRound_ReturnsNotFoundWithNoActiveRoundTitle()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedMemberAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/leagues/global/leaderboard/active-round");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("No active round"));
    }

    [Test]
    public async Task REQ407_ActiveRoundLeaderboardGet_NonParticipantMember_DoesNotAppearAtAll()
    {
        var authProviderUserId = Guid.NewGuid();
        var participantId = await SeedMemberAsync(authProviderUserId, "You");
        await SeedMemberAsync(Guid.NewGuid(), "NeverPlayed");
        var (roundId, cellId) = await SeedActiveRoundWithOneCellAsync();
        await SeedGuessAsync(roundId, participantId, cellId, isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid());
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/leagues/global/leaderboard/active-round");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(body!.Rows, Has.Count.EqualTo(1));
        Assert.That(body.Rows.Single().UserId, Is.EqualTo(participantId));
    }

    // ---- REQ-408: browsable past closed-round leaderboards -------------------

    [Test]
    public async Task REQ408_ClosedRoundsGet_ReturnsClosedRoundsOnlyMostRecentlyClosedFirst()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedMemberAsync(authProviderUserId, "Alex");
        var now = DateTime.UtcNow;
        var earlier = await SeedClosedRoundAsync(now.AddDays(-2));
        var later = await SeedClosedRoundAsync(now.AddDays(-1));
        var stillActive = await SeedRoundNotYetClosedAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/leagues/global/leaderboard/closed-rounds");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ClosedRoundListResponse>();
        Assert.That(body!.Rounds.Select(r => r.RoundId), Is.EqualTo(new[] { later, earlier }));
        Assert.That(body.Rounds.Any(r => r.RoundId == stillActive), Is.False);
    }

    [Test]
    public async Task REQ408_ClosedRoundsGet_PageSizeSmallerThanCount_ReturnsCappedPageWithUsableCursor()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedMemberAsync(authProviderUserId, "Alex");
        var now = DateTime.UtcNow;
        var roundIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
            roundIds.Add(await SeedClosedRoundAsync(now.AddDays(-i - 1)));
        var client = CreateAuthenticatedClient(authProviderUserId);

        var firstResponse = await client.GetAsync("/leagues/global/leaderboard/closed-rounds?pageSize=2");
        var firstPage = await firstResponse.Content.ReadFromJsonAsync<ClosedRoundListResponse>();
        Assert.That(firstPage!.Rounds, Has.Count.EqualTo(2));
        Assert.That(firstPage.HasMore, Is.True);
        Assert.That(firstPage.NextCursor, Is.Not.Null);

        var secondResponse = await client.GetAsync($"/leagues/global/leaderboard/closed-rounds?cursor={firstPage.NextCursor}&pageSize=2");
        var secondPage = await secondResponse.Content.ReadFromJsonAsync<ClosedRoundListResponse>();
        Assert.That(secondPage!.Rounds, Has.Count.EqualTo(1));
        Assert.That(secondPage.HasMore, Is.False);

        var allRoundIds = firstPage.Rounds.Concat(secondPage.Rounds).Select(r => r.RoundId).ToList();
        Assert.That(allRoundIds, Is.EquivalentTo(roundIds));
    }

    [Test]
    public async Task REQ408_ClosedRoundLeaderboardGet_UnknownRoundId_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedMemberAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/leagues/global/leaderboard/closed-rounds/{Guid.NewGuid()}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Round not found"));
    }

    [Test]
    public async Task REQ408_ClosedRoundLeaderboardGet_RoundNotClosedYet_ReturnsConflictDistinctFromNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedMemberAsync(authProviderUserId, "Alex");
        var roundId = await SeedRoundNotYetClosedAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/leagues/global/leaderboard/closed-rounds/{roundId}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Round not closed yet"));
    }

    [Test]
    public async Task REQ408_ClosedRoundLeaderboardGet_ClosedRound_ReturnsLockedTotalsRankedAscending()
    {
        var authProviderUserId = Guid.NewGuid();
        var youId = await SeedMemberAsync(authProviderUserId, "You");
        var alexId = await SeedMemberAsync(Guid.NewGuid(), "Alex");
        var roundId = await SeedClosedRoundAsync(DateTime.UtcNow.AddDays(-1));
        await SeedGuessAsync(roundId, youId, Guid.NewGuid(), isCorrect: true, attemptCount: 1, playerAnswerId: Guid.NewGuid(), finalPoints: 20);
        await SeedGuessAsync(roundId, alexId, Guid.NewGuid(), isCorrect: false, attemptCount: GuessRules.MaxAttemptsPerCell, finalPoints: ScoringRules.MaxPointsPerCell);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/leagues/global/leaderboard/closed-rounds/{roundId}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.That(body!.Rows.Select(r => r.DisplayName), Is.EqualTo(new[] { "You", "Alex" }));
        Assert.That(body.Rows.Select(r => r.TotalPoints), Is.EqualTo(new[] { 20, ScoringRules.MaxPointsPerCell }));
    }
}
