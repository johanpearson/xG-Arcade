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

    // REQ-517/S-181's own acceptance criterion ("Approving supersedes any
    // prior Approved row for that player... never leaving two Approved
    // rows"). Written before ApproveAsync itself existed (S-180), as a
    // hand-written stand-in proving AvatarSubmission's data model didn't
    // preclude the invariant — ApproveAsync now exists (S-181) and is
    // covered directly below (REQ517_ApproveAsync_*); this test is kept
    // as an independent, lower-level proof of the same invariant against
    // the raw DbContext rather than removed.
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

    // ---- REQ-517: GetByIdAsync / GetAllPendingAsync ------------------------

    [Test]
    public async Task REQ517_GetByIdAsync_ReturnsNull_ForUnknownId()
    {
        var result = await _repository.GetByIdAsync(Guid.NewGuid());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task REQ517_GetByIdAsync_ReturnsTheRow_RegardlessOfStatus()
    {
        var userId = Guid.NewGuid();
        var created = await _repository.CreateOrReplacePendingAsync(userId, "some-image", DateTime.UtcNow);

        var result = await _repository.GetByIdAsync(created.Submission.Id);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Id, Is.EqualTo(created.Submission.Id));
    }

    [Test]
    public async Task REQ517_GetAllPendingAsync_ReturnsOnlyPendingRows_OldestFirst()
    {
        var firstUserId = Guid.NewGuid();
        var secondUserId = Guid.NewGuid();
        var thirdUserId = Guid.NewGuid();
        var oldest = await _repository.CreateOrReplacePendingAsync(
            firstUserId, "oldest-image", new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        var newest = await _repository.CreateOrReplacePendingAsync(
            secondUserId, "newest-image", new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));
        _dbContext.AvatarSubmissions.Add(new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = thirdUserId,
            ImageStorageKey = "already-approved",
            Status = AvatarSubmissionStatus.Approved,
            CreatedAt = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc),
        });
        await _dbContext.SaveChangesAsync();

        var pending = await _repository.GetAllPendingAsync();

        Assert.That(pending.Select(p => p.Id), Is.EqualTo(new[] { oldest.Submission.Id, newest.Submission.Id }),
            "REQ-517: oldest first, matching REQ-509's existing pending-suggestion ordering convention, and excludes non-Pending rows");
    }

    [Test]
    public async Task REQ517_GetAllPendingAsync_ReturnsEmpty_WhenNothingIsPending()
    {
        var pending = await _repository.GetAllPendingAsync();

        Assert.That(pending, Is.Empty);
    }

    // ---- REQ-517: ApproveAsync ----------------------------------------------

    [Test]
    public async Task REQ517_ApproveAsync_ReturnsNull_ForUnknownId()
    {
        var result = await _repository.ApproveAsync(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task REQ517_ApproveAsync_SetsApprovedStatusAndAuditFields_AndReturnsNullSupersededKey_WhenNoPriorApproval()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var created = await _repository.CreateOrReplacePendingAsync(userId, "brand-new-image", DateTime.UtcNow);
        var resolvedAt = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        var result = await _repository.ApproveAsync(created.Submission.Id, adminId, resolvedAt);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Submission.Status, Is.EqualTo(AvatarSubmissionStatus.Approved));
        Assert.That(result.Submission.ResolvedByAdminId, Is.EqualTo(adminId));
        Assert.That(result.Submission.ResolvedAt, Is.EqualTo(resolvedAt));
        Assert.That(result.SupersededImageStorageKey, Is.Null, "no prior Approved row existed for this player");

        var reloaded = await _dbContext.AvatarSubmissions.FirstAsync(a => a.Id == created.Submission.Id);
        Assert.That(reloaded.Status, Is.EqualTo(AvatarSubmissionStatus.Approved));
    }

    // REQ-517: "a player has at most one visible avatar at a time."
    [Test]
    public async Task REQ517_ApproveAsync_SupersedesAndDeletesThePriorApprovedRow_ForTheSamePlayer()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var priorApproval = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = userId,
            ImageStorageKey = "old-approved-image",
            Status = AvatarSubmissionStatus.Approved,
            CreatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
        };
        _dbContext.AvatarSubmissions.Add(priorApproval);
        await _dbContext.SaveChangesAsync();
        var created = await _repository.CreateOrReplacePendingAsync(
            userId, "new-image", new DateTime(2026, 8, 24, 0, 0, 0, DateTimeKind.Utc));

        var result = await _repository.ApproveAsync(created.Submission.Id, adminId, DateTime.UtcNow);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SupersededImageStorageKey, Is.EqualTo("old-approved-image"),
            "the caller needs this to best-effort delete the now-orphaned image from storage");

        var approvedRows = await _dbContext.AvatarSubmissions
            .Where(a => a.SubmittingUserId == userId && a.Status == AvatarSubmissionStatus.Approved)
            .ToListAsync();
        Assert.That(approvedRows, Has.Count.EqualTo(1), "REQ-517: never two Approved rows for the same player");
        Assert.That(approvedRows[0].Id, Is.EqualTo(created.Submission.Id));
        Assert.That(await _dbContext.AvatarSubmissions.AnyAsync(a => a.Id == priorApproval.Id), Is.False,
            "the superseded row is deleted outright, matching CreateOrReplacePendingAsync's own replace precedent");
    }

    [Test]
    public async Task REQ517_ApproveAsync_DoesNotAffectAnotherPlayersApprovedRow()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var otherApproval = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = otherUserId,
            ImageStorageKey = "other-players-approved-image",
            Status = AvatarSubmissionStatus.Approved,
            CreatedAt = DateTime.UtcNow.AddDays(-3),
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = DateTime.UtcNow.AddDays(-3),
        };
        _dbContext.AvatarSubmissions.Add(otherApproval);
        await _dbContext.SaveChangesAsync();
        var created = await _repository.CreateOrReplacePendingAsync(userId, "new-image", DateTime.UtcNow);

        var result = await _repository.ApproveAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(result!.SupersededImageStorageKey, Is.Null, "a different player's Approved row must never be treated as superseded");
        var otherReloaded = await _dbContext.AvatarSubmissions.FirstAsync(a => a.Id == otherApproval.Id);
        Assert.That(otherReloaded.Status, Is.EqualTo(AvatarSubmissionStatus.Approved), "another player's Approved row must be untouched");
    }

    // REQ-517: "acting twice on an already-decided submission is a 409, not
    // a silent success" — race-safe re-check at the repository level.
    [Test]
    public async Task REQ517_ApproveAsync_ReturnsNull_WhenTheSubmissionIsAlreadyResolved()
    {
        var userId = Guid.NewGuid();
        var created = await _repository.CreateOrReplacePendingAsync(userId, "some-image", DateTime.UtcNow);
        var firstApprove = await _repository.ApproveAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);
        Assert.That(firstApprove, Is.Not.Null);

        var secondApprove = await _repository.ApproveAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(secondApprove, Is.Null, "an already-Approved submission must not be approved again");
    }

    [Test]
    public async Task REQ517_ApproveAsync_ReturnsNull_WhenTheSubmissionIsAlreadyRejected()
    {
        var userId = Guid.NewGuid();
        var created = await _repository.CreateOrReplacePendingAsync(userId, "some-image", DateTime.UtcNow);
        var rejected = await _repository.RejectAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);
        Assert.That(rejected, Is.True);

        var approve = await _repository.ApproveAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(approve, Is.Null, "a Rejected submission must not become Approved");
    }

    // ---- REQ-517: RejectAsync ------------------------------------------------

    [Test]
    public async Task REQ517_RejectAsync_ReturnsFalse_ForUnknownId()
    {
        var result = await _repository.RejectAsync(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(result, Is.False);
    }

    [Test]
    public async Task REQ517_RejectAsync_SetsRejectedStatusAndAuditFields()
    {
        var userId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var created = await _repository.CreateOrReplacePendingAsync(userId, "some-image", DateTime.UtcNow);
        var resolvedAt = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

        var result = await _repository.RejectAsync(created.Submission.Id, adminId, resolvedAt);

        Assert.That(result, Is.True);
        var reloaded = await _dbContext.AvatarSubmissions.FirstAsync(a => a.Id == created.Submission.Id);
        Assert.That(reloaded.Status, Is.EqualTo(AvatarSubmissionStatus.Rejected));
        Assert.That(reloaded.ResolvedByAdminId, Is.EqualTo(adminId));
        Assert.That(reloaded.ResolvedAt, Is.EqualTo(resolvedAt));
    }

    // REQ-517: "rejecting... the player's previously-approved avatar if any
    // is unchanged."
    [Test]
    public async Task REQ517_RejectAsync_NeverTouchesAPriorApprovedRow_ForTheSamePlayer()
    {
        var userId = Guid.NewGuid();
        var priorApproval = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = userId,
            ImageStorageKey = "still-approved-image",
            Status = AvatarSubmissionStatus.Approved,
            CreatedAt = DateTime.UtcNow.AddDays(-3),
            ResolvedByAdminId = Guid.NewGuid(),
            ResolvedAt = DateTime.UtcNow.AddDays(-3),
        };
        _dbContext.AvatarSubmissions.Add(priorApproval);
        await _dbContext.SaveChangesAsync();
        var created = await _repository.CreateOrReplacePendingAsync(userId, "new-image-to-reject", DateTime.UtcNow);

        var result = await _repository.RejectAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(result, Is.True);
        var approvedReloaded = await _dbContext.AvatarSubmissions.FirstAsync(a => a.Id == priorApproval.Id);
        Assert.That(approvedReloaded.Status, Is.EqualTo(AvatarSubmissionStatus.Approved));
        Assert.That(approvedReloaded.ImageStorageKey, Is.EqualTo("still-approved-image"));
        Assert.That(await _dbContext.AvatarSubmissions.CountAsync(), Is.EqualTo(2), "the prior Approved row must still exist, not be deleted");
    }

    [Test]
    public async Task REQ517_RejectAsync_ReturnsFalse_WhenTheSubmissionIsAlreadyResolved()
    {
        var userId = Guid.NewGuid();
        var created = await _repository.CreateOrReplacePendingAsync(userId, "some-image", DateTime.UtcNow);
        var firstReject = await _repository.RejectAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);
        Assert.That(firstReject, Is.True);

        var secondReject = await _repository.RejectAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(secondReject, Is.False, "an already-Rejected submission must not be rejected again");
    }

    [Test]
    public async Task REQ517_RejectAsync_ReturnsFalse_WhenTheSubmissionIsAlreadyApproved()
    {
        var userId = Guid.NewGuid();
        var created = await _repository.CreateOrReplacePendingAsync(userId, "some-image", DateTime.UtcNow);
        var approved = await _repository.ApproveAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);
        Assert.That(approved, Is.Not.Null);

        var reject = await _repository.RejectAsync(created.Submission.Id, Guid.NewGuid(), DateTime.UtcNow);

        Assert.That(reject, Is.False, "an Approved submission must not also be rejectable");
    }
}
