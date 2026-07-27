using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Guesses;
using XGArcade.Api.Path;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGPath;

namespace XGArcade.Api.Tests;

// REQ-1203/S-082: API-level coverage for GET /path/current — the
// client-facing read path for the active xg-path round's puzzles and their
// progressively-revealed clue turns (PathEndpoints' own doc comment: mirrors
// XGArcade.Api.Rounds.RoundEndpoints's GET /rounds/current shape closely).
// Same in-memory-DbContext-swap/local-e2e-auth pattern as
// CurrentRoundEndpointTests (this project's established convention).
//
// REQ-1203's own "Test level" note in docs/requirements-document.md scopes
// this REQ to Unit only — the detailed split/appearance-count/chronological-
// order/year-range-content/halt-on-correct-guess coverage lives in
// PathClueSequenceBuilderTests (XGArcade.Games.XGPath.Tests), which is
// DB-free and exhaustive. This file covers only what an API-level test adds
// on top: that GET /path/current actually wires PathClueSequenceBuilder's
// output through auth, round resolution, and per-puzzle guess state
// end-to-end — the same "mirrors GET /rounds/current" claim PathEndpoints'
// own doc comment makes, proven by test rather than by inspection alone.
public class PathEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Same reasoning as CurrentRoundEndpointTests' own comment:
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

    // Seeds one PathInstance/PathPuzzle whose target is a player with three
    // career stints (REQ-1201's minimum N=3, splitting 1-1-1 per REQ-1203) —
    // enough to exercise the full 7-turn reveal sequence deterministically.
    // position/nationality/birthYear default to null ("not available",
    // REQ-1207) unless a test opts in via the optional params.
    private async Task<(Guid RoundId, Guid PuzzleId, Guid TargetPlayerId)> SeedPathRoundAsync(
        DateTime startTime, DateTime endTime, bool allowGuessChange = true,
        string? position = null, string? nationality = null, int? birthYear = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var targetPlayer = new Player
        {
            Id = Guid.NewGuid(),
            FullName = "Kylian Mbappe",
            WikidataQid = $"Qplayer-{Guid.NewGuid()}",
            Position = position,
            BirthYear = birthYear,
        };
        dbContext.Players.Add(targetPlayer);
        if (nationality is not null)
        {
            dbContext.PlayerAttributes.Add(new PlayerAttribute
            {
                PlayerId = targetPlayer.Id, AttributeType = "nationality", AttributeValue = nationality,
            });
        }
        dbContext.PlayerCareerStints.AddRange(
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = targetPlayer.Id, ClubName = "AS Monaco", StartYear = 2015, EndYear = 2017, SequenceOrder = 0, AppearanceCount = 60 },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = targetPlayer.Id, ClubName = "Paris Saint-Germain", StartYear = 2017, EndYear = 2024, SequenceOrder = 1, AppearanceCount = null },
            new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = targetPlayer.Id, ClubName = "Real Madrid", StartYear = 2024, EndYear = null, SequenceOrder = 2, AppearanceCount = 10 });

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
            StartTime = startTime,
            EndTime = endTime,
            AllowGuessChange = allowGuessChange,
        };
        dbContext.Rounds.Add(round);

        await dbContext.SaveChangesAsync();
        return (round.Id, puzzleId, targetPlayer.Id);
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth guardrails ------------------------------------------------

    [Test]
    public async Task PathCurrent_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/path/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task PathCurrent_Get_ReturnsUnauthorized_ForTokenWithNoMatchingLocalUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.GetAsync("/path/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- REQ-1203: fetch the active round and its progressively-revealed --
    // clue sequence -----------------------------------------------------

    [Test]
    public async Task REQ1203_PathCurrent_Get_ReturnsNotFound_WhenNoActiveRoundExists()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/path/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("No active round"));
    }

    [Test]
    public async Task REQ1203_PathCurrent_Get_UnattemptedPuzzle_ReturnsOnlyTheFirstClubRevealTurn()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, puzzleId, _) = await SeedPathRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/path/current");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<CurrentPathResponse>();
        Assert.That(body!.RoundId, Is.EqualTo(roundId));
        var puzzle = body.Puzzles.Single(p => p.PuzzleId == puzzleId);
        Assert.That(puzzle.Clues, Has.Count.EqualTo(1), "turn 1 is visible before any guess — REQ-1203's GetRevealedTurnCount(0, false)");
        Assert.That(puzzle.Clues[0].TurnNumber, Is.EqualTo(1));
        Assert.That(puzzle.Clues[0].Kind, Is.EqualTo("ClubReveal"));
        Assert.That(puzzle.Clues[0].Clubs!.Select(c => c.ClubName), Is.EqualTo(new[] { "AS Monaco" }));
        Assert.That(puzzle.Clues[0].Clubs![0].AppearanceCount, Is.EqualTo(60));
        Assert.That(puzzle.Guess, Is.Null, "an unattempted puzzle must carry no guess state");
    }

    [Test]
    public async Task REQ1203_PathCurrent_Get_AfterOneWrongGuess_RevealsExactlyTwoTurns()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, puzzleId, _) = await SeedPathRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{puzzleId}/guesses", new SubmitGuessRequest("Nobody Real"));

        var response = await client.GetAsync("/path/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentPathResponse>();
        var puzzle = body!.Puzzles.Single(p => p.PuzzleId == puzzleId);
        Assert.That(puzzle.Clues.Select(c => c.TurnNumber), Is.EqualTo(new[] { 1, 2 }));
        Assert.That(puzzle.Clues[1].Clubs!.Select(c => c.ClubName), Is.EqualTo(new[] { "Paris Saint-Germain" }));
        Assert.That(puzzle.Guess!.IsCorrect, Is.False);
        Assert.That(puzzle.Guess.AttemptCount, Is.EqualTo(1));
        Assert.That(puzzle.Guess.Locked, Is.False, "one wrong attempt out of seven must not lock the puzzle");
        Assert.That(puzzle.Guess.ResolvedPlayerName, Is.Null, "an incorrect guess never reveals the target's identity");
    }

    [Test]
    public async Task REQ1203_PathCurrent_Get_CorrectGuessOnFirstAttempt_LocksImmediately_AndRevealsNoFurtherTurn()
    {
        // REQ-1203: "a correct guess submitted at any point stops the
        // reveal sequence immediately — no further clue is ever revealed
        // once the puzzle is solved." At the API level, this means the
        // response must show exactly ONE turn (what was visible when the
        // winning guess was submitted), never two.
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, puzzleId, targetPlayerId) = await SeedPathRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);
        await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{puzzleId}/guesses", new SubmitGuessRequest("Kylian Mbappe"));

        var response = await client.GetAsync("/path/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentPathResponse>();
        var puzzle = body!.Puzzles.Single(p => p.PuzzleId == puzzleId);
        Assert.That(puzzle.Guess!.IsCorrect, Is.True);
        Assert.That(puzzle.Guess.Locked, Is.True, "REQ-210's immediate lock, mirrored for xG Path");
        Assert.That(puzzle.Guess.ResolvedPlayerName, Is.EqualTo("Kylian Mbappe"));
        Assert.That(puzzle.Clues, Has.Count.EqualTo(1),
            "must never reveal turn 2 just because the puzzle is now solved — the sequence halts at what was visible when the winning guess was made");
        Assert.That(targetPlayerId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task REQ1205_PathCurrent_Get_SevenWrongGuesses_LocksAsUnsolved_RevealingAllSevenTurns_NeverGridsFixedCapOfTwo()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var (roundId, puzzleId, _) = await SeedPathRoundAsync(
            DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1),
            position: "Forward", nationality: "France", birthYear: 1998);
        var client = CreateAuthenticatedClient(authProviderUserId);

        for (var attempt = 0; attempt < 7; attempt++)
        {
            var response = await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{puzzleId}/guesses", new SubmitGuessRequest($"Nobody Real {attempt}"));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), $"attempt {attempt + 1} of 7 must still be accepted — REQ-1205's cap is 7, not xG Grid's 2");
        }

        var getResponse = await client.GetAsync("/path/current");

        var body = await getResponse.Content.ReadFromJsonAsync<CurrentPathResponse>();
        var puzzle = body!.Puzzles.Single(p => p.PuzzleId == puzzleId);
        Assert.That(puzzle.Guess!.AttemptCount, Is.EqualTo(7));
        Assert.That(puzzle.Guess.IsCorrect, Is.False);
        Assert.That(puzzle.Guess.Locked, Is.True, "exhausting the puzzle's own 7-attempt cap locks it as unsolved");
        Assert.That(puzzle.Clues, Has.Count.EqualTo(7), "every one of the fixed 7 turns must be visible once the cap is exhausted");
        Assert.That(puzzle.Clues.Select(c => c.TurnNumber), Is.EqualTo(Enumerable.Range(1, 7)));

        var yearRangeTurn = puzzle.Clues.Single(c => c.Kind == "YearRange");
        Assert.That(yearRangeTurn.YearRanges, Is.EqualTo(new[] { "2015-17", "2017-24", "2024-present" }));
        var positionTurn = puzzle.Clues.Single(c => c.Kind == "Position");
        Assert.That(positionTurn.TextValue, Is.EqualTo("Forward"));
        var nationalityTurn = puzzle.Clues.Single(c => c.Kind == "Nationality");
        Assert.That(nationalityTurn.TextValue, Is.EqualTo("France"));
        var ageTurn = puzzle.Clues.Single(c => c.Kind == "Age");
        Assert.That(ageTurn.TextValue, Is.EqualTo("1998"));
    }

    [Test]
    public async Task REQ1207_PathCurrent_Get_NoPositionNationalityOrBirthYearData_RendersNotAvailable_NeverSkipsTheTurn()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        // position/nationality/birthYear all left null (the default).
        var (roundId, puzzleId, _) = await SeedPathRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var client = CreateAuthenticatedClient(authProviderUserId);
        for (var attempt = 0; attempt < 6; attempt++)
            await client.PostAsJsonAsync($"/rounds/{roundId}/cells/{puzzleId}/guesses", new SubmitGuessRequest($"Nobody Real {attempt}"));

        var response = await client.GetAsync("/path/current");

        var body = await response.Content.ReadFromJsonAsync<CurrentPathResponse>();
        var puzzle = body!.Puzzles.Single(p => p.PuzzleId == puzzleId);
        Assert.That(puzzle.Clues, Has.Count.EqualTo(7), "a data gap must never shrink the fixed 7-turn sequence");
        Assert.That(puzzle.Clues.Single(c => c.Kind == "Position").TextValue, Is.EqualTo("not available"));
        Assert.That(puzzle.Clues.Single(c => c.Kind == "Nationality").TextValue, Is.EqualTo("not available"));
        Assert.That(puzzle.Clues.Single(c => c.Kind == "Age").TextValue, Is.EqualTo("not available"));
    }

    [Test]
    public async Task REQ1203_PathCurrent_Get_NeverRevealsAnotherPlayersGuessState()
    {
        var firstAuthProviderUserId = Guid.NewGuid();
        var secondAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(firstAuthProviderUserId);
        await SeedUserAsync(secondAuthProviderUserId);
        var (roundId, puzzleId, _) = await SeedPathRoundAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));
        var firstClient = CreateAuthenticatedClient(firstAuthProviderUserId);
        var secondClient = CreateAuthenticatedClient(secondAuthProviderUserId);

        await firstClient.PostAsJsonAsync($"/rounds/{roundId}/cells/{puzzleId}/guesses", new SubmitGuessRequest("Kylian Mbappe"));

        var secondResponse = await secondClient.GetAsync("/path/current");

        var secondBody = await secondResponse.Content.ReadFromJsonAsync<CurrentPathResponse>();
        var secondPuzzle = secondBody!.Puzzles.Single(p => p.PuzzleId == puzzleId);
        Assert.That(secondPuzzle.Guess, Is.Null, "REQ-1203: a response must never reveal another player's guess");
        Assert.That(secondPuzzle.Clues, Has.Count.EqualTo(1), "another player's correct guess must not accelerate my own reveal sequence");
    }
}
