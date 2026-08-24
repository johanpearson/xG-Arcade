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
}
