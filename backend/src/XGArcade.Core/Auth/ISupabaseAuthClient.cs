namespace XGArcade.Core.Auth;

// COMP-01 (Core.Users)'s only path to the Auth provider (Supabase Auth) —
// see ADR-0013. Password credentials are never stored locally; they pass
// through to Supabase, which owns them.
public interface ISupabaseAuthClient
{
    Task<SupabaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default);

    Task<SupabaseAuthResult> SignInWithPasswordAsync(string email, string password, CancellationToken cancellationToken = default);
}

public record SupabaseAuthResult
{
    public required bool Success { get; init; }
    public Guid? AuthProviderUserId { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? ErrorMessage { get; init; }
}
