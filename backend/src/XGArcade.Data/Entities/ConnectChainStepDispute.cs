namespace XGArcade.Data.Entities;

// Games.XGConnect (COMP-17) entity — REQ-1412/1413,
// docs/decisions/0109-connect-dispute-reintroduces-claimed-club-narrowly.md:
// a player's dispute of their own failed ConnectChainStep, naming the
// specific club they believe connects the candidate and the immediately
// preceding chain player — the one narrow, dispute-only reintroduction of a
// player-typed claimed club that ADR-0104 otherwise removed from ordinary
// chain-step submission. See ADR-0109's own "For AI agents" note before
// extending this field's use anywhere else.
//
// ConnectChainStepId is a real FK to ConnectChainStep, cascade — at most one
// dispute per step, ever, regardless of outcome (IX_ConnectChainStepDisputes
// _ConnectChainStepId is unique, enforced at the DB level; the service layer
// also checks this before insert so it can return a clean AlreadyDisputed
// outcome instead of surfacing a raw constraint violation). No navigation
// property either direction — matches this component's existing FK-only,
// no-nav-property convention (see ConnectChainStep/ConnectTargetPick/
// ConnectChatMessage's own FKs to ConnectMatch/Player in
// XGArcadeDbContext.OnModelCreating).
//
// ClaimedClubName has NO server-side validation against PlayerCareerStint,
// PlayerAttribute, or any other career/attribute data, by deliberate design
// (ADR-0109) — the match's own opponent's approval (REQ-1413) is the only
// check this flow has. Never compared, normalized, or matched against
// anything server-side.
//
// Status starts Pending and is set exactly once, at review time (REQ-1413)
// — mirrors PlayerSuggestionStatus's own resolve-once shape. See
// ConnectChainStep.HasPendingDispute's own doc comment for the denormalized
// cache this column's Pending state is mirrored into everywhere chain
// validity is checked; that cache is a read-side convenience only — this
// Status column is the single source of truth.
//
// RaisedAt is when the dispute was created (the same instant that position's
// one REQ-1407 retry is consumed, per the product-owner's 2026-09-05
// confirmation — see REQ-1413's own status note). ReviewedAt is null until
// Approved/Denied, set in that same review call, never independently.
public class ConnectChainStepDispute
{
    public Guid Id { get; set; }
    public required Guid ConnectChainStepId { get; set; }
    public required string ClaimedClubName { get; set; }
    public ConnectChainStepDisputeStatus Status { get; set; } = ConnectChainStepDisputeStatus.Pending;
    public required DateTime RaisedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
}

public enum ConnectChainStepDisputeStatus
{
    Pending,
    Approved,
    Denied,
}
