using Microsoft.EntityFrameworkCore;
using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

public class GuessRepository(XGArcadeDbContext dbContext) : IGuessRepository
{
    // Not AsNoTracking: GuessSubmissionService reads a Guess, mutates it, and
    // saves it back via UpdateAsync in the same request/scope on a
    // resubmission — same read-then-write flow as RoundRepository.GetByIdAsync.
    public async Task<Guess?> GetAsync(Guid roundId, Guid userId, Guid cellId, CancellationToken cancellationToken = default) =>
        await dbContext.Guesses.FirstOrDefaultAsync(
            g => g.RoundId == roundId && g.UserId == userId && g.CellId == cellId, cancellationToken);

    public async Task<IReadOnlyList<Guess>> GetByRoundAndUserAsync(Guid roundId, Guid userId, CancellationToken cancellationToken = default) =>
        await dbContext.Guesses
            .AsNoTracking()
            .Where(g => g.RoundId == roundId && g.UserId == userId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guess>> GetCorrectByCellIdsAsync(IReadOnlyCollection<Guid> cellIds, CancellationToken cancellationToken = default) =>
        await dbContext.Guesses
            .AsNoTracking()
            .Where(g => g.IsCorrect && cellIds.Contains(g.CellId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guess>> GetByRoundIdAsync(Guid roundId, CancellationToken cancellationToken = default) =>
        await dbContext.Guesses
            .Where(g => g.RoundId == roundId)
            .ToListAsync(cancellationToken);

    // REQ-408: same DB-side GroupBy/Sum shape as
    // GetTotalFinalPointsByRoundIdsAsync below, scoped to one round instead
    // of a set of them. Delegates to GetTotalFinalPointsByRoundIdsAsync
    // (REQ-405) with a one-element collection rather than keeping two
    // independent query implementations — signature/callers unchanged.
    public Task<IReadOnlyDictionary<Guid, int>> GetTotalFinalPointsByRoundIdAsync(Guid roundId, CancellationToken cancellationToken = default) =>
        GetTotalFinalPointsByRoundIdsAsync([roundId], cancellationToken);

    // REQ-405: same "sum FinalPoints, treating null as 0" formula, filtered
    // to a *set* of rounds (a calendar window's closed round ids) rather
    // than a single one. `RoundId` is the unique (RoundId, UserId, CellId)
    // index's leading column (see XGArcadeDbContext.OnModelCreating), so a
    // `RoundId IN (...)` filter here is already index-covered — no new index
    // needed for this REQ-405 query shape either.
    public async Task<IReadOnlyDictionary<Guid, int>> GetTotalFinalPointsByRoundIdsAsync(IReadOnlyCollection<Guid> roundIds, CancellationToken cancellationToken = default)
    {
        if (roundIds.Count == 0)
            return new Dictionary<Guid, int>();

        return await dbContext.Guesses
            .AsNoTracking()
            .Where(g => roundIds.Contains(g.RoundId) && g.UserId != null)
            .GroupBy(g => g.UserId!.Value)
            .Select(group => new { UserId = group.Key, Total = group.Sum(g => g.FinalPoints ?? 0) })
            .ToDictionaryAsync(x => x.UserId, x => x.Total, cancellationToken);
    }

    // REQ-409 (2026-07-20): for the leaderboard's median ranking. Guess has
    // no navigation property to Round (see Guess's own doc comment on why
    // RoundId/CellId stay plain Guids), so "closed round" is checked via an
    // explicit join rather than a navigated `.Round.ClosedAt`. GroupBy on
    // (UserId, RoundId) first to get one already-DB-summed row per
    // (user, qualifying round) pair, then a second, in-memory GroupBy on
    // UserId alone to fold those into each user's list — EF Core can't
    // project a GroupBy's element sequence into a nested
    // IReadOnlyList<int> server-side, so this second grouping has to happen
    // after materializing, but only over one row per (user, qualifying
    // round) pair rather than the raw Guess table, which stays the
    // efficient, single-query shape.
    // REQ-717/ADR-0036 (2026-07-21): two additional narrowings on top of the
    // REQ-409 shape below, both joining in Users (guess has no navigation
    // property to User either, same reason as the Round join above) —
    // (1) a guest (User.IsGuest) is excluded outright, regardless of how
    // many qualifying rounds they've accumulated; (2) a claimed
    // (guest-then-upgraded) account's rounds closed *before* the moment of
    // claiming (User.ClaimedAt) never count — only rounds closed after
    // claiming do. Both conditions are trivially true (never exclude
    // anything) for an account that was never a guest at all: IsGuest is
    // false and ClaimedAt is null from creation.
    //
    // REQ-411 (2026-08-24): applyGuestEligibilityRules defaults to true so
    // LeaderboardService.GetRankedMembersAsync's existing REQ-409/717
    // ranking call (and every pre-existing test of it) is completely
    // unaffected. GetUserStatsAsync is the one caller that passes false —
    // REQ-411's own "Out of scope" text is explicit that a guest's
    // rounds-played/best/average figures are shown the same as a claimed
    // account's, and only the *rank* figure (still computed via the
    // unchanged, guest-excluding GetRankedMembersAsync) inherits REQ-409's
    // guest-eligibility gate.
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> GetPerRoundFinalPointsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, string gameKey, CancellationToken cancellationToken = default,
        bool applyGuestEligibilityRules = true)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<int>>();

        // REQ-410/ADR-0043: round.GameKey == gameKey added on top of the
        // existing Guess-Round join — no new join, no change to the
        // median/qualifying-round logic itself.
        var perUserPerRoundTotals = await (
            from guess in dbContext.Guesses.AsNoTracking()
            join round in dbContext.Rounds.AsNoTracking() on guess.RoundId equals round.Id
            join user in dbContext.Users.AsNoTracking() on guess.UserId equals (Guid?)user.Id
            where guess.UserId != null
                && userIds.Contains(guess.UserId.Value)
                && round.ClosedAt != null
                && round.GameKey == gameKey
                && (!applyGuestEligibilityRules || (!user.IsGuest && (user.ClaimedAt == null || round.ClosedAt > user.ClaimedAt)))
            group guess by new { UserId = guess.UserId!.Value, guess.RoundId } into perRoundGroup
            select new { perRoundGroup.Key.UserId, Total = perRoundGroup.Sum(g => g.FinalPoints ?? 0) })
            .ToListAsync(cancellationToken);

        return perUserPerRoundTotals
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(x => x.Total).ToList());
    }

    public async Task<Guess> AddAsync(Guess guess, CancellationToken cancellationToken = default)
    {
        dbContext.Guesses.Add(guess);
        await dbContext.SaveChangesAsync(cancellationToken);
        return guess;
    }

    public async Task AddRangeAsync(IReadOnlyCollection<Guess> guesses, CancellationToken cancellationToken = default)
    {
        if (guesses.Count == 0)
            return;

        dbContext.Guesses.AddRange(guesses);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Guess guess, CancellationToken cancellationToken = default)
    {
        dbContext.Guesses.Update(guess);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Load-then-save rather than ExecuteUpdateAsync: this codebase's tests
    // run against EF Core's InMemory provider (docs/coding-guidelines.md),
    // which doesn't support translating bulk ExecuteUpdate/ExecuteDelete
    // calls — same reason every other write in this repository already
    // goes through the change tracker instead.
    public async Task AnonymizeByUserIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var guesses = await dbContext.Guesses.Where(g => g.UserId == userId).ToListAsync(cancellationToken);
        foreach (var guess in guesses)
        {
            guess.UserId = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
