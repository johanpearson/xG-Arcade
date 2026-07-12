using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Guesses;
using XGArcade.Api.Rounds;
using XGArcade.Core.Scoring;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Tests;

// S-010 (docs/backlog.md): API-level coverage for GET /rounds/current
// (REQ-303) — the read path the Grid UI uses to open the active round
// without already knowing its id. Same in-memory-DbContext-swap pattern as
// GuessEndpointTests (this project's established convention, not shared via
// a base class).
public class CurrentRoundEndpointTests
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

    // Seeds a Round (GameKey/GameInstanceId only, per ADR-0003) backed by a
    // two-cell GridInstance directly — enough to exercise the read endpoint
    // without depending on real grid generation.
    private async Task<(Guid RoundId, Guid FirstCellId, Guid SecondCellId)> SeedRoundWithCellsAsync(
        DateTime startTime, DateTime endTime, bool allowGuessChange = true)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var instanceId = Guid.NewGuid();
        var firstCellId = Guid.NewGuid();
        var secondCellId = Guid.NewGuid();
        dbContext.GridInstances.Add(new GridInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Cells =
            [
                new GridCell
                {
                    Id = firstCellId,
                    GridInstanceId = instanceId,
                    Row = 0,
                    Col = 0,
                    RowCategoryType = CategoryPairingRules.Country,
                    RowCategoryValue = "France",
                    ColCategoryType = CategoryPairingRules.Club,
                    ColCategoryValue = "Arsenal",
                },
                new GridCell
                {
                    Id = secondCellId,
                    GridInstanceId = instanceId,
                    Row = 0,
                    Col = 1,
                    RowCategoryType = CategoryPairingRules.Country,
                    RowCategoryValue = "France",
                    ColCategoryType = CategoryPairingRules.Club,
                    ColCategoryValue = "Barcelona",
                },
            ],
        });

        var player = new Player { Id = Guid.NewGuid(), FullName = "Thierry Henry", WikidataQid = $"Qplayer-{Guid.NewGuid()}" };
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
        return (round.Id, firstCellId, secondCellId);
    }

    // REQ-204: seeds a second player's already-correct Guess directly into
    // the DbContext, bypassing GuessSubmissionService/POST entirely — this
    // endpoint's uniqueness calculation reads straight from the Guess table
    // (GetCorrectByCellIdsAsync), so it doesn't matter how the row got
    // there, only that IsCorrect/PlayerAnswerId are set. Same
    // seed-directly-via-DbContext pattern SeedUserAsync/SeedRoundWithCellsAsync
    // already use in this file.
    private async Task SeedCorrectGuessDirectlyAsync(Guid roundId, Guid cellId, Guid userId, Guid playerAnswerId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        dbContext.Guesses.Add(new Guess
        {
            Id = Guid.NewGuid(),
            RoundId = roundId,
            UserId = userId,
            CellId = cellId,
            SubmittedName = "Someone Else",
            PlayerAnswerId = playerAnswerId,
            IsCorrect = true,
            AttemptCount = 1,
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

    // ---- Auth guardrails ------------------------------------------------

    [Test]
    public async Task CurrentRound_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/rounds/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task CurrentRound_Get_ReturnsUnauthorized_ForTokenWithNoMatchingLocalUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/rounds/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- REQ-303: fetch the active round and grid for display -------------

    [Test]
    public async Task REQ303_CurrentRound_Get_ReturnsNotFound_WhenNoActiveRoundExists()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/rounds/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("No active round"));
    }

    [Test]
    public async Task REQ303_CurrentRound_Get_IgnoresUpcomingRound_ReturnsOnlyTheActiveOne()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (activeRoundId, _, _) = await SeedRoundWithCellsAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        // REQ-301 generation runs one round ahead — an upcoming round for the
        // same game key must never be mistaken for the one a player can play.
        await SeedRoundWithCellsAsync(DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(5));
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/rounds/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<CurrentRoundResponse>();
        Assert.That(body!.RoundId, Is.EqualTo(activeRoundId));
    }

    [Test]
    public async Task REQ303_CurrentRound_Get_ReturnsAllCells_WithCategoryValues()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, firstCellId, secondCellId) = await SeedRoundWithCellsAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/rounds/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<CurrentRoundResponse>();
        Assert.That(body!.RoundId, Is.EqualTo(roundId));
        Assert.That(body.Cells, Has.Count.EqualTo(2));
        var first = body.Cells.Single(c => c.CellId == firstCellId);
        Assert.That(first.RowCategoryType, Is.EqualTo(CategoryPairingRules.Country));
        Assert.That(first.RowCategoryValue, Is.EqualTo("France"));
        Assert.That(first.ColCategoryType, Is.EqualTo(CategoryPairingRules.Club));
        Assert.That(first.ColCategoryValue, Is.EqualTo("Arsenal"));
        Assert.That(first.Guess, Is.Null, "an unattempted cell must carry no guess state");
        Assert.That(body.Cells.Single(c => c.CellId == secondCellId).Guess, Is.Null);
    }

    [Test]
    public async Task REQ303_CurrentRound_Get_ReflectsRequestingPlayersOwnGuessState_NotOtherPlayers()
    {
        var firstAuthProviderUserId = Guid.NewGuid();
        var secondAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(firstAuthProviderUserId);
        await SeedUserAsync(secondAuthProviderUserId);
        var (roundId, firstCellId, secondCellId) = await SeedRoundWithCellsAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var firstClient = CreateAuthenticatedClient(firstAuthProviderUserId);
        var secondClient = CreateAuthenticatedClient(secondAuthProviderUserId);

        // First player: one wrong guess on the first cell (locked=false,
        // attempts=1). Second player: never guesses anything.
        await firstClient.PostAsJsonAsync($"/rounds/{roundId}/cells/{firstCellId}/guesses", new SubmitGuessRequest("Wrong Guess"));

        var firstResponse = await firstClient.GetAsync("/rounds/current");
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<CurrentRoundResponse>();
        var firstPlayersCell = firstBody!.Cells.Single(c => c.CellId == firstCellId);
        Assert.That(firstPlayersCell.Guess, Is.Not.Null);
        Assert.That(firstPlayersCell.Guess!.IsCorrect, Is.False);
        Assert.That(firstPlayersCell.Guess.AttemptCount, Is.EqualTo(1));
        Assert.That(firstPlayersCell.Guess.Locked, Is.False, "one wrong attempt out of two must not lock the cell");
        Assert.That(firstPlayersCell.Guess.SubmittedName, Is.EqualTo("Wrong Guess"),
            "REQ-303: the submitted name must round-trip so the UI can redisplay it after a reload");
        Assert.That(firstBody.Cells.Single(c => c.CellId == secondCellId).Guess, Is.Null);

        var secondResponse = await secondClient.GetAsync("/rounds/current");
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<CurrentRoundResponse>();
        Assert.That(secondBody!.Cells.Single(c => c.CellId == firstCellId).Guess, Is.Null,
            "REQ-303: a response must never reveal another player's guess");
    }

    [Test]
    public async Task REQ303_CurrentRound_Get_MultipleActiveRoundsForSameGame_DeterministicallyReturnsMostRecentlyStarted()
    {
        // Leftover data (e.g. a stale local/test database) can leave more
        // than one Active round for the same GameKey even though REQ-301's
        // steady-state scheduling never produces that — this must still
        // resolve deterministically rather than depending on row order.
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (olderRoundId, _, _) = await SeedRoundWithCellsAsync(
            DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddDays(3));
        var (newerRoundId, _, _) = await SeedRoundWithCellsAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/rounds/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<CurrentRoundResponse>();
        Assert.That(body!.RoundId, Is.EqualTo(newerRoundId));
        Assert.That(body.RoundId, Is.Not.EqualTo(olderRoundId));
    }

    [Test]
    public async Task REQ303_CurrentRound_Get_CorrectGuess_ReportsLockedTrue()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, firstCellId, _) = await SeedRoundWithCellsAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{firstCellId}/guesses", new SubmitGuessRequest("Thierry Henry"));

        var response = await client.GetAsync("/rounds/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentRoundResponse>();
        var guessedCell = body!.Cells.Single(c => c.CellId == firstCellId);
        Assert.That(guessedCell.Guess!.IsCorrect, Is.True);
        Assert.That(guessedCell.Guess.Locked, Is.True, "REQ-210: a correct guess locks the cell immediately, even with attempts remaining");
    }

    // ---- REQ-204: live unique_percent --------------------------------------

    [Test]
    public async Task REQ204_CurrentRound_Get_CorrectGuessWithNoOtherCorrectGuessesOnCell_ReturnsUniquePercentZero()
    {
        // A lone correct guesser is the entire correct-guess population for
        // that cell (1/1) — per the REQ-204 formula that's 0% unique, which
        // is intended, not a bug (see UniquenessCalculator's own tests).
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, firstCellId, _) = await SeedRoundWithCellsAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{firstCellId}/guesses", new SubmitGuessRequest("Thierry Henry"));

        var response = await client.GetAsync("/rounds/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentRoundResponse>();
        var guessedCell = body!.Cells.Single(c => c.CellId == firstCellId);
        Assert.That(guessedCell.Guess!.IsCorrect, Is.True);
        Assert.That(guessedCell.Guess.UniquePercent, Is.EqualTo(0.0));
        // S-018: LivePoints follows the same round(uniqueScore *
        // MaxPointsPerCell) formula REQ-205 locks at round close — 0%
        // unique must live-estimate to 0 points, not null.
        Assert.That(guessedCell.Guess.LivePoints, Is.EqualTo((int)Math.Round(0.0 * ScoringRules.MaxPointsPerCell)));
    }

    [Test]
    public async Task REQ204_CurrentRound_Get_SecondPlayerCorrectlyGuessedDifferentAnswerOnSameCell_ReturnsUniquePercentHalfForBoth()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        var (roundId, firstCellId, _) = await SeedRoundWithCellsAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{firstCellId}/guesses", new SubmitGuessRequest("Thierry Henry"));

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var myGuess = await dbContext.Guesses.SingleAsync(g => g.RoundId == roundId && g.CellId == firstCellId && g.UserId == userId);
            Assert.That(myGuess.PlayerAnswerId, Is.Not.Null);

            // Another player who correctly guessed a *different* fitting
            // player for the same cell — REQ-204's denominator counts only
            // correct guesses, one per player, so this makes 2 correct
            // guesses total, each with a distinct answer.
            await SeedCorrectGuessDirectlyAsync(roundId, firstCellId, Guid.NewGuid(), Guid.NewGuid());
        }

        var response = await client.GetAsync("/rounds/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentRoundResponse>();
        var guessedCell = body!.Cells.Single(c => c.CellId == firstCellId);
        Assert.That(guessedCell.Guess!.IsCorrect, Is.True);
        Assert.That(guessedCell.Guess.UniquePercent, Is.EqualTo(0.5));
        // S-018: this scenario's LivePoints must equal exactly
        // round(uniqueScore * ScoringRules.MaxPointsPerCell), referencing
        // the real constant (not hardcoding 100) so the assertion can't
        // silently drift from REQ-205's locked-score formula. 0.5 unique
        // resolves to 50 (round(0.5 * 100)).
        Assert.That(guessedCell.Guess.LivePoints, Is.EqualTo((int)Math.Round(guessedCell.Guess.UniquePercent!.Value * ScoringRules.MaxPointsPerCell)));
        Assert.That(guessedCell.Guess.LivePoints, Is.EqualTo(50));
    }

    [Test]
    public async Task REQ204_CurrentRound_Get_IncorrectGuess_ReturnsUniquePercentNull()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, firstCellId, _) = await SeedRoundWithCellsAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{firstCellId}/guesses", new SubmitGuessRequest("Wrong Guess"));

        var response = await client.GetAsync("/rounds/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentRoundResponse>();
        var guessedCell = body!.Cells.Single(c => c.CellId == firstCellId);
        Assert.That(guessedCell.Guess!.IsCorrect, Is.False);
        Assert.That(guessedCell.Guess.UniquePercent, Is.Null,
            "an incorrect guess has no real answer to measure rarity against");
        // S-018: LivePoints is null exactly when UniquePercent is — an
        // incorrect guess has no uniqueness fraction to estimate points from.
        Assert.That(guessedCell.Guess.LivePoints, Is.Null,
            "LivePoints must be null whenever UniquePercent is, per its own doc comment on CurrentRoundGuessResponse");
    }
}
