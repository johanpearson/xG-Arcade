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

    // REQ-710: AccountDeletionService's entry point — looks up the row by
    // local User.Id (not AuthProviderUserId) so both the self-service path
    // (resolves its own id from the caller's JWT first) and S-026's
    // admin-triggered path (already has the target id from the route) can
    // call the same reusable service.
    Task<User?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-506: an admin identifies the user to delete by email (the only
    // identifier an admin actually has to hand — User.Id is opaque), then
    // resolves it to the local User.Id AccountDeletionService needs.
    // Case-insensitive, matching how Supabase Auth itself treats email.
    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    // REQ-710: permanently removes the local profile row. Callers must
    // anonymize this user's Guess rows (AnonymizeByUserIdAsync) and remove
    // their LeagueMembership rows (ILeagueRepository.RemoveMembershipsByUserIdAsync)
    // *before* calling this — nothing here cascades those on its own. (A
    // real Postgres FK would cascade LeagueMembership automatically, but
    // this codebase's tests run against EF Core's InMemory provider, which
    // doesn't enforce that constraint — see AccountDeletionService, which
    // does both explicitly rather than relying on it.)
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
