namespace XGArcade.Data.Entities;

// COMP-02 (Core.Leagues) entity — a player's membership in one league.
// REQ-401: every new user gets exactly one of these (the global league) at
// signup, with no action required from them.
public class LeagueMembership
{
    public Guid LeagueId { get; set; }
    public Guid UserId { get; set; }
}
