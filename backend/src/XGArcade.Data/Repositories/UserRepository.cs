using Microsoft.EntityFrameworkCore;
using Npgsql;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class UserRepository(XGArcadeDbContext dbContext) : IUserRepository
{
    // Matches XGArcadeDbContext's EF-generated index name for the unique
    // index on User.NormalizedDisplayName ("IX_<Table>_<Column>").
    private const string DisplayNameUniqueIndexName = "IX_Users_NormalizedDisplayName";

    public async Task<User?> GetByAuthProviderUserIdAsync(Guid authProviderUserId, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.AuthProviderUserId == authProviderUserId, cancellationToken);

    public async Task<bool> DisplayNameExistsAsync(string displayName, Guid? excludeUserId = null, CancellationToken cancellationToken = default)
    {
        var normalized = User.NormalizeCase(displayName);
        return await dbContext.Users.AsNoTracking()
            .AnyAsync(u => u.NormalizedDisplayName == normalized && (excludeUserId == null || u.Id != excludeUserId), cancellationToken);
    }

    public async Task<User> AddAsync(User user, CancellationToken cancellationToken = default)
    {
        dbContext.Users.Add(user);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: DisplayNameUniqueIndexName })
        {
            throw new DisplayNameAlreadyInUseException(user.DisplayName);
        }

        return user;
    }

    public async Task<User?> UpdateDisplayNameAsync(Guid id, string newDisplayName, CancellationToken cancellationToken = default)
    {
        // Load-then-SaveChangesAsync (docs/coding-guidelines.md): the
        // InMemory provider this codebase's tests run against can't
        // translate ExecuteUpdateAsync.
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return null;

        // User.DisplayName's own setter keeps NormalizedDisplayName in
        // lockstep — see User.cs.
        user.DisplayName = newDisplayName;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: DisplayNameUniqueIndexName })
        {
            throw new DisplayNameAlreadyInUseException(newDisplayName);
        }

        return user;
    }

    // REQ-717: the claim/upgrade path. Load-then-SaveChangesAsync (docs/
    // coding-guidelines.md) — the InMemory provider this codebase's tests
    // run against can't translate ExecuteUpdateAsync, same reason
    // UpdateDisplayNameAsync above doesn't use it either.
    public async Task<User?> ClaimGuestAsync(Guid id, string email, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return null;

        user.Email = email;
        user.IsGuest = false;
        user.ClaimedAt = DateTime.UtcNow;
        // Tier 0: Supabase's confirm-email requirement is off — see
        // MVP-SCOPE.md and AuthController.Signup's identical assignment.
        user.EmailConfirmed = true;
        // REQ-718/ADR-0038: claiming is one of the four activity-tracking
        // events — folded into this same load-then-save rather than a
        // second UpdateLastActiveAtAsync round trip.
        user.LastActiveAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        return user;
    }

    // REQ-718/ADR-0038: Login/GuessEndpoints' shared write path — see this
    // method's own doc comment on IUserRepository for why Signup/Guest/Claim
    // don't call this separately.
    public async Task<User?> UpdateLastActiveAtAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return null;

        user.LastActiveAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<IReadOnlyList<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.ToLowerInvariant();
        // Email is nullable since REQ-717 (a guest has none) — a guest row
        // can never match a real email lookup, so it's excluded up front
        // rather than risking a null.ToLower() call.
        return await dbContext.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == normalized, cancellationToken);
    }

    // REQ-718/ADR-0038 rule 2's own selection query — see IUserRepository's
    // doc comment for the exact condition this mirrors.
    public async Task<IReadOnlyList<User>> GetUnclaimedGuestsOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking()
            .Where(u => u.IsGuest && u.ClaimedAt == null && u.CreatedAt < cutoff)
            .ToListAsync(cancellationToken);

    // REQ-718/ADR-0038 rule 3's own selection query — see IUserRepository's
    // doc comment for the exact condition this mirrors.
    public async Task<IReadOnlyList<User>> GetInactiveGuestsOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking()
            .Where(u => u.IsGuest && u.LastActiveAt < cutoff)
            .ToListAsync(cancellationToken);

    // REQ-507's live "total user count".
    public async Task<int> CountUsersAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().CountAsync(cancellationToken);

    // REQ-507/REQ-508's shared unconditional guest count — see this
    // method's own doc comment on IUserRepository for why the REQ-718
    // age-filtered queries above aren't reused here.
    public async Task<int> CountGuestsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().CountAsync(u => u.IsGuest, cancellationToken);

    // REQ-507's "claimed guest" count.
    public async Task<int> CountClaimedGuestsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().CountAsync(u => u.ClaimedAt != null, cancellationToken);

    // REQ-508's bulk force-clear action own selection query — see this
    // method's own doc comment on IUserRepository for why this is a new,
    // unfiltered query rather than a reuse of the REQ-718 queries above.
    public async Task<IReadOnlyList<Guid>> GetAllGuestIdsAsync(CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().Where(u => u.IsGuest).Select(u => u.Id).ToListAsync(cancellationToken);

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return;

        dbContext.Users.Remove(user);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
