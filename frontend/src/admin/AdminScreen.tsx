import { useCallback, useEffect, useState, type FormEvent } from 'react';
import {
  ApiError,
  closeAdminRound,
  createPlayerOverride,
  deleteUserByEmail,
  describeError,
  fetchActiveAdminRound,
  fetchUnverifiedPlayerData,
  updateAdminRoundEndTime,
} from '../lib/api';
import type { AdminActiveRound, UnverifiedPlayerData } from '../lib/types';
import { XG_GRID_GAME_KEY } from '../games/GameSelectScreen';
import './AdminScreen.css';

export interface AdminScreenProps {
  accessToken: string;
  onAuthError: () => void;
}

type PageState =
  | { phase: 'loading' }
  | { phase: 'access-denied' }
  | { phase: 'error'; message: string }
  | { phase: 'ready' };

// SCREEN-04, REQ-504: the admin page S-012 deliberately deferred. Reached
// only via App.tsx's admin-only nav link (REQ-504's "no visible entry
// point" half); this component provides the other half — every underlying
// endpoint 403s a non-admin token directly, and the unverified-data fetch's
// own 403 is what flips this whole page to an access-denied message,
// independent of the nav-hiding.
export function AdminScreen({ accessToken, onAuthError }: AdminScreenProps) {
  const [pageState, setPageState] = useState<PageState>({ phase: 'loading' });
  const [unverifiedRows, setUnverifiedRows] = useState<UnverifiedPlayerData[]>([]);
  // null both while the round-control/user-deletion feature is genuinely
  // absent (404 probe) and before the first load resolves — pageState.phase
  // gates the "still loading" case, so by the time pageState is 'ready',
  // null here always means "hidden", never "not fetched yet".
  const [activeRound, setActiveRound] = useState<AdminActiveRound | null>(null);

  const refreshUnverified = useCallback(async () => {
    const rows = await fetchUnverifiedPlayerData(accessToken);
    setUnverifiedRows(rows);
  }, [accessToken]);

  const refreshActiveRound = useCallback(async () => {
    const probe = await fetchActiveAdminRound(accessToken, XG_GRID_GAME_KEY);
    setActiveRound(probe);
  }, [accessToken]);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      const [unverifiedResult, activeRoundResult] = await Promise.allSettled([
        fetchUnverifiedPlayerData(accessToken),
        fetchActiveAdminRound(accessToken, XG_GRID_GAME_KEY),
      ]);
      if (cancelled) return;

      if (unverifiedResult.status === 'rejected') {
        const err = unverifiedResult.reason;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setPageState({ phase: 'access-denied' });
          return;
        }
        setPageState({ phase: 'error', message: describeError(err) });
        return;
      }
      setUnverifiedRows(unverifiedResult.value);

      if (activeRoundResult.status === 'rejected') {
        const err = activeRoundResult.reason;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setPageState({ phase: 'access-denied' });
          return;
        }
        // Non-fatal for the page as a whole — the round-control/user-deletion
        // sections just stay hidden, same as a genuine 404 probe result.
        setActiveRound(null);
      } else {
        setActiveRound(activeRoundResult.value);
      }

      setPageState({ phase: 'ready' });
    }

    load();

    return () => {
      cancelled = true;
    };
  }, [accessToken, onAuthError]);

  if (pageState.phase === 'loading') {
    return <p className="admin-screen__status">Loading…</p>;
  }

  if (pageState.phase === 'access-denied') {
    // REQ-504: the defense-in-depth half — reachable even if a non-admin
    // somehow lands on this screen directly, independent of App.tsx's
    // nav-hiding.
    return <p className="admin-screen__status">You don't have access to this page.</p>;
  }

  if (pageState.phase === 'error') {
    return <p className="admin-screen__status admin-screen__status--error">{pageState.message}</p>;
  }

  return (
    <div className="admin-screen">
      <h2 className="admin-screen__title">Admin</h2>

      <UnverifiedDataSection
        accessToken={accessToken}
        rows={unverifiedRows}
        onAuthError={onAuthError}
        onRefresh={refreshUnverified}
      />

      {activeRound !== null && (
        <>
          <RoundControlSection
            accessToken={accessToken}
            activeRound={activeRound}
            onAuthError={onAuthError}
            onRefresh={refreshActiveRound}
          />
          <UserDeletionSection accessToken={accessToken} onAuthError={onAuthError} />
        </>
      )}
    </div>
  );
}

interface UnverifiedDataSectionProps {
  accessToken: string;
  rows: UnverifiedPlayerData[];
  onAuthError: () => void;
  onRefresh: () => Promise<void>;
}

// REQ-501/502/503 (SCREEN-04): only "Correct" is a real backend action —
// design-document.md's earlier mock also showed Approve/Remove, but neither
// exists server-side (REQ-503's status note), so they're not built here.
function UnverifiedDataSection({ accessToken, rows, onAuthError, onRefresh }: UnverifiedDataSectionProps) {
  const [openRowId, setOpenRowId] = useState<string | null>(null);
  const [value, setValue] = useState('');
  const [reason, setReason] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

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

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Unverified data ({rows.length})</h3>
      {rows.length === 0 ? (
        <p className="admin-screen__empty">No unverified data to review.</p>
      ) : (
        <ul className="admin-screen__list">
          {rows.map((row) => (
            <li key={row.id} className="admin-screen__row">
              <p className="admin-screen__row-summary">
                {row.playerFullName} · {row.field} · {row.value} · {row.source}
              </p>
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
      )}
    </section>
  );
}

interface RoundControlSectionProps {
  accessToken: string;
  activeRound: AdminActiveRound;
  onAuthError: () => void;
  onRefresh: () => Promise<void>;
}

// REQ-505: rendered only when the round-control/user-deletion probe found
// the feature present (AdminScreen's `activeRound !== null` gate) — never
// disabled-but-visible in Production, since the probe itself 404s there.
function RoundControlSection({ accessToken, activeRound, onAuthError, onRefresh }: RoundControlSectionProps) {
  const [confirmingEnd, setConfirmingEnd] = useState(false);
  const [ending, setEnding] = useState(false);
  const [endError, setEndError] = useState<string | null>(null);

  const [newEndTime, setNewEndTime] = useState('');
  const [updating, setUpdating] = useState(false);
  const [updateError, setUpdateError] = useState<string | null>(null);

  async function handleEndRoundConfirmed() {
    setEnding(true);
    setEndError(null);
    try {
      await closeAdminRound(accessToken, XG_GRID_GAME_KEY);
      setConfirmingEnd(false);
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setEndError(describeError(err));
    } finally {
      setEnding(false);
    }
  }

  async function handleUpdateEndTime(event: FormEvent) {
    event.preventDefault();
    if (!newEndTime) return;
    setUpdating(true);
    setUpdateError(null);
    try {
      const endTimeIso = new Date(newEndTime).toISOString();
      await updateAdminRoundEndTime(accessToken, XG_GRID_GAME_KEY, endTimeIso);
      setNewEndTime('');
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setUpdateError(describeError(err));
    } finally {
      setUpdating(false);
    }
  }

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Round control — {XG_GRID_GAME_KEY}</h3>
      {activeRound.hasActiveRound && activeRound.round ? (
        <p className="admin-screen__row-summary">
          Round {activeRound.round.roundId} · ends {activeRound.round.endTime}
        </p>
      ) : (
        <p className="admin-screen__empty">No active round right now.</p>
      )}

      {activeRound.hasActiveRound && (
        <div className="admin-screen__action-group">
          {confirmingEnd ? (
            <div className="admin-screen__confirm-row">
              <button type="button" onClick={handleEndRoundConfirmed} disabled={ending}>
                {ending ? 'Ending…' : 'Yes, end round now'}
              </button>
              <button type="button" onClick={() => setConfirmingEnd(false)} disabled={ending}>
                Cancel
              </button>
            </div>
          ) : (
            <button type="button" onClick={() => setConfirmingEnd(true)}>
              End round now
            </button>
          )}
          {endError && (
            <p className="admin-screen__error" role="alert">
              {endError}
            </p>
          )}
        </div>
      )}

      <form className="admin-screen__inline-form" onSubmit={handleUpdateEndTime}>
        <label className="admin-screen__field">
          <span>New end time</span>
          <input
            type="datetime-local"
            required
            value={newEndTime}
            onChange={(event) => setNewEndTime(event.target.value)}
            disabled={updating}
          />
        </label>
        {updateError && (
          <p className="admin-screen__error" role="alert">
            {updateError}
          </p>
        )}
        <button type="submit" disabled={updating}>
          {updating ? 'Updating…' : 'Update end time'}
        </button>
      </form>
    </section>
  );
}

interface UserDeletionSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-506: same visibility gate as RoundControlSection above (both are
// hidden together by AdminScreen's activeRound !== null check, since they
// share the same Production environment gate server-side).
function UserDeletionSection({ accessToken, onAuthError }: UserDeletionSectionProps) {
  const [email, setEmail] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function handleDeleteConfirmed() {
    setDeleting(true);
    setError(null);
    setMessage(null);
    try {
      const result = await deleteUserByEmail(accessToken, email);
      setConfirming(false);
      if (result === 'not-found') {
        setError('No user found with that email.');
      } else {
        setEmail('');
        setMessage('Deleted.');
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setError(describeError(err));
    } finally {
      setDeleting(false);
    }
  }

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Delete a user</h3>
      <label className="admin-screen__field">
        <span>Email</span>
        <input
          type="email"
          required
          value={email}
          onChange={(event) => {
            setEmail(event.target.value);
            setMessage(null);
            setError(null);
          }}
          disabled={deleting}
        />
      </label>

      {error && (
        <p className="admin-screen__error" role="alert">
          {error}
        </p>
      )}
      {message && <p className="admin-screen__confirmation">{message}</p>}

      <div className="admin-screen__action-group">
        {confirming ? (
          <div className="admin-screen__confirm-row">
            <button type="button" onClick={handleDeleteConfirmed} disabled={deleting || !email}>
              {deleting ? 'Deleting…' : 'Yes, delete this user permanently'}
            </button>
            <button type="button" onClick={() => setConfirming(false)} disabled={deleting}>
              Cancel
            </button>
          </div>
        ) : (
          <button type="button" onClick={() => setConfirming(true)} disabled={!email}>
            Delete user
          </button>
        )}
      </div>
    </section>
  );
}
