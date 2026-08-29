using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XGArcade.Api.Grid;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Tests;

// S-007 (docs/backlog.md): API-level coverage for POST /internal/grid/generate
// — the endpoint itself isn't REQ-numbered (it's scaffolding to exercise
// grid generation end to end before Core.Rounds/S-008 exists), but the size
// validation and the GridGenerationException -> Problem mapping are real
// behavior worth testing at this level, distinct from GridGameModuleTests'
// REQ101/102/107/109 unit coverage of the generation algorithm itself.
public class GridEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as AuthEndpointTests —
                    // see that file's SetUp comment for why every
                    // XGArcadeDbContext-closed descriptor must be removed,
                    // not just the two obvious ones.
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

                    // MinValidAnswers=1 so a single seeded PlayerAttribute
                    // pair per (country, club) combination is enough —
                    // avoids depending on the real Wikidata HTTP client
                    // (registered via AddHttpClient in Program.cs, which
                    // this test host would otherwise try to actually call).
                    services.RemoveAll<GridGenerationOptions>();
                    services.AddSingleton(new GridGenerationOptions { MinValidAnswers = 1, MaxAttempts = 50 });
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [TestCase(2)]
    [TestCase(6)]
    public async Task GenerateGrid_Post_ReturnsBadRequest_ForSizeOutsideThreeToFive(int size)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/internal/grid/generate", new GenerateGridRequest(size));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task GenerateGrid_Post_ReturnsGridWithExactlyNineCells_ForSizeThree()
    {
        await SeedFullyMatchedReferenceDataAsync(size: 3);
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/internal/grid/generate", new GenerateGridRequest(3));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<GenerateGridResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Size, Is.EqualTo(3));
        Assert.That(body.Cells, Has.Count.EqualTo(9));
        Assert.That(body.Cells.Select(c => c.RowCategoryValue).Distinct().Count(), Is.EqualTo(3));
        Assert.That(body.Cells.Select(c => c.ColCategoryValue).Distinct().Count(), Is.EqualTo(3));
    }

    [Test]
    public async Task GenerateGrid_Post_ReturnsProblem_AndLogsError_WhenGenerationAborts()
    {
        // No countries/clubs seeded at all — GridGameModule aborts
        // immediately with "not enough reference data" (GridGenerationException).
        var loggerProvider = new CapturingLoggerProvider();
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider)));
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/internal/grid/generate", new GenerateGridRequest(3));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
        // REQ-101: "generation aborts and logs an error" — verified here,
        // not just that the client got a Problem response.
        Assert.That(loggerProvider.Entries, Has.Some.Matches<(LogLevel Level, string Message)>(
            e => e.Level == LogLevel.Error && e.Message.Contains("Grid generation aborted")));
    }

    private async Task SeedFullyMatchedReferenceDataAsync(int size)
    {
        using var scope = _factory.Services.CreateScope();
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

        // Every country x club pair gets one matching player, so
        // MinValidAnswers=1 (set in SetUp) always accepts on the first try
        // regardless of shuffle order.
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

}
