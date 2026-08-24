using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class AvatarSubmissionRepository(XGArcadeDbContext dbContext) : IAvatarSubmissionRepository
{
    public async Task<AvatarSubmission?> GetPendingAsync(Guid submittingUserId, CancellationToken cancellationToken = default) =>
        await dbContext.AvatarSubmissions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.SubmittingUserId == submittingUserId && a.Status == AvatarSubmissionStatus.Pending, cancellationToken);

    public async Task<AvatarSubmission?> GetApprovedAsync(Guid submittingUserId, CancellationToken cancellationToken = default) =>
        await dbContext.AvatarSubmissions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.SubmittingUserId == submittingUserId && a.Status == AvatarSubmissionStatus.Approved, cancellationToken);

    // REQ-722 (S-182): most-recent by CreatedAt — see this method's own doc
    // comment on IAvatarSubmissionRepository for why "most recent" and why
    // this is independent of GetApprovedAsync above.
    public async Task<AvatarSubmission?> GetLatestRejectedAsync(Guid submittingUserId, CancellationToken cancellationToken = default) =>
        await dbContext.AvatarSubmissions
            .AsNoTracking()
            .Where(a => a.SubmittingUserId == submittingUserId && a.Status == AvatarSubmissionStatus.Rejected)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<AvatarSubmissionCreationResult> CreateOrReplacePendingAsync(
        Guid submittingUserId, string imageStorageKey, DateTime createdAt, CancellationToken cancellationToken = default)
    {
        // Tracked (not AsNoTracking) — this row is about to be removed by
        // the change tracker below.
        var existingPending = await dbContext.AvatarSubmissions
            .FirstOrDefaultAsync(a => a.SubmittingUserId == submittingUserId && a.Status == AvatarSubmissionStatus.Pending, cancellationToken);

        string? replacedImageStorageKey = null;
        if (existingPending is not null)
        {
            // REQ-722: "never two pending submissions queued for the same
            // player at once" — the prior Pending row is fully replaced,
            // not kept around in some other status (unlike REQ-517's
            // ApproveAsync/RejectAsync below, which resolve a Pending row
            // into a permanent Approved/Rejected record). A submission
            // superseded before any admin ever saw it has nothing worth
            // keeping.
            replacedImageStorageKey = existingPending.ImageStorageKey;
            dbContext.AvatarSubmissions.Remove(existingPending);
        }

        var submission = new AvatarSubmission
        {
            Id = Guid.NewGuid(),
            SubmittingUserId = submittingUserId,
            ImageStorageKey = imageStorageKey,
            Status = AvatarSubmissionStatus.Pending,
            CreatedAt = createdAt,
        };
        dbContext.AvatarSubmissions.Add(submission);

        // One SaveChangesAsync for both the delete and the insert — same
        // "one SaveChangesAsync per logical write" discipline
        // PlayerSuggestionRepository.AddAsync's own comment documents.
        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteDeleteAsync — the InMemory test provider can't translate it.
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AvatarSubmissionCreationResult(submission, replacedImageStorageKey);
    }

    public async Task<AvatarSubmission?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.AvatarSubmissions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<AvatarSubmission>> GetAllPendingAsync(CancellationToken cancellationToken = default) =>
        await dbContext.AvatarSubmissions
            .AsNoTracking()
            .Where(a => a.Status == AvatarSubmissionStatus.Pending)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<AvatarSubmissionApprovalResult?> ApproveAsync(
        Guid id, Guid adminId, DateTime resolvedAt, CancellationToken cancellationToken = default)
    {
        // Tracked (not AsNoTracking) — both this row and (below) any prior
        // Approved row are about to be mutated/removed by the change
        // tracker.
        var submission = await dbContext.AvatarSubmissions.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (submission is null || submission.Status != AvatarSubmissionStatus.Pending)
            return null;

        // REQ-517: "a player has at most one visible avatar at a time" —
        // supersede any prior Approved row for the same player. See this
        // interface's own ApproveAsync doc comment for why this deletes
        // the row outright rather than introducing a new status value.
        var priorApproved = await dbContext.AvatarSubmissions
            .FirstOrDefaultAsync(
                a => a.SubmittingUserId == submission.SubmittingUserId && a.Status == AvatarSubmissionStatus.Approved,
                cancellationToken);

        string? supersededImageStorageKey = null;
        if (priorApproved is not null)
        {
            supersededImageStorageKey = priorApproved.ImageStorageKey;
            dbContext.AvatarSubmissions.Remove(priorApproved);
        }

        submission.Status = AvatarSubmissionStatus.Approved;
        submission.ResolvedByAdminId = adminId;
        submission.ResolvedAt = resolvedAt;

        // One SaveChangesAsync for both the supersede-delete and the
        // approval write, same "one SaveChangesAsync per logical write"
        // discipline CreateOrReplacePendingAsync above already establishes.
        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync/ExecuteDeleteAsync — the InMemory test
        // provider can't translate them.
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AvatarSubmissionApprovalResult(submission, supersededImageStorageKey);
    }

    public async Task<bool> RejectAsync(Guid id, Guid adminId, DateTime resolvedAt, CancellationToken cancellationToken = default)
    {
        var submission = await dbContext.AvatarSubmissions.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (submission is null || submission.Status != AvatarSubmissionStatus.Pending)
            return false;

        // REQ-517: deliberately no lookup of any prior Approved row here —
        // rejecting must never touch it (see this interface's own
        // RejectAsync doc comment).
        submission.Status = AvatarSubmissionStatus.Rejected;
        submission.ResolvedByAdminId = adminId;
        submission.ResolvedAt = resolvedAt;

        // Load-then-SaveChangesAsync, never ExecuteUpdateAsync — same
        // reasoning as ApproveAsync above.
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
