import { useState, type FormEvent } from 'react';
import { CategoryLabel } from './CategoryLabel';
import { describeError } from '../lib/api';
import { MAX_ATTEMPTS_PER_CELL } from '../lib/guessRules';
import type { CurrentRoundCell } from '../lib/types';
import './GuessInput.css';

export interface GuessInputProps {
  cell: CurrentRoundCell;
  onSubmit: (submittedName: string) => Promise<void>;
  onClose: () => void;
}

// SCREEN-02: bottom sheet on mobile / inline popover on desktop, switched
// purely by CSS media query (GuessInput.css) — no new library. Tier 0 has
// no autocomplete (REQ-207 deferred) — this is a plain controlled <input>.
export function GuessInput({ cell, onSubmit, onClose }: GuessInputProps) {
  const [name, setName] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const attemptCount = cell.guess?.attemptCount ?? 0;
  const locked = cell.guess?.locked ?? false;

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) {
      setError('Type a player name to submit a guess.');
      return;
    }

    setSubmitting(true);
    setError(null);
    try {
      await onSubmit(trimmed);
      onClose();
    } catch (err) {
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="guess-input-backdrop" onClick={onClose}>
      <div
        className="guess-input"
        role="dialog"
        aria-modal="true"
        aria-label={`Guess ${cell.rowCategoryValue} × ${cell.colCategoryValue}`}
        onClick={(event) => event.stopPropagation()}
      >
        <div className="guess-input__header">
          <span className="guess-input__categories">
            <CategoryLabel categoryType={cell.rowCategoryType} value={cell.rowCategoryValue} />
            <span className="guess-input__x" aria-hidden="true">
              ×
            </span>
            <CategoryLabel categoryType={cell.colCategoryType} value={cell.colCategoryValue} />
          </span>
          <button
            type="button"
            className="guess-input__cancel"
            onClick={onClose}
            aria-label="Cancel guess"
          >
            Cancel
          </button>
        </div>

        {/* Only shown once at least 1 attempt has been used — an untried
            cell shows no attempt count line at all (design-document.md
            SCREEN-02). */}
        {attemptCount > 0 && (
          <p className="guess-input__attempts">
            {attemptCount} of {MAX_ATTEMPTS_PER_CELL} attempts used
          </p>
        )}

        {locked ? (
          <p className="guess-input__locked">
            This cell is locked — no attempts remain.
          </p>
        ) : (
          <form onSubmit={handleSubmit} className="guess-input__form">
            <input
              type="text"
              className="guess-input__field"
              placeholder="Type a player name..."
              autoComplete="off"
              autoFocus
              value={name}
              onChange={(event) => setName(event.target.value)}
              disabled={submitting}
              aria-label="Player name"
            />
            {error && <p className="guess-input__error">{error}</p>}
            <button type="submit" className="guess-input__submit" disabled={submitting}>
              {submitting ? 'Submitting…' : 'Submit guess'}
            </button>
          </form>
        )}
      </div>
    </div>
  );
}
