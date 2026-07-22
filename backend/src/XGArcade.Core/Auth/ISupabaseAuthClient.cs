namespace XGArcade.Core.Auth;

// COMP-01 (Core.Users)'s only path to the Auth provider (Supabase Auth) —
// see ADR-0013. Password credentials are never stored locally; they pass
// through to Supabase, which owns them.
public interface ISupabaseAuthClient
{
    Task<SupabaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default);

    Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default);

    // REQ-715: exchanges a stored refresh token for a new access token
    // (and, if Supabase's own rotation returns one, a new refresh token) —
    // mediated through the backend the same way SignUp/SignInWithPassword
    // already are (ADR-0013), never a direct frontend-to-Supabase call.
    // Same "never throws, Success=false + ErrorMessage on rejection" shape
    // as SignInWithPasswordAsync — this is an interactive path (a person
    // waiting on a page load), not a batch job, so a failed/expired/revoked
    // refresh token is reported back for the caller to react to, not thrown
    // (docs/coding-guidelines.md's external-client error-handling split).
    Task<SupabaseAuthResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

    // REQ-710: permanently removes the identity/credential from Supabase
    // Auth, so the caller can no longer log in and the email becomes
    // available for a new signup. Unlike SignUp/SignIn above, this calls
    // Supabase's Admin API and requires the service_role key, never the
    // anon key (see SupabaseAuthClient's own doc comment).
    Task<bool> DeleteUserAsync(Guid authProviderUserId, CancellationToken cancellationToken = default);

    // REQ-717/ADR-0036: provisions a brand-new anonymous identity via
    // Supabase Auth's Anonymous Sign-ins feature (no email/password at
    // all) — mediated through the backend the same way SignUpAsync already
    // is (ADR-0013), never called directly from the frontend. Same
    // anon-keyed HttpClient defaults as SignUpAsync/SignInWithPasswordAsync
    // above, and the same "never throws, Success=false + ErrorMessage on
    // rejection" contract.
    //
    // captchaToken (REQ-717's 2026-07-21 "Bot-check (captcha)" addition /
    // ADR-0037): passed straight through, unmodified, as
    // gotrue_meta_security.captcha_token on the same signup call — this
    // backend performs no independent Cloudflare Turnstile verification of
    // its own, Supabase's own server-side verification is the only check
    // (ADR-0037's "mediate, don't reimplement" decision, same boundary as
    // password credentials). A missing/expired/invalid token is expected to
    // surface as a distinguishable rejection in the returned
    // SupabaseAuthResult (see IsCaptchaRejection below), not a special
    // exception path.
    Task<SupabaseAuthResult> SignInAnonymouslyAsync(string captchaToken, CancellationToken cancellationToken = default);

    // REQ-717/ADR-0036: the claim/upgrade path — adds email+password to an
    // *existing* anonymous identity, converting it in place (Supabase's
    // user-update operation; never creates a second, disconnected
    // identity). accessToken must be the guest's own current access token:
    // this call authenticates as the identity being converted, unlike every
    // other method here, which authenticates with the shared anon key (see
    // SupabaseAuthClient.LinkEmailPasswordAsync's own doc comment for why).
    Task<SupabaseAuthResult> LinkEmailPasswordAsync(string accessToken, string email, string password, CancellationToken cancellationToken = default);
}

public record SupabaseAuthResult
{
    public required bool Success { get; init; }
    public Guid? AuthProviderUserId { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? ErrorMessage { get; init; }

    // REQ-717's 2026-07-21 "Bot-check (captcha)" addition / ADR-0037: set by
    // SupabaseAuthClient when a failed request's error body indicates the
    // rejection was specifically a captcha-verification failure (missing,
    // expired, or otherwise invalid Turnstile token), as opposed to any
    // other rejection reason. AuthController.Guest uses this to return a
    // distinct rejection response the frontend can act on (reset the
    // Turnstile widget and retry) instead of the generic "Guest sign-in
    // failed" response — REQ-717's own explicit acceptance criterion.
    // Always false for a Success result and for every other call on this
    // interface (SignUp/SignInWithPassword/etc. never set it).
    public bool IsCaptchaRejection { get; init; }
}
