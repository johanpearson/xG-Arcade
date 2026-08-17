using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Rounds;
using XGArcade.Core.Games;
using XGArcade.Core.Rounds;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGGrid;
using XGArcade.Games.XGPath;

namespace XGArcade.Api.Tests;

// S-008 (docs/backlog.md): API-level coverage for POST /internal/generate-round
// (REQ-301) and POST /internal/test-data/force-close-round/{id} (REQ-806).
public class RoundEndpointTests
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
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Internal:JobToken"] = ValidJobToken,
                    });
                });

                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as GridEndpointTests — see
                    // that file's SetUp comment for why every
                    // XGArcadeDbContext-closed descriptor must be removed, not
                    // just the two obvious ones.
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

                    // MinValidAnswers=1 so a single seeded PlayerAttribute pair
                    // is enough — avoids depending on the real Wikidata HTTP
                    // client (same reasoning as GridEndpointTests.SetUp).
                    // GridSize=3 lives here now (S-084/REQ-1202 follow-up),
                    // not on RoundSchedulingOptions — see that type's own doc
                    // comment for why.
                    services.RemoveAll<GridGenerationOptions>();
                    services.AddSingleton(new GridGenerationOptions { MinValidAnswers = 1, MaxAttempts = 50, GridSize = 3 });

                    // A tiny round duration keeps REQ-301's "one round ahead"
                    // assertions (start-at-previous-round's-end-time) exact
                    // and fast without a special test-only branch in
                    // RoundGenerationService itself. RemoveAll<RoundSchedulingOptions>()
                    // removes BOTH Program.cs registrations (xg-grid's and
                    // xg-path's — S-084), and only xg-grid's is re-added
                    // below — fine, since every test in this class exercises
                    // "xg-grid" only (the endpoint's default gameKey when
                    // omitted); no test here calls generate-round with
                    // gameKey=xg-path, so an xg-path RoundSchedulingOptions
                    // being unregistered in this test factory is never
                    // exercised.
                    services.RemoveAll<RoundSchedulingOptions>();
                    services.AddSingleton(new RoundSchedulingOptions
                    {
                        GameKey = GridGameModule.XGGridGameKey,
                        RoundDuration = TimeSpan.FromDays(3),
                    });
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private async Task SeedFullyMatchedReferenceDataAsync(int size, WebApplicationFactory<Program>? factory = null)
    {
        using var scope = (factory ?? _factory).Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var countries = Enumerable.Range(0, size)
            .Select(i => new CountryDefinition { Id = Guid.NewGuid(), Name = $"Country{i}", WikidataQid = $"Qc{i}" })
            .ToList();
        var clubs = Enumerable.Range(0, size)
            .Select(i => new ClubDefinition { Id = Guid.NewGuid(), Name = $"Club{i}", WikidataQid = $"Qk{i}" })
            .ToList();
        dbContext.CountryDefinitions.AddRange(countries);
        dbContext.ClubDefinitions.AddRange(clubs);

        foreach (var country in countries)
        {
            foreach (var club in clubs)
            {
                var player = new Player { Id = Guid.NewGuid(), FullName = $"{country.Name}-{club.Name}", WikidataQid = $"Qp-{country.Name}-{club.Name}" };
                dbContext.Players.Add(player);
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = country.Name });
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = club.Name });
            }
        }

        await dbContext.SaveChangesAsync();
    }

    // S-084/REQ-1202: eligible xG Path target players (REQ-1201 — at least 3
    // ordered career stints, one at a seeded club) — mirrors
    // XGPathGameModuleTests.SeedEligiblePlayer's exact fixture shape (3
    // well-ordered stints, one at a seeded club) rather than reinventing it,
    // since that's the file that already established what "eligible" means
    // for this game at a fixture level.
    private async Task SeedEligiblePathPlayersAsync(int count, WebApplicationFactory<Program>? factory = null)
    {
        using var scope = (factory ?? _factory).Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        const string seededClubName = "Seeded FC";
        dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = seededClubName, WikidataQid = "Qclub-seeded-fc" });

        for (var i = 0; i < count; i++)
        {
            var player = new Player { Id = Guid.NewGuid(), FullName = $"Eligible Path Player {i}", WikidataQid = $"Qpathplayer-{i}-{Guid.NewGuid()}" };
            dbContext.Players.Add(player);
            dbContext.PlayerCareerStints.AddRange(
                new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = seededClubName, StartYear = 2010, EndYear = 2013, SequenceOrder = 0 },
                new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Some Unseeded Club", StartYear = 2013, EndYear = 2016, SequenceOrder = 1 },
                new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = "Another Unseeded Club", StartYear = 2016, EndYear = null, SequenceOrder = 2 });
        }

        await dbContext.SaveChangesAsync();
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);
        return client;
    }

    // ---- REQ-301: generate-round runs one round ahead ----------------------

    [Test]
    public async Task GenerateRound_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/internal/generate-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task GenerateRound_Post_ReturnsUnauthorized_WithWrongBearerToken()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-right-token");

        var response = await client.PostAsync("/internal/generate-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_CreatesFirstRound_WhenNoneExistYet()
    {
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/generate-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.GameKey, Is.EqualTo(GridGameModule.XGGridGameKey));
        Assert.That(body.EndTime - body.StartTime, Is.EqualTo(TimeSpan.FromDays(3)));
        // REQ-304: GenerateRoundResponse carries the round's SequenceNumber
        // alongside its unchanged RoundId — this is this GameKey's
        // first-ever round, so SequenceNumber must be 1 (the assignment
        // rule itself — MAX+1 scoped to GameKey — is covered independently
        // by RoundGenerationServiceTests; this only proves the DTO surfaces
        // the field).
        Assert.That(body.SequenceNumber, Is.EqualTo(1));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_IsIdempotent_WhenAnUpcomingRoundAlreadyExists()
    {
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();

        // Call 1: no round exists yet -> creates round 1, which starts
        // immediately (StartTime ~= now), so it's already active by the time
        // call 2 runs a moment later.
        var first = await client.PostAsync("/internal/generate-round", content: null);
        await first.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        // Call 2: round 1 is active with no round scheduled after it yet ->
        // correctly generates round 2 (the genuine "one round ahead" case),
        // which starts 3 days from now and is therefore still upcoming.
        var second = await client.PostAsync("/internal/generate-round", content: null);
        var secondBody = await second.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        // Call 3: round 2 already exists and hasn't started yet -> already
        // one round ahead, so this must be a no-op, not a round 3.
        var third = await client.PostAsync("/internal/generate-round", content: null);
        var thirdBody = await third.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        Assert.That(third.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(thirdBody!.RoundId, Is.EqualTo(secondBody!.RoundId),
            "already one round ahead — a third call must not create a third round");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_WithRoundDurationHoursOverride_UsesOverrideInsteadOfConfiguredDefault()
    {
        // SetUp's RoundSchedulingOptions.RoundDuration is 3 days (72h) — a
        // 24h override (ADR-0027's inclusive floor, still below the 72h
        // configured default) must win for this call, proving the query
        // param is actually plumbed through rather than merely accepted and
        // ignored.
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/generate-round?roundDurationHours=24", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.EndTime - body.StartTime, Is.EqualTo(TimeSpan.FromHours(24)));
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_WithZeroRoundDurationHours_ReturnsBadRequest()
    {
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/generate-round?roundDurationHours=0", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid roundDurationHours"));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.Zero, "an invalid override must not generate a round");
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_WithNegativeRoundDurationHours_ReturnsBadRequest()
    {
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/generate-round?roundDurationHours=-5", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid roundDurationHours"));
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_WithRoundDurationHoursBelow24Floor_ReturnsBadRequest()
    {
        // ADR-0027's 24h floor: distinct from the already-covered <=0 cases
        // above — this is a *positive* value that's still unsafe, because it
        // could let a round close before generate-round.yml's daily cron
        // fires again and generates its successor.
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/generate-round?roundDurationHours=23", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid roundDurationHours"));
        Assert.That(problem.Detail, Is.EqualTo(
            "roundDurationHours must be at least 24 (the daily cron's maximum gap — see ADR-0027) " +
            "to avoid a round closing before the next scheduled run can generate its successor."));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.Zero, "an invalid override must not generate a round");
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_WithRoundDurationHoursExactly24_IsAccepted()
    {
        // Boundary: ADR-0027's floor is inclusive (>= 24), so exactly 24
        // must succeed, not be rejected alongside 23.
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/generate-round?roundDurationHours=24", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.EndTime - body.StartTime, Is.EqualTo(TimeSpan.FromHours(24)));
    }

    [Test]
    public async Task REQ301_GenerateRound_Post_UsesRoundSchedulingRoundDurationHoursFromConfiguration_WhenNoOverrideSupplied()
    {
        // Every other test in this class swaps RoundSchedulingOptions out via
        // services.RemoveAll<RoundSchedulingOptions>()/AddSingleton (SetUp
        // above), which bypasses Program.cs's actual
        // `RoundScheduling:RoundDurationHours` config-binding logic (the
        // literal mechanism ADR-0027/REQ-301 added so play frequency is
        // adjustable without a code change). This test uses a dedicated
        // factory that never touches RoundSchedulingOptions itself, so
        // Program.cs's real `builder.Configuration.GetValue<double?>(...) ??
        // 48` registration is exercised end-to-end.
        //
        // The override must be a real process environment variable, not a
        // WithWebHostBuilder/ConfigureAppConfiguration source: Program.cs
        // reads RoundScheduling:RoundDurationHours directly off
        // builder.Configuration in its own top-level code, *before*
        // builder.Build() runs — the exact same "eager read" gotcha
        // documented on SeedGuessableRound/ForceCloseRound's Production
        // tests below for ConnectionStrings/Supabase. WebApplicationFactory's
        // ConfigureAppConfiguration hook only takes effect once the deferred
        // host-build machinery intercepts Build(), which is too late for a
        // value Program.cs already read. Real environment variables are
        // loaded by WebApplication.CreateBuilder(args) itself, before
        // Program.cs's top-level code runs, so they're the only override
        // visible early enough here.
        const double configuredRoundDurationHours = 72;
        using var _ = TemporaryEnvironmentVariables(
            ("RoundScheduling__RoundDurationHours", configuredRoundDurationHours.ToString()));

        using var configBoundFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Internal:JobToken"] = ValidJobToken,
                    });
                });

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

                    services.RemoveAll<GridGenerationOptions>();
                    services.AddSingleton(new GridGenerationOptions { MinValidAnswers = 1, MaxAttempts = 50 });

                    // Deliberately no RoundSchedulingOptions override here —
                    // that's the entire point of this test.
                });
            });
        await SeedFullyMatchedReferenceDataAsync(size: 3, factory: configBoundFactory);
        var client = configBoundFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);

        var response = await client.PostAsync("/internal/generate-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.EndTime - body.StartTime, Is.EqualTo(TimeSpan.FromHours(configuredRoundDurationHours)),
            "Program.cs's RoundScheduling:RoundDurationHours config binding must drive the generated round's duration");
    }

    [Test]
    public async Task GenerateRound_Post_ReturnsProblemDetails_WhenAnUnexpectedExceptionOccurs()
    {
        // Regression coverage for the 2026-07-12 dev incident: a manual
        // workflow_dispatch of generate-round.yml got a bare, opaque 500
        // with no diagnosable body because the endpoint's try/catch only
        // ever handled GridGenerationException — anything else (a DB blip,
        // here simulated directly) fell through to ASP.NET's default empty
        // 500. This asserts the catch-all branch added in response to that
        // incident actually produces a caller-visible problem-details body.
        var throwingFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRoundGenerationService>();
                services.AddScoped<IRoundGenerationService, ThrowingRoundGenerationService>();
            });
        });
        var client = throwingFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);

        var response = await client.PostAsync("/internal/generate-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Round generation failed unexpectedly"));
        Assert.That(problem.Detail, Is.EqualTo("simulated DB failure"));
    }

    private sealed class ThrowingRoundGenerationService : IRoundGenerationService
    {
        public Task<Round> GenerateNextRoundIfNeededAsync(string gameKey, RoundConfig config, TimeSpan? roundDurationOverride = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated DB failure");
    }

    // ---- REQ-304: SequenceNumber is distinct/incrementing per GameKey, ------
    // proven through the real HTTP endpoint (not just RoundGenerationService
    // directly, which RoundGenerationServiceTests already covers at the Unit
    // level) ------------------------------------------------------------

    [Test]
    public async Task REQ304_GenerateRound_Post_CalledTwiceForSameGameKey_AssignsDistinctIncrementingSequenceNumbers()
    {
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();

        // Call 1: no round exists yet for this GameKey -> creates round 1,
        // which starts immediately (StartTime ~= now), so it's already
        // active by the time call 2 runs a moment later — same "call 2
        // generates a real next round" mechanics as
        // REQ301_GenerateRound_Post_IsIdempotent_WhenAnUpcomingRoundAlreadyExists
        // above, reused here rather than inventing a new way to force a
        // second real round.
        var first = await client.PostAsync("/internal/generate-round", content: null);
        var firstBody = await first.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        // Call 2: round 1 is active with no round scheduled after it yet ->
        // a genuine second round for the same GameKey, not the idempotent
        // no-op case.
        var second = await client.PostAsync("/internal/generate-round", content: null);
        var secondBody = await second.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(firstBody!.SequenceNumber, Is.EqualTo(1));
        Assert.That(secondBody!.RoundId, Is.Not.EqualTo(firstBody.RoundId), "a genuine second round, not the idempotent no-op case");
        Assert.That(secondBody.SequenceNumber, Is.EqualTo(2));
        Assert.That(secondBody.SequenceNumber, Is.Not.EqualTo(firstBody.SequenceNumber));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.EqualTo(2));
    }

    // ---- S-084/REQ-1202: generate-round is genuinely GameKey-parameterized --
    // for "xg-path" too, end-to-end through the real endpoint --------------

    [Test]
    public async Task REQ1202_GenerateRound_Post_WithGameKeyXgPath_GeneratesAnXgPathRound_UsingItsOwnConfiguredRoundDuration()
    {
        // A dedicated layered factory adds xg-path's own RoundSchedulingOptions
        // (30h — deliberately distinct from SetUp's xg-grid 72h) and a smaller
        // PathGenerationOptions.PuzzleCount so only 3 eligible target players
        // need seeding, mirroring the GridGenerationOptions.GridSize=3 override
        // SetUp already does for xg-grid. This is the API-level proof of
        // REQ-1202's "independent of xG Grid's own round timing/duration" —
        // not just the unit-level proof in RoundGenerationServiceTests, but the
        // real endpoint, real DI graph, real PathTemplateResolver find-or-create
        // path, and a real XGPathGameModule.GenerateInstanceAsync run.
        var xgPathFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<PathGenerationOptions>();
                services.AddSingleton(new PathGenerationOptions { PuzzleCount = 3 });

                services.AddSingleton(new RoundSchedulingOptions
                {
                    GameKey = XGPathGameModule.XGPathGameKey,
                    RoundDuration = TimeSpan.FromHours(30),
                });
            });
        });
        await SeedEligiblePathPlayersAsync(count: 3, factory: xgPathFactory);
        var client = xgPathFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);

        var response = await client.PostAsync("/internal/generate-round?gameKey=xg-path", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.GameKey, Is.EqualTo(XGPathGameModule.XGPathGameKey));
        Assert.That(body.EndTime - body.StartTime, Is.EqualTo(TimeSpan.FromHours(30)),
            "must use xg-path's own configured RoundDuration (30h), never xg-grid's (72h, per this class's SetUp)");

        using var scope = xgPathFactory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.EqualTo(1));
        var template = await dbContext.PathTemplates.SingleAsync();
        Assert.That(template.PuzzleCount, Is.EqualTo(3), "PathTemplateResolver's find-or-create path must use PathGenerationOptions.PuzzleCount");
        var instance = await dbContext.PathInstances.Include(pi => pi.Puzzles).SingleAsync();
        Assert.That(instance.Puzzles, Has.Count.EqualTo(3), "REQ-1202: exactly PuzzleCount puzzles, each targeting a distinct eligible player");
        Assert.That(instance.Puzzles.Select(p => p.TargetPlayerId).Distinct().Count(), Is.EqualTo(3));
    }

    // ---- REQ-1208/ADR-0058: xG Path target cycle tracking, end-to-end -----
    // through the real /internal/generate-round endpoint ------------------

    [Test]
    public async Task REQ1208_GenerateRound_Post_WithGameKeyXgPath_AcrossCycleRolloverBoundary_ProducesExactlyNDistinctTargetPuzzlesEachTime()
    {
        // Only 4 eligible players, PuzzleCount 3: round 1 uses 3 of the 4
        // (leaving 1 unused-in-cycle, below PuzzleCount), so round 2 — the
        // genuine "one round ahead" generation REQ301's own idempotency test
        // already proves happens on a second call once round 1 has started —
        // must roll the cycle over rather than aborting for lack of
        // candidates, and must still produce exactly 3 distinct targets.
        var xgPathFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<PathGenerationOptions>();
                services.AddSingleton(new PathGenerationOptions { PuzzleCount = 3 });

                services.AddSingleton(new RoundSchedulingOptions
                {
                    GameKey = XGPathGameModule.XGPathGameKey,
                    RoundDuration = TimeSpan.FromHours(30),
                });
            });
        });
        await SeedEligiblePathPlayersAsync(count: 4, factory: xgPathFactory);
        var client = xgPathFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);

        var firstResponse = await client.PostAsync("/internal/generate-round?gameKey=xg-path", content: null);
        var firstRound = await firstResponse.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        // Round 1 starts ~now and is therefore already active by the time
        // this second call runs a moment later — same "call 2 generates a
        // real next round" mechanics as REQ301_GenerateRound_Post_
        // IsIdempotent_WhenAnUpcomingRoundAlreadyExists above, just against
        // xg-path.
        var secondResponse = await client.PostAsync("/internal/generate-round?gameKey=xg-path", content: null);
        var secondRound = await secondResponse.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(secondRound!.RoundId, Is.Not.EqualTo(firstRound!.RoundId), "a genuine second round, not the idempotent no-op case");

        using var scope = xgPathFactory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var firstRoundEntity = await dbContext.Rounds.SingleAsync(r => r.Id == firstRound.RoundId);
        var secondRoundEntity = await dbContext.Rounds.SingleAsync(r => r.Id == secondRound.RoundId);
        var firstTargets = (await dbContext.PathInstances.Include(pi => pi.Puzzles).SingleAsync(pi => pi.Id == firstRoundEntity.GameInstanceId))
            .Puzzles.Select(p => p.TargetPlayerId).ToList();
        var secondTargets = (await dbContext.PathInstances.Include(pi => pi.Puzzles).SingleAsync(pi => pi.Id == secondRoundEntity.GameInstanceId))
            .Puzzles.Select(p => p.TargetPlayerId).ToList();

        Assert.That(firstTargets, Has.Count.EqualTo(3));
        Assert.That(firstTargets.Distinct().Count(), Is.EqualTo(3), "REQ-1202's own within-instance distinctness guarantee, unaffected by cycle tracking");
        Assert.That(secondTargets, Has.Count.EqualTo(3));
        Assert.That(secondTargets.Distinct().Count(), Is.EqualTo(3));

        var cycleState = await dbContext.PathTargetCycles.SingleAsync();
        Assert.That(cycleState.CycleNumber, Is.EqualTo(2), "the remaining-unused-in-cycle count (1) dropped below PuzzleCount(3), so round 2's generation must have rolled the cycle over");
        Assert.That(cycleState.ObservedPoolSize, Is.EqualTo(4));
        Assert.That(cycleState.UsedInCycleCount, Is.EqualTo(3));
        Assert.That(cycleState.LastCycleCompletedAt, Is.Not.Null);
    }

    [Test]
    public async Task REQ1208_GenerateRound_Post_WithGameKeyXgPath_InsufficientTotalEligiblePool_ReturnsProblemDetails_UnaffectedByCycleState()
    {
        var xgPathFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<PathGenerationOptions>();
                services.AddSingleton(new PathGenerationOptions { PuzzleCount = 3 });

                services.AddSingleton(new RoundSchedulingOptions
                {
                    GameKey = XGPathGameModule.XGPathGameKey,
                    RoundDuration = TimeSpan.FromHours(30),
                });
            });
        });
        await SeedEligiblePathPlayersAsync(count: 2, factory: xgPathFactory); // fewer than PuzzleCount(3), total pool, regardless of cycle state
        var client = xgPathFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);

        var response = await client.PostAsync("/internal/generate-round?gameKey=xg-path", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Round generation failed"));
        Assert.That(problem.Detail, Does.Contain("Not enough eligible target players"));

        using var scope = xgPathFactory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.Zero, "an aborted generation must never create a round");
        Assert.That(await dbContext.PathTargetCycles.CountAsync(), Is.Zero,
            "REQ-1208's cycle state is only created/mutated once a generation actually succeeds — the pre-existing insufficient-total-pool abort runs first and is unaffected by (and creates no) cycle state");
    }

    [Test]
    public async Task REQ1202_GenerateRound_Post_OmittingGameKey_StillDefaultsToXgGrid()
    {
        // Regression check, not new behavior: S-084 added the optional
        // gameKey query parameter — an existing caller (or a stray/older
        // manual workflow_dispatch run) that never passes it must keep
        // generating xg-grid rounds exactly as before.
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/generate-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.GameKey, Is.EqualTo(GridGameModule.XGGridGameKey));
    }

    [Test]
    public async Task REQ1202_GenerateRound_Post_WithUnrecognizedGameKey_ReturnsProblemDetails_NotAnUnhandledException()
    {
        // Quality-gate follow-up (S-084): an unrecognized gameKey is
        // malformed caller input (a bad query-string value), not a round-
        // generation failure, so InternalRoundEndpoints validates it up
        // front and returns 400 Bad Request via Results.Problem — the same
        // discipline the roundDurationHours check in that handler already
        // uses — rather than letting the gameKey switch's defensive throw
        // fall through into the generic 500 catch-all. This asserts that
        // 400 path, not the earlier 500 behavior it replaced.
        var client = CreateAuthorizedClient();

        var response = await client.PostAsync("/internal/generate-round?gameKey=xg-nonexistent", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid gameKey"));
        Assert.That(problem.Detail, Does.Contain("Unknown gameKey 'xg-nonexistent'"));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.Zero, "an unrecognized gameKey must not generate any round");
    }

    // ---- REQ-806: force-close-round is a non-Production-only test control --

    [Test]
    public async Task REQ806_ForceCloseRound_Post_ClosesRoundImmediately_InNonProductionEnvironment()
    {
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = CreateAuthorizedClient();
        var generateResponse = await client.PostAsync("/internal/generate-round", content: null);
        var round = await generateResponse.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        var response = await _factory.CreateClient().PostAsync($"/internal/test-data/force-close-round/{round!.RoundId}", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<ForceCloseRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.EndTime, Is.LessThan(round.EndTime), "closing before the round's real end_time must pull it forward");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var persisted = await dbContext.Rounds.SingleAsync(r => r.Id == round.RoundId);
        Assert.That(persisted.GetStatus(DateTime.UtcNow), Is.EqualTo(RoundStatus.Closed));
    }

    [Test]
    public async Task ForceCloseRound_Post_ReturnsNotFound_ForUnknownRoundId()
    {
        var response = await _factory.CreateClient().PostAsync($"/internal/test-data/force-close-round/{Guid.NewGuid()}", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ForceCloseRound_Post_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        // A round that genuinely exists — proves the 404 below comes from
        // the route itself being absent (REQ-801's discipline, reused by
        // REQ-806), not merely "round not found" against an existing route.
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var generateResponse = await CreateAuthorizedClient().PostAsync("/internal/generate-round", content: null);
        var round = await generateResponse.Content.ReadFromJsonAsync<GenerateRoundResponse>();

        // Program.cs reads several required config values (connection
        // string, Supabase settings) eagerly, before WebApplicationFactory's
        // ConfigureAppConfiguration/UseEnvironment hooks can take effect (those
        // only apply once the deferred host-build machinery intercepts
        // Build(), which happens after Program.cs's own top-level code has
        // already run) — real process environment variables are the only
        // override visible early enough to genuinely flip which environment
        // this host starts under, so appsettings.Development.json's values
        // are skipped the same way a real Production deployment would.
        using var _ = TemporaryEnvironmentVariables(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("ConnectionStrings__Database", "Host=localhost;Database=unused-in-tests;Username=postgres;Password=postgres"),
            ("Supabase__Url", "http://localhost:54321"),
            ("Supabase__AnonKey", "test-placeholder-anon-key"),
            ("Supabase__ServiceRoleKey", "test-placeholder-service-role-key"));

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.PostAsync($"/internal/test-data/force-close-round/{round!.RoundId}", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-807: seed-guessable-round is a non-Production-only test control --

    [Test]
    public async Task REQ807_SeedGuessableRound_Post_CreatesAnActiveRoundWithOneGuessableCell()
    {
        var response = await _factory.CreateClient().PostAsync("/internal/test-data/seed-guessable-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SeedGuessableRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.CorrectPlayerName, Is.Not.Empty);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var round = await dbContext.Rounds.SingleAsync(r => r.Id == body.RoundId);
        Assert.That(round.GetStatus(DateTime.UtcNow), Is.EqualTo(RoundStatus.Active));
        var instance = await dbContext.GridInstances.Include(gi => gi.Cells).SingleAsync(gi => gi.Id == round.GameInstanceId);
        Assert.That(instance.Cells.Select(c => c.Id), Does.Contain(body.CellId));
    }

    [Test]
    public async Task SeedGuessableRound_Post_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = TemporaryEnvironmentVariables(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("ConnectionStrings__Database", "Host=localhost;Database=unused-in-tests;Username=postgres;Password=postgres"),
            ("Supabase__Url", "http://localhost:54321"),
            ("Supabase__AnonKey", "test-placeholder-anon-key"),
            ("Supabase__ServiceRoleKey", "test-placeholder-service-role-key"));

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.PostAsync("/internal/test-data/seed-guessable-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- S-088/REQ-807 extension: seed-guessable-path-round is a non-Production-only test control --

    [Test]
    public async Task REQ807_SeedGuessablePathRound_Post_CreatesAnActiveXgPathRoundWithOneGuessablePuzzle()
    {
        var response = await _factory.CreateClient().PostAsync("/internal/test-data/seed-guessable-path-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<SeedGuessablePathRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.CorrectPlayerName, Is.Not.Empty);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var round = await dbContext.Rounds.SingleAsync(r => r.Id == body.RoundId);
        Assert.That(round.GetStatus(DateTime.UtcNow), Is.EqualTo(RoundStatus.Active));
        Assert.That(round.GameKey, Is.EqualTo(XGPathGameModule.XGPathGameKey));

        var instance = await dbContext.PathInstances.Include(pi => pi.Puzzles).SingleAsync(pi => pi.Id == round.GameInstanceId);
        Assert.That(instance.Puzzles.Select(p => p.Id), Does.Contain(body.PuzzleId));

        var puzzle = instance.Puzzles.Single(p => p.Id == body.PuzzleId);
        var stints = await dbContext.PlayerCareerStints
            .Where(s => s.PlayerId == puzzle.TargetPlayerId)
            .OrderBy(s => s.SequenceOrder)
            .ToListAsync();
        Assert.That(stints, Has.Count.GreaterThanOrEqualTo(3));
    }

    [Test]
    public async Task SeedGuessablePathRound_Post_IsNeverRegistered_WhenEnvironmentIsProduction()
    {
        using var _ = TemporaryEnvironmentVariables(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("ConnectionStrings__Database", "Host=localhost;Database=unused-in-tests;Username=postgres;Password=postgres"),
            ("Supabase__Url", "http://localhost:54321"),
            ("Supabase__AnonKey", "test-placeholder-anon-key"),
            ("Supabase__ServiceRoleKey", "test-placeholder-service-role-key"));

        var productionFactory = _factory.WithWebHostBuilder(builder => { });
        var client = productionFactory.CreateClient();

        var response = await client.PostAsync("/internal/test-data/seed-guessable-path-round", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // Sets process environment variables for the duration of one test,
    // restoring each to its original value (including "unset") on dispose.
    private static IDisposable TemporaryEnvironmentVariables(params (string Name, string Value)[] variables)
    {
        var originalValues = variables.Select(v => (v.Name, Original: Environment.GetEnvironmentVariable(v.Name))).ToList();
        foreach (var (name, value) in variables)
            Environment.SetEnvironmentVariable(name, value);

        return new RestoreEnvironmentVariables(originalValues);
    }

    private sealed class RestoreEnvironmentVariables(List<(string Name, string? Original)> originalValues) : IDisposable
    {
        public void Dispose()
        {
            foreach (var (name, original) in originalValues)
                Environment.SetEnvironmentVariable(name, original);
        }
    }
}
