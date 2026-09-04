using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Auth;
using XGArcade.Api.Connect;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Api.Tests;

// REQ-1406 (docs/requirements-document.md §4.15): API-level coverage for
// POST /matches/{matchId}/chain-steps. Same WebApplicationFactory<Program> +
// in-memory-DbContext-swap + LocalE2EAuth pattern as ConnectMatchEndpointTests.
// The full REQ-1406 Given/When/Then branch matrix (non-overlapping-period
// rejection, never-played-for-club rejection, closing-vs-starting-target
// distinction, chain-already-complete, every mechanical outcome) is already
// covered at the service level by
// XGArcade.Games.XGConnect.Tests/ConnectChainStepServiceTests.cs and is
// deliberately NOT re-proven here — this file only covers status-code
// mapping for each outcome, over the real HTTP pipeline.
//
// Target/candidate players' PlayerCareerStint rows are seeded directly
// (never via a real Wikidata call) for every outcome except
// LiveLookupUnavailable, which needs a genuine technical failure — that one
// test swaps in a local FakeWikidataClient (RemoveAll+AddSingleton, same
// precedent as AdminEndpointTests.FakeWikidataClient) rather than reaching
// wikidata.org.
public class ConnectChainStepEndpointTests
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

    private async Task<Guid> SeedPlayerAsync(string fullName, string? wikidataQid = null)
    {
        using var scope = _factory.Services.CreateScope();
        var playerRepository = scope.ServiceProvider.GetRequiredService<IPlayerRepository>();
        var player = await playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = fullName, WikidataQid = wikidataQid });
        return player.Id;
    }

    private async Task SeedCareerStintAsync(Guid playerId, string clubName, int startYear, int? endYear)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.PlayerCareerStints.Add(new PlayerCareerStint
        {
            Id = Guid.NewGuid(),
            PlayerId = playerId,
            ClubName = clubName,
            StartYear = startYear,
            EndYear = endYear,
        });
        await dbContext.SaveChangesAsync();
    }

    // Creates an ACTIVE match with both target picks recorded (locked) —
    // the REQ-1406 precondition.
    private async Task<Guid> CreateActiveMatchAsync(Guid aUserId, Guid bUserId, Guid aTargetPlayerId, Guid bTargetPlayerId)
    {
        using var scope = _factory.Services.CreateScope();
        var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
        var now = DateTime.UtcNow;
        var match = await connectMatchRepository.AddMatchAsync(new ConnectMatch
        {
            Id = Guid.NewGuid(),
            PlayerAUserId = aUserId,
            PlayerBUserId = bUserId,
            CreatedAt = now,
            Status = ConnectMatchStatus.Active,
            StartedAt = now,
            DeadlineUtc = now.AddHours(6),
        });
        await connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, aUserId, aTargetPlayerId, now);
        await connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, bUserId, bTargetPlayerId, now);
        await connectMatchRepository.LockTargetPicksForMatchAsync(match.Id);
        return match.Id;
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth gating --------------------------------------------------------

    [Test]
    public async Task REQ1406_PostChainStep_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/matches/{Guid.NewGuid()}/chain-steps", new SubmitChainStepRequest("Anyone"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- Happy path: an accepted, non-closing step over the real HTTP
    // ---- pipeline -------------------------------------------------------------

    [Test]
    public async Task REQ1406_PostChainStep_ValidOverlappingStep_ReturnsOkWithIsValidTrue()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Middle Link Player");
        // Overlaps A's target at Arsenal, but not B's target at all — accepted, not closing.
        await SeedCareerStintAsync(aTargetPlayerId, "Arsenal", 1999, 2007);
        await SeedCareerStintAsync(candidateId, "Arsenal", 2003, 2010);
        await SeedCareerStintAsync(bTargetPlayerId, "Chelsea", 1999, 2007);
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Middle Link Player"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitChainStepResponse>();
        Assert.That(body!.IsValid, Is.True);
        Assert.That(body.ChainComplete, Is.False);
        Assert.That(body.CandidatePlayerId, Is.EqualTo(candidateId));
    }

    // ---- Closing step ---------------------------------------------------------

    [Test]
    public async Task REQ1406_PostChainStep_ClosingStep_ReturnsOkWithChainCompleteTrue()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Closing Link Player");
        await SeedCareerStintAsync(aTargetPlayerId, "Arsenal", 1999, 2007);
        await SeedCareerStintAsync(candidateId, "Arsenal", 2003, 2010);
        // Also overlaps B's target at Chelsea — closes the chain.
        await SeedCareerStintAsync(candidateId, "Chelsea", 2011, 2015);
        await SeedCareerStintAsync(bTargetPlayerId, "Chelsea", 2012, 2016);
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Closing Link Player"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitChainStepResponse>();
        Assert.That(body!.IsValid, Is.True);
        Assert.That(body.ChainComplete, Is.True);
    }

    // ---- Invalid step (fails live validation) — still 200 OK, mirroring
    // ---- GuessEndpoints' "wrong guess is a normal 200" precedent -------------

    [Test]
    public async Task REQ1406_PostChainStep_NonOverlappingPeriodClaim_ReturnsOkWithIsValidFalse()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var candidateId = await SeedPlayerAsync("Middle Link Player");
        await SeedCareerStintAsync(aTargetPlayerId, "Arsenal", 1999, 2003);
        await SeedCareerStintAsync(candidateId, "Arsenal", 2010, 2015); // Non-overlapping period.
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Middle Link Player"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitChainStepResponse>();
        Assert.That(body!.IsValid, Is.False);
        Assert.That(body.ChainComplete, Is.False);
    }

    // ---- REQ-1407/S-214: a second, consecutive failure at the same
    // ---- position busts the caller — still 200 OK, with Busted: true ---------

    [Test]
    public async Task REQ1407_PostChainStep_SecondConsecutiveFailureAtSamePosition_ReturnsOkWithBustedTrue()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        await SeedPlayerAsync("First Attempt Player");
        await SeedPlayerAsync("Retry Attempt Player");
        // Neither candidate is ever given a career stint at the claimed
        // club — both attempts fail the live overlap check.
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var client = CreateAuthenticatedClient(aAuthProviderUserId);
        await client.PostAsJsonAsync($"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("First Attempt Player"));

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Retry Attempt Player"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitChainStepResponse>();
        Assert.That(body!.IsValid, Is.False);
        Assert.That(body.Busted, Is.True);
    }

    // ---- REQ-1407/S-214: a player whose own slot already forfeited cannot
    // ---- submit further steps, even while the match itself is still Active --

    [Test]
    public async Task REQ1407_PostChainStep_CallerAlreadyBusted_ReturnsConflict()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);

        using (var scope = _factory.Services.CreateScope())
        {
            var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
            await connectMatchRepository.MarkPlayerBustedAsync(matchId, isPlayerA: true, DateTime.UtcNow);
        }

        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Anyone"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Already forfeited"));
    }

    // ---- Candidate name doesn't resolve to any known player — still 200 OK --

    [Test]
    public async Task REQ1406_PostChainStep_CandidateNotFound_ReturnsOkWithIsValidFalse()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Nobody Real"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitChainStepResponse>();
        Assert.That(body!.IsValid, Is.False);
        Assert.That(body.CandidatePlayerId, Is.Null);
    }

    // ---- Precondition failures --------------------------------------------

    [Test]
    public async Task REQ1406_PostChainStep_MatchNotFound_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, "Alex");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{Guid.NewGuid()}/chain-steps", new SubmitChainStepRequest("Anyone"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ1406_PostChainStep_CallerNotAParticipant_ReturnsForbidden()
    {
        var userAId = await SeedUserAsync(Guid.NewGuid(), "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);
        var outsiderAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(outsiderAuthProviderUserId, "Outsider");
        var client = CreateAuthenticatedClient(outsiderAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Anyone"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Not a participant"));
    }

    [Test]
    public async Task REQ1406_PostChainStep_MatchNotActive_ReturnsConflict()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");

        // Build the not-yet-started match directly (AwaitingTargetPicks is
        // the entity's own default Status) — same repository access as
        // CreateActiveMatchAsync, just without locking any picks.
        Guid matchId;
        using (var scope = _factory.Services.CreateScope())
        {
            var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
            var match = await connectMatchRepository.AddMatchAsync(new ConnectMatch
            {
                Id = Guid.NewGuid(),
                PlayerAUserId = userAId,
                PlayerBUserId = userBId,
                CreatedAt = DateTime.UtcNow,
            });
            matchId = match.Id;
        }

        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Anyone"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Match is not active"));
    }

    [Test]
    public async Task REQ1406_PostChainStep_ChainAlreadyComplete_ReturnsConflict()
    {
        var aAuthProviderUserId = Guid.NewGuid();
        var userAId = await SeedUserAsync(aAuthProviderUserId, "Alex");
        var userBId = await SeedUserAsync(Guid.NewGuid(), "Blair");
        var aTargetPlayerId = await SeedPlayerAsync("A Target Player");
        var bTargetPlayerId = await SeedPlayerAsync("B Target Player");
        var matchId = await CreateActiveMatchAsync(userAId, userBId, aTargetPlayerId, bTargetPlayerId);

        using (var scope = _factory.Services.CreateScope())
        {
            var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
            await connectMatchRepository.AddChainStepAsync(new ConnectChainStep
            {
                Id = Guid.NewGuid(),
                ConnectMatchId = matchId,
                UserId = userAId,
                Position = 1,
                AttemptNumber = 1,
                CandidatePlayerId = bTargetPlayerId,
                MatchedClubName = "Chelsea",
                MatchedOverlapStartYear = 2000,
                IsValid = true,
                ClosesChain = true,
                SubmittedAt = DateTime.UtcNow,
            });
        }

        var client = CreateAuthenticatedClient(aAuthProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Anyone"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Chain already complete"));
    }

    // ---- Live-lookup-unavailable — needs a genuine technical failure, so
    // ---- this one test swaps in a local FakeWikidataClient rather than
    // ---- reaching wikidata.org (same RemoveAll+AddSingleton precedent as
    // ---- AdminEndpointTests.FakeWikidataClient) ------------------------------

    [Test]
    public async Task REQ1406_PostChainStep_LiveLookupUnavailable_ReturnsServiceUnavailable()
    {
        var failingClient = new FailingWikidataClient();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWikidataClient>();
                services.AddSingleton<IWikidataClient>(failingClient);
            });
        });

        var aAuthProviderUserId = Guid.NewGuid();
        Guid userAId, userBId, aTargetPlayerId, bTargetPlayerId, matchId;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(), AuthProviderUserId = aAuthProviderUserId, Email = "a@example.com",
                DisplayName = "Alex", EmailConfirmed = true, CreatedAt = DateTime.UtcNow,
            });
            var bAuthProviderUserId = Guid.NewGuid();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(), AuthProviderUserId = bAuthProviderUserId, Email = "b@example.com",
                DisplayName = "Blair", EmailConfirmed = true, CreatedAt = DateTime.UtcNow,
            });
            await dbContext.SaveChangesAsync();
            userAId = dbContext.Users.Single(u => u.AuthProviderUserId == aAuthProviderUserId).Id;
            userBId = dbContext.Users.Single(u => u.AuthProviderUserId == bAuthProviderUserId).Id;

            var playerRepository = scope.ServiceProvider.GetRequiredService<IPlayerRepository>();
            // A valid-looking WikidataQid with ZERO cached PlayerCareerStint
            // rows — the "needs a live refresh" trigger — and the failing
            // client above throws on every such call.
            var aTarget = await playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "A Target Player", WikidataQid = "Q1" });
            var bTarget = await playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "B Target Player", WikidataQid = "Q2" });
            await playerRepository.AddPlayerAsync(new Player { Id = Guid.NewGuid(), FullName = "Middle Link Player", WikidataQid = "Q3" });
            aTargetPlayerId = aTarget.Id;
            bTargetPlayerId = bTarget.Id;

            var connectMatchRepository = scope.ServiceProvider.GetRequiredService<IConnectMatchRepository>();
            var now = DateTime.UtcNow;
            var match = await connectMatchRepository.AddMatchAsync(new ConnectMatch
            {
                Id = Guid.NewGuid(),
                PlayerAUserId = userAId,
                PlayerBUserId = userBId,
                CreatedAt = now,
                Status = ConnectMatchStatus.Active,
                StartedAt = now,
                DeadlineUtc = now.AddHours(6),
            });
            await connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, userAId, aTargetPlayerId, now);
            await connectMatchRepository.AddOrUpdateTargetPickAsync(match.Id, userBId, bTargetPlayerId, now);
            await connectMatchRepository.LockTargetPicksForMatchAsync(match.Id);
            matchId = match.Id;
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(aAuthProviderUserId));

        var response = await client.PostAsJsonAsync(
            $"/matches/{matchId}/chain-steps", new SubmitChainStepRequest("Middle Link Player"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Live verification unavailable"));
    }

    // ---- Minimal local fake for IWikidataClient — same "trivial stub except
    // ---- for the one member under test" precedent as
    // ---- AdminEndpointTests.FakeWikidataClient. Only
    // ---- QueryPlayerCareerStintsByQidsAsync is meaningfully implemented
    // ---- (always throws), since that's the only member
    // ---- PlayerCareerStintRefreshService's REQ-1406 code path calls. -------
    private sealed class FailingWikidataClient : IWikidataClient
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>> QueryPlayerCareerStintsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            throw new WikidataQueryException("simulated WDQS failure for REQ-1406 chain-step live validation");

        public Task<WikidataPlayerCareerLookupResult?> QueryPlayerCareerAndNationalityByNameAsync(
            string playerName, CancellationToken cancellationToken = default) =>
            Task.FromResult<WikidataPlayerCareerLookupResult?>(null);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
            string countryWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryNationalTeamClubIntersectionAsync(
            string nationalTeamWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
            string clubAWikidataQid, string clubBWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyCountryIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyClubIntersectionAsync(
            string trophyWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyCountryIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyNationalTeamIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyClubIntersectionAsync(
            string trophyWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyNationalTeamIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolBirthYearAsync(
            int birthYear, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        public Task<IReadOnlyDictionary<string, string>> QueryPlayerPhotosByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>> QueryPlayerPositionsAndBirthYearsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>>(new Dictionary<string, PlayerPositionBirthYearEntry>());

        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
            string nationalityWikidataQid, bool useCountryForSportProperty, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByClubAsync(
            string clubWikidataQid, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        public Task<RecentClubTransferLookupResult> QueryRecentClubTransfersAsync(
            string clubWikidataQid, string clubName, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecentClubTransferLookupResult(
                new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>(), new Dictionary<string, string>()));

        public Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<WikidataPlayerPhotoLookupResult?> QueryPlayerPhotoByNameAsync(
            string playerName, CancellationToken cancellationToken = default) =>
            Task.FromResult<WikidataPlayerPhotoLookupResult?>(null);

        public Task<WikidataPlayerRefreshData> QueryPlayerRefreshDataByQidAsync(
            string wikidataQid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WikidataPlayerRefreshData(null, null, null, null));
    }
}
