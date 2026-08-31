namespace XGArcade.Data.Entities;

// REQ-1306/ADR-0098: one player's explicit, optional "confirm and lock"
// action for one PredictInstance (COMP-15) — independent of, and does not
// substitute for, REQ-1303's round-wide automatic lock at the first match's
// kickoff. Composite-keyed on (PredictInstanceId, UserId), same "no
// surrogate id needed for a pure membership/flag row" precedent
// LeagueMembership already sets (XGArcadeDbContext.OnModelCreating) — see
// ADR-0098 for why this is its own table rather than a column on
// PredictMatchPrediction.
//
// The existence of a row IS the lock; there is no boolean to flip back off —
// REQ-1306 never lets a player un-confirm once locked (the "dismiss/cancel
// the prompt" case in REQ-1306's acceptance criteria never reaches this
// table at all, since no row is written until the player affirms).
// LockedAt is display/audit data only, mirroring PredictMatchPrediction.
// SubmittedAt's own "when this happened" role; nothing currently reads it
// back to make a decision.
public class PredictPlayerLock
{
    public required Guid PredictInstanceId { get; set; }
    public required Guid UserId { get; set; }
    public DateTime LockedAt { get; set; }
}
