using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Api.Guesses;
using XGArcade.Api.Rounds;
using XGArcade.Api.Suggestions;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.DataSync.Wikidata;

namespace XGArcade.Api.Tests;

// S-245 (docs/backlog.md, REQ-509/REQ-510): API-level coverage for POST
// /internal/test-data/seed-guessable-round-with-missing-club
// (InternalRoundEndpoints.cs) — the E2E seed endpoint
// frontend/tests/e2e/admin-review.spec.ts depends on to build a "misfit real
// player" scenario for both REQ-509's suggestion-review-and-commit and
// REQ-510's standalone search-and-commit.
//
// The round-trip tests below replicate the full flow (seed -> guess
// incorrect -> commit through the SAME production admin endpoints the E2E
// spec will drive -> guess correct) at the API level — the same
// "prove the effect is externally observable" discipline
// AdminEndpointTests.REQ501_CreatePlayerOverride_FlipsCellCorrectness_ForSubsequentGuess
// already established for the manual-override path, applied here to this
// story's two admin-commit paths instead. A swapped-in fake IWikidataClient
// (this file's own FakeWikidataClient, mirroring
// AdminSuggestionEndpointTests' identical precedent — same "reuse the
// pattern, not a shared class" convention this codebase already uses four
// times) means no test here ever makes a real network call. This suite
// proves the WRITE-PATH WIRING (seed step and commit step agree on the same
// Player row, category values line up exactly) is correct; it can't
// substitute for a real Wikidata response, which only ci.yml's real E2E run
// exercises — see this endpoint's own top comment in
// InternalRoundEndpoints.cs for the "why a real name/QID, not a fake" story.
public class SeedGuessableRoundWithMissingClubEndpointTests
{
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;
    private FakeWikidataClient _fakeWikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeWikidataClient = new FakeWikidataClient();
        var inMemoryDatabaseName = Guid.NewGuid().ToString();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Auth:Mode", "local-e2e");
                builder.UseSetting("Admin:UserIds", AdminAuthProviderUserId.ToString());

                builder.ConfigureServices(services =>
                {
                    // Same in-memory-DbContext swap as every other API test
                    // file in this project — see AdminSuggestionEndpointTests'
                    // SetUp for the "why every XGArcadeDbContext-closed
                    // descriptor must be removed" reasoning.
                    var xgArcadeDbContextDescriptors = services
                        .Where(d => d.ServiceType == typeof(XGArcadeDbContext)
                            || (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(XGArcadeDbContext))))
                        .ToList();
                    foreach (var descriptor in xgArcadeDbContextDescriptors)
                    {
                        services.Remove(descriptor);
                    }

                    services.AddDbContext<XGArcadeDbContext>(options =>
                        options.UseInMemoryDatabase(inMemoryDatabaseName));

                    services.RemoveAll<IWikidataClient>();
                    services.AddSingleton<IWikidataClient>(_fakeWikidataClient);
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    // ---- Seeding/auth helpers -------------------------------------------

    // Mirrors AdminEndpointTests.SeedGuessingUserAsync exactly — a real
    // guess submission requires a matching local User row for the bearer
    // token's "sub", and (for the REQ-509 round-trip test below) REQ-215's
    // suggestion endpoint additionally requires that user be non-guest,
    // which IsGuest's bool default (false) already satisfies here.
    private async Task SeedGuessingUserAsync(Guid authProviderUserId)
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

    private HttpClient CreateAdminClient() => CreateAuthenticatedClient(AdminAuthProviderUserId);

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    // Uri.EscapeDataString, never a hand-rolled "+"-for-space substitution —
    // "+" has no guaranteed special meaning in a raw URL query string the
    // way it does in an application/x-www-form-urlencoded body, so every
    // realPlayerName below (including the multi-word ones) goes through this
    // single helper rather than each call site risking it differently.
    private Task<HttpResponseMessage> PostSeedAsync(string realPlayerName) =>
        _factory.CreateClient().PostAsync(
            $"/internal/test-data/seed-guessable-round-with-missing-club?realPlayerName={Uri.EscapeDataString(realPlayerName)}", content: null);

    private async Task<SeedGuessableRoundWithMissingClubResponse> SeedAsync(string realPlayerName)
    {
        var response = await PostSeedAsync(realPlayerName);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<SeedGuessableRoundWithMissingClubResponse>();
        Assert.That(body, Is.Not.Null);
        return body!;
    }

    // ---- Validation/error paths ------------------------------------------

    [Test]
    public async Task Seed_ReturnsBadRequest_WhenRealPlayerNameIsBlank()
    {
        var response = await _factory.CreateClient().PostAsync(
            "/internal/test-data/seed-guessable-round-with-missing-club?realPlayerName=", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task Seed_ReturnsServiceUnavailable_NeverSilentNoMatch_WhenWikidataQueryFails()
    {
        _fakeWikidataClient.FailNextCareerLookups(1);

        var response = await PostSeedAsync("Anyone");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "ADR-0046: a failed/timed-out lookup must never be silently treated as no-match");
    }

    [Test]
    public async Task Seed_ReturnsUnprocessableEntity_WhenNoFootballerMatchesTheName()
    {
        // Deliberately not calling SetCareerLookup — the fake's
        // QueryPlayerCareerAndNationalityByNameAsync returns null for any
        // name it wasn't configured for, exercising the Found=false path.
        var response = await PostSeedAsync("Nobody Real");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
    }

    [Test]
    public async Task Seed_ReturnsUnprocessableEntity_WhenLookupHasNoClubs()
    {
        _fakeWikidataClient.SetCareerLookup("No Clubs Player", new WikidataPlayerCareerLookupResult("Q1", "No Clubs Player", "France", []));

        var response = await PostSeedAsync("No Clubs Player");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
    }

    [Test]
    public async Task Seed_ReturnsUnprocessableEntity_WhenLookupHasNoNationality()
    {
        _fakeWikidataClient.SetCareerLookup("No Nationality Player", new WikidataPlayerCareerLookupResult("Q2", "No Nationality Player", null, ["Some Club"]));

        var response = await PostSeedAsync("No Nationality Player");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
    }

    // ---- Retry/idempotency (same realPlayerName called twice) -----------

    [Test]
    public async Task Seed_SecondCallForSamePlayer_PicksNextUnsatisfiedClub_NotAnAlreadyCommittedOne()
    {
        _fakeWikidataClient.SetCareerLookup("Retry Player", new WikidataPlayerCareerLookupResult(
            "Qretry1", "Retry Player", "France", ["Club A", "Club B"]));

        var first = await SeedAsync("Retry Player");
        Assert.That(first.ExpectedClubName, Is.EqualTo("Club A"), "the first call must pick the first not-yet-effective club");

        // Commits "Club A" the same way REQ-510's standalone commit path
        // would, WITHOUT going through this test's own admin client helper
        // (irrelevant to this test) — simulates a prior E2E run's admin
        // action having already landed.
        var adminClient = CreateAdminClient();
        var commitResponse = await adminClient.PostAsJsonAsync(
            "/admin/player-search/commit",
            new CommitPlayerDataRequest("Qretry1", "Retry Player", null, ["Club A"], "pre-committed for retry test"));
        Assert.That(commitResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var second = await SeedAsync("Retry Player");
        Assert.That(second.PlayerId, Is.EqualTo(first.PlayerId), "same WikidataQid must resolve to the same Player row");
        Assert.That(second.ExpectedClubName, Is.EqualTo("Club B"), "must skip Club A (already committed) and pick the next unsatisfied club");
        Assert.That(second.RoundId, Is.Not.EqualTo(first.RoundId));
        Assert.That(second.CellId, Is.Not.EqualTo(first.CellId));
    }

    [Test]
    public async Task Seed_ReturnsConflict_WhenEveryKnownClubIsAlreadyEffective()
    {
        _fakeWikidataClient.SetCareerLookup("Exhausted Player", new WikidataPlayerCareerLookupResult(
            "Qexhausted1", "Exhausted Player", "Spain", ["Only Club"]));

        var first = await SeedAsync("Exhausted Player");
        Assert.That(first.ExpectedClubName, Is.EqualTo("Only Club"));

        var adminClient = CreateAdminClient();
        var commitResponse = await adminClient.PostAsJsonAsync(
            "/admin/player-search/commit",
            new CommitPlayerDataRequest("Qexhausted1", "Exhausted Player", null, ["Only Club"], "pre-committed for exhaustion test"));
        Assert.That(commitResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var response = await PostSeedAsync("Exhausted Player");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    // ---- REQ-510: standalone search-and-commit flips the same guess -----

    [Test]
    public async Task REQ510_StandaloneSearchAndCommit_FlipsSeededCellCorrectness_ForSubsequentGuess()
    {
        _fakeWikidataClient.SetCareerLookup("Patrick Vieira", new WikidataPlayerCareerLookupResult(
            "Qvieira1", "Patrick Vieira", "France", ["Arsenal F.C.", "Juventus F.C."]));

        var seeded = await SeedAsync("Patrick Vieira");
        Assert.That(seeded.Nationality, Is.EqualTo("France"));
        Assert.That(seeded.ExpectedClubName, Is.EqualTo("Arsenal F.C."));
        Assert.That(seeded.AllKnownClubs, Is.EquivalentTo(new[] { "Arsenal F.C.", "Juventus F.C." }));

        var guessingUserId = Guid.NewGuid();
        await SeedGuessingUserAsync(guessingUserId);
        var guessingClient = CreateAuthenticatedClient(guessingUserId);

        var before = await guessingClient.PostAsJsonAsync(
            $"/rounds/{seeded.RoundId}/cells/{seeded.CellId}/guesses", new SubmitGuessRequest(seeded.CorrectPlayerFullName));
        Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var beforeBody = await before.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(beforeBody, Is.Not.Null);
        Assert.That(beforeBody!.IsCorrect, Is.False, "the player's club is not yet recorded, so the cell's club requirement is unsatisfied before any admin action");

        var adminClient = CreateAdminClient();
        var lookupResponse = await adminClient.PostAsJsonAsync(
            "/admin/player-search/lookup", new PlayerSearchLookupRequest("Patrick Vieira"));
        Assert.That(lookupResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var lookupBody = await lookupResponse.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(lookupBody, Is.Not.Null);
        Assert.That(lookupBody!.Found, Is.True);
        Assert.That(lookupBody.Clubs, Does.Contain(seeded.ExpectedClubName), "the admin's own live lookup must surface the exact club this scenario needs committed");

        var commitResponse = await adminClient.PostAsJsonAsync(
            "/admin/player-search/commit",
            new CommitPlayerDataRequest(lookupBody.WikidataQid!, lookupBody.FullName!, lookupBody.Nationality, lookupBody.Clubs, "Confirmed via live Wikidata lookup"));
        Assert.That(commitResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var commitBody = await commitResponse.Content.ReadFromJsonAsync<CommitPlayerDataResponse>();
        Assert.That(commitBody, Is.Not.Null);
        Assert.That(commitBody!.PlayerId, Is.EqualTo(seeded.PlayerId), "the commit must land on the SAME Player row the seed step created, via the shared WikidataQid");
        Assert.That(commitBody.ClubsAdded, Does.Contain(seeded.ExpectedClubName));

        var after = await guessingClient.PostAsJsonAsync(
            $"/rounds/{seeded.RoundId}/cells/{seeded.CellId}/guesses", new SubmitGuessRequest(seeded.CorrectPlayerFullName));
        Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var afterBody = await after.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(afterBody, Is.Not.Null);
        Assert.That(afterBody!.IsCorrect, Is.True, "REQ-510: a standalone search-and-commit must flip the same cell/guess from incorrect to correct");
    }

    // ---- REQ-509: suggestion-review-and-commit flips the same guess -----

    [Test]
    public async Task REQ509_SuggestionReviewAndCommit_FlipsSeededCellCorrectness_ForSubsequentGuess()
    {
        _fakeWikidataClient.SetCareerLookup("Dennis Bergkamp", new WikidataPlayerCareerLookupResult(
            "Qbergkamp1", "Dennis Bergkamp", "Netherlands", ["Ajax", "Inter Milan", "Arsenal F.C."]));

        var seeded = await SeedAsync("Dennis Bergkamp");
        Assert.That(seeded.Nationality, Is.EqualTo("Netherlands"));
        Assert.That(seeded.ExpectedClubName, Is.EqualTo("Ajax"));

        var guessingUserId = Guid.NewGuid();
        await SeedGuessingUserAsync(guessingUserId);
        var guessingClient = CreateAuthenticatedClient(guessingUserId);

        var before = await guessingClient.PostAsJsonAsync(
            $"/rounds/{seeded.RoundId}/cells/{seeded.CellId}/guesses", new SubmitGuessRequest(seeded.CorrectPlayerFullName));
        Assert.That(before.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var beforeBody = await before.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(beforeBody, Is.Not.Null);
        Assert.That(beforeBody!.IsCorrect, Is.False);

        // REQ-215: the same guessing (non-guest) user submits a suggestion
        // against the cell they just guessed wrong on, asserting the club
        // this scenario needs.
        var suggestionResponse = await guessingClient.PostAsJsonAsync(
            $"/rounds/{seeded.RoundId}/cells/{seeded.CellId}/suggestions",
            new SubmitSuggestionRequest(seeded.CorrectPlayerFullName, [seeded.ExpectedClubName], seeded.Nationality));
        Assert.That(suggestionResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var suggestionBody = await suggestionResponse.Content.ReadFromJsonAsync<SubmitSuggestionResponse>();
        Assert.That(suggestionBody, Is.Not.Null);

        var adminClient = CreateAdminClient();
        var lookupResponse = await adminClient.PostAsync($"/admin/suggestions/{suggestionBody!.Id}/lookup", null);
        Assert.That(lookupResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var lookupBody = await lookupResponse.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(lookupBody, Is.Not.Null);
        Assert.That(lookupBody!.Found, Is.True);
        Assert.That(lookupBody.Clubs, Does.Contain(seeded.ExpectedClubName));

        var commitResponse = await adminClient.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionBody.Id}/commit",
            new CommitPlayerDataRequest(lookupBody.WikidataQid!, lookupBody.FullName!, lookupBody.Nationality, lookupBody.Clubs, "Confirmed via live Wikidata lookup"));
        Assert.That(commitResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var commitBody = await commitResponse.Content.ReadFromJsonAsync<CommitPlayerDataResponse>();
        Assert.That(commitBody, Is.Not.Null);
        Assert.That(commitBody!.PlayerId, Is.EqualTo(seeded.PlayerId), "the commit must land on the SAME Player row the seed step created, via the shared WikidataQid");

        var after = await guessingClient.PostAsJsonAsync(
            $"/rounds/{seeded.RoundId}/cells/{seeded.CellId}/guesses", new SubmitGuessRequest(seeded.CorrectPlayerFullName));
        Assert.That(after.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var afterBody = await after.Content.ReadFromJsonAsync<SubmitGuessResponse>();
        Assert.That(afterBody, Is.Not.Null);
        Assert.That(afterBody!.IsCorrect, Is.True, "REQ-509: an admin's suggestion review-and-commit must flip the same cell/guess from incorrect to correct");
    }

    // Same IWikidataClient fake shape as AdminSuggestionEndpointTests'
    // private FakeWikidataClient — duplicated here rather than shared/made
    // public, matching this codebase's own established "hand-rolled Fake*
    // per test file, no mocking framework, no shared fake library" precedent
    // (docs/coding-guidelines.md; already duplicated across AdminEndpointTests,
    // AdminSuggestionEndpointTests, and two DataSync/XGGrid test files).
    private sealed class FakeWikidataClient : IWikidataClient
    {
        private readonly Dictionary<string, WikidataPlayerCareerLookupResult> _careerLookupByName = new();
        private int _remainingCareerLookupFailures;

        public void SetCareerLookup(string playerName, WikidataPlayerCareerLookupResult result) =>
            _careerLookupByName[playerName] = result;

        public void FailNextCareerLookups(int calls) => _remainingCareerLookupFailures = calls;

        public Task<WikidataPlayerCareerLookupResult?> QueryPlayerCareerAndNationalityByNameAsync(
            string playerName, CancellationToken cancellationToken = default)
        {
            if (_remainingCareerLookupFailures > 0)
            {
                _remainingCareerLookupFailures--;
                throw new WikidataQueryException("simulated WDQS failure for admin career/nationality lookup");
            }

            var result = _careerLookupByName.TryGetValue(playerName, out var configured) ? configured : null;
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryCountryClubIntersectionAsync(
            string countryWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryNationalTeamClubIntersectionAsync(
            string nationalTeamWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryClubClubIntersectionAsync(
            string clubAWikidataQid, string clubBWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyCountryIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyClubIntersectionAsync(
            string trophyWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyCountryIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyNationalTeamIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTeamTrophyClubIntersectionAsync(
            string trophyWikidataQid, string clubWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataPlayerMatch>> QueryTrophyNationalTeamIntersectionAsync(
            string trophyWikidataQid, string countryWikidataQid, bool throwOnTimeout = false, CancellationToken cancellationToken = default,
            Action? onTechnicalFailure = null, WikidataQueryTimeoutTier timeoutTier = WikidataQueryTimeoutTier.Default) =>
            Task.FromResult<IReadOnlyList<WikidataPlayerMatch>>([]);

        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolBirthYearAsync(
            int birthYear, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        public Task<IReadOnlyDictionary<string, string>> QueryPlayerPhotosByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>> QueryPlayerPositionsAndBirthYearsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PlayerPositionBirthYearEntry>>(new Dictionary<string, PlayerPositionBirthYearEntry>());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>> QueryPlayerCareerStintsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<WikidataCareerStintEntry>>>(new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>());

        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByNationalityAsync(
            string nationalityWikidataQid, bool useCountryForSportProperty, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByClubAsync(
            string clubWikidataQid, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        public Task<RecentClubTransferLookupResult> QueryRecentClubTransfersAsync(
            string clubWikidataQid, string clubName, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RecentClubTransferLookupResult(
                new Dictionary<string, IReadOnlyList<WikidataCareerStintEntry>>(), new Dictionary<string, string>()));

        public Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<WikidataPlayerPhotoLookupResult?> QueryPlayerPhotoByNameAsync(
            string playerName, CancellationToken cancellationToken = default) =>
            Task.FromResult<WikidataPlayerPhotoLookupResult?>(null);

        public Task<WikidataPlayerRefreshData> QueryPlayerRefreshDataByQidAsync(
            string wikidataQid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WikidataPlayerRefreshData(null, null, null, null));

        public Task<IReadOnlyDictionary<string, WikidataInternationalStatsEntry>> QueryInternationalStatsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, WikidataInternationalStatsEntry>>(new Dictionary<string, WikidataInternationalStatsEntry>());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> QueryIndividualTrophyStatsByQidsAsync(
            IReadOnlyList<string> playerWikidataQids, IReadOnlyList<string> trophyWikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> QueryTeamTrophyStatsByQidsAsync(
            IReadOnlyList<string> playerWikidataQids, IReadOnlyList<string> trophyWikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>());
    }
}
