namespace XGArcade.Core.Scoring;

// COMP-04 (Core.Scoring): the single entry point for REQ-201's guess
// submission — realizes REQ-201/202/203/210 (and, via the owning
// IGameModule, REQ-207/208/209/211's name resolution).
public interface IGuessSubmissionService
{
    Task<GuessSubmissionResult> SubmitGuessAsync(
        Guid roundId, Guid userId, Guid cellId, string submittedName, CancellationToken cancellationToken = default);
}
