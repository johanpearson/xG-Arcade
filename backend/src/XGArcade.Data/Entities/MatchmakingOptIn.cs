namespace XGArcade.Data.Entities;

// Core.Social (COMP-16) entity — REQ-1403: one player opting into random
// matchmaking for an xG Connect match, waiting up to a 12-hour pairing
// window (S-210's future sweep job pairs waiting rows; not built by this
// story).
//
// UserId gets a real FK to User, cascade — same "pure flag row" precedent
// as FriendRequest/Challenge above.
//
// Status starts Waiting; the future sweep job moves it to Paired (a pairing
// was found) or Expired (12h window passed with no pairing) — neither
// transition is implemented by this story.
//
// ResultingMatchId mirrors Challenge.ResultingMatchId's own shape exactly:
// a plain, opaque Guid? column with NO FK into Games.XGConnect's
// ConnectMatch table (ADR-0003/ADR-0103) — see that property's own doc
// comment for the full reasoning.
public class MatchmakingOptIn
{
    public Guid Id { get; set; }
    public required Guid UserId { get; set; }
    public required DateTime OptedInAt { get; set; }
    public required DateTime ExpiresAt { get; set; }
    public MatchmakingOptInStatus Status { get; set; } = MatchmakingOptInStatus.Waiting;

    // See this entity's own doc comment above — deliberately opaque,
    // no FK into Games.XGConnect's ConnectMatch table (ADR-0003/ADR-0103).
    public Guid? ResultingMatchId { get; set; }
}

public enum MatchmakingOptInStatus
{
    Waiting,
    Paired,
    Expired,
}
