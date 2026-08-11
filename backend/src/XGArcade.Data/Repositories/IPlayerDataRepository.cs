using XGArcade.Data.Entities;

namespace XGArcade.Data.Repositories;

// COMP-06 (Data.PlayerStore), split from IPlayerStoreRepository (S-106, pure
// refactor — see that story's own doc comment/CHANGELOG entry for why): the
// PlayerData raw append-log/unverified-review/approve/remove concern. See
// IPlayerRepository's own doc comment for the shared "no facade" boundary
// note that applies identically here.
public interface IPlayerDataRepository
{
    Task AddPlayerDataAsync(PlayerData data, CancellationToken cancellationToken = default);

    // Bug-bundle fix (2026-07-27): batched counterpart to AddPlayerDataAsync
    // — one SaveChangesAsync call for the whole list (docs/coding-
    // guidelines.md), not one round trip per row. No dedup here: PlayerData
    // is a raw, per-source append log (see AddPlayerDataAsync's own
    // comment) — every row is recorded unconditionally, same contract as
    // the single-item method.
    Task AddPlayerDataBatchAsync(IReadOnlyList<PlayerData> data, CancellationToken cancellationToken = default);

    // REQ-503 (S-012): the admin review view's candidate list — every
    // PlayerData row still awaiting an admin's approve/correct/remove
    // decision.
    Task<IReadOnlyList<PlayerData>> GetUnverifiedPlayerDataAsync(CancellationToken cancellationToken = default);

    // REQ-503 (2026-07-20 extension): the "approve" action — flips one or
    // more PlayerData rows' Confidence to "verified" in a single call,
    // logging each row individually via ApprovedByAdminId/ApprovedAt (same
    // "who and when, on the row itself" shape as
    // PlayerOverride.LockedByAdminId/LockedAt). Bulk includes single-row as
    // the N=1 case. Each id is evaluated independently and never fails the
    // rest of the batch — a row that no longer exists, or whose Confidence
    // is no longer "unverified" (deleted or changed by another admin
    // between selection and submission), is reported as a failed outcome
    // for that id only, per this REQ's partial-failure reporting
    // requirement. One SaveChangesAsync call for the whole batch
    // (load-then-SaveChangesAsync, coding-guidelines.md), not one
    // round-trip per row.
    Task<IReadOnlyList<PlayerDataApprovalOutcome>> ApprovePlayerDataAsync(
        IReadOnlyCollection<Guid> playerDataIds, Guid adminId, CancellationToken cancellationToken = default);

    // REQ-503 (2026-07-20 extension): the "remove" action — hard-deletes one
    // or more PlayerData rows in a single call. Unlike
    // ApprovePlayerDataAsync, there is no "must still be unverified"
    // precondition: removing a data point is a general corrective action,
    // not exclusively tied to the review queue's current state, so a row
    // already flipped to "verified" (by another admin, between selection
    // and submission) can still be removed. Bulk includes single-row as the
    // N=1 case. Each id is evaluated independently and never fails the rest
    // of the batch — a row that no longer exists (already removed by
    // another admin between selection and submission) is reported as a
    // failed outcome for that id only. One SaveChangesAsync call for the
    // whole batch (load-then-SaveChangesAsync, coding-guidelines.md).
    //
    // No ApprovedByAdminId/ApprovedAt-style audit columns for removal: once
    // a row is deleted there's nothing left in this table to attach
    // "who/when" to. Nothing else in the schema references a PlayerData
    // row by its own Id (PlayerOverride keys on (PlayerId, Field), not a
    // PlayerData row id; PlayerAttribute has no PlayerData reference at
    // all), so a hard delete is safe here without a soft-delete flag to
    // protect some other table's foreign key. The "who and when" REQ-503
    // requires ("the action is logged with admin_id and a timestamp") is
    // satisfied by a structured ILogger line at the call site
    // (AdminEndpoints.cs) instead — matching this codebase's established
    // preference (PlayerOverride/PlayerData's own audit columns) for not
    // introducing a general-purpose audit-log table.
    Task<IReadOnlyList<PlayerDataRemovalOutcome>> RemovePlayerDataAsync(
        IReadOnlyCollection<Guid> playerDataIds, CancellationToken cancellationToken = default);
}

// REQ-503 (2026-07-20 extension): per-row outcome of
// IPlayerDataRepository.ApprovePlayerDataAsync — the shape that lets a
// bulk approve report which rows succeeded and which failed rather than
// treating the whole batch as one all-or-nothing unit.
public record PlayerDataApprovalOutcome(Guid PlayerDataId, bool Approved, PlayerDataApprovalFailureReason? FailureReason);

public enum PlayerDataApprovalFailureReason
{
    // The id didn't match any PlayerData row — already deleted between
    // selection and submission (or never existed).
    NotFound,
    // The row exists but its Confidence was no longer "unverified" at
    // write time — already approved, or otherwise changed, by another
    // admin between selection and submission.
    NotUnverified,
}

// REQ-503 (2026-07-20 extension): per-row outcome of
// IPlayerDataRepository.RemovePlayerDataAsync.
public record PlayerDataRemovalOutcome(Guid PlayerDataId, bool Removed, PlayerDataRemovalFailureReason? FailureReason);

public enum PlayerDataRemovalFailureReason
{
    // The id didn't match any PlayerData row — already removed (or never
    // existed) between selection and submission.
    NotFound,
}
