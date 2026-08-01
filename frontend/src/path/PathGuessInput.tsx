import { useState, type FormEvent } from 'react';
import { describeError } from '../lib/api';
import { MAX_CLUES_PER_PUZZLE } from '../lib/pathRules';
import type { CurrentPathGuess } from '../lib/types';
import './PathGuessInput.css';

export interface PathGuessInputProps {
  clueCount: number;
  guess: CurrentPathGuess | null;
  // Resolves to whether the submitted guess was correct (parent owns the
  // actual POST .../guesses call plus the GET /path/current re-fetch that
  // picks up the next revealed clue turn — see PathScreen.tsx's own
  // comment on why a re-fetch, not a local patch, is the mechanism). Throws
  // on a genuine request failure (network error, non-2xx) — never resolves
  // for a rejected guess, since a rejected guess is still a normal, scored
  // 200 response.
  onSubmit: (submittedName: string) => Promise<boolean>;
}

// SCREEN-10 (S-086): no disambiguation picker, no autocomplete/suggestions
// dropdown (REQ-207/PlayerNameIndex is xG-Grid-cell-specific, not mentioned
// anywhere in SCREEN-10's spec), no REQ-215 suggestion entry point (also
// out of this story's scope per S-086's own text) — deliberately a plain
// input + submit button only, unlike GuessInput.tsx's xG Grid version.
export function PathGuessInput({ clueCount, guess, onSubmit }: PathGuessInputProps) {
  const [name, setName] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // design-document.md SCREEN-10 "Rejected guess": reuses SCREEN-02's shake
  // cue verbatim (PathGuessInput.css) — same remount-via-key technique
  // CellState.tsx's useShakeToken uses so the animation restarts on every
  // rejection, even a second one in a row.
  const [shakeToken, setShakeToken] = useState(0);

  const isCorrect = guess?.isCorrect ?? false;
  const locked = guess?.locked ?? false;
  const disabled = submitting || isCorrect || locked;

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
      const correct = await onSubmit(trimmed);
      if (!correct) {
        setShakeToken((current) => current + 1);
        setName('');
      }
    } catch (err) {
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div key={shakeToken} className={`path-guess-input ${shakeToken > 0 && !isCorrect ? 'path-guess-input--shake' : ''}`}>
      <form onSubmit={handleSubmit} className="path-guess-input__form">
        <input
          type="text"
          className="path-guess-input__field"
          placeholder="Guess the player…"
          autoComplete="off"
          value={name}
          onChange={(event) => setName(event.target.value)}
          disabled={disabled}
          aria-label="Player name"
        />
        <button type="submit" className="path-guess-input__submit" disabled={disabled}>
          {submitting ? 'Submitting…' : 'Guess'}
        </button>
      </form>
      {error && <p className="path-guess-input__error">{error}</p>}
      {/* §6: text-paired, never color-only — locked/solved are both stated
          in words, not just a disabled-looking control. */}
      {locked && !isCorrect && (
        <p className="path-guess-input__locked">No attempts remain for this puzzle.</p>
      )}
      {isCorrect && <p className="path-guess-input__solved-hint">Solved — nothing left to guess here.</p>}
      <p className="path-guess-input__counter mono-figure">
        Clue {clueCount} of {MAX_CLUES_PER_PUZZLE}
      </p>
    </div>
  );
}
