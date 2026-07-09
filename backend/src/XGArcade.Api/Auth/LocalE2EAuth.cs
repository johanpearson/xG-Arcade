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

    private static SupabaseAuthResult Authenticate(string email)
    {
        var authProviderUserId = DeterministicGuid(email);

        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalE2EAuth.Issuer,
            Audience = LocalE2EAuth.Audience,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = authProviderUserId.ToString(),
                ["role"] = "authenticated",
            },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(LocalE2EAuth.SigningKey, SecurityAlgorithms.HmacSha256),
        });

        return new SupabaseAuthResult
        {
            Success = true,
            AuthProviderUserId = authProviderUserId,
            AccessToken = token,
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
