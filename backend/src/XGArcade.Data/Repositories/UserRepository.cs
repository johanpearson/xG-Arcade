using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class UserRepository(XGArcadeDbContext dbContext) : IUserRepository
{
    public async Task<User?> GetByAuthProviderUserIdAsync(Guid authProviderUserId, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.AuthProviderUserId == authProviderUserId, cancellationToken);

    public async Task<User> AddAsync(User user, CancellationToken cancellationToken = default)
    {
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<IReadOnlyList<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);
}
