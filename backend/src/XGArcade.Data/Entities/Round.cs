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
}
