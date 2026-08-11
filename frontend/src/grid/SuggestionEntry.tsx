import { useState, type FormEvent } from 'react';
import { describeError } from '../lib/apiClient';
import { submitSuggestion } from '../lib/rounds';
import { SUGGESTION_GUEST_LOCKED_COPY, SUGGESTION_SUBMITTED_COPY } from '../lib/suggestionCopy';
import './SuggestionEntry.css';

export interface SuggestionEntryProps {
  roundId: string;
  cellId: string;
  accessToken: string;
  // The player name already known from the triggering guess (REQ-215) —
  // never re-typed by the player here, only shown read-only for context.
  playerName: string;
  // REQ-215's "guest vs. non-guest visibility" rule: present but
  // disabled/inert for a guest, with copy explaining registration unlocks
  // it — never fully hidden. The actual restriction is enforced
  // server-side regardless (submitSuggestion's own 403), this prop only
  // controls what the entry point looks like.
  isGuest: boolean;
}

type Phase = 'idle' | 'open' | 'submitted';

// REQ-215 (S-089): the frontend half of the player-suggested-correction
// pipeline — appears only where GuessInput.tsx mounts it (an incorrect
// scored guess, or a REQ-211 live-lookup timeout), never as a standalone
// always-visible control. No multi-value input pattern exists elsewhere in
// this codebase (design-document.md §2's own token system has no
// "chip list"/"tag input" component), so multiple clubs are entered as one
// comma-separated field — the simpler of the two reasonable shapes the
// story description offered, and consistent with this form's otherwise
// plain-text-field-only style.
export function SuggestionEntry({ roundId, cellId, accessToken, playerName, isGuest }: SuggestionEntryProps) {
  const [phase, setPhase] = useState<Phase>('idle');
  const [clubsInput, setClubsInput] = useState('');
  const [nationality, setNationality] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (isGuest) {
    return (
      <div className="suggestion-entry suggestion-entry--guest">
        <button
          type="button"
          className="suggestion-entry__trigger"
          disabled
          aria-disabled="true"
          data-testid="suggestion-entry-point"
        >
          Suggest a correction
        </button>
        <p className="suggestion-entry__guest-copy" data-testid="suggestion-guest-copy">
          {SUGGESTION_GUEST_LOCKED_COPY}
        </p>
      </div>
    );
  }

  if (phase === 'submitted') {
    return (
      <p className="suggestion-entry__confirmation" data-testid="suggestion-confirmation">
        {SUGGESTION_SUBMITTED_COPY}
      </p>
    );
  }

  if (phase === 'idle') {
    return (
      <button
        type="button"
        className="suggestion-entry__trigger"
        onClick={() => setPhase('open')}
        data-testid="suggestion-entry-point"
      >
        Suggest a correction
      </button>
    );
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    // REQ-215: "requires at least one club... and the nationality... clear
    // validation error if either is missing" — client-side first, though
    // the backend is the real enforcement (SuggestionEndpoints).
    const clubs = clubsInput
      .split(',')
      .map((club) => club.trim())
      .filter(Boolean);
    if (clubs.length === 0) {
      setError('Enter at least one club.');
      return;
    }
    if (!nationality.trim()) {
      setError('Enter the nationality you believe is correct.');
      return;
    }

    setSubmitting(true);
    setError(null);
    try {
      await submitSuggestion(accessToken, roundId, cellId, playerName, clubs, nationality.trim());
      setPhase('submitted');
    } catch (err) {
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form className="suggestion-entry__form" onSubmit={handleSubmit}>
      <p className="suggestion-entry__intro">
        Believe {playerName} is a genuine match? Tell us the club(s) and
        nationality an admin should check — this won&apos;t change this
        guess&apos;s own score.
      </p>
      <label className="suggestion-entry__field">
        <span>Player</span>
        <input type="text" value={playerName} readOnly disabled data-testid="suggestion-player-name" />
      </label>
      <label className="suggestion-entry__field">
        <span>Club(s)</span>
        <input
          type="text"
          placeholder="e.g. Arsenal, Barcelona"
          value={clubsInput}
          onChange={(event) => setClubsInput(event.target.value)}
          disabled={submitting}
          data-testid="suggestion-clubs-input"
        />
      </label>
      <label className="suggestion-entry__field">
        <span>Nationality</span>
        <input
          type="text"
          value={nationality}
          onChange={(event) => setNationality(event.target.value)}
          disabled={submitting}
          data-testid="suggestion-nationality-input"
        />
      </label>
      {error && <p className="suggestion-entry__error">{error}</p>}
      <div className="suggestion-entry__actions">
        <button
          type="button"
          className="suggestion-entry__cancel"
          onClick={() => setPhase('idle')}
          disabled={submitting}
        >
          Cancel
        </button>
        <button
          type="submit"
          className="suggestion-entry__submit"
          disabled={submitting}
          data-testid="suggestion-submit"
        >
          {submitting ? 'Submitting…' : 'Submit suggestion'}
        </button>
      </div>
    </form>
  );
}
