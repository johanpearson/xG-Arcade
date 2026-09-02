using System.Security.Claims;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Api.Auth;

// Cross-cutting auth concern, not owned by any one feature area: resolves
// the authenticated caller's XGArcade.Data.Entities.User row from a
// ClaimsPrincipal's "sub" claim (see ClaimsPrincipalExtensions.
// GetAuthProviderUserId), or null if either the claim is missing/
// unparseable or no matching User exists yet. Callers translate a null
// into Results.Unauthorized() themselves (kept out of this helper so it
// stays a plain resolver, not a response-shaping one).
//
// Extracted (2026-09-02, S-209 quality-gate finding, ADR-0084 rule-of-
// three) from three near-identical private/internal copies that had
// accumulated in LeaderboardEndpoints.cs (originally `internal` so
// UserEndpoints.cs could reuse it directly), LeagueEndpoints.cs, and
// FriendEndpoints.cs, plus a fourth copy (ResolveCurrentUserAsync) in
// AvatarEndpoints.cs — all four had the exact same body. Every
// *Endpoints.cs file that needs to resolve the caller should call this
// instead of adding another local copy.
public static class RequestingUserResolver
{
    public static async Task<User?> ResolveAsync(
        ClaimsPrincipal principal, IUserRepository userRepository, CancellationToken cancellationToken)
    {
        var authProviderUserId = principal.GetAuthProviderUserId();
        if (authProviderUserId is null)
            return null;

        return await userRepository.GetByAuthProviderUserIdAsync(authProviderUserId.Value, cancellationToken);
    }
}
