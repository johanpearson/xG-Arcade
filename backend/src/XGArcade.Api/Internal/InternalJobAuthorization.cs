using System.Security.Cryptography;
using System.Text;

namespace XGArcade.Api.Internal;

// Shared by every bearer-token-gated /internal/* endpoint whose only caller
// is a scheduled job (generate-grid-round.yml/generate-path-round.yml,
// split from a single generate-round.yml as of S-136/ADR-0072,
// /InternalRoundEndpoints,
// purge-guest-accounts.yml/InternalGuestCleanupEndpoints, ...) — extracted
// from InternalRoundEndpoints (its original, only caller) so a second such
// endpoint doesn't hand-duplicate this constant-time comparison, the same
// "shared configuration helper, not hand-duplicated" discipline CLAUDE.md
// requires for HttpClient configuration (WikidataHttpClientConfiguration).
public static class InternalJobAuthorization
{
    public static bool IsAuthorized(HttpRequest request, IConfiguration configuration)
    {
        var expectedToken = configuration["Internal:JobToken"];
        if (string.IsNullOrEmpty(expectedToken))
            return false;

        if (!request.Headers.TryGetValue("Authorization", out var authHeader))
            return false;

        var expectedBytes = Encoding.UTF8.GetBytes($"Bearer {expectedToken}");
        var actualBytes = Encoding.UTF8.GetBytes(authHeader.ToString());

        // FixedTimeEquals rejects a length mismatch immediately on its own —
        // this token authorizes a real write action, so constant-time
        // comparison is used rather than a plain ==, not for any extra
        // protection the explicit length check below would add.
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
