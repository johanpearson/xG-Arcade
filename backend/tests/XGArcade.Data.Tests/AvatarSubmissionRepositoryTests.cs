using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Data.Tests;

// REQ-722 (S-180): AvatarSubmissionRepository's own write behavior — same
// EF Core InMemory-provider unit-test shape as
// AnnouncementBannerRepositoryTests/CategoryValueRepositoryTests, distinct
// from the API-level coverage in
// XGArcade.Api.Tests/AvatarEndpointTests.cs.
public class AvatarSubmissionRepositoryTests
{
    // Always assigned in SetUp before any test body runs — null! is safe here.
    private XGArcadeDbContext _dbContext = null!;
    private IAvatarSubmissionRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<XGArcadeDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new XGArcadeDbContext(options);
        _repository = new AvatarSubmissionRepository(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    [Test]
    public async Task REQ722_GetPendingAsync_ReturnsNull_WhenTheCallerHasNeverSubmittedOne()
    {
        var pending = await _repository.GetPendingAsync(Guid.NewGuid());

        Assert.That(pending, Is.Null);
    }

    [Test]
    public async Task REQ722_CreateOrReplacePendingAsync_CreatesAPendingRow_WhenNoneExistsYet()
    {
        var userId = Guid.NewGuid();
        var createdAt = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        var result = await _repository.CreateOrReplacePendingAsync(userId, "image-key-1", createdAt);

        Assert.That(result.Submission.SubmittingUserId, Is.EqualTo(userId));
        Assert.That(result.Submission.ImageStorageKey, Is.EqualTo("image-key-1"));
        Assert.That(result.Submission.Status, Is.EqualTo(AvatarSubmissionStatus.Pending));
        Assert.That(result.Submission.CreatedAt, Is.EqualTo(createdAt));
        Assert.That(result.ReplacedImageStorageKey, Is.Null, "nothing existed to replace");
        Assert.That(await _dbContext.AvatarSubmissions.CountAsync(), Is.EqualTo(1));
    }

    // REQ-722's own "Given a player already has a submission in Pending
    // status / When they upload again / Then the prior pending submission
    // is replaced by the new one — never two pending submissions queued
    // for the same player at once."
    [Test]
    public async Task REQ722_CreateOrReplacePendingAsync_UploadingWhileAPendingSubmissionExists_ReplacesItRatherThanCreatingASecondOne()
    {
        var userId = Guid.NewGuid();
        var firstResult = await _repository.CreateOrReplacePendingAsync(
            userId, "image-key-original", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        var secondResult = await _repository.CreateOrReplacePendingAsync(
            userId, "image-key-replacement", new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));

        Assert.That(await _dbContext.AvatarSubmissions.CountAsync(), Is.EqualTo(1),
            "REQ-722: a second upload while Pending replaces the existing row, it does not create an additional one");
        Assert.That(secondResult.Submission.Id, Is.Not.EqualTo(firstResult.Submission.Id),
            "the replacement is a brand-new row, not the same one mutated in place");
        Assert.That(secondResult.Submission.ImageStorageKey, Is.EqualTo("image-key-replacement"));
        Assert.That(secondResult.Submission.Status, Is.EqualTo(AvatarSubmissionStatus.Pending));
        Assert.That(secondResult.ReplacedImageStorageKey, Is.EqualTo("image-key-original"),
            "the caller needs the replaced row's storage key to best-effort delete the now-orphaned image");

        var pendingAfter = await _repository.GetPendingAsync(userId);
        Assert.That(pendingAfter, Is.Not.Null);
        Assert.That(pendingAfter!.Id, Is.EqualTo(secondResult.Submission.Id));
    }

    [Test]
    public async Task REQ722_CreateOrReplacePendingAsync_DoesNotAffectAnotherPlayersPendingRow()
    {
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        await _repository.CreateOrReplacePendingAsync(firstUserId, "first-player-image", DateTime.UtcNow);

        await _repository.CreateOrReplacePendingAsync(secondUserId, "second-player-image", DateTime.UtcNow);

        Assert.That(await _dbContext.AvatarSubmissions.CountAsync(), Is.EqualTo(2));
        var firstPending = await _repository.GetPendingAsync(firstUserId);
        Assert.That(firstPending, Is.Not.Null);
        Assert.That(firstPending!.ImageStorageKey, Is.EqualTo("first-player-image"));
    }

    [Test]
    public async Task REQ722_GetApprovedAsync_ReturnsNull_WhenTheCallerHasNoApprovedRow()
    {
        var userId = Guid.NewGuid();
        await _repository.CreateOrReplacePendingAsync(userId, "still-pending", DateTime.UtcNow);

        var approved = await _repository.GetApprovedAsync(userId);

        Assert.That(approved, Is.Null);
    }

    [Test]
    public async Task REQ722_GetApprovedAsync_ReturnsTheApprovedRow_AndIgnoresAPendingRowForTheSamePlayer()
    {
        var userId = Guid.NewGuid();
        var approvedSubmission = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = userId,
            ImageStorageKey = "approved-image",
            Status = AvatarSubmissionStatus.Approved,
            CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        _dbContext.AvatarSubmissions.Add(approvedSubmission);
        await _dbContext.SaveChangesAsync();

        // REQ-722's "Replacing an approved avatar": a fresh upload while an
        // Approved row already exists must never touch that Approved row.
        await _repository.CreateOrReplacePendingAsync(userId, "new-pending-image", new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));

        var approved = await _repository.GetApprovedAsync(userId);
        Assert.That(approved, Is.Not.Null);
        Assert.That(approved!.Id, Is.EqualTo(approvedSubmission.Id));
        Assert.That(approved.ImageStorageKey, Is.EqualTo("approved-image"),
            "REQ-722: uploading a new submission must never touch/clear an existing Approved row");
        Assert.That(await _dbContext.AvatarSubmissions.CountAsync(), Is.EqualTo(2),
            "the Approved row and the new Pending row coexist");
    }

    [Test]
    public async Task REQ722_GetLatestRejectedAsync_ReturnsNull_WhenTheCallerHasNoRejectedRow()
    {
        var userId = Guid.NewGuid();
        await _repository.CreateOrReplacePendingAsync(userId, "still-pending", DateTime.UtcNow);

        var rejected = await _repository.GetLatestRejectedAsync(userId);

        Assert.That(rejected, Is.Null);
    }

    // The one genuinely new piece of logic GetLatestRejectedAsync adds over
    // GetPendingAsync/GetApprovedAsync above: OrderByDescending(CreatedAt) —
    // a player can accumulate more than one Rejected row over time (each
    // rejection is a permanent terminal record, never replaced/removed the
    // way a superseded Pending row is), and "Seeing your own status" (REQ-722)
    // means the most recent one.
    [Test]
    public async Task REQ722_GetLatestRejectedAsync_ReturnsTheMostRecentlyCreatedRejectedRow_WhenMoreThanOneExists()
    {
        var userId = Guid.NewGuid();
        var olderRejected = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = userId,
            ImageStorageKey = "older-rejected-image",
            Status = AvatarSubmissionStatus.Rejected,
            CreatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        var newerRejected = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = userId,
            ImageStorageKey = "newer-rejected-image",
            Status = AvatarSubmissionStatus.Rejected,
            CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        // Added out of chronological order, deliberately, so the assertion
        // can't pass by coincidence of insertion order.
        _dbContext.AvatarSubmissions.AddRange(newerRejected, olderRejected);
        await _dbContext.SaveChangesAsync();

        var rejected = await _repository.GetLatestRejectedAsync(userId);

        Assert.That(rejected, Is.Not.Null);
        Assert.That(rejected!.Id, Is.EqualTo(newerRejected.Id));
        Assert.That(rejected.ImageStorageKey, Is.EqualTo("newer-rejected-image"));
    }

    [Test]
    public async Task REQ722_GetLatestRejectedAsync_IgnoresAPendingOrApprovedRowForTheSamePlayer()
    {
        var userId = Guid.NewGuid();
        var approvedSubmission = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = userId,
            ImageStorageKey = "approved-image",
            Status = AvatarSubmissionStatus.Approved,
            CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        _dbContext.AvatarSubmissions.Add(approvedSubmission);
        await _dbContext.SaveChangesAsync();
        await _repository.CreateOrReplacePendingAsync(userId, "still-pending", new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));

        var rejected = await _repository.GetLatestRejectedAsync(userId);

        Assert.That(rejected, Is.Null, "REQ-722: GetLatestRejectedAsync only matches Status == Rejected, never Pending/Approved");
    }

    [Test]
    public async Task REQ722_GetByIdAsync_ReturnsTheRow_ForAKnownId_RegardlessOfStatus()
    {
        var pendingResult = await _repository.CreateOrReplacePendingAsync(Guid.NewGuid(), "pending-image", DateTime.UtcNow);
        var approvedSubmission = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = Guid.NewGuid(),
            ImageStorageKey = "approved-image",
            Status = AvatarSubmissionStatus.Approved,
            CreatedAt = DateTime.UtcNow,
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = DateTime.UtcNow,
        };
        var rejectedSubmission = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = Guid.NewGuid(),
            ImageStorageKey = "rejected-image",
            Status = AvatarSubmissionStatus.Rejected,
            CreatedAt = DateTime.UtcNow,
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = DateTime.UtcNow,
        };
        _dbContext.AvatarSubmissions.AddRange(approvedSubmission, rejectedSubmission);
        await _dbContext.SaveChangesAsync();

        var foundPending = await _repository.GetByIdAsync(pendingResult.Submission.Id);
        var foundApproved = await _repository.GetByIdAsync(approvedSubmission.Id);
        var foundRejected = await _repository.GetByIdAsync(rejectedSubmission.Id);

        Assert.That(foundPending, Is.Not.Null);
        Assert.That(foundPending!.ImageStorageKey, Is.EqualTo("pending-image"));
        Assert.That(foundApproved, Is.Not.Null);
        Assert.That(foundApproved!.ImageStorageKey, Is.EqualTo("approved-image"));
        Assert.That(foundRejected, Is.Not.Null);
        Assert.That(foundRejected!.ImageStorageKey, Is.EqualTo("rejected-image"));
    }

    [Test]
    public async Task REQ722_GetByIdAsync_ReturnsNull_ForAnUnknownId()
    {
        await _repository.CreateOrReplacePendingAsync(Guid.NewGuid(), "some-image", DateTime.UtcNow);

        var found = await _repository.GetByIdAsync(Guid.NewGuid());

        Assert.That(found, Is.Null);
    }

    // REQ-517/S-181's own acceptance criterion ("Approving supersedes any
    // prior Approved row for that player... never leaving two Approved
    // rows") — S-181's actual admin approve endpoint doesn't exist yet, so
    // this hand-writes the state transition an approve action will need to
    // perform (load the prior Approved row, if any, and move it off
    // Approved in the same write that marks the new row Approved) directly
    // against the repository's existing read/write surface, to prove
    // AvatarSubmission's data model doesn't preclude that invariant. Not
    // full endpoint coverage of S-181, which isn't built by this story.
    [Test]
    public async Task REQ517_ApprovingASubmission_HandWrittenStateTransition_NeverLeavesTwoApprovedRowsForTheSamePlayer()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var originalApproval = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = userId,
            ImageStorageKey = "original-approved-image",
            Status = AvatarSubmissionStatus.Approved,
            CreatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            ResolvedByAdminId = adminId,
            ResolvedAt = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        _dbContext.AvatarSubmissions.Add(originalApproval);
        await _dbContext.SaveChangesAsync();
        var pendingResult = await _repository.CreateOrReplacePendingAsync(
            userId, "new-pending-image", new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));

        // Hand-written approve transition (S-181's future job): the prior
        // Approved row, if any, must move off Approved in the same write
        // that approves the new one — this test picks Rejected as the
        // superseded terminal state (the only other terminal value
        // AvatarSubmissionStatus defines) since a superseded image is, from
        // every reader's perspective, no longer an active/visible avatar,
        // the same end state Rejected already represents.
        var priorApproved = await _repository.GetApprovedAsync(userId);
        Assert.That(priorApproved, Is.Not.Null, "precondition: an Approved row exists before this approval");

        var trackedPrior = await _dbContext.AvatarSubmissions.FirstAsync(a => a.Id == priorApproved!.Id);
        trackedPrior.Status = AvatarSubmissionStatus.Rejected;
        var trackedNew = await _dbContext.AvatarSubmissions.FirstAsync(a => a.Id == pendingResult.Submission.Id);
        trackedNew.Status = AvatarSubmissionStatus.Approved;
        trackedNew.ResolvedByAdminId = adminId;
        trackedNew.ResolvedAt = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        await _dbContext.SaveChangesAsync();

        var approvedRowsAfter = await _dbContext.AvatarSubmissions
            .Where(a => a.SubmittingUserId == userId && a.Status == AvatarSubmissionStatus.Approved)
            .ToListAsync();
        Assert.That(approvedRowsAfter, Has.Count.EqualTo(1),
            "REQ-517: approving a new submission must never leave two Approved rows for the same player");
        Assert.That(approvedRowsAfter[0].Id, Is.EqualTo(pendingResult.Submission.Id));

        var nowApproved = await _repository.GetApprovedAsync(userId);
        Assert.That(nowApproved, Is.Not.Null);
        Assert.That(nowApproved!.ImageStorageKey, Is.EqualTo("new-pending-image"));
    }
}
