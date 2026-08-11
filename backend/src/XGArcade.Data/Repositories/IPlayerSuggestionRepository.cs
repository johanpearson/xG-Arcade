using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// REQ-215/ADR-0052 (S-089): PlayerSuggestion's own repository, deliberately
// separate from every COMP-06 repository — those interfaces are scoped to
// "the only path to PlayerData/PlayerOverride/PlayerAttribute/PlayerAlias/
// PlayerCareerStint/etc.," and ADR-0052 keeps PlayerSuggestion its own
// table/pipeline, never folded into REQ-503's queue or its repository.
//
// S-089 only ever called AddAsync — the list/commit/reject methods below
// land with REQ-509/S-090's admin review.
public interface IPlayerSuggestionRepository
{
    Task<PlayerSuggestion> AddAsync(PlayerSuggestion suggestion, CancellationToken cancellationToken = default);

    // REQ-509 (S-090): the admin review view's own candidate list — every
    // still-pending suggestion, with AssertedClubs eagerly loaded (the list
    // view needs to display them, same reasoning REQ-503's unverified queue
    // doesn't need a second query per row). Oldest-first, matching REQ-503's
    // implicit "work through the backlog in submission order" precedent —
    // no existing REQ text mandates an order, but a stable one avoids the
    // list silently reordering between an admin's page loads.
    Task<IReadOnlyList<PlayerSuggestion>> GetPendingAsync(CancellationToken cancellationToken = default);

    // REQ-509: resolves one specific suggestion for the live-lookup/commit/
    // reject actions below — AssertedClubs eagerly loaded (the review view
    // needs the claim alongside the fresh fetch to compare against it).
    // AsNoTracking, same as GetPendingAsync — no caller of this method
    // mutates the returned entity directly; ResolveAsync below owns the one
    // write this table supports.
    Task<PlayerSuggestion?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    // REQ-509: the one write this repository supports post-submission —
    // moves a suggestion from Pending to Committed or Rejected and stamps
    // ResolvedByAdminId/ResolvedAt, in one call, for both outcomes (see
    // PlayerSuggestion.ResolvedByAdminId's own doc comment for why one
    // mechanism covers both). Returns false, and writes nothing, when the
    // suggestion doesn't exist OR is no longer Pending (already resolved by
    // another admin between the review view loading and this submission,
    // or a stale double-submission of the same action) — "never pending
    // after either action" is a one-way transition, never re-enterable.
    // Load-then-SaveChangesAsync (docs/coding-guidelines.md), never
    // ExecuteUpdateAsync.
    Task<bool> ResolveAsync(
        Guid id, PlayerSuggestionStatus status, Guid adminId, DateTime resolvedAt, CancellationToken cancellationToken = default);
}
