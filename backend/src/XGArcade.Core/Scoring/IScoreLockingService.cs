namespace XGArcade.Core.Scoring;

// COMP-04 (Core.Scoring) owns "score locking" per architecture-document.md
// §5/§6.2's data-flow diagram ("Round Scheduler Job -> Core.Scoring: lock
// final scores for all guesses in round") — this is that call, kept in
// Core.Scoring rather than inline in Core.Rounds' RoundCloseService, same
// thin-caller/owning-component shape GuessEndpoints -> GuessSubmissionService
// already establishes for guess acceptance.
public interface IScoreLockingService
{
    // REQ-205: locks FinalUniquenessScore/FinalPoints for every Guess in the
    // given round. Idempotent — safe to call again on an already-closed
    // round (no new guesses can land once the round stops reporting Active,
    // so the correct-guess population this depends on can't have changed).
    Task LockRoundScoresAsync(Guid roundId, CancellationToken cancellationToken = default);
}
