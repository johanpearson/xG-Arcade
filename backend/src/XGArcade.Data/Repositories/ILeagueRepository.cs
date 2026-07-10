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
}
