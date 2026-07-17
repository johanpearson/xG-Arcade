namespace XGArcade.Core.Leagues;

// COMP-02 (Core.Leagues)'s first real code (S-011) — REQ-401/404's Tier 0
// slice (the global league only; custom leagues are REQ-402-404, deferred
// per MVP-SCOPE.md). Kept as a Core service rather than inline in the API
// endpoint so aggregation logic across League/User/Guess lives in the
// component that's documented to own it, not the transport layer — same
// thin-endpoint/owning-Core-service shape GuessEndpoints ->
// GuessSubmissionService already establishes.
//
// REQ-607/S-034: paginated per implementation-document.md §6's
// cursor-shaped contract. `cursor` is the last-seen rank (0 meaning "start
// from the top"); the implementation still composes the full member list
// in memory (accepted MVP-scale tradeoff, see the doc) but the *response*
// is bounded to `pageSize` and always carries the requesting user's own
// row so SCREEN-03's sticky "your position" footer never needs a second
// round-trip.
public interface ILeaderboardService
{
    Task<LeaderboardPage> GetGlobalLeaderboardAsync(
        Guid requestingUserId,
        int cursor,
        int pageSize,
        CancellationToken cancellationToken = default);
}

// Rank is 1-based and global (not page-local) — the frontend previously
// derived rank from array index, which breaks once a page can start
// mid-list.
public record LeaderboardEntry(int Rank, Guid UserId, string DisplayName, int TotalPoints, bool IsRequestingUser);

// RequestingUserEntry is always populated when the requesting user is a
// league member (true for every authenticated caller today — signup
// auto-adds every user to the global league, AuthController.cs) even when
// their row falls outside Rows for the current page. Nullable only as a
// defensive fallback if that invariant is ever broken.
public record LeaderboardPage(
    IReadOnlyList<LeaderboardEntry> Rows,
    LeaderboardEntry? RequestingUserEntry,
    int? NextCursor,
    bool HasMore);
