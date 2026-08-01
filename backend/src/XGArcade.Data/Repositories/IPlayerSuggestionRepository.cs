using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// REQ-215/ADR-0052 (S-089): PlayerSuggestion's own repository, deliberately
// separate from IPlayerStoreRepository (COMP-06) — that interface's own doc
// comment scopes it to "the only path to PlayerData/PlayerOverride/
// PlayerAttribute/PlayerAlias," and ADR-0052 keeps PlayerSuggestion its own
// table/pipeline, never folded into REQ-503's queue or its repository.
//
// This story (S-089) only ever calls AddAsync — the list/commit/reject
// methods REQ-509/S-090's admin review needs land with that story, not this
// one.
public interface IPlayerSuggestionRepository
{
    Task<PlayerSuggestion> AddAsync(PlayerSuggestion suggestion, CancellationToken cancellationToken = default);
}
