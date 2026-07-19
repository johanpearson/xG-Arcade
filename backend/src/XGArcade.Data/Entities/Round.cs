namespace XGArcade.Data.Entities;

// COMP-03 (Core.Rounds) entity — persisted here alongside every other
// entity in the single shared DbContext, same ADR-0014 precedent as
// User/COMP-01 and GridTemplate/GridInstance/GridCell/COMP-05.
//
// Deliberately holds no foreign key to GridInstance or any other
// game-specific table (ADR-0003, architecture-document.md boundary rule 2):
// GameKey + GameInstanceId is an opaque pair, meaningful only to the
// IGameModule implementation that owns GameInstanceId.
public class Round
{
    public Guid Id { get; set; }

    public required string GameKey { get; set; }
    public required Guid GameInstanceId { get; set; }

    public required DateTime StartTime { get; set; }
    public required DateTime EndTime { get; set; }

    public bool AllowGuessChange { get; set; }

    // REQ-408: set once, the first time RoundCloseService.CloseRoundAsync
    // closes this round — null means "still active/upcoming, never
    // browsable via REQ-408's past-rounds endpoints" (ADR-0022's own
    // follow-up note, actioned here once a past-round-detail screen was
    // actually being built). Idempotent by construction: CloseRoundAsync
    // only ever sets this when it is still null, matching its existing
    // "only pull EndTime earlier" first-close-wins pattern, so a second
    // close never overwrites the original close timestamp.
    public DateTime? ClosedAt { get; set; }
}
