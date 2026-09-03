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
    //
    // Bug fix (S-218 prep, ADR-0007): takes a raw player NAME, never a
    // client-supplied Guid — the only player-search UI a client has
    // (`/players/autocomplete`, COMP-10) returns `PlayerNameIndex.PlayerId`
    // values, which live in a different, unreconciled id space from
    // `Player.Id` (see `PlayerNameIndex.PlayerId`'s own doc comment). This
    // mirrors `ConnectChainStepService.SubmitChainStepAsync`'s own
    // candidatePlayerName resolution exactly: normalize
    // (`PlayerNameNormalizer.Normalize`), resolve via
    // `IPlayerRepository.GetPlayersByNormalizedFullNameAsync` (COMP-06,
    // never `PlayerNameIndex`), lowest-`Id`-wins on a same-name collision —
    // a known, deliberate simplification, not a new REQ, same as that
    // sibling's own comment.
    Task<SubmitTargetPickResult> SubmitTargetPickAsync(
        Guid matchId, Guid userId, string targetPlayerName, CancellationToken cancellationToken = default);
}

public enum SubmitTargetPickOutcome
{
    // REQ-1404: stored (or replaced) unlocked — the other participant has
    // no pick yet, so nothing to compare against and no puzzle decided yet.
    RecordedAwaitingOther,

    // REQ-1404: this was the completing (second) selection, the two target
    // picks are NOT already directly connected, and both ConnectTargetPick
    // rows for this match are now IsLocked = true — the puzzle is fixed.
    // This outcome IS also the trigger for the match-start transition
    // (ConnectMatch.Status/StartedAt/DeadlineUtc, REQ-1405) — see
    // ConnectTargetPickService.SubmitTargetPickAsync's own call to
    // IConnectMatchLifecycleService.StartMatchIfBothPicksLockedAsync,
    // added by S-212.
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

    // Bug fix (S-218 prep, ADR-0007): targetPlayerName didn't resolve to
    // any known Player (COMP-06) via an exact normalized-name match.
    // Nothing is persisted — ConnectTargetPick.TargetPlayerId is a
    // required, non-nullable FK, so there is no real player id to store.
    // Mirrors SubmitChainStepOutcome.CandidateNotFound's own doc comment.
    TargetPlayerNotFound,
}

// TargetPick is the CALLER's own resulting row — non-null only for
// RecordedAwaitingOther/RecordedAndLocked. Null for every other outcome
// (nothing was written).
public record SubmitTargetPickResult(SubmitTargetPickOutcome Outcome, ConnectTargetPick? TargetPick);
