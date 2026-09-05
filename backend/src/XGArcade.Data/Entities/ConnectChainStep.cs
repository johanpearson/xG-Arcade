namespace XGArcade.Data.Entities;

// Games.XGConnect (COMP-17) entity — REQ-1406/1407: one submitted step in a
// player's chain-building attempt to link their ConnectMatch's two target
// players via real "played together" overlaps. Live validation logic
// (IsValid's computation) and the two-strikes/bust rule (REQ-1407) are
// later stories (S-213/S-214) — this entity only stores the outcome of each
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
// MatchedClubName/MatchedOverlapStartYear/MatchedOverlapEndYear (design
// change, 2026-09-04, REQ-1406, product-owner direction — see ADR-0104):
// replace the original ClaimedClubName, a free-text field the PLAYER typed
// to claim a specific club. That design asked the player to correctly
// recall and spell a club name that had to exactly match an
// already-canonicalized stored value — the direct cause of a real
// false-rejection bug (a genuinely correct step scored invalid because
// "Chelsea FC" as typed didn't string-match the stored "Chelsea"). The
// player no longer names a club at all: IPlayerCareerOverlapService
// .GetSharedClubOverlapsAsync computes every club (and overlapping year
// range) the candidate and the preceding chain player actually share, and
// ConnectChainStepService picks and persists ONE representative overlap
// (deterministically, the one with the latest OverlapStartYear, same
// "pick deterministically rather than invent new disambiguation" precedent
// this file's own candidate-name-collision handling already uses) when a
// pair shared more than one club (e.g. Maxwell and Zlatan Ibrahimović —
// Inter, Barcelona, PSG all valid). All three are null together, only for
// an invalid step (IsValid false — no club was found at all, nothing to
// record) — never independently null.
//
// IsValid is the outcome of the live overlapping-time-period check.
//
// ClosesChain (S-213/REQ-1406): true only on a step that is ALSO IsValid,
// where the candidate additionally has a valid overlapping-time shared-club
// connection (checked via IPlayerCareerOverlapService.HaveSharedClubOverlapAsync
// — any shared club, not restricted to this step's own MatchedClubName) to
// the match's OTHER target pick — never the one this player's chain started
// from. Never true when IsValid is false. Once a step with ClosesChain=true
// exists for a (ConnectMatchId, UserId) pair, that player's chain is
// complete and no further steps may be submitted for this match
// (ConnectChainStepService enforces this, not this entity).
//
// Index on (ConnectMatchId, UserId, Position, AttemptNumber) matches a
// future chain-reconstruction read's natural shape — deliberately NOT
// unique: both a failed first attempt and a successful retry at the same
// position are legitimate distinct rows.
//
// HasPendingDispute (REQ-1412/1413/ADR-0109): a denormalized cache of
// "does this step have a Pending ConnectChainStepDispute" — false by
// default, flipped true the instant a dispute is raised
// (IConnectMatchRepository.AddDisputeAsync) and back to false the instant
// it resolves in either direction (ApproveDisputeAsync/DenyDisputeAsync).
// ConnectChainStepDispute.Status remains the single source of truth; this
// column exists purely so ConnectChainStepExtensions.IsEffectivelyValid()
// (and every caller that used to read IsValid directly — see that
// extension's own doc comment) can answer "is this step valid for chain-
// continuation/closing/forfeiture purposes right now" from an already-
// loaded ConnectChainStep row alone, with no join — this component
// deliberately has no EF navigation properties anywhere (every FK here is
// configured store-only, see XGArcadeDbContext's own OnModelCreating), so
// a live join at every read site would mean re-deriving this same lookup
// repeatedly. ConnectMatchRepository's own dispute read/write methods are
// the ONLY code that ever sets this column — kept tightly scoped so the
// two can't drift.
public class ConnectChainStep
{
    public Guid Id { get; set; }
    public required Guid ConnectMatchId { get; set; }
    public Guid? UserId { get; set; }
    public required int Position { get; set; }
    public required int AttemptNumber { get; set; }
    public required Guid CandidatePlayerId { get; set; }
    public string? MatchedClubName { get; set; }
    public int? MatchedOverlapStartYear { get; set; }
    public int? MatchedOverlapEndYear { get; set; }
    public required bool IsValid { get; set; }
    public required bool ClosesChain { get; set; }
    public required DateTime SubmittedAt { get; set; }
    public bool HasPendingDispute { get; set; }
}
