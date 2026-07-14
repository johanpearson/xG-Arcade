using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace XGArcade.Core.Auth;

// REQ-710/ADR-0026: the Supabase Admin API key — kept as its own tiny type
// (not a bare string, which DI's typed-client activation can't resolve
// unambiguously) purely so SupabaseAuthClient can go on being registered as
// one ordinary AddHttpClient<ISupabaseAuthClient, SupabaseAuthClient> typed
// client, with this one extra value supplied from the DI container the same
// way any other constructor dependency is, rather than a second,
// hand-rolled HttpClient/factory registration just for one call.
public record SupabaseServiceRoleKey(string Value);

// Calls Supabase Auth's REST API directly (ADR-0013) — HttpClient is
// registered as a typed client by XGArcade.Api with BaseAddress =
// Supabase:Url and the "apikey"/Authorization headers set from
// Supabase:AnonKey, so this class only knows the auth-specific paths.
//
// DeleteUserAsync (REQ-710, ADR-0026) is the one method here that can't use
// those anon-keyed defaults: Supabase's Admin API only accepts the
// service_role key, a genuinely more privileged credential (bypasses Row
// Level Security entirely). Rather than a second HttpClient, it sets
// "apikey"/"Authorization" directly on that one call's HttpRequestMessage —
// a request's own header value always wins over an HttpClient's
// DefaultRequestHeaders for the same header name (HttpClient only fills in
// headers the request doesn't already have), so the anon key set on
// httpClient's defaults is never sent on this specific call.
public class SupabaseAuthClient(HttpClient httpClient, SupabaseServiceRoleKey serviceRoleKey) : ISupabaseAuthClient
{
    public Task<SupabaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default) =>
        PostCredentialsAsync("auth/v1/signup", email, password, cancellationToken);

    public Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default) =>
        PostCredentialsAsync("auth/v1/token?grant_type=password", email, password, cancellationToken);

    public async Task<bool> DeleteUserAsync(Guid authProviderUserId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"auth/v1/admin/users/{authProviderUserId}");
        request.Headers.Add("apikey", serviceRoleKey.Value);
        request.Headers.Add("Authorization", $"Bearer {serviceRoleKey.Value}");

        using var response = await httpClient.SendAsync(request, cancellationToken);

        // A 404 here means the identity is already gone (e.g. a retried
        // delete after a prior partial failure) — treated as success since
        // the end state the caller wants (no such identity) already holds.
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
    }

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
