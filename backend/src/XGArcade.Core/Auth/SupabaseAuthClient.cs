using System.Net;
using System.Net.Http.Headers;
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
        PostAuthRequestAsync("auth/v1/signup", new { email, password }, cancellationToken);

    public Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default) =>
        PostAuthRequestAsync("auth/v1/token?grant_type=password", new { email, password }, cancellationToken);

    // REQ-715: same anon-keyed HttpClient defaults as SignUp/SignInWithPassword
    // above (no service_role key needed — unlike DeleteUserAsync below, this
    // isn't an Admin API call) and the exact same
    // success/failure-shape-not-exception contract.
    public Task<SupabaseAuthResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        PostAuthRequestAsync("auth/v1/token?grant_type=refresh_token", new { refresh_token = refreshToken }, cancellationToken);

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

    // REQ-717/ADR-0036: Supabase's Anonymous Sign-ins feature — the same
    // POST auth/v1/signup endpoint SignUpAsync above calls, but with no
    // email/password in the body at all, which is what Supabase's API
    // documents as triggering anonymous-identity creation rather than a
    // validation error. NOT independently verified against a live Supabase
    // project from this environment (no network access here, and this
    // project has been burned before by unverified assumptions about
    // external APIs — see docs/decisions/0008-*.md's precedent) — flagged
    // for manual verification against a real Supabase project before this
    // ships. Reuses PostAuthRequestAsync since the response shape (a normal
    // session: access_token/refresh_token/user) is documented to be the
    // same as a real signup's.
    //
    // captchaToken (REQ-717's 2026-07-21 "Bot-check (captcha)" addition /
    // ADR-0037): forwarded unmodified as gotrue_meta_security.captcha_token,
    // the field ADR-0037 records as Supabase Auth's documented mechanism for
    // native Cloudflare Turnstile verification. Also NOT independently
    // verified against a live Supabase project from this environment — same
    // caveat as this method's own pre-existing comment above; ADR-0037
    // itself flags this field name/shape as recorded from Supabase's
    // documentation, not confirmed against a live project either.
    public Task<SupabaseAuthResult> SignInAnonymouslyAsync(string captchaToken, CancellationToken cancellationToken = default) =>
        PostAuthRequestAsync(
            "auth/v1/signup",
            new { gotrue_meta_security = new { captcha_token = captchaToken } },
            cancellationToken);

    // REQ-717/ADR-0036: the claim/upgrade path. PUT auth/v1/user is
    // Supabase's user-update endpoint — passing email/password there is
    // documented as adding real credentials to whichever identity the
    // request authenticates as, converting it in place rather than
    // creating a second identity. NOT independently verified against a
    // live Supabase project from this environment — same caveat as
    // SignInAnonymouslyAsync above; flagged for manual verification.
    //
    // Unlike every anon-keyed call above, this one must authenticate as the
    // guest identity being converted — Supabase's user-update endpoint
    // identifies *which* user to modify from the bearer token itself, not
    // from a body field. Setting Authorization directly on this request
    // overrides the anon-keyed default the HttpClient sets (a request's own
    // header value always wins — the same mechanism DeleteUserAsync's
    // service-role override above relies on), while "apikey" stays the
    // HttpClient's anon-key default, which Supabase's REST API expects
    // alongside the caller's own bearer token on this endpoint (unlike
    // DeleteUserAsync's Admin API call, this is not a service_role-only
    // operation).
    public async Task<SupabaseAuthResult> LinkEmailPasswordAsync(
        string accessToken, string email, string password, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "auth/v1/user")
        {
            Content = JsonContent.Create(new { email, password }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return await ReadFailureResultAsync(response, cancellationToken);
        }

        // Supabase's user-update endpoint returns the updated user object
        // at the top level (id/email/...), the same shape
        // PostAuthRequestAsync's non-session fallback (body?.Id) already
        // handles for other endpoints.
        var body = await response.Content.ReadFromJsonAsync<SupabaseUser>(cancellationToken: cancellationToken);
        if (body is null)
        {
            return new SupabaseAuthResult { Success = false, ErrorMessage = "Supabase Auth returned no user id." };
        }

        return new SupabaseAuthResult { Success = true, AuthProviderUserId = body.Id };
    }

    // Shared by SignUpAsync/SignInWithPasswordAsync/RefreshTokenAsync above —
    // all three are anon-keyed POSTs to Supabase Auth's token/signup
    // endpoints that return the same session-shaped response on success and
    // the same error shape on failure; only the path and request body
    // differ per call.
    private async Task<SupabaseAuthResult> PostAuthRequestAsync(
        string path, object requestBody, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, requestBody, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return await ReadFailureResultAsync(response, cancellationToken);
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

    // Shared by PostAuthRequestAsync and LinkEmailPasswordAsync above — both
    // parse the same Supabase error-response shape on a non-success status
    // code, previously duplicated verbatim between the two; extracted so
    // the precedence order (msg/error_description/message/generic fallback)
    // only ever needs updating in one place. Also computes IsCaptchaRejection
    // (REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037) so
    // AuthController.Guest can distinguish a captcha-specific rejection from
    // every other failure without AuthController itself needing to know
    // anything about Supabase's error-body shape.
    private static async Task<SupabaseAuthResult> ReadFailureResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var error = await response.Content.ReadFromJsonAsync<SupabaseErrorResponse>(cancellationToken: cancellationToken);
        var message = error?.Msg ?? error?.ErrorDescription ?? error?.Message ?? "Supabase Auth request failed.";

        // NOT independently verified against a live Supabase project from
        // this environment (no network access here — same caveat
        // SignInAnonymouslyAsync's own comment already carries, per
        // ADR-0037). Supabase Auth (GoTrue)'s documented machine-readable
        // error codes include "captcha_failed" for a rejected/missing/
        // expired captcha token, carried in an "error_code" field
        // alongside the existing "msg"/"error_description"/"message"
        // fields this class already parses — checked first as the more
        // reliable signal. Falls back to a case-insensitive substring match
        // on the human-readable message (Supabase's own past wording for
        // this failure, e.g. "captcha verification process failed" /
        // "captcha protection: ...", has consistently contained the word
        // "captcha") in case error_code isn't present on whichever Supabase
        // Auth version is actually deployed — flagged for manual
        // verification against a real Supabase project the same way
        // ADR-0037 already flags the request-side field name/shape.
        var isCaptchaRejection =
            string.Equals(error?.ErrorCode, "captcha_failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("captcha", StringComparison.OrdinalIgnoreCase);

        return new SupabaseAuthResult
        {
            Success = false,
            ErrorMessage = message,
            IsCaptchaRejection = isCaptchaRejection,
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

        // REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037:
        // Supabase Auth (GoTrue)'s documented machine-readable error code
        // (e.g. "captcha_failed") — NOT independently verified against a
        // live Supabase project from this environment, see
        // ReadFailureResultAsync's own comment above for the full caveat.
        [JsonPropertyName("error_code")]
        public string? ErrorCode { get; init; }
    }
}
