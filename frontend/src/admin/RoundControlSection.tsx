import { useState, type FormEvent } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { closeAdminRound, startUpcomingAdminRound, updateAdminRoundEndTime } from '../lib/admin';
import type { AdminActiveRound } from '../lib/types';
import type { XG_GRID_GAME_KEY, XG_PATH_GAME_KEY, XG_PREDICT_GAME_KEY } from '../games/GameSelectScreen';

interface RoundControlSectionProps {
  accessToken: string;
  gameKey: typeof XG_GRID_GAME_KEY | typeof XG_PATH_GAME_KEY | typeof XG_PREDICT_GAME_KEY;
  // The human-readable round label prefix (REQ-304), e.g. "Grid Round"/
  // "Predict Round" — never the raw roundId GUID (see the render below).
  roundLabel: string;
  activeRound: AdminActiveRound;
  onAuthError: () => void;
  onRefresh: () => Promise<void>;
}

// REQ-505: rendered only when the round-control/user-deletion probe found
// the feature present (AdminScreen's `activeRound !== null` gate) — never
// disabled-but-visible in Production, since the probe itself 404s there.
// REQ-304/REQ-505: generalized (2026-08-31) from its original Grid-only
// shape to any game with round-control UI — `gameKey`/`roundLabel` are now
// props rather than a hardcoded `XG_GRID_GAME_KEY` import, per REQ-304's own
// "apply whenever a round-control UI element is added for another GameKey"
// note. AdminScreen renders one instance per game that needs it.
export function RoundControlSection({
  accessToken,
  gameKey,
  roundLabel,
  activeRound,
  onAuthError,
  onRefresh,
}: RoundControlSectionProps) {
  const [confirmingEnd, setConfirmingEnd] = useState(false);
  const [ending, setEnding] = useState(false);
  const [endError, setEndError] = useState<string | null>(null);

  const [newEndTime, setNewEndTime] = useState('');
  const [updating, setUpdating] = useState(false);
  const [updateError, setUpdateError] = useState<string | null>(null);

  // REQ-505 (2026-08-31 addition): for when there's no active round left to
  // close (REQ-301's already-provisioned successor may still be scheduled
  // days out) — pulls it to start right now instead.
  const [startingUpcoming, setStartingUpcoming] = useState(false);
  const [startUpcomingError, setStartUpcomingError] = useState<string | null>(null);

  async function handleEndRoundConfirmed() {
    setEnding(true);
    setEndError(null);
    try {
      await closeAdminRound(accessToken, gameKey);
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

  async function handleStartUpcoming() {
    setStartingUpcoming(true);
    setStartUpcomingError(null);
    try {
      await startUpcomingAdminRound(accessToken, gameKey);
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setStartUpcomingError(describeError(err));
    } finally {
      setStartingUpcoming(false);
    }
  }

  async function handleUpdateEndTime(event: FormEvent) {
    event.preventDefault();
    if (!newEndTime) return;
    setUpdating(true);
    setUpdateError(null);
    try {
      const endTimeIso = new Date(newEndTime).toISOString();
      await updateAdminRoundEndTime(accessToken, gameKey, endTimeIso);
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
      <h3 className="admin-screen__section-title">Round control — {gameKey}</h3>
      {activeRound.hasActiveRound && activeRound.round ? (
        <p className="admin-screen__row-summary">
          {roundLabel} #{activeRound.round.sequenceNumber} · ends {activeRound.round.endTime}
        </p>
      ) : (
        <p className="admin-screen__empty">No active round right now.</p>
      )}

      {!activeRound.hasActiveRound && (
        <div className="admin-screen__action-group">
          <button type="button" onClick={handleStartUpcoming} disabled={startingUpcoming}>
            {startingUpcoming ? 'Starting…' : 'Start upcoming round now'}
          </button>
          {startUpcomingError && (
            <p className="admin-screen__error" role="alert">
              {startUpcomingError}
            </p>
          )}
        </div>
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
