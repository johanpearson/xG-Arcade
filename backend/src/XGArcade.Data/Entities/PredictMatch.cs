namespace XGArcade.Data.Entities;

// Games.XGPredict (COMP-15) entity — one real-world Premier League match
// within a PredictInstance, and the "cell" IGameModule.GetCellIdsAsync
// returns for this game (PredictMatch.Id = cell id, matching that
// interface's existing contract — same precedent as GridCell.Id/
// PathPuzzle.Id). See ADR-0096 §1 for the full reasoning behind this shape
// versus GridCell's dynamically-matched categories and PathPuzzle's single
// fixed target player: a PredictMatch has no "answer" at all until REQ-1305's
// asynchronous grading (a separate, later story) confirms the real final
// score.
//
// ExternalFixtureId is API-Football's own id for this fixture (ADR-0094) —
// REQ-1305's future grading lookup key. HomeTeamName/AwayTeamName are
// display data. KickoffUtc is this match's own scheduled kickoff, always
// normalized to UTC (mirrors ApiFootballFixture.KickoffUtc's own
// normalization) — REQ-1303's round-lock instant is
// `Matches.Min(m => m.KickoffUtc)` across a PredictInstance's Matches,
// reconstructable from these rows alone without a second fetch.
//
// GradingStatus/ActualHomeGoals/ActualAwayGoals: REQ-1305/ADR-0097 §2 —
// added by this story. GradingStatus is the SOLE source of truth for
// "has this match been graded," never inferred from whether prediction
// rows happen to carry FinalPoints (PredictMatchPrediction.cs's own doc
// comment) — this is also PredictGradingService's whole idempotency
// mechanism (ADR-0097 Decision §3): a match is only ever considered by
// the grading query while GradingStatus == Pending. ActualHomeGoals/
// ActualAwayGoals are set only when GradingStatus == Graded; a Voided
// match never gets these written (API-Football's own values for a
// postponed/abandoned fixture are untrustworthy — see
// ApiFootballFixtureOutcome.PostponedOrAbandoned's own doc comment).
public class PredictMatch
{
    public Guid Id { get; set; }
    public required Guid PredictInstanceId { get; set; }
    public required int ExternalFixtureId { get; set; }
    public required string HomeTeamName { get; set; }
    public required string AwayTeamName { get; set; }
    public required DateTime KickoffUtc { get; set; }
    public PredictMatchGradingStatus GradingStatus { get; set; } = PredictMatchGradingStatus.Pending;
    public int? ActualHomeGoals { get; set; }
    public int? ActualAwayGoals { get; set; }
}

// REQ-1305/ADR-0097 §2: modeled as a plain enum (no HasConversion — same
// "no existing precedent for storing an enum as a string" convention
// PlayerSuggestionStatus/AvatarSubmissionStatus already establish; see
// AvatarSubmission.Status's own doc comment). Every match — including
// every one that already existed before this migration — starts and stays
// Pending until a grading run moves it to Graded or Voided; nothing ever
// moves a match backward out of Graded/Voided (mirrors REQ-205's "closing
// a round never re-scores it" precedent, extended here to "grading a
// match never re-grades it").
public enum PredictMatchGradingStatus
{
    Pending,
    Graded,
    Voided,
}
