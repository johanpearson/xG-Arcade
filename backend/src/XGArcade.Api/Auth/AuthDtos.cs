namespace XGArcade.Api.Auth;

// REQ-701's checkbox clause: AgeConfirmed must be true or signup is
// rejected before Supabase Auth is ever called — see ADR-0013. DisplayName
// (added S-011, REQ-401/404) is the only identity a leaderboard ever shows
// another player — collected here rather than derived from Email so a
// public leaderboard never has to expose an email address. ConfirmPassword
// (added S-016) must match Password or signup is rejected the same way,
// before Supabase Auth is ever called. CaptchaToken (REQ-701/REQ-717's
// 2026-07-25 "scope correction" addition / ADR-0037's amendment) is the
// same Cloudflare Turnstile token GuestRequest.CaptchaToken below already
// documents, now required here too — same reasoning applies: nullable
// (rather than a plain, non-nullable `string`) so a request body that omits
// the field reaches AuthController.Signup's own explicit check instead of
// short-circuiting into ASP.NET Core's generic model-validation 400, which
// would defeat REQ-701's requirement that a missing token gets the same
// distinct "Captcha verification failed" response as an invalid one.
public record SignupRequest(string Email, string Password, string ConfirmPassword, string DisplayName, bool AgeConfirmed, string? CaptchaToken);

public record SignupResponse(Guid Id, string Email, string DisplayName);

// CaptchaToken (REQ-701's 2026-07-25 "scope correction" addition /
// ADR-0037's amendment): same Cloudflare Turnstile token/nullability
// reasoning as SignupRequest.CaptchaToken above — required here too, and
// nullable for the same "must reach AuthController.Login's own check, not
// ASP.NET Core's generic model-validation 400" reason.
public record LoginRequest(string Email, string Password, string? CaptchaToken);

public record LoginResponse(string AccessToken, string? RefreshToken);

// REQ-717's 2026-07-21 "Bot-check (captcha) for guest creation" addition /
// ADR-0037: the frontend obtains a Cloudflare Turnstile token client-side
// (via Turnstile's widget/JS) before ever calling this endpoint, and sends
// it here. AuthController.Guest passes CaptchaToken through unmodified to
// Supabase Auth's anonymous sign-in call (gotrue_meta_security.captcha_token)
// for Supabase's own server-side verification against Cloudflare — this
// backend never verifies it independently (ADR-0037's "mediate, don't
// reimplement" decision). Replaces REQ-717's original "no request body"
// design for this endpoint, now that there's something for the caller to
// supply.
// CaptchaToken is nullable (rather than a plain, non-nullable `string`)
// deliberately: with a non-nullable reference-type property, ASP.NET
// Core's automatic model validation (Nullable enabled + [ApiController])
// treats a request body that omits the field as an invalid model and
// short-circuits with its own generic "One or more validation errors
// occurred." response *before* AuthController.Guest's own code ever runs
// — which would silently defeat REQ-717's own acceptance criterion that a
// missing token gets the same distinct "Captcha verification failed"
// response as an invalid one. Nullable here means a missing/empty token
// reaches the controller, which checks for it explicitly and returns that
// same distinct response itself, never Supabase's.
public record GuestRequest(string? CaptchaToken);

// IsAdmin (S-026, REQ-504) is what lets the frontend decide whether to
// render the admin nav entry point at all — computed the same way
// AdminAuthorizationHandler decides it server-side (Admin:UserIds), never a
// second, independently-maintained check. Email is nullable since REQ-717:
// a guest (IsGuest = true) has none until it claims a real account. IsGuest
// mirrors User.IsGuest directly (REQ-717/ADR-0036) so the frontend has a
// first-class field to check instead of inferring guest status from
// Email being null.
public record MeResponse(Guid Id, string? Email, string DisplayName, bool EmailConfirmed, bool IsAdmin, bool IsGuest);

// REQ-710: the confirmation step an irreversible self-service deletion
// requires — the caller's current password, re-verified against Supabase
// Auth before anything is deleted (AuthController.DeleteAccount), rather
// than a bare confirmation flag a client could set without the user
// actually re-affirming intent. CaptchaToken (REQ-710's 2026-07-25 addition
// / ADR-0037's second amendment) is the same Cloudflare Turnstile token
// SignupRequest/LoginRequest/GuestRequest's own CaptchaToken fields already
// document — nullable for the same reason: it must reach
// AuthController.DeleteAccount's own explicit check instead of
// short-circuiting into ASP.NET Core's generic model-validation 400, which
// would defeat the requirement that a missing token gets the same distinct
// "Captcha verification failed" response as an invalid one.
public record DeleteAccountRequest(string Password, string? CaptchaToken);

// REQ-714: edit an existing account's DisplayName from Settings. Reuses
// REQ-701's exact 1-30 character bound and case-insensitive uniqueness
// check (AuthController.UpdateDisplayName), this time excluding the
// caller's own row so a no-op/casing-only resubmission of their own name
// is never treated as a conflict against itself.
public record UpdateDisplayNameRequest(string DisplayName);

public record UpdateDisplayNameResponse(Guid Id, string DisplayName);

// REQ-715: exchanges a stored refresh token for a new access token (and,
// if Supabase's own rotation returns one, a new refresh token) without the
// person re-entering credentials — mediated through the backend the same
// way POST /auth/login/signup already are (ADR-0013).
public record RefreshRequest(string RefreshToken);

// REQ-717/ADR-0036: the claim/upgrade path — a guest (AuthController.Claim)
// adds a real email+password to their existing identity. Same
// Password/ConfirmPassword shape and REQ-701 password-policy reuse as
// SignupRequest above, deliberately not a second, looser policy just
// because the caller already has a session.
public record ClaimAccountRequest(string Email, string Password, string ConfirmPassword);

// REQ-711 (S-249): the caller's own GDPR data export — never another user's
// data (AuthController.Export resolves the row exclusively from the
// caller's own JWT, the same pattern every other authenticated endpoint
// here uses; there is deliberately no id parameter anywhere on this DTO or
// its endpoint for a caller to tamper with).
public record DataExportResponse(
    DataExportAccountInfo Account,
    IReadOnlyList<DataExportGuess> Guesses,
    IReadOnlyList<DataExportLeagueMembership> LeagueMemberships,
    // REQ-711/MVP-SCOPE.md: NotificationPreference doesn't exist yet (Tier
    // 1) — same no-op-until-built precedent REQ-710/AccountDeletionService
    // already established for the same table (see that class's own doc
    // comment). Always null here; once that table exists, this becomes a
    // real, populated value rather than a new field being added.
    object? NotificationPreferences);

// AuthProviderUserId is included even though it's not player-facing
// anywhere else in this API — it's a genuine piece of data this system
// holds about the account (the link to the Supabase Auth identity), and
// REQ-711 asks for "the User row, excluding the credential itself, which
// the auth provider holds" — this identifier is not itself a credential
// (no password/secret ever lives in this table; see User.cs), so excluding
// it would under-deliver on "a copy of what the platform holds."
public record DataExportAccountInfo(
    Guid Id,
    Guid AuthProviderUserId,
    string? Email,
    string DisplayName,
    bool EmailConfirmed,
    bool IsGuest,
    DateTime? ClaimedAt,
    DateTime CreatedAt,
    DateTime LastActiveAt);

// Mirrors XGArcade.Data.Entities.Guess directly (minus nothing — unlike
// SubmitGuessResponse elsewhere in this API, there's no "only reveal on a
// locked/correct guess" narrowing here: this is the player's own full
// record of their own guess, not something shown to any other player, so
// REQ-201/216's normal reveal-timing rules don't apply to an export of your
// own data).
public record DataExportGuess(
    Guid Id,
    Guid RoundId,
    Guid CellId,
    string SubmittedName,
    Guid? PlayerAnswerId,
    bool IsCorrect,
    int AttemptCount,
    double? FinalUniquenessScore,
    int? FinalPoints,
    DateTime CreatedAt,
    string? MatchedPlayerName,
    string? MatchedPlayerPhotoUrl);

// LeagueName (never just LeagueId) is REQ-711's own explicit acceptance
// criterion — resolved by ILeagueRepository.GetMembershipsWithLeagueNameByUserIdAsync's
// join so this DTO can be built directly from repository output.
public record DataExportLeagueMembership(Guid LeagueId, string LeagueName, string LeagueType);
