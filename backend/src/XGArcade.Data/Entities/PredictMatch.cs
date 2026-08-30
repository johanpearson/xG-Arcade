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
public class PredictMatch
{
    public Guid Id { get; set; }
    public required Guid PredictInstanceId { get; set; }
    public required int ExternalFixtureId { get; set; }
    public required string HomeTeamName { get; set; }
    public required string AwayTeamName { get; set; }
    public required DateTime KickoffUtc { get; set; }
}
