import { useState } from 'react';
import { ApiError } from '../lib/apiClient';
import { submitConnectTargetPick } from '../lib/connectMatches';
import { useSubmitAction } from '../lib/useSubmitAction';
import type { ConnectTargetPickView, PlayerAutocompleteSuggestion } from '../lib/types';
import { PlayerSearchField } from './PlayerSearchField';

export interface TargetPickPanelProps {
  matchId: string;
  accessToken: string;
  myTargetPick: ConnectTargetPickView | null;
  onAuthError: () => void;
  // Always called after a successful submit (locked or not) — the parent
  // owns the real GET /matches/{matchId} refetch, since a submission may
  // have just started the match (both picks now locked) or resolved the
  // opponent's own trivially-connected block, neither of which this panel
  // can see on its own.
  onSubmitted: () => void;
}

// REQ-1404 (design-document.md SCREEN-16's "Target-pick phase"): target-pick
// selection, including the trivially-connected rejection. Free resubmission
// is allowed for as long as `myTargetPick` isn't locked — this panel always
// renders the search form in that state (pre-filled with the current pick's
// name, if any), never just a static "you picked X" once-only view.
export function TargetPickPanel({ matchId, accessToken, myTargetPick, onAuthError, onSubmitted }: TargetPickPanelProps) {
  const [name, setName] = useState('');
  const [selectedName, setSelectedName] = useState<string | null>(null);
  const { submitting, error, run } = useSubmitAction<void>({ onAuthError });
  // TriviallyConnected (REQ-1404) and TargetPlayerNotFound both get their
  // own, more specific message than whatever generic ApiError.detail
  // handling would otherwise apply, since the server's own wording already
  // instructs the player what to do next — surfaced separately from `error`
  // so a plain retry-able 503 doesn't get the same "pick again" framing.
  const [rejectionNotice, setRejectionNotice] = useState<string | null>(null);

  if (myTargetPick?.locked) {
    return (
      <section className="connect-match__section">
        <h3 className="connect-match__section-title">Target picks locked in</h3>
        <p className="connect-match__description">
          Your target: <strong>{myTargetPick.targetPlayerName}</strong>
        </p>
        <p className="connect-match__status">Waiting for your opponent to lock in their target pick…</p>
      </section>
    );
  }

  function handleSelect(suggestion: PlayerAutocompleteSuggestion) {
    setSelectedName(suggestion.name);
    setRejectionNotice(null);
  }

  function handleSubmit() {
    if (!selectedName) return;
    setRejectionNotice(null);
    run(
      async () => {
        try {
          await submitConnectTargetPick(accessToken, matchId, selectedName);
        } catch (err) {
          if (
            err instanceof ApiError &&
            ((err.status === 409 && err.title === 'Target picks are already connected') ||
              (err.status === 404 && err.title === 'Target player not found'))
          ) {
            setRejectionNotice(err.detail ?? err.title);
            // REQ-1404: nothing was persisted for this (rejected) selection
            // — the player's own prior pick, if any, is unaffected. Clear
            // the field so they search for a genuinely different target
            // rather than resubmitting the exact same rejected name.
            setName('');
            setSelectedName(null);
          }
          // Re-thrown either way — useSubmitAction's `run` must treat this
          // as a failed submission (never calling onSubmitted/refetch,
          // since nothing changed server-side for a rejection). Setting
          // `error` too is harmless: the `!rejectionNotice` guard on that
          // render below keeps the two messages from ever showing at once.
          throw err;
        }
      },
      () => onSubmitted(),
    );
  }

  return (
    <section className="connect-match__section">
      <h3 className="connect-match__section-title">Pick your target player</h3>
      {myTargetPick && (
        <p className="connect-match__description">
          Current pick: <strong>{myTargetPick.targetPlayerName}</strong> — you can change it until your opponent
          also picks.
        </p>
      )}
      <p className="connect-match__description">
        Search for a real player. Once both of you have picked, the puzzle is fixed: the shortest played-together
        chain linking your two targets.
      </p>
      <PlayerSearchField
        id="target-pick-search"
        label="Target player name"
        accessToken={accessToken}
        value={name}
        onValueChange={(value) => {
          setName(value);
          setSelectedName(null);
        }}
        onSelect={handleSelect}
        placeholder="Search for a player…"
        disabled={submitting}
      />
      <button
        type="button"
        className="connect-match__button"
        disabled={submitting || !selectedName}
        onClick={handleSubmit}
      >
        {submitting ? 'Saving…' : 'Set target pick'}
      </button>
      {rejectionNotice && (
        <p className="connect-match__error" role="alert">
          {rejectionNotice}
        </p>
      )}
      {error && !rejectionNotice && (
        <p className="connect-match__error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}
