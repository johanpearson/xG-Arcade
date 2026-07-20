using XGArcade.Data.Entities;

namespace XGArcade.Core.Leagues;

// COMP-02 (Core.Leagues): REQ-402/403's custom-league create/join business
// logic, alongside ILeaderboardService's read path in the same component.
// Kept as its own interface/service (not folded into ILeaderboardService)
// since create/join mutates League/LeagueMembership — a genuinely
// different responsibility from that service's read-only aggregation.
public interface ILeagueService
{
    // REQ-402: creates a League(Type="custom") row with a freshly
    // generated, guaranteed-unique InviteCode, and adds the creator as its
    // first member in the same call — "the creator is automatically added
    // as a member" is never a separate step a caller could skip, the same
    // discipline AuthController.Signup already applies to the global
    // league (REQ-401).
    Task<League> CreateCustomLeagueAsync(string name, Guid creatorUserId, CancellationToken cancellationToken = default);

    // REQ-403: resolves an invite code to its League and adds the caller as
    // a member. Re-entering a code for a league the caller already belongs
    // to is treated as an idempotent success (JoinLeagueOutcome.AlreadyMember),
    // not a duplicate-membership error; an unrecognized code is
    // JoinLeagueOutcome.InvalidCode with no membership ever created — the
    // specific, "clear error" case REQ-403 requires.
    Task<JoinLeagueResult> JoinByInviteCodeAsync(string inviteCode, Guid userId, CancellationToken cancellationToken = default);

    // This story's "simple list" of a player's own custom leagues — every
    // Type="custom" league the given user currently belongs to, excluding
    // the Type="global" league REQ-401 already always enrolls them in
    // (that's read via ILeaderboardService, not this list).
    Task<IReadOnlyList<League>> GetMemberLeaguesAsync(Guid userId, CancellationToken cancellationToken = default);
}

public enum JoinLeagueOutcome
{
    Joined,
    AlreadyMember,
    InvalidCode,
}

// League is null only when Outcome is InvalidCode.
public record JoinLeagueResult(JoinLeagueOutcome Outcome, League? League);
