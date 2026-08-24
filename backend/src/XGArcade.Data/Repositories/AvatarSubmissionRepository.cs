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
            // not kept around in some other status (unlike REQ-517/S-181's
            // future approve/reject, which resolves a Pending row into a
            // permanent Approved/Rejected record). A submission superseded
            // before any admin ever saw it has nothing worth keeping.
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
}
