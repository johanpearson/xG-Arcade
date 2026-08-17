using XGArcade.Api.Internal;
using XGArcade.Core.Auth;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Auth;

// REQ-718/ADR-0038: the scheduled-purge half of guest account cleanup —
// AuthController.Logout's best-effort delete-at-logout is the other half,
// and this endpoint is the safety net for whenever that call never reaches
// the backend or fails. Runs in every environment (including a future real
// Production), following the exact same bearer-token pattern
// InternalRoundEndpoints established for Tier 0's only other
// production-scheduled trigger (generate-grid-round.yml/
// generate-path-round.yml, split from a single generate-round.yml as of
// S-136/ADR-0072, calling /internal/generate-round) — see that file's own
// comment for why an
// environment gate doesn't apply to a bearer-token-gated /internal/*
// endpoint like this one.
public static class InternalGuestCleanupEndpoints
{
    public static void MapInternalGuestCleanupEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/purge-guest-accounts", async (
            HttpContext httpContext,
            IConfiguration configuration,
            IUserRepository userRepository,
            IAccountDeletionService accountDeletionService,
            TimeProvider timeProvider,
            ILogger<GuestCleanupLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            if (!InternalJobAuthorization.IsAuthorized(httpContext.Request, configuration))
                return Results.Unauthorized();

            try
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;

                // REQ-718 rule 2: unclaimed for more than 30 days since creation.
                var unclaimedGuests = await userRepository.GetUnclaimedGuestsOlderThanAsync(now.AddDays(-30), cancellationToken);

                // REQ-718 rule 3: no LastActiveAt activity for more than 7
                // days — deliberately no ClaimedAt condition (see
                // GetInactiveGuestsOlderThanAsync's own doc comment): claiming
                // already clears IsGuest, so a claimed account can never
                // match this query regardless of how old LastActiveAt later
                // becomes.
                var inactiveGuests = await userRepository.GetInactiveGuestsOlderThanAsync(now.AddDays(-7), cancellationToken);

                // A row can satisfy both rules at once (an unclaimed guest
                // that's also been inactive) — deduped here so it's only
                // ever deleted once, via the exact same
                // IAccountDeletionService path either rule would have used on
                // its own (ADR-0038: never a second deletion path for
                // guests).
                var deletedUserIds = new HashSet<Guid>();

                foreach (var user in unclaimedGuests)
                {
                    if (await TryDeleteAsync(user.Id, accountDeletionService, logger, cancellationToken))
                        deletedUserIds.Add(user.Id);
                }

                foreach (var user in inactiveGuests)
                {
                    if (deletedUserIds.Contains(user.Id))
                        continue;

                    if (await TryDeleteAsync(user.Id, accountDeletionService, logger, cancellationToken))
                        deletedUserIds.Add(user.Id);
                }

                return Results.Ok(new PurgeGuestAccountsResponse(
                    UnclaimedGuestsMatched: unclaimedGuests.Count,
                    InactiveGuestsMatched: inactiveGuests.Count,
                    TotalAccountsDeleted: deletedUserIds.Count));
            }
            catch (Exception ex)
            {
                // Same narrow, documented carve-out InternalRoundEndpoints'
                // /internal/generate-round already relies on (docs/coding-
                // guidelines.md's error-handling rule): this endpoint's only
                // caller is purge-guest-accounts.yml's bearer-token-gated
                // scheduled job, not a player-facing surface, so its own
                // exception message in `detail` is what makes a failed run
                // diagnosable from the workflow's own log without direct
                // server log access (REQ-902's failure alerting is Tier 1,
                // not built yet).
                logger.LogError(ex, "Guest account purge failed unexpectedly.");

                return Results.Problem(
                    title: "Guest account purge failed unexpectedly",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    // Best-effort across the whole batch — one account's deletion failing
    // (e.g. AccountDeletionService's own documented gap, a Supabase Auth
    // identity delete failing) never aborts the rest of this run. Logged
    // server-side per docs/coding-guidelines.md; a failed delete leaves the
    // row's IsGuest/CreatedAt/LastActiveAt state untouched, so the next
    // scheduled run simply picks it up again.
    private static async Task<bool> TryDeleteAsync(
        Guid userId, IAccountDeletionService accountDeletionService, ILogger logger, CancellationToken cancellationToken)
    {
        var result = await accountDeletionService.DeleteAccountAsync(userId, cancellationToken);
        if (!result.Success)
        {
            logger.LogError("Guest account purge failed for user {UserId}: {ErrorMessage}", userId, result.ErrorMessage);
        }

        return result.Success;
    }
}

// UnclaimedGuestsMatched/InactiveGuestsMatched: how many rows each rule's own
// selection query matched this run (before dedup) — TotalAccountsDeleted is
// the actual, deduped count of accounts this run removed (a row matching
// both rules is only ever deleted once, so TotalAccountsDeleted can be less
// than the sum of the two Matched counts).
public record PurgeGuestAccountsResponse(int UnclaimedGuestsMatched, int InactiveGuestsMatched, int TotalAccountsDeleted);

// Pure log-category marker for ILogger<T> — same pattern as
// InternalRoundEndpoints.RoundGenerationLogCategory.
internal sealed class GuestCleanupLogCategory;
