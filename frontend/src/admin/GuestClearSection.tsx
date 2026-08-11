import { useState } from 'react';
import { ApiError, clearGuestAccounts, describeError, fetchGuestAccountCount } from '../lib/api';
import type { ClearGuestAccountResult } from '../lib/types';

interface GuestClearSectionProps {
  accessToken: string;
  onAuthError: () => void;
  onCleared: () => Promise<void>;
}

type GuestClearPhase =
  | { phase: 'idle' }
  | { phase: 'counting' }
  | { phase: 'confirming'; count: number }
  | { phase: 'clearing'; count: number };

// REQ-508: the bulk force-clear-guests action — a stronger two-step confirm
// than RoundControlSection/UserDeletionSection's own ("Yes, end round now" /
// "Yes, delete this user permanently"), since here the confirm step must
// itself show the dry-run count so the admin confirms a known, specific
// number of accounts, not an open-ended action. Reports a per-account
// outcome afterward, same "never a single pass/fail for the whole batch"
// discipline UnverifiedDataSection's bulk approve/remove already establishes.
export function GuestClearSection({ accessToken, onAuthError, onCleared }: GuestClearSectionProps) {
  const [phase, setPhase] = useState<GuestClearPhase>({ phase: 'idle' });
  const [clearError, setClearError] = useState<string | null>(null);
  const [zeroGuestsMessage, setZeroGuestsMessage] = useState<string | null>(null);
  const [results, setResults] = useState<ClearGuestAccountResult[] | null>(null);

  async function handleForceClearClick() {
    setClearError(null);
    setZeroGuestsMessage(null);
    setPhase({ phase: 'counting' });
    try {
      const count = await fetchGuestAccountCount(accessToken);
      if (count === 0) {
        // Nothing to confirm — showing "Yes, delete all 0 guest accounts"
        // would be an odd, actionable-looking prompt for an action that
        // would do nothing.
        setZeroGuestsMessage('No guest accounts to clear right now.');
        setPhase({ phase: 'idle' });
        return;
      }
      setPhase({ phase: 'confirming', count });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setClearError(describeError(err));
      setPhase({ phase: 'idle' });
    }
  }

  function handleCancelClear() {
    setPhase({ phase: 'idle' });
    setClearError(null);
  }

  async function handleConfirmClear(count: number) {
    setPhase({ phase: 'clearing', count });
    setClearError(null);
    try {
      const response = await clearGuestAccounts(accessToken);
      setResults(response.results);
      setPhase({ phase: 'idle' });
      await onCleared();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setClearError(describeError(err));
      setPhase({ phase: 'confirming', count });
    }
  }

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Guest accounts</h3>
      <p className="admin-screen__empty">
        Deletes every current guest account immediately — a manual remedy you can use any time, separate from the
        scheduled automatic purge.
      </p>

      {clearError && (
        <p className="admin-screen__error" role="alert">
          {clearError}
        </p>
      )}

      {zeroGuestsMessage && <p className="admin-screen__empty">{zeroGuestsMessage}</p>}

      {results && (
        <div className="admin-screen__approval-results">
          <ul className="admin-screen__list">
            {results.map((result) => (
              <li
                key={result.userId}
                className={
                  result.outcome === 'Succeeded'
                    ? 'admin-screen__approval-result'
                    : 'admin-screen__approval-result admin-screen__approval-result--failed'
                }
              >
                {result.userId} — {describeGuestClearOutcome(result)}
              </li>
            ))}
          </ul>
          <button type="button" onClick={() => setResults(null)}>
            Dismiss
          </button>
        </div>
      )}

      <div className="admin-screen__action-group">
        {phase.phase === 'confirming' || phase.phase === 'clearing' ? (
          <div className="admin-screen__confirm-row">
            <button
              type="button"
              onClick={() => handleConfirmClear(phase.count)}
              disabled={phase.phase === 'clearing'}
            >
              {phase.phase === 'clearing'
                ? 'Clearing…'
                : `Yes, delete all ${phase.count} guest account${phase.count === 1 ? '' : 's'}`}
            </button>
            <button type="button" onClick={handleCancelClear} disabled={phase.phase === 'clearing'}>
              Cancel
            </button>
          </div>
        ) : (
          <button type="button" onClick={handleForceClearClick} disabled={phase.phase === 'counting'}>
            {phase.phase === 'counting' ? 'Checking…' : 'Force clear guests'}
          </button>
        )}
      </div>
    </section>
  );
}

// REQ-508: turns the backend's three known `outcome` values into copy that
// states what happened, per design-document.md §5 — never a generic
// "failed" with no explanation, and never the raw enum string shown to an
// admin as-is. Mirrors UnverifiedDataSection's describeApprovalFailure/
// describeRemovalFailure, but for a three-outcome (not two-outcome,
// success-implied-by-absence) shape.
function describeGuestClearOutcome(result: ClearGuestAccountResult): string {
  switch (result.outcome) {
    case 'Succeeded':
      return 'Cleared.';
    case 'NotFound':
      return 'Not cleared — this account no longer exists.';
    case 'Failed':
      return result.errorMessage ? `Not cleared — ${result.errorMessage}` : 'Not cleared.';
    default:
      return 'Not cleared.';
  }
}
