using Microsoft.AspNetCore.Authorization;

namespace XGArcade.Api.Auth;

// Marker requirement for the "Admin" authorization policy — see
// AdminAuthorizationHandler for the actual check. Empty by design: there is
// nothing to configure per-endpoint, every admin endpoint uses the same rule.
public class AdminRequirement : IAuthorizationRequirement;

// architecture-document.md's "Admin authorization" pipeline step: admin =
// the authenticated user's Supabase user id (JWT "sub" claim) appears in the
// Admin__UserIds environment variable (comma-separated GUIDs). Config-based,
// not a database role — deliberately the simplest thing that works for a
// solo-operated Tier 0 (implementation-document.md §4); revisit only if
// there's ever more than a couple of admins. Re-parses the configured list on
// every check rather than caching it — Tier 0's admin list is a handful of
// entries at most, and this lets an env var change take effect on the next
// request with no cache-invalidation to reason about.
public class AdminAuthorizationHandler(IConfiguration configuration) : AuthorizationHandler<AdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        var authProviderUserId = context.User.GetAuthProviderUserId();
        if (authProviderUserId is not null && GetAdminUserIds().Contains(authProviderUserId.Value))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private HashSet<Guid> GetAdminUserIds() =>
        (configuration["Admin:UserIds"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .OfType<Guid>()
            .ToHashSet();
}
