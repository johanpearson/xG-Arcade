using XGArcade.Data.Entities;

namespace XGArcade.Core.Rounds;

public static class RoundStatusExtensions
{
    // REQ-302: only `active` rounds accept new guesses (enforced by S-009's
    // guess endpoint, not here) — this is just the status calculation itself.
    public static RoundStatus GetStatus(this Round round, DateTime now) =>
        now < round.StartTime ? RoundStatus.Upcoming
        : now > round.EndTime ? RoundStatus.Closed
        : RoundStatus.Active;
}
