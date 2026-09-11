using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.HigherLower;
using XGArcade.Core.Games;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGHigherLower;

namespace XGArcade.Api.Tests;

// REQ-1504/1505: API-level coverage for xG Higher/Lower's own read/write
// surface (GET /higher-lower/current, POST /higher-lower/guesses) — S-227's
// own accept criterion. Same in-memory-DbContext-swap/local-e2e-auth pattern
// as PredictEndpointTests (this project's established convention) — these
// tests exercise already-existing REQ-1504/1505 acceptance criteria via a
// new access path (the HTTP surface), not new requirements.
public class HigherLowerEndpointTests
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

    // Seeds one active xg-higher-lower Round + HigherLowerInstance with a
    // baseline plus `comparatorCount` comparators, each with a strictly
    // increasing Value (baselineValue, baselineValue+1, baselineValue+2, ...)
    // so "Higher" is always the correct direction all the way through —
    // tests that need an incorrect guess submit "Lower" instead. Seeds a
    // Player row for every player id used so GetPlayersByIdsAsync returns
    // real names.
    private async Task<(Guid RoundId, Guid InstanceId, Guid BaselinePlayerId, List<Guid> ComparatorPlayerIds)> SeedHigherLowerRoundAsync(
        int comparatorCount = 3, int baselineValue = 10, string? baselinePhotoUrl = null, string? firstComparatorPhotoUrl = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var instanceId = Guid.NewGuid();
        var baselinePlayerId = Guid.NewGuid();
        dbContext.Players.Add(new Player { Id = baselinePlayerId, FullName = "Baseline Player", PhotoUrl = baselinePhotoUrl });

        var comparatorPlayerIds = new List<Guid>();
        var comparators = new List<HigherLowerComparator>();
        for (var i = 0; i < comparatorCount; i++)
        {
            var playerId = Guid.NewGuid();
            comparatorPlayerIds.Add(playerId);
            // REQ-1508: only the first comparator (the one GET's
            // NextComparator/a single-guess POST both exercise) takes the
            // optional photo — later comparators stay photo-less, matching
            // every other test in this file that only cares about the first.
            dbContext.Players.Add(new Player
            {
                Id = playerId,
                FullName = $"Comparator {i + 1}",
                PhotoUrl = i == 0 ? firstComparatorPhotoUrl : null,
            });
            comparators.Add(new HigherLowerComparator
            {
                Id = Guid.NewGuid(),
                HigherLowerInstanceId = instanceId,
                SequencePosition = i,
                PlayerId = playerId,
                Value = baselineValue + i + 1,
            });
        }

        dbContext.HigherLowerInstances.Add(new HigherLowerInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            StatCategory = "trophy",
            BaselinePlayerId = baselinePlayerId,
            BaselineValue = baselineValue,
            Comparators = comparators,
        });

        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGHigherLowerGameModule.XGHigherLowerGameKey,
            GameInstanceId = instanceId,
            SequenceNumber = 1,
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(6),
            AllowGuessChange = false,
        };
        dbContext.Rounds.Add(round);

        await dbContext.SaveChangesAsync();
        return (round.Id, instanceId, baselinePlayerId, comparatorPlayerIds);
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth guardrails ------------------------------------------------

    [Test]
    public async Task HigherLowerCurrent_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/higher-lower/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task HigherLowerCurrent_Get_ReturnsUnauthorized_ForTokenWithNoMatchingLocalUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/higher-lower/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- GET /higher-lower/current ---------------------------------------

    [Test]
    public async Task REQ1504_HigherLowerCurrent_Get_ReturnsNotFound_WhenNoActiveRoundExists()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/higher-lower/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("No active round"));
    }

    [Test]
    public async Task REQ1504_HigherLowerCurrent_Get_OnFreshAttempt_ReflectsInstancesFixedBaseline_AndFirstComparatorIdentityOnly()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, _, baselinePlayerId, comparatorPlayerIds) = await SeedHigherLowerRoundAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/higher-lower/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<CurrentHigherLowerResponse>();
        Assert.That(body!.RoundId, Is.EqualTo(roundId));
        Assert.That(body.StreakLength, Is.EqualTo(0));
        Assert.That(body.HasEnded, Is.False);
        Assert.That(body.ComparatorCount, Is.EqualTo(3));
        Assert.That(body.Baseline.PlayerId, Is.EqualTo(baselinePlayerId));
        Assert.That(body.Baseline.Name, Is.EqualTo("Baseline Player"));
        Assert.That(body.Baseline.Value, Is.EqualTo(10), "the current baseline's value is always revealed");
        Assert.That(body.NextComparator, Is.Not.Null);
        Assert.That(body.NextComparator!.PlayerId, Is.EqualTo(comparatorPlayerIds[0]));
        Assert.That(body.NextComparator.Name, Is.EqualTo("Comparator 1"));
        // The C# record has no Value field on HigherLowerNextComparatorResponse
        // at all — REQ-1504's "value hidden until guessed" contract is
        // enforced at the DTO shape level, so there is nothing further to
        // assert beyond the type itself carrying no such property.
    }

    // ---- POST /higher-lower/guesses --------------------------------------

    [Test]
    public async Task REQ1504_SubmitGuess_Correct_AdvancesStreak_AndReflectedOnCurrent()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (_, _, _, comparatorPlayerIds) = await SeedHigherLowerRoundAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            "/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Higher));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitHigherLowerGuessResponse>();
        Assert.That(body!.IsCorrect, Is.True);
        Assert.That(body.RevealedPlayerId, Is.EqualTo(comparatorPlayerIds[0]));
        Assert.That(body.RevealedValue, Is.EqualTo(11));
        Assert.That(body.StreakLength, Is.EqualTo(1));
        Assert.That(body.HasEnded, Is.False);

        var current = await (await client.GetAsync("/higher-lower/current")).Content.ReadFromJsonAsync<CurrentHigherLowerResponse>();
        Assert.That(current!.StreakLength, Is.EqualTo(1));
        Assert.That(current.HasEnded, Is.False);
        Assert.That(current.Baseline.PlayerId, Is.EqualTo(comparatorPlayerIds[0]), "the just-guessed comparator becomes the new baseline");
        Assert.That(current.Baseline.Value, Is.EqualTo(11));
        Assert.That(current.NextComparator!.PlayerId, Is.EqualTo(comparatorPlayerIds[1]));
    }

    [Test]
    public async Task REQ1504_SubmitGuess_Incorrect_EndsAttempt_StreakStaysAtPreGuessLength()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedHigherLowerRoundAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        // Every comparator's Value is strictly greater than the baseline's —
        // "Lower" is always the wrong direction here.
        var response = await client.PostAsJsonAsync(
            "/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Lower));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitHigherLowerGuessResponse>();
        Assert.That(body!.IsCorrect, Is.False);
        Assert.That(body.StreakLength, Is.EqualTo(0), "an incorrect guess never advances the streak");
        Assert.That(body.HasEnded, Is.True);

        var current = await (await client.GetAsync("/higher-lower/current")).Content.ReadFromJsonAsync<CurrentHigherLowerResponse>();
        Assert.That(current!.HasEnded, Is.True);
        Assert.That(current.NextComparator, Is.Null, "an ended attempt has no next comparator");
    }

    [Test]
    public async Task REQ1504_SubmitGuess_CorrectThroughEveryComparator_EndsAttempt_AtFullComparatorCount()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedHigherLowerRoundAsync(comparatorCount: 3);
        var client = CreateAuthenticatedClient(authProviderUserId);

        SubmitHigherLowerGuessResponse? lastBody = null;
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Higher));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            lastBody = await response.Content.ReadFromJsonAsync<SubmitHigherLowerGuessResponse>();
        }

        Assert.That(lastBody!.StreakLength, Is.EqualTo(3));
        Assert.That(lastBody.HasEnded, Is.True);
    }

    [Test]
    public async Task REQ1504_SubmitGuess_AgainstAlreadyEndedAttempt_ReturnsConflict()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedHigherLowerRoundAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync("/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Lower));

        var response = await client.PostAsJsonAsync(
            "/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Higher));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Attempt has ended"));
    }

    [Test]
    public async Task REQ1504_SubmitGuess_NoActiveRound_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            "/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Higher));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-1505: independent per-user progression ----------------------

    [Test]
    public async Task REQ1505_TwoUsersAgainstSameInstance_ProgressIndependently()
    {
        var firstAuthProviderUserId = Guid.NewGuid();
        var secondAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(firstAuthProviderUserId);
        await SeedUserAsync(secondAuthProviderUserId);
        await SeedHigherLowerRoundAsync();
        var firstClient = CreateAuthenticatedClient(firstAuthProviderUserId);
        var secondClient = CreateAuthenticatedClient(secondAuthProviderUserId);

        // First user guesses correctly twice.
        await firstClient.PostAsJsonAsync("/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Higher));
        await firstClient.PostAsJsonAsync("/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Higher));

        // Second user has never guessed yet.
        var secondCurrent = await (await secondClient.GetAsync("/higher-lower/current")).Content.ReadFromJsonAsync<CurrentHigherLowerResponse>();
        Assert.That(secondCurrent!.StreakLength, Is.EqualTo(0), "REQ-1505: never another player's progress");
        Assert.That(secondCurrent.HasEnded, Is.False);

        var firstCurrent = await (await firstClient.GetAsync("/higher-lower/current")).Content.ReadFromJsonAsync<CurrentHigherLowerResponse>();
        Assert.That(firstCurrent!.StreakLength, Is.EqualTo(2));
    }

    // ---- REQ-1508: PhotoUrl on Baseline/NextComparator/reveal -----------

    [Test]
    public async Task REQ1508_HigherLowerCurrent_Get_ReturnsBaselinePhotoUrl_WhenPlayerHasPhoto()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        const string photoUrl = "https://commons.wikimedia.org/wiki/Special:FilePath/Baseline%20Player.jpg";
        await SeedHigherLowerRoundAsync(baselinePhotoUrl: photoUrl);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/higher-lower/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentHigherLowerResponse>();
        Assert.That(body!.Baseline.PhotoUrl, Is.EqualTo(photoUrl));
    }

    [Test]
    public async Task REQ1508_HigherLowerCurrent_Get_BaselinePhotoUrlIsNull_WhenPlayerHasNoPhoto()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedHigherLowerRoundAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/higher-lower/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentHigherLowerResponse>();
        Assert.That(body!.Baseline.PhotoUrl, Is.Null, "no photo is a normal case, never an error/broken-image placeholder");
    }

    [Test]
    public async Task REQ1508_HigherLowerCurrent_Get_ReturnsNextComparatorPhotoUrl_WhenPlayerHasPhoto()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        const string photoUrl = "https://commons.wikimedia.org/wiki/Special:FilePath/Comparator%201.jpg";
        await SeedHigherLowerRoundAsync(firstComparatorPhotoUrl: photoUrl);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/higher-lower/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentHigherLowerResponse>();
        Assert.That(body!.NextComparator!.PhotoUrl, Is.EqualTo(photoUrl),
            "REQ-1508: the Next card's photo is always visible, independent of the still-hidden Value");
    }

    [Test]
    public async Task REQ1508_HigherLowerCurrent_Get_NextComparatorPhotoUrlIsNull_WhenPlayerHasNoPhoto()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedHigherLowerRoundAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/higher-lower/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentHigherLowerResponse>();
        Assert.That(body!.NextComparator!.PhotoUrl, Is.Null, "no photo is a normal case, never an error/broken-image placeholder");
    }

    [Test]
    public async Task REQ1508_SubmitGuess_ReturnsRevealedPlayerPhotoUrl_WhenPlayerHasPhoto()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        const string photoUrl = "https://commons.wikimedia.org/wiki/Special:FilePath/Comparator%201.jpg";
        await SeedHigherLowerRoundAsync(firstComparatorPhotoUrl: photoUrl);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            "/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Higher));

        var body = await response.Content.ReadFromJsonAsync<SubmitHigherLowerGuessResponse>();
        Assert.That(body!.RevealedPlayerPhotoUrl, Is.EqualTo(photoUrl));
    }

    [Test]
    public async Task REQ1508_SubmitGuess_RevealedPlayerPhotoUrlIsNull_WhenPlayerHasNoPhoto()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedHigherLowerRoundAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            "/higher-lower/guesses", new SubmitHigherLowerGuessRequest(HigherLowerDirection.Higher));

        var body = await response.Content.ReadFromJsonAsync<SubmitHigherLowerGuessResponse>();
        Assert.That(body!.RevealedPlayerPhotoUrl, Is.Null, "no photo is a normal case, never an error/broken-image placeholder");
    }
}
