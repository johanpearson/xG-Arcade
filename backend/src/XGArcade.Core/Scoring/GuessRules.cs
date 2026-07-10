namespace XGArcade.Core.Scoring;

// REQ-210's attempt cap, shared between GuessSubmissionService (which
// enforces it while accepting a submission) and any read path (REQ-303) that
// needs to know whether an already-recorded Guess is locked, without
// resubmitting one.
public static class GuessRules
{
    public const int MaxAttemptsPerCell = 2;
}
