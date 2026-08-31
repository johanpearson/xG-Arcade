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
using XGArcade.DataSync.ApiFootball;
using XGArcade.Games.XGGrid;
using XGArcade.Games.XGPath;
using XGArcade.Games.XGPredict;

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

        // ADR-0089: headers now pick their category type independently, so a
        // Club header can land on BOTH axes (Club x Club), not just paired
        // against a Country header as under the old per-instance pairing —
        // every same-type pair this fixture's clubs could now be drawn into
        // needs its own cached match below, and every WikidataQid must be
        // real-format (^Q\d+$, see WikidataQid.IsValid) since a live lookup
        // that used to be structurally unreachable (Club x Club needed
        // 2 x size clubs under the old SelectPairing, more than this fixture
        // ever seeded) can now actually fire and validate the QID format.
        //
        // CI-found fix (2026-08-29): countries/clubs seeded at exactly
        // `size` (no trophies at all in this fixture) hits a real deficit,
        // not just "zero slack" — Country x Country is banned, so ANY
        // country row dooms every remaining country candidate; whenever the
        // row draw picks a 2-of-one-type + 1-of-the-other split (roughly
        // 90% of all 3-header draws from a 2-type, 3-each pool), only
        // `clubCount - (clubs used as rows)` candidates stay valid, which
        // can drop below `size`. Widened to `size + 3` (clubs specifically
        // need >= 2*size - 1 to guarantee no deficit under this worst case
        // — see NOTES.md's 2026-08-29 entry for the derivation) so
        // generation always succeeds regardless of the random split.
        var countries = Enumerable.Range(0, size + 3)
            .Select(i => new CountryDefinition { Id = Guid.NewGuid(), Name = $"Country{i}", WikidataQid = $"Q1{i}" })
            .ToList();
        var clubs = Enumerable.Range(0, size + 3)
            .Select(i => new ClubDefinition { Id = Guid.NewGuid(), Name = $"Club{i}", WikidataQid = $"Q2{i}" })
            .ToList();
        dbContext.CountryDefinitions.AddRange(countries);
        dbContext.ClubDefinitions.AddRange(clubs);

        foreach (var country in countries)
        {
            foreach (var club in clubs)
            {
                var player = new Player { Id = Guid.NewGuid(), FullName = $"{country.Name}-{club.Name}", WikidataQid = $"Q3{country.Name}-{club.Name}" };
                dbContext.Players.Add(player);
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = country.Name });
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = club.Name });
            }
        }

        // Every distinct pair of clubs also gets a matching player, same
        // "AttributeType = club" x2 shape GridGenerationServiceTests.cs
        // already established for Club x Club — covers any (Club, Club)
        // cell the new per-header mixing might now produce.
        for (var i = 0; i < clubs.Count; i++)
        {
            for (var j = i + 1; j < clubs.Count; j++)
            {
                var player = new Player { Id = Guid.NewGuid(), FullName = $"{clubs[i].Name}-{clubs[j].Name}", WikidataQid = $"Q4{clubs[i].Name}-{clubs[j].Name}" };
                dbContext.Players.Add(player);
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = clubs[i].Name });
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = clubs[j].Name });
            }
        }

        await dbContext.SaveChangesAsync();
    }

    // S-084/REQ-1202: eligible xG Path target players — REQ-1201/ADR-0074/
    // S-138: at least 2 DISTINCT qualifying seeded-club stints (updated from
    // the old "≥3 ordered stints, ≥1 at a seeded club" rule) — mirrors
    // XGPathGameModuleTests.SeedEligiblePlayer's exact fixture shape (2
    // distinct seeded clubs plus 1 unseeded stint) rather than reinventing
    // it, since that's the file that already established what "eligible"
    // means for this game at a fixture level. BirthYear = 1990 for the same
    // reason XGPathGameModuleTests.SeedPlayer/SeedEligiblePlayer default to
    // it (REQ-1201/ADR-0073/S-137): comfortably above the BirthYear >= 1975
    // floor, so this fixture stays eligible without needing to know about
    // that rule either. Position = "Forward" for the same reason again
    // (REQ-1201/ADR-0079/S-161, 2026-08-19 CI fix): a null Position now
    // fails eligibility too, and this fixture predates that floor.
    private async Task SeedEligiblePathPlayersAsync(int count, WebApplicationFactory<Program>? factory = null)
    {
        using var scope = (factory ?? _factory).Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        const string seededClubName = "Seeded FC";
        const string secondSeededClubName = "Seeded FC 2";
        dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = seededClubName, WikidataQid = "Qclub-seeded-fc" });
        dbContext.ClubDefinitions.Add(new ClubDefinition { Id = Guid.NewGuid(), Name = secondSeededClubName, WikidataQid = "Qclub-seeded-fc-2" });

        for (var i = 0; i < count; i++)
        {
            var player = new Player { Id = Guid.NewGuid(), FullName = $"Eligible Path Player {i}", WikidataQid = $"Qpathplayer-{i}-{Guid.NewGuid()}", BirthYear = 1990, Position = "Forward" };
            dbContext.Players.Add(player);
            dbContext.PlayerCareerStints.AddRange(
                new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = seededClubName, StartYear = 2010, EndYear = 2013, SequenceOrder = 0 },
                new PlayerCareerStint { Id = Guid.NewGuid(), PlayerId = player.Id, ClubName = secondSeededClubName, StartYear = 2013, EndYear = 2016, SequenceOrder = 1 },
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
        // could let a round close before that GameKey's own round-generation
        // workflow (generate-grid-round.yml/generate-path-round.yml as of
        // S-136/ADR-0072; previously the shared generate-round.yml) daily
        // cron fires again and generates its successor.
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
        // workflow_dispatch of generate-round.yml (since split into
        // generate-grid-round.yml/generate-path-round.yml, S-136/ADR-0072)
        // got a bare, opaque 500
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

    [Test]
    public async Task REQ304_GenerateRound_Post_TwoDifferentGameKeys_EachIndependentlyAssignsSequenceNumberOne()
    {
        // Independence per GameKey (REQ-304's "Independence per GameKey"
        // criterion), proven through the real HTTP endpoint rather than only
        // at the Unit level (RoundGenerationServiceTests' own
        // REQ304_GenerateNextRoundIfNeeded_TwoDifferentGameKeys_
        // EachIndependentlyAssignsSequenceNumberOne): both calls share the
        // same database (one factory, one in-memory DB), so if SequenceNumber
        // were ever a single global counter instead of scoped per GameKey,
        // the second call below would incorrectly come back as 2, not 1.
        //
        // xg-path needs its own RoundSchedulingOptions (added on top of
        // SetUp's xg-grid-only registration, per that field's own doc
        // comment) and a small PathGenerationOptions.PuzzleCount, mirroring
        // REQ1202_GenerateRound_Post_WithGameKeyXgPath_GeneratesAnXgPathRound_
        // UsingItsOwnConfiguredRoundDuration below — this is the "existing
        // cross-GameKey test in this file" pattern referenced for how to
        // seed xg-path-specific data.
        var multiGameKeyFactory = _factory.WithWebHostBuilder(builder =>
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
        var client = multiGameKeyFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);

        // Grid must be seeded and generated before xg-path's own reference
        // data exists: GridGenerationService.GenerateInstanceAsync's club
        // candidate pool comes from every ClubDefinition row in the shared
        // database (categoryValueRepository.GetClubsAsync, not scoped to
        // this test's grid fixture), so if SeedEligiblePathPlayersAsync's
        // "Seeded FC" ClubDefinition already existed, it would join that
        // pool with no matching PlayerAttribute data, forcing a live
        // Wikidata lookup that then fails on this test's synthetic,
        // non-real-format QIDs ("Qc0" etc. — fine for the cache-lookup path
        // this test relies on, never meant to reach WikidataClient).
        await SeedFullyMatchedReferenceDataAsync(size: 3, factory: multiGameKeyFactory);
        var gridResponse = await client.PostAsync("/internal/generate-round?gameKey=xg-grid", content: null);

        await SeedEligiblePathPlayersAsync(count: 3, factory: multiGameKeyFactory);
        var pathResponse = await client.PostAsync("/internal/generate-round?gameKey=xg-path", content: null);

        Assert.That(gridResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(pathResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var gridBody = await gridResponse.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        var pathBody = await pathResponse.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        Assert.That(gridBody!.GameKey, Is.EqualTo(GridGameModule.XGGridGameKey));
        Assert.That(pathBody!.GameKey, Is.EqualTo(XGPathGameModule.XGPathGameKey));
        Assert.That(gridBody.RoundId, Is.Not.EqualTo(pathBody.RoundId));
        Assert.That(gridBody.SequenceNumber, Is.EqualTo(1));
        Assert.That(pathBody.SequenceNumber, Is.EqualTo(1),
            "SequenceNumber is an independent counter per GameKey — a second GameKey's first round may share the same value as another GameKey's first round, not continue a shared global counter");

        using var scope = multiGameKeyFactory.Services.CreateScope();
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

    // ---- This story (wiring "xg-predict" into round scheduling): ----------
    // generate-round is genuinely GameKey-parameterized for "xg-predict"
    // too, end-to-end through the real endpoint -----------------------------

    [Test]
    public async Task REQ1301_GenerateRound_Post_WithGameKeyXgPredict_GeneratesAnXgPredictRound_UsingItsOwnConfiguredRoundDuration()
    {
        // Mirrors REQ1202_GenerateRound_Post_WithGameKeyXgPath_... above: a
        // dedicated layered factory adds xg-predict's own
        // RoundSchedulingOptions (30h, deliberately distinct from SetUp's
        // xg-grid 72h), a FakeApiFootballClient standing in for the real
        // HTTP client (XGPredictGameModule.GenerateInstanceAsync's fixture
        // source, REQ-1301) so no real api-football.com egress happens, and
        // PredictGenerationOptions.MatchCount=5 with exactly 5 fake
        // fixtures. This is the API-level proof that gameKey=xg-predict is
        // no longer rejected by InternalRoundEndpoints' up-front
        // gameKey-allowlist check and resolves a real PredictTemplate.Id via
        // PredictTemplateResolver — not just the unit-level proof in
        // PredictTemplateResolverTests, but the real endpoint, real DI
        // graph, and a real XGPredictGameModule.GenerateInstanceAsync run.
        var fakeApiFootballClient = new FakeApiFootballClient
        {
            Fixtures = Enumerable.Range(0, 5)
                .Select(i => new ApiFootballFixture(
                    FixtureId: 100 + i,
                    HomeTeamId: 1000 + i,
                    HomeTeamName: $"Home{i}",
                    AwayTeamId: 2000 + i,
                    AwayTeamName: $"Away{i}",
                    KickoffUtc: DateTime.UtcNow.AddDays(7).AddMinutes(i)))
                .ToList(),
        };
        var xgPredictFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IApiFootballClient>();
                services.AddSingleton<IApiFootballClient>(fakeApiFootballClient);

                services.RemoveAll<PredictGenerationOptions>();
                services.AddSingleton(new PredictGenerationOptions { MatchCount = 5 });

                services.AddSingleton(new RoundSchedulingOptions
                {
                    GameKey = XGPredictGameModule.XGPredictGameKey,
                    RoundDuration = TimeSpan.FromHours(30),
                });
            });
        });
        var client = xgPredictFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);

        var response = await client.PostAsync("/internal/generate-round?gameKey=xg-predict", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GenerateRoundResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.GameKey, Is.EqualTo(XGPredictGameModule.XGPredictGameKey));
        Assert.That(body.EndTime - body.StartTime, Is.EqualTo(TimeSpan.FromHours(30)),
            "must use xg-predict's own configured RoundDuration (30h), never xg-grid's (72h, per this class's SetUp)");

        using var scope = xgPredictFactory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.EqualTo(1));
        var template = await dbContext.PredictTemplates.SingleAsync();
        Assert.That(template.MatchCount, Is.EqualTo(5), "PredictTemplateResolver's find-or-create path must use PredictGenerationOptions.MatchCount");
        var instance = await dbContext.PredictInstances.Include(pi => pi.Matches).SingleAsync();
        Assert.That(instance.Matches, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task REQ1301_GenerateRound_Post_WithGameKeyXgPredict_TooFewUpcomingFixtures_ReturnsProblemDetails()
    {
        // REQ-1301's abort-and-log case surfacing through this endpoint's
        // catch filter, now extended to include PredictGenerationException
        // alongside GridGenerationException/PathGenerationException — mirrors
        // REQ1208_GenerateRound_Post_WithGameKeyXgPath_InsufficientTotalEligiblePool_...'s
        // "Round generation failed" 500 assertion below, for xg-predict's own
        // abort path instead.
        var fakeApiFootballClient = new FakeApiFootballClient
        {
            Fixtures =
            [
                new ApiFootballFixture(101, 1001, "Home0", 2001, "Away0", DateTime.UtcNow.AddDays(7)),
            ], // fewer than MatchCount(5)
        };
        var xgPredictFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IApiFootballClient>();
                services.AddSingleton<IApiFootballClient>(fakeApiFootballClient);

                services.RemoveAll<PredictGenerationOptions>();
                services.AddSingleton(new PredictGenerationOptions { MatchCount = 5 });

                services.AddSingleton(new RoundSchedulingOptions
                {
                    GameKey = XGPredictGameModule.XGPredictGameKey,
                    RoundDuration = TimeSpan.FromHours(30),
                });
            });
        });
        var client = xgPredictFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ValidJobToken);

        var response = await client.PostAsync("/internal/generate-round?gameKey=xg-predict", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Round generation failed"));
        Assert.That(problem.Detail, Does.Contain("Not enough upcoming fixtures"));

        using var scope = xgPredictFactory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Rounds.CountAsync(), Is.Zero, "an aborted generation must never create a round");
    }

    // Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
    // "don't over-mock"), mirroring XGArcade.Games.XGPredict.Tests'
    // FakeApiFootballClient's exact shape — duplicated here rather than
    // shared across test assemblies because no InternalsVisibleTo wiring
    // exists between them (same "a different assembly, no InternalsVisibleTo
    // wired" precedent already noted on this file's sibling
    // AdminEndpointTests.FakeWikidataClient/
    // AdminSuggestionEndpointTests.FakeWikidataClient). GetFixtureResultAsync
    // (REQ-1305) is not exercised by anything this story wires up — not
    // implemented, throws if ever called.
    private sealed class FakeApiFootballClient : IApiFootballClient
    {
        public IReadOnlyList<ApiFootballFixture> Fixtures { get; set; } = [];

        public Task<IReadOnlyList<ApiFootballFixture>> GetUpcomingGameweekFixturesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Fixtures);

        public Task<ApiFootballFixtureResult> GetFixtureResultAsync(int fixtureId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException("REQ-1305 grading is out of scope for this story — GetFixtureResultAsync should never be called here.");
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
