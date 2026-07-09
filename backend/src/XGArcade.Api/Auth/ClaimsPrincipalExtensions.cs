using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace XGArcade.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    // Supabase Auth JWTs carry the auth user id in the standard "sub" claim.
    public static Guid? GetAuthProviderUserId(this ClaimsPrincipal user)
    {
        var subject = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(subject, out var authProviderUserId) ? authProviderUserId : null;
    }
}
