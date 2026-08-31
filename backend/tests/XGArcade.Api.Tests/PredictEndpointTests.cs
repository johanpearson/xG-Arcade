using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Predict;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGPredict;

namespace XGArcade.Api.Tests;

// REQ-1302/1303/1306: API-level coverage for xG Predict's own read/write
// surface (GET /predict/current, POST /predict/matches/{matchId}/predictions,
// POST /predict/confirm) — the first real HTTP endpoints for this game,
// following ADR-0098's placement decision. Same in-memory-DbContext-swap/
// local-e2e-auth pattern as PathEndpointTests (this project's established
// convention).
//
// Match kickoff times are seeded relative to the REAL system clock
// (DateTime.UtcNow +/- an offset), never a swapped-in fake TimeProvider —
// same convention every other Api.Tests file already follows (no existing
// file in this project replaces the DI TimeProvider registration); a match
// whose kickoff is a few seconds in the past is a stable, deterministic way
// to exercise REQ-1303's round-wide lock without needing wall-clock timing
// precision beyond what a single test method's execution takes.
public class PredictEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Same reasoning as every other Api.Tests file's own comment:
                // Program.cs's real-Supabase JWT validation branch fetches a
                // live JWKS document (ADR-0017), so this test host uses the
                // in-process HS256 signer/validator instead.
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

    // ---- Seeding helpers ----------------------------------------------

    private async Task<Guid> SeedUserAsync(Guid authProviderUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = $"{authProviderUserId}@example.com",
            DisplayName = "Test Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    // Seeds one active xg-predict Round + PredictInstance with 5 matches,
    // each kicking off `kickoffOffsets[i]` from now (negative = already
    // kicked off). The round's own StartTime/EndTime always span "now" so
    // GetActiveByGameKeyAsync finds it regardless of the matches' own
    // kickoff offsets — REQ-1303's round-wide lock is driven entirely by
    // the matches' KickoffUtc, never by Round.StartTime/EndTime, mirroring
    // XGPredictGameModule's own formula exactly.
    private async Task<(Guid RoundId, Guid InstanceId, List<Guid> MatchIds)> SeedPredictRoundAsync(TimeSpan[] kickoffOffsets)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var instanceId = Guid.NewGuid();
        var matches = kickoffOffsets.Select((offset, i) => new PredictMatch
        {
            Id = Guid.NewGuid(),
            PredictInstanceId = instanceId,
            ExternalFixtureId = i + 1,
            HomeTeamName = $"Home {i + 1}",
            AwayTeamName = $"Away {i + 1}",
            KickoffUtc = DateTime.UtcNow.Add(offset),
        }).ToList();

        dbContext.PredictInstances.Add(new PredictInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Matches = matches,
        });

        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGPredictGameModule.XGPredictGameKey,
            GameInstanceId = instanceId,
            SequenceNumber = 1,
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(6),
            AllowGuessChange = true,
        };
        dbContext.Rounds.Add(round);

        await dbContext.SaveChangesAsync();
        return (round.Id, instanceId, matches.Select(m => m.Id).ToList());
    }

    private static TimeSpan[] FiveFutureKickoffs() =>
        [TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(4), TimeSpan.FromHours(5)];

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth guardrails ------------------------------------------------

    [Test]
    public async Task PredictCurrent_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/predict/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task PredictCurrent_Get_ReturnsUnauthorized_ForTokenWithNoMatchingLocalUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/predict/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- GET /predict/current -------------------------------------------

    [Test]
    public async Task REQ1301_PredictCurrent_Get_ReturnsNotFound_WhenNoActiveRoundExists()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/predict/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("No active round"));
    }

    [Test]
    public async Task REQ1301_PredictCurrent_Get_ReturnsAllFiveMatches_WithNoPredictionYet_NotLocked()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/predict/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<CurrentPredictResponse>();
        Assert.That(body!.RoundId, Is.EqualTo(roundId));
        Assert.That(body.Locked, Is.False);
        Assert.That(body.ConfirmedLocked, Is.False);
        Assert.That(body.Matches, Has.Count.EqualTo(5));
        Assert.That(body.Matches.Select(m => m.MatchId), Is.EquivalentTo(matchIds));
        Assert.That(body.Matches, Has.All.Matches<CurrentPredictMatchResponse>(m => m.HomeGoals == null && m.AwayGoals == null));
    }

    [Test]
    public async Task REQ1302_PredictCurrent_Get_NeverRevealsAnotherPlayersPrediction()
    {
        var firstAuthProviderUserId = Guid.NewGuid();
        var secondAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(firstAuthProviderUserId);
        await SeedUserAsync(secondAuthProviderUserId);
        var (_, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var firstClient = CreateAuthenticatedClient(firstAuthProviderUserId);
        var secondClient = CreateAuthenticatedClient(secondAuthProviderUserId);
        await firstClient.PostAsJsonAsync($"/predict/matches/{matchIds[0]}/predictions", new SubmitPredictionRequest(2, 1));

        var secondResponse = await secondClient.GetAsync("/predict/current");

        var secondBody = await secondResponse.Content.ReadFromJsonAsync<CurrentPredictResponse>();
        var match = secondBody!.Matches.Single(m => m.MatchId == matchIds[0]);
        Assert.That(match.HomeGoals, Is.Null, "REQ-1302: a response must never reveal another player's prediction");
        Assert.That(match.AwayGoals, Is.Null);
    }

    // ---- REQ-1302: POST /predict/matches/{matchId}/predictions ----------

    [Test]
    public async Task REQ1302_SubmitPrediction_BeforeLock_IsAccepted_AndVisibleOnCurrent()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (_, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync($"/predict/matches/{matchIds[0]}/predictions", new SubmitPredictionRequest(2, 1));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<PredictionSubmissionResponse>();
        Assert.That(body!.MatchId, Is.EqualTo(matchIds[0]));
        Assert.That(body.HomeGoals, Is.EqualTo(2));
        Assert.That(body.AwayGoals, Is.EqualTo(1));

        var current = await (await client.GetAsync("/predict/current")).Content.ReadFromJsonAsync<CurrentPredictResponse>();
        var match = current!.Matches.Single(m => m.MatchId == matchIds[0]);
        Assert.That(match.HomeGoals, Is.EqualTo(2));
        Assert.That(match.AwayGoals, Is.EqualTo(1));
    }

    [Test]
    public async Task REQ1302_SubmitPrediction_Resubmission_OverwritesPriorValue()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (_, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/predict/matches/{matchIds[0]}/predictions", new SubmitPredictionRequest(2, 1));

        var response = await client.PostAsJsonAsync($"/predict/matches/{matchIds[0]}/predictions", new SubmitPredictionRequest(0, 0));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var current = await (await client.GetAsync("/predict/current")).Content.ReadFromJsonAsync<CurrentPredictResponse>();
        var match = current!.Matches.Single(m => m.MatchId == matchIds[0]);
        Assert.That(match.HomeGoals, Is.EqualTo(0), "a resubmission must replace the prior value");
        Assert.That(match.AwayGoals, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1302_SubmitPrediction_NegativeGoalCount_ReturnsBadRequest()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (_, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync($"/predict/matches/{matchIds[0]}/predictions", new SubmitPredictionRequest(-1, 0));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid prediction"));
    }

    [Test]
    public async Task REQ1302_SubmitPrediction_UnknownMatchId_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedPredictRoundAsync(FiveFutureKickoffs());
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync($"/predict/matches/{Guid.NewGuid()}/predictions", new SubmitPredictionRequest(1, 1));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ1302_SubmitPrediction_NoActiveRound_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync($"/predict/matches/{Guid.NewGuid()}/predictions", new SubmitPredictionRequest(1, 1));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-1303: round-wide lock at the first match's kickoff ---------

    [Test]
    public async Task REQ1303_SubmitPrediction_AfterEarliestKickoff_RejectsEveryMatch_IncludingOneNotYetKickedOff()
    {
        // Match 0 kicked off 10 seconds ago (round-wide lock instant already
        // passed); matches 1-4 kick off hours from now — REQ-1303 says the
        // whole round is locked regardless of any one match's own kickoff.
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (_, _, matchIds) = await SeedPredictRoundAsync(
            [TimeSpan.FromSeconds(-10), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(4)]);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var responseForNotYetKickedOffMatch = await client.PostAsJsonAsync(
            $"/predict/matches/{matchIds[1]}/predictions", new SubmitPredictionRequest(1, 1));

        Assert.That(responseForNotYetKickedOffMatch.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
            "match 1's own kickoff hasn't happened yet, but the round-wide lock (match 0's kickoff) already has");
        var problem = await responseForNotYetKickedOffMatch.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Round is locked"));
    }

    [Test]
    public async Task REQ1303_PredictCurrent_Get_ReflectsLockedTrue_OnceEarliestKickoffHasPassed()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedPredictRoundAsync(
            [TimeSpan.FromSeconds(-10), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(4)]);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/predict/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentPredictResponse>();
        Assert.That(body!.Locked, Is.True);
    }

    // ---- REQ-1306: POST /predict/confirm ---------------------------------

    [Test]
    public async Task REQ1306_Confirm_NotAllFivePredictionsSubmitted_ReturnsConflict()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (_, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var client = CreateAuthenticatedClient(authProviderUserId);
        // Only 4 of the 5 matches predicted.
        for (var i = 0; i < 4; i++)
            await client.PostAsJsonAsync($"/predict/matches/{matchIds[i]}/predictions", new SubmitPredictionRequest(1, 0));

        var response = await client.PostAsync("/predict/confirm", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Not all predictions submitted"));
    }

    [Test]
    public async Task REQ1306_Confirm_AllFivePredictionsSubmitted_Succeeds_AndReflectedOnCurrent()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var client = CreateAuthenticatedClient(authProviderUserId);
        foreach (var matchId in matchIds)
            await client.PostAsJsonAsync($"/predict/matches/{matchId}/predictions", new SubmitPredictionRequest(1, 0));

        var response = await client.PostAsync("/predict/confirm", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ConfirmPredictionsResponse>();
        Assert.That(body!.RoundId, Is.EqualTo(roundId));

        var current = await (await client.GetAsync("/predict/current")).Content.ReadFromJsonAsync<CurrentPredictResponse>();
        Assert.That(current!.ConfirmedLocked, Is.True);
    }

    [Test]
    public async Task REQ1306_Confirm_ThenFurtherSubmission_IsRejected_EvenThoughRoundWideLockHasNotFired()
    {
        // Every match kicks off hours from now — REQ-1303's round-wide lock
        // has NOT fired. The per-player confirm-lock must still reject a
        // further submission, independent of REQ-1303 (REQ-1306's own core
        // acceptance criterion).
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (_, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var client = CreateAuthenticatedClient(authProviderUserId);
        foreach (var matchId in matchIds)
            await client.PostAsJsonAsync($"/predict/matches/{matchId}/predictions", new SubmitPredictionRequest(1, 0));
        await client.PostAsync("/predict/confirm", null);

        var response = await client.PostAsJsonAsync($"/predict/matches/{matchIds[0]}/predictions", new SubmitPredictionRequest(2, 2));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Predictions already confirmed and locked"));

        // The pre-confirm value must remain unchanged (the rejected
        // resubmission must never have taken effect).
        var current = await (await client.GetAsync("/predict/current")).Content.ReadFromJsonAsync<CurrentPredictResponse>();
        var match = current!.Matches.Single(m => m.MatchId == matchIds[0]);
        Assert.That(match.HomeGoals, Is.EqualTo(1));
        Assert.That(match.AwayGoals, Is.EqualTo(0));
    }

    [Test]
    public async Task REQ1306_Confirm_CalledTwice_SecondCallReturnsConflict()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (_, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var client = CreateAuthenticatedClient(authProviderUserId);
        foreach (var matchId in matchIds)
            await client.PostAsJsonAsync($"/predict/matches/{matchId}/predictions", new SubmitPredictionRequest(1, 0));
        await client.PostAsync("/predict/confirm", null);

        var response = await client.PostAsync("/predict/confirm", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task REQ1306_Confirm_DoesNotAffectAnotherPlayersAbilityToSubmit()
    {
        var firstAuthProviderUserId = Guid.NewGuid();
        var secondAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(firstAuthProviderUserId);
        await SeedUserAsync(secondAuthProviderUserId);
        var (_, _, matchIds) = await SeedPredictRoundAsync(FiveFutureKickoffs());
        var firstClient = CreateAuthenticatedClient(firstAuthProviderUserId);
        var secondClient = CreateAuthenticatedClient(secondAuthProviderUserId);
        foreach (var matchId in matchIds)
            await firstClient.PostAsJsonAsync($"/predict/matches/{matchId}/predictions", new SubmitPredictionRequest(1, 0));
        await firstClient.PostAsync("/predict/confirm", null);

        var response = await secondClient.PostAsJsonAsync($"/predict/matches/{matchIds[0]}/predictions", new SubmitPredictionRequest(3, 3));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "REQ-1306's per-player lock must never affect any other player's ability to submit");
    }
}
