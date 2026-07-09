using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-01 (Core.Users): the only path to the local User profile table.
public interface IUserRepository
{
    Task<User?> GetByAuthProviderUserIdAsync(Guid authProviderUserId, CancellationToken cancellationToken = default);
    Task<User> AddAsync(User user, CancellationToken cancellationToken = default);
}
