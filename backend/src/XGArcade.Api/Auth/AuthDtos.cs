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

public record MeResponse(Guid Id, string Email, string DisplayName, bool EmailConfirmed);

// REQ-710: the confirmation step an irreversible self-service deletion
// requires — the caller's current password, re-verified against Supabase
// Auth before anything is deleted (AuthController.DeleteAccount), rather
// than a bare confirmation flag a client could set without the user
// actually re-affirming intent.
public record DeleteAccountRequest(string Password);
