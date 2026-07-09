using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace XGArcade.Core.Auth;

// Calls Supabase Auth's REST API directly (ADR-0013) — HttpClient is
// registered as a typed client by XGArcade.Api with BaseAddress =
// Supabase:Url and the "apikey"/Authorization headers set from
// Supabase:AnonKey, so this class only knows the auth-specific paths.
public class SupabaseAuthClient(HttpClient httpClient) : ISupabaseAuthClient
{
    public Task<SupabaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default) =>
        PostCredentialsAsync("auth/v1/signup", email, password, cancellationToken);

    public Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default) =>
        PostCredentialsAsync("auth/v1/token?grant_type=password", email, password, cancellationToken);

    private async Task<SupabaseAuthResult> PostCredentialsAsync(
        string path, string email, string password, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, new { email, password }, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<SupabaseErrorResponse>(cancellationToken: cancellationToken);
            return new SupabaseAuthResult
            {
                Success = false,
                ErrorMessage = error?.Msg ?? error?.ErrorDescription ?? error?.Message ?? "Supabase Auth request failed.",
            };
        }

        var body = await response.Content.ReadFromJsonAsync<SupabaseSessionResponse>(cancellationToken: cancellationToken);

        // Auto-confirm signups/logins return the user nested under "user";
        // Supabase's non-session responses put id/email at the top level.
        var authProviderUserId = body?.User?.Id ?? body?.Id;
        if (authProviderUserId is null)
        {
            return new SupabaseAuthResult { Success = false, ErrorMessage = "Supabase Auth returned no user id." };
        }

        return new SupabaseAuthResult
        {
            Success = true,
            AuthProviderUserId = authProviderUserId,
            AccessToken = body?.AccessToken,
            RefreshToken = body?.RefreshToken,
        };
    }

    private record SupabaseSessionResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("user")]
        public SupabaseUser? User { get; init; }

        [JsonPropertyName("id")]
        public Guid? Id { get; init; }
    }

    private record SupabaseUser
    {
        [JsonPropertyName("id")]
        public Guid Id { get; init; }
    }

    private record SupabaseErrorResponse
    {
        [JsonPropertyName("msg")]
        public string? Msg { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
