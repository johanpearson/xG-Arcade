using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Data;
using XGArcade.Data.Entities;
using XGArcade.DataSync.Wikidata;
using XGArcade.Games.XGGrid;

namespace XGArcade.Api.Tests;

// S-090 (docs/backlog.md): API-level coverage for REQ-509's admin
// suggestion review/commit/reject endpoints and REQ-510's standalone
// search-and-add endpoints — a minimal, wiring-level suite (this codebase's
// backend-implementer convention: comprehensive coverage lands with
// test-writer). Same in-memory-DbContext-swap/local-e2e-auth/Admin__UserIds
// pattern as AdminEndpointTests, plus a swapped-in fake IWikidataClient
// (this file's own private FakeWikidataClient, mirroring
// AuthEndpointTests.FakeSupabaseAuthClient's "local fake, swapped via
// RemoveAll+AddSingleton" precedent) so no test here ever makes a real
// network call.
public class AdminSuggestionEndpointTests
{
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;
    private FakeWikidataClient _fakeWikidataClient = null!;

    [SetUp]
    public void SetUp()
    {
        _fakeWikidataClient = new FakeWikidataClient();

        // Generated once here, not inside the ConfigureServices closure
        // below — CreateAdminClientWithLogging derives a second factory via
        // _factory.WithWebHostBuilder(...), which replays every
        // accumulated configuration action (including this one) against a
        // fresh host. A name generated inside the closure would be
        // re-rolled on that replay, silently pointing the logging-enabled
        // client at an empty second database instead of the one any
        // Seed*Async helper already wrote to via _factory.Services. Captured
        // once here, both factories resolve the same EF Core InMemory store
        // (keyed by name, shared per-process) regardless of how many nested
        // WithWebHostBuilder factories get built from _factory in a test.
        var inMemoryDatabaseName = Guid.NewGuid().ToString();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Auth:Mode", "local-e2e");
                builder.UseSetting("Admin:UserIds", AdminAuthProviderUserId.ToString());

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

                    services.AddDbContext<XGArcadeDbContext>(options =>
                        options.UseInMemoryDatabase(inMemoryDatabaseName));

                    services.RemoveAll<IWikidataClient>();
                    services.AddSingleton<IWikidataClient>(_fakeWikidataClient);
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    // ---- Seeding helpers ----------------------------------------------

    private async Task<Guid> SeedSubmittingUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = "submitter@example.com",
            DisplayName = "Submitting Player",
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedPendingSuggestionAsync(Guid submittingUserId, string playerName = "Clarence Seedorf")
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();

        var instanceId = Guid.NewGuid();
        var cellId = Guid.NewGuid();
        dbContext.GridInstances.Add(new GridInstance
        {
            Id = instanceId,
            TemplateId = Guid.NewGuid(),
            Cells = [new GridCell { Id = cellId, GridInstanceId = instanceId, Row = 0, Col = 0, RowCategoryType = CategoryPairingRules.Club, RowCategoryValue = "AC Milan", ColCategoryType = CategoryPairingRules.Club, ColCategoryValue = "Real Madrid" }],
        });
        var round = new Round
        {
            Id = Guid.NewGuid(),
            GameKey = GridGameModule.XGGridGameKey,
            GameInstanceId = instanceId,
            SequenceNumber = 1,
            StartTime = DateTime.UtcNow.AddDays(-1),
            EndTime = DateTime.UtcNow.AddDays(1),
        };
        dbContext.Rounds.Add(round);

        var suggestionId = Guid.NewGuid();
        dbContext.PlayerSuggestions.Add(new PlayerSuggestion
        {
            Id = suggestionId,
            PlayerName = playerName,
            AssertedNationality = "Netherlands",
            SubmittingUserId = submittingUserId,
            CellId = cellId,
            RoundId = round.Id,
            RowCategoryType = CategoryPairingRules.Club,
            ColCategoryType = CategoryPairingRules.Club,
            Status = PlayerSuggestionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            AssertedClubs = [new PlayerSuggestionClub { Id = Guid.NewGuid(), PlayerSuggestionId = suggestionId, ClubName = "AC Milan" }],
        });
        await dbContext.SaveChangesAsync();
        return suggestionId;
    }

    // REQ-515: seeds a local Player row for a given WikidataQid, the same
    // real-XGArcadeDbContext-backed-repository seeding shape every other
    // Seed*Async helper in this file already uses (no fake IPlayerRepository
    // — GetPlayerByWikidataQidAsync is a simple DB read with nothing external
    // to fake).
    private async Task<Guid> SeedPlayerWithWikidataQidAsync(string wikidataQid, string fullName)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = new Player { Id = Guid.NewGuid(), FullName = fullName, WikidataQid = wikidataQid };
        dbContext.Players.Add(player);
        await dbContext.SaveChangesAsync();
        return player.Id;
    }

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));
        return client;
    }

    // Same "swap in a capturing provider, create a client off that factory"
    // shape as GridEndpointTests.GenerateGrid_Post_ReturnsProblem_AndLogsError_WhenGenerationAborts
    // — used by the bug-fix regression tests below (2026-08-09) that assert
    // a Wikidata lookup failure is logged server-side, not just turned into
    // a 503 response.
    private HttpClient CreateAdminClientWithLogging(out CapturingLoggerProvider loggerProvider)
    {
        var provider = new CapturingLoggerProvider();
        loggerProvider = provider;
        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(provider)));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));
        return client;
    }

    // ---- Admin policy guardrail --------------------------------------------

    [Test]
    public async Task AdminSuggestionEndpoint_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(Guid.NewGuid()));

        var response = await client.GetAsync("/admin/suggestions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    // ---- REQ-509: listing pending suggestions ------------------------------

    [Test]
    public async Task REQ509_GetPendingSuggestions_ReturnsClubsNationalitySubmitterAndTimestamp()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/suggestions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<PendingSuggestionResponse>>();
        Assert.That(body, Is.Not.Null);
        var row = body!.Single();
        Assert.That(row.PlayerName, Is.EqualTo("Clarence Seedorf"));
        Assert.That(row.AssertedClubs, Is.EquivalentTo(new[] { "AC Milan" }));
        Assert.That(row.AssertedNationality, Is.EqualTo("Netherlands"));
        Assert.That(row.SubmittingUserId, Is.EqualTo(submittingUserId));
        Assert.That(row.SubmittingUserDisplayName, Is.EqualTo("Submitting Player"));
    }

    // ---- REQ-509: live lookup -----------------------------------------------

    [Test]
    public async Task REQ509_Lookup_ReturnsFetchedCareerAndNationality()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        _fakeWikidataClient.SetCareerLookup("Clarence Seedorf", new WikidataPlayerCareerLookupResult(
            "Q188207", "Clarence Seedorf", "Netherlands",
            ["AC Milan"]));
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/suggestions/{suggestionId}/lookup", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Found, Is.True);
        Assert.That(body.WikidataQid, Is.EqualTo("Q188207"));
        Assert.That(body.Nationality, Is.EqualTo("Netherlands"));
        Assert.That(body.Clubs, Is.EquivalentTo(new[] { "AC Milan" }));
    }

    [Test]
    public async Task REQ509_Lookup_ReturnsServiceUnavailable_NeverSilentNoMatch_WhenWikidataQueryFails()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        _fakeWikidataClient.FailNextCareerLookups(1);
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/suggestions/{suggestionId}/lookup", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "ADR-0046: a failed/timed-out lookup must never be silently treated as no-match");
    }

    // Bug fix (2026-08-09): before this fix, a Wikidata failure on either
    // admin lookup endpoint returned the same 503 with nothing logged
    // server-side, making a production "Lookup unavailable" report
    // undiagnosable and indistinguishable from the other endpoint's own
    // failures. Proves the exception is now logged at Warning, with enough
    // context (the suggestion id) to tell it apart from a later failure —
    // in addition to (not instead of) the existing 503-response assertion
    // above.
    [Test]
    public async Task REQ509_Lookup_LogsWarning_WhenWikidataQueryFails()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        _fakeWikidataClient.FailNextCareerLookups(1);
        var client = CreateAdminClientWithLogging(out var loggerProvider);

        var response = await client.PostAsync($"/admin/suggestions/{suggestionId}/lookup", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(loggerProvider.Entries, Has.Some.Matches<(LogLevel Level, string Message)>(
            e => e.Level == LogLevel.Warning && e.Message.Contains(suggestionId.ToString())),
            "the exception must be logged at Warning server-side, with the suggestion id for later diagnosis");
    }

    [Test]
    public async Task REQ509_Lookup_ReturnsNotFound_ForUnknownSuggestionId()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/suggestions/{Guid.NewGuid()}/lookup", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-515: ExistingPlayerId on the suggestion-scoped lookup ---------

    [Test]
    public async Task REQ515_Lookup_ReturnsExistingPlayerId_WhenLocalPlayerAlreadyExistsForFoundWikidataQid()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var existingPlayerId = await SeedPlayerWithWikidataQidAsync("Q188207", "Clarence Seedorf");
        _fakeWikidataClient.SetCareerLookup("Clarence Seedorf", new WikidataPlayerCareerLookupResult(
            "Q188207", "Clarence Seedorf", "Netherlands",
            ["AC Milan"]));
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/suggestions/{suggestionId}/lookup", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Found, Is.True);
        Assert.That(body.ExistingPlayerId, Is.EqualTo(existingPlayerId),
            "REQ-515: a matching local Player row for the found WikidataQid must surface its id");
    }

    [Test]
    public async Task REQ515_Lookup_ReturnsNullExistingPlayerId_WhenFoundButNoLocalPlayerYet()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        _fakeWikidataClient.SetCareerLookup("Clarence Seedorf", new WikidataPlayerCareerLookupResult(
            "Q188207", "Clarence Seedorf", "Netherlands",
            ["AC Milan"]));
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/suggestions/{suggestionId}/lookup", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Found, Is.True);
        Assert.That(body.ExistingPlayerId, Is.Null,
            "REQ-515: Found=true with no matching local Player row yet must leave ExistingPlayerId null");
    }

    [Test]
    public async Task REQ515_Lookup_ReturnsNullExistingPlayerId_WhenNotFound()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        // Deliberately not calling SetCareerLookup — the fake's
        // QueryPlayerCareerAndNationalityByNameAsync returns null for any
        // name it wasn't configured for, exercising the Found=false path.
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/suggestions/{suggestionId}/lookup", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Found, Is.False);
        Assert.That(body.ExistingPlayerId, Is.Null,
            "REQ-515: a Found=false response must leave ExistingPlayerId null, consistent with every other field");
    }

    // ---- REQ-509: commit ----------------------------------------------------

    [Test]
    public async Task REQ509_Commit_WritesNationalityOverrideAndClubAttribute_AndResolvesSuggestionAsCommitted()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", ["AC Milan"], "Confirmed via live Wikidata lookup"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<CommitPlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.PlayerCreated, Is.True, "REQ509/S-129: no Player row existed for this WikidataQid before this commit");
        Assert.That(body.Nationality, Is.EqualTo("Netherlands"));
        Assert.That(body.NationalityWritten, Is.True);
        Assert.That(body.ClubsAdded, Is.EquivalentTo(new[] { "AC Milan" }));
        Assert.That(body.ClubsAlreadyEffective, Is.Empty);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = await dbContext.Players.SingleAsync(p => p.WikidataQid == "Q188207");
        var playerOverride = await dbContext.PlayerOverrides.SingleAsync(o => o.PlayerId == player.Id && o.Field == "nationality");
        Assert.That(playerOverride.Value, Is.EqualTo("Netherlands"));
        Assert.That(playerOverride.Reason, Is.EqualTo("Confirmed via live Wikidata lookup"));
        Assert.That(playerOverride.LockedByAdminId, Is.EqualTo(AdminAuthProviderUserId));
        Assert.That(await dbContext.PlayerAttributes.AnyAsync(a => a.PlayerId == player.Id && a.AttributeType == "club" && a.AttributeValue == "AC Milan"), Is.True);

        var suggestion = await dbContext.PlayerSuggestions.SingleAsync(s => s.Id == suggestionId);
        Assert.That(suggestion.Status, Is.EqualTo(PlayerSuggestionStatus.Committed), "REQ-509: never left pending after a commit");
        Assert.That(suggestion.ResolvedByAdminId, Is.EqualTo(AdminAuthProviderUserId));
        Assert.That(suggestion.ResolvedAt, Is.Not.Null);

        // ADR-0053's core non-negotiable guarantee: a commit changes
        // correctness-checking data only, and must NEVER be implemented as a
        // write to the name index (ADR-0007's autocomplete/correctness
        // boundary). Asserted explicitly, not just left as an implicit
        // absence, since a regression here is a real correctness leak.
        Assert.That(await dbContext.PlayerNameIndexEntries.AnyAsync(), Is.False,
            "ADR-0053: committing a suggestion must never write PlayerNameIndex");
    }

    // ADR-0053/ADR-0007: a dedicated, standalone test for the same
    // guarantee as the assertion above, covering the multi-club commit
    // shape too (a future refactor that only checked the single-club path
    // above could otherwise miss a per-club PlayerNameIndex write creeping
    // in alongside the additive PlayerAttribute loop).
    [Test]
    public async Task REQ509_Commit_NeverWritesPlayerNameIndex_EvenWithMultipleClubs()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", ["AC Milan", "Real Madrid", "Inter Milan"], "Confirmed via live Wikidata lookup"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.PlayerNameIndexEntries.AnyAsync(), Is.False,
            "ADR-0053: committing a suggestion must never write PlayerNameIndex, regardless of how many clubs are confirmed");
    }

    // REQ-509: multi-club commit writes each confirmed club as its own
    // additive PlayerAttribute row, and a second commit for a club already
    // effective for the player doesn't duplicate it
    // (HasEffectiveAttributeAsync skip path in CommitPlayerDataAsync).
    [Test]
    public async Task REQ509_Commit_WritesEachConfirmedClubAsSeparateAttribute_AndSkipsAlreadyEffectiveClubOnASecondCommit()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var firstResponse = await client.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", ["AC Milan", "Real Madrid"], "Confirmed via live Wikidata lookup"));
        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<CommitPlayerDataResponse>();
        Assert.That(firstBody!.PlayerCreated, Is.True);
        Assert.That(firstBody.ClubsAdded, Is.EquivalentTo(new[] { "AC Milan", "Real Madrid" }));
        Assert.That(firstBody.ClubsAlreadyEffective, Is.Empty);

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var player = await dbContext.Players.SingleAsync(p => p.WikidataQid == "Q188207");
            var clubAttributes = await dbContext.PlayerAttributes
                .Where(a => a.PlayerId == player.Id && a.AttributeType == "club")
                .Select(a => a.AttributeValue)
                .ToListAsync();
            Assert.That(clubAttributes, Is.EquivalentTo(new[] { "AC Milan", "Real Madrid" }),
                "each confirmed club must be its own additive PlayerAttribute row");
        }

        // A second suggestion for the SAME player (same WikidataQid), one
        // club overlapping (AC Milan, already effective) and one new
        // (Inter Milan) — the standalone REQ-510 path exercises the same
        // shared CommitPlayerDataAsync helper, so either entry point proves
        // the skip-duplicate behavior; this uses REQ-510's path since it
        // needs no second suggestion row.
        var secondResponse = await client.PostAsJsonAsync(
            "/admin/player-search/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", null, ["AC Milan", "Inter Milan"], "Adding one more confirmed club"));
        Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<CommitPlayerDataResponse>();
        Assert.That(secondBody!.PlayerCreated, Is.False, "REQ509/S-129: this WikidataQid already had a Player row from the first commit");
        Assert.That(secondBody.ClubsAdded, Is.EquivalentTo(new[] { "Inter Milan" }), "only the genuinely new club is reported as added");
        Assert.That(secondBody.ClubsAlreadyEffective, Is.EquivalentTo(new[] { "AC Milan" }), "the already-effective club is reported separately, not silently folded into ClubsAdded");
        Assert.That(secondBody.NationalityWritten, Is.False, "no nationality was supplied on this commit");

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var player = await dbContext.Players.SingleAsync(p => p.WikidataQid == "Q188207");
            var clubAttributes = await dbContext.PlayerAttributes
                .Where(a => a.PlayerId == player.Id && a.AttributeType == "club")
                .Select(a => a.AttributeValue)
                .ToListAsync();
            Assert.That(clubAttributes, Is.EquivalentTo(new[] { "AC Milan", "Real Madrid", "Inter Milan" }),
                "AC Milan must not be duplicated — HasEffectiveAttributeAsync's skip path — while the genuinely new Inter Milan is added");
        }
    }

    // REQ-509: nationality commit upserts PlayerOverride — this covers the
    // "existing override gets updated" branch (the "no existing override"
    // branch is already covered by REQ509_Commit_
    // WritesNationalityOverrideAndClubAttribute_AndResolvesSuggestionAsCommitted
    // above).
    [Test]
    public async Task REQ509_Commit_UpdatesExistingNationalityOverride_RatherThanCreatingADuplicate()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var firstSuggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var firstResponse = await client.PostAsJsonAsync(
            $"/admin/suggestions/{firstSuggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", [], "First confirmation"));
        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        Guid overrideId;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var player = await dbContext.Players.SingleAsync(p => p.WikidataQid == "Q188207");
            var existingOverride = await dbContext.PlayerOverrides.SingleAsync(o => o.PlayerId == player.Id && o.Field == "nationality");
            Assert.That(existingOverride.Value, Is.EqualTo("Netherlands"));
            overrideId = existingOverride.Id;
        }

        // A second, independent suggestion for the same player corrects the
        // nationality (e.g. an earlier admin mistake) — REQ-510's standalone
        // path shares the identical CommitPlayerDataAsync write step.
        var secondResponse = await client.PostAsJsonAsync(
            "/admin/player-search/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Suriname", [], "Corrected nationality"));
        Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<CommitPlayerDataResponse>();
        Assert.That(secondBody!.NationalityWritten, Is.True, "REQ509/S-129: the update branch still writes the override, and must be reported as such");
        Assert.That(secondBody.PlayerCreated, Is.False, "the Player row already existed from the first commit");

        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var player = await dbContext.Players.SingleAsync(p => p.WikidataQid == "Q188207");
            var overrides = await dbContext.PlayerOverrides.Where(o => o.PlayerId == player.Id && o.Field == "nationality").ToListAsync();
            Assert.That(overrides, Has.Count.EqualTo(1), "must update the existing override in place, never create a second row for the same field");
            Assert.That(overrides.Single().Id, Is.EqualTo(overrideId), "the same override row is updated, not replaced");
            Assert.That(overrides.Single().Value, Is.EqualTo("Suriname"));
            Assert.That(overrides.Single().Reason, Is.EqualTo("Corrected nationality"), "Reason/audit fields are refreshed on update too");
        }
    }

    [Test]
    public async Task REQ509_Commit_ReturnsConflict_WhenSuggestionAlreadyResolved()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();
        var firstCommit = await client.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", ["AC Milan"], "Confirmed"));
        Assert.That(firstCommit.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var secondCommit = await client.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", ["AC Milan"], "Confirmed again"));

        Assert.That(secondCommit.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task REQ509_Commit_ReturnsBadRequest_WhenReasonMissing()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", ["AC Milan"], ""));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    // ADR-0060's 2026-08-10 status note: a clubs-only commit has nowhere to
    // persist a reason (PlayerAttribute carries no audit columns), so unlike
    // the nationality case above, an empty reason must not be rejected.
    [Test]
    public async Task REQ509_Commit_SucceedsWithoutReason_WhenClubsOnly_NoNationality()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", null, ["AC Milan"], ""));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<CommitPlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Nationality, Is.Null);
        Assert.That(body.NationalityWritten, Is.False, "no nationality was supplied, so no PlayerOverride write should be reported");
        Assert.That(body.ClubsAdded, Is.EquivalentTo(new[] { "AC Milan" }));
        Assert.That(body.ClubsAlreadyEffective, Is.Empty);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = await dbContext.Players.SingleAsync(p => p.WikidataQid == "Q188207");
        Assert.That(await dbContext.PlayerOverrides.AnyAsync(o => o.PlayerId == player.Id), Is.False,
            "a clubs-only commit must never write a PlayerOverride row");
        Assert.That(await dbContext.PlayerAttributes.AnyAsync(a => a.PlayerId == player.Id && a.AttributeType == "club" && a.AttributeValue == "AC Milan"), Is.True);
    }

    // S-129: a genuine full no-op — the player, nationality override, and
    // club attribute all already exist exactly as re-asserted — must be
    // unambiguous in the response: PlayerCreated=false, NationalityWritten=
    // false (nationality omitted here; ADR-0060 doesn't compare/upsert a
    // matching value, so a resubmission of the SAME nationality would still
    // hit the update branch and report NationalityWritten=true — that's
    // still an accurate "yes, a write happened," just not a value CHANGE),
    // and ClubsAdded empty with the club instead in ClubsAlreadyEffective.
    // This is exactly the shape the product gap in this story's own
    // description called out: previously indistinguishable from a real write.
    [Test]
    public async Task REQ509_Commit_ReportsUnambiguousNoOp_WhenPlayerExistsAndAllClubsAlreadyEffectiveAndNoNationalitySupplied()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var firstSuggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var firstResponse = await client.PostAsJsonAsync(
            $"/admin/suggestions/{firstSuggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", ["AC Milan"], "Confirmed"));
        Assert.That(firstResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Second, independent commit for the SAME player/club, no nationality
        // this time — nothing left to write.
        var secondResponse = await client.PostAsJsonAsync(
            "/admin/player-search/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", null, ["AC Milan"], ""));

        Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await secondResponse.Content.ReadFromJsonAsync<CommitPlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.PlayerCreated, Is.False, "the Player row already existed from the first commit");
        Assert.That(body.NationalityWritten, Is.False, "no nationality was supplied on this commit");
        Assert.That(body.ClubsAdded, Is.Empty, "no new PlayerAttribute row was written — the club was already effective");
        Assert.That(body.ClubsAlreadyEffective, Is.EquivalentTo(new[] { "AC Milan" }),
            "the already-effective club must still be reported, just not as newly added — this is what makes the no-op unambiguous rather than silent");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var player = await dbContext.Players.SingleAsync(p => p.WikidataQid == "Q188207");
        Assert.That(await dbContext.PlayerAttributes.CountAsync(a => a.PlayerId == player.Id && a.AttributeType == "club"), Is.EqualTo(1),
            "confirms no duplicate PlayerAttribute row was written by the no-op commit");
    }

    // ---- REQ-509: reject ------------------------------------------------

    [Test]
    public async Task REQ509_Reject_WritesNoPlayerData_AndResolvesSuggestionAsRejected()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/suggestions/{suggestionId}/reject", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.PlayerOverrides.AnyAsync(), Is.False);
        Assert.That(await dbContext.PlayerAttributes.AnyAsync(), Is.False);
        Assert.That(await dbContext.Players.AnyAsync(), Is.False, "a reject must never create a Player row either");
        Assert.That(await dbContext.PlayerNameIndexEntries.AnyAsync(), Is.False,
            "REQ-509: rejecting a suggestion must write no PlayerAttribute/PlayerOverride/PlayerNameIndex row at all");

        var suggestion = await dbContext.PlayerSuggestions.SingleAsync(s => s.Id == suggestionId);
        Assert.That(suggestion.Status, Is.EqualTo(PlayerSuggestionStatus.Rejected));
        Assert.That(suggestion.ResolvedByAdminId, Is.EqualTo(AdminAuthProviderUserId));
        Assert.That(suggestion.ResolvedAt, Is.Not.Null);
    }

    [Test]
    public async Task REQ509_Reject_ReturnsConflict_WhenSuggestionAlreadyResolved()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();
        var firstReject = await client.PostAsync($"/admin/suggestions/{suggestionId}/reject", null);
        Assert.That(firstReject.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var secondReject = await client.PostAsync($"/admin/suggestions/{suggestionId}/reject", null);

        Assert.That(secondReject.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task REQ509_Reject_ReturnsConflict_WhenSuggestionAlreadyCommitted()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();
        var commit = await client.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", ["AC Milan"], "Confirmed"));
        Assert.That(commit.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var reject = await client.PostAsync($"/admin/suggestions/{suggestionId}/reject", null);

        Assert.That(reject.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "a committed suggestion must not also be rejectable");
    }

    [Test]
    public async Task REQ509_Lookup_ReturnsConflict_WhenSuggestionAlreadyResolved()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = CreateAdminClient();
        var reject = await client.PostAsync($"/admin/suggestions/{suggestionId}/reject", null);
        Assert.That(reject.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var lookup = await client.PostAsync($"/admin/suggestions/{suggestionId}/lookup", null);

        Assert.That(lookup.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "a resolved suggestion must not still allow a live re-lookup");
    }

    // ---- REQ-509: Admin policy guardrail, commit/reject specifically ------
    // (the existing AdminSuggestionEndpoint_ReturnsForbidden_
    // ForAuthenticatedNonAdminUser test above only covers GET
    // /admin/suggestions — commit and reject are independently
    // `.RequireAuthorization("Admin")`-gated route registrations, so each
    // needs its own guardrail assertion, same as AdminEndpointTests.cs's own
    // per-endpoint REQ503_*_ReturnsForbidden_ForAuthenticatedNonAdminUser
    // pattern.)

    [Test]
    public async Task REQ509_Commit_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(Guid.NewGuid()));

        var response = await client.PostAsJsonAsync(
            $"/admin/suggestions/{suggestionId}/commit",
            new CommitPlayerDataRequest("Q188207", "Clarence Seedorf", "Netherlands", ["AC Milan"], "Confirmed"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.PlayerOverrides.AnyAsync(), Is.False, "a forbidden request must never reach the write path");
    }

    [Test]
    public async Task REQ509_Reject_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var submittingUserId = await SeedSubmittingUserAsync();
        var suggestionId = await SeedPendingSuggestionAsync(submittingUserId);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(Guid.NewGuid()));

        var response = await client.PostAsync($"/admin/suggestions/{suggestionId}/reject", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var suggestion = await dbContext.PlayerSuggestions.SingleAsync(s => s.Id == suggestionId);
        Assert.That(suggestion.Status, Is.EqualTo(PlayerSuggestionStatus.Pending), "a forbidden request must never resolve the suggestion");
    }

    [Test]
    public async Task REQ510_StandaloneLookup_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(Guid.NewGuid()));

        var response = await client.PostAsJsonAsync("/admin/player-search/lookup", new PlayerSearchLookupRequest("Robert Pires"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task REQ510_StandaloneCommit_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(Guid.NewGuid()));

        var response = await client.PostAsJsonAsync(
            "/admin/player-search/commit",
            new CommitPlayerDataRequest("Qpires", "Robert Pires", "France", ["Arsenal"], "Manually added via admin search"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.Players.AnyAsync(), Is.False, "a forbidden request must never reach the write path");
    }

    // ---- REQ-510: standalone search-and-add ---------------------------------

    [Test]
    public async Task REQ510_StandaloneLookup_ReturnsFetchedData_WithNoSuggestionInvolved()
    {
        _fakeWikidataClient.SetCareerLookup("Robert Pires", new WikidataPlayerCareerLookupResult(
            "Qpires", "Robert Pires", "France", ["Arsenal"]));
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-search/lookup", new PlayerSearchLookupRequest("Robert Pires"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(body!.Found, Is.True);
        Assert.That(body.FullName, Is.EqualTo("Robert Pires"));
    }

    [Test]
    public async Task REQ510_StandaloneLookup_ReturnsServiceUnavailable_NeverSilentNoMatch_WhenWikidataQueryFails()
    {
        _fakeWikidataClient.FailNextCareerLookups(1);
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-search/lookup", new PlayerSearchLookupRequest("Robert Pires"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "ADR-0046: a failed/timed-out lookup must never be silently treated as no-match");
    }

    // Bug fix (2026-08-09) — see REQ509_Lookup_LogsWarning_WhenWikidataQueryFails's
    // own comment for the full "why." This endpoint has no suggestion id to
    // log, so the player name is the distinguishing context instead.
    [Test]
    public async Task REQ510_StandaloneLookup_LogsWarning_WhenWikidataQueryFails()
    {
        _fakeWikidataClient.FailNextCareerLookups(1);
        var client = CreateAdminClientWithLogging(out var loggerProvider);

        var response = await client.PostAsJsonAsync("/admin/player-search/lookup", new PlayerSearchLookupRequest("Robert Pires"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(loggerProvider.Entries, Has.Some.Matches<(LogLevel Level, string Message)>(
            e => e.Level == LogLevel.Warning && e.Message.Contains("Robert Pires")),
            "the exception must be logged at Warning server-side, with the player name for later diagnosis");
    }

    // ---- REQ-515: ExistingPlayerId on the standalone search lookup ---------
    // Same three cases as the suggestion-scoped lookup above, proving
    // LookupPlayerAsync's shared behavior with no endpoint-specific
    // special-casing (REQ-515's own acceptance criterion).

    [Test]
    public async Task REQ515_StandaloneLookup_ReturnsExistingPlayerId_WhenLocalPlayerAlreadyExistsForFoundWikidataQid()
    {
        var existingPlayerId = await SeedPlayerWithWikidataQidAsync("Qpires", "Robert Pires");
        _fakeWikidataClient.SetCareerLookup("Robert Pires", new WikidataPlayerCareerLookupResult(
            "Qpires", "Robert Pires", "France", ["Arsenal"]));
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-search/lookup", new PlayerSearchLookupRequest("Robert Pires"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Found, Is.True);
        Assert.That(body.ExistingPlayerId, Is.EqualTo(existingPlayerId),
            "REQ-515: a matching local Player row for the found WikidataQid must surface its id, identically on the standalone search endpoint");
    }

    [Test]
    public async Task REQ515_StandaloneLookup_ReturnsNullExistingPlayerId_WhenFoundButNoLocalPlayerYet()
    {
        _fakeWikidataClient.SetCareerLookup("Robert Pires", new WikidataPlayerCareerLookupResult(
            "Qpires", "Robert Pires", "France", ["Arsenal"]));
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-search/lookup", new PlayerSearchLookupRequest("Robert Pires"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Found, Is.True);
        Assert.That(body.ExistingPlayerId, Is.Null,
            "REQ-515: Found=true with no matching local Player row yet must leave ExistingPlayerId null, identically on the standalone search endpoint");
    }

    [Test]
    public async Task REQ515_StandaloneLookup_ReturnsNullExistingPlayerId_WhenNotFound()
    {
        // Deliberately not calling SetCareerLookup — see the identical
        // suggestion-scoped test above for why this exercises Found=false.
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync("/admin/player-search/lookup", new PlayerSearchLookupRequest("Robert Pires"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<WikidataPlayerLookupResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Found, Is.False);
        Assert.That(body.ExistingPlayerId, Is.Null,
            "REQ-515: a Found=false response must leave ExistingPlayerId null, identically on the standalone search endpoint");
    }

    [Test]
    public async Task REQ510_StandaloneCommit_WritesPlayerData_WithNoSuggestionRowCreatedOrTouched()
    {
        // A wholly unrelated, still-pending suggestion exists throughout —
        // proves this path doesn't just avoid ITS OWN suggestion (there
        // isn't one), but genuinely never creates or touches
        // ANY PlayerSuggestion row: the count before and after must be
        // identical, and the pre-existing one must stay untouched/Pending.
        var submittingUserId = await SeedSubmittingUserAsync();
        var unrelatedSuggestionId = await SeedPendingSuggestionAsync(submittingUserId, "Someone Else Entirely");
        int suggestionCountBefore;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            suggestionCountBefore = await dbContext.PlayerSuggestions.CountAsync();
        }
        var client = CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            "/admin/player-search/commit",
            new CommitPlayerDataRequest("Qpires", "Robert Pires", "France", ["Arsenal"], "Manually added via admin search"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<CommitPlayerDataResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.PlayerCreated, Is.True, "REQ510/S-129: this WikidataQid is being seen for the first time");
        Assert.That(body.Nationality, Is.EqualTo("France"));
        Assert.That(body.NationalityWritten, Is.True);
        Assert.That(body.ClubsAdded, Is.EquivalentTo(new[] { "Arsenal" }));
        Assert.That(body.ClubsAlreadyEffective, Is.Empty);

        using var scope2 = _factory.Services.CreateScope();
        var dbContext2 = scope2.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext2.Players.AnyAsync(p => p.WikidataQid == "Qpires"), Is.True);
        Assert.That(await dbContext2.PlayerNameIndexEntries.AnyAsync(), Is.False,
            "ADR-0053: REQ-510's standalone commit must never write PlayerNameIndex either");

        var suggestionCountAfter = await dbContext2.PlayerSuggestions.CountAsync();
        Assert.That(suggestionCountAfter, Is.EqualTo(suggestionCountBefore),
            "REQ-510: no suggestion record created as a side effect — count before and after must match exactly");
        var unrelatedSuggestion = await dbContext2.PlayerSuggestions.SingleAsync(s => s.Id == unrelatedSuggestionId);
        Assert.That(unrelatedSuggestion.Status, Is.EqualTo(PlayerSuggestionStatus.Pending), "the pre-existing suggestion must remain untouched");
    }

    // ---- Test double for IWikidataClient -----------------------------------
    // Deliberately NOT the DataSync.Tests project's own internal
    // FakeWikidataClient (a different assembly, no InternalsVisibleTo wired
    // between it and this project) — a minimal local fake instead, same
    // "local fake, swapped via RemoveAll+AddSingleton" precedent as
    // AuthEndpointTests.FakeSupabaseAuthClient. Only
    // QueryPlayerCareerAndNationalityByNameAsync is meaningfully
    // implemented; every other IWikidataClient member is never called by
    // AdminSuggestionEndpoints and stays a trivial stub purely to satisfy
    // the interface.
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

        // ADR-0061: AdminSuggestionEndpoints never calls these — a trivial
        // stub, same as every other intersection method in this fake besides
        // QueryPlayerCareerAndNationalityByNameAsync above.
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

        // ADR-0069: never touched by AdminSuggestionEndpoints (it's
        // PlayerCareerPrefetchService's own prefetch-time method) — a
        // trivial stub, same as QueryPlayerPoolByNationalityAsync above.
        public Task<IReadOnlyList<WikidataNameIndexEntry>> QueryPlayerPoolByClubAsync(
            string clubWikidataQid, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WikidataNameIndexEntry>>([]);

        // S-188: never touched by AdminSuggestionEndpoints (it's
        // RecentTransferSweepService's own sweep-time method) — a trivial
        // stub, same as QueryPlayerPoolByClubAsync above.
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

        // REQ-513 (GitHub issue #239): AdminSuggestionEndpoints never calls
        // this (it's AdminEndpoints' single-player refresh action) — a
        // trivial stub, same as every other method in this fake besides
        // QueryPlayerCareerAndNationalityByNameAsync above.
        public Task<WikidataPlayerRefreshData> QueryPlayerRefreshDataByQidAsync(
            string wikidataQid, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WikidataPlayerRefreshData(null, null, null, null));
    }
}
