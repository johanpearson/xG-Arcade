import { useEffect, useState, type FormEvent } from 'react';
import { ApiError, approvePlayerData, createPlayerOverride, describeError, removePlayerData } from '../lib/api';
import type { UnverifiedPlayerData } from '../lib/types';

interface UnverifiedDataSectionProps {
  accessToken: string;
  rows: UnverifiedPlayerData[];
  onAuthError: () => void;
  onRefresh: () => Promise<void>;
}

// REQ-501/502/503 (SCREEN-04): "Correct" (creates a PlayerOverride, requires
// a reason), "Approve"/"Approve selected" (flips confidence to "verified"
// in bulk, including select-all, no reason field), and "Remove selected"
// (hard-deletes the selected rows in bulk, also no reason field — REQ-503's
// 2026-07-20 "remove" extension, a sibling of "approve" in every respect
// except what it does to the row) are all real backend actions.
export function UnverifiedDataSection({ accessToken, rows, onAuthError, onRefresh }: UnverifiedDataSectionProps) {
  const [openRowId, setOpenRowId] = useState<string | null>(null);
  const [value, setValue] = useState('');
  const [reason, setReason] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [approving, setApproving] = useState(false);
  const [approveError, setApproveError] = useState<string | null>(null);
  const [approvalResults, setApprovalResults] = useState<RowApprovalResult[] | null>(null);

  const [removing, setRemoving] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  const [removalResults, setRemovalResults] = useState<RowRemovalResult[] | null>(null);

  // Drops any selected id that's no longer in the current row list (e.g.
  // after a refetch removes an approved row) — otherwise a stale id could
  // sit selected-but-invisible and get resubmitted on the next approve.
  useEffect(() => {
    setSelectedIds((prev) => {
      const rowIds = new Set(rows.map((row) => row.id));
      const filtered = new Set([...prev].filter((id) => rowIds.has(id)));
      return filtered.size === prev.size ? prev : filtered;
    });
  }, [rows]);

  function openCorrection(row: UnverifiedPlayerData) {
    setOpenRowId(row.id);
    setValue(row.value);
    setReason('');
    setError(null);
  }

  function closeCorrection() {
    setOpenRowId(null);
    setError(null);
  }

  async function handleSubmit(event: FormEvent, row: UnverifiedPlayerData) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await createPlayerOverride(accessToken, row.playerId, row.field, value, reason);
      setOpenRowId(null);
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      // REQ-501: a 409 (override already exists for this playerId/field)
      // surfaces here via its own detail text — there's no "edit an
      // existing override" UI to route to instead (S-012 never built a
      // browsable override list).
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  function toggleRowSelected(id: string) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  const allSelected = rows.length > 0 && selectedIds.size === rows.length;

  function toggleSelectAll() {
    setSelectedIds(allSelected ? new Set() : new Set(rows.map((row) => row.id)));
  }

  // REQ-503 (2026-07-20 extension): submits the whole selection as one bulk
  // call — a single selected row is just the N=1 case of the same action,
  // not a separate code path. Always shows a per-row outcome afterward
  // (never reads as a full success or a full failure when it's actually a
  // partial one), and always refetches the list the same way "Correct"'s
  // successful submit already does above, regardless of whether every row
  // in the selection succeeded.
  async function handleApprove() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    setApproving(true);
    setApproveError(null);
    try {
      const rowsById = new Map(rows.map((row) => [row.id, row]));
      const response = await approvePlayerData(accessToken, ids);
      const results = response.results.map((result) => {
        const row = rowsById.get(result.playerDataId);
        return {
          id: result.playerDataId,
          summary: row
            ? `${row.playerFullName} · ${row.field} · ${row.value}`
            : result.playerDataId,
          approved: result.approved,
          failureReason: result.failureReason,
        };
      });
      setApprovalResults(results);
      setSelectedIds(new Set());
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setApproveError(describeError(err));
    } finally {
      setApproving(false);
    }
  }

  // REQ-503 (2026-07-20 extension): sibling to handleApprove above in every
  // respect except which endpoint it calls and that removal has only one
  // failure reason ("NotFound" — see describeRemovalFailure). Same bulk
  // semantics: a single selected row is just the N=1 case, always shows a
  // per-row outcome, and always refetches the list afterward regardless of
  // whether every row in the selection succeeded.
  async function handleRemove() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    setRemoving(true);
    setRemoveError(null);
    try {
      const rowsById = new Map(rows.map((row) => [row.id, row]));
      const response = await removePlayerData(accessToken, ids);
      const results = response.results.map((result) => {
        const row = rowsById.get(result.playerDataId);
        return {
          id: result.playerDataId,
          summary: row
            ? `${row.playerFullName} · ${row.field} · ${row.value}`
            : result.playerDataId,
          removed: result.removed,
          failureReason: result.failureReason,
        };
      });
      setRemovalResults(results);
      setSelectedIds(new Set());
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setRemoveError(describeError(err));
    } finally {
      setRemoving(false);
    }
  }

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Unverified data ({rows.length})</h3>
      {rows.length === 0 ? (
        <p className="admin-screen__empty">No unverified data to review.</p>
      ) : (
        <>
          <div className="admin-screen__bulk-bar">
            <label className="admin-screen__checkbox">
              <input
                type="checkbox"
                checked={allSelected}
                onChange={toggleSelectAll}
                disabled={approving || removing}
              />
              <span>Select all</span>
            </label>
            <span className="admin-screen__selected-count">{selectedIds.size} selected</span>
            <button type="button" onClick={handleApprove} disabled={selectedIds.size === 0 || approving || removing}>
              {approving ? 'Approving…' : 'Approve selected'}
            </button>
            <button type="button" onClick={handleRemove} disabled={selectedIds.size === 0 || approving || removing}>
              {removing ? 'Removing…' : 'Remove selected'}
            </button>
          </div>

          {approveError && (
            <p className="admin-screen__error" role="alert">
              {approveError}
            </p>
          )}

          {removeError && (
            <p className="admin-screen__error" role="alert">
              {removeError}
            </p>
          )}

          {approvalResults && (
            <div className="admin-screen__approval-results">
              <ul className="admin-screen__list">
                {approvalResults.map((result) => (
                  <li
                    key={result.id}
                    className={
                      result.approved
                        ? 'admin-screen__approval-result'
                        : 'admin-screen__approval-result admin-screen__approval-result--failed'
                    }
                  >
                    {result.summary} — {result.approved ? 'Approved.' : describeApprovalFailure(result.failureReason)}
                  </li>
                ))}
              </ul>
              <button type="button" onClick={() => setApprovalResults(null)}>
                Dismiss
              </button>
            </div>
          )}

          {removalResults && (
            <div className="admin-screen__approval-results">
              <ul className="admin-screen__list">
                {removalResults.map((result) => (
                  <li
                    key={result.id}
                    className={
                      result.removed
                        ? 'admin-screen__approval-result'
                        : 'admin-screen__approval-result admin-screen__approval-result--failed'
                    }
                  >
                    {result.summary} — {result.removed ? 'Removed.' : describeRemovalFailure(result.failureReason)}
                  </li>
                ))}
              </ul>
              <button type="button" onClick={() => setRemovalResults(null)}>
                Dismiss
              </button>
            </div>
          )}

          <ul className="admin-screen__list">
            {rows.map((row) => (
              <li key={row.id} className="admin-screen__row">
                <label className="admin-screen__checkbox">
                  <input
                    type="checkbox"
                    checked={selectedIds.has(row.id)}
                    onChange={() => toggleRowSelected(row.id)}
                    disabled={approving || removing}
                    aria-label={`Select ${row.playerFullName} · ${row.field} · ${row.value}`}
                  />
                  <span className="admin-screen__row-summary">
                    {row.playerFullName} · {row.field} · {row.value} · {row.source}
                  </span>
                </label>
                {openRowId === row.id ? (
                  <form className="admin-screen__inline-form" onSubmit={(event) => handleSubmit(event, row)}>
                    <label className="admin-screen__field">
                      <span>Value</span>
                      <input
                        type="text"
                        required
                        value={value}
                        onChange={(event) => setValue(event.target.value)}
                        disabled={submitting}
                      />
                    </label>
                    <label className="admin-screen__field">
                      <span>Reason</span>
                      <input
                        type="text"
                        required
                        value={reason}
                        onChange={(event) => setReason(event.target.value)}
                        disabled={submitting}
                      />
                    </label>
                    {error && (
                      <p className="admin-screen__error" role="alert">
                        {error}
                      </p>
                    )}
                    <div className="admin-screen__inline-form-actions">
                      <button type="button" onClick={closeCorrection} disabled={submitting}>
                        Cancel
                      </button>
                      <button type="submit" disabled={submitting}>
                        {submitting ? 'Saving…' : 'Save correction'}
                      </button>
                    </div>
                  </form>
                ) : (
                  <button type="button" onClick={() => openCorrection(row)}>
                    Correct
                  </button>
                )}
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

interface RowApprovalResult {
  id: string;
  summary: string;
  approved: boolean;
  failureReason: string | null;
}

// REQ-503 (2026-07-20 extension): turns the backend's two known
// `failureReason` values into copy that states what happened, per
// design-document.md §5 — never a generic "failed" with no explanation, and
// never the raw enum string shown to an admin as-is.
function describeApprovalFailure(failureReason: string | null): string {
  switch (failureReason) {
    case 'NotFound':
      return 'Not approved — this row no longer exists.';
    case 'NotUnverified':
      return 'Not approved — already reviewed by someone else.';
    default:
      return 'Not approved.';
  }
}

interface RowRemovalResult {
  id: string;
  summary: string;
  removed: boolean;
  failureReason: string | null;
}

// REQ-503 (2026-07-20 extension): sibling to describeApprovalFailure above —
// removal has only one known `failureReason` ("NotFound", since removing a
// row has no "must still be unverified" precondition the way approving
// does), but keeps the same never-a-raw-enum-string, never-a-bare-"failed"
// shape for consistency and to degrade gracefully if the backend ever adds
// a new reason.
function describeRemovalFailure(failureReason: string | null): string {
  switch (failureReason) {
    case 'NotFound':
      return 'Not removed — this row no longer exists.';
    default:
      return 'Not removed.';
  }
}
