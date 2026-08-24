import { useState } from 'react';
import { ApiError } from '../lib/apiClient';
import { refreshPlayerFromWikidata } from '../lib/admin';
import type { RefreshPlayerFromWikidataResponse } from '../lib/types';
import { PlayerRefreshFieldsList, describePlayerRefreshError } from './PlayerRefreshFieldsList';

interface PlayerRefreshSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-513/514 (SCREEN-04): a pure UI layer over REQ-513's existing
// POST /admin/players/{id}/refresh-from-wikidata endpoint — re-fetches a
// player's FullName/Position/BirthYear/PhotoUrl from their already-stored
// WikidataQid, never a free-text edit and never a player-search/browse UI
// (both explicit scope cuts, matching REQ-513's own). Unlike
// RoundControlSection/UserDeletionSection, this section is rendered
// unconditionally (not gated by the Non-Production-only `activeRound`
// probe) — REQ-513's endpoint is registered and reachable in every
// environment including Production. Unlike UserDeletionSection, this action
// is non-destructive (it can only apply already-trusted Wikidata data), so
// there is no two-step confirm/cancel — submitting refreshes immediately.
export function PlayerRefreshSection({ accessToken, onAuthError }: PlayerRefreshSectionProps) {
  const [playerId, setPlayerId] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<RefreshPlayerFromWikidataResponse | null>(null);

  async function handleRefresh() {
    setSubmitting(true);
    setError(null);
    try {
      const response = await refreshPlayerFromWikidata(accessToken, playerId.trim());
      setResult(response);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      // REQ-514's three named error states — each gets its own specific
      // message, never a shared generic one (the test level's own
      // requirement), via the shared describePlayerRefreshError helper
      // (REQ-515 extracted this so PlayerReviewPanel's inline refresh action
      // never carries a second copy of the same mapping).
      setError(describePlayerRefreshError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Refresh a player from Wikidata</h3>
      <label className="admin-screen__field">
        <span>Player id</span>
        <input
          type="text"
          required
          value={playerId}
          onChange={(event) => {
            setPlayerId(event.target.value);
            setError(null);
            setResult(null);
          }}
          disabled={submitting}
        />
      </label>

      {error && (
        <p className="admin-screen__error" role="alert">
          {error}
        </p>
      )}

      <div className="admin-screen__action-group">
        <button type="button" onClick={handleRefresh} disabled={submitting || !playerId.trim()}>
          {submitting ? 'Refreshing…' : 'Refresh from Wikidata'}
        </button>
      </div>

      {result && <PlayerRefreshFieldsList result={result} />}
    </section>
  );
}
