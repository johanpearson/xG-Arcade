using Microsoft.EntityFrameworkCore;

namespace XGArcade.Data.Seeding;

// One-time backfill for User.DisplayName (S-011): the migration that added
// this required column defaults existing rows to "" (same pattern
// PlayerNormalizedFullNameBackfiller already established for
// Player.NormalizedFullName), which would show as a blank leaderboard row
// and would fail AuthController's 1-30-char validation if that user ever
// re-triggered it. Unlike NormalizedFullName, there's no way to derive a
// real chosen display name after the fact — the email's local part (before
// "@") is used as a reasonable, always-available fallback, distinct enough
// to be recognizable without silently exposing the full address the way
// showing Email outright on the leaderboard would have. Idempotent: only
// rows with an empty DisplayName are touched.
public static class UserDisplayNameBackfiller
{
    public static async Task BackfillAsync(XGArcadeDbContext dbContext, CancellationToken cancellationToken = default)
    {
        var users = await dbContext.Users.Where(u => u.DisplayName == string.Empty).ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            user.DisplayName = user.Email.Split('@')[0];
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
