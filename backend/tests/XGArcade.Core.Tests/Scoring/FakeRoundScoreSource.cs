using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;

namespace XGArcade.Core.Tests.Scoring;

// Hand-rolled fake (docs/coding-guidelines.md's no-mocking-framework rule),
// same shape/role as FakeScoringStrategy in this same folder — lets
// LeaderboardServiceTests exercise ADR-0100's per-GameKey resolver-routing
// behavior for a "xg-predict"-shaped GameKey without this test project
// referencing Games.XGPredict/IPredictInstanceRepository at all (Core.Tests
// never references a game project — same boundary LeaderboardService itself
// must respect). The real participation/graded-points/guest-eligibility
// logic PredictRoundScoreSource implements is covered by its own dedicated
// tests in XGArcade.Games.XGPredict.Tests instead; this fake only proves
// LeaderboardService resolves the right IRoundScoreSource per scope and
// uses whatever it returns correctly (ranking, pagination, sort direction).
internal class FakeRoundScoreSource : IRoundScoreSource
{
    public Func<IReadOnlyCollection<Guid>, IReadOnlyCollection<Round>, IReadOnlyCollection<User>, bool, IReadOnlyDictionary<Guid, IReadOnlyList<int>>>
        GetPerRoundTotalsByUserIdsResult { get; set; } = (_, _, _, _) => new Dictionary<Guid, IReadOnlyList<int>>();

    public Func<Round, IReadOnlyDictionary<Guid, int>> GetActiveRoundTotalsByUserIdResult { get; set; } =
        _ => new Dictionary<Guid, int>();

    public Func<Round, IReadOnlyDictionary<Guid, int>> GetTotalsByRoundResult { get; set; } =
        _ => new Dictionary<Guid, int>();

    public Func<IReadOnlyCollection<Round>, IReadOnlyDictionary<Guid, int>> GetTotalsByRoundsResult { get; set; } =
        _ => new Dictionary<Guid, int>();

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> GetPerRoundTotalsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Round> closedRounds,
        IReadOnlyCollection<User> members,
        CancellationToken cancellationToken = default,
        bool applyGuestEligibilityRules = true) =>
        Task.FromResult(GetPerRoundTotalsByUserIdsResult(userIds, closedRounds, members, applyGuestEligibilityRules));

    public Task<IReadOnlyDictionary<Guid, int>> GetActiveRoundTotalsByUserIdAsync(
        Round activeRound, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetActiveRoundTotalsByUserIdResult(activeRound));

    public Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundAsync(
        Round round, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetTotalsByRoundResult(round));

    public Task<IReadOnlyDictionary<Guid, int>> GetTotalsByRoundsAsync(
        IReadOnlyCollection<Round> rounds, CancellationToken cancellationToken = default) =>
        Task.FromResult(GetTotalsByRoundsResult(rounds));
}
