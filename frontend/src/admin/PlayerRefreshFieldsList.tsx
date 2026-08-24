import { ApiError, describeError } from '../lib/apiClient';
import type { PlayerRefreshFieldResult, RefreshPlayerFromWikidataResponse } from '../lib/types';
import './PlayerRefreshFieldsList.css';

// REQ-513/514/515: the four-field changed/unchanged/old-new value
// presentation for POST /admin/players/{id}/refresh-from-wikidata's
// response, shared by PlayerRefreshSection (REQ-514's original
// player-id-driven refresh flow) and PlayerReviewPanel's inline
// "Refresh from Wikidata" action (REQ-515, SuggestionsScreen.tsx) — extracted
// here so the two call sites never carry two copies of the same display
// logic (the exact duplication the code-health-budget rule flags). Its own
// class prefix/CSS file, not either caller's `admin-screen__*`/
// `suggestions-screen__*` namespace, since both entry points render it.
export const PLAYER_REFRESH_FIELD_LABELS: ReadonlyArray<{ key: string; label: string }> = [
  { key: 'fullName', label: 'Full name' },
  { key: 'position', label: 'Position' },
  { key: 'birthYear', label: 'Birth year' },
  { key: 'photoUrl', label: 'Photo URL' },
];

// REQ-514: a field missing from the response (should never happen — REQ-513
// always returns all four) degrades to "Unchanged: (none)" rather than
// throwing, since this is display-only, read-safe code.
export function describePlayerRefreshField(label: string, field: PlayerRefreshFieldResult | undefined): string {
  const oldDisplay = field?.oldValue ?? '(none)';
  if (field?.changed) {
    return `${label}: Changed — "${oldDisplay}" → "${field.newValue ?? '(none)'}"`;
  }
  return `${label}: Unchanged — "${oldDisplay}"`;
}

// REQ-513/514/515: the three named error states
// POST /admin/players/{id}/refresh-from-wikidata can return, shared by both
// call sites above — see PlayerRefreshSection.tsx's original REQ-514 doc
// comment for why 404/409 are UI-authored while 503 reads the server's own
// `detail` via `describeError`. A 401 is deliberately NOT handled here —
// both callers check `err.status === 401` and call their own `onAuthError`
// before ever reaching this helper, same as every other admin action.
export function describePlayerRefreshError(err: unknown): string {
  if (err instanceof ApiError && err.status === 404) {
    return 'No player found with that id.';
  }
  if (err instanceof ApiError && err.status === 409) {
    return 'This player has no Wikidata id to refresh from.';
  }
  return describeError(err);
}

export interface PlayerRefreshFieldsListProps {
  result: RefreshPlayerFromWikidataResponse;
}

// REQ-514: renders every one of the four fields as a single text node per
// row, explicitly marked "Changed"/"Unchanged" — never color-only (§6's
// accessibility floor).
export function PlayerRefreshFieldsList({ result }: PlayerRefreshFieldsListProps) {
  return (
    <ul className="player-refresh-fields">
      {PLAYER_REFRESH_FIELD_LABELS.map(({ key, label }) => {
        const field = result.fields.find((candidate) => candidate.field === key);
        const changed = field?.changed ?? false;
        return (
          <li key={key} className="player-refresh-fields__row">
            <span
              className={
                changed
                  ? 'player-refresh-fields__field player-refresh-fields__field--changed'
                  : 'player-refresh-fields__field player-refresh-fields__field--unchanged'
              }
            >
              {describePlayerRefreshField(label, field)}
            </span>
          </li>
        );
      })}
    </ul>
  );
}
