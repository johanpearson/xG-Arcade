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

    // REQ-717/ADR-0036: nullable because a guest (IsGuest = true) has no
    // email at all — every existing caller that assumed this was always
    // set (Signup, Login, DeleteAccount's re-confirmation, GetByEmailAsync)
    // has been audited and updated for that possibility; see those call
    // sites' own comments for how each one now handles a null Email.
    public string? Email { get; set; }

    private string _displayName = string.Empty;

    // REQ-401/REQ-404: leaderboards show this, never the raw Email, to every
    // other player — collected at signup (REQ-701) precisely so a public
    // leaderboard never has to expose an email address to do its job.
    public required string DisplayName
    {
        get => _displayName;
        set
        {
            _displayName = value;
            // REQ-701: display names are unique case-insensitively only —
            // spaces/punctuation/format are left exactly as entered, a
            // deliberate decision against reshaping this into a
            // username-style field. Kept in lockstep the same way
            // Player.NormalizedFullName tracks Player.FullName.
            NormalizedDisplayName = NormalizeCase(value);
        }
    }

    public string NormalizedDisplayName { get; private set; } = string.Empty;

    // The one place "case-insensitive" is defined for DisplayName —
    // UserRepository.DisplayNameExistsAsync calls this too, so the setter
    // above and that pre-check can never quietly disagree on what counts as
    // a match.
    public static string NormalizeCase(string displayName) => displayName.ToLowerInvariant();

    // Mirrors Supabase Auth's confirmed state; see REQ-702 (deferred — Tier
    // 0 has confirm-email off, so this is always true at creation for now).
    public bool EmailConfirmed { get; set; }

    // REQ-717/ADR-0036: true only for a row created via POST /auth/guest —
    // the *only* place this flag is ever consulted is REQ-409's qualifying-
    // rounds query (LeaderboardService/GuessRepository.
    // GetPerRoundFinalPointsByUserIdsAsync), which excludes guest rows from
    // the all-time median ranking entirely. Every other REQ (201-210, 204,
    // 406/407/408) reads Guess/LeagueMembership exactly as it already does —
    // a guest is indistinguishable from a real account to all of that code,
    // by design (ADR-0036's "For AI agents" section). Never branch on this
    // anywhere else.
    public bool IsGuest { get; set; }

    // REQ-717: set once, the moment a guest claims a real account (adds
    // email+password) — never before, never twice. Used by REQ-409's
    // qualifying-rounds query to exclude any round closed before this
    // moment, so a claimed account can't retroactively count guest-era
    // rounds toward the all-time median (see that query's own comment for
    // the full anti-loophole rationale).
    public DateTime? ClaimedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
