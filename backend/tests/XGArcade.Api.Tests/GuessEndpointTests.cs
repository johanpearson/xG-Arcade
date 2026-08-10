using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Auth;
using XGArcade.Api.Guesses;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.DataSync.Wikidata;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Tests;

// S-009 (docs/backlog.md): API-level coverage for POST
// /rounds/{roundId}/cells/{cellId}/guesses — REQ-201 (submit a guess),
// REQ-202 (guess locking / allow_guess_change), REQ-210 (two-guess cap,
// immediate lock on correct). REQ-203/208's correctness/normalization
// branches are Unit-level only (requirements-document.md's own "Test level"
// notes) — covered in GridGameModuleTests, not repeated here at the API
// level. Real GridGameModule/GuessSubmissionService run behind the endpoint
// (no game-module fake at this level, unlike GuessSubmissionServiceTests) —
// only the DbContext is swapped for an in-memory provider, same pattern as
// every other file in this project.
public class GuessEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Program.cs's real-Supabase JWT validation branch now
                // fetches a live JWKS document (ADR-0017) — unit/API tests
                // must never depend on live network (docs/coding-
                // guidelines.md), so this test host uses the same in-process
                // HS256 signer/validator ci.yml's local E2E stack uses
                // instead.
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

    // Seeds a Round backed by a single-cell GridInstance directly (bypassing
    // /internal/generate-round entirely) plus one Player who satisfies that
    // cell's row/col categories — enough to exercise guess submission
    // end-to-end without depending on the real Wikidata HTTP client.
    private Task<(Guid RoundId, Guid CellId, string CorrectAnswerName)> SeedRoundWithCellAsync(
        DateTime startTime, DateTime endTime, bool allowGuessChange) =>
        SeedRoundWithCellAsync(startTime, endTime, allowGuessChange, photoUrl: null);

    // REQ-214: photoUrl defaults to null (today's every-other-test shape,
    // regression-covered by REQ201_Guess_Post_ActiveRound_StoresGuess_
    // AndReturnsCorrectnessImmediately below asserting ResolvedPlayerPhotoUrl
    // is null) — only the dedicated REQ214 tests pass a real value.
    private async Task<(Guid RoundId, Guid CellId, string CorrectAnswerName)> SeedRoundWithCellAsync(
        DateTime startTime, DateTime endTime, bool allowGuessChange, string? photoUrl)
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

        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = $"Qplayer-{Guid.NewGuid()}", PhotoUrl = photoUrl };
        dbContext.Players.Add(player);
        dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });
        dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });

        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GridGameModule.XGGridGameKey,
            GameInstanceId = instanceId,
            StartTime = startTime,
            EndTime = endTime,
            AllowGuessChange = allowGuessChange,
        };
        dbContext.Rounds.Add(round);

        await dbContext.SaveChangesAsync();
        return (round.Id, cellId, "Thierry Henry");
    }

    // REQ-209: same shape as SeedRoundWithCellAsync above, but seeds TWO
    // same-named players who both satisfy the cell's row/col categories —
    // enough to exercise the disambiguation-prompt response end-to-end.
    private async Task<(Guid RoundId, Guid CellId, string SharedName, Guid FirstPlayerId, Guid SecondPlayerId)> SeedRoundWithAmbiguousCellAsync(
        DateTime startTime, DateTime endTime, bool allowGuessChange)
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

        const string sharedName = "John Smith";
        var first = new Player { Id = Guid.NewGuid(), FullName = sharedName, WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        var second = new Player { Id = Guid.NewGuid(), FullName = sharedName, WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        dbContext.Players.AddRange(first, second);
        foreach (var player in new[] { first, second })
        {
            dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = "France" });
            dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = "Arsenal" });
        }

        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GridGameModule.XGGridGameKey,
            GameInstanceId = instanceId,
            StartTime = startTime,
            EndTime = endTime,
            AllowGuessChange = allowGuessChange,
        };
        dbContext.Rounds.Add(round);

        await dbContext.SaveChangesAsync();
        return (round.Id, cellId, sharedName, first.Id, second.Id);
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth / request-validation guardrails ------------------------------

    [Test]
    public async Task Guess_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/rounds/{Guid.NewGuid()}/cells/{Guid.NewGuid()}/guesses", new SubmitGuessRequest("Thierry Henry"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Guess_Post_ReturnsUnauthorized_ForTokenWithNoMatchingLocalUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsJsonAsync(
            $"/rounds/{Guid.NewGuid()}/cells/{Guid.NewGuid()}/guesses", new SubmitGuessRequest("Thierry Henry"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task Guess_Post_ReturnsBadRequest_ForEmptyOrWhitespaceSubmittedName(string submittedName)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{Guid.NewGuid()}/cells/{Guid.NewGuid()}/guesses", new SubmitGuessRequest(submittedName));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Guess_Post_ReturnsNotFound_ForUnknownCellId()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, _, _) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{Guid.NewGuid()}/guesses", new SubmitGuessRequest("Thierry Henry"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-201: submit a guess ------------------------------------------

    [Test]
    public async Task REQ201_Guess_Post_ActiveRound_StoresGuess_AndReturnsCorrectnessImmediately()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, correctAnswer) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(correctAnswer));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.IsCorrect, Is.True, "REQ-203/REQ-201: correctness must be determined and returned immediately upon submission");
        Assert.That(body.AttemptCount, Is.EqualTo(1));
        Assert.That(body.Locked, Is.True);
        Assert.That(body.ResolvedPlayerName, Is.EqualTo(correctAnswer), "frontend name-display fix: a correct guess's canonical name is returned in the same response");
        Assert.That(body.ResolvedPlayerPhotoUrl, Is.Null, "REQ-214: no photo was seeded for this player, so the field must be absent, not an error");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var stored = await dbContext.Guesses.SingleAsync(g => g.RoundId == roundId && g.CellId == cellId && g.UserId == userId);
        Assert.That(stored.SubmittedName, Is.EqualTo(correctAnswer));
        Assert.That(stored.IsCorrect, Is.True);
    }

    [TestCase(1, 4, TestName = "REQ201_Guess_Post_UpcomingRound_ReturnsConflict")]
    [TestCase(-4, -1, TestName = "REQ201_Guess_Post_ClosedRound_ReturnsConflict")]
    public async Task REQ201_Guess_Post_RoundNotCurrentlyActive_ReturnsConflictWithRoundNotActive(int startOffsetDays, int endOffsetDays)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, correctAnswer) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(startOffsetDays), DateTime.UtcNow.AddDays(endOffsetDays), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(correctAnswer));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Round is not active"));
    }

    [Test]
    public async Task REQ201_Guess_Post_UnknownRoundId_ReturnsNotFound()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{Guid.NewGuid()}/cells/{Guid.NewGuid()}/guesses", new SubmitGuessRequest("Thierry Henry"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ201_Guess_Post_Resubmission_OverwritesExistingGuessRow_NotDuplicateInsert()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, correctAnswer) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Someone Wrong"));

        var response = await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(correctAnswer));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var rowCount = await dbContext.Guesses.CountAsync(g => g.RoundId == roundId && g.CellId == cellId && g.UserId == userId);
        Assert.That(rowCount, Is.EqualTo(1), "a resubmission must overwrite the existing row, never insert a second one");
    }

    // ---- REQ-202: guess locking (allow_guess_change) -----------------------

    [Test]
    public async Task REQ202_Guess_Post_AllowGuessChangeFalse_SecondAttempt_ReturnsConflictWithDistinctReason()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, _) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: false);
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Wrong Guess One"));

        var response = await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Wrong Guess Two"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Guess changes are not allowed"));
    }

    [Test]
    public async Task REQ202_Guess_Post_AllowGuessChangeTrue_SecondAttempt_Accepted()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, correctAnswer) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Wrong Guess One"));

        var response = await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(correctAnswer));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.IsCorrect, Is.True);
        Assert.That(body.AttemptCount, Is.EqualTo(2));
    }

    // ---- Frontend name-display fix: canonical name for a correct guess -----

    [Test]
    public async Task REQ201_Guess_Post_CorrectGuessTypedInLowercase_ReturnsCanonicallyCasedResolvedPlayerName()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, correctAnswer) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(correctAnswer.ToLowerInvariant()));

        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.ResolvedPlayerName, Is.EqualTo(correctAnswer), "the display name must be the canonical Player.FullName, not the raw as-typed guess");
    }

    // ---- REQ-214: photo reveal alongside the resolved player name ---------

    [Test]
    public async Task REQ214_Guess_Post_CorrectGuess_ReturnsResolvedPlayerPhotoUrl_WhenPlayerHasPhoto()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        const string photoUrl = "https://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg";
        var (roundId, cellId, correctAnswer) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true, photoUrl);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(correctAnswer));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.ResolvedPlayerName, Is.EqualTo(correctAnswer));
        Assert.That(body.ResolvedPlayerPhotoUrl, Is.EqualTo(photoUrl));
    }

    [Test]
    public async Task REQ214_Guess_Post_CorrectGuess_ResolvedPlayerPhotoUrlIsNull_WhenPlayerHasNoPhoto()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, correctAnswer) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true, photoUrl: null);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(correctAnswer));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.ResolvedPlayerName, Is.EqualTo(correctAnswer), "REQ-212's name reveal must fall back unaffected when no photo exists");
        Assert.That(body.ResolvedPlayerPhotoUrl, Is.Null, "no photo is a normal case, never an error/broken-image placeholder");
    }

    [Test]
    public async Task REQ214_Guess_Post_IncorrectGuess_ResolvedPlayerPhotoUrlIsNull_EvenWhenAPlayerWithAPhotoExists()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, _) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true,
            photoUrl: "https://commons.wikimedia.org/wiki/Special:FilePath/Thierry%20Henry.jpg");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Someone Else Entirely"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.IsCorrect, Is.False);
        Assert.That(body.ResolvedPlayerPhotoUrl, Is.Null, "no photo is ever shown for an incorrect guess, unchanged from REQ-212's rule for names");
    }

    // ---- REQ-216/ADR-0057: wrong-guess photo on a locked, final-incorrect
    // cell --------------------------------------------------------------
    // Seeds a PlayerNameIndex entry PLUS an already-cached Player row for
    // the wrong-but-real guess (distinct from the cell's own correct
    // answer) — GridGameModule.ResolveWrongGuessPlayerAsync's cache-first
    // branch then returns without ever calling the live Wikidata client, so
    // these tests never depend on real network (docs/coding-guidelines.md).

    private async Task SeedMatchablePlayerAsync(string name, string? photoUrl)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.PlayerNameIndexEntries.Add(new PlayerNameIndex
        {
            PlayerId = Guid.NewGuid(),
            PrimaryName = name,
            NormalizedName = PlayerNameNormalizer.Normalize(name),
        });
        dbContext.Players.Add(new Player { Id = Guid.NewGuid(), FullName = name, PhotoUrl = photoUrl });
        await dbContext.SaveChangesAsync();
    }

    [Test]
    public async Task REQ216_Guess_Post_IncorrectWithAttemptsRemaining_ReturnsNullIncorrectGuessMatchedPlayerFields()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, _) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        await SeedMatchablePlayerAsync("Clarence Seedorf", "https://example.org/seedorf.jpg");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Clarence Seedorf"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.Locked, Is.False, "state 2 (incorrect, attempts remaining) — this REQ never applies");
        Assert.That(body.IncorrectGuessMatchedPlayerName, Is.Null);
        Assert.That(body.IncorrectGuessMatchedPlayerPhotoUrl, Is.Null);
    }

    [Test]
    public async Task REQ216_Guess_Post_FinalAttemptStillIncorrect_MatchesRealPlayer_ReturnsCanonicalNameAndPhoto()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, _) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        await SeedMatchablePlayerAsync("Clarence Seedorf", "https://example.org/seedorf.jpg");
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("First Wrong Guess"));

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Clarence Seedorf"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.Locked, Is.True);
        Assert.That(body.IsCorrect, Is.False);
        Assert.That(body.IncorrectGuessMatchedPlayerName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(body.IncorrectGuessMatchedPlayerPhotoUrl, Is.EqualTo("https://example.org/seedorf.jpg"));
    }

    [Test]
    public async Task REQ216_Guess_Post_FinalAttemptStillIncorrect_NoPlayerNameIndexMatch_ReturnsNullFields()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, _) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("First Wrong Guess"));

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Totally Made Up Name"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.Locked, Is.True);
        Assert.That(body.IncorrectGuessMatchedPlayerName, Is.Null,
            "a guess matching no real PlayerNameIndex candidate at all has no identity to show (REQ-216)");
        Assert.That(body.IncorrectGuessMatchedPlayerPhotoUrl, Is.Null);
    }

    // ---- REQ-210: two guesses per cell, locked immediately on correct -----

    [Test]
    public async Task REQ210_Guess_Post_CorrectFirstAttempt_LocksCell_SecondAttemptReturnsCellAlreadySolved_NotGuessChangeReason()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        // AllowGuessChange = true — proves the rejection below is REQ-210's
        // lock, not REQ-202's guess-change policy (which would allow this).
        var (roundId, cellId, correctAnswer) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);
        var first = await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(correctAnswer));
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var response = await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Another Guess"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Cell already solved"));
    }

    [Test]
    public async Task REQ210_Guess_Post_ThirdAttemptAfterTwoWrongUsed_ReturnsConflictWithNoAttemptsRemaining()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, _) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Wrong Guess One"));
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Wrong Guess Two"));

        var response = await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Wrong Guess Three"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("No attempts remaining"));
    }

    // ---- REQ-209/REQ-210: disambiguation prompt ----------------------------

    [Test]
    public async Task REQ209_Guess_Post_NameMatchesMultipleFittingCandidates_ReturnsOkWithCandidates_NotScored()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, sharedName, firstPlayerId, secondPlayerId) = await SeedRoundWithAmbiguousCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(sharedName));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Candidates, Is.Not.Null.And.Count.EqualTo(2), "Candidates != null is the frontend's signal to show the picker");
        Assert.That(body.Candidates!.Select(c => c.PlayerId), Is.EquivalentTo(new[] { firstPlayerId, secondPlayerId }));
        Assert.That(body.IsCorrect, Is.False, "nothing was actually scored yet");
        Assert.That(body.AttemptCount, Is.EqualTo(0), "REQ-210: showing the prompt must never consume an attempt");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var stored = await dbContext.Guesses.FirstOrDefaultAsync(g => g.RoundId == roundId && g.CellId == cellId && g.UserId == userId);
        Assert.That(stored, Is.Null, "REQ-210: showing the prompt must never persist a Guess row at all");
    }

    [Test]
    public async Task REQ210_Guess_Post_ValidChosenPlayerIdResubmission_ScoresCorrectly_AndConsumesExactlyOneAttempt()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, sharedName, firstPlayerId, _) = await SeedRoundWithAmbiguousCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);
        var prompt = await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(sharedName));
        Assert.That(prompt.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var promptBody = await prompt.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(promptBody!.Candidates, Is.Not.Null);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(sharedName, firstPlayerId));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.Candidates, Is.Null, "a resolved ChosenPlayerId response is scored normally, never another prompt");
        Assert.That(body.IsCorrect, Is.True);
        Assert.That(body.AttemptCount, Is.EqualTo(1),
            "REQ-210: resolving the prompt is part of the same attempt that triggered it, not a second one");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var stored = await dbContext.Guesses.SingleAsync(g => g.RoundId == roundId && g.CellId == cellId && g.UserId == userId);
        Assert.That(stored.AttemptCount, Is.EqualTo(1));
        Assert.That(stored.PlayerAnswerId, Is.EqualTo(firstPlayerId));
        Assert.That(stored.IsCorrect, Is.True);
    }

    [Test]
    public async Task REQ209_Guess_Post_InvalidChosenPlayerIdResubmission_ReturnsIncorrect_ConsumingAnAttempt()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        var (roundId, cellId, sharedName, _, _) = await SeedRoundWithAmbiguousCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(sharedName, Guid.NewGuid()));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(body!.Candidates, Is.Null);
        Assert.That(body.IsCorrect, Is.False);
        Assert.That(body.AttemptCount, Is.EqualTo(1), "an invalid ChosenPlayerId is a real scored guess, not free like the prompt itself");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var stored = await dbContext.Guesses.SingleAsync(g => g.RoundId == roundId && g.CellId == cellId && g.UserId == userId);
        Assert.That(stored.AttemptCount, Is.EqualTo(1));
        Assert.That(stored.IsCorrect, Is.False);
    }

    // ---- REQ-718/ADR-0038: a submitted guess is one of the four
    // LastActiveAt activity-tracking events, updated unconditionally
    // regardless of scoring outcome (see GuessEndpoints' own comment). ----

    [Test]
    public async Task REQ718_Guess_Post_UpdatesSubmittingUsersLastActiveAt()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        using (var seedScope = _factory.Services.CreateScope())
        {
            var seedDbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var seededUser = await seedDbContext.Users.SingleAsync(u => u.Id == userId);
            seededUser.LastActiveAt = DateTime.UtcNow.AddDays(-10);
            await seedDbContext.SaveChangesAsync();
        }
        var (roundId, cellId, correctAnswer) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var before = DateTime.UtcNow;
        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest(correctAnswer));
        var after = DateTime.UtcNow;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var reloaded = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.That(reloaded.LastActiveAt, Is.InRange(before, after));
    }

    // Even a rejected/incorrect guess still means the account genuinely
    // engaged with an active round — LastActiveAt updates unconditionally,
    // before the eventual scoring outcome is even known.
    [Test]
    public async Task REQ718_Guess_Post_UpdatesLastActiveAt_EvenForAnIncorrectGuess()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        using (var seedScope = _factory.Services.CreateScope())
        {
            var seedDbContext = seedScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var seededUser = await seedDbContext.Users.SingleAsync(u => u.Id == userId);
            seededUser.LastActiveAt = DateTime.UtcNow.AddDays(-10);
            await seedDbContext.SaveChangesAsync();
        }
        var (roundId, cellId, _) = await SeedRoundWithCellAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1), allowGuessChange: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var before = DateTime.UtcNow;
        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Definitely Not The Answer"));
        var after = DateTime.UtcNow;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var reloaded = await assertDbContext.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        Assert.That(reloaded.LastActiveAt, Is.InRange(before, after));
    }

    // ---- REQ-211 (2026-07-27 fix): live-lookup-unavailable outcome --------

    // Hand-rolled fake, not a mocking-framework double (docs/coding-
    // guidelines.md "don't over-mock") — overrides the real
    // WikidataLookupService for exactly one test below, simulating a
    // guess-time-fallback timeout without any real HTTP/timeout machinery.
    private sealed class ThrowingWikidataLookupService : IWikidataLookupService
    {
        public Task<IReadOnlyList<Player>> LookupAndPersistAsync(
            CountryDefinition country, ClubDefinition club, WikidataLookupOrigin origin, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null,
            WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            throw new WikidataQueryException("simulated Wikidata timeout");

        public Task<IReadOnlyList<Player>> LookupAndPersistClubClubAsync(
            ClubDefinition clubA, ClubDefinition clubB, WikidataLookupOrigin origin, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null,
            WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            throw new WikidataQueryException("simulated Wikidata timeout");

        public Task<IReadOnlyList<Player>> LookupAndPersistTrophyCountryAsync(
            TrophyDefinition trophy, CountryDefinition country, WikidataLookupOrigin origin, CancellationToken cancellationToken = default) =>
            throw new WikidataQueryException("simulated Wikidata timeout");

        public Task<IReadOnlyList<Player>> LookupAndPersistTrophyClubAsync(
            TrophyDefinition trophy, ClubDefinition club, WikidataLookupOrigin origin, CancellationToken cancellationToken = default) =>
            throw new WikidataQueryException("simulated Wikidata timeout");
    }

    // The full stack, end to end: GridGameModule catches WikidataQueryException
    // and re-throws LiveLookupUnavailableException (Core.Games);
    // GuessSubmissionService catches that and rejects with
    // GuessSubmissionOutcome.LiveLookupUnavailable; GuessEndpoints maps it to
    // a 503. Reproduces the bug bundle's reported "guessed Seedorf, got
    // 'failed to fetch', retried, got 'incorrect'" symptom being fixed, not
    // reproduced — this asserts the correct 503, never a scored "incorrect."
    [Test]
    public async Task REQ211_Guess_Post_LiveLookupTimesOut_ReturnsServiceUnavailable_AndPersistsNoGuessRow()
    {
        // A derived factory with its own separate in-memory database (same
        // "WithWebHostBuilder builds its own separate host" pattern as
        // GridEndpointTests/AdminAccountsEndpointTests) — swaps the real
        // WikidataLookupService for one that always throws
        // WikidataQueryException, so this test never depends on a real
        // Wikidata HTTP call.
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IWikidataLookupService>();
                services.AddScoped<IWikidataLookupService, ThrowingWikidataLookupService>();
            }));

        var authProviderUserId = Guid.NewGuid();
        var roundId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                AuthProviderUserId = authProviderUserId,
                Email = $"{authProviderUserId}@example.com",
                DisplayName = "Test Player",
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
            });

            dbContext.CountryDefinitions.Add(new CountryDefinition { Id = Guid.NewGuid(), Name = "France", WikidataQid = "Q142" });
            dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = "Arsenal", WikidataQid = "Q9617" });
            // REQ-211's PlayerNameIndex gate (2026-07-27 fix) must pass for
            // the live-lookup fallback to be attempted at all — without
            // this row, ScoreSubmissionAsync would return a plain incorrect
            // result before ever reaching the (throwing) lookup service.
            dbContext.PlayerNameIndexEntries.Add(new PlayerNameIndex
            {
                PlayerId = Guid.NewGuid(),
                PrimaryName = "Clarence Seedorf",
                NormalizedName = PlayerNameNormalizer.Normalize("Clarence Seedorf"),
            });

            var instanceId = Guid.NewGuid();
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

            // Deliberately no cached PlayerAttribute rows at all for this
            // cell — a genuine cache miss, forcing ScoreSubmissionAsync down
            // the live-lookup fallback path.
            dbContext.Rounds.Add(new Round
            {
                Id = roundId,
                GameKey = GridGameModule.XGGridGameKey,
                GameInstanceId = instanceId,
                StartTime = DateTime.UtcNow.AddDays(-1),
                EndTime = DateTime.UtcNow.AddDays(1),
                AllowGuessChange = true,
            });

            await dbContext.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/guesses", new SubmitGuessRequest("Clarence Seedorf"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));

        using var assertScope = factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var stored = await assertDbContext.Guesses.SingleOrDefaultAsync(g => g.RoundId == roundId && g.CellId == cellId);
        Assert.That(stored, Is.Null,
            "a live-lookup-unavailable outcome must never persist a Guess row — the player gets a genuine retry, not a wasted one");
    }
}
