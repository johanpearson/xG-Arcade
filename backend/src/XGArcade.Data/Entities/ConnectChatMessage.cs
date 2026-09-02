namespace XGArcade.Data.Entities;

// Games.XGConnect (COMP-17) entity — REQ-1410: one chat message exchanged
// between the two players of a ConnectMatch.
//
// ConnectMatchId is a real FK to ConnectMatch, cascade — same COMP-17-
// internal-FK reasoning as ConnectTargetPick.ConnectMatchId/
// ConnectChainStep.ConnectMatchId above.
//
// SenderUserId is nullable with NO FK to User — see ConnectMatch's own doc
// comment for the shared anonymize-in-place reasoning.
//
// Index on (ConnectMatchId, SentAt) is the only read shape REQ-1410 needs —
// a chronological per-match read.
public class ConnectChatMessage
{
    public Guid Id { get; set; }
    public required Guid ConnectMatchId { get; set; }
    public Guid? SenderUserId { get; set; }
    public required string MessageText { get; set; }
    public required DateTime SentAt { get; set; }
}
