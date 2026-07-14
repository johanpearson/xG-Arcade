using XGArcade.Data.Repositories;

namespace XGArcade.Core.Auth;

// COMP-01 (Core.Users): REQ-710's anonymize-don't-hard-delete account
// deletion, built as reusable service logic (not endpoint-only code) so
// S-026's admin-triggered deletion (docs/backlog.md) can call the same
// path rather than a second, independently-written implementation —
// callers identify the target by local User.Id, not by anything only the
// account owner would have (a password, a JWT), so both the self-service
// endpoint (resolves its own id from the caller's JWT first) and an
// admin endpoint (already has the target id from the route) can use it
// identically. Any caller-specific confirmation step (self-service
// re-verifies the caller's password; an admin path would use its own
// authorization/confirmation) belongs in the calling endpoint, not here.
public interface IAccountDeletionService
{
    Task<AccountDeletionResult> DeleteAccountAsync(Guid userId, CancellationToken cancellationToken = default);
}

public record AccountDeletionResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

// Order matches implementation-document.md §6.8's documented flow: anonymize
// Guess rows, remove LeagueMembership rows, delete the local User row, then
// delete the Supabase Auth identity last. NotificationPreference (also named
// in that flow and in REQ-710) has no Tier 0 table yet — Resend/notification
// preferences are Tier 1 (MVP-SCOPE.md) — so that step is a no-op here, not
// silently skipped without explanation.
public class AccountDeletionService(
    IUserRepository userRepository,
    IGuessRepository guessRepository,
    ILeagueRepository leagueRepository,
    ISupabaseAuthClient authClient) : IAccountDeletionService
{
    public async Task<AccountDeletionResult> DeleteAccountAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await userRepository.GetByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return new AccountDeletionResult { Success = false, ErrorMessage = "User not found." };
        }

        // REQ-710: sever the link rather than deleting the rows — other
        // players' historical uniqueness scores and leaderboard totals
        // depend on the total guess count staying intact.
        await guessRepository.AnonymizeByUserIdAsync(user.Id, cancellationToken);

        await leagueRepository.RemoveMembershipsByUserIdAsync(user.Id, cancellationToken);

        await userRepository.DeleteAsync(user.Id, cancellationToken);

        // Deleting the Supabase Auth identity is last and, unlike the local
        // writes above, isn't part of the same database transaction — a
        // genuinely separate, non-transactional external call. If it fails
        // here, the local account data is already gone but the credential
        // (and the email address it holds) is not; this is a real,
        // acknowledged gap at MVP scale (implementation-document.md §6.8
        // notes no background job/saga is built for this), surfaced to the
        // caller as a failure rather than swallowed, so it's visible instead
        // of silently leaving a stranded identity.
        var deleted = await authClient.DeleteUserAsync(user.AuthProviderUserId, cancellationToken);
        if (!deleted)
        {
            // Logged by the caller (e.g. AuthController.DeleteAccount), which
            // already has an ILogger and the request context — this service
            // stays a plain library with no logging dependency of its own.
            return new AccountDeletionResult
            {
                Success = false,
                ErrorMessage = "Account data was removed, but the credential could not be deleted. Contact support.",
            };
        }

        return new AccountDeletionResult { Success = true };
    }
}
