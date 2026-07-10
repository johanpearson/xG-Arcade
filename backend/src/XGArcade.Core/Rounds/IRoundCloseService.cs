using XGArcade.Data.Entities;

namespace XGArcade.Core.Rounds;

// COMP-03/COMP-04: the round-close job REQ-205 and REQ-806 both refer to.
// Tier 0 (S-008) only implements the "close" half — making EndTime reflect
// immediate closure so REQ-302's live status calculation and REQ-201's
// active-round check both treat the round as closed right away. The other
// half of REQ-205 (locking each Guess's final_uniqueness_score/final_points)
// is S-011's job, once Guess/Core.Scoring exist — this interface is the
// extension point that story will add to, not a separate mechanism.
public interface IRoundCloseService
{
    // Returns null if roundId doesn't exist, so callers (e.g. REQ-806's
    // endpoint) can map that to 404 without a try/catch.
    Task<Round?> CloseRoundAsync(Guid roundId, DateTime closedAt, CancellationToken cancellationToken = default);
}
