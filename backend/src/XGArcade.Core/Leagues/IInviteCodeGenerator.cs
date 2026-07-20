namespace XGArcade.Core.Leagues;

// REQ-402: generates the shareable, human-typeable code a custom league's
// invite is built around — kept as its own interface (not inlined in
// LeagueService) so tests can inject a deterministic sequence of codes to
// exercise CreateCustomLeagueAsync's collision-retry loop, which a real
// random generator can't be made to do on demand.
public interface IInviteCodeGenerator
{
    string Generate();
}
