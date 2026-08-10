using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XGArcade.Api.Admin;
using XGArcade.Api.Announcements;
using XGArcade.Api.Auth;
using XGArcade.Data;

namespace XGArcade.Api.Tests;

// REQ-511 (docs/requirements-document.md): API-level coverage for the
// site-wide announcement banner — the public, unauthenticated
// GET /announcement-banner (Announcements/AnnouncementBannerEndpoints.cs)
// and the admin-only PUT/activate/deactivate/GET
// /admin/announcement-banner (Admin/AdminAnnouncementBannerEndpoints.cs).
// Same in-memory-DbContext-swap/local-e2e-auth/Admin__UserIds pattern as
// AdminEndpointTests/AdminSuggestionEndpointTests — the closest existing
// analogues for a small admin-adjacent + public-read feature (per this
// story's own brief). The repository's own singleton-upsert-not-insert
// logic has a dedicated, lower-level unit test in
// XGArcade.Data.Tests/AnnouncementBannerRepositoryTests.cs; this file
// covers the endpoint wiring — validation, status codes, and the
// Admin-policy authorization boundary — on top of it.
public class AnnouncementBannerEndpointTests
{
    // Fixed so every test can configure the same "this is an admin" identity
    // via Admin:UserIds without re-creating the factory per test.
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
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
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(AdminAuthProviderUserId));
        return client;
    }

    private HttpClient CreateNonAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(Guid.NewGuid()));
        return client;
    }

    // ---- REQ-511: public read — no authentication of any kind -------------

    [Test]
    public async Task REQ511_Get_ReturnsOkActiveFalseAndNullMessage_WhenNoBannerHasEverBeenCreated()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/announcement-banner");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "REQ-511: never an error, even with no banner ever created");
        var body = await response.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Active, Is.False);
        Assert.That(body.Message, Is.Null);
    }

    [Test]
    public async Task REQ511_Get_ReturnsOkActiveFalseAndNullMessage_WhenTheOnlyBannerOnRecordIsInactive()
    {
        var adminClient = CreateAdminClient();
        await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Never activated."));
        var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync("/announcement-banner");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(body!.Active, Is.False);
        Assert.That(body.Message, Is.Null, "REQ-511: 'no active banner' collapses inactive-with-text to a null message too");
    }

    [Test]
    public async Task REQ511_Get_ReturnsOkActiveTrueWithMessage_WhenAnActiveBannerExists()
    {
        var adminClient = CreateAdminClient();
        await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Scheduled maintenance tonight."));
        await adminClient.PostAsync("/admin/announcement-banner/activate", null);
        var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync("/announcement-banner");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(body!.Active, Is.True);
        Assert.That(body.Message, Is.EqualTo("Scheduled maintenance tonight."));
    }

    [Test]
    public async Task REQ511_Get_RequiresNoAuthentication_AndWorksWithNoBearerTokenAtAll()
    {
        // A bare HttpClient with no Authorization header whatsoever — the
        // "fully logged-out visitor with no session at all" case, distinct
        // from a client carrying an invalid/expired token.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/announcement-banner");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---- REQ-511: creating and editing --------------------------------------

    [Test]
    public async Task REQ511_Put_CreatesTheBanner_StartingInactive_WhenNoneExisted()
    {
        var client = CreateAdminClient();

        var response = await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("First banner text."));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Message, Is.EqualTo("First banner text."));
        Assert.That(body.IsActive, Is.False);
        Assert.That(body.LastUpdatedByAdminId, Is.EqualTo(AdminAuthProviderUserId));
    }

    [Test]
    public async Task REQ511_Put_ReplacesTheExistingBannersMessage_RatherThanCreatingASecondRecord()
    {
        var client = CreateAdminClient();
        var first = await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Original message."));
        var firstBody = await first.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();

        var second = await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Replacement message."));

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var secondBody = await second.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();
        Assert.That(secondBody!.Id, Is.EqualTo(firstBody!.Id), "the same singleton row is replaced in place, never a second one");
        Assert.That(secondBody.Message, Is.EqualTo("Replacement message."));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.AnnouncementBanners.CountAsync(), Is.EqualTo(1),
            "REQ-511: there is exactly one banner record at a time, never a list or queue of concurrent banners");
    }

    [Test]
    public async Task REQ511_Put_EditingAnAlreadyActiveBanner_KeepsItActive_AndSubsequentReadsSeeTheNewText()
    {
        var adminClient = CreateAdminClient();
        await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Original text."));
        await adminClient.PostAsync("/admin/announcement-banner/activate", null);

        var editResponse = await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Updated text."));

        Assert.That(editResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var editedBody = await editResponse.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();
        Assert.That(editedBody!.IsActive, Is.True,
            "REQ-511: an edit to an already-active banner does not require a separate deactivate/reactivate step");

        var publicResponse = await _factory.CreateClient().GetAsync("/announcement-banner");
        var publicBody = await publicResponse.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(publicBody!.Active, Is.True);
        Assert.That(publicBody.Message, Is.EqualTo("Updated text."),
            "REQ-511: the updated text is what subsequent visitors see on their next fetch");
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task REQ511_Put_RejectsBlankOrWhitespaceMessage_ReturnsBadRequest_AndDoesNotChangeTheStoredBanner(string message)
    {
        var client = CreateAdminClient();
        await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Untouched original."));

        var response = await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest(message));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid announcement banner"));

        var adminGet = await client.GetAsync("/admin/announcement-banner");
        var stored = await adminGet.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();
        Assert.That(stored!.Message, Is.EqualTo("Untouched original."), "a rejected blank message must not change the stored banner");
    }

    // Unlike IncidentEndpoints.TitleMaxLength etc. (public const, referenced
    // directly by IncidentEndpointTests), AdminAnnouncementBannerEndpoints
    // .MaxMessageLength is private — this test hardcodes the literal 500
    // read directly from that file rather than changing its accessibility
    // just for a test to reference it.
    [Test]
    public async Task REQ511_Put_RejectsMessageOverTheMaxLength_ReturnsBadRequest_AndDoesNotChangeTheStoredBanner()
    {
        var client = CreateAdminClient();
        await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Untouched original."));
        var tooLong = new string('a', 501);

        var response = await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest(tooLong));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Invalid announcement banner"));

        var adminGet = await client.GetAsync("/admin/announcement-banner");
        var stored = await adminGet.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();
        Assert.That(stored!.Message, Is.EqualTo("Untouched original."));
    }

    [Test]
    public async Task REQ511_Put_AcceptsAMessageExactlyAtTheMaxLength()
    {
        var client = CreateAdminClient();
        var exactlyMax = new string('a', 500);

        var response = await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest(exactlyMax));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---- REQ-511: activating and deactivating -------------------------------

    [Test]
    public async Task REQ511_Activate_MakesTheBannerVisibleToTheNextPublicRead()
    {
        var adminClient = CreateAdminClient();
        await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Going live."));
        var beforeActivate = await _factory.CreateClient().GetAsync("/announcement-banner");
        var beforeBody = await beforeActivate.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(beforeBody!.Active, Is.False, "sanity check: newly-created banners start inactive");

        var activateResponse = await adminClient.PostAsync("/admin/announcement-banner/activate", null);

        Assert.That(activateResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var activateBody = await activateResponse.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();
        Assert.That(activateBody!.IsActive, Is.True);

        var afterActivate = await _factory.CreateClient().GetAsync("/announcement-banner");
        var afterBody = await afterActivate.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(afterBody!.Active, Is.True);
        Assert.That(afterBody.Message, Is.EqualTo("Going live."));
    }

    [Test]
    public async Task REQ511_Deactivate_HidesTheBannerFromTheNextPublicRead_ButPreservesItsSavedMessage()
    {
        var adminClient = CreateAdminClient();
        await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Take me down."));
        await adminClient.PostAsync("/admin/announcement-banner/activate", null);

        var deactivateResponse = await adminClient.PostAsync("/admin/announcement-banner/deactivate", null);

        Assert.That(deactivateResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var deactivateBody = await deactivateResponse.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();
        Assert.That(deactivateBody!.IsActive, Is.False);
        Assert.That(deactivateBody.Message, Is.EqualTo("Take me down."),
            "REQ-511: deactivating does not delete the banner's saved message");

        var publicRead = await _factory.CreateClient().GetAsync("/announcement-banner");
        var publicBody = await publicRead.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(publicBody!.Active, Is.False, "REQ-511: it stops being visible to every visitor the next time they fetch it");

        // REQ-511: "an admin can reactivate the same text later... without
        // retyping it from scratch" — proves the saved text really is still
        // there to reactivate, not just present in the deactivate response.
        var reactivateResponse = await adminClient.PostAsync("/admin/announcement-banner/activate", null);
        var reactivateBody = await reactivateResponse.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();
        Assert.That(reactivateBody!.Message, Is.EqualTo("Take me down."));
        Assert.That(reactivateBody.IsActive, Is.True);
    }

    [Test]
    public async Task REQ511_Activate_ReturnsNotFound_WhenNoBannerHasEverBeenCreated()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsync("/admin/announcement-banner/activate", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ511_Deactivate_ReturnsNotFound_WhenNoBannerHasEverBeenCreated()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsync("/admin/announcement-banner/deactivate", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // ---- REQ-511: admin GET (full state, including audit fields) -----------

    [Test]
    public async Task REQ511_AdminGet_ReturnsNotFound_WhenNoBannerHasEverBeenCreated()
    {
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/announcement-banner");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ511_AdminGet_ReturnsTheFullShape_IncludingIsActiveAndAuditFields()
    {
        var client = CreateAdminClient();
        await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Audit me."));
        await client.PostAsync("/admin/announcement-banner/activate", null);

        var response = await client.GetAsync("/admin/announcement-banner");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AdminAnnouncementBannerResponse>();
        Assert.That(body!.Message, Is.EqualTo("Audit me."));
        Assert.That(body.IsActive, Is.True);
        Assert.That(body.LastUpdatedByAdminId, Is.EqualTo(AdminAuthProviderUserId));
        Assert.That(body.CreatedAt, Is.Not.EqualTo(default(DateTime)));
        Assert.That(body.UpdatedAt, Is.Not.EqualTo(default(DateTime)));
    }

    // ---- REQ-511: authorization boundary on write actions -------------------
    // Every write action (PUT, activate, deactivate) is independently
    // `.RequireAuthorization("Admin")`-gated, same as AdminSuggestionEndpointTests'
    // own per-endpoint guardrail pattern — each needs its own 401/403 pair,
    // plus a "no state change on rejection" assertion.

    [Test]
    public async Task REQ511_Put_ReturnsUnauthorized_WithoutBearerToken_AndDoesNotCreateABanner()
    {
        var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Should never be stored."));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.AnnouncementBanners.AnyAsync(), Is.False);
    }

    [Test]
    public async Task REQ511_Put_ReturnsForbidden_ForAuthenticatedNonAdminUser_AndDoesNotCreateABanner()
    {
        var client = CreateNonAdminClient();

        var response = await client.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Should never be stored."));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        Assert.That(await dbContext.AnnouncementBanners.AnyAsync(), Is.False);
    }

    [Test]
    public async Task REQ511_Activate_ReturnsUnauthorized_WithoutBearerToken_AndDoesNotChangeState()
    {
        var adminClient = CreateAdminClient();
        await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Stays inactive."));
        var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.PostAsync("/admin/announcement-banner/activate", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        var publicRead = await _factory.CreateClient().GetAsync("/announcement-banner");
        var publicBody = await publicRead.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(publicBody!.Active, Is.False, "a rejected activate request must never change banner state");
    }

    [Test]
    public async Task REQ511_Activate_ReturnsForbidden_ForAuthenticatedNonAdminUser_AndDoesNotChangeState()
    {
        var adminClient = CreateAdminClient();
        await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Stays inactive."));
        var nonAdminClient = CreateNonAdminClient();

        var response = await nonAdminClient.PostAsync("/admin/announcement-banner/activate", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        var publicRead = await _factory.CreateClient().GetAsync("/announcement-banner");
        var publicBody = await publicRead.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(publicBody!.Active, Is.False, "a rejected activate request must never change banner state");
    }

    [Test]
    public async Task REQ511_Deactivate_ReturnsUnauthorized_WithoutBearerToken_AndDoesNotChangeState()
    {
        var adminClient = CreateAdminClient();
        await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Stays active."));
        await adminClient.PostAsync("/admin/announcement-banner/activate", null);
        var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.PostAsync("/admin/announcement-banner/deactivate", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        var publicRead = await _factory.CreateClient().GetAsync("/announcement-banner");
        var publicBody = await publicRead.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(publicBody!.Active, Is.True, "a rejected deactivate request must never change banner state");
    }

    [Test]
    public async Task REQ511_Deactivate_ReturnsForbidden_ForAuthenticatedNonAdminUser_AndDoesNotChangeState()
    {
        var adminClient = CreateAdminClient();
        await adminClient.PutAsJsonAsync("/admin/announcement-banner", new UpsertAnnouncementBannerRequest("Stays active."));
        await adminClient.PostAsync("/admin/announcement-banner/activate", null);
        var nonAdminClient = CreateNonAdminClient();

        var response = await nonAdminClient.PostAsync("/admin/announcement-banner/deactivate", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));

        var publicRead = await _factory.CreateClient().GetAsync("/announcement-banner");
        var publicBody = await publicRead.Content.ReadFromJsonAsync<AnnouncementBannerResponse>();
        Assert.That(publicBody!.Active, Is.True, "a rejected deactivate request must never change banner state");
    }

    [Test]
    public async Task REQ511_AdminGet_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/announcement-banner");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ511_AdminGet_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var client = CreateNonAdminClient();

        var response = await client.GetAsync("/admin/announcement-banner");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }
}
