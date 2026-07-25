using XGArcade.Core.Auth;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Admin;

// REQ-507 (admin guest/user metrics view) and REQ-508 (admin bulk
// force-clear of guest accounts). Unlike AdminManagementEndpoints.cs
// (REQ-505/506), these are registered unconditionally — including in
// Production — because both act on real account data as their stated
// purpose, not on seeded/test data; see each REQ's own "Scope note"/
// "Given ASPNETCORE_ENVIRONMENT == Production" acceptance criterion.
// Kept in their own file, alongside AdminEndpoints.cs's REQ-501/502/503 and
// separate from AdminManagementEndpoints.cs's REQ-505/506, so the "always
// registered" vs. "non-Production only" split stays visible at a glance
// rather than becoming a per-endpoint condition.
public static class AdminAccountsEndpoints
{
    public static void MapAdminAccountsEndpoints(this WebApplication app)
    {
        // REQ-507: live counts as of the moment of the request — no
        // caching layer, so this always reflects the current DB state
        // (also why CurrentGuestCount/ClaimedGuestCount are computed with
        // one CountAsync call each rather than materializing the User
        // table into memory).
        app.MapGet("/admin/accounts/metrics", async (
            IUserRepository userRepository,
            CancellationToken cancellationToken) =>
        {
            // Sequential, not Task.WhenAll: these calls share the request-
            // scoped XGArcadeDbContext via IUserRepository, and concurrent
            // use of one DbContext is unsafe in EF Core (works against the
            // InMemory test provider, throws against real Npgsql).
            var totalUserCount = await userRepository.CountUsersAsync(cancellationToken);
            var currentGuestCount = await userRepository.CountGuestsAsync(cancellationToken);
            var claimedGuestCount = await userRepository.CountClaimedGuestsAsync(cancellationToken);

            return Results.Ok(new AdminAccountMetricsResponse(totalUserCount, currentGuestCount, claimedGuestCount));
        }).RequireAuthorization("Admin");

        // REQ-508 step 1: the dry-run count shown before anything is
        // deleted, so the client's confirmation step can display a known,
        // specific number rather than an open-ended action. Reuses the
        // exact same unconditional guest count as REQ-507's metrics view
        // above (CountGuestsAsync) — deliberately not one of REQ-718's
        // age-filtered queries (IUserRepository's own comment on
        // CountGuestsAsync explains why).
        app.MapGet("/admin/accounts/guests/count", async (
            IUserRepository userRepository,
            CancellationToken cancellationToken) =>
        {
            var count = await userRepository.CountGuestsAsync(cancellationToken);
            return Results.Ok(new GuestAccountCountResponse(count));
        }).RequireAuthorization("Admin");

        // REQ-508 step 2: the execute action, called only after the client
        // has already shown the count above and the admin has confirmed —
        // this endpoint itself does not re-display or re-confirm the count
        // (that two-step UI lives client-side, same as REQ-506's existing
        // confirm/cancel). Selects every currently-matching guest id fresh
        // at execution time (not the id list from a prior /count call), so
        // a guest created or claimed in the gap between the two calls is
        // handled correctly by construction — matching the REQ's explicit
        // "not required to re-verify the count is unchanged" allowance.
        //
        // Deletes each one via IAccountDeletionService.DeleteAccountAsync —
        // per ADR-0038, no second/raw bulk-delete path — and reports a
        // per-account outcome rather than a single all-or-nothing result,
        // the same reporting discipline AdminEndpoints.cs's POST
        // /admin/player-data/approve already establishes (REQ-503).
        app.MapPost("/admin/accounts/guests/clear", async (
            IUserRepository userRepository,
            IAccountDeletionService accountDeletionService,
            ILogger<AdminAccountsLogCategory> logger,
            CancellationToken cancellationToken) =>
        {
            var guestIds = await userRepository.GetAllGuestIdsAsync(cancellationToken);

            var results = new List<GuestAccountClearResult>(guestIds.Count);
            foreach (var userId in guestIds)
            {
                var result = await accountDeletionService.DeleteAccountAsync(userId, cancellationToken);
                if (result.Success)
                {
                    results.Add(new GuestAccountClearResult(userId, GuestAccountClearOutcome.Succeeded.ToString(), null));
                    continue;
                }

                // AccountDeletionService.UserNotFoundErrorMessage is the one
                // structured signal DeleteAccountAsync gives for "this id no
                // longer matches a User row" (the race window this REQ's own
                // acceptance criteria acknowledges) — anything else (e.g. the
                // Supabase-delete failure it also returns) is reported as
                // "Failed" rather than misclassified as "NotFound".
                var outcome = result.ErrorMessage == AccountDeletionService.UserNotFoundErrorMessage
                    ? GuestAccountClearOutcome.NotFound
                    : GuestAccountClearOutcome.Failed;

                if (outcome == GuestAccountClearOutcome.Failed)
                {
                    logger.LogError(
                        "Admin-triggered guest account clear failed for user {UserId}: {ErrorMessage}",
                        userId, result.ErrorMessage);
                }

                results.Add(new GuestAccountClearResult(userId, outcome.ToString(), result.ErrorMessage));
            }

            return Results.Ok(new ClearGuestAccountsResponse(results));
        }).RequireAuthorization("Admin");
    }
}

// REQ-507: TotalUserCount is every User row; CurrentGuestCount/
// ClaimedGuestCount can never disagree by construction (claiming clears
// IsGuest and stamps ClaimedAt atomically, REQ-717/ADR-0036) — both are
// surfaced anyway so an admin doesn't need to know that invariant to read
// this view correctly (REQ-507's own acceptance criteria).
public record AdminAccountMetricsResponse(int TotalUserCount, int CurrentGuestCount, int ClaimedGuestCount);

public record GuestAccountCountResponse(int Count);

// Outcome is one of GuestAccountClearOutcome's string values ("Succeeded"/
// "NotFound"/"Failed") — kept as a plain string at the API boundary rather
// than serializing the enum type directly, same pattern as
// PlayerDataApprovalResult/PlayerDataRemovalResult (AdminEndpoints.cs).
// ErrorMessage is null when Outcome is "Succeeded".
public record GuestAccountClearResult(Guid UserId, string Outcome, string? ErrorMessage);

public record ClearGuestAccountsResponse(IReadOnlyList<GuestAccountClearResult> Results);

// REQ-508's per-account outcome — see MapPost("/admin/accounts/guests/clear")
// above for how each value is decided.
public enum GuestAccountClearOutcome
{
    Succeeded,
    NotFound,
    Failed,
}

// Pure log-category marker for ILogger<T> — same pattern as
// AdminEndpointsLogCategory/AdminManagementLogCategory.
internal sealed class AdminAccountsLogCategory;
