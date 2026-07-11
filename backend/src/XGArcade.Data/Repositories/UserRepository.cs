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

    public async Task<bool> DisplayNameExistsAsync(string displayName, CancellationToken cancellationToken = default)
    {
        var normalized = displayName.ToLowerInvariant();
        return await dbContext.Users.AsNoTracking().AnyAsync(u => u.NormalizedDisplayName == normalized, cancellationToken);
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

    public async Task<IReadOnlyList<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);
}
