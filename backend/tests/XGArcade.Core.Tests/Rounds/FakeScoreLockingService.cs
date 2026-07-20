using XGArcade.Core.Scoring;

namespace XGArcade.Core.Tests.Rounds;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as FakeGameModule in this directory).
// Lets RoundCloseServiceTests exercise CloseRoundAsync's ordering against
// IScoreLockingService in isolation (e.g. a locking call that fails partway
// through), without depending on ScoreLockingService's own real
// cell-materialization/per-guess-loop logic.
internal class FakeScoreLockingService : IScoreLockingService
{
    public int LockRoundScoresAsyncCallCount { get; private set; }

    // Set to simulate a locking failure (e.g. a real partial-failure mid-loop
    // in ScoreLockingService) — thrown on every call for as long as it's set,
    // so a retried CloseRoundAsync can be exercised too.
    public Exception? ThrowOnLock { get; set; }

    public Task LockRoundScoresAsync(Guid roundId, CancellationToken cancellationToken = default)
    {
        LockRoundScoresAsyncCallCount++;
        if (ThrowOnLock is not null)
            throw ThrowOnLock;

        return Task.CompletedTask;
    }
}
