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

    Task<ConnectChainStep> AddChainStepAsync(ConnectChainStep chainStep, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectChainStep>> GetChainStepsForMatchAndUserAsync(
        Guid matchId, Guid? userId, CancellationToken cancellationToken = default);
}
