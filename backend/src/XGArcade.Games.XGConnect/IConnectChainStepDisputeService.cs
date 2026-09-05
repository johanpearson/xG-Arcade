using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect;

// REQ-1412/1413/1414 (docs/requirements-document.md §4.15), ADR-0109:
// raising, listing, and reviewing a dispute of a failed ConnectChainStep —
// layered on top of IConnectMatchRepository's own dispute persistence
// primitives, same "outcome enum + result record" shape as
// IConnectChainStepService/IConnectTargetPickService.
public interface IConnectChainStepDisputeService
{
    // REQ-1412: raises a dispute against `chainStepId`, naming
    // `claimedClubName` as the club the caller believes connects the
    // candidate and the immediately preceding chain player. Check-before-
    // persist, same discipline as SubmitChainStepAsync — every precondition
    // is resolved before anything is written.
    Task<RaiseChainStepDisputeResult> RaiseDisputeAsync(
        Guid matchId, Guid chainStepId, Guid userId, string claimedClubName, CancellationToken cancellationToken = default);

    // REQ-1413: only the match's OTHER participant (never the disputing
    // player, never anyone else) may resolve a Pending dispute — enforced
    // here, not left to the caller. `approve` selects the Approve/Deny
    // branch; see ReviewChainStepDisputeOutcome's own doc comment for each
    // branch's effect.
    Task<ReviewChainStepDisputeResult> ReviewDisputeAsync(
        Guid matchId, Guid disputeId, Guid reviewerUserId, bool approve, CancellationToken cancellationToken = default);

    // REQ-1412/1413: every dispute (any status) raised by either
    // participant in this match, in the caller's own perspective
    // (RaisedByMe) — backs both "what's the status of my own dispute" and
    // "what do I need to review as the opponent."
    Task<GetChainStepDisputesResult> GetDisputesForMatchAsync(
        Guid matchId, Guid userId, CancellationToken cancellationToken = default);
}

public enum RaiseChainStepDisputeOutcome
{
    // REQ-1412: the dispute is recorded Pending, the disputed step's slot is
    // marked busted (consuming this position's one REQ-1407 retry
    // immediately — see ConnectChainStepDisputeService's own comment), and
    // resolution is attempted (a no-op today, since this Pending dispute
    // itself now blocks it — REQ-1413).
    Raised,

    MatchNotFound,

    // The caller is neither PlayerAUserId nor PlayerBUserId on this match.
    NotAParticipant,

    // chainStepId doesn't resolve to any ConnectChainStep row belonging to
    // this match at all.
    StepNotFound,

    // REQ-1412: "a dispute can only be raised by the step's own owner" —
    // the caller IS a match participant (checked above), but this specific
    // step belongs to the OTHER participant.
    NotStepOwner,

    // The referenced step is already IsValid — nothing to dispute.
    StepNotInvalid,

    // REQ-1412: "only once per step" — a dispute (any status) already
    // exists for this exact step.
    AlreadyDisputed,

    // REQ-1412: "only on that player's own most-recent invalid step (can't
    // dispute an old superseded one)" — a later attempt already exists at
    // this same position (e.g. a failed first attempt once a failed or
    // successful retry has already been submitted at that position).
    StepSuperseded,

    // claimedClubName was null/blank/whitespace-only.
    InvalidClaimedClubName,
}

public record RaiseChainStepDisputeResult(RaiseChainStepDisputeOutcome Outcome, ConnectChainStepDispute? Dispute);

public enum ReviewChainStepDisputeOutcome
{
    // REQ-1413: the disputed step becomes a permanent, valid step (its
    // claimed club becomes MatchedClubName, no re-verification), scored
    // exactly like an ordinary successful validation at that attempt
    // number the next time this match resolves (zero new scoring logic —
    // IConnectScoringService.CalculateScore is unchanged), the disputing
    // player's provisional bust is cleared, and a REQ-1414 data-correction
    // suggestion is durably recorded.
    Approved,

    // REQ-1413: the disputed step, and every step that player built after
    // it in their own chain, are discarded — the player's provisional bust
    // (already set the instant the dispute was raised) is NOT cleared, so
    // it stands as a real, final bust.
    Denied,

    MatchNotFound,

    // The caller is neither PlayerAUserId nor PlayerBUserId on this match.
    NotAParticipant,

    // disputeId doesn't resolve to any dispute belonging to this match.
    DisputeNotFound,

    // REQ-1413: "only the other participant... never the disputing player"
    // — the caller IS a match participant (checked above), but is the same
    // player who raised this specific dispute.
    CannotReviewOwnDispute,

    // The dispute is no longer Pending (already Approved or Denied).
    AlreadyReviewed,
}

public record ReviewChainStepDisputeResult(ReviewChainStepDisputeOutcome Outcome, ConnectChainStepDispute? Dispute);

public enum GetChainStepDisputesOutcome
{
    Found,
    MatchNotFound,
    NotAParticipant,
}

// One dispute row, in the caller's own perspective. Position is the
// disputed step's own chain position (never exposed via ChainStepId alone,
// since the caller needs it for display without a second round trip).
// RaisedByMe is true only when the CALLER raised this specific dispute —
// the opponent-review UI filters/highlights on this; a dispute the caller
// raised themselves is never reviewable by them (CannotReviewOwnDispute).
public record ChainStepDisputeView(
    Guid DisputeId,
    Guid ChainStepId,
    int Position,
    string ClaimedClubName,
    ConnectChainStepDisputeStatus Status,
    DateTime RaisedAt,
    DateTime? ReviewedAt,
    bool RaisedByMe);

public record GetChainStepDisputesResult(GetChainStepDisputesOutcome Outcome, IReadOnlyList<ChainStepDisputeView> Disputes);
