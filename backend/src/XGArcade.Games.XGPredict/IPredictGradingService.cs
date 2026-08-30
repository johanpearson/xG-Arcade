namespace XGArcade.Games.XGPredict;

// REQ-1305/ADR-0097 Decision §2: lives in Games.XGPredict (COMP-15), NOT
// Core.Scoring — grading reads and writes PredictMatch/
// PredictMatchPrediction directly, both COMP-15-owned entity types, so
// putting this in XGArcade.Core would mean Core referencing game-specific
// entities (ADR-0003/CLAUDE.md's xG Arcade/game boundary rule). See that
// ADR's Decision §2/Alternatives table for the full reasoning, including
// why this does NOT go through IScoringStrategy/IScoringStrategyResolver.
public interface IPredictGradingService
{
    // REQ-1305: called by POST /internal/grade-predict-matches
    // (XGArcade.Api.Predict.InternalPredictGradingEndpoints), a new,
    // independent, hourly-scheduled job (ADR-0097 Decision §1) —
    // deliberately not triggered by, or reusing, any round-close trigger
    // point. Grades every PredictMatch whose kickoff (plus
    // PredictGradingOptions.TypicalMatchDuration) has already passed and
    // is still Pending; a match already Graded/Voided is never revisited
    // (idempotent by construction — ADR-0097 Decision §3). One match's own
    // failure (e.g. an ApiFootballClientException) never aborts the rest
    // of the run.
    Task<PredictGradingRunResult> GradeReadyMatchesAsync(CancellationToken cancellationToken = default);
}

// REQ-1305: a summary of one grading run, surfaced back through the
// endpoint's response body rather than a single pass/fail signal — mirrors
// ADR-0097 Decision §3's own "the job's overall response summarizing
// counts... rather than a single pass/fail signal" instruction.
public record PredictGradingRunResult(int Graded, int Voided, int StillPending, int Failed);
