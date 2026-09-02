namespace XGArcade.Data.Entities;

// Games.XGConnect (COMP-17) entity — REQ-1406/1407: one submitted step in a
// player's chain-building attempt to link their ConnectMatch's two target
// players via real "played together" overlaps. Live validation logic
// (IsValid's computation) and the two-strikes/bust rule (REQ-1407) are
// later stories (S-212+) — this entity only stores the outcome of each
// attempt.
//
// ConnectMatchId is a real FK to ConnectMatch, cascade — same COMP-17-
// internal-FK reasoning as ConnectTargetPick.ConnectMatchId above.
//
// UserId is nullable with NO FK to User — see ConnectMatch's own doc
// comment for the shared anonymize-in-place reasoning.
//
// Position is the 1-based position in the chain (REQ-1407 tracks strikes
// per position); AttemptNumber is 1 for the first attempt at that position,
// 2 for the one allowed retry (REQ-1407's two-strikes rule) — a failed
// first attempt and a successful retry at the same position are both
// legitimate, distinct rows, never overwritten.
//
// CandidatePlayerId is a real, meaningful FK to Player (COMP-06), cascade —
// same PathPuzzle-style precedent as ConnectTargetPick.TargetPlayerId
// above.
//
// ClaimedClubName is free-text, NOT an FK to ClubDefinition — mirrors
// PlayerCareerStint.ClubName's own free-text shape, since REQ-1406
// explicitly requires candidates from the platform's broad player search,
// "not restricted to the curated club/country reference tables." The live
// overlap check this schema supports will validate against
// PlayerCareerStint data, not ClubDefinition.
//
// IsValid is the outcome of the live overlapping-time-period check
// (computed by a later story, not this one).
//
// Index on (ConnectMatchId, UserId, Position, AttemptNumber) matches a
// future chain-reconstruction read's natural shape — deliberately NOT
// unique: both a failed first attempt and a successful retry at the same
// position are legitimate distinct rows.
public class ConnectChainStep
{
    public Guid Id { get; set; }
    public required Guid ConnectMatchId { get; set; }
    public Guid? UserId { get; set; }
    public required int Position { get; set; }
    public required int AttemptNumber { get; set; }
    public required Guid CandidatePlayerId { get; set; }
    public required string ClaimedClubName { get; set; }
    public required bool IsValid { get; set; }
    public required DateTime SubmittedAt { get; set; }
}
