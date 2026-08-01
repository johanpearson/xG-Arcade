import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { CategoryLabel } from '../components/CategoryLabel';
import { SuggestionEntry } from './SuggestionEntry';
import { ApiError, describeError, fetchPlayerAutocomplete } from '../lib/api';
import { MAX_ATTEMPTS_PER_CELL } from '../lib/guessRules';
import type {
  CurrentRoundCell,
  DisambiguationCandidate,
  PlayerAutocompleteSuggestion,
  SubmitGuessResponse,
} from '../lib/types';
import './GuessInput.css';

export interface GuessInputProps {
  cell: CurrentRoundCell;
  // REQ-215 (S-089): needed to submit a suggestion (POST /rounds/{roundId}/
  // cells/{cellId}/suggestions) from either trigger site below — not used
  // for anything else in this component. Optional/defaulting to '' so
  // existing direct-unit-test call sites that never exercise an incorrect-
  // scored or live-lookup-timeout outcome (the only paths that ever mount
  // SuggestionEntry) don't need updating just to satisfy this prop —
  // GridScreen, the only real caller, always supplies the actual round id.
  roundId?: string;
  accessToken: string;
  // REQ-717/REQ-215: whether the caller is a guest account — controls only
  // the suggestion entry point's disabled/advertised state (SuggestionEntry
  // itself), nothing else in this form. Defaults to false so existing
  // direct-unit-test call sites that don't exercise guest gating don't need
  // updating.
  isGuest?: boolean;
  // REQ-209/REQ-215 (S-089 revision): resolves to the full, unmodified
  // SubmitGuessResponse for every submission that actually reaches the
  // server — GuessInput itself now decides what to show from its fields
  // (`candidates` non-null renders SCREEN-02a's picker; `isCorrect` decides
  // whether to close immediately or show REQ-215's "not a match" outcome
  // view with the suggestion entry point). Resolving to `undefined` is
  // still supported as a defensive "close, nothing to show" fallback for a
  // caller that genuinely has nothing to submit against (GridScreen's own
  // guard clause), not a real scored outcome.
  onSubmit: (submittedName: string) => Promise<SubmitGuessResponse | undefined>;
  // REQ-209/REQ-210/REQ-215 (S-089 revision): resolves the picker by
  // resubmitting the same guess with the chosen candidate's playerId —
  // always a normal, scored response (never another disambiguation prompt).
  // Same "GuessInput decides from the response's own fields" contract as
  // onSubmit above: a correct result closes the sheet exactly as before; an
  // incorrect one shows REQ-215's outcome view instead. This never consumes
  // a separate attempt — it's the same attempt REQ-210 already counted for
  // the submission that triggered the prompt, per REQ-210's explicit
  // clause. Rejecting shows the error inline and leaves the picker open,
  // same error-handling shape as the plain guess form.
  onResolveDisambiguation: (chosenPlayerId: string, submittedName: string) => Promise<SubmitGuessResponse | undefined>;
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
export function GuessInput({
  cell,
  roundId = '',
  accessToken,
  isGuest = false,
  onSubmit,
  onResolveDisambiguation,
  onClose,
}: GuessInputProps) {
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

  // REQ-215 (S-089): set once a submission (direct or via the REQ-209
  // disambiguation resubmission) comes back scored *incorrect*, and never
  // cleared while the sheet stays mounted (see the "Try another guess"
  // handler below) — it's this component's only source of the latest known
  // attemptCount/locked value, since the `cell` prop itself is a stale
  // snapshot from when the sheet opened (GridScreen doesn't re-pass a fresh
  // one while it stays mounted). `submittedName` is the specific player name
  // this outcome is about — the raw typed text for a direct submission, or
  // the chosen candidate's own canonical name for a disambiguation
  // resubmission (more accurate than re-using the raw typed text in that
  // case, since the player already confirmed exactly which real player they
  // meant).
  const [scoredResult, setScoredResult] = useState<{ response: SubmitGuessResponse; submittedName: string } | null>(
    null,
  );
  // REQ-215 (S-089): whether the outcome view (vs. the plain form) is
  // currently displayed — separate from `scoredResult` above so "Try
  // another guess" can return to the plain form (for a genuine second
  // attempt, still within the same sheet) without losing the latest known
  // attemptCount/locked value `scoredResult` carries; a further incorrect
  // submission sets both this and `scoredResult` again.
  const [showOutcome, setShowOutcome] = useState(false);
  // REQ-211/REQ-215: non-null only once a submission throws the
  // "Live verification unavailable" 503 (GridScreen's existing inline-error
  // handling for this case, unchanged) — REQ-215's second trigger
  // condition. Holds the exact name that was submitted when the timeout
  // happened, so the suggestion entry point (rendered alongside the
  // existing inline error, below) has a stable player name even if the
  // player keeps editing the field afterward.
  const [liveLookupUnavailableFor, setLiveLookupUnavailableFor] = useState<string | null>(null);

  const [suggestions, setSuggestions] = useState<PlayerAutocompleteSuggestion[]>([]);
  const [showSuggestions, setShowSuggestions] = useState(false);
  const [highlightedIndex, setHighlightedIndex] = useState(-1);
  // Selecting a suggestion sets `name` to that suggestion's own text, which
  // would otherwise immediately re-trigger this same effect and reopen the
  // list for a query that just got answered — this ref skips exactly that
  // one re-trigger without needing to touch the debounce timing itself.
  const justSelectedRef = useRef(false);

  // REQ-215 (S-089): once a submission has scored an outcome this render
  // (scoredResult), attemptCount/locked prefer that fresh response over the
  // `cell` prop — `cell` is a snapshot from when the sheet was opened and
  // GridScreen doesn't re-pass a fresh one while the sheet stays mounted
  // (only relevant now that a scored-incorrect result no longer closes the
  // sheet immediately; before this story the sheet always closed before
  // this staleness could ever be visible).
  const attemptCount = scoredResult?.response.attemptCount ?? cell.guess?.attemptCount ?? 0;
  const locked = scoredResult?.response.locked ?? cell.guess?.locked ?? false;
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
    setLiveLookupUnavailableFor(null);
    try {
      const result = await onSubmit(trimmed);
      if (!result) {
        // Defensive fallback only (GridScreen's own guard clause) — not a
        // real scored outcome, see this prop's own doc comment.
        onClose();
      } else if (result.candidates) {
        // REQ-209: nothing was scored — show the picker instead of closing.
        setCandidates(result.candidates);
        setSelectedCandidateId(null);
      } else if (result.isCorrect) {
        onClose();
      } else {
        // REQ-215 trigger condition 1: a submitted guess scored incorrect —
        // stay open and show the outcome view with the suggestion entry
        // point, instead of closing immediately as every other case above
        // still does.
        setScoredResult({ response: result, submittedName: trimmed });
        setShowOutcome(true);
      }
    } catch (err) {
      setError(describeError(err));
      // REQ-215 trigger condition 2: a REQ-211 live lookup for this same
      // guess timed out (503, "Live verification unavailable") — no attempt
      // was consumed and nothing was scored either way, so the form itself
      // stays exactly as it already did before this story (untouched,
      // resubmittable); this only adds the suggestion entry point alongside
      // the existing inline error.
      if (err instanceof ApiError && err.status === 503) {
        setLiveLookupUnavailableFor(trimmed);
      }
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
    const chosenCandidate = candidates?.find((candidate) => candidate.playerId === selectedCandidateId);

    setSubmitting(true);
    setError(null);
    try {
      const result = await onResolveDisambiguation(selectedCandidateId, name.trim());
      if (!result || result.isCorrect) {
        onClose();
      } else {
        // REQ-215 trigger condition 1, via the disambiguation path — same
        // outcome view as a direct incorrect submission, using the chosen
        // candidate's own canonical name (the player already confirmed
        // exactly who they meant) rather than the raw typed text.
        setCandidates(null);
        setSelectedCandidateId(null);
        setScoredResult({ response: result, submittedName: chosenCandidate?.name ?? name.trim() });
        setShowOutcome(true);
      }
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
        ) : showOutcome && scoredResult ? (
          // REQ-215 (S-089): trigger condition 1 — a submitted guess (direct
          // or via the REQ-209 disambiguation resubmission) scored
          // incorrect. Replaces the plain form the same way the candidates
          // branch above does; the header/Cancel button stay put, so
          // dismissing this via Cancel/backdrop-click is still available at
          // any point. Text-only "not a match"/attempts signal (§6: never
          // color-only), and no wording anywhere here implies this guess's
          // own score could still change (REQ-215's 2026-08-01 "no
          // retroactive rescoring" decision) — SuggestionEntry's own intro
          // text says so explicitly.
          <div className="guess-input__outcome">
            <p className="guess-input__outcome-result">
              <span className="guess-input__outcome-icon" aria-hidden="true">
                ✕
              </span>
              Not a match.
            </p>
            <p className="guess-input__outcome-hint">
              {scoredResult.response.locked
                ? 'No attempts remain for this cell.'
                : 'You can try again, or suggest a correction below.'}
            </p>
            <SuggestionEntry
              roundId={roundId}
              cellId={cell.cellId}
              accessToken={accessToken}
              playerName={scoredResult.submittedName}
              isGuest={isGuest}
            />
            <div className="guess-input__outcome-actions">
              {!scoredResult.response.locked && (
                <button
                  type="button"
                  className="guess-input__cancel"
                  onClick={() => {
                    // Returns to the plain form for a genuine second
                    // attempt, still within the same sheet — deliberately
                    // does NOT clear `scoredResult` itself, since
                    // attemptCount/locked above keep reading it as the
                    // latest known value until a fresher one replaces it.
                    setShowOutcome(false);
                    setName('');
                  }}
                >
                  Try another guess
                </button>
              )}
              <button type="button" className="guess-input__submit" onClick={onClose}>
                Close
              </button>
            </div>
          </div>
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
                    {suggestion.birthYear && (
                      <span className="guess-input__suggestion-meta">
                        {[suggestion.birthYear].filter(Boolean).join(' · ')}
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            )}
            {error && <p className="guess-input__error">{error}</p>}
            {/* REQ-215 (S-089): trigger condition 2 — a REQ-211 live lookup
                for this same guess timed out. The form itself is untouched
                (no attempt was consumed, GridScreen's own state is
                unaffected either way) — this only adds the suggestion entry
                point alongside the existing inline error above. */}
            {liveLookupUnavailableFor && (
              <SuggestionEntry
                roundId={roundId}
                cellId={cell.cellId}
                accessToken={accessToken}
                playerName={liveLookupUnavailableFor}
                isGuest={isGuest}
              />
            )}
            <button type="submit" className="guess-input__submit" disabled={submitting}>
              {submitting ? 'Submitting…' : 'Submit guess'}
            </button>
          </form>
        )}
      </div>
    </div>
  );
}
