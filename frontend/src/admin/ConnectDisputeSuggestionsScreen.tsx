import { useEffect, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { fetchConnectDisputeSuggestions } from '../lib/admin';
import type { ConnectDisputeDataCorrectionSuggestion } from '../lib/types';
import './SuggestionsScreen.css';

export interface ConnectDisputeSuggestionsScreenProps {
  accessToken: string;
  onAuthError: () => void;
  onBackToAdmin: () => void;
}

type PageState =
  | { phase: 'loading' }
  | { phase: 'access-denied' }
  | { phase: 'error'; message: string }
  | { phase: 'ready' };

// REQ-1414: a new, standalone, deliberately simple admin screen — never
// folded into SuggestionsScreen.tsx's REQ-509/510 queue above (that ADR-0053
// precedent applies here too: this is a club-overlap fact discovered via an
// xG Connect match, not a cell-guess candidate). Follows SuggestionsScreen's
// exact PageState/loading/401/403 structural shape (mirroring how it in
// turn follows AdminScreen's own shape) — but NOT its content: this REQ is
// explicitly read-only, with zero review/commit/approve/reject workflow, so
// there is no per-row expandable panel, no lookup, no commit form, nothing
// clickable at all. Fetches the list once on mount; no polling/refresh
// action either, since REQ-1414 itself never specifies staleness tolerance
// and a suggestion existing/being ignored has no effect on anything else
// (its own "purely additive" acceptance criterion).
export function ConnectDisputeSuggestionsScreen({
  accessToken,
  onAuthError,
  onBackToAdmin,
}: ConnectDisputeSuggestionsScreenProps) {
  const [pageState, setPageState] = useState<PageState>({ phase: 'loading' });
  const [suggestions, setSuggestions] = useState<ConnectDisputeDataCorrectionSuggestion[]>([]);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      try {
        const rows = await fetchConnectDisputeSuggestions(accessToken);
        if (cancelled) return;
        setSuggestions(rows);
        setPageState({ phase: 'ready' });
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setPageState({ phase: 'access-denied' });
          return;
        }
        setPageState({ phase: 'error', message: describeError(err) });
      }
    }

    load();

    return () => {
      cancelled = true;
    };
  }, [accessToken, onAuthError]);

  if (pageState.phase === 'loading') {
    return <p className="suggestions-screen__status">Loading…</p>;
  }

  if (pageState.phase === 'access-denied') {
    return <p className="suggestions-screen__status">You don't have access to this page.</p>;
  }

  if (pageState.phase === 'error') {
    return <p className="suggestions-screen__status suggestions-screen__status--error">{pageState.message}</p>;
  }

  return (
    <div className="suggestions-screen">
      <div className="suggestions-screen__header">
        <h2 className="suggestions-screen__title">Connect dispute suggestions</h2>
        <button type="button" onClick={onBackToAdmin}>
          Back to admin
        </button>
      </div>

      <section className="suggestions-screen__section">
        <h3 className="suggestions-screen__section-title">
          Approved dispute suggestions ({suggestions.length})
        </h3>
        <p className="suggestions-screen__hint">
          Read-only — an approved xG Connect dispute (REQ-1412/1413) never changes a match's own recorded outcome;
          these are only a starting point for a possible future correction to the underlying player data.
        </p>

        {suggestions.length === 0 ? (
          <p className="suggestions-screen__empty">No dispute suggestions recorded yet.</p>
        ) : (
          <table className="suggestions-screen__table">
            <thead>
              <tr>
                <th scope="col">Candidate</th>
                <th scope="col">Preceding player</th>
                <th scope="col">Claimed club</th>
                <th scope="col">Match</th>
                <th scope="col">Recorded</th>
              </tr>
            </thead>
            <tbody>
              {suggestions.map((suggestion) => (
                <tr key={suggestion.id}>
                  <td>{suggestion.candidatePlayerName}</td>
                  <td>{suggestion.precedingPlayerName}</td>
                  <td>{suggestion.claimedClubName}</td>
                  <td className="mono-figure">{suggestion.connectMatchId}</td>
                  <td className="mono-figure">{suggestion.createdAt}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  );
}
