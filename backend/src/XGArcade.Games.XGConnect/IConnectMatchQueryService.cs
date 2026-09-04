using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect;

// S-218 prep (docs/backlog.md's S-218 entry, REQ-1404/1405/1406/1409/1411):
// every existing xG Connect endpoint before this was write-only — there was
// no way for a client to read a match's current state at all, which blocks
// S-218's frontend gameplay screen. This is the read-only projection layer
// backing GET /matches and GET /matches/{matchId}
// (XGArcade.Api.Connect.ConnectMatchQueryEndpoints). Same
// "outcome enum + result record" shape as
// IConnectTargetPickService/IConnectChainStepService for the single-match
// read, and a plain list for the multi-match read (mirrors
// IConnectMatchLifecycleService.GetMatchesAwaitingActionAsync's own plain-
// list shape).
//
// Deliberately reuses rather than re-derives: ConnectMatchAccessExtensions.
// ResolveParticipantMatchAsync (found/not-found/not-a-participant, already
// shared by four other call sites), IConnectMatchLifecycleService.
// GetMatchesAwaitingActionAsync (the exact per-slot terminal-state check
// backing AwaitingMyAction below — REQ-1411), and
// ConnectChainStepExtensions.HasClosedChain (the "completed" component of a
// terminal-state view). No new terminal-state rule is introduced here.
public interface IConnectMatchQueryService
{
    // Every match (open or Resolved) the caller participates in, in the
    // caller's own perspective (Outcome translated, OpponentUserId instead
    // of PlayerA/PlayerB slots) — GET /matches.
    Task<IReadOnlyList<ConnectMatchSummary>> GetMatchesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    // Full single-match detail for the gameplay screen — GET
    // /matches/{matchId}. NotAParticipant/MatchNotFound mirror every other
    // xG Connect write endpoint's own 403/404 mapping (see
    // ConnectMatchAccessOutcome), so the two surfaces stay consistent about
    // who's allowed to see what.
    Task<ConnectMatchDetailResult> GetMatchDetailAsync(
        Guid matchId, Guid userId, CancellationToken cancellationToken = default);
}

public enum ConnectMatchDetailOutcome
{
    Found,
    MatchNotFound,
    NotAParticipant,
}

// REQ-1409: the match's Outcome (Pending/PlayerAWin/PlayerBWin/Draw)
// translated into the CALLER's own perspective — a client should never have
// to know which slot (PlayerA/PlayerB) it occupies. Win/Loss are mutually
// exclusive per caller by construction (TranslateOutcome in
// ConnectMatchQueryService is the single place this mapping is computed).
public enum ConnectMatchPerspectiveOutcome
{
    Pending,
    Win,
    Loss,
    Draw,
}

// One list-row for GET /matches. OpponentUserId is nullable — null once
// REQ-710 anonymization has run for that participant, same nullable shape
// every other xG Connect response already uses for a UserId-shaped field
// (e.g. ConnectChatMessage.SenderUserId via ChatMessageResponse).
// OpponentDisplayName mirrors OpponentUserId's own nullability exactly — null
// whenever OpponentUserId is null, never a placeholder — resolved via a
// single batch IUserRepository.GetByIdsAsync call across every row rather
// than one lookup per row (SCREEN-15 "Identity gap" fix already applied to
// Core.Social's FriendshipResponse/ChallengeResponse; same
// batch-then-map shape LeaderboardService established for REQ-404).
// AwaitingMyAction is exactly IConnectMatchLifecycleService.
// GetMatchesAwaitingActionAsync's own membership test for this match — see
// ConnectMatchQueryService.GetMatchesForUserAsync's own comment for why a
// Resolved match always yields false here without a separate branch.
public record ConnectMatchSummary(
    Guid MatchId,
    Guid? OpponentUserId,
    string? OpponentDisplayName,
    ConnectMatchStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? DeadlineUtc,
    DateTime? ResolvedAt,
    ConnectMatchPerspectiveOutcome Outcome,
    bool AwaitingMyAction);

// A resolved target pick, with the player's display name already joined in
// (IPlayerRepository.GetPlayersByIdsAsync) so the frontend gameplay screen
// never needs a second round trip just to label a target.
public record ConnectTargetPickView(Guid TargetPlayerId, string TargetPlayerName, bool Locked);

// One of the caller's OWN chain steps, in submission order. Never used for
// an opponent's steps — see ConnectMatchDetail.OpponentTerminalState's own
// comment for why the opponent's actual steps are never returned.
public record ConnectChainStepView(
    int Position,
    int AttemptNumber,
    Guid CandidatePlayerId,
    string CandidatePlayerName,
    string ClaimedClubName,
    bool IsValid,
    bool ClosesChain,
    DateTime SubmittedAt);

// The three terminal-reaching signals ConnectMatchLifecycleService already
// checks (timeout/REQ-1405, bust/REQ-1407, chain completion/REQ-1408),
// bundled for display — deliberately NOT "has this player reached a
// terminal state" collapsed to a single bool, since the gameplay screen
// needs to say which one (e.g. "your opponent timed out" vs. "your
// opponent finished their chain").
public record ConnectTerminalState(bool Busted, bool TimedOut, bool Completed);

// Full single-match detail (GET /matches/{matchId}).
//
// OpponentTargetPick is null both when the opponent hasn't picked yet AND
// (REQ-1404) whenever Status is still AwaitingTargetPicks, even if the
// opponent's own pick already exists unlocked in the database — REQ-1404's
// mutual-invisibility rule means the caller must never see it before both
// picks are locked, regardless of the caller's own pick state. See
// ConnectMatchQueryService.GetMatchDetailAsync's own comment.
//
// OpponentTerminalState.Completed is derived from the opponent's own chain
// steps (ConnectChainStepExtensions.HasClosedChain), but those steps
// themselves are never included anywhere in this record — only whether
// they collectively reached a terminal state. REQ-1406 doesn't require
// live visibility into an opponent's in-progress chain; keeping the actual
// steps private is a minimal, reasonable default, not a new structural
// decision.
// OpponentDisplayName mirrors OpponentUserId's own nullability exactly —
// see ConnectMatchSummary.OpponentDisplayName's own doc comment for the
// same rule/batch-resolve rationale (a single-id resolve here, since this
// is a single-match read).
public record ConnectMatchDetail(
    ConnectMatchStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? DeadlineUtc,
    DateTime? ResolvedAt,
    ConnectMatchPerspectiveOutcome Outcome,
    Guid? OpponentUserId,
    string? OpponentDisplayName,
    ConnectTargetPickView? MyTargetPick,
    ConnectTargetPickView? OpponentTargetPick,
    IReadOnlyList<ConnectChainStepView> MyChainSteps,
    ConnectTerminalState MyTerminalState,
    ConnectTerminalState OpponentTerminalState,
    int? MyScore,
    int? OpponentScore);

// Detail is non-null only for Found — mirrors SubmitChainStepResult's own
// "result payload null except for the outcome(s) that actually have one"
// shape.
public record ConnectMatchDetailResult(ConnectMatchDetailOutcome Outcome, ConnectMatchDetail? Detail);
