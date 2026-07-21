namespace XGArcade.Api.Auth;

// REQ-701's checkbox clause: AgeConfirmed must be true or signup is
// rejected before Supabase Auth is ever called — see ADR-0013. DisplayName
// (added S-011, REQ-401/404) is the only identity a leaderboard ever shows
// another player — collected here rather than derived from Email so a
// public leaderboard never has to expose an email address. ConfirmPassword
// (added S-016) must match Password or signup is rejected the same way,
// before Supabase Auth is ever called.
public record SignupRequest(string Email, string Password, string ConfirmPassword, string DisplayName, bool AgeConfirmed);

public record SignupResponse(Guid Id, string Email, string DisplayName);

public record LoginRequest(string Email, string Password);

public record LoginResponse(string AccessToken, string? RefreshToken);

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
// actually re-affirming intent.
public record DeleteAccountRequest(string Password);

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
