using XGArcade.Core.Rounds;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Rounds;

// Hand-rolled fake, not a mocking-framework double (docs/coding-guidelines.md
// "don't over-mock" — same pattern as FakeGameModule.cs in this folder).
// RoundGenerationServiceTests uses this to assert *whether/which round*
// RoundGenerationService decided to close (ADR-0022), without re-exercising
// real score-locking behavior — that's already covered end-to-end by
// RoundCloseServiceTests/RoundCloseServiceScoringTests.
internal class FakeRoundCloseService : IRoundCloseService
{
    public List<(Guid RoundId, DateTime ClosedAt)> Calls { get; } = [];

    public Task<Round?> CloseRoundAsync(Guid roundId, DateTime closedAt, CancellationToken cancellationToken = default)
    {
        Calls.Add((roundId, closedAt));
        return Task.FromResult<Round?>(null);
    }
}
