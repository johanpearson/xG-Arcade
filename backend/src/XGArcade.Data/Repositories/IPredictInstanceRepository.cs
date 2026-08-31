using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGPredict's (COMP-15) own persistence — the only path Games.XGPredict
// reaches PredictTemplate/PredictInstance/PredictMatch/PredictMatchPrediction
// through, same repository-per-component pattern as IPathInstanceRepository
// (COMP-11)/IGridInstanceRepository (COMP-05). ADR-0096.
public interface IPredictInstanceRepository
{
    Task<PredictTemplate?> GetTemplateByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // Mirrors IPathInstanceRepository.GetTemplateByPuzzleCountAsync/
    // AddTemplateAsync's exact find-or-create-by-config-value pattern —
    // PredictTemplateResolver (XGArcade.Api.Predict) is the caller, same
    // role PathTemplateResolver/GridTemplateResolver play for their own
    // template types. See ADR-0051's 2026-08-30 amendment (xG Predict
    // wiring) for the re-derivation confirming this pattern still holds.
    Task<PredictTemplate?> GetTemplateByMatchCountAsync(int matchCount, CancellationToken cancellationToken = default);
    Task<PredictTemplate> AddTemplateAsync(PredictTemplate template, CancellationToken cancellationToken = default);

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

    // REQ-1305/ADR-0097 §3: the grading job's own query — every PredictMatch
    // still Pending whose kickoff, plus this GameKey-wide typical match
    // duration, has already passed `nowUtc`. No Round/IRoundRepository
    // dependency: a match's own kickoff having passed already implies its
    // round is locked (ADR-0097's Context — the round lock instant is
    // always the MINIMUM of its 5 matches' own kickoffs, so any one
    // match's kickoff is, by construction, always >= that minimum).
    // GradingStatus == Pending is the WHOLE idempotency mechanism (Decision
    // §3) — a Graded/Voided match is never returned here again, so
    // PredictGradingService never re-fetches or re-grades it.
    Task<IReadOnlyList<PredictMatch>> GetMatchesReadyForGradingAsync(
        TimeSpan typicalMatchDuration, DateTime nowUtc, CancellationToken cancellationToken = default);

    // REQ-1305: every stored prediction for one match, read so
    // PredictGradingService can score each one via
    // XGPredictScoringStrategy.ScorePrediction before persisting the
    // result via GradeMatchAsync below. Mirrors GetPredictionAsync's own
    // read-only, AsNoTracking shape.
    Task<IReadOnlyList<PredictMatchPrediction>> GetPredictionsForMatchAsync(
        Guid predictMatchId, CancellationToken cancellationToken = default);

    // REQ-1305/ADR-0097 §3: persists a confirmed/Finished match's grading
    // outcome atomically — the match's GradingStatus/ActualHomeGoals/
    // ActualAwayGoals AND every one of its predictions' FinalPoints
    // (keyed by PredictMatchPrediction.Id in finalPointsByPredictionId),
    // in the same unit of work (one SaveChangesAsync call), so a crash
    // mid-write cannot leave FinalPoints set on some predictions while the
    // match itself is still Pending (which would make a retry re-grade and
    // double-count), or vice versa. Load-then-save, never
    // ExecuteUpdateAsync/ExecuteDeleteAsync — same convention
    // AddOrUpdatePredictionAsync's own doc comment already follows (the
    // InMemory test provider can't translate those).
    Task GradeMatchAsync(
        Guid predictMatchId,
        int actualHomeGoals,
        int actualAwayGoals,
        IReadOnlyDictionary<Guid, int> finalPointsByPredictionId,
        CancellationToken cancellationToken = default);

    // REQ-1305/ADR-0097 §3: persists a postponed/abandoned match's voided
    // outcome — GradingStatus = Voided only. No ActualHomeGoals/
    // ActualAwayGoals write (football-data.org's own values for this outcome
    // are untrustworthy per FootballDataFixtureOutcome's own doc comment),
    // and no PredictMatchPrediction row for this match is ever touched —
    // every one keeps FinalPoints == null permanently, indistinguishable
    // from "not yet graded," which is REQ-1305's own deliberate voiding
    // behavior.
    Task VoidMatchAsync(Guid predictMatchId, CancellationToken cancellationToken = default);

    // REQ-1305/ADR-0097 §2: a PredictInstance's running total per user —
    // UserId -> SUM(FinalPoints), summed only over predictions whose
    // parent PredictMatch.GradingStatus == Graded. Pending and Voided
    // matches are excluded entirely (not a placeholder worst-case value),
    // satisfying REQ-1305's "an ungraded match contributes no components"
    // and "a round's total... grow[s]... over time" criteria directly:
    // calling this again after another match is graded returns a larger
    // sum for any user with predictions on it, with no other state to
    // update. Deliberately NOT wired into ILeaderboardService in this
    // story — see ADR-0097 Decision §2's own explicit scope note.
    Task<IReadOnlyDictionary<Guid, int>> GetTotalPointsByInstanceIdAsync(
        Guid predictInstanceId, CancellationToken cancellationToken = default);

    // ADR-0100 §3: every user who submitted >=1 prediction for this
    // instance, regardless of grading state — participation, not points.
    // Used only to decide qualifying-round membership (REQ-409);
    // PredictRoundScoreSource (Games.XGPredict) pairs this with
    // GetTotalPointsByInstanceIdAsync above (defaulting to 0 for a
    // participant with nothing graded yet) to build each qualifying
    // round's contributed value. Do NOT reuse GetTotalPointsByInstanceIdAsync's
    // absent-key semantics as a stand-in for "did this user participate" —
    // it means "has at least one graded point," not "predicted at all."
    Task<IReadOnlyCollection<Guid>> GetParticipantUserIdsByInstanceIdAsync(
        Guid predictInstanceId, CancellationToken cancellationToken = default);

    // REQ-1302/ADR-0098: every one of this user's stored predictions across
    // this instance's matches, in one query — GET /predict/current's own
    // "bulk fetch once for the whole instance" discipline (mirrors
    // XGArcade.Api.Path.PathEndpoints.MapPathEndpoints' own comment for the
    // same reasoning), rather than one GetPredictionAsync call per match.
    Task<IReadOnlyList<PredictMatchPrediction>> GetPredictionsForInstanceAndUserAsync(
        Guid predictInstanceId, Guid userId, CancellationToken cancellationToken = default);

    // REQ-1306/ADR-0098: true once this specific player has confirmed and
    // locked their predictions for this instance — independent of, and
    // checked alongside, REQ-1303's round-wide automatic lock. Never
    // becomes false again once true (no "unlock" concept — see
    // PredictPlayerLock's own doc comment).
    Task<bool> IsPlayerLockedAsync(Guid predictInstanceId, Guid userId, CancellationToken cancellationToken = default);

    // REQ-1306/ADR-0098: sets the per-player lock for this instance. Callers
    // (XGArcade.Api.Predict.PredictEndpoints' POST /predict/confirm) are
    // responsible for verifying REQ-1306's own precondition (all of this
    // instance's matches already have a stored prediction for this user)
    // before calling this — this method itself only persists the flag,
    // mirroring AddOrUpdatePredictionAsync's own "caller computes
    // correctness/timing, repository just persists" split. Idempotent:
    // calling it again for an already-locked (instance, user) pair is a
    // harmless no-op (load-then-check-then-insert, never a raw insert that
    // could violate the composite key).
    Task LockPlayerPredictionsAsync(Guid predictInstanceId, Guid userId, DateTime lockedAt, CancellationToken cancellationToken = default);
}
