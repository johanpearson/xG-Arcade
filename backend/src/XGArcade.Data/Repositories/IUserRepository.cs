using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-01 (Core.Users): the only path to the local User profile table.
public interface IUserRepository
{
    Task<User?> GetByAuthProviderUserIdAsync(Guid authProviderUserId, CancellationToken cancellationToken = default);

    // REQ-701: AuthController.Signup's pre-check, before Supabase Auth is
    // ever called — case-insensitive, matching the DB's unique index on
    // NormalizedDisplayName.
    Task<bool> DisplayNameExistsAsync(string displayName, CancellationToken cancellationToken = default);

    // Throws DisplayNameAlreadyInUseException if the DB's unique index
    // rejects the insert — the race window behind DisplayNameExistsAsync's
    // own pre-check.
    Task<User> AddAsync(User user, CancellationToken cancellationToken = default);

    // REQ-404's leaderboard: resolves every member's DisplayName in one
    // query rather than one round-trip per row.
    Task<IReadOnlyList<User>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
}
