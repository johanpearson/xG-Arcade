namespace XGArcade.Data.Entities;

// Games.XGConnect (COMP-17) entity — REQ-1404/1405: an xG Connect match
// between two players, created on demand (a resolved Challenge/REQ-1402 or
// MatchmakingOptIn pairing/REQ-1403), never via RoundGenerationService/
// IRoundSchedulingOptionsResolver and never assigned a GameKey/
// GameInstanceId pair under Round. See ADR-0103 for the full "why a new
// first-class concept, not a Round" reasoning — Core.Rounds/Core.Scoring/
// Core.Leagues are untouched by this story.
//
// PlayerAUserId/PlayerBUserId are deliberately NULLABLE and have NO FK to
// User — mirrors Guess.UserId's/PredictMatchPrediction.UserId's own
// anonymize-in-place shape, not LeagueMembership's hard-delete shape.
// ADR-0103 requires Games.XGConnect to implement IGameModule.
// PurgeUserDataAsync (REQ-710/ADR-0101's per-module purge hook) even though
// the purge logic itself is a later story — leaving these columns
// anonymize-capable now avoids a schema migration later just to support
// that purge, the same forward-compatibility reasoning Guess/
// PredictMatchPrediction already established. Every UserId-shaped column on
// this entity and the three below it (ConnectTargetPick.UserId,
// ConnectChainStep.UserId, ConnectChatMessage.SenderUserId) follows the same
// nullable-no-FK treatment for the same reason.
//
// Status: AwaitingTargetPicks (initial) -> Active (both picks locked,
// StartedAt set, REQ-1405) -> Resolved (REQ-1409's win/draw/forfeit
// outcome reached). None of those transitions are written by this story —
// S-208 only scaffolds the schema/CRUD those later stories (S-211 onward)
// will drive.
//
// DeadlineUtc is StartedAt + 6h (REQ-1405's forfeit timer) — computed and
// persisted by the caller once the match starts, not derived here.
//
// Outcome defaults to Pending and is set exactly once, at resolution
// (REQ-1409) — mirrors PredictMatch.GradingStatus's own "sole source of
// truth for resolution state" role.
public class ConnectMatch
{
    public Guid Id { get; set; }
    public Guid? PlayerAUserId { get; set; }
    public Guid? PlayerBUserId { get; set; }
    public required DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? DeadlineUtc { get; set; }
    public ConnectMatchStatus Status { get; set; } = ConnectMatchStatus.AwaitingTargetPicks;
    public ConnectMatchOutcome Outcome { get; set; } = ConnectMatchOutcome.Pending;
    public DateTime? ResolvedAt { get; set; }
}

public enum ConnectMatchStatus
{
    AwaitingTargetPicks,
    Active,
    Resolved,
}

public enum ConnectMatchOutcome
{
    Pending,
    PlayerAWin,
    PlayerBWin,
    Draw,
}
