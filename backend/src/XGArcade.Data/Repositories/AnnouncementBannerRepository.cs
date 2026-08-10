using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class AnnouncementBannerRepository(XGArcadeDbContext dbContext) : IAnnouncementBannerRepository
{
    // AsNoTracking: every caller of this method either reads-only (the
    // public endpoint) or re-loads the row itself before mutating it
    // (UpsertMessageAsync/SetActiveAsync below) — same "AsNoTracking for
    // reads, load-then-mutate for writes" split PlayerSuggestionRepository
    // already follows. FirstOrDefaultAsync, not SingleOrDefaultAsync: the
    // singleton invariant is enforced at write time by this repository
    // always mutating the one existing row instead of inserting a second
    // one, not by asserting it here on every read.
    public async Task<AnnouncementBanner?> GetAsync(CancellationToken cancellationToken = default) =>
        await dbContext.AnnouncementBanners
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<AnnouncementBanner> UpsertMessageAsync(
        string message, Guid adminId, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.AnnouncementBanners.FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            existing.Message = message;
            existing.LastUpdatedByAdminId = adminId;
            existing.UpdatedAt = updatedAt;

            // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
            // ExecuteUpdateAsync — the InMemory test provider can't
            // translate it.
            await dbContext.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var banner = new AnnouncementBanner
        {
            Id = Guid.NewGuid(),
            Message = message,
            IsActive = false,
            CreatedAt = updatedAt,
            LastUpdatedByAdminId = adminId,
            UpdatedAt = updatedAt,
        };
        dbContext.AnnouncementBanners.Add(banner);
        await dbContext.SaveChangesAsync(cancellationToken);
        return banner;
    }

    public async Task<AnnouncementBanner?> SetActiveAsync(
        bool isActive, Guid adminId, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.AnnouncementBanners.FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
            return null;

        existing.IsActive = isActive;
        existing.LastUpdatedByAdminId = adminId;
        existing.UpdatedAt = updatedAt;

        // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
        // ExecuteUpdateAsync.
        await dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }
}
