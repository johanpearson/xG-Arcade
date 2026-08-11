using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore), split from IPlayerStoreRepository (S-106, pure
// refactor — see that story's own doc comment/CHANGELOG entry for why): the
// PlayerAlias concern, including the alias-driven query that returns Player
// rows (GetPlayersByNormalizedAliasAsync — it queries PlayerAlias first and
// is fundamentally alias-driven, not Player-driven). See IPlayerRepository's
// own doc comment for the shared "no facade" boundary note that applies
// identically here.
public interface IPlayerAliasRepository
{
    Task<IReadOnlyList<PlayerAlias>> GetPlayerAliasesAsync(Guid playerId, CancellationToken cancellationToken = default);
    Task AddPlayerAliasAsync(PlayerAlias alias, CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): batched counterpart to AddPlayerAliasAsync
    // — one SaveChangesAsync call for the whole list. Same caller-dedups-
    // first discipline as AddPlayerAttributesBatchAsync (IPlayerAttributeRepository).
    Task AddPlayerAliasesBatchAsync(IReadOnlyList<PlayerAlias> aliases, CancellationToken cancellationToken = default);

    // REQ-208: guess-time name matching's alias path, checked only when the
    // primary-name path (IPlayerRepository.GetPlayersByNormalizedFullNameAsync)
    // above found no candidate satisfying the cell's categories —
    // PlayerAlias.NormalizedAlias is populated at persist time
    // (WikidataLookupService.PersistMatchesAsync) with the same
    // PlayerNameNormalizer.Normalize used here, so an exact string match is
    // enough. Never PlayerNameIndex — same boundary as the method above.
    Task<IReadOnlyList<Player>> GetPlayersByNormalizedAliasAsync(
        string normalizedAlias, CancellationToken cancellationToken = default);

    // REQ-208: bulk alias fetch for the fuzzy pass's bounded candidate pool
    // (IPlayerAttributeRepository.GetPlayersWithEitherAttributeAsync) —
    // one query for every candidate's aliases rather than one
    // GetPlayerAliasesAsync call per candidate. A playerId with no aliases
    // is simply absent from the result (not present with an empty list).
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<PlayerAlias>>> GetPlayerAliasesByPlayerIdsAsync(
        IReadOnlyCollection<Guid> playerIds, CancellationToken cancellationToken = default);
}
