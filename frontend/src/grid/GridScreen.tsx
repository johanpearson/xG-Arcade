import { useCallback, useEffect, useState } from 'react';
import { ApiError, describeError, fetchCurrentRound, submitGuess } from '../lib/api';
import type { CurrentRoundCell, CurrentRoundResponse } from '../lib/types';
import { Grid } from './Grid';
import { GuessInput } from './GuessInput';
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
  // Player names guessed this session — GET /rounds/current doesn't return
  // the guessed/answer name, only correctness (see CellState.tsx's comment
  // on this gap).
  const [knownPlayerNames, setKnownPlayerNames] = useState<Record<string, string>>({});

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
      // POST .../guesses' own response doesn't echo the submitted name back
      // (SubmitGuessResponse only has isCorrect/attemptCount/locked) —
      // filled in from what was just typed, same source knownPlayerNames uses.
      const guess = { ...result, submittedName };

      setKnownPlayerNames((prev) => ({ ...prev, [cellId]: submittedName }));
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

  return (
    <div className="grid-screen">
      <div className="grid-screen__header">
        <h2>Current round</h2>
        <p className="grid-screen__progress mono-figure">
          {answeredCount}/{state.round.cells.length} answered
        </p>
      </div>
      <Grid
        cells={state.round.cells}
        roundStatus={ROUND_STATUS}
        knownPlayerNames={knownPlayerNames}
        onCellClick={setActiveCell}
      />
      {activeCell && (
        <GuessInput
          cell={activeCell}
          onSubmit={handleSubmitGuess}
          onClose={() => setActiveCell(null)}
        />
      )}
    </div>
  );
}
