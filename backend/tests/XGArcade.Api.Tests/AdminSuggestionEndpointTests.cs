using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

                    var inMemoryDatabaseName = Guid.NewGuid().ToString();
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

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
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

    [Test]
    public async Task REQ509_Lookup_ReturnsNotFound_ForUnknownSuggestionId()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/suggestions/{Guid.NewGuid()}/lookup", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
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
        Assert.That(body!.Nationality, Is.EqualTo("Netherlands"));
        Assert.That(body.Clubs, Is.EquivalentTo(new[] { "AC Milan" }));

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
        Assert.That(body!.Nationality, Is.EqualTo("France"));

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

        public Task<IReadOnlyDictionary<string, int>> QuerySitelinkCountsByQidsAsync(
            IReadOnlyList<string> wikidataQids, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<WikidataPlayerPhotoLookupResult?> QueryPlayerPhotoByNameAsync(
            string playerName, CancellationToken cancellationToken = default) =>
            Task.FromResult<WikidataPlayerPhotoLookupResult?>(null);
    }
}
