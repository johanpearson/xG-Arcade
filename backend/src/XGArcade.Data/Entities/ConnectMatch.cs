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
// outcome reached). S-212 (this story) implements the first transition
// (ConnectMatchLifecycleService.StartMatchIfBothPicksLockedAsync, called
// from ConnectTargetPickService's completing-pick branch) and the
// forfeit-timeout half of the second (ConnectMatchLifecycleService.
// RunForfeitSweepAsync) — REQ-1407/1408's bust/chain-completion terminal
// paths (S-213/S-214) are the only other ways a player reaches terminal,
// and are not built yet.
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
// Timeout is currently the ONLY terminal-reaching path with real code
// behind it; REQ-1407 (bust)/REQ-1408 (chain completion) will each need
// their own "this slot reached terminal" write once S-213/S-214 build them,
// but that write is a different concept from these two timeout-specific
// columns — see ConnectMatchLifecycleService's own doc comment for how the
// two are meant to be told apart once both terminal-reaching paths exist.
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
