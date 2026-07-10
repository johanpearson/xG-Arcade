namespace XGArcade.Api.Auth;

// REQ-701's checkbox clause: AgeConfirmed must be true or signup is
// rejected before Supabase Auth is ever called — see ADR-0013. DisplayName
// (added S-011, REQ-401/404) is the only identity a leaderboard ever shows
// another player — collected here rather than derived from Email so a
// public leaderboard never has to expose an email address.
public record SignupRequest(string Email, string Password, string DisplayName, bool AgeConfirmed);

public record SignupResponse(Guid Id, string Email, string DisplayName);

public record LoginRequest(string Email, string Password);

public record LoginResponse(string AccessToken, string? RefreshToken);

public record MeResponse(Guid Id, string Email, string DisplayName, bool EmailConfirmed);
