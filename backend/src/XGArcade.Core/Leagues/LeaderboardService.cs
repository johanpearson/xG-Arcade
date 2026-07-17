using XGArcade.Data.Repositories;

namespace XGArcade.Core.Leagues;

public class LeaderboardService(
    ILeagueRepository leagueRepository,
    IUserRepository userRepository,
    IGuessRepository guessRepository) : ILeaderboardService
{
    public async Task<LeaderboardPage> GetGlobalLeaderboardAsync(
        Guid requestingUserId, int cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        var globalLeague = await leagueRepository.GetOrCreateGlobalLeagueAsync(cancellationToken);
        var memberUserIds = await leagueRepository.GetMemberUserIdsAsync(globalLeague.Id, cancellationToken);
        var members = await userRepository.GetByIdsAsync(memberUserIds, cancellationToken);
        var totalsByUserId = await guessRepository.GetTotalFinalPointsByUserIdsAsync(memberUserIds, cancellationToken);

        // REQ-404/ADR-0021: sorted ascending by total score — xG Arcade is
        // scored like golf, lowest total wins. A member with no locked
        // FinalPoints yet (no rounds closed for them) is absent from
        // totalsByUserId — treated as 0, not omitted from the list (and 0
        // is the best possible score under this model, so an
        // unlocked/never-played member legitimately ranks at the top until
        // their first round locks).
        var ranked = members
            .Select(member => (member.Id, member.DisplayName, TotalPoints: totalsByUserId.GetValueOrDefault(member.Id, 0)))
            .OrderBy(m => m.TotalPoints)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select((m, index) => new LeaderboardEntry(
                index + 1,
                m.Id,
                m.DisplayName,
                m.TotalPoints,
                m.Id == requestingUserId))
            .ToList();

        // REQ-607/S-034: `cursor` is the last-seen rank (0 = nothing seen
        // yet, i.e. start from the top) — at MVP scale this is equivalent
        // to a plain offset since ranks are 1-based and contiguous, per
        // implementation-document.md §6's "simple offset is acceptable for
        // MVP scale, contract should already look cursor-shaped" note. An
        // out-of-range cursor (e.g. stale, from a since-shrunk league)
        // isn't an error — `Skip` beyond the list length is already a
        // no-op in LINQ, so it falls back to an empty final page rather
        // than a 500. Negative cursors are rejected earlier, at the API
        // boundary (LeaderboardEndpoints), before reaching this method.
        var page = ranked.Skip(cursor).Take(pageSize).ToList();
        var hasMore = cursor + page.Count < ranked.Count;
        int? nextCursor = hasMore ? cursor + page.Count : null;

        var requestingUserEntry = ranked.SingleOrDefault(entry => entry.IsRequestingUser);

        return new LeaderboardPage(page, requestingUserEntry, nextCursor, hasMore);
    }
}
