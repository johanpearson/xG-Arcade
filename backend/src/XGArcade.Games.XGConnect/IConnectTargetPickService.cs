using XGArcade.Data.Entities;

namespace XGArcade.Games.XGConnect;

// S-211/REQ-1404: target-pick selection business logic, layered on top of
// IConnectMatchRepository's pure persistence primitives (S-208) — same
// "outcome enum + result record for expected branches" shape as
// IChallengeService/ISendChallengeResult (Core.Social).
public interface IConnectTargetPickService
{
    // Check-before-persist (see the implementation's own doc comment for the
    // exact step order) — a rejected completing submission never writes
    // anything, so the caller's own prior pick (if any) and the other
    // participant's pick are always left exactly as they were.
    Task<SubmitTargetPickResult> SubmitTargetPickAsync(
        Guid matchId, Guid userId, Guid targetPlayerId, CancellationToken cancellationToken = default);
}

public enum SubmitTargetPickOutcome
{
    // REQ-1404: stored (or replaced) unlocked — the other participant has
    // no pick yet, so nothing to compare against and no puzzle decided yet.
    RecordedAwaitingOther,

    // REQ-1404: this was the completing (second) selection, the two target
    // picks are NOT already directly connected, and both ConnectTargetPick
    // rows for this match are now IsLocked = true — the puzzle is fixed.
    // Does NOT itself start the match (ConnectMatch.Status/StartedAt/
    // DeadlineUtc) — that's S-212's own separate transition.
    RecordedAndLocked,

    MatchNotFound,

    // The caller is neither PlayerAUserId nor PlayerBUserId on this match.
    NotAParticipant,

    // The caller's own pick for this match is already IsLocked — the match
    // has officially started (or is about to, from the other player's
    // perspective) and target picks can no longer change for either player.
    AlreadyLocked,

    // REQ-1404: this would have been the completing selection, but the
    // candidate target already shares a club with an overlapping time
    // period with the other participant's existing pick — a direct,
    // zero-connection puzzle. Nothing is persisted: the caller's own prior
    // pick (if any) is untouched, and the other participant's pick is
    // completely untouched.
    TriviallyConnected,

    // ADR-0010/0011: the shared career-overlap check's live Wikidata
    // refresh couldn't complete in time — genuinely unknown, not a
    // rejection of anything the player did. Nothing is persisted, same as
    // TriviallyConnected.
    LiveLookupUnavailable,
}

// TargetPick is the CALLER's own resulting row — non-null only for
// RecordedAwaitingOther/RecordedAndLocked. Null for every other outcome
// (nothing was written).
public record SubmitTargetPickResult(SubmitTargetPickOutcome Outcome, ConnectTargetPick? TargetPick);
