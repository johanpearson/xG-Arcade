import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { CategoryLabel } from './CategoryLabel';
import { describeError, fetchPlayerAutocomplete } from '../lib/api';
import { MAX_ATTEMPTS_PER_CELL } from '../lib/guessRules';
import type { CurrentRoundCell, DisambiguationCandidate, PlayerAutocompleteSuggestion } from '../lib/types';
import './GuessInput.css';

export interface GuessInputProps {
  cell: CurrentRoundCell;
  accessToken: string;
  // REQ-209: resolves to the disambiguation candidates when the submitted
  // name matched more than one fitting player (SubmitGuessResponse.candidates
  // non-null/non-empty) — GuessInput renders SCREEN-02a's picker instead of
  // closing in that case. Resolves to `undefined` for every ordinary,
  // already-scored result (correct or incorrect), same as this prop's
  // original contract — GuessInput closes exactly as it always did.
  onSubmit: (submittedName: string) => Promise<DisambiguationCandidate[] | undefined>;
  // REQ-209/REQ-210: resolves the picker by resubmitting the same guess with
  // the chosen candidate's playerId. Always a normal, scored response
  // (never another disambiguation prompt) — resolving this promise closes
  // the sheet exactly like a normal onSubmit success; rejecting it shows the
  // error inline and leaves the picker open, same error-handling shape as
  // the plain guess form. This never consumes a separate attempt — it's the
  // same attempt REQ-210 already counted for the submission that triggered
  // the prompt, per REQ-210's explicit clause.
  onResolveDisambiguation: (chosenPlayerId: string, submittedName: string) => Promise<void>;
  onClose: () => void;
}

// REQ-207 (S-032): only fetch once the trimmed query is at least this long
// — an empty/very-short query is a near-certain-miss request that's not
// worth a round trip, and matches the backend contract's own "empty/very
// short query returns an empty array" behavior.
const MIN_QUERY_LENGTH = 2;
// Simple setTimeout-based debounce — no new library needed for this.
const DEBOUNCE_MS = 275;
const SUGGESTION_LIMIT = 8;

// SCREEN-02: bottom sheet on mobile / inline popover on desktop, switched
// purely by CSS media query (GuessInput.css) — no new library.
//
// REQ-207/ADR-0007 (S-032): suggestions are sourced from PlayerNameIndex
// only (via fetchPlayerAutocomplete), entirely separate from the
// PlayerAttribute/PlayerOverride correctness-check path (REQ-203). A name
// appearing in this list implies nothing about whether it's correct for
// this cell — so the list is deliberately styled with neutral tokens only
// (no accent-green/accent-gold "this looks right" treatment), carries no
// client-side re-ranking, and selecting an item only fills the input; it
// never auto-submits, since that could read as the UI vouching for the
// answer. The suggestions fetch is a nice-to-have: a network failure here
// is swallowed and just shows no suggestions, never blocking or erroring
// the guess form itself.
export function GuessInput({ cell, accessToken, onSubmit, onResolveDisambiguation, onClose }: GuessInputProps) {
  const [name, setName] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // REQ-209/SCREEN-02a: non-null only once a submission comes back needing
  // disambiguation — while set, the picker view replaces the plain guess
  // form entirely (the categories header and Cancel button stay, since
  // abandoning the picker via Cancel/backdrop-click is still "the guess is
  // not submitted," same as abandoning the plain form).
  const [candidates, setCandidates] = useState<DisambiguationCandidate[] | null>(null);
  const [selectedCandidateId, setSelectedCandidateId] = useState<string | null>(null);

  const [suggestions, setSuggestions] = useState<PlayerAutocompleteSuggestion[]>([]);
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [highlightedIndex, setHighlightedIndex] = useState(-1);
  // Selecting a suggestion sets `name` to that suggestion's own text, which
  // would otherwise immediately re-trigger this same effect and reopen the
  // list for a query that just got answered — this ref skips exactly that
  // one re-trigger without needing to touch the debounce timing itself.
  const justSelectedRef = useRef(false);

  const attemptCount = cell.guess?.attemptCount ?? 0;
  const locked = cell.guess?.locked ?? false;
  const listboxId = `guess-input-suggestions-${cell.cellId}`;

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
      const result = await onSubmit(trimmed);
      if (result) {
        // REQ-209: nothing was scored — show the picker instead of closing.
        setCandidates(result);
        setSelectedCandidateId(null);
      } else {
        onClose();
      }
    } catch (err) {
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  async function handleConfirmDisambiguation(event: FormEvent) {
    event.preventDefault();
    if (!selectedCandidateId) {
      setError('Choose a player to submit your guess.');
      return;
    }

    setSubmitting(true);
    setError(null);
    try {
      await onResolveDisambiguation(selectedCandidateId, name.trim());
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

        {candidates ? (
          // SCREEN-02a: the disambiguation prompt (REQ-209) — replaces the
          // plain guess form once a submission comes back matching more than
          // one fitting candidate. The header/Cancel button above stay put,
          // so abandoning this via Cancel or a backdrop click still means
          // "the guess is not submitted" (SCREEN-02a), same as abandoning
          // the plain form.
          <form onSubmit={handleConfirmDisambiguation} className="guess-input__form">
            <h3 className="guess-input__disambiguation-prompt">Which player did you mean?</h3>
            <div
              className="guess-input__candidates"
              role="radiogroup"
              aria-label="Choose the player you meant"
            >
              {candidates.map((candidate) => (
                <label key={candidate.playerId} className="guess-input__candidate">
                  <input
                    type="radio"
                    className="guess-input__candidate-radio"
                    name="guess-input-disambiguation-candidate"
                    value={candidate.playerId}
                    checked={selectedCandidateId === candidate.playerId}
                    onChange={() => setSelectedCandidateId(candidate.playerId)}
                    disabled={submitting}
                  />
                  <span className="guess-input__candidate-text">
                    <span className="guess-input__candidate-name">{candidate.name}</span>
                    {candidate.distinguishingAttributes.length > 0 && (
                      <span className="guess-input__candidate-meta">
                        {candidate.distinguishingAttributes.join(' · ')}
                      </span>
                    )}
                  </span>
                </label>
              ))}
            </div>
            {error && <p className="guess-input__error">{error}</p>}
            <button
              type="submit"
              className="guess-input__submit"
              disabled={submitting || !selectedCandidateId}
            >
              {submitting ? 'Submitting…' : 'Confirm'}
            </button>
          </form>
        ) : locked ? (
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
              onKeyDown={handleFieldKeyDown}
              disabled={submitting}
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
              <ul className="guess-input__suggestions" role="listbox" id={listboxId} aria-label="Player name suggestions">
                {suggestions.map((suggestion, index) => (
                  <li
                    key={suggestion.playerId}
                    id={`${listboxId}-option-${index}`}
                    role="option"
                    aria-selected={index === highlightedIndex}
                    className={
                      index === highlightedIndex
                        ? 'guess-input__suggestion guess-input__suggestion--highlighted'
                        : 'guess-input__suggestion'
                    }
                    // Selecting via mouse must fire before the field's blur
                    // handler would otherwise dismiss the list.
                    onMouseDown={(event) => event.preventDefault()}
                    onClick={() => selectSuggestion(suggestion)}
                  >
                    <span className="guess-input__suggestion-name">{suggestion.name}</span>
                    {(suggestion.nationality || suggestion.birthYear) && (
                      <span className="guess-input__suggestion-meta">
                        {[suggestion.nationality, suggestion.birthYear].filter(Boolean).join(' · ')}
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            )}
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
