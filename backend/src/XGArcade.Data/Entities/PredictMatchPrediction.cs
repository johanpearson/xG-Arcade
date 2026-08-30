namespace XGArcade.Data.Entities;

// Games.XGPredict (COMP-15) entity — one player's stored score prediction
// for one PredictMatch (REQ-1302). ADR-0096 §2: deliberately a SEPARATE
// top-level table, NOT owned by PredictMatch's own collection (unlike
// PredictInstance.Matches above) — predictions accumulate independently,
// from many different users, over the round's open window, the same reason
// Guess (Core.Scoring) is a top-level table rather than an owned collection
// of Round, not a per-match static field.
//
// PredictMatchId is a real FK to PredictMatch, cascade — both tables are
// COMP-15-internal, so (unlike Guess.CellId's deliberately-opaque
// cross-game shape) there is no ADR-0003 boundary reason to leave this
// unconstrained; same "own-component FK" precedent GridCell.GridInstanceId/
// PathPuzzle.PathInstanceId already set.
//
// UserId is nullable/unconstrained, mirroring Guess.UserId's own shape
// exactly, so REQ-710 account-deletion anonymization has an identical,
// already-proven path to reuse later (set UserId = null rather than
// hard-deleting the row).
//
// Unique index on (PredictMatchId, UserId) enforces REQ-1302's "a
// resubmission replaces the prior value, never inserts a second row" —
// same precedent as Guess's own (RoundId, UserId, CellId) unique index.
public class PredictMatchPrediction
{
    public Guid Id { get; set; }
    public required Guid PredictMatchId { get; set; }
    public Guid? UserId { get; set; } // nullable: mirrors Guess.UserId (REQ-710 anonymization precedent)
    public required int HomeGoals { get; set; }
    public required int AwayGoals { get; set; }
    public DateTime CreatedAt { get; set; }
}
