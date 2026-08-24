import { useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { refreshPlayerFromWikidata } from '../lib/admin';
import type { PlayerRefreshFieldResult, RefreshPlayerFromWikidataResponse } from '../lib/types';

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
const FIELD_LABELS: ReadonlyArray<{ key: string; label: string }> = [
  { key: 'fullName', label: 'Full name' },
  { key: 'position', label: 'Position' },
  { key: 'birthYear', label: 'Birth year' },
  { key: 'photoUrl', label: 'Photo URL' },
];

// REQ-514: renders every one of the four fields as a single text node per
// row, explicitly marked "Changed"/"Unchanged" — never color-only (§6's
// accessibility floor) — with a changed field showing both its old and new
// value and an unchanged field showing its current stored value (`field`'s
// `oldValue`, which is always the pre-refresh value regardless of
// `changed`). A field missing from the response (should never happen —
// REQ-513 always returns all four) degrades to "Unchanged: (none)" rather
// than throwing, since this is display-only, read-safe code.
function describeField(label: string, field: PlayerRefreshFieldResult | undefined): string {
  const oldDisplay = field?.oldValue ?? '(none)';
  if (field?.changed) {
    return `${label}: Changed — "${oldDisplay}" → "${field.newValue ?? '(none)'}"`;
  }
  return `${label}: Unchanged — "${oldDisplay}"`;
}

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
      // requirement). 404's bare NotFound() has no response body to read a
      // detail from, so that message is crafted here rather than sourced
      // from the server; 409/503 both already carry an admin-appropriate
      // `detail` from AdminEndpoints.cs, read via the same describeError
      // convention every other admin action in this directory already uses
      // (e.g. UserDeletionSection's non-404/401 fallback).
      if (err instanceof ApiError && err.status === 404) {
        setError('No player found with that id.');
      } else if (err instanceof ApiError && err.status === 409) {
        setError('This player has no Wikidata id to refresh from.');
      } else {
        setError(describeError(err));
      }
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

      {result && (
        <ul className="admin-screen__list">
          {FIELD_LABELS.map(({ key, label }) => {
            const field = result.fields.find((candidate) => candidate.field === key);
            const changed = field?.changed ?? false;
            return (
              <li key={key} className="admin-screen__row">
                <span
                  className={
                    changed
                      ? 'admin-screen__refresh-field admin-screen__refresh-field--changed'
                      : 'admin-screen__refresh-field admin-screen__refresh-field--unchanged'
                  }
                >
                  {describeField(label, field)}
                </span>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
