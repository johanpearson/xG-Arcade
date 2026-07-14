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
}
