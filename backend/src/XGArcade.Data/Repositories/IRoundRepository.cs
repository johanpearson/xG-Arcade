using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-03 (Core.Rounds)'s own persistence — the only path Core.Rounds
// reaches Round through, same repository-per-component pattern as
// IGridInstanceRepository/IUserRepository.
public interface IRoundRepository
{
    // REQ-301's "one round ahead" check needs the most recently scheduled
    // round for a game, regardless of whether it has started yet.
    Task<Round?> GetLatestByGameKeyAsync(string gameKey, CancellationToken cancellationToken = default);

    // REQ-205: the round immediately before a given round in this game's
    // chronological chain (the one whose EndTime the given round's StartTime
    // was set from) — used by RoundGenerationService to find the round that
    // needs closing once its successor has started, since "latest" itself
    // has already moved on to that successor by then.
    Task<Round?> GetPreviousByGameKeyAsync(string gameKey, DateTime beforeStartTime, CancellationToken cancellationToken = default);

    // REQ-303: the round a player can actually see and play right now —
    // "latest" isn't good enough here, since generation runs one round ahead
    // (REQ-301) and "latest" is often that not-yet-started upcoming round.
    Task<Round?> GetActiveByGameKeyAsync(string gameKey, DateTime now, CancellationToken cancellationToken = default);

    // REQ-408: browsable past rounds, most recently closed first — never the
    // active/upcoming round (ClosedAt is only ever set by RoundCloseService).
    // `take` is deliberately the caller's own page-size-plus-one "peek"
    // (LeaderboardService's convention here, since this is a real DB-side
    // OrderBy/Skip/Take unlike the leaderboard's necessarily in-memory
    // pagination) so the caller can detect "is there another page" without a
    // second COUNT query.
    Task<IReadOnlyList<Round>> GetClosedByGameKeyAsync(string gameKey, int skip, int take, CancellationToken cancellationToken = default);

    // REQ-405: the ids of every closed round (ClosedAt != null — same
    // locked-only rule as REQ-401/404/408) for this game whose EndTime falls
    // within [windowStartUtc, windowEndUtc) — the half-open range
    // LeaderboardService uses for its calendar-aligned week/month/year
    // windows. Deliberately ids-only rather than full Round rows: callers
    // only ever feed the result straight into
    // IGuessRepository.GetTotalFinalPointsByRoundIdsAsync.
    Task<IReadOnlyList<Guid>> GetClosedIdsWithinWindowAsync(
        string gameKey, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken cancellationToken = default);

    Task<Round?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Round> AddAsync(Round round, CancellationToken cancellationToken = default);

    Task UpdateAsync(Round round, CancellationToken cancellationToken = default);
}
