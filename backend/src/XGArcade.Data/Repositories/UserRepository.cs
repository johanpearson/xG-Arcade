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

    public async Task<IReadOnlyList<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);

    public async Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.ToLowerInvariant();
        return await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
            return;

        dbContext.Users.Remove(user);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
