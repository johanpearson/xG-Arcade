using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-01 (Core.Users): the only path to the local User profile table.
public interface IUserRepository
{
    Task<User?> GetByAuthProviderUserIdAsync(Guid authProviderUserId, CancellationToken cancellationToken = default);

    // REQ-701: AuthController.Signup's pre-check, before Supabase Auth is
    // ever called — case-insensitive, matching the DB's unique index on
    // NormalizedDisplayName. REQ-714: AuthController.UpdateDisplayName
    // reuses this exact same check for an edit, passing excludeUserId so a
    // no-op resubmission of the caller's own current name (including a
    // pure-casing change) is never treated as a conflict against itself.
    Task<bool> DisplayNameExistsAsync(string displayName, Guid? excludeUserId = null, CancellationToken cancellationToken = default);

    // Throws DisplayNameAlreadyInUseException if the DB's unique index
    // rejects the insert — the race window behind DisplayNameExistsAsync's
    // own pre-check.
    Task<User> AddAsync(User user, CancellationToken cancellationToken = default);

    // REQ-714: loads the row by local User.Id (tracked, not AsNoTracking
    // like the other getters here — this write needs a tracked entity),
    // updates DisplayName (whose own setter keeps NormalizedDisplayName in
    // lockstep — see User.cs), and saves. Returns null if no such user
    // exists (caller should already have resolved the row, so this is only
    // a defensive race window, not the expected path). Throws
    // DisplayNameAlreadyInUseException on the DB's unique-index race
    // fallback, the same pattern AddAsync already uses — the window between
    // AuthController.UpdateDisplayName's own DisplayNameExistsAsync
    // pre-check and this save.
    Task<User?> UpdateDisplayNameAsync(Guid id, string newDisplayName, CancellationToken cancellationToken = default);

    // REQ-717: the claim/upgrade path — sets Email, clears IsGuest, and
    // stamps ClaimedAt on the caller's own row (resolved by AuthController.
    // Claim from the caller's own JWT, same as every other authenticated
    // endpoint here). Load-then-SaveChangesAsync, same pattern as
    // UpdateDisplayNameAsync — never ExecuteUpdateAsync (docs/coding-
    // guidelines.md). Returns null if no such user exists (defensive only;
    // the caller should already have resolved this row via
    // GetByAuthProviderUserIdAsync before calling this). Never touches
    // Guess/LeagueMembership rows — REQ-717 is explicit that claiming is an
    // in-place identity conversion, not a re-link.
    Task<User?> ClaimGuestAsync(Guid id, string email, CancellationToken cancellationToken = default);

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
