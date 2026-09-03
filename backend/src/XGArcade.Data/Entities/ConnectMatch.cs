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
// outcome reached). S-212 implemented the first transition
// (ConnectMatchLifecycleService.StartMatchIfBothPicksLockedAsync, called
// from ConnectTargetPickService's completing-pick branch) and the
// forfeit-timeout half of the second (ConnectMatchLifecycleService.
// RunForfeitSweepAsync). S-214 implements the remaining two
// terminal-reaching paths — REQ-1407's bust
// (ConnectChainStepService.SubmitChainStepAsync's bust branch, writing
// PlayerABustedAt/PlayerBBustedAt below) and REQ-1408's chain completion
// (detected via a ClosesChain=true ConnectChainStep row, not a column on
// this entity) — plus the mixed-outcome resolution logic
// (ConnectMatchLifecycleService.TryResolveMatchIfBothTerminalAsync) that
// ties all three terminal paths together into REQ-1409's win/draw/forfeit
// outcome.
//
// DeadlineUtc is StartedAt + 6h (REQ-1405's forfeit timer) — computed and
// persisted by ConnectMatchLifecycleService.StartMatchIfBothPicksLockedAsync
// the instant both target picks lock, not derived here.
//
// PlayerATimedOutAt/PlayerBTimedOutAt (REQ-1405, S-212): non-null once that
// SLOT (not UserId — PlayerAUserId/PlayerBUserId are nullable
// post-anonymization per REQ-710, so slot-based tracking is the only safe
// way to record "this participant, whoever they were/are, timed out") has
// been auto-forfeited by ConnectMatchLifecycleService.RunForfeitSweepAsync
// for not reaching a terminal state (timeout, bust, or chain completion) by
// DeadlineUtc. Each is set independently of the other — REQ-1405's "each
// player is forfeited independently" rule — and, once set, is never
// overwritten (MarkPlayerTimedOutAsync's own idempotent ??= semantics).
//
// PlayerABustedAt/PlayerBBustedAt (REQ-1407, S-214): the bust half of the
// same slot-based terminal-tracking shape — non-null once that slot has
// failed a second, consecutive attempt at the same chain position
// (ConnectChainStepService.SubmitChainStepAsync's bust branch), set via the
// same idempotent ??= semantics as MarkPlayerTimedOutAsync
// (MarkPlayerBustedAsync). A slot reaches terminal via exactly one of three
// paths — timeout, bust, or a ClosesChain=true ConnectChainStep row for
// that slot's UserId (REQ-1408) — and ConnectMatchLifecycleService.
// TryResolveMatchIfBothTerminalAsync is what evaluates all three uniformly
// once both slots have reached one of them.
//
// PlayerAScore/PlayerBScore (REQ-1408/1409, S-214): the persisted result of
// IConnectScoringService.CalculateScore for a player who actually completed
// a valid chain — null for a player who forfeited (bust or timeout), since
// REQ-1408 defines no comparable score for that case. Written exactly once,
// in the same ResolveMatchAsync call that sets Outcome/ResolvedAt — never a
// separate write.
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
    public DateTime? PlayerATimedOutAt { get; set; }
    public DateTime? PlayerBTimedOutAt { get; set; }
    public DateTime? PlayerABustedAt { get; set; }
    public DateTime? PlayerBBustedAt { get; set; }
    public int? PlayerAScore { get; set; }
    public int? PlayerBScore { get; set; }
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
