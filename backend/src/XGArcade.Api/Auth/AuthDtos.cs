namespace XGArcade.Api.Auth;

// REQ-701's checkbox clause: AgeConfirmed must be true or signup is
// rejected before Supabase Auth is ever called — see ADR-0013.
public record SignupRequest(string Email, string Password, bool AgeConfirmed);

public record SignupResponse(Guid Id, string Email);

public record LoginRequest(string Email, string Password);

public record LoginResponse(string AccessToken, string? RefreshToken);

public record MeResponse(Guid Id, string Email, bool EmailConfirmed);
