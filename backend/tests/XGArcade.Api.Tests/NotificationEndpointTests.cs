using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Notifications;
using XGArcade.Api.Social;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Tests;

// REQ-1411 (docs/requirements-document.md §4.15, S-216): API-level
// coverage for GET /notifications/summary. Same WebApplicationFactory<Program>
// + in-memory-DbContext-swap + LocalE2EAuth pattern as
// ChallengeEndpointTests/FriendEndpointTests/ConnectChainStepEndpointTests.
// Friend-request and challenge fixtures go through the real HTTP endpoints
// (POST /friends/requests, POST /challenges, and their accept/decline
// counterparts) exactly like FriendEndpointTests/ChallengeEndpointTests
// already do; xG Connect match fixtures go through IConnectMatchRepository
// directly (AddMatchAsync/MarkPlayerBustedAsync/AddChainStepAsync), the
// same "no endpoint reaches this state directly" precedent
// ConnectChainStepEndpointTests already uses for busted/chain-already-
// complete setup — there is no player-facing way to reach a busted, timed-
// out, or closed-chain slot other than a real (network-dependent) live
// overlap check, which this file deliberately avoids per that test file's
// own precedent.
public class NotificationEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Auth:Mode", "local-e2e");

                builder.ConfigureServices(services =>
                {
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

    private async Task<Guid> SeedUserAsync(Guid authProviderUserId, string displayName)
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
        return user.Id;
    }

    private async Task MakeFriendsAsync(Guid userAId, Guid userBId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var (userA, userB) = userAId.CompareTo(userBId) <= 0 ? (userAId, userBId) : (userBId, userAId);
        dbContext.Friendships.Add(new Friendship
        {
            Id = Guid.NewGuid(),
            UserAId = userA,
            UserBId = userB,
            CreatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    // A match with `userId` in one slot and no chain steps for either
    // side yet — the "no target pick / no chain steps in progress" case
    // GetMatchesAwaitingActionAsync's own doc comment says naturally falls
    // through to "included", used as the REQ-1411 category-3 fixture for
    // the combined-presence test below.
    private async Task<Guid> CreateOpenMatchAwaitingActionAsync(Guid userId, Guid otherUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var now = DateTime.UtcNow;
        var match = await connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = userId,
            PlayerBUserId = otherUserId,
            CreatedAt = now,
            Status = ConnectMatchStatus.Active,
            StartedAt = now,
            DeadlineUtc = now.AddHours(6),
        });
        return match.Id;
    }

    // Marks `userId`'s own slot on `matchId` busted — one of the three
    // ways GetMatchesAwaitingActionAsync's own doc comment says a slot
    // reaches terminal (bust, timeout, or ClosesChain). The match itself is
    // deliberately left Active (not Resolved) — REQ-1411's own note is that
    // GetMatchesAwaitingActionAsync only cares about the caller's own slot,
    // not whether the other participant/the match as a whole is terminal.
    private async Task MarkSlotBustedAsync(Guid matchId, Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var match = await connectMatchRepository.GetMatchByIdAsync(matchId);
        var isPlayerA = match!.PlayerAUserId == userId;
        await connectMatchRepository.MarkPlayerBustedAsync(matchId, isPlayerA, DateTime.UtcNow);
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth gating --------------------------------------------------------

    [Test]
    public async Task REQ1411_GetNotificationsSummary_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/notifications/summary");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- Zero baseline -------------------------------------------------------

    [Test]
    public async Task REQ1411_GetNotificationsSummary_NoPendingItemsAtAll_ReturnsAllZeroCountsAndHasPendingFalse()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/notifications/summary");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<NotificationSummaryResponse>();
        Assert.That(body!.PendingFriendRequestCount, Is.EqualTo(0));
        Assert.That(body.PendingChallengeCount, Is.EqualTo(0));
        Assert.That(body.MatchesAwaitingActionCount, Is.EqualTo(0));
        Assert.That(body.HasPending, Is.False);
    }

    // ---- GWT#3: combined presence across more than one category --------------

    [Test]
    public async Task REQ1411_GetNotificationsSummary_ItemsAcrossAllThreeCategories_ReturnsCombinedCountsAndHasPendingTrue()
    {
        var ownerAuthProviderUserId = Guid.NewGuid();
        var ownerId = await SeedUserAsync(ownerAuthProviderUserId, "Owner");
        var friendRequesterAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(friendRequesterAuthProviderUserId, "Requester");
        var challengerAuthProviderUserId = Guid.NewGuid();
        var challengerId = await SeedUserAsync(challengerAuthProviderUserId, "Challenger");
        var matchOpponentAuthProviderUserId = Guid.NewGuid();
        var matchOpponentId = await SeedUserAsync(matchOpponentAuthProviderUserId, "Opponent");

        var ownerClient = CreateAuthenticatedClient(ownerAuthProviderUserId);
        var friendRequesterClient = CreateAuthenticatedClient(friendRequesterAuthProviderUserId);
        var challengerClient = CreateAuthenticatedClient(challengerAuthProviderUserId);

        // Category 1: a pending friend request sent TO the owner.
        await friendRequesterClient.PostAsJsonAsync("/friends/requests", new SendFriendRequestRequest(ownerId));

        // Category 2: a pending challenge sent TO the owner — requires an
        // existing friendship first, per REQ-1402.
        await MakeFriendsAsync(ownerId, challengerId);
        await challengerClient.PostAsJsonAsync("/challenges", new SendChallengeRequest(ownerId));

        // Category 3: an open match with the owner's own slot not yet
        // terminal.
        await CreateOpenMatchAwaitingActionAsync(ownerId, matchOpponentId);

        var response = await ownerClient.GetAsync("/notifications/summary");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<NotificationSummaryResponse>();
        Assert.That(body!.PendingFriendRequestCount, Is.EqualTo(1));
        Assert.That(body.PendingChallengeCount, Is.EqualTo(1));
        Assert.That(body.MatchesAwaitingActionCount, Is.EqualTo(1));
        Assert.That(body.HasPending, Is.True,
            "a player with items in more than one category must see combined presence, not just one category represented");
    }

    // ---- GWT#5: zero once every contributing item has resolved ---------------

    [Test]
    public async Task REQ1411_GetNotificationsSummary_EveryContributingItemResolved_ReturnsAllZeroCountsAndHasPendingFalse()
    {
        var ownerAuthProviderUserId = Guid.NewGuid();
        var ownerId = await SeedUserAsync(ownerAuthProviderUserId, "Owner");
        var friendRequesterAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(friendRequesterAuthProviderUserId, "Requester");
        var challengerAuthProviderUserId = Guid.NewGuid();
        var challengerId = await SeedUserAsync(challengerAuthProviderUserId, "Challenger");
        var matchOpponentAuthProviderUserId = Guid.NewGuid();
        var matchOpponentId = await SeedUserAsync(matchOpponentAuthProviderUserId, "Opponent");

        var ownerClient = CreateAuthenticatedClient(ownerAuthProviderUserId);
        var friendRequesterClient = CreateAuthenticatedClient(friendRequesterAuthProviderUserId);
        var challengerClient = CreateAuthenticatedClient(challengerAuthProviderUserId);

        // Friend request: sent then accepted — resolved, no longer pending.
        var sentFriendRequestResponse = await friendRequesterClient.PostAsJsonAsync(
            "/friends/requests", new SendFriendRequestRequest(ownerId));
        var sentFriendRequest = await sentFriendRequestResponse.Content.ReadFromJsonAsync<FriendRequestResponse>();
        await ownerClient.PostAsync($"/friends/requests/{sentFriendRequest!.Id}/accept", content: null);

        // Challenge: sent then declined — resolved, no ConnectMatch created
        // as a side effect that would otherwise reappear as category 3.
        await MakeFriendsAsync(ownerId, challengerId);
        var sentChallengeResponse = await challengerClient.PostAsJsonAsync("/challenges", new SendChallengeRequest(ownerId));
        var sentChallenge = await sentChallengeResponse.Content.ReadFromJsonAsync<ChallengeResponse>();
        await ownerClient.PostAsync($"/challenges/{sentChallenge!.Id}/decline", content: null);

        // Match: the owner's own slot reaches a terminal state (bust) —
        // the match itself is left Active, matching REQ-1411's "their own
        // slot" scope, not "the whole match resolved".
        var matchId = await CreateOpenMatchAwaitingActionAsync(ownerId, matchOpponentId);
        await MarkSlotBustedAsync(matchId, ownerId);

        var response = await ownerClient.GetAsync("/notifications/summary");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<NotificationSummaryResponse>();
        Assert.That(body!.PendingFriendRequestCount, Is.EqualTo(0));
        Assert.That(body.PendingChallengeCount, Is.EqualTo(0));
        Assert.That(body.MatchesAwaitingActionCount, Is.EqualTo(0));
        Assert.That(body.HasPending, Is.False);
    }

    // ---- GWT#6: an unpaired matchmaking opt-in alone never counts ------------

    [Test]
    public async Task REQ1411_GetNotificationsSummary_UnpairedMatchmakingOptInOnly_ReturnsZeroCountsAndHasPendingFalse()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var optInResponse = await client.PostAsync("/matchmaking/opt-in", content: null);
        Assert.That(optInResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var optIn = await optInResponse.Content.ReadFromJsonAsync<MatchmakingOptInResponse>();
        Assert.That(optIn!.Status, Is.EqualTo("Waiting"));

        var response = await client.GetAsync("/notifications/summary");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<NotificationSummaryResponse>();
        Assert.That(body!.PendingFriendRequestCount, Is.EqualTo(0));
        Assert.That(body.PendingChallengeCount, Is.EqualTo(0));
        Assert.That(body.MatchesAwaitingActionCount, Is.EqualTo(0));
        Assert.That(body.HasPending, Is.False,
            "an unpaired MatchmakingOptIn must never be counted as a pending item");
    }
}
