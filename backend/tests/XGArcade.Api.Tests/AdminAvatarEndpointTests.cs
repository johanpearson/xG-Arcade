using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XGArcade.Api.Admin;
using XGArcade.Api.Auth;
using XGArcade.Core.Storage;
using XGArcade.Data;
using XGArcade.Data.Entities;

namespace XGArcade.Api.Tests;

// REQ-517 (S-181): API-level coverage for the admin avatar moderation
// endpoints (list pending / approve / reject) — same in-memory-DbContext-
// swap/local-e2e-auth/Admin__UserIds pattern as AdminSuggestionEndpointTests,
// plus a swapped-in FakeAvatarStorage (same fake AvatarEndpointTests already
// uses) so no test here ever calls the real Supabase Storage REST API.
public class AdminAvatarEndpointTests
{
    private static readonly Guid AdminAuthProviderUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // Always assigned in SetUp before any test body runs — null! is safe here.
    private WebApplicationFactory<Program> _factory = null!;
    private readonly FakeAvatarStorage _fakeAvatarStorage = new();

    [SetUp]
    public void SetUp()
    {
        _fakeAvatarStorage.UploadedContentTypes.Clear();
        _fakeAvatarStorage.DeletedStorageKeys.Clear();
        _fakeAvatarStorage.PreviewUrlRequests.Clear();

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

                    services.RemoveAll<IAvatarStorage>();
                    services.AddSingleton<IAvatarStorage>(_fakeAvatarStorage);
                });
            });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    // ---- Seeding helpers ----------------------------------------------

    private async Task<Guid> SeedUserAsync(string displayName = "Submitting Player")
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            AuthProviderUserId = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@example.com",
            DisplayName = displayName,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedAvatarSubmissionAsync(
        Guid submittingUserId,
        AvatarSubmissionStatus status = AvatarSubmissionStatus.Pending,
        string imageStorageKey = "image-key",
        DateTime? createdAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var submission = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = submittingUserId,
            ImageStorageKey = imageStorageKey,
            Status = status,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            ResolvedByAdminId = status == AvatarSubmissionStatus.Pending ? null : Guid.NewGuid(),
            ResolvedAt = status == AvatarSubmissionStatus.Pending ? null : DateTime.UtcNow,
        };
        dbContext.AvatarSubmissions.Add(submission);
        await dbContext.SaveChangesAsync();
        return submission.Id;
    }

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

    // ---- Admin policy guardrail --------------------------------------------

    [Test]
    public async Task AdminAvatarEndpoints_List_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var response = await CreateNonAdminClient().GetAsync("/admin/avatar-submissions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task AdminAvatarEndpoints_Approve_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var userId = await SeedUserAsync();
        var submissionId = await SeedAvatarSubmissionAsync(userId);

        var response = await CreateNonAdminClient().PostAsync($"/admin/avatar-submissions/{submissionId}/approve", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var submission = await dbContext.AvatarSubmissions.SingleAsync(a => a.Id == submissionId);
        Assert.That(submission.Status, Is.EqualTo(AvatarSubmissionStatus.Pending), "a forbidden request must never resolve the submission");
    }

    [Test]
    public async Task AdminAvatarEndpoints_Reject_ReturnsForbidden_ForAuthenticatedNonAdminUser()
    {
        var userId = await SeedUserAsync();
        var submissionId = await SeedAvatarSubmissionAsync(userId);

        var response = await CreateNonAdminClient().PostAsync($"/admin/avatar-submissions/{submissionId}/reject", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task AdminAvatarEndpoints_List_ReturnsUnauthorized_WithoutBearerToken()
    {
        var response = await _factory.CreateClient().GetAsync("/admin/avatar-submissions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    // ---- REQ-517: listing pending submissions ------------------------------

    [Test]
    public async Task REQ517_GetPendingAvatarSubmissions_ReturnsPreviewSubmitterAndTimestamp()
    {
        var userId = await SeedUserAsync("Submitting Player");
        var submissionId = await SeedAvatarSubmissionAsync(userId, imageStorageKey: "the-image-key");
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/avatar-submissions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<PendingAvatarSubmissionResponse>>();
        Assert.That(body, Is.Not.Null);
        var row = body!.Single();
        Assert.That(row.Id, Is.EqualTo(submissionId));
        Assert.That(row.SubmittingUserId, Is.EqualTo(userId));
        Assert.That(row.SubmittingUserDisplayName, Is.EqualTo("Submitting Player"));
        Assert.That(row.ImagePreviewUrl, Is.EqualTo("https://fake-storage.test/the-image-key"),
            "the resolved preview URL, never the raw storage key");
        Assert.That(_fakeAvatarStorage.PreviewUrlRequests, Is.EqualTo(new[] { "the-image-key" }));
    }

    [Test]
    public async Task REQ517_GetPendingAvatarSubmissions_ReturnsOnlyPendingRows_OldestFirst()
    {
        var userA = await SeedUserAsync("Player A");
        var userB = await SeedUserAsync("Player B");
        var userC = await SeedUserAsync("Player C");
        var oldestId = await SeedAvatarSubmissionAsync(
            userA, imageStorageKey: "oldest-key", createdAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        var newestId = await SeedAvatarSubmissionAsync(
            userB, imageStorageKey: "newest-key", createdAt: new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));
        await SeedAvatarSubmissionAsync(
            userC, status: AvatarSubmissionStatus.Approved, imageStorageKey: "already-approved-key");
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/avatar-submissions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<PendingAvatarSubmissionResponse>>();
        Assert.That(body!.Select(r => r.Id), Is.EqualTo(new[] { oldestId, newestId }),
            "REQ-517: oldest first, matching REQ-509's existing ordering convention, and only Pending rows");
    }

    [Test]
    public async Task REQ517_GetPendingAvatarSubmissions_ReturnsNullDisplayName_WhenSubmittingUserWasHardDeleted()
    {
        var deletedUserId = Guid.NewGuid();
        await SeedAvatarSubmissionAsync(deletedUserId);
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/avatar-submissions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<PendingAvatarSubmissionResponse>>();
        Assert.That(body!.Single().SubmittingUserDisplayName, Is.Null,
            "SubmittingUserId has no FK — a hard-deleted user must resolve to a null display name, not an error");
    }

    [Test]
    public async Task REQ517_GetPendingAvatarSubmissions_ReturnsEmptyList_WhenNothingIsPending()
    {
        var client = CreateAdminClient();

        var response = await client.GetAsync("/admin/avatar-submissions");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        var body = await response.Content.ReadFromJsonAsync<List<PendingAvatarSubmissionResponse>>();
        Assert.That(body, Is.Empty);
    }

    // ---- REQ-517: approve -----------------------------------------------

    [Test]
    public async Task REQ517_Approve_ReturnsNotFound_ForUnknownId()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/avatar-submissions/{Guid.NewGuid()}/approve", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ517_Approve_SetsApprovedStatus_AndRemovesFromThePendingQueue()
    {
        var userId = await SeedUserAsync();
        var submissionId = await SeedAvatarSubmissionAsync(userId);
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/approve", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var submission = await dbContext.AvatarSubmissions.SingleAsync(a => a.Id == submissionId);
        Assert.That(submission.Status, Is.EqualTo(AvatarSubmissionStatus.Approved));
        Assert.That(submission.ResolvedByAdminId, Is.EqualTo(AdminAuthProviderUserId));
        Assert.That(submission.ResolvedAt, Is.Not.Null);

        var listResponse = await client.GetAsync("/admin/avatar-submissions");
        var body = await listResponse.Content.ReadFromJsonAsync<List<PendingAvatarSubmissionResponse>>();
        Assert.That(body, Is.Empty, "an approved submission must leave the pending queue");
    }

    // REQ-517: "if the same player already had a previously-approved
    // avatar, the new one replaces it — a player has at most one visible
    // avatar at a time."
    [Test]
    public async Task REQ517_Approve_SupersedesAndBestEffortDeletesThePriorApprovedImage_ForTheSamePlayer()
    {
        var userId = await SeedUserAsync();
        var priorApprovedId = await SeedAvatarSubmissionAsync(
            userId, status: AvatarSubmissionStatus.Approved, imageStorageKey: "old-approved-image");
        var submissionId = await SeedAvatarSubmissionAsync(userId, imageStorageKey: "new-image");
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/approve", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var approvedRows = await dbContext.AvatarSubmissions
            .Where(a => a.SubmittingUserId == userId && a.Status == AvatarSubmissionStatus.Approved)
            .ToListAsync();
        Assert.That(approvedRows, Has.Count.EqualTo(1), "REQ-517: never two Approved rows for the same player");
        Assert.That(approvedRows[0].Id, Is.EqualTo(submissionId));
        Assert.That(await dbContext.AvatarSubmissions.AnyAsync(a => a.Id == priorApprovedId), Is.False,
            "the superseded row is removed, matching CreateOrReplacePendingAsync's own replace precedent");
        Assert.That(_fakeAvatarStorage.DeletedStorageKeys, Is.EqualTo(new[] { "old-approved-image" }),
            "the now-orphaned prior image is best-effort deleted from storage");
    }

    [Test]
    public async Task REQ517_Approve_DoesNotDeleteAnything_WhenNoPriorApprovedRowExists()
    {
        var userId = await SeedUserAsync();
        var submissionId = await SeedAvatarSubmissionAsync(userId, imageStorageKey: "new-image");
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/approve", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(_fakeAvatarStorage.DeletedStorageKeys, Is.Empty);
    }

    [Test]
    public async Task REQ517_Approve_ReturnsConflict_WhenAlreadyApproved()
    {
        var userId = await SeedUserAsync();
        var submissionId = await SeedAvatarSubmissionAsync(userId);
        var client = CreateAdminClient();
        var firstApprove = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/approve", null);
        Assert.That(firstApprove.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var secondApprove = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/approve", null);

        Assert.That(secondApprove.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
            "REQ-517: acting twice on an already-decided submission is a 409, not a silent success");
    }

    [Test]
    public async Task REQ517_Approve_ReturnsConflict_WhenAlreadyRejected()
    {
        var userId = await SeedUserAsync();
        var submissionId = await SeedAvatarSubmissionAsync(userId);
        var client = CreateAdminClient();
        var reject = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/reject", null);
        Assert.That(reject.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var approve = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/approve", null);

        Assert.That(approve.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "a rejected submission must not become approvable");
    }

    // ---- REQ-517: reject ------------------------------------------------

    [Test]
    public async Task REQ517_Reject_ReturnsNotFound_ForUnknownId()
    {
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/avatar-submissions/{Guid.NewGuid()}/reject", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task REQ517_Reject_SetsRejectedStatus_AndRemovesFromThePendingQueue()
    {
        var userId = await SeedUserAsync();
        var submissionId = await SeedAvatarSubmissionAsync(userId);
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/reject", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var submission = await dbContext.AvatarSubmissions.SingleAsync(a => a.Id == submissionId);
        Assert.That(submission.Status, Is.EqualTo(AvatarSubmissionStatus.Rejected));
        Assert.That(submission.ResolvedByAdminId, Is.EqualTo(AdminAuthProviderUserId));
        Assert.That(submission.ResolvedAt, Is.Not.Null);
        Assert.That(_fakeAvatarStorage.DeletedStorageKeys, Is.Empty, "rejecting must never touch/delete an image");
    }

    // REQ-517: "the player's previously-approved avatar if any is
    // unchanged" — rejecting a new submission must never touch a prior
    // Approved row, unlike approve.
    [Test]
    public async Task REQ517_Reject_NeverTouchesAPriorApprovedRow_ForTheSamePlayer()
    {
        var userId = await SeedUserAsync();
        var priorApprovedId = await SeedAvatarSubmissionAsync(
            userId, status: AvatarSubmissionStatus.Approved, imageStorageKey: "still-approved-image");
        var submissionId = await SeedAvatarSubmissionAsync(userId, imageStorageKey: "image-to-reject");
        var client = CreateAdminClient();

        var response = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/reject", null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<XGArcadeDbContext>();
        var priorApproved = await dbContext.AvatarSubmissions.SingleAsync(a => a.Id == priorApprovedId);
        Assert.That(priorApproved.Status, Is.EqualTo(AvatarSubmissionStatus.Approved));
        Assert.That(priorApproved.ImageStorageKey, Is.EqualTo("still-approved-image"));
        Assert.That(_fakeAvatarStorage.DeletedStorageKeys, Is.Empty, "no image should be deleted by a reject");
    }

    [Test]
    public async Task REQ517_Reject_ReturnsConflict_WhenAlreadyRejected()
    {
        var userId = await SeedUserAsync();
        var submissionId = await SeedAvatarSubmissionAsync(userId);
        var client = CreateAdminClient();
        var firstReject = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/reject", null);
        Assert.That(firstReject.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var secondReject = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/reject", null);

        Assert.That(secondReject.StatusCode, Is.EqualTo(HttpStatusCode.Conflict),
            "REQ-517: acting twice on an already-decided submission is a 409, not a silent success");
    }

    [Test]
    public async Task REQ517_Reject_ReturnsConflict_WhenAlreadyApproved()
    {
        var userId = await SeedUserAsync();
        var submissionId = await SeedAvatarSubmissionAsync(userId);
        var client = CreateAdminClient();
        var approve = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/approve", null);
        Assert.That(approve.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var reject = await client.PostAsync($"/admin/avatar-submissions/{submissionId}/reject", null);

        Assert.That(reject.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "an approved submission must not also be rejectable");
    }
}
