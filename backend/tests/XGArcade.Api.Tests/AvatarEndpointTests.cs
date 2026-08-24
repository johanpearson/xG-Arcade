using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Auth;
using XGArcade.Api.Avatars;
using XGArcade.Core.Storage;
using XGArcade.Data;
using XGArcade.Data.Entities;

namespace XGArcade.Api.Tests;

// REQ-722/ADR-0087 (S-180): API-level coverage for POST /users/me/avatar.
// Same in-memory-DbContext-swap/local-e2e-auth pattern as
// IncidentEndpointTests, with IAvatarStorage swapped for FakeAvatarStorage
// — this suite must never call the real Supabase Storage REST API.
public class AvatarEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private readonly FakeAvatarStorage _fakeAvatarStorage = new();

    [SetUp]
    public void SetUp()
    {
        _fakeAvatarStorage.UploadedContentTypes.Clear();
        _fakeAvatarStorage.DeletedStorageKeys.Clear();

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

                    services.RemoveAll<IAvatarStorage>();
                    services.AddSingleton<IAvatarStorage>(_fakeAvatarStorage);
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private async Task<Guid> SeedUserAsync(Guid authProviderUserId, bool isGuest = false)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = authProviderUserId,
            Email = isGuest ? null : $"{authProviderUserId}@example.com",
            DisplayName = isGuest ? $"Guest{Guid.NewGuid():N}"[..12] : "Test Player",
            EmailConfirmed = !isGuest,
            IsGuest = isGuest,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    private HttpClient CreateAuthenticatedClient(Guid authProviderUserId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalE2EAuth.MintToken(authProviderUserId));
        return client;
    }

    private static MultipartFormDataContent BuildUploadContent(byte[] bytes, string contentType, string fileName = "avatar.jpg")
    {
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        var form = new MultipartFormDataContent { { fileContent, "file", fileName } };
        return form;
    }

    private static byte[] SmallImageBytes(int length = 16) => Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

    // ---- REQ-722: unauthenticated ---------------------------------------

    [Test]
    public async Task REQ722_Avatar_Post_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/jpeg"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_fakeAvatarStorage.UploadedContentTypes, Is.Empty);
    }

    [Test]
    public async Task REQ722_Avatar_Post_ReturnsUnauthorized_ForTokenWithNoMatchingLocalUser()
    {
        var client = CreateAuthenticatedClient(Guid.NewGuid());

        var response = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/jpeg"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(_fakeAvatarStorage.UploadedContentTypes, Is.Empty);
    }

    // ---- REQ-722: guests are allowed, unlike REQ-215/REQ-903 -------------

    [Test]
    public async Task REQ722_Avatar_Post_GuestAccount_IsAllowed_UnlikeSuggestionsOrIncidentReports()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId, isGuest: true);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/jpeg"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
    }

    // ---- REQ-722: size/type validation -----------------------------------

    [Test]
    public async Task REQ722_Avatar_Post_ReturnsBadRequest_ForAnEmptyFile()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsync("/users/me/avatar", BuildUploadContent([], "image/jpeg"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("An image file is required"));
        Assert.That(_fakeAvatarStorage.UploadedContentTypes, Is.Empty);
    }

    [Test]
    public async Task REQ722_Avatar_Post_ReturnsBadRequest_ForAFileOverTheSizeLimit()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);
        var tooLarge = SmallImageBytes((int)AvatarEndpoints.MaxImageSizeBytes + 1);

        var response = await client.PostAsync("/users/me/avatar", BuildUploadContent(tooLarge, "image/jpeg"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Image too large"));
        Assert.That(_fakeAvatarStorage.UploadedContentTypes, Is.Empty);
    }

    [TestCase("image/gif")]
    [TestCase("image/svg+xml")]
    [TestCase("application/pdf")]
    [TestCase("text/plain")]
    public async Task REQ722_Avatar_Post_ReturnsBadRequest_ForAnUnsupportedContentType(string contentType)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), contentType));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Unsupported image type"));
        Assert.That(_fakeAvatarStorage.UploadedContentTypes, Is.Empty);
    }

    // ---- REQ-722: happy path ---------------------------------------------

    [TestCase("image/jpeg")]
    [TestCase("image/png")]
    [TestCase("image/webp")]
    public async Task REQ722_Avatar_Post_ValidUpload_CreatesAPendingSubmission(string contentType)
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), contentType));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        var body = await response.Content.ReadFromJsonAsync<SubmitAvatarResponse>();
        Assert.That(body!.Status, Is.EqualTo(nameof(AvatarSubmissionStatus.Pending)));
        Assert.That(_fakeAvatarStorage.UploadedContentTypes, Is.EqualTo(new[] { contentType }));
    }

    // ---- REQ-722: storage failure (the "Failed to fetch" bug fix) --------

    // Before this fix, AvatarStorage.UploadAsync's exception propagated
    // unhandled out of the handler while `file`'s multipart body was still
    // being read — the one call in this file with no try/catch, unlike the
    // superseded-image DeleteAsync below it. Reproduced here via
    // FakeAvatarStorage.ThrowOnUpload rather than a real network failure
    // (this suite never touches real storage); the client-visible symptom
    // this test guards against is documented on the handler's own catch
    // block in AvatarEndpoints.cs.
    [Test]
    public async Task REQ722_Avatar_Post_ReturnsServiceUnavailable_WhenStorageUploadFails()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);
        _fakeAvatarStorage.ThrowOnUpload = true;

        var response = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/jpeg"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.That(problem!.Title, Is.EqualTo("Avatar upload unavailable"));
        // Never the raw exception message — this is a player-facing
        // endpoint, not the /internal/* narrow exception
        // docs/coding-guidelines.md carves out.
        Assert.That(problem.Detail, Does.Not.Contain("Simulated"));
    }

    // REQ-722's own "Given a player already has a submission in Pending
    // status / When they upload again / Then the prior pending submission
    // is replaced by the new one — never two pending submissions queued
    // for the same player at once."
    [Test]
    public async Task REQ722_Avatar_Post_SecondUploadWhilePending_ReplacesRatherThanDuplicates()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var firstResponse = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/jpeg"));
        var firstBody = await firstResponse.Content.ReadFromJsonAsync<SubmitAvatarResponse>();

        var secondResponse = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/png"));
        var secondBody = await secondResponse.Content.ReadFromJsonAsync<SubmitAvatarResponse>();

        Assert.That(secondResponse.StatusCode, Is.EqualTo(HttpStatusCode.Created));
        Assert.That(secondBody!.Id, Is.Not.EqualTo(firstBody!.Id), "the replacement is a new row, not the same one");

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var submissions = await dbContext.AvatarSubmissions.Where(a => a.SubmittingUserId == userId).ToListAsync();
        Assert.That(submissions, Has.Count.EqualTo(1),
            "REQ-722: a second upload while Pending replaces the existing row, it does not create an additional one");
        Assert.That(submissions[0].Id, Is.EqualTo(secondBody.Id));

        // REQ-722: the now-orphaned first image is best-effort deleted from
        // storage once it's replaced.
        Assert.That(_fakeAvatarStorage.DeletedStorageKeys, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task REQ722_Avatar_Post_NeverTouchesAnExistingApprovedRow()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.AvatarSubmissions.Add(new AvatarSubmission
            {
                Id = Guid.NewGuid(),
                SubmittingUserId = userId,
                ImageStorageKey = "already-approved-image",
                Status = AvatarSubmissionStatus.Approved,
                CreatedAt = DateTime.UtcNow.AddDays(-3),
                ResolvedByAdminId = Guid.NewGuid(),
                ResolvedAt = DateTime.UtcNow.AddDays(-3),
            });
            await dbContext.SaveChangesAsync();
        }
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/jpeg"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDbContext = verifyScope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var approved = await verifyDbContext.AvatarSubmissions
            .SingleAsync(a => a.SubmittingUserId == userId && a.Status == AvatarSubmissionStatus.Approved);
        Assert.That(approved.ImageStorageKey, Is.EqualTo("already-approved-image"),
            "REQ-722: uploading a new submission must never touch/clear an existing Approved row");
        Assert.That(_fakeAvatarStorage.DeletedStorageKeys, Is.Empty,
            "no Pending row existed to replace, so nothing should have been deleted from storage");
    }

    // ---- REQ-722 (S-182): GET /users/me/avatar — own status -------------
    // Minimal smoke coverage only, added because this diff can't be
    // compiled/run locally (no dotnet SDK in this sandbox) — formal
    // REQ-722 read-path coverage is test-writer's own task, not this one.

    [Test]
    public async Task REQ722_AvatarStatus_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/users/me/avatar");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task REQ722_AvatarStatus_Get_ReturnsAllNull_WhenNoSubmissionsExist()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/users/me/avatar");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AvatarStatusResponse>();
        Assert.That(body!.Pending, Is.Null);
        Assert.That(body.Rejected, Is.Null);
        Assert.That(body.Approved, Is.Null);
    }

    // REQ-722's own "a Rejected status does not remove or affect a
    // separately-existing Approved avatar from an earlier, different
    // submission" — and symmetrically, a fresh Pending upload doesn't hide
    // an older Approved row either. All three must be reported at once.
    [Test]
    public async Task REQ722_AvatarStatus_Get_ReportsPendingRejectedAndApproved_Independently()
    {
        var authProviderUserId = Guid.NewGuid();
        var userId = await SeedUserAsync(authProviderUserId);
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            dbContext.AvatarSubmissions.AddRange(
                new AvatarSubmission
                {
                    Id = Guid.NewGuid(),
                    SubmittingUserId = userId,
                    ImageStorageKey = "approved-key",
                    Status = AvatarSubmissionStatus.Approved,
                    CreatedAt = DateTime.UtcNow.AddDays(-5),
                    ResolvedByAdminId = Guid.NewGuid(),
                    ResolvedAt = DateTime.UtcNow.AddDays(-5),
                },
                new AvatarSubmission
                {
                    Id = Guid.NewGuid(),
                    SubmittingUserId = userId,
                    ImageStorageKey = "rejected-key",
                    Status = AvatarSubmissionStatus.Rejected,
                    CreatedAt = DateTime.UtcNow.AddDays(-2),
                    ResolvedByAdminId = Guid.NewGuid(),
                    ResolvedAt = DateTime.UtcNow.AddDays(-2),
                },
                new AvatarSubmission
                {
                    Id = Guid.NewGuid(),
                    SubmittingUserId = userId,
                    ImageStorageKey = "pending-key",
                    Status = AvatarSubmissionStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                });
            await dbContext.SaveChangesAsync();
        }
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync("/users/me/avatar");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<AvatarStatusResponse>();
        Assert.That(body!.Pending, Is.Not.Null);
        Assert.That(body.Rejected, Is.Not.Null);
        Assert.That(body.Approved, Is.Not.Null);
        Assert.That(body.Pending!.ImageUrl, Is.EqualTo($"/users/me/avatar/{body.Pending.Id}/image"));
    }

    // ---- REQ-722 (S-182): GET /users/me/avatar/{id}/image ----------------

    [Test]
    public async Task REQ722_AvatarImage_Get_StreamsBytes_ForOwnPendingSubmission()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var uploadResponse = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/png"));
        var uploadBody = await uploadResponse.Content.ReadFromJsonAsync<SubmitAvatarResponse>();

        var imageResponse = await client.GetAsync($"/users/me/avatar/{uploadBody!.Id}/image");

        Assert.That(imageResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(imageResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
        var bytes = await imageResponse.Content.ReadAsByteArrayAsync();
        Assert.That(bytes, Is.EqualTo(SmallImageBytes()));
    }

    [Test]
    public async Task REQ722_AvatarImage_Get_ReturnsNotFound_ForAnotherPlayersSubmission()
    {
        var ownerAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(ownerAuthProviderUserId);
        var ownerClient = CreateAuthenticatedClient(ownerAuthProviderUserId);
        var uploadResponse = await ownerClient.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/png"));
        var uploadBody = await uploadResponse.Content.ReadFromJsonAsync<SubmitAvatarResponse>();

        var otherAuthProviderUserId = Guid.NewGuid();
        await SeedUserAsync(otherAuthProviderUserId);
        var otherClient = CreateAuthenticatedClient(otherAuthProviderUserId);

        var response = await otherClient.GetAsync($"/users/me/avatar/{uploadBody!.Id}/image");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
            "REQ-722: a Pending/Rejected image is never shown to anyone but the submitting player — 404, never 403, so existence isn't leaked");
    }

    [Test]
    public async Task REQ722_AvatarImage_Get_ReturnsNotFound_ForAnUnknownSubmissionId()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var response = await client.GetAsync($"/users/me/avatar/{Guid.NewGuid()}/image");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    // Sibling to REQ722_AvatarStatus_Get_ReturnsUnauthorized_WithoutBearerToken
    // above, for the image endpoint — no equivalent existed for
    // GET /users/me/avatar/{id}/image.
    [Test]
    public async Task REQ722_AvatarImage_Get_ReturnsUnauthorized_WithoutBearerToken()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/users/me/avatar/{Guid.NewGuid()}/image");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // The row exists and is owned by the caller (GetByIdAsync succeeds),
    // but the underlying storage object it points at is gone — the
    // `image is null` branch in AvatarEndpoints.cs, distinct from the
    // "no such row"/"someone else's row" 404s covered above.
    [Test]
    public async Task REQ722_AvatarImage_Get_ReturnsNotFound_WhenStorageObjectIsMissingForAnOwnedRow()
    {
        var authProviderUserId = Guid.NewGuid();
        await SeedUserAsync(authProviderUserId);
        var client = CreateAuthenticatedClient(authProviderUserId);

        var uploadResponse = await client.PostAsync("/users/me/avatar", BuildUploadContent(SmallImageBytes(), "image/png"));
        var uploadBody = await uploadResponse.Content.ReadFromJsonAsync<SubmitAvatarResponse>();

        string storageKey;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
            var submission = await dbContext.AvatarSubmissions.SingleAsync(a => a.Id == uploadBody!.Id);
            storageKey = submission.ImageStorageKey;
        }

        // Simulate the storage object having gone missing (e.g. deleted
        // out-of-band in the bucket) while the DB row still references it
        // — directly against the fake, not through the API, since this
        // isn't reachable via any endpoint.
        await _fakeAvatarStorage.DeleteAsync(storageKey);

        var imageResponse = await client.GetAsync($"/users/me/avatar/{uploadBody!.Id}/image");

        Assert.That(imageResponse.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
