using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Auth;
using XGArcade.Data;
using XGArcade.Data.Entities;

namespace XGArcade.Api.Tests;

// REQ-718/ADR-0038: API-level coverage for POST /internal/purge-guest-accounts
// (the scheduled-purge half of guest account cleanup; AuthEndpointTests
// covers rule 1, deletion at logout). Same bearer-token-gated
// WebApplicationFactory pattern RoundEndpointTests uses for
// /internal/generate-round. Auth:Mode=local-e2e is set (like
// AuthEndpointTests/GuessEndpointTests) purely so the real
// IAccountDeletionService this endpoint calls resolves a safe,
// always-succeeding ISupabaseAuthClient (LocalE2EAuthClient) rather than
// attempting a real HTTP call to Supabase — this suite doesn't otherwise
// touch JWT-authenticated endpoints at all.
public class InternalGuestCleanupEndpointTests
{
    private const string ValidJobToken = "test-internal-job-token";

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Auth:Mode", "local-e2e");

                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Internal:JobToken"] = ValidJobToken,
                    });
                });

                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as every other
                    // XGArcade.Api.Tests file — see AuthEndpointTests' SetUp
                    // comment for why every XGArcadeDbContext-closed
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

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);
        return client;
    }

    private async Task<Guid> SeedUserAsync(bool isGuest, DateTime? claimedAt, DateTime createdAt, DateTime lastActiveAt, string? email = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = email,
            DisplayName = $"Player-{Guid.NewGuid():N}",
            EmailConfirmed = email is not null,
            IsGuest = isGuest,
            ClaimedAt = claimedAt,
            CreatedAt = createdAt,
            LastActiveAt = lastActiveAt,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    [Test]
    public async Task REQ718_PurgeGuestAccounts_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/internal/purge-guest-accounts", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ718_PurgeGuestAccounts_Post_ReturnsUnauthorized_WithWrongBearerToken()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-right-token");

        var response = await client.PostAsync("/internal/purge-guest-accounts", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // The main scenario: six seeded rows covering every rule-2/rule-3/
    // scope-note case in one run, so the endpoint's dedup and count-reporting
    // behavior can be pinned down against a single seeded snapshot —
    // - unclaimedOldGuest: only rule 2 (unclaimed, 35 days old, but active
    //   only a day ago, so rule 3 alone wouldn't catch it)
    // - unclaimedRecentGuest: caught by neither rule
    // - inactiveGuest: only rule 3 (10 days inactive, but created only 10
    //   days ago so rule 2's 30-day threshold doesn't apply yet)
    // - bothRulesGuest: matches both rules at once (40 days old and 40 days
    //   inactive) — the dedup case
    // - claimedOldGuest: REQ-718's own scope note — a claimed account is
    //   never purged by either rule, no matter how old
    // - activeRealUser: an ordinary (never-guest) account, old and inactive —
    //   REQ-718 adds no inactivity-based purge for real accounts at all
    [Test]
    public async Task REQ718_PurgeGuestAccounts_Post_PurgesExactlyUnclaimedOver30DaysAndInactiveOver7Days_DedupingOverlap()
    {
        var now = DateTime.UtcNow;
        var unclaimedOldGuestId = await SeedUserAsync(isGuest: true, claimedAt: null, createdAt: now.AddDays(-35), lastActiveAt: now.AddDays(-1));
        var unclaimedRecentGuestId = await SeedUserAsync(isGuest: true, claimedAt: null, createdAt: now.AddDays(-5), lastActiveAt: now.AddDays(-5));
        var inactiveGuestId = await SeedUserAsync(isGuest: true, claimedAt: null, createdAt: now.AddDays(-10), lastActiveAt: now.AddDays(-10));
        var bothRulesGuestId = await SeedUserAsync(isGuest: true, claimedAt: null, createdAt: now.AddDays(-40), lastActiveAt: now.AddDays(-40));
        var claimedOldGuestId = await SeedUserAsync(isGuest: false, claimedAt: now.AddDays(-40), createdAt: now.AddDays(-50), lastActiveAt: now.AddDays(-50), email: "claimed-old@example.com");
        var activeRealUserId = await SeedUserAsync(isGuest: false, claimedAt: null, createdAt: now.AddDays(-100), lastActiveAt: now.AddDays(-100), email: "real-inactive@example.com");
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/purge-guest-accounts", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<PurgeGuestAccountsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.UnclaimedGuestsMatched, Is.EqualTo(2), "unclaimedOldGuest + bothRulesGuest");
        Assert.That(body.InactiveGuestsMatched, Is.EqualTo(2), "inactiveGuest + bothRulesGuest");
        Assert.That(body.TotalAccountsDeleted, Is.EqualTo(3), "bothRulesGuest counted once, not twice");

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var remainingIds = (await assertDbContext.Users.AsNoTracking().Select(u => u.Id).ToListAsync()).ToHashSet();

        Assert.That(remainingIds, Does.Not.Contain(unclaimedOldGuestId), "rule 2: unclaimed for more than 30 days");
        Assert.That(remainingIds, Does.Not.Contain(inactiveGuestId), "rule 3: inactive for more than 7 days");
        Assert.That(remainingIds, Does.Not.Contain(bothRulesGuestId), "matches both rules — still deleted, exactly once");

        Assert.That(remainingIds, Does.Contain(unclaimedRecentGuestId), "caught by neither rule");
        Assert.That(remainingIds, Does.Contain(claimedOldGuestId), "claimed accounts are never purged by either rule, regardless of age");
        Assert.That(remainingIds, Does.Contain(activeRealUserId), "REQ-718 adds no inactivity-based purge for real (non-guest) accounts");
    }

    [Test]
    public async Task REQ718_PurgeGuestAccounts_Post_NoQualifyingAccounts_ReturnsZeroCounts()
    {
        var now = DateTime.UtcNow;
        await SeedUserAsync(isGuest: true, claimedAt: null, createdAt: now.AddDays(-1), lastActiveAt: now.AddDays(-1));
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/purge-guest-accounts", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<PurgeGuestAccountsResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.UnclaimedGuestsMatched, Is.EqualTo(0));
        Assert.That(body.InactiveGuestsMatched, Is.EqualTo(0));
        Assert.That(body.TotalAccountsDeleted, Is.EqualTo(0));
    }
}
