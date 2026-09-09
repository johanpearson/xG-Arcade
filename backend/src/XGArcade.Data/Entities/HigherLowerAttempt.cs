namespace XGArcade.Data.Entities;

// Games.XGHigherLower (COMP-18) entity — one participant's independent,
// in-progress (or ended) attempt at a HigherLowerInstance's fixed
// comparator sequence (REQ-1504). A SEPARATE top-level table, NOT owned by
// HigherLowerInstance's own Comparators collection — attempts accumulate
// independently, from many different users, over the round's open window,
// the same "own-user, own-progress" shape as PredictMatchPrediction/Guess,
// not a per-instance static field.
//
// HigherLowerInstanceId is a real FK to HigherLowerInstance, cascade — both
// tables are COMP-18-internal, same "own-component FK" precedent
// PredictMatchPrediction.PredictMatchId's own doc comment sets (no ADR-0003
// boundary reason to leave this unconstrained the way Guess.CellId is).
//
// UserId is nullable/unconstrained, mirroring Guess.UserId's/
// PredictMatchPrediction.UserId's own shape exactly — REQ-710 account-
// deletion anonymization (XGHigherLowerGameModule.PurgeUserDataAsync calling
// IHigherLowerInstanceRepository.AnonymizeAttemptsByUserIdAsync) reuses that
// already-proven path rather than a hard delete. Unlike PredictPlayerLock,
// nothing here forces a composite primary key that would make UserId
// non-nullable, so the nullable/anonymize precedent is the natural fit.
//
// StreakLength doubles as BOTH "how many comparators has this attempt
// correctly guessed so far" (REQ-1505's eventual FinalPoints source) AND
// "the 0-based SequencePosition of the next comparator still to guess" —
// the two are always numerically identical by construction (REQ-1504: a
// correct guess increments the streak by exactly one and advances to
// exactly the next position in the fixed sequence), so a single field
// covers both without redundancy. CurrentBaselinePlayerId/
// CurrentBaselineValue are the attempt's own current baseline — initially
// the HigherLowerInstance's own fixed BaselinePlayerId/BaselineValue,
// replaced by the just-resolved comparator's PlayerId/Value on every
// correct, non-terminal guess (REQ-1504). HasEnded is true once the attempt
// has reached a terminal outcome (an incorrect guess, or every comparator in
// the sequence guessed correctly) — no further guess is ever accepted once
// true (REQ-1504's last Given/When/Then block).
//
// Unique index on (HigherLowerInstanceId, UserId): at most one attempt row
// per participant per instance, same "a resubmission/further guess updates
// this row, never inserts a second one" precedent as
// PredictMatchPrediction's own (PredictMatchId, UserId) unique index.
//
// No separate row is created until a participant's FIRST guess — there is
// no dedicated "start attempt" endpoint/story (REQ-1504 has none), so
// XGHigherLowerGameModule.ScoreSubmissionAsync itself lazily initializes an
// implicit "streak 0, baseline = the instance's own fixed starting
// baseline" state whenever no row exists yet for a (instance, user) pair,
// and only persists a row once the first guess has been resolved.
public class HigherLowerAttempt
{
    public Guid Id { get; set; }
    public required Guid HigherLowerInstanceId { get; set; }
    public Guid? UserId { get; set; } // nullable: mirrors Guess.UserId/PredictMatchPrediction.UserId (REQ-710 anonymization precedent)
    public required int StreakLength { get; set; }
    public required Guid CurrentBaselinePlayerId { get; set; }
    public required int CurrentBaselineValue { get; set; }
    public required bool HasEnded { get; set; }
}
