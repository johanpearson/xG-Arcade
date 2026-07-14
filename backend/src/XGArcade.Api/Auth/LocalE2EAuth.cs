using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using XGArcade.Core.Auth;

namespace XGArcade.Api.Auth;

// Test-only constants shared by LocalE2EAuthClient (mints tokens) and
// Program.cs's JwtBearer setup (validates them). Only ever wired up when
// ASPNETCORE_ENVIRONMENT is Development AND Auth:Mode=local-e2e — see
// Program.cs, which re-checks the environment itself rather than trusting
// the config flag alone (same gating principle as COMP-09's
// Testing.SeedManager, ADR-0006). Not a real secret: ci.yml's local E2E
// stack (no live Supabase project) is the only place this ever gets used.
public static class LocalE2EAuth
{
    public const string Issuer = "local-e2e";
    public const string Audience = "authenticated";

    public static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("local-e2e-test-signing-key-never-used-outside-development"));

    // Mints a token for a caller-chosen sub — used directly by
    // XGArcade.Api.Tests' WebApplicationFactory-based tests
    // (AuthEndpointTests, CurrentRoundEndpointTests, GuessEndpointTests),
    // which need a token for a specific pre-seeded (or deliberately
    // unseeded) authProviderUserId, not one derived from an email the way
    // LocalE2EAuthClient.Authenticate below needs. Never reachable outside
    // Development — same gating as the rest of this class.
    public static string MintToken(Guid authProviderUserId, string role = "authenticated")
    {
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = authProviderUserId.ToString(),
                ["role"] = role,
            },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }
}

// Stand-in for ISupabaseAuthClient so Playwright can sign real accounts up
// and log in against ci.yml's local Postgres-only E2E stack, which has no
// live Supabase project to call. No real password check — acceptable only
// because this is unreachable outside Development (see LocalE2EAuth above).
public class LocalE2EAuthClient : ISupabaseAuthClient
{
    public Task<SupabaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default) =>
        Task.FromResult(Authenticate(email));

    public Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default) =>
        Task.FromResult(Authenticate(email));

    // REQ-710: no real Supabase project exists in this mode (see class doc
    // comment above) — nothing to call, so this always "succeeds," letting
    // ci.yml's local E2E stack exercise the rest of account deletion
    // (anonymization, User/LeagueMembership removal) without a live
    // service_role key.
    public Task<bool> DeleteUserAsync(Guid authProviderUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    private static SupabaseAuthResult Authenticate(string email)
    {
        var authProviderUserId = DeterministicGuid(email);

        return new SupabaseAuthResult
        {
            Success = true,
            AuthProviderUserId = authProviderUserId,
            AccessToken = LocalE2EAuth.MintToken(authProviderUserId),
        };
    }

    // Deterministic so signing up and later logging in with the same email
    // resolve to the same local User row, the same way Supabase's real
    // identity id would stay stable across those two calls. MD5 chosen only
    // for its convenient 16-byte output to build a Guid from — not used for
    // any cryptographic property (collision resistance, etc.), and this
    // whole class is unreachable outside Development (see LocalE2EAuth above).
    private static Guid DeterministicGuid(string email) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));
}
