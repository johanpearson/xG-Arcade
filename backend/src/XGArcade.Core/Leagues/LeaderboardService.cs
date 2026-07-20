using XGArcade.Core.Scoring;
using XGArcade.Data.Entities;
using XGArcade.Data.Repositories;

namespace XGArcade.Core.Leagues;

public class LeaderboardService(
    ILeagueRepository leagueRepository,
    IUserRepository userRepository,
    IGuessRepository guessRepository,
    IRoundRepository roundRepository,
    ILiveRoundContributionService liveRoundContributionService) : ILeaderboardService
{
    public async Task<LeaderboardPage> GetGlobalLeaderboardAsync(
        Guid requestingUserId, int cursor, int pageSize, Round? activeRound, CancellationToken cancellationToken = default)
    {
        var globalLeague = await leagueRepository.GetOrCreateGlobalLeagueAsync(cancellationToken);
        var memberUserIds = await leagueRepository.GetMemberUserIdsAsync(globalLeague.Id, cancellationToken);
        var members = await userRepository.GetByIdsAsync(memberUserIds, cancellationToken);
        var totalsByUserId = await guessRepository.GetTotalFinalPointsByUserIdsAsync(memberUserIds, cancellationToken);

        // REQ-406/ADR-0031: folded on top of the locked total, recomputed
        // fresh on every read — never cached or snapshotted anywhere in this
        // path. A member with zero guesses in the active round (or no active
        // round at all) is unaffected: GetValueOrDefault falls back to 0,
        // identical to today's pre-REQ-406 behavior.
        var liveContributionsByUserId = activeRound is null
            ? new Dictionary<Guid, int>()
            : await liveRoundContributionService.GetContributionsByUserIdAsync(activeRound, cancellationToken);

        // REQ-404/ADR-0021: sorted ascending by total score — xG Arcade is
        // scored like golf, lowest total wins. A member with no locked
        // FinalPoints yet (no rounds closed for them) is absent from
        // totalsByUserId — treated as 0, not omitted from the list (and 0
        // is the best possible score under this model, so an
        // unlocked/never-played member legitimately ranks at the top until
        // their first round locks).
        var ranked = members
            .Select(member => (
                member.Id,
                member.DisplayName,
                TotalPoints: totalsByUserId.GetValueOrDefault(member.Id, 0) + liveContributionsByUserId.GetValueOrDefault(member.Id, 0)))
            .OrderBy(m => m.TotalPoints)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select((m, index) => new LeaderboardEntry(
                index + 1,
                m.Id,
                m.DisplayName,
                m.TotalPoints,
                m.Id == requestingUserId))
            .ToList();

        return Paginate(ranked, cursor, pageSize);
    }

    // REQ-407: participant-only, live, active-round-scoped leaderboard — the
    // same per-cell contribution ILiveRoundContributionService computes for
    // REQ-406 above, exposed as its own standalone scope instead of folded
    // onto a locked total. A non-participant is never a key in
    // liveContributionsByUserId (see ILiveRoundContributionService's own doc
    // comment), so they simply never appear here.
    public async Task<LeaderboardPage> GetActiveRoundLeaderboardAsync(
        Guid requestingUserId, Round activeRound, int cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        var liveContributionsByUserId = await liveRoundContributionService.GetContributionsByUserIdAsync(activeRound, cancellationToken);
        if (liveContributionsByUserId.Count == 0)
            return new LeaderboardPage([], null, null, false);

        var participants = await userRepository.GetByIdsAsync(liveContributionsByUserId.Keys.ToList(), cancellationToken);

        var ranked = participants
            .Select(participant => (participant.Id, participant.DisplayName, TotalPoints: liveContributionsByUserId[participant.Id]))
            .OrderBy(p => p.TotalPoints)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select((p, index) => new LeaderboardEntry(
                index + 1,
                p.Id,
                p.DisplayName,
                p.TotalPoints,
                p.Id == requestingUserId))
            .ToList();

        return Paginate(ranked, cursor, pageSize);
    }

    // REQ-408: DB-side ordered/paged (a plain persisted-column sort, unlike
    // the leaderboard scopes above which necessarily compute in memory) —
    // fetches pageSize+1 rows to detect HasMore without a second COUNT query.
    public async Task<ClosedRoundListPage> GetClosedRoundsAsync(
        string gameKey, int cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        var rounds = await roundRepository.GetClosedByGameKeyAsync(gameKey, cursor, pageSize + 1, cancellationToken);
        var hasMore = rounds.Count > pageSize;
        var page = (hasMore ? rounds.Take(pageSize) : rounds)
            .Select(r => new ClosedRoundSummary(r.Id, r.StartTime, r.EndTime, r.ClosedAt!.Value))
            .ToList();
        int? nextCursor = hasMore ? cursor + pageSize : null;

        return new ClosedRoundListPage(page, nextCursor, hasMore);
    }

    // REQ-408: SUM(final_points) for one round only — REQ-206's own locked
    // formula, unchanged, just filtered to a single round instead of a
    // user's whole history. Never recomputed live (contrast
    // GetActiveRoundLeaderboardAsync above): once a round is closed,
    // FinalPoints never changes again.
    public async Task<ClosedRoundLeaderboardResult> GetClosedRoundLeaderboardAsync(
        Guid roundId, Guid requestingUserId, int cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        var round = await roundRepository.GetByIdAsync(roundId, cancellationToken);
        if (round is null)
            return new ClosedRoundLeaderboardResult(ClosedRoundLeaderboardStatus.RoundNotFound, null);

        if (round.ClosedAt is null)
            return new ClosedRoundLeaderboardResult(ClosedRoundLeaderboardStatus.RoundNotClosedYet, null);

        var totalsByUserId = await guessRepository.GetTotalFinalPointsByRoundIdAsync(roundId, cancellationToken);
        if (totalsByUserId.Count == 0)
            return new ClosedRoundLeaderboardResult(ClosedRoundLeaderboardStatus.Found, new LeaderboardPage([], null, null, false));

        var participants = await userRepository.GetByIdsAsync(totalsByUserId.Keys.ToList(), cancellationToken);

        var ranked = participants
            .Select(participant => (participant.Id, participant.DisplayName, TotalPoints: totalsByUserId.GetValueOrDefault(participant.Id, 0)))
            .OrderBy(p => p.TotalPoints)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select((p, index) => new LeaderboardEntry(
                index + 1,
                p.Id,
                p.DisplayName,
                p.TotalPoints,
                p.Id == requestingUserId))
            .ToList();

        return new ClosedRoundLeaderboardResult(ClosedRoundLeaderboardStatus.Found, Paginate(ranked, cursor, pageSize));
    }

    // REQ-607/S-034: `cursor` is the last-seen rank (0 = nothing seen yet,
    // i.e. start from the top) — at MVP scale this is equivalent to a plain
    // offset since ranks are 1-based and contiguous, per
    // implementation-document.md §6's "simple offset is acceptable for MVP
    // scale, contract should already look cursor-shaped" note. An
    // out-of-range cursor (e.g. stale, from a since-shrunk league) isn't an
    // error — `Skip` beyond the list length is already a no-op in LINQ, so
    // it falls back to an empty final page rather than a 500. Negative
    // cursors are rejected earlier, at the API boundary (LeaderboardEndpoints),
    // before reaching this method. Shared by every ranked scope this service
    // exposes (REQ-401/404/406/407/408).
    private static LeaderboardPage Paginate(IReadOnlyList<LeaderboardEntry> ranked, int cursor, int pageSize)
    {
        var page = ranked.Skip(cursor).Take(pageSize).ToList();
        var hasMore = cursor + page.Count < ranked.Count;
        int? nextCursor = hasMore ? cursor + page.Count : null;

        var requestingUserEntry = ranked.SingleOrDefault(entry => entry.IsRequestingUser);

        return new LeaderboardPage(page, requestingUserEntry, nextCursor, hasMore);
    }
}
