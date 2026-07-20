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
    // REQ-409 (2026-07-20): the product owner's decided qualification floor
    // — a player needs at least this many qualifying rounds (closed round +
    // >=1 Guess in it) before the median comparison is considered meaningful
    // enough to rank them at all.
    private const int MinimumQualifyingRoundsForRanking = 5;

    public async Task<LeaderboardPage> GetGlobalLeaderboardAsync(
        Guid requestingUserId, int cursor, int pageSize, CancellationToken cancellationToken = default)
    {
        var globalLeague = await leagueRepository.GetOrCreateGlobalLeagueAsync(cancellationToken);
        var memberUserIds = await leagueRepository.GetMemberUserIdsAsync(globalLeague.Id, cancellationToken);
        var members = await userRepository.GetByIdsAsync(memberUserIds, cancellationToken);
        var perRoundTotalsByUserId = await guessRepository.GetPerRoundFinalPointsByUserIdsAsync(memberUserIds, cancellationToken);

        // REQ-409: replaces REQ-401/404's old SUM(FinalPoints ?? 0) ranking
        // outright — see ILeaderboardService's own doc comment for the full
        // "why" (a pure sum only ever grows the more rounds someone plays,
        // measuring volume as much as skill). A member with fewer than
        // MinimumQualifyingRoundsForRanking qualifying rounds — including a
        // member with zero, which subsumes the old
        // GetUserIdsWithAnyGuessAsync "ever played at all" exclusion this
        // REQ retires — is filtered out entirely before ranking, the same
        // "absent, not defaulted" shape that older exclusion already used.
        // No live component: unlike the old ranking (REQ-406), this method
        // no longer folds in a contribution from the currently active round
        // — see ILeaderboardService's doc comment for why folding a live
        // round into a median has no resolved meaning.
        var ranked = members
            .Select(member => (
                member.Id,
                member.DisplayName,
                PerRoundTotals: perRoundTotalsByUserId.GetValueOrDefault(member.Id, Array.Empty<int>())))
            .Where(m => m.PerRoundTotals.Count >= MinimumQualifyingRoundsForRanking)
            .Select(m => (m.Id, m.DisplayName, Median: ComputeMedian(m.PerRoundTotals)))
            // ADR-0021: ascending — lowest median wins, same direction as
            // every other ranking in this file. Sorting/tie-breaking happens
            // on the unrounded double median, so ComputeMedian's rounding
            // for LeaderboardEntry.TotalPoints (below) never affects order.
            .OrderBy(m => m.Median)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select((m, index) => new LeaderboardEntry(
                index + 1,
                m.Id,
                m.DisplayName,
                (int)Math.Round(m.Median, MidpointRounding.AwayFromZero),
                m.Id == requestingUserId))
            .ToList();

        return Paginate(ranked, cursor, pageSize);
    }

    // REQ-409: the standard median — the middle value once a player's
    // qualifying-round totals are sorted ascending, or the arithmetic mean
    // of the two middle values when the count is even. Computed over EVERY
    // qualifying round the player has ever played (the 5-round minimum
    // above is a qualification floor, not a rolling window over their most
    // recent 5). Returns a double rather than an int: an even-count average
    // (e.g. 2.5) is not always a whole number, and this exact value is what
    // GetGlobalLeaderboardAsync sorts/tie-breaks on — only rounded to an int
    // afterward, for LeaderboardEntry.TotalPoints's display value.
    private static double ComputeMedian(IReadOnlyList<int> perRoundTotals)
    {
        var sorted = perRoundTotals.OrderBy(total => total).ToList();
        var middle = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
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

    // REQ-405: round/week/month/year resolutions. Round reuses the existing
    // single-round path (GetClosedByGameKeyAsync + GetTotalFinalPointsByRoundIdAsync)
    // exactly as REQ-408's browsing feature already does, just always
    // resolved to the single most-recently-closed round rather than a
    // caller-chosen one. Week/Month/Year share one path: resolve the
    // calendar-aligned UTC window, find every closed round whose EndTime
    // falls in it, and sum FinalPoints across all of them.
    public async Task<LeaderboardPage> GetWindowedLeaderboardAsync(
        Guid requestingUserId,
        string gameKey,
        LeaderboardWindowResolution resolution,
        DateTime nowUtc,
        int cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<Guid, int> totalsByUserId;

        if (resolution == LeaderboardWindowResolution.Round)
        {
            // "round" means the single most recently *closed* round for the
            // game (REQ-405) — never an arbitrary one, unlike REQ-408's
            // caller-chosen roundId.
            var mostRecentlyClosedRounds = await roundRepository.GetClosedByGameKeyAsync(gameKey, 0, 1, cancellationToken);
            var mostRecentlyClosedRound = mostRecentlyClosedRounds.FirstOrDefault();
            if (mostRecentlyClosedRound is null)
                return new LeaderboardPage([], null, null, false);

            totalsByUserId = await guessRepository.GetTotalFinalPointsByRoundIdAsync(mostRecentlyClosedRound.Id, cancellationToken);
        }
        else
        {
            var (windowStartUtc, windowEndUtc) = GetCalendarWindow(resolution, nowUtc);
            var roundIds = await roundRepository.GetClosedIdsWithinWindowAsync(gameKey, windowStartUtc, windowEndUtc, cancellationToken);
            totalsByUserId = await guessRepository.GetTotalFinalPointsByRoundIdsAsync(roundIds, cancellationToken);
        }

        if (totalsByUserId.Count == 0)
            return new LeaderboardPage([], null, null, false);

        var participants = await userRepository.GetByIdsAsync(totalsByUserId.Keys.ToList(), cancellationToken);

        // Same "absent from the totals dictionary means not ranked at all"
        // pattern as every other scope in this file (REQ-401/404/406/407/408)
        // — a member with zero guesses in this window simply never appears
        // here, rather than being defaulted to a TotalPoints of 0 (which
        // ADR-0021's lowest-wins model would otherwise treat as the best
        // possible score).
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

        return Paginate(ranked, cursor, pageSize);
    }

    // REQ-405: calendar-aligned, half-open [start, end) UTC window for
    // Week/Month/Year — never a rolling last-N-days window. Round has no
    // window (handled entirely by the caller above), so it's not a valid
    // input here.
    private static (DateTime WindowStartUtc, DateTime WindowEndUtc) GetCalendarWindow(LeaderboardWindowResolution resolution, DateTime nowUtc)
    {
        switch (resolution)
        {
            case LeaderboardWindowResolution.Week:
            {
                // ISO week: Monday 00:00:00 through the following Monday
                // (exclusive). DayOfWeek is Sunday=0..Saturday=6, so Sunday
                // needs its own case (6 days back to the preceding Monday)
                // rather than falling out of the general "current - (day-1)"
                // formula the other days share.
                var today = nowUtc.Date;
                var daysSinceMonday = today.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)today.DayOfWeek - 1;
                var mondayThisWeek = today.AddDays(-daysSinceMonday);
                return (mondayThisWeek, mondayThisWeek.AddDays(7));
            }
            case LeaderboardWindowResolution.Month:
            {
                var firstOfMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                return (firstOfMonth, firstOfMonth.AddMonths(1));
            }
            case LeaderboardWindowResolution.Year:
            {
                var firstOfYear = new DateTime(nowUtc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                return (firstOfYear, firstOfYear.AddYears(1));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, "GetCalendarWindow only handles Week/Month/Year.");
        }
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
