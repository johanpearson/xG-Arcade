import { useCallback, useEffect, useState } from 'react';
import { ApiError, describeError, fetchCurrentRound, submitGuess } from '../lib/api';
import type { CurrentRoundCell, CurrentRoundResponse } from '../lib/types';
import { MAX_POINTS_PER_CELL } from '../lib/scoringRules';
import { Grid } from './Grid';
import { GuessInput } from './GuessInput';
import { ScoringExplainer } from './ScoringExplainer';
import './GridScreen.css';

export interface GridScreenProps {
  accessToken: string;
  // Called when the round fetch itself finds the token invalid (401) — the
  // caller owns logging the user out, GridScreen only reports it.
  onAuthError: () => void;
}

type LoadState =
  | { phase: 'loading' }
  | { phase: 'empty' }
  | { phase: 'error'; message: string }
  | { phase: 'ready'; round: CurrentRoundResponse };

// GET /rounds/current only ever returns an Active round today (round-close
// is S-011 scope) — so roundStatus is always "active" here. SCREEN-01a's
// "closed" state is exercised via CellState's own props/test instead.
const ROUND_STATUS = 'active' as const;

export function GridScreen({ accessToken, onAuthError }: GridScreenProps) {
  const [state, setState] = useState<LoadState>({ phase: 'loading' });
  const [activeCell, setActiveCell] = useState<CurrentRoundCell | null>(null);
  // REQ-213 (S-041): independent of activeCell/GuessInput on purpose — an
  // open guess-input sheet must stay untouched if the player also opens
  // this, and vice versa (see SCREEN-06's "doesn't discard in-progress
  // state" requirement).
  const [explainerOpen, setExplainerOpen] = useState(false);
  // S-020: cellIds whose guess was submitted in this browser session — see
  // GridCell's own doc comment. GET /rounds/current already returns a
  // correct guess's canonical resolvedPlayerName directly (no client-side
  // name cache needed for that), so this set exists only to distinguish
  // "just submitted" from "loaded via page reload" for the shake cue.
  const [submittedThisSessionCellIds, setSubmittedThisSessionCellIds] = useState<ReadonlySet<string>>(new Set());

  useEffect(() => {
    let cancelled = false;

    fetchCurrentRound(accessToken)
      .then((round) => {
        if (cancelled) return;
        setState(round ? { phase: 'ready', round } : { phase: 'empty' });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        if (error instanceof ApiError && error.status === 401) {
          onAuthError();
          return;
        }
        setState({ phase: 'error', message: describeError(error) });
      });

    return () => {
      cancelled = true;
    };
  }, [accessToken, onAuthError]);

  const handleSubmitGuess = useCallback(
    async (submittedName: string) => {
      if (!activeCell || state.phase !== 'ready') return;
      const cellId = activeCell.cellId;
      const roundId = state.round.roundId;

      const result = await submitGuess(accessToken, roundId, cellId, submittedName);
      // uniquePercent/livePoints aren't in the submit response (REQ-204 is a
      // read-time calculation, GET /rounds/current's job, not the write
      // response's) — null here is accurate for this instant; the next
      // fetchCurrentRound (e.g. a reload) picks up the real live values.
      // resolvedPlayerName IS in the submit response (frontend name-display
      // fix) — the canonical name for a correct guess, never the raw
      // submittedName.
      const guess = { ...result, submittedName, uniquePercent: null, livePoints: null };

      setSubmittedThisSessionCellIds((prev) => new Set(prev).add(cellId));
      setState((prev) => {
        if (prev.phase !== 'ready') return prev;
        return {
          ...prev,
          round: {
            ...prev.round,
            cells: prev.round.cells.map((cell) =>
              cell.cellId === cellId ? { ...cell, guess } : cell,
            ),
          },
        };
      });
    },
    [accessToken, activeCell, state],
  );

  if (state.phase === 'loading') {
    return <p className="grid-screen__status">Loading this round…</p>;
  }

  if (state.phase === 'error') {
    return <p className="grid-screen__status grid-screen__status--error">{state.message}</p>;
  }

  // design-document.md §5: "empty states are invitations" — a calm, real
  // empty state, not an error screen (REQ: no active round to play).
  if (state.phase === 'empty') {
    return (
      <div className="grid-screen__empty">
        <h2>No round to play right now</h2>
        <p>The next round is on its way — check back soon.</p>
      </div>
    );
  }

  const answeredCount = state.round.cells.filter((cell) => cell.guess !== null).length;

  // REQ-206: the round's running total, shown here since there's otherwise
  // nowhere a player can see it before the round closes and the leaderboard
  // picks it up. Same "~N pts estimated" wording REQ-204/S-018 already
  // established for a single cell's live point value — this is that same
  // provisional value, just summed, never a promise of the locked total
  // REQ-205 computes at round close.
  //
  // S-033 bugfix: a locked-incorrect cell (guess.locked && !isCorrect) is
  // guaranteed to lock at MaxPointsPerCell (ADR-0021's golf-scoring worst
  // case) — that's a known constant, not something still waiting on a live
  // computation the way a correct guess's livePoints is, so omitting it
  // from this total (as the previous version did) understated it, not just
  // left it incomplete. A correct guess without livePoints yet (submitted
  // this instant, GET /rounds/current not yet re-fetched) is still
  // genuinely unknown and stays excluded, same as before.
  const totalKnownPoints = state.round.cells.reduce((sum, cell) => {
    if (!cell.guess) return sum;
    if (cell.guess.isCorrect) return cell.guess.livePoints != null ? sum + cell.guess.livePoints : sum;
    return cell.guess.locked ? sum + MAX_POINTS_PER_CELL : sum;
  }, 0);
  const anyPointsKnown = state.round.cells.some(
    (cell) => cell.guess != null && (cell.guess.isCorrect ? cell.guess.livePoints != null : cell.guess.locked),
  );

  return (
    <div className="grid-screen">
      <div className="grid-screen__header">
        <div className="grid-screen__title-row">
          <h2>Current round</h2>
          {/* REQ-213 (S-041): opens SCREEN-06's general scoring/live-updates
              explainer — reachable at any time an active round is shown,
              not gated behind attempting any particular cell. */}
          <button
            type="button"
            className="grid-screen__info-toggle"
            onClick={() => setExplainerOpen(true)}
            aria-label="How scoring works"
          >
            ⓘ
          </button>
        </div>
        <div className="grid-screen__header-stats">
          <p className="grid-screen__progress mono-figure">
            {answeredCount}/{state.round.cells.length} answered
          </p>
          {anyPointsKnown && (
            <p className="grid-screen__total mono-figure">~{totalKnownPoints} pts estimated</p>
          )}
        </div>
      </div>
      <Grid
        cells={state.round.cells}
        roundStatus={ROUND_STATUS}
        submittedThisSessionCellIds={submittedThisSessionCellIds}
        onCellClick={setActiveCell}
      />
      {activeCell && (
        <GuessInput
          cell={activeCell}
          onSubmit={handleSubmitGuess}
          onClose={() => setActiveCell(null)}
        />
      )}
      {explainerOpen && <ScoringExplainer onClose={() => setExplainerOpen(false)} />}
    </div>
  );
}
