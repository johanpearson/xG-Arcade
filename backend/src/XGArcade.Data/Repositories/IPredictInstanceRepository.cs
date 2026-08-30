using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGPredict's (COMP-15) own persistence — the only path Games.XGPredict
// reaches PredictTemplate/PredictInstance/PredictMatch/PredictMatchPrediction
// through, same repository-per-component pattern as IPathInstanceRepository
// (COMP-11)/IGridInstanceRepository (COMP-05). ADR-0096.
public interface IPredictInstanceRepository
{
    Task<PredictTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // Persists instance + matches together, mirroring
    // IPathInstanceRepository.AddInstanceAsync's exact shape.
    Task<PredictInstance> AddInstanceAsync(PredictInstance instance, CancellationToken cancellationToken = default);

    Task<PredictInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-1302: the currently-stored prediction (if any) for one match/user
    // pair. userId mirrors Guess.UserId's own nullable shape (REQ-710
    // anonymization) — callers scoring a submission always pass the real
    // authenticated user's id; the nullable parameter exists so this method's
    // signature matches the column it queries.
    Task<PredictMatchPrediction?> GetPredictionAsync(Guid predictMatchId, Guid? userId, CancellationToken cancellationToken = default);

    // REQ-1302: store or replace (never insert a second row for) this
    // match/user pair's prediction — load-then-save, never
    // ExecuteUpdateAsync/ExecuteDeleteAsync (docs/coding-guidelines.md — the
    // InMemory test provider can't translate those). submittedAt is supplied
    // by the caller (XGPredictGameModule, via its own injectable
    // TimeProvider) rather than computed here, mirroring
    // GuessSubmissionService/ScoreLockingService's own "caller computes
    // `now`, repository just persists it" convention. On a resubmission
    // that replaces an existing row, PredictMatchPrediction.SubmittedAt is
    // overwritten to this new value — see that entity's own doc comment for
    // why it is named SubmittedAt rather than CreatedAt (quality-gate fix,
    // 2026-08-30).
    Task AddOrUpdatePredictionAsync(Guid predictMatchId, Guid? userId, int homeGoals, int awayGoals, DateTime submittedAt, CancellationToken cancellationToken = default);
}
