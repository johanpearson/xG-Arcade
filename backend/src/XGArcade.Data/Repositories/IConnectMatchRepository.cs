using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGConnect's (COMP-17) own persistence — the only path
// Games.XGConnect reaches ConnectMatch/ConnectTargetPick/ConnectChainStep
// through, mirrors IPredictInstanceRepository owning
// PredictInstance+PredictMatch+PredictMatchPrediction+PredictPlayerLock
// together (one component's whole entity family, one repository). See
// ADR-0103.
//
// S-208 (this story) scaffolds pure persistence primitives only — no
// trivial-pair rejection, no match-start/lock transition logic, no live
// overlap validation, no bust/penalty/scoring/resolution logic. Those are
// S-211 through S-215's business logic, layered on top of these methods by
// future service classes, not this repository.
public interface IConnectMatchRepository
{
    Task<ConnectMatch> AddMatchAsync(ConnectMatch match, CancellationToken cancellationToken = default);

    Task<ConnectMatch?> GetMatchByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-1404/1405: store or replace (never insert a second row for) this
    // match/user pair's target pick — load-then-save, mirrors
    // PredictInstanceRepository.AddOrUpdatePredictionAsync's own
    // store/replace shape exactly. selectedAt is supplied by the caller,
    // same "caller computes `now`, repository just persists it" convention.
    Task<ConnectTargetPick> AddOrUpdateTargetPickAsync(
        Guid matchId, Guid? userId, Guid targetPlayerId, DateTime selectedAt, CancellationToken cancellationToken = default);

    Task<ConnectTargetPick?> GetTargetPickAsync(Guid matchId, Guid? userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectTargetPick>> GetTargetPicksForMatchAsync(Guid matchId, CancellationToken cancellationToken = default);

    // REQ-1404/S-211: flips IsLocked to true on EVERY ConnectTargetPick row
    // for this match (both participants' picks) in one call — the "fixed,
    // puzzle decided" transition that fires once the second (completing)
    // selection is confirmed as NOT trivially already connected (see
    // ConnectTargetPickService.SubmitTargetPickAsync). Deliberately whole-
    // match-scoped rather than per-pick-id: by the time this is ever called,
    // exactly two ConnectTargetPick rows exist for this match (the caller's
    // just-stored pick and the other participant's already-existing one),
    // and both must lock together — there is no valid state where only one
    // of the two should end up locked. Load-then-SaveChangesAsync
    // (coding-guidelines.md), never ExecuteUpdateAsync. Does NOT touch
    // ConnectMatch.Status/StartedAt/DeadlineUtc — that's
    // ConnectMatchLifecycleService.StartMatchIfBothPicksLockedAsync's own
    // separate match-start/timer transition (REQ-1405, S-212), which
    // re-detects "both target picks locked" via GetTargetPicksForMatchAsync
    // rather than being triggered directly from here.
    Task LockTargetPicksForMatchAsync(Guid matchId, CancellationToken cancellationToken = default);

    // REQ-1405/S-212: the four methods below back
    // ConnectMatchLifecycleService's match-start/forfeit-timer/resolution
    // scaffolding. Every call trusts an already-known-valid matchId (the
    // caller always resolved it via GetMatchByIdAsync/
    // GetActiveMatchesPastDeadlineAsync first) — no defensive not-found
    // handling, per CLAUDE.md's "don't add error handling for scenarios
    // that can't happen".

    // The forfeit sweep's own candidate set: every match still Active whose
    // shared 6h deadline has already passed. AsNoTracking — the sweep only
    // reads match identity/slot state from these rows before deciding what
    // to write through MarkPlayerTimedOutAsync/ResolveMatchAsync below, it
    // never mutates the objects returned here directly.
    Task<IReadOnlyList<ConnectMatch>> GetActiveMatchesPastDeadlineAsync(
        DateTime now, CancellationToken cancellationToken = default);

    // The match-start transition (REQ-1405): both target picks just locked,
    // so the shared 6h forfeit clock starts for BOTH players from this same
    // instant. Load-then-save, tracked (unlike the AsNoTracking read above)
    // since this row is mutated in place.
    Task<ConnectMatch> StartMatchAsync(
        Guid matchId, DateTime startedAt, DateTime deadlineUtc, CancellationToken cancellationToken = default);

    // Forfeits exactly one SLOT (PlayerA or PlayerB, never a UserId — see
    // ConnectMatch's own doc comment for why slot-based tracking is the only
    // safe approach post-anonymization). Idempotent: `??=` semantics, so a
    // slot already marked timed-out is left with its original timestamp,
    // never overwritten by a later sweep pass.
    Task<ConnectMatch> MarkPlayerTimedOutAsync(
        Guid matchId, bool isPlayerA, DateTime timedOutAt, CancellationToken cancellationToken = default);

    // REQ-1407/S-214: the bust half of the same slot-based terminal-marking
    // shape as MarkPlayerTimedOutAsync immediately above — same `??=`
    // idempotent semantics (a slot already marked busted is left with its
    // original timestamp), called from
    // ConnectChainStepService.SubmitChainStepAsync's bust branch (a second,
    // consecutive failure at the same chain position).
    Task<ConnectMatch> MarkPlayerBustedAsync(
        Guid matchId, bool isPlayerA, DateTime bustedAt, CancellationToken cancellationToken = default);

    // The resolution transition (REQ-1405/1409): both players have now
    // reached a terminal state (by whatever mix of timeout/bust/completion
    // paths exist), so the match is fixed at this outcome, immediately —
    // never deferred to wait out any unused remainder of the 6h window.
    // playerAScore/playerBScore (REQ-1408, S-214) are persisted in this same
    // write — null for a player who forfeited (no valid score), non-null
    // only for a player who completed a valid chain. One resolution write,
    // never a separate follow-up call to persist scores.
    Task<ConnectMatch> ResolveMatchAsync(
        Guid matchId, ConnectMatchOutcome outcome, DateTime resolvedAt,
        int? playerAScore, int? playerBScore, CancellationToken cancellationToken = default);

    Task<ConnectChainStep> AddChainStepAsync(ConnectChainStep chainStep, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectChainStep>> GetChainStepsForMatchAndUserAsync(
        Guid matchId, Guid? userId, CancellationToken cancellationToken = default);

    // REQ-1411/S-216: the notification indicator's own candidate set — every
    // match this user is a participant in (either slot) that has not yet
    // reached ConnectMatch.Status == Resolved. Deliberately a pure
    // status/participant filter only, same "repository returns the coarse
    // candidate set, caller does the finer per-slot terminal-state check"
    // split GetActiveMatchesPastDeadlineAsync above establishes for
    // ConnectMatchLifecycleService.RunForfeitSweepAsync — the bust/timeout/
    // ClosesChain-per-slot filtering that turns this into "matches actually
    // awaiting THIS user's own next move" lives in
    // ConnectMatchLifecycleService.GetMatchesAwaitingActionAsync (Games.
    // XGConnect), not here, since that filtering already depends on
    // ConnectChainStepExtensions.HasClosedChain and this repository has no
    // business evaluating game-specific terminal-state rules itself.
    // AsNoTracking — same read-only shape as every other list read on this
    // interface.
    Task<IReadOnlyList<ConnectMatch>> GetOpenMatchesForUserAsync(
        Guid userId, CancellationToken cancellationToken = default);

    // REQ-710/ADR-0101: anonymizes every UserId-shaped column this user
    // appears in, across all three of this component's per-user tables —
    // ConnectMatch.PlayerAUserId/PlayerBUserId, ConnectTargetPick.UserId,
    // ConnectChainStep.UserId. Mirrors
    // IPredictInstanceRepository.AnonymizePredictionsByUserIdAsync's
    // anonymize-in-place shape (every one of these columns is nullable with
    // no FK to User, per each entity's own doc comment) rather than a hard
    // delete — other participants' match history/chain data depends on
    // these rows surviving. Called only from XGConnectGameModule.
    // PurgeUserDataAsync (this is the one place outside Games.XGConnect's
    // own boundary Core.Auth's AccountDeletionService reaches, via
    // IGameModule, never this repository directly — ADR-0003/ADR-0101).
    // Does NOT touch ConnectChatMessage.SenderUserId — that table is owned
    // by the separate IConnectChatMessageRepository, which has its own
    // AnonymizeSenderAsync method (S-215/REQ-1410) that
    // XGConnectGameModule.PurgeUserDataAsync calls alongside this one.
    Task AnonymizeUserDataAsync(Guid userId, CancellationToken cancellationToken = default);
}
