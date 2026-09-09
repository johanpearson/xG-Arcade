using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// Games.XGHigherLower's (COMP-18) own persistence — the only path
// Games.XGHigherLower reaches HigherLowerInstance/HigherLowerComparator
// through, same repository-per-component pattern as IPredictInstanceRepository
// (COMP-15)/IPathInstanceRepository (COMP-11)/IGridInstanceRepository
// (COMP-05). ADR-0110.
//
// S-224 added GenerateInstanceAsync's write path and GetCellIdsAsync's read
// path (AddInstanceAsync/GetInstanceByIdAsync below). S-225 (REQ-1504) adds
// this story's own per-participant attempt read/write/anonymize methods —
// see HigherLowerAttempt's own doc comment for the entity shape these
// methods read and write.
public interface IHigherLowerInstanceRepository
{
    // Persists instance + comparators together, mirroring
    // IPredictInstanceRepository.AddInstanceAsync's exact shape.
    Task<HigherLowerInstance> AddInstanceAsync(HigherLowerInstance instance, CancellationToken cancellationToken = default);

    Task<HigherLowerInstance?> GetInstanceByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-1504: the currently-stored attempt (if any) for one instance/user
    // pair — null means no guess has ever been submitted yet, which
    // XGHigherLowerGameModule.ScoreSubmissionAsync treats as an implicit
    // "streak 0, baseline = the instance's own fixed starting baseline"
    // state (HigherLowerAttempt's own doc comment). AsNoTracking, mirroring
    // IPredictInstanceRepository.GetPredictionAsync's own read-only shape —
    // the caller always persists any resulting change via SaveAttemptAsync
    // below, never by mutating a tracked entity returned from here.
    Task<HigherLowerAttempt?> GetAttemptAsync(Guid higherLowerInstanceId, Guid? userId, CancellationToken cancellationToken = default);

    // REQ-1504: store or replace (never insert a second row for) this
    // instance/user pair's attempt state — load-then-save, never
    // ExecuteUpdateAsync/ExecuteDeleteAsync (docs/coding-guidelines.md — the
    // InMemory test provider can't translate those). Mirrors
    // IPredictInstanceRepository.AddOrUpdatePredictionAsync's exact "caller
    // computes every field's new value, repository just persists it"
    // convention — the caller (XGHigherLowerGameModule.ScoreSubmissionAsync)
    // is responsible for REQ-1504's correctness/streak-increment/terminal
    // logic; this method has no opinion on any of it. Also creates the row
    // on a participant's first-ever guess (no separate "start attempt" call
    // exists — see HigherLowerAttempt's own doc comment).
    Task SaveAttemptAsync(
        Guid higherLowerInstanceId,
        Guid? userId,
        int streakLength,
        Guid currentBaselinePlayerId,
        int currentBaselineValue,
        bool hasEnded,
        CancellationToken cancellationToken = default);

    // ADR-0100 §3/REQ-1505/S-226: every user who has ever submitted >=1
    // guess for this instance (a HigherLowerAttempt row exists for them),
    // regardless of whether that attempt has ended — participation, not
    // points. Used only to decide qualifying-round membership (REQ-409),
    // same role as IPredictInstanceRepository.
    // GetParticipantUserIdsByInstanceIdAsync — HigherLowerRoundScoreSource
    // (Games.XGHigherLower) pairs this with GetStreakLengthsByInstanceIdAsync
    // below to build each qualifying round's contributed value. The
    // UserId != null filter excludes any attempt already anonymized by
    // REQ-710 (AnonymizeAttemptsByUserIdAsync below) — an anonymized
    // attempt has no user left to attribute participation to.
    Task<IReadOnlyCollection<Guid>> GetParticipantUserIdsByInstanceIdAsync(
        Guid higherLowerInstanceId, CancellationToken cancellationToken = default);

    // ADR-0100 §4/REQ-1505/S-226: UserId -> StreakLength for every attempt
    // row with a non-null UserId. Unlike
    // IPredictInstanceRepository.GetTotalPointsByInstanceIdAsync (which
    // excludes ungraded matches — PredictMatchPrediction.FinalPoints is
    // null until a grading job runs), there is no "not yet graded" gap to
    // model here: HigherLowerAttempt.StreakLength is ALWAYS a real, current
    // value the instant a row exists at all (REQ-1504 — it doubles as
    // "current progress," ended or in-progress alike; see that entity's own
    // doc comment). So every attempt row unconditionally contributes its
    // StreakLength, with no Graded/Pending distinction to filter on.
    Task<IReadOnlyDictionary<Guid, int>> GetStreakLengthsByInstanceIdAsync(
        Guid higherLowerInstanceId, CancellationToken cancellationToken = default);

    // REQ-710: severs every one of this user's HigherLowerAttempt rows from
    // them (UserId = NULL) without deleting the rows themselves — same
    // reasoning as Guess/PredictMatchPrediction (REQ-710): nothing else in
    // this game currently depends on an attempt row's UserId surviving for
    // scoring purposes, but anonymizing rather than hard-deleting keeps this
    // table's REQ-710 treatment consistent with every other nullable-UserId
    // per-user table in the codebase, in case a later leaderboard/history
    // feature reads this table the way GetTotalPointsByInstanceIdAsync reads
    // PredictMatchPrediction. Load-then-save through the change tracker, not
    // ExecuteUpdateAsync — mirrors
    // IPredictInstanceRepository.AnonymizePredictionsByUserIdAsync exactly.
    Task AnonymizeAttemptsByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
}
