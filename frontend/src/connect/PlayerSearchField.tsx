import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { fetchPlayerAutocomplete } from '../lib/rounds';
import type { PlayerAutocompleteSuggestion } from '../lib/types';

export interface PlayerSearchFieldProps {
  id: string;
  label: string;
  accessToken: string;
  value: string;
  onValueChange: (value: string) => void;
  // Fired only when a suggestion is actually picked (click or Enter on a
  // highlighted option) — never on plain typing. The caller decides what a
  // selection means: TargetPickPanel needs the suggestion's playerId (the
  // target-pick endpoint takes an id), ChainBuilder's candidate field only
  // ever needs the name text (the chain-step endpoint takes a name and
  // resolves it server-side) — see each caller's own comment.
  onSelect: (suggestion: PlayerAutocompleteSuggestion) => void;
  placeholder?: string;
  disabled?: boolean;
}

// S-218 (design-document.md SCREEN-16): the shared player-name-search input
// behind both the target-pick form (TargetPickPanel) and the chain-step
// candidate field (ChainBuilder) — REQ-1406's own "search-pattern
// precedent" note points at GuessInput.tsx (xG Grid)/PathGuessInput.tsx (xG
// Path) for this exact debounce/suggestion-list/keyboard-nav shape. Unlike
// those two (different feature areas, each keeps its own duplicate per this
// codebase's own "extract past three" convention — see FetchListSection.tsx's
// own comment), TargetPickPanel and ChainBuilder live in the same new
// `connect/` feature area, so this is built shared from the start rather
// than duplicated a third time.
//
// Deliberately owns only the search/suggestion-list mechanics, never a
// <form> or submit button — each caller wraps this in whatever form shape
// it needs (a single-field target pick vs. a two-field candidate+club chain
// step).
const MIN_QUERY_LENGTH = 2;
const DEBOUNCE_MS = 150;
const SUGGESTION_LIMIT = 8;

export function PlayerSearchField({
  id,
  label,
  accessToken,
  value,
  onValueChange,
  onSelect,
  placeholder,
  disabled,
}: PlayerSearchFieldProps) {
  const [suggestions, setSuggestions] = useState<PlayerAutocompleteSuggestion[]>([]);
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [highlightedIndex, setHighlightedIndex] = useState(-1);
  // Same purpose as GuessInput.tsx's/PathGuessInput.tsx's identical ref:
  // selecting a suggestion sets `value` to the caller's own chosen text,
  // which would otherwise immediately re-trigger the fetch effect below and
  // reopen the list for a query that just got answered.
  const justSelectedRef = useRef(false);
  const abortControllerRef = useRef<AbortController | null>(null);
  const listboxId = `${id}-suggestions`;

  useEffect(() => {
    if (justSelectedRef.current) {
      justSelectedRef.current = false;
      setSuggestions([]);
      setShowSuggestions(false);
      return;
    }

    const trimmed = value.trim();
    if (trimmed.length < MIN_QUERY_LENGTH) {
      setSuggestions([]);
      setShowSuggestions(false);
      setHighlightedIndex(-1);
      return;
    }

    const timer = setTimeout(() => {
      abortControllerRef.current?.abort();
      const controller = new AbortController();
      abortControllerRef.current = controller;

      fetchPlayerAutocomplete(accessToken, trimmed, SUGGESTION_LIMIT, controller.signal)
        .then((results) => {
          if (controller.signal.aborted) return;
          setSuggestions(results);
          setShowSuggestions(results.length > 0);
          setHighlightedIndex(-1);
        })
        .catch((err) => {
          if (err instanceof DOMException && err.name === 'AbortError') return;
          if (controller.signal.aborted) return;
          // Autocomplete is a nice-to-have — a failed fetch never blocks or
          // errors the surrounding form, it just shows no suggestions.
          setSuggestions([]);
          setShowSuggestions(false);
        });
    }, DEBOUNCE_MS);

    return () => {
      clearTimeout(timer);
      abortControllerRef.current?.abort();
    };
  }, [value, accessToken]);

  function selectSuggestion(suggestion: PlayerAutocompleteSuggestion) {
    justSelectedRef.current = true;
    onValueChange(suggestion.name);
    onSelect(suggestion);
    setSuggestions([]);
    setShowSuggestions(false);
    setHighlightedIndex(-1);
  }

  function handleKeyDown(event: KeyboardEvent<HTMLInputElement>) {
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
    } else if (event.key === 'Escape') {
      event.preventDefault();
      setShowSuggestions(false);
      setHighlightedIndex(-1);
    }
  }

  return (
    <div className="connect-match__search-field">
      <input
        type="text"
        id={id}
        className="connect-match__search-input"
        placeholder={placeholder}
        autoComplete="off"
        value={value}
        onChange={(event) => onValueChange(event.target.value)}
        onKeyDown={handleKeyDown}
        disabled={disabled}
        aria-label={label}
        role="combobox"
        aria-expanded={showSuggestions}
        aria-controls={listboxId}
        aria-autocomplete="list"
        aria-activedescendant={
          showSuggestions && highlightedIndex >= 0 ? `${listboxId}-option-${highlightedIndex}` : undefined
        }
      />
      {showSuggestions && (
        <ul className="connect-match__suggestions" role="listbox" id={listboxId} aria-label={`${label} suggestions`}>
          {suggestions.map((suggestion, index) => (
            <li
              key={suggestion.playerId}
              id={`${listboxId}-option-${index}`}
              role="option"
              aria-selected={index === highlightedIndex}
              className={
                index === highlightedIndex
                  ? 'connect-match__suggestion connect-match__suggestion--highlighted'
                  : 'connect-match__suggestion'
              }
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => selectSuggestion(suggestion)}
            >
              <span>{suggestion.name}</span>
              {suggestion.birthYear && (
                <span className="connect-match__suggestion-meta">{suggestion.birthYear}</span>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
