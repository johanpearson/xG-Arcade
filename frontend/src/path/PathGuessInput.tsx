import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { describeError, fetchPlayerAutocomplete } from '../lib/api';
import { MAX_CLUES_PER_PUZZLE } from '../lib/pathRules';
import type { CurrentPathGuess, PlayerAutocompleteSuggestion } from '../lib/types';
import './PathGuessInput.css';

export interface PathGuessInputProps {
  clueCount: number;
  guess: CurrentPathGuess | null;
  // REQ-207/ADR-0007 (S-091): needed only to call the shared, game-agnostic
  // GET /players/autocomplete endpoint — same token the caller already holds
  // for every other authenticated request, nothing xG-Path-specific about it.
  accessToken: string;
  // Resolves to whether the submitted guess was correct (parent owns the
  // actual POST .../guesses call plus the GET /path/current re-fetch that
  // picks up the next revealed clue turn — see PathScreen.tsx's own
  // comment on why a re-fetch, not a local patch, is the mechanism). Throws
  // on a genuine request failure (network error, non-2xx) — never resolves
  // for a rejected guess, since a rejected guess is still a normal, scored
  // 200 response.
  onSubmit: (submittedName: string) => Promise<boolean>;
}

// REQ-207 (S-091): same debounce/limit constants GuessInput.tsx (xG Grid)
// uses for the identical, shared autocomplete endpoint — no reason for this
// screen to behave differently.
const MIN_QUERY_LENGTH = 2;
const DEBOUNCE_MS = 275;
const SUGGESTION_LIMIT = 8;

// SCREEN-10 (S-086, autocomplete added S-091): no disambiguation picker
// (REQ-209 — deliberately reviewed and rejected for xG Path, see S-091's
// backlog entry: XGPathGameModule.ScoreSubmissionAsync/REQ-1204 already
// resolves correctness independent of which same-named candidate a picker
// would let the player choose, so a picker here would be purely cosmetic),
// no REQ-215 suggestion entry point (out of this story's scope). Autocomplete
// (REQ-207/ADR-0007) is wired the same way GuessInput.tsx's xG Grid version
// does: suggestions come from the shared, game-agnostic PlayerNameIndex via
// fetchPlayerAutocomplete, imply nothing about correctness, and selecting one
// only fills the field — it never auto-submits. A failed suggestions fetch is
// swallowed and just shows no suggestions, never blocking or erroring the
// guess form itself.
export function PathGuessInput({ clueCount, guess, accessToken, onSubmit }: PathGuessInputProps) {
  const [name, setName] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // design-document.md SCREEN-10 "Rejected guess": reuses SCREEN-02's shake
  // cue verbatim (PathGuessInput.css) — same remount-via-key technique
  // CellState.tsx's useShakeToken uses so the animation restarts on every
  // rejection, even a second one in a row.
  const [shakeToken, setShakeToken] = useState(0);

  const [suggestions, setSuggestions] = useState<PlayerAutocompleteSuggestion[]>([]);
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [highlightedIndex, setHighlightedIndex] = useState(-1);
  // Same purpose as GuessInput.tsx's identical ref: selecting a suggestion
  // sets `name` to that suggestion's own text, which would otherwise
  // immediately re-trigger the fetch effect below and reopen the list for a
  // query that just got answered.
  const justSelectedRef = useRef(false);

  const isCorrect = guess?.isCorrect ?? false;
  const locked = guess?.locked ?? false;
  const disabled = submitting || isCorrect || locked;
  const listboxId = 'path-guess-input-suggestions';

  useEffect(() => {
    if (justSelectedRef.current) {
      justSelectedRef.current = false;
      setSuggestions([]);
      setShowSuggestions(false);
      return;
    }

    const trimmed = name.trim();
    if (trimmed.length < MIN_QUERY_LENGTH) {
      setSuggestions([]);
      setShowSuggestions(false);
      setHighlightedIndex(-1);
      return;
    }

    let cancelled = false;
    const timer = setTimeout(() => {
      fetchPlayerAutocomplete(accessToken, trimmed, SUGGESTION_LIMIT)
        .then((results) => {
          if (cancelled) return;
          setSuggestions(results);
          setShowSuggestions(results.length > 0);
          setHighlightedIndex(-1);
        })
        .catch(() => {
          // Autocomplete is a nice-to-have — a failed fetch never blocks or
          // errors the guess form, it just shows no suggestions.
          if (cancelled) return;
          setSuggestions([]);
          setShowSuggestions(false);
        });
    }, DEBOUNCE_MS);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [name, accessToken]);

  function selectSuggestion(suggestion: PlayerAutocompleteSuggestion) {
    justSelectedRef.current = true;
    setName(suggestion.name);
    setSuggestions([]);
    setShowSuggestions(false);
    setHighlightedIndex(-1);
  }

  function handleFieldKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (!showSuggestions || suggestions.length === 0) return;

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setHighlightedIndex((prev) => (prev + 1) % suggestions.length);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setHighlightedIndex((prev) => (prev <= 0 ? suggestions.length - 1 : prev - 1));
    } else if (event.key === 'Enter') {
      if (highlightedIndex >= 0) {
        event.preventDefault();
        selectSuggestion(suggestions[highlightedIndex]);
      }
      // No item highlighted — let Enter fall through to the form's normal
      // submit, same as if there were no suggestions at all.
    } else if (event.key === 'Escape') {
      event.preventDefault();
      setShowSuggestions(false);
      setHighlightedIndex(-1);
    }
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) {
      setError('Type a player name to submit a guess.');
      return;
    }

    setShowSuggestions(false);
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
        <div className="path-guess-input__field-wrap">
          <input
            type="text"
            className="path-guess-input__field"
            placeholder="Guess the player…"
            autoComplete="off"
            value={name}
            onChange={(event) => setName(event.target.value)}
            onKeyDown={handleFieldKeyDown}
            disabled={disabled}
            aria-label="Player name"
            role="combobox"
            aria-expanded={showSuggestions}
            aria-controls={listboxId}
            aria-autocomplete="list"
            aria-activedescendant={
              showSuggestions && highlightedIndex >= 0
                ? `${listboxId}-option-${highlightedIndex}`
                : undefined
            }
          />
          {showSuggestions && (
            <ul
              className="path-guess-input__suggestions"
              role="listbox"
              id={listboxId}
              aria-label="Player name suggestions"
            >
              {suggestions.map((suggestion, index) => (
                <li
                  key={suggestion.playerId}
                  id={`${listboxId}-option-${index}`}
                  role="option"
                  aria-selected={index === highlightedIndex}
                  className={
                    index === highlightedIndex
                      ? 'path-guess-input__suggestion path-guess-input__suggestion--highlighted'
                      : 'path-guess-input__suggestion'
                  }
                  // Selecting via mouse must fire before the field's blur
                  // handler would otherwise dismiss the list.
                  onMouseDown={(event) => event.preventDefault()}
                  onClick={() => selectSuggestion(suggestion)}
                >
                  <span className="path-guess-input__suggestion-name">{suggestion.name}</span>
                  {suggestion.birthYear && (
                    <span className="path-guess-input__suggestion-meta">{suggestion.birthYear}</span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
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
