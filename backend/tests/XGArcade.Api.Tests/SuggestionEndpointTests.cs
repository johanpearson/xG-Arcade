using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Suggestions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGGrid;
using XGArcade.Games.XGPath;

namespace XGArcade.Api.Tests;

// S-089 (docs/backlog.md): API-level coverage for POST
// /rounds/{roundId}/cells/{cellId}/suggestions — REQ-215's submission-only
// half (ADR-0052). Same in-memory-DbContext-swap/local-e2e-auth pattern as
// GuessEndpointTests, which this file otherwise mirrors closely (including
// its {roundId}/{cellId} seeding shape) since SuggestionEndpoints.cs itself
// says it mirrors GuessEndpoints's route shape and conventions.
public class SuggestionEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Same reasoning as GuessEndpointTests' SetUp: avoid a real
                // Supabase JWKS fetch in unit/API tests.
                builder.UseSetting("Auth:Mode", "local-e2e");

                builder.ConfigureServices(services =>
                {
                    // Same "remove every XGArcadeDbContext-closed descriptor,
                    // not just the two obvious ones" pattern as every other
                    // file in this project — see AuthEndpointTests' SetUp.
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

    private async Task<Guid> SeedUserAsync(Guid authProviderUserId, bool isGuest = false)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = isGuest ? null : $"{authProviderUserId}@example.com",
            DisplayName = isGuest ? $"Guest{Guid.NewGuid():N}"[..12] : "Test Player",
            EmailConfirmed = !isGuest,
            IsGuest = isGuest,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    // Seeds a Round backed by a single-cell GridInstance — same shape as
    // GuessEndpointTests.SeedRoundWithCellAsync, minus the correct-answer
    // Player seeding this endpoint never reads.
    private async Task<(Guid RoundId, Guid CellId)> SeedRoundWithCellAsync()
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
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(1),
            AllowGuessChange = true,
        };
        dbContext.Rounds.Add(round);

        await dbContext.SaveChangesAsync();
        return (round.Id, cellId);
    }

    // Seeds a Round backed by a single-puzzle PathInstance — proves
    // SuggestionEndpoints' cell-category-type resolution genuinely
    // dispatches by Round.GameKey through IGameModuleResolver (architecture-
    // review fix, post-S-089) rather than a hardcoded Grid-only
    // IGridInstanceRepository/GridCell read, which could never even resolve
    // a PathPuzzle id in the first place. Same PathInstance/PathPuzzle shape
    // as PathEndpointTests.SeedPathRoundAsync, trimmed to what this file's
    // one polymorphic-dispatch test needs (no clue/career-stint data).
    private async Task<(Guid RoundId, Guid PuzzleId)> SeedXGPathRoundWithPuzzleAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var targetPlayer = new Player { Id = Guid.NewGuid(), FullName = "Kylian Mbappe", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
        dbContext.Players.Add(targetPlayer);

        var instanceId = Guid.NewGuid();
        var puzzleId = Guid.NewGuid();
        dbContext.PathInstances.Add(new PathInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Puzzles = [new PathPuzzle { Id = puzzleId, PathInstanceId = instanceId, TargetPlayerId = targetPlayer.Id }],
        });

        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = XGPathGameModule.XGPathGameKey,
            GameInstanceId = instanceId,
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(1),
            AllowGuessChange = true,
        };
        dbContext.Rounds.Add(round);

        await dbContext.SaveChangesAsync();
        return (round.Id, puzzleId);
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    private static SubmitSuggestionRequest ValidRequest() =>
        new("Thierry Henry", ["Arsenal", "Monaco"], "France");

    // ---- REQ-215: unauthenticated / not-found guardrails -------------------

    [Test]
    public async Task REQ215_Suggestion_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/rounds/{Guid.NewGuid()}/cells/{Guid.NewGuid()}/suggestions", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ215_Suggestion_Post_ReturnsUnauthorized_ForTokenWithNoMatchingLocalUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsJsonAsync(
            $"/rounds/{Guid.NewGuid()}/cells/{Guid.NewGuid()}/suggestions", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ215_Suggestion_Post_ReturnsNotFound_ForUnknownCellId()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, _) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{Guid.NewGuid()}/suggestions", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-215: guest rejection is server-side, not just a disabled UI --

    [Test]
    public async Task REQ215_Suggestion_Post_GuestAccount_ReturnsForbidden_EvenWithAWellFormedRequest()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, isGuest: true);
        var (roundId, cellId) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/suggestions", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Guest accounts cannot submit suggestions"));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await assertDbContext.PlayerSuggestions.CountAsync(), Is.EqualTo(0),
            "a rejected guest submission must never persist a suggestion row");
    }

    // ---- REQ-215: validation — playerName / clubs / nationality -----------

    [TestCase("")]
    [TestCase("   ")]
    public async Task REQ215_Suggestion_Post_ReturnsBadRequest_ForEmptyOrWhitespacePlayerName(string playerName)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/suggestions",
            new SubmitSuggestionRequest(playerName, ["Arsenal"], "France"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("A player name is required"));
    }

    [Test]
    public async Task REQ215_Suggestion_Post_ReturnsBadRequest_ForEmptyClubsArray()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/suggestions",
            new SubmitSuggestionRequest("Thierry Henry", [], "France"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("At least one club is required"));
    }

    [Test]
    public async Task REQ215_Suggestion_Post_ReturnsBadRequest_ForClubsArrayOfOnlyBlankEntries()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/suggestions",
            new SubmitSuggestionRequest("Thierry Henry", ["", "   "], "France"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("At least one club is required"),
            "blank-only entries in the array don't count as a real club");
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task REQ215_Suggestion_Post_ReturnsBadRequest_ForEmptyOrWhitespaceNationality(string nationality)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/suggestions",
            new SubmitSuggestionRequest("Thierry Henry", ["Arsenal"], nationality));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("A nationality is required"));
    }

    // ---- REQ-215: happy path — persisted Pending, correct response --------

    [Test]
    public async Task REQ215_Suggestion_Post_ValidRequestFromNonGuest_ReturnsCreated_AndPersistsPendingRow()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        var (roundId, cellId) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/suggestions", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var body = await response.Content.ReadFromJsonAsync<SubmitSuggestionResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.PlayerName, Is.EqualTo("Thierry Henry"));
        Assert.That(body.AssertedClubs, Is.EquivalentTo(new[] { "Arsenal", "Monaco" }));
        Assert.That(body.AssertedNationality, Is.EqualTo("France"));
        Assert.That(body.Status, Is.EqualTo(nameof(PlayerSuggestionStatus.Pending)));
        Assert.That(response.Headers.Location!.ToString(),
            Is.EqualTo($"/rounds/{roundId}/cells/{cellId}/suggestions/{body.Id}"));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var stored = await assertDbContext.PlayerSuggestions
            .Include(s => s.AssertedClubs)
            .SingleAsync(s => s.Id == body.Id);
        Assert.That(stored.PlayerName, Is.EqualTo("Thierry Henry"));
        Assert.That(stored.AssertedNationality, Is.EqualTo("France"));
        Assert.That(stored.AssertedClubs.Select(c => c.ClubName), Is.EquivalentTo(new[] { "Arsenal", "Monaco" }));
        Assert.That(stored.SubmittingUserId, Is.EqualTo(userId));
        Assert.That(stored.CellId, Is.EqualTo(cellId));
        Assert.That(stored.RoundId, Is.EqualTo(roundId));
        Assert.That(stored.Status, Is.EqualTo(PlayerSuggestionStatus.Pending));
    }

    [Test]
    public async Task REQ215_Suggestion_Post_TrimsWhitespaceFromClubNames_BeforePersisting()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/suggestions",
            new SubmitSuggestionRequest("Thierry Henry", [" Arsenal ", "", "  Monaco"], "France"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var body = await response.Content.ReadFromJsonAsync<SubmitSuggestionResponse>();
        Assert.That(body!.AssertedClubs, Is.EquivalentTo(new[] { "Arsenal", "Monaco" }));
    }

    // ---- REQ-215/ADR-0052 boundary: submission must never touch the
    // correctness-checking or autocomplete tables --------------------------

    [Test]
    public async Task REQ215_Suggestion_Post_ValidSubmission_WritesNothingToPlayerAttribute_PlayerOverride_OrPlayerNameIndex()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/suggestions", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await assertDbContext.PlayerAttributes.CountAsync(), Is.EqualTo(0),
            "REQ-215 + ADR-0052: a submitted suggestion must never write PlayerAttribute — it is a pending human claim, not a verified fact");
        Assert.That(await assertDbContext.PlayerOverrides.CountAsync(), Is.EqualTo(0),
            "REQ-215 + ADR-0052: a submitted suggestion must never write PlayerOverride");
        Assert.That(await assertDbContext.PlayerNameIndexEntries.CountAsync(), Is.EqualTo(0),
            "REQ-215 + ADR-0052: a submitted suggestion must never write PlayerNameIndex — the autocomplete/correctness boundary is never crossed by submission alone");
        // And, per the same story's "no retroactive rescoring" decision,
        // no Guess row is touched by this endpoint at all either.
        Assert.That(await assertDbContext.Guesses.CountAsync(), Is.EqualTo(0),
            "submitting a suggestion is a data-correction proposal only — it never writes to or rescores the triggering Guess");
    }

    // ---- REQ-215: the persisted category types come from the real cell,
    // never from anything the client could send (there's no such field on
    // SubmitSuggestionRequest at all) ---------------------------------------

    [Test]
    public async Task REQ215_Suggestion_Post_PersistsRowAndColCategoryTypes_FromTheSeededCellServerSide()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, cellId) = await SeedRoundWithCellAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{cellId}/suggestions", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var body = await response.Content.ReadFromJsonAsync<SubmitSuggestionResponse>();

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var stored = await assertDbContext.PlayerSuggestions.SingleAsync(s => s.Id == body!.Id);
        Assert.That(stored.RowCategoryType, Is.EqualTo(CategoryPairingRules.Country),
            "must match the seeded cell's real RowCategoryType, not anything the client sent — SubmitSuggestionRequest has no such field");
        Assert.That(stored.ColCategoryType, Is.EqualTo(CategoryPairingRules.Club),
            "must match the seeded cell's real ColCategoryType, not anything the client sent — SubmitSuggestionRequest has no such field");
    }

    // ---- REQ-215/ADR-0052 (S-089, architecture-review fix): category-type
    // resolution genuinely dispatches through Round.GameKey/IGameModule,
    // never a hardcoded Grid-only lookup ------------------------------------

    [Test]
    public async Task REQ215_Suggestion_Post_ForAnXGPathKeyedRound_ResolvesThroughGameModuleResolver_NotAHardcodedGridOnlyLookup()
    {
        // Before this fix, this endpoint queried IGridInstanceRepository.
        // GetCellByIdAsync directly, which only ever reads GridCells and so
        // could never resolve a real PathPuzzle id — every xg-path round
        // would unconditionally 404 here regardless of whether puzzleId was
        // genuinely valid. After the fix, a real xg-path round's puzzleId
        // instead reaches XGPathGameModule.GetCellCategoryTypesAsync, which
        // deliberately throws NotSupportedException (see that method's own
        // doc comment) — a different, distinguishable outcome that can only
        // happen if resolution genuinely went through Round.GameKey ->
        // IGameModuleResolver.Resolve rather than a hardcoded Grid-only
        // path. Same "endpoint's own try/catch doesn't handle this exception
        // type, so it falls through to ASP.NET's default 500" shape
        // RoundEndpointTests.GenerateRound_Post_ReturnsProblemDetails_
        // WhenAnUnexpectedExceptionOccurs already establishes for this test
        // host.
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, puzzleId) = await SeedXGPathRoundWithPuzzleAsync();
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsJsonAsync(
            $"/rounds/{roundId}/cells/{puzzleId}/suggestions", ValidRequest());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));

        using var assertScope = _factory.Services.CreateScope();
        var assertDbContext = assertScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await assertDbContext.PlayerSuggestions.CountAsync(), Is.EqualTo(0),
            "a rejected/failed resolution must never persist a suggestion row");
    }
}
