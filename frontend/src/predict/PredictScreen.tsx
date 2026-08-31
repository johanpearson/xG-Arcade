import { useCallback, useState } from 'react';
import { confirmPredictions, fetchCurrentPredict } from '../lib/predict';
import { describeError } from '../lib/apiClient';
import { formatRoundEndTime } from '../lib/roundTime';
import { useRoundFetch } from '../lib/useRoundFetch';
import { PredictMatchInput } from './PredictMatchInput';
import { PredictConfirmDialog } from './PredictConfirmDialog';
import './PredictScreen.css';

export interface PredictScreenProps {
  accessToken: string;
  // Same contract as GridScreenProps.onAuthError/PathScreenProps.onAuthError
  // — the caller owns logging the user out, this component only reports a
  // 401 found while fetching the round.
  onAuthError: () => void;
  // No isGuest prop, unlike GridScreenProps — nothing on this screen is
  // guest-gated (same reasoning as PathScreenProps' own doc comment). No
  // onViewRoundLeaderboard prop either, unlike GridScreen/PathScreen — xG
  // Predict deliberately has no REQ-1210 completion celebration/leaderboard
  // link (see this story's own report and requirements-document.md §4.14's
  // intro note); wiring that prop in here would invite exactly the
  // REQ-1210 violation this story's instructions explicitly rule out.
}

// design-document.md SCREEN-14/REQ-1301: xG Predict's round screen shows all
// 5 matches at once — never one puzzle/cell at a time the way xG Path's
// puzzle stepper or xG Grid's per-cell reveal do. There is no per-match
// "current index" state here at all, unlike PathScreen.tsx's puzzleIndex,
// because REQ-1301's whole point is "a round feels like one coherent slate."
export function PredictScreen({ accessToken, onAuthError }: PredictScreenProps) {
  const { state, setState } = useRoundFetch(accessToken, fetchCurrentPredict, onAuthError);

  // REQ-1306: gates PredictConfirmDialog. Opened only by the explicit
  // "Confirm and lock my predictions" button, itself only ever rendered once
  // every one of the round's 5 matches already has a stored prediction
  // (allFilled below) and neither lock already applies.
  const [confirmDialogOpen, setConfirmDialogOpen] = useState(false);
  const [confirming, setConfirming] = useState(false);
  // REQ-1306: a confirm attempt that itself fails (e.g. a race where the
  // round locked between this screen loading and the click) — surfaced
  // plainly, distinct from any one match's own save error.
  const [confirmError, setConfirmError] = useState<string | null>(null);

  // Shared by refetchAfterLock and handleConfirm below — both need the exact
  // same "re-fetch, then apply the same ready/empty LoadState PathScreen's
  // own re-fetch idiom already establishes" shape, recomputing roundEndTime
  // fresh (formatRoundEndTime's own doc comment: computed once per fetch,
  // never reused stale across a later one).
  const applyFreshRound = useCallback(async () => {
    const fresh = await fetchCurrentPredict(accessToken);
    setState(
      fresh
        ? { phase: 'ready', round: fresh, roundEndTime: formatRoundEndTime(fresh.endTime, new Date()) }
        : { phase: 'empty' },
    );
  }, [accessToken, setState]);

  // REQ-1302/1303/1306: called by any PredictMatchInput row the instant a
  // save comes back 409 — re-fetches the whole round so every row's
  // disabled/locked treatment reflects the server's authoritative
  // locked/confirmedLocked flags immediately, not just the row that happened
  // to notice first.
  const refetchAfterLock = useCallback(() => {
    applyFreshRound().catch(() => {
      // Best-effort refresh only — if this itself fails, each row's own
      // next save attempt will surface the same 409 again and retry this.
    });
  }, [applyFreshRound]);

  // REQ-1302: updates this screen's own copy of one match's stored
  // prediction from a successful save response, without a full re-fetch —
  // see SubmitPredictionResponse's own doc comment in types.ts for why this
  // mirrors GridScreen's apply-from-response idiom rather than PathScreen's
  // always-re-fetch one.
  const handleMatchSaved = useCallback(
    (matchId: string, homeGoals: number, awayGoals: number) => {
      setState((current) => {
        if (current.phase !== 'ready') return current;
        return {
          ...current,
          round: {
            ...current.round,
            matches: current.round.matches.map((match) =>
              match.matchId === matchId ? { ...match, homeGoals, awayGoals } : match,
            ),
          },
        };
      });
    },
    [setState],
  );

  async function handleConfirm() {
    setConfirming(true);
    setConfirmError(null);
    try {
      await confirmPredictions(accessToken);
      setConfirmDialogOpen(false);
      // REQ-1306: re-fetching GET /predict/current (rather than assuming
      // confirmedLocked=true locally) is the same "let the server's response
      // be the source of truth" idiom PathScreen.handleSubmitGuess already
      // establishes for this codebase's post-mutation refresh pattern.
      await applyFreshRound();
    } catch (error) {
      setConfirmDialogOpen(false);
      setConfirmError(describeError(error));
    } finally {
      setConfirming(false);
    }
  }

  if (state.phase === 'loading') {
    return <p className="predict-screen__status">Loading this round…</p>;
  }

  if (state.phase === 'error') {
    return <p className="predict-screen__status predict-screen__status--error">{state.message}</p>;
  }

  // design-document.md §5: "empty states are invitations" — same calm,
  // non-error empty state Grid/Path already use, reworded for xG Predict's
  // own vocabulary.
  if (state.phase === 'empty') {
    return (
      <div className="predict-screen__empty">
        <h2>No round to predict right now</h2>
        <p>The next round is on its way — check back soon.</p>
      </div>
    );
  }

  const round = state.round;
  const locked = round.locked;
  const confirmedLocked = round.confirmedLocked;
  const anyLock = locked || confirmedLocked;
  const allFilled = round.matches.every((match) => match.homeGoals !== null && match.awayGoals !== null);
  const canOfferConfirm = allFilled && !anyLock;

  return (
    <div className="predict-screen">
      <div className="predict-screen__header">
        <h2>xG Predict</h2>
        <p className="predict-screen__subtitle">Predict the final score of all 5 matches.</p>
      </div>

      {/* §6: locked states are always stated in words, never color-only —
          each of REQ-1303/1306's two independent locks gets its own plain
          sentence, shown independently since either can be true without the
          other. */}
      {locked && (
        <p className="predict-screen__lock-notice" data-testid="predict-round-locked-notice">
          This round has locked — the first match has kicked off. Predictions can no longer be
          changed.
        </p>
      )}
      {confirmedLocked && (
        <p className="predict-screen__lock-notice" data-testid="predict-confirmed-locked-notice">
          You&apos;ve confirmed and locked your predictions for this round.
        </p>
      )}

      <div className="predict-screen__matches">
        {round.matches.map((match) => (
          <PredictMatchInput
            key={match.matchId}
            match={match}
            accessToken={accessToken}
            disabled={anyLock}
            onSaved={handleMatchSaved}
            onLockDetected={refetchAfterLock}
          />
        ))}
      </div>

      {canOfferConfirm && (
        <button
          type="button"
          className="predict-screen__confirm-button"
          onClick={() => setConfirmDialogOpen(true)}
        >
          Confirm and lock my predictions
        </button>
      )}

      {confirmError && <p className="predict-screen__confirm-error">{confirmError}</p>}

      {confirmDialogOpen && (
        <PredictConfirmDialog
          confirming={confirming}
          onCancel={() => setConfirmDialogOpen(false)}
          onConfirm={handleConfirm}
        />
      )}
    </div>
  );
}
