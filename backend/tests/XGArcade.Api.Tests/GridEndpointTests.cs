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

        var countries = Enumerable.Range(0, size)
            .Select(i => new CountryDefinition { Id = Guid.NewGuid(), Name = $"Country{i}", WikidataQid = $"Qc{i}" })
            .ToList();
        var clubs = Enumerable.Range(0, size)
            .Select(i => new ClubDefinition { Id = Guid.NewGuid(), Name = $"Club{i}", WikidataQid = $"Qk{i}" })
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
                var player = new Player { Id = Guid.NewGuid(), FullName = $"{country.Name}-{club.Name}", WikidataQid = $"Qp-{country.Name}-{club.Name}" };
                dbContext.Players.Add(player);
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "nationality", AttributeValue = country.Name });
                dbContext.PlayerAttributes.Add(new PlayerAttribute { PlayerId = player.Id, AttributeType = "club", AttributeValue = club.Name });
            }
        }

        await dbContext.SaveChangesAsync();
    }

    // Captures log entries written through ILogger<T> during a request, so
    // the abort-path test can assert the error was actually logged
    // server-side (docs/coding-guidelines.md: "log the full exception
    // server-side"), not just that the client got a Problem response.
    private class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
