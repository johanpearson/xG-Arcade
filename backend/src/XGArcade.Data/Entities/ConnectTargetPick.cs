namespace XGArcade.Data.Entities;

// Games.XGConnect (COMP-17) entity — REQ-1404/1405: one player's chosen
// target player for a ConnectMatch. Independent, mutually-invisible target
// selection (REQ-1404) until both picks lock at match start (REQ-1405) — the
// visibility/timing rules themselves are S-211's service logic, not this
// story's.
//
// ConnectMatchId is a real FK to ConnectMatch, cascade — both tables are
// COMP-17-internal, no ADR-0003/ADR-0103 boundary concern (that boundary is
// specifically about Core.Social never holding a direct reference into
// Games.XGConnect internals, not about this table's own internal FKs).
//
// UserId is nullable with NO FK to User — see ConnectMatch's own doc
// comment for the shared anonymize-in-place reasoning across every
// UserId-shaped column on this entity and its COMP-17 siblings.
//
// TargetPlayerId is a real, meaningful FK to Player (COMP-06), cascade —
// mirrors PathPuzzle.TargetPlayerId's own precedent (a game module
// referencing shared player data is a different boundary than ADR-0003's
// Core/game FK omission).
//
// IsLocked flips true once the match officially starts (REQ-1405) — never
// set by this story.
//
// Unique index on (ConnectMatchId, UserId) mirrors PredictMatchPrediction's
// own "at most one row per (match, user), a resubmission overwrites it"
// shape — the future store/replace write path (S-211) will look exactly
// like PredictInstanceRepository.AddOrUpdatePredictionAsync.
public class ConnectTargetPick
{
    public Guid Id { get; set; }
    public required Guid ConnectMatchId { get; set; }
    public Guid? UserId { get; set; }
    public required Guid TargetPlayerId { get; set; }
    public required DateTime SelectedAt { get; set; }
    public bool IsLocked { get; set; }
}
