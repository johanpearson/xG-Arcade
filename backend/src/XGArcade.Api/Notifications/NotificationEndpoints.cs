using System.Security.Claims;
using XGArcade.Api.Auth;
using XGArcade.Core.Social;
using XGArcade.Data.Repositories;
using XGArcade.Games.XGConnect;

namespace XGArcade.Api.Notifications;

// REQ-1411/S-216: the visible notification indicator's own aggregate read.
// Per ADR-0103's "REQ-1411's notification indicator belongs to neither
// [Core.Social nor Games.XGConnect]" paragraph, this is deliberately NOT a
// new component (no Core.Notifications/COMP-08 exists as code yet — that's
// Tier 1, referenced only in docs for round-result emails) — it's a small
// aggregating endpoint in XGArcade.Api that queries Core.Social
// (IFriendService/IChallengeService) and Games.XGConnect
// (IConnectMatchLifecycleService) through their own normal read paths, same
// thin-endpoint orchestration shape ChallengeEndpoints.cs's accept handler
// already establishes for a different cross-component write.
//
// Deliberately excludes an unpaired MatchmakingOptIn (REQ-1403, `Waiting`
// status) — nothing actionable for the player yet, so
// IMatchmakingService/IMatchmakingOptInRepository are never injected here.
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this WebApplication app)
    {
        // REQ-1411: combined pending-item presence/count across pending
        // friend requests sent to the caller (REQ-1401), pending challenges
        // sent to the caller (REQ-1402), and the caller's own open xG
        // Connect matches that are still awaiting THEIR next move
        // (REQ-1404/1405/1407/1408). Returns per-category counts (not just
        // a single combined total) so a client can, if it ever wants to,
        // distinguish "which category" — REQ-1411 itself only requires
        // combined presence, not a breakdown, but withholding the breakdown
        // here would just push a second endpoint onto S-217 for no reason.
        app.MapGet("/notifications/summary", async (
            ClaimsPrincipal principal,
            IUserRepository userRepository,
            IFriendService friendService,
            IChallengeService challengeService,
            IConnectMatchLifecycleService connectMatchLifecycleService,
            CancellationToken cancellationToken) =>
        {
            var requestingUser = await RequestingUserResolver.ResolveAsync(principal, userRepository, cancellationToken);
            if (requestingUser is null)
                return Results.Unauthorized();

            var pendingFriendRequests = await friendService.GetPendingFriendRequestsAsync(requestingUser.Id, cancellationToken);
            var pendingChallenges = await challengeService.GetPendingChallengesAsync(requestingUser.Id, cancellationToken);
            var matchesAwaitingAction = await connectMatchLifecycleService.GetMatchesAwaitingActionAsync(requestingUser.Id, cancellationToken);

            var response = new NotificationSummaryResponse(
                pendingFriendRequests.Count,
                pendingChallenges.Count,
                matchesAwaitingAction.Count,
                pendingFriendRequests.Count > 0 || pendingChallenges.Count > 0 || matchesAwaitingAction.Count > 0);

            return Results.Ok(response);
        }).RequireAuthorization();
    }
}

// HasPending is the single combined presence flag REQ-1411 requires ("a
// single, persistent notification indicator ... showing that at least one
// such item exists") — the three counts alongside it are not mandated by
// REQ-1411 (which explicitly leaves count-vs-presence-dot to
// design-document.md) but are exposed anyway so S-217's frontend badge can
// choose either treatment without a second round-trip.
public record NotificationSummaryResponse(
    int PendingFriendRequestCount,
    int PendingChallengeCount,
    int MatchesAwaitingActionCount,
    bool HasPending);
