import { useEffect, useState, type ChangeEvent } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { submitPrediction } from '../lib/predict';
import { formatMatchKickoff } from '../lib/roundTime';
import type { PredictMatch } from '../lib/types';
import './PredictMatchInput.css';

export interface PredictMatchInputProps {
  match: PredictMatch;
  accessToken: string;
  // True once either lock (REQ-1303's round-wide auto-lock or REQ-1306's
  // per-player confirm-and-lock) applies — PredictScreen.tsx computes this
  // once from `state.round.locked || state.round.confirmedLocked` and passes
  // it down; this component has no lock concept of its own beyond "am I
  // editable right now."
  disabled: boolean;
  // REQ-1302: called with the server-confirmed values after a successful
  // save, so PredictScreen.tsx can update its own copy of this match (used
  // by REQ-1306's "all 5 filled" check) without a full re-fetch.
  onSaved: (matchId: string, homeGoals: number, awayGoals: number) => void;
  // REQ-1302/1303/1306: called when a save attempt comes back 409 — the
  // submission itself already failed for this row (handled locally, below),
  // but a 409 also means this component's own `disabled` prop is about to be
  // stale (the round or this player's own predictions just became locked
  // server-side, possibly moments after this screen loaded). PredictScreen
  // re-fetches GET /predict/current in response so every row's locked
  // treatment — not just this one — reflects reality immediately, rather
  // than only this row noticing on its own next save attempt.
  onLockDetected: () => void;
}

// REQ-1302: non-negative integers only, matching the API contract exactly
// (`homeGoals`/`awayGoals` each "a non-negative integer"). Rejects a missing,
// negative, non-integer, or non-numeric field client-side before ever
// calling the API — mirrors the server's own validation so a rejected
// submission is the rare case (a stale client, a race with round lock),
// not the normal path for a normal-looking input.
function parseNonNegativeInteger(raw: string): number | null {
  if (raw.trim().length === 0) return null;
  if (!/^\d+$/.test(raw.trim())) return null;
  const value = Number(raw.trim());
  return Number.isInteger(value) && value >= 0 ? value : null;
}

// design-document.md SCREEN-14: one match's two-integer score prediction —
// team names, mono-figure kickoff time, home/away goal inputs, and an
// explicit per-match "Save" affordance (REQ-1302's own "your call" between
// submit-on-blur and an explicit affordance — an explicit button was chosen
// so a half-typed value is never accidentally submitted by an incidental
// blur, e.g. tabbing between the two goal fields of the same match).
export function PredictMatchInput({ match, accessToken, disabled, onSaved, onLockDetected }: PredictMatchInputProps) {
  const [homeText, setHomeText] = useState(match.homeGoals !== null ? String(match.homeGoals) : '');
  const [awayText, setAwayText] = useState(match.awayGoals !== null ? String(match.awayGoals) : '');
  const [status, setStatus] = useState<'idle' | 'saving' | 'saved' | 'error'>('idle');
  const [errorMessage, setErrorMessage] = useState<string | null>(null);

  // Once this match becomes locked (either lock, REQ-1303/1306), any
  // unsaved edit sitting in the fields is no longer real — re-sync to the
  // server-confirmed value so a player is never shown a stray typed value
  // that was never actually saved, which would be exactly the "ambiguity
  // about why nothing is editable" §6/this story's own instructions rule out.
  useEffect(() => {
    if (disabled) {
      setHomeText(match.homeGoals !== null ? String(match.homeGoals) : '');
      setAwayText(match.awayGoals !== null ? String(match.awayGoals) : '');
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [disabled]);

  function handleFieldChange(setter: (value: string) => void) {
    return (event: ChangeEvent<HTMLInputElement>) => {
      setter(event.target.value);
      // An edit invalidates whatever the last save attempt reported —
      // stale "Saved"/error text sitting next to a value the player has
      // since changed would misdescribe the field's current, unsaved state.
      if (status !== 'idle') setStatus('idle');
      if (errorMessage) setErrorMessage(null);
    };
  }

  async function handleSave() {
    const homeGoals = parseNonNegativeInteger(homeText);
    const awayGoals = parseNonNegativeInteger(awayText);

    if (homeGoals === null || awayGoals === null) {
      setStatus('error');
      setErrorMessage('Enter a whole number, 0 or higher, for both scores.');
      return;
    }

    setStatus('saving');
    setErrorMessage(null);
    try {
      const result = await submitPrediction(accessToken, match.matchId, homeGoals, awayGoals);
      setStatus('saved');
      onSaved(match.matchId, result.homeGoals, result.awayGoals);
    } catch (error) {
      setStatus('error');
      setErrorMessage(describeError(error));
      // A 409 here always means one of REQ-1303's round lock or REQ-1306's
      // per-player confirm-lock just applied — see this prop's own doc
      // comment on why the parent needs to know, not just this row.
      if (error instanceof ApiError && error.status === 409) onLockDetected();
    }
  }

  const kickoff = formatMatchKickoff(match.kickoffUtc);

  return (
    <div className="predict-match-input">
      <div className="predict-match-input__header">
        <span className="predict-match-input__teams">
          {match.homeTeamName} <span className="predict-match-input__vs">v</span> {match.awayTeamName}
        </span>
        <span
          className="predict-match-input__kickoff mono-figure"
          tabIndex={0}
          aria-label={kickoff.accessibleLabel}
        >
          {kickoff.text}
        </span>
      </div>

      <div className="predict-match-input__row">
        <label className="predict-match-input__field-wrap">
          <span className="predict-match-input__field-label">{match.homeTeamName}</span>
          <input
            type="number"
            inputMode="numeric"
            min={0}
            step={1}
            className="predict-match-input__field mono-figure"
            value={homeText}
            onChange={handleFieldChange(setHomeText)}
            disabled={disabled || status === 'saving'}
            aria-label={`${match.homeTeamName} predicted goals`}
          />
        </label>
        <span className="predict-match-input__separator" aria-hidden="true">
          –
        </span>
        <label className="predict-match-input__field-wrap">
          <span className="predict-match-input__field-label">{match.awayTeamName}</span>
          <input
            type="number"
            inputMode="numeric"
            min={0}
            step={1}
            className="predict-match-input__field mono-figure"
            value={awayText}
            onChange={handleFieldChange(setAwayText)}
            disabled={disabled || status === 'saving'}
            aria-label={`${match.awayTeamName} predicted goals`}
          />
        </label>
        <button
          type="button"
          className="predict-match-input__save"
          onClick={handleSave}
          disabled={disabled || status === 'saving'}
        >
          {status === 'saving' ? 'Saving…' : 'Save'}
        </button>
      </div>

      {/* §6: text-paired feedback, never color/icon-only. */}
      {status === 'saved' && <p className="predict-match-input__saved">Saved.</p>}
      {status === 'error' && errorMessage && (
        <p className="predict-match-input__error">{errorMessage}</p>
      )}
    </div>
  );
}
