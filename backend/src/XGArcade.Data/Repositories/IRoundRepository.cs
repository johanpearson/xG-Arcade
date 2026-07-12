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

    Task<Round?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Round> AddAsync(Round round, CancellationToken cancellationToken = default);

    Task UpdateAsync(Round round, CancellationToken cancellationToken = default);
}
