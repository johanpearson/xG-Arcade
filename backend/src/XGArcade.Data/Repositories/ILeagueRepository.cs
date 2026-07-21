using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-02 (Core.Leagues)'s own persistence — the only path Core.Leagues
// reaches League/LeagueMembership through, same repository-per-component
// pattern as IRoundRepository/IGuessRepository.
public interface ILeagueRepository
{
    // REQ-401: idempotent singleton lookup — the first call ever made
    // creates the one Type="global" League row; every call after returns
    // that same row. Guarded by a filtered unique index (XGArcadeDbContext)
    // against the (very unlikely, single-instance-Tier-0) race of two
    // concurrent first signups both trying to create it.
    Task<League> GetOrCreateGlobalLeagueAsync(CancellationToken cancellationToken = default);

    // REQ-401: called once per signup, right after GetOrCreateGlobalLeagueAsync
    // — "requires no action from the user" is enforced by AuthController
    // doing this automatically, not by anything here.
    Task AddMembershipAsync(Guid leagueId, Guid userId, CancellationToken cancellationToken = default);

    // REQ-404's leaderboard population: every user id currently a member of
    // this league.
    Task<IReadOnlyList<Guid>> GetMemberUserIdsAsync(Guid leagueId, CancellationToken cancellationToken = default);

    // REQ-710: removes every league membership for a user being deleted —
    // called by AccountDeletionService (Core.Auth/COMP-01) before the User
    // row itself is removed, the same cross-component repository call
    // AuthController.Signup already makes into ILeagueRepository directly.
    // implementation-document.md §6.8 documents this as an explicit delete
    // step, not something left to a DB-level cascade.
    Task RemoveMembershipsByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);

    // REQ-402: creates a new custom League row (Type="custom", a caller-
    // generated InviteCode, CreatedByUserId set) — throws
    // InviteCodeAlreadyInUseException if the DB's unique index on
    // InviteCode (XGArcadeDbContext) rejects the insert, the race window
    // behind LeagueService.CreateCustomLeagueAsync's own
    // InviteCodeExistsAsync pre-check, same split as UserRepository.AddAsync's
    // DisplayName handling. Never adds the creator's own membership —
    // the caller does that as its own explicit AddMembershipAsync call, same
    // as AuthController already does for the global league.
    Task<League> AddCustomLeagueAsync(League league, CancellationToken cancellationToken = default);

    // REQ-402: LeagueService's cheap pre-check before attempting a candidate
    // invite code's insert — avoids a wasted round trip in the overwhelming
    // majority of calls. The DB's own unique index is the real race-safety
    // net for the (very unlikely) window between this check and the insert,
    // not this check itself — see AddCustomLeagueAsync above.
    Task<bool> InviteCodeExistsAsync(string inviteCode, CancellationToken cancellationToken = default);

    // REQ-403: resolves a player-entered invite code to its League row, or
    // null if no custom league has that code. Mapping "no such code" to a
    // specific, clear error is the caller's (LeagueService.JoinByInviteCodeAsync)
    // responsibility, not this repository's.
    Task<League?> GetByInviteCodeAsync(string inviteCode, CancellationToken cancellationToken = default);

    // REQ-403: whether a user is already a member of a given league — lets
    // JoinByInviteCodeAsync treat re-entering a code for a league already
    // joined as an idempotent no-op rather than a duplicate-membership
    // failure.
    Task<bool> IsMemberAsync(Guid leagueId, Guid userId, CancellationToken cancellationToken = default);

    // REQ-402/403: every Type="custom" league a user currently belongs to —
    // this story's "simple list" of a player's own custom leagues,
    // deliberately excluding the Type="global" league every user is also
    // always a member of (REQ-401), since that's not what this list is for.
    Task<IReadOnlyList<League>> GetCustomLeaguesByMemberUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
}
