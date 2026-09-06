namespace XGArcade.Data.Entities;

// Games.XGConnect (COMP-17) entity — REQ-1414. Per ADR-0053's own precedent
// (REQ-215's PlayerSuggestion got its own new, separate admin view/table
// rather than being folded into an unrelated queue), this is a deliberately
// new, small, standalone table — a club-overlap fact discovered through an
// approved xG Connect dispute, structurally different from PlayerSuggestion's
// cell-guess-candidate shape or PlayerData's unverified-attribute queue.
//
// Recorded exactly once, at the instant a ConnectChainStepDispute is
// Approved (REQ-1413) — never written to again, and never read by, or
// written from, anything that affects any match's own outcome/score
// (REQ-1414's own "purely additive, optional follow-up data" rule; see
// IConnectMatchRepository.AddDataCorrectionSuggestionAsync's own doc
// comment). No approve/reject/act-on workflow exists for this table at
// all — it is a durable, admin-readable record only
// (GET /admin/connect-dispute-suggestions); REQ-1414's own text leaves the
// actual future data-correction mechanism (e.g. a PlayerOverride-style
// write) to a later decision, deliberately out of scope here.
//
// CandidatePlayerId/PrecedingPlayerId are real FKs to Player (COMP-06),
// cascade — same meaningful-FK precedent as ConnectChainStep.
// CandidatePlayerId/ConnectTargetPick.TargetPlayerId.
//
// ConnectChainStepDisputeId is the one real, enforced FK into this
// component's own tables (cascade) — "a reference to the match and step
// the dispute came from" (REQ-1414's own wording) is satisfied
// transitively through it (ConnectChainStepDispute.ConnectChainStepId ->
// ConnectChainStep.ConnectMatchId). ConnectMatchId/ConnectChainStepId below
// are deliberately PLAIN, unenforced denormalized columns (no
// HasForeignKey configured — same precedent as Challenge.ResultingMatchId/
// MatchmakingOptIn.ResultingMatchId) purely so the admin read endpoint
// doesn't need a multi-hop join to display them; see
// XGArcadeDbContext.OnModelCreating's own comment for why a THIRD cascade
// path down to ConnectMatch was deliberately not configured.
public class ConnectDisputeDataCorrectionSuggestion
{
    public Guid Id { get; set; }
    public required Guid ConnectMatchId { get; set; }
    public required Guid ConnectChainStepId { get; set; }
    public required Guid ConnectChainStepDisputeId { get; set; }
    public required Guid CandidatePlayerId { get; set; }
    public required Guid PrecedingPlayerId { get; set; }
    public required string ClaimedClubName { get; set; }
    public required DateTime CreatedAt { get; set; }
}
