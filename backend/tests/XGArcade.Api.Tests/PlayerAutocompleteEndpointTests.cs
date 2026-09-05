using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Auth;
using XGArcade.Api.Players;
using XGArcade.Data;
using XGArcade.Data.Entities;

namespace XGArcade.Api.Tests;

// S-032 (docs/backlog.md, ADR-0007/REQ-207): GET /players/autocomplete.
// Same in-memory-DbContext/local-e2e-auth swap as GuessEndpointTests — see
// that file's SetUp comment for why every XGArcadeDbContext-closed
// descriptor must be removed, not just the two obvious ones.
public class PlayerAutocompleteEndpointTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
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

    private async Task SeedUserAsync(Guid authProviderUserId)
    {
        using var scope = _factory.Services.CreateScope();
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
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedPlayerNameIndexEntryAsync(
        string primaryName, int? birthYear = null, string? nationality = null, string? wikidataQid = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var entry = new PlayerNameIndex
        {
            PlayerId = Guid.NewGuid(),
            PrimaryName = primaryName,
            NormalizedName = PlayerNameNormalizer.Normalize(primaryName),
            BirthYear = birthYear,
            PrimaryNationality = nationality,
            WikidataQid = wikidataQid,
        };
        dbContext.PlayerNameIndexEntries.Add(entry);
        await dbContext.SaveChangesAsync();
        return entry.PlayerId;
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // ---- Auth guardrail -----------------------------------------------

    [Test]
    public async Task Autocomplete_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/players/autocomplete?query=Henry");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- REQ-207: suggestions come from PlayerNameIndex only ---------------

    [Test]
    public async Task REQ207_Autocomplete_Get_ReturnsEntry_WhenPlayerHasZeroPlayerAttributeOrPlayerDataRows()
    {
        // The structural guarantee ADR-0007 exists for: a name in
        // PlayerNameIndex with no corresponding PlayerAttribute/PlayerData
        // rows anywhere is a normal, expected state, and autocomplete must
        // still return it — never silently filtered because "nothing backs
        // this player up yet."
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var playerId = await SeedPlayerNameIndexEntryAsync("Someone Uncached", birthYear: 1995, nationality: "Norway");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete?query=someone");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions, Is.Not.Null);
        Assert.That(suggestions!, Has.Count.EqualTo(1));
        Assert.That(suggestions[0].PlayerId, Is.EqualTo(playerId));
        Assert.That(suggestions[0].Name, Is.EqualTo("Someone Uncached"));
        Assert.That(suggestions[0].BirthYear, Is.EqualTo(1995));
    }

    // ---- REQ-1406: candidate search for xG Connect's chain-step builder
    // ---- stays on the platform's existing broad search (COMP-10), not the
    // ---- curated club/country reference tables --------------------------------

    [Test]
    public async Task REQ1406_Autocomplete_Get_ReturnsPlayerWithZeroClubOrCountryDefinitionRows()
    {
        // REQ-1406's own Given/When/Then: "suggested candidates are drawn
        // from the platform's existing broad player-name search, which is
        // not restricted to the curated club/country reference tables" — no
        // new endpoint for xG Connect's own candidate search; this proves
        // the existing, unmodified /players/autocomplete endpoint already
        // satisfies that requirement, since it never reads
        // ClubDefinition/CountryDefinition at all (only PlayerNameIndex,
        // ADR-0007). No ClubDefinition/CountryDefinition rows are seeded in
        // this test's database at all — a real player from any club or
        // league can still be found.
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var playerId = await SeedPlayerNameIndexEntryAsync("Uncurated Club Player");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete?query=uncurated");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions!, Has.Count.EqualTo(1));
        Assert.That(suggestions[0].PlayerId, Is.EqualTo(playerId));
        Assert.That(suggestions[0].Name, Is.EqualTo("Uncurated Club Player"));
    }

    // Bug-bundle fix (2026-07-27, ADR-0007/REQ-207): Nationality must never
    // appear in the autocomplete response at all, regardless of whether the
    // underlying PlayerNameIndex row has one — seeding a nationality above
    // and confirming it never reaches the wire is the actual regression
    // test for the leak, not just an assertion this field happens to be
    // absent from the record shape.
    [Test]
    public async Task REQ207_Autocomplete_Get_ResponseNeverIncludesNationality_EvenWhenPlayerNameIndexHasOne()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedPlayerNameIndexEntryAsync("Someone Nationalised", nationality: "Norway");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete?query=someone");

        var rawJson = await response.Content.ReadAsStringAsync();
        Assert.That(rawJson, Does.Not.Contain("Norway"),
            "a nationality value must never be serialized into the autocomplete response — it leaks correctness for a nationality-based cell");
        Assert.That(rawJson, Does.Not.Contain("nationality").IgnoreCase,
            "the field itself must be gone from the response shape, not merely null");
    }

    // Bug fix (2026-09-05, ADR-0107): WikidataQid is a real, deliberate
    // addition to this response — the opposite of the Nationality test
    // above — since it's what lets xG Connect's candidate/target-pick
    // resolution disambiguate two different real people sharing a name
    // (e.g. "Jonas Olsson"). A null value (a row indexed before this column
    // existed) must still serialize as present-but-null, never omit the
    // field or throw.
    [Test]
    public async Task ADR0107_Autocomplete_Get_ResponseIncludesWikidataQid_WhenPlayerNameIndexHasOne()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedPlayerNameIndexEntryAsync("Someone Identified", wikidataQid: "Q123456");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete?query=someone");

        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions!, Has.Count.EqualTo(1));
        Assert.That(suggestions[0].WikidataQid, Is.EqualTo("Q123456"));
    }

    [Test]
    public async Task ADR0107_Autocomplete_Get_ResponseHasNullWikidataQid_WhenPlayerNameIndexRowPredatesTheColumn()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedPlayerNameIndexEntryAsync("Someone Unidentified", wikidataQid: null);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete?query=someone");

        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions!, Has.Count.EqualTo(1));
        Assert.That(suggestions[0].WikidataQid, Is.Null);
    }

    [Test]
    public async Task Autocomplete_Get_QueryIsCaseAndDiacriticInsensitive()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedPlayerNameIndexEntryAsync("Kylián Mbappé");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete?query=KYLIAN");

        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions!, Has.Count.EqualTo(1));
        Assert.That(suggestions[0].Name, Is.EqualTo("Kylián Mbappé"));
    }

    [Test]
    public async Task Autocomplete_Get_NoMatch_ReturnsEmptyArray()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedPlayerNameIndexEntryAsync("Thierry Henry");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete?query=zzz");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions, Is.Empty);
    }

    // ---- Empty/short query never hits the repository -----------------------

    [TestCase("")]
    [TestCase(" ")]
    [TestCase("a")]
    public async Task Autocomplete_Get_EmptyOrTooShortQuery_ReturnsEmptyArray_WithoutError(string query)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedPlayerNameIndexEntryAsync("Aaron Aardvark");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/players/autocomplete?query={Uri.EscapeDataString(query)}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions, Is.Empty);
    }

    [Test]
    public async Task Autocomplete_Get_MissingQueryParam_ReturnsEmptyArray()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions, Is.Empty);
    }

    // ---- Limit clamping ------------------------------------------------

    [Test]
    public async Task Autocomplete_Get_LimitAboveMax_IsClampedServerSide()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        for (var i = 0; i < 30; i++)
            await SeedPlayerNameIndexEntryAsync($"Zzzplayer {i:D2}");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete?query=zzzplayer&limit=1000");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions!, Has.Count.LessThanOrEqualTo(25), "limit must be clamped to a sane server-side max regardless of what the caller asked for");
    }

    [Test]
    public async Task Autocomplete_Get_NoLimitParam_DefaultsToTen()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        for (var i = 0; i < 15; i++)
            await SeedPlayerNameIndexEntryAsync($"Manyplayer {i:D2}");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete?query=manyplayer");

        var suggestions = await response.Content.ReadFromJsonAsync<List<PlayerAutocompleteSuggestion>>();
        Assert.That(suggestions!, Has.Count.EqualTo(10));
    }

    // ---- S-151/REQ-207: cold-start warm-up call ------------------------
    // Proves GET /players/autocomplete/warmup actually exercises the real
    // PlayerNameIndexRepository via normal DI (the SetUp above only swaps
    // XGArcadeDbContext for an in-memory provider — the repository itself
    // is never substituted with a fake), not just /health's process-only
    // wake-up. Seeding an entry and getting 204 back with no error proves
    // the repository call ran end-to-end rather than short-circuiting.

    [Test]
    public async Task REQ207_AutocompleteWarmup_Get_ReturnsNoContent_AndExercisesRealPlayerNameIndexRepository()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        await SeedPlayerNameIndexEntryAsync("Aaron Aardvark");
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/players/autocomplete/warmup");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test]
    public async Task REQ207_AutocompleteWarmup_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/players/autocomplete/warmup");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
