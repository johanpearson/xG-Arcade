namespace XGArcade.Core.Leagues;

// COMP-02 (Core.Leagues)'s first real code (S-011) — REQ-401/404's Tier 0
// slice (the global league only; custom leagues are REQ-402-404, deferred
// per MVP-SCOPE.md). Kept as a Core service rather than inline in the API
// endpoint so aggregation logic across League/User/Guess lives in the
// component that's documented to own it, not the transport layer — same
// thin-endpoint/owning-Core-service shape GuessEndpoints ->
// GuessSubmissionService already establishes.
public interface ILeaderboardService
{
    Task<IReadOnlyList<LeaderboardEntry>> GetGlobalLeaderboardAsync(Guid requestingUserId, CancellationToken cancellationToken = default);
}

public record LeaderboardEntry(Guid UserId, string DisplayName, int TotalPoints, bool IsRequestingUser);
