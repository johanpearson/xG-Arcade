using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-04 (Core.Scoring)'s own persistence — the only path Core.Scoring
// reaches Guess through, same repository-per-component pattern as
// IRoundRepository/IGridInstanceRepository.
public interface IGuessRepository
{
    // REQ-201: a player has at most one Guess row per (round, user, cell) —
    // a resubmission overwrites it (subject to REQ-202/210), never inserts a
    // second row.
    Task<Guess?> GetAsync(Guid roundId, Guid userId, Guid cellId, CancellationToken cancellationToken = default);

    // REQ-303: every guess a player has made in one round, for rendering
    // their own progress across the whole grid in a single query rather than
    // one GetAsync call per cell.
    Task<IReadOnlyList<Guess>> GetByRoundAndUserAsync(Guid roundId, Guid userId, CancellationToken cancellationToken = default);

    // REQ-204: every *correct* guess across a set of cells, across every
    // player who solved them — this is the live uniqueness denominator's
    // population. Never includes incorrect/burned-attempt guesses (see
    // UniquenessCalculator's own doc comment on why that regressed once).
    Task<IReadOnlyList<Guess>> GetCorrectByCellIdsAsync(IReadOnlyCollection<Guid> cellIds, CancellationToken cancellationToken = default);

    // REQ-205: every guess (correct and incorrect) recorded for a round,
    // so round-close can lock FinalUniquenessScore/FinalPoints for all of
    // them in one pass.
    Task<IReadOnlyList<Guess>> GetByRoundIdAsync(Guid roundId, CancellationToken cancellationToken = default);

    // REQ-408: SUM(FinalPoints ?? 0), filtered to one round instead of
    // summed across every round a user has ever played — a closed round's
    // own permanently-locked per-participant total, never recomputed live
    // (contrast ILiveRoundContributionService, which is the active-round
    // equivalent and genuinely live). Only ever meaningful once
    // ScoreLockingService has run for this round (REQ-205) — callers must
    // check Round.ClosedAt first (LeaderboardService does).
    Task<IReadOnlyDictionary<Guid, int>> GetTotalFinalPointsByRoundIdAsync(Guid roundId, CancellationToken cancellationToken = default);

    // REQ-405: the same "sum FinalPoints, treating null as 0" formula,
    // filtered to a set of rounds (a calendar window's closed rounds)
    // instead of exactly one — GetTotalFinalPointsByRoundIdAsync above is
    // implemented in terms of this method with a one-element collection,
    // rather than keeping two independent query implementations in sync.
    Task<IReadOnlyDictionary<Guid, int>> GetTotalFinalPointsByRoundIdsAsync(IReadOnlyCollection<Guid> roundIds, CancellationToken cancellationToken = default);

    // REQ-409 (2026-07-20): for each candidate user, the list of that user's
    // SUM(FinalPoints) totals, one entry per *qualifying* round they've ever
    // played — a qualifying round is closed (Round.ClosedAt set) AND the
    // user has at least one Guess row in it, the same participant
    // definition REQ-406/407/408 and ADR-0021's
    // MaterializeUnansweredCellsAsync already use. Deliberately NOT built
    // from the old GetUserIdsWithAnyGuessAsync existence check this REQ
    // retires: that method answered "has this user ever guessed at all, in
    // any round, closed or not" (a yes/no), where this needs a genuine
    // per-round count, and must exclude active/unlocked rounds outright — a
    // still-active round contributes no entry here at all, not a zero one.
    // A user with zero qualifying rounds is simply absent from the returned
    // dictionary (never present with an empty list) — callers (currently
    // only LeaderboardService's median ranking) treat a missing key the
    // same as an empty list, i.e. "doesn't qualify."
    //
    // REQ-717/ADR-0036 (2026-07-21): a User with IsGuest = true never
    // contributes a qualifying round here, regardless of count; a claimed
    // (formerly-guest) User's rounds closed before User.ClaimedAt don't
    // either — only rounds closed after claiming do. See the
    // implementation's own doc comment for the full rationale.
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> GetPerRoundFinalPointsByUserIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default);

    Task<Guess> AddAsync(Guess guess, CancellationToken cancellationToken = default);

    // REQ-206/ADR-0021: round-close materializes one synthetic Guess row per
    // (participant, unattempted cell) pair so an unanswered cell scores the
    // same penalty as an incorrect one, rather than silently contributing 0
    // (which would now be the *best* possible score under the lowest-wins
    // model). Batched since a round-close can synthesize many rows at once.
    Task AddRangeAsync(IReadOnlyCollection<Guess> guesses, CancellationToken cancellationToken = default);

    Task UpdateAsync(Guess guess, CancellationToken cancellationToken = default);

    // REQ-710: severs every one of this user's Guess rows from them
    // (UserId = NULL) without deleting the rows themselves — other players'
    // historical uniqueness scores and leaderboard totals depend on the
    // total guess count staying intact. Implemented as a load-then-save
    // through the change tracker, not ExecuteUpdateAsync — see
    // GuessRepository's own doc comment for why (this codebase's tests run
    // against EF Core's InMemory provider, which doesn't support
    // translating that call).
    Task AnonymizeByUserIdAsync(Guid userId, CancellationToken cancellationToken = default);
}
