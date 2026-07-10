namespace XGArcade.Data.Entities;

// COMP-01 (Core.Users)'s local profile record. Password credentials live in
// Supabase Auth, not here — this table only mirrors the minimal
// profile/state XGArcade.Core needs. See ADR-0004/0005/0013.
public class User
{
    public Guid Id { get; set; }

    // Supabase Auth's user id (the JWT's "sub" claim) — every authenticated
    // request resolves this row via that claim.
    public Guid AuthProviderUserId { get; set; }

    public required string Email { get; set; }

    // REQ-401/REQ-404: leaderboards show this, never the raw Email, to every
    // other player — collected at signup (REQ-701) precisely so a public
    // leaderboard never has to expose an email address to do its job.
    public required string DisplayName { get; set; }

    // Mirrors Supabase Auth's confirmed state; see REQ-702 (deferred — Tier
    // 0 has confirm-email off, so this is always true at creation for now).
    public bool EmailConfirmed { get; set; }

    public DateTime CreatedAt { get; set; }
}
