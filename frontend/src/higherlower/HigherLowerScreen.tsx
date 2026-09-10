import { useCallback, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import {
  fetchCurrentHigherLower,
  higherLowerCategoryLabel,
  higherLowerValueLabel,
  submitHigherLowerGuess,
} from '../lib/higherLower';
import { HigherLowerDirection } from '../lib/types';
import type { CurrentHigherLowerResponse, SubmitHigherLowerGuessResponse } from '../lib/types';
import { formatRoundEndTime, formatRoundEndTimeAccessibleLabel } from '../lib/roundTime';
import { computeRoundCompletion, useCompletionTransition, type CompletableItem } from '../lib/roundCompletion';
import { useRoundFetch } from '../lib/useRoundFetch';
import { XG_HIGHER_LOWER_GAME_KEY } from '../games/GameSelectScreen';
import type { LeaderboardRoundTarget } from '../leaderboard/LeaderboardScreen';
import { RoundCompletionBanner } from '../components/RoundCompletionBanner';
import './HigherLowerScreen.css';

export interface HigherLowerScreenProps {
  accessToken: string;
  // Called when the round fetch itself finds the token invalid (401) — same
  // contract as GridScreenProps.onAuthError/PathScreenProps.onAuthError (the
  // caller owns logging the user out, this component only reports it).
  onAuthError: () => void;
  // No isGuest prop, mirroring PathScreenProps/PredictScreenProps: nothing
  // on this screen is guest-gated — REQ-1504/1505's Given/When/Then text has
  // no own-vs-guest distinction anywhere, and there is no REQ-215-style
  // suggestion entry point here either (a Higher/Lower guess is a fixed
  // two-choice action, not free text), so there's nothing here for an
  // isGuest prop to control.
  // REQ-1210/ADR-0083: same contract as GridScreenProps/PathScreenProps'
  // onViewRoundLeaderboard — see that prop's own doc comment. Optional so
  // test call sites that never complete a full round don't need updating
  // just to satisfy this prop.
  onViewRoundLeaderboard?: (target: LeaderboardRoundTarget) => void;
}

// REQ-1210/ADR-0083, design-document.md SCREEN-18's own "diverging from
// SCREEN-14" status note: unlike xG Predict (whose grading is asynchronous
// and explicitly excluded from this banner), an xG Higher/Lower attempt's
// `HasEnded` is a synchronous, immediate "you just finished" moment — the
// same shape Grid/Path already have. A single-item array is deliberate, not
// a simplification of a general case: there is exactly one attempt per
// round for this game (no per-cell/per-puzzle/per-match list to map over),
// so "every item locked" collapses to "this one attempt has ended."
function toCompletableItem(round: CurrentHigherLowerResponse): CompletableItem {
  return { locked: round.hasEnded, points: round.hasEnded ? round.streakLength : null };
}

// design-document.md SCREEN-18: baseline (value revealed) + next comparator
// (identity only) + an explicit Higher/Lower choice, structurally closer to
// a single always-current PathScreen puzzle (one current comparison at a
// time) than to PredictScreen's whole-slate-at-once layout — see that
// SCREEN entry for the full wireframe and the judgment calls recorded
// alongside it (the completion-banner decision above, the reused shake cue,
// and why the terminal-incorrect reveal is sourced from the POST response
// rather than a refetch).
export function HigherLowerScreen({ accessToken, onAuthError, onViewRoundLeaderboard }: HigherLowerScreenProps) {
  const { state, setState, checkRoundStillLive } = useRoundFetch(accessToken, fetchCurrentHigherLower, onAuthError);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // design-document.md SCREEN-18 (judgment call, flagged in that section):
  // unlike PathScreen's always-refetch idiom, GET /higher-lower/current's
  // `baseline` does NOT change after an incorrect guess — the backend never
  // advances CurrentBaselinePlayerId/CurrentBaselineValue in that case
  // (XGHigherLowerGameModule.ScoreSubmissionAsync's own "the baseline does
  // not advance" comment) — so the just-revealed WRONG comparator's own
  // identity/value would be lost entirely if this screen relied on a
  // refetch alone. This holds the POST response directly instead, which
  // always carries the reveal (correct or incorrect, REQ-1504). Cleared at
  // the start of every new guess so it never shows a stale outcome once a
  // fresh one is in flight; also naturally absent on a fresh page load of an
  // already-ended round (a real, accepted gap — see the design doc's own
  // note on this).
  const [lastGuessResult, setLastGuessResult] = useState<SubmitHigherLowerGuessResponse | null>(null);

  const completion = state.phase === 'ready' ? computeRoundCompletion([toCompletableItem(state.round)]) : null;
  const justCompletedRound = useCompletionTransition(completion ? completion.isComplete : null);
  const [completionBannerDismissed, setCompletionBannerDismissed] = useState(false);
  const [checkingLeaderboardTarget, setCheckingLeaderboardTarget] = useState(false);

  // REQ-1210: mirrors GridScreen.tsx's/PathScreen.tsx's
  // handleViewCompletedRoundLeaderboard exactly (see either's own doc
  // comment for the full reasoning) — re-asks GET /higher-lower/current
  // (already used by this screen) rather than a new endpoint to decide
  // 'live' vs 'past'.
  const handleViewCompletedRoundLeaderboard = useCallback(async () => {
    if (state.phase !== 'ready' || !onViewRoundLeaderboard) return;
    const roundId = state.round.roundId;
    setCheckingLeaderboardTarget(true);
    const scope = await checkRoundStillLive(roundId);
    setCheckingLeaderboardTarget(false);
    onViewRoundLeaderboard({ gameKey: XG_HIGHER_LOWER_GAME_KEY, scope, roundId });
  }, [state, onViewRoundLeaderboard, checkRoundStillLive]);

  // REQ-1504: submits one Higher/Lower guess, then re-fetches
  // GET /higher-lower/current to pick up the newly-revealed next comparator
  // (a correct, non-terminal guess needs a fresh next-comparator identity
  // the POST response itself never carries — only the instance's own fixed
  // sequence knows it, and the frontend is never handed the whole sequence
  // at once, REQ-1504's "value/identity hidden until guessed" contract).
  // Mirrors PathScreen.tsx's handleSubmitGuess two-call shape (submit, then
  // refetch) and its "a refetch failure must never look like the guess
  // itself failed" discipline — REQ-1504's attempt-advance already happened
  // server-side by the time the refetch could fail.
  const handleGuess = useCallback(
    async (direction: HigherLowerDirection) => {
      if (state.phase !== 'ready' || submitting) return;

      setSubmitting(true);
      setError(null);
      setLastGuessResult(null);

      try {
        const result = await submitHigherLowerGuess(accessToken, direction);
        setLastGuessResult(result);

        try {
          const fresh = await fetchCurrentHigherLower(accessToken);
          if (fresh) {
            setState({ phase: 'ready', round: fresh, roundEndTime: formatRoundEndTime(fresh.endTime, new Date()) });
          } else {
            // The round closed between the submit and this re-fetch — same
            // "don't leave stale round state on screen" handling
            // PathScreen.tsx's own post-guess re-fetch already establishes.
            setState({ phase: 'empty' });
          }
        } catch {
          setError("Guess submitted, but couldn't refresh — try reloading this screen.");
        }
      } catch (err) {
        // REQ-1504's last Given/When/Then block: a guess against an
        // already-ended attempt is rejected (409) — reachable here only via
        // a genuine race (e.g. two tabs/devices for the same account), since
        // this screen's own `submitting`/`hasEnded`-gated buttons prevent an
        // ordinary double-submit. Re-syncs from the server rather than
        // leaving this screen's own (now provably stale) idea of "still
        // active" on screen.
        if (err instanceof ApiError && err.status === 409) {
          setError('This attempt has already ended — refreshing the latest state.');
          try {
            const fresh = await fetchCurrentHigherLower(accessToken);
            setState(
              fresh
                ? { phase: 'ready', round: fresh, roundEndTime: formatRoundEndTime(fresh.endTime, new Date()) }
                : { phase: 'empty' },
            );
          } catch {
            // Best-effort resync only — the 409 message above already told
            // the player what happened; a second failed request here must
            // not overwrite that with a more confusing one.
          }
        } else {
          setError(describeError(err));
        }
      } finally {
        setSubmitting(false);
      }
    },
    [accessToken, state, submitting, setState],
  );

  if (state.phase === 'loading') {
    return <p className="higher-lower-screen__status">Loading this round…</p>;
  }

  if (state.phase === 'error') {
    return <p className="higher-lower-screen__status higher-lower-screen__status--error">{state.message}</p>;
  }

  // design-document.md §5: "empty states are invitations" — same calm,
  // non-error empty state GridScreen/PathScreen/PredictScreen already use,
  // reworded for this game's own vocabulary.
  if (state.phase === 'empty') {
    return (
      <div className="higher-lower-screen__empty">
        <h2>No round to play right now</h2>
        <p>The next round is on its way — check back soon.</p>
      </div>
    );
  }

  const round = state.round;
  const roundEndTime = state.roundEndTime;

  // REQ-1508: Baseline card content, shared between the side-by-side
  // (non-terminal) and standalone (terminal) layouts below rather than
  // duplicated — only the wrapper around it differs per the JSX branches
  // further down.
  const baselineCard = (
    <div className="higher-lower-screen__card higher-lower-screen__card--baseline">
      <p className="higher-lower-screen__card-label">Baseline</p>
      <div className="higher-lower-screen__player-row">
        <HigherLowerPlayerPhoto key={round.baseline.playerId} photoUrl={round.baseline.photoUrl} />
        <p className="higher-lower-screen__player-name">{round.baseline.name}</p>
      </div>
      <p className="higher-lower-screen__player-value mono-figure">
        {higherLowerValueLabel(round.statCategory, round.baseline.value)}
      </p>
    </div>
  );

  return (
    <div className="higher-lower-screen">
      <div className="higher-lower-screen__header">
        <div className="higher-lower-screen__title-row">
          <h2>xG Higher/Lower</h2>
          {/* REQ-303: mirrors GridScreen.tsx's/PathScreen.tsx's end-time
              indicator exactly — see either file's own comment for the full
              rationale (a relative-duration signal computed once at
              fetch-success time, never a live/ticking countdown; reachable
              by keyboard/screen reader via tabIndex+aria-label, and by a
              sighted mouse user via the native `title` tooltip). */}
          <span
            className="higher-lower-screen__end-time mono-figure"
            tabIndex={0}
            title={`Round ends ${roundEndTime.absoluteLabel}`}
            aria-label={formatRoundEndTimeAccessibleLabel(roundEndTime)}
          >
            {roundEndTime.text}
          </span>
        </div>
        {/* design-document.md §2: any number meant to be compared at a
            glance is always mono/tabular — a streak count is exactly that,
            same treatment as "Puzzle N of M"/"Clue N of M" elsewhere in this
            app. `comparatorCount` is this Round's own fixed sequence length
            (REQ-1502/1503), never a fixed number across rounds. */}
        <p className="higher-lower-screen__meta mono-figure">
          Streak {round.streakLength} of {round.comparatorCount}
        </p>
        <p className="higher-lower-screen__category">Comparing {higherLowerCategoryLabel(round.statCategory)}</p>
      </div>

      {/* REQ-1210/ADR-0083/design-document.md SCREEN-12 (SCREEN-18's own
          diverging-from-SCREEN-14 note): same inline, non-blocking banner
          GridScreen.tsx/PathScreen.tsx render. Plain "N pts" wording
          (mirroring xG Path's REQ-1206 convention, not xG Grid's
          "~estimated" one) — a streak's FinalPoints is exactly the streak
          length reached, known and unchanging the instant `hasEnded`
          becomes true, never a provisional value another player's own guess
          could still shift. */}
      {justCompletedRound && !completionBannerDismissed && completion && onViewRoundLeaderboard && (
        <RoundCompletionBanner
          pointsText={`${completion.currentPoints} pts`}
          onViewLeaderboard={handleViewCompletedRoundLeaderboard}
          viewLeaderboardDisabled={checkingLeaderboardTarget}
          onDismiss={() => setCompletionBannerDismissed(true)}
        />
      )}

      {/* §6: outcome is always stated in text, never color-only — the word
          "Correct"/"Incorrect" carries the meaning, the gold/red color is
          reinforcement, not the only signal. Gold for correct (§2's
          "settled/correct" token, same as a locked-correct grid cell/solved
          Path puzzle), red for incorrect — deliberately NOT
          accent-green(-text), which this codebase reserves for a
          live/active or merely-saved action, not a correctness signal (see
          PredictMatchInput.css's own comment on that exact distinction). */}
      {lastGuessResult && (
        <p
          className={`higher-lower-screen__outcome ${
            lastGuessResult.isCorrect
              ? 'higher-lower-screen__outcome--correct'
              : 'higher-lower-screen__outcome--incorrect'
          }`}
          // aria-live (not role="status"): RoundCompletionBanner.tsx already
          // owns role="status" and can render at the same moment this does
          // (a terminal guess), so a second status-role region here would
          // make `getByRole('status')` ambiguous for any caller/test and,
          // for a screen-reader user, announce two competing "status"
          // regions for one event. A plain polite live-region announcement
          // is all this needs.
          aria-live="polite"
        >
          {/* REQ-1508: the just-guessed comparator's photo, confirming their
              revealed identity — always visible whenever present, same
              graceful name-only fallback as the two cards below. Keyed on
              revealedPlayerId so a second guess (a fresh player, a fresh
              photoUrl or none at all) gets its own mount and its own
              load-failure state rather than inheriting the previous guess's
              outcome-photo failure. */}
          <HigherLowerPlayerPhoto
            key={lastGuessResult.revealedPlayerId}
            photoUrl={lastGuessResult.revealedPlayerPhotoUrl}
          />
          {lastGuessResult.isCorrect ? 'Correct.' : 'Incorrect.'} {lastGuessResult.revealedPlayerName}:{' '}
          <span className="mono-figure">
            {higherLowerValueLabel(round.statCategory, lastGuessResult.revealedValue)}
          </span>
        </p>
      )}

      {round.hasEnded ? (
        // REQ-1508's own terminal-state carve-out: no Next card exists to
        // place side by side, so this branch is visually unchanged from
        // before this REQ — the Baseline card renders standalone, exactly
        // as it always has.
        <>
          {baselineCard}
          <p className="higher-lower-screen__complete">You&rsquo;ve completed this round.</p>
        </>
      ) : round.nextComparator ? (
        <>
          {/* REQ-1508: Baseline (left) and Next (right) side by side, at
              every viewport width — not gated to a breakpoint, since the
              REQ itself specifies this unconditionally. No swipe/gesture
              handler anywhere here — considered and explicitly declined for
              this iteration (REQ-1508's own scope note); both cards are
              simply laid out in a two-column flex row. */}
          <div className="higher-lower-screen__cards">
            {baselineCard}
            {/* REQ-1504: identity only — deliberately no value/placeholder
                rendered here at all, not even a "?" glyph, matching the
                backend DTO's own "no Value field on the wire, not just
                withheld" enforcement (HigherLowerNextComparator has no
                value field to render even if this component wanted to). */}
            <div className="higher-lower-screen__card higher-lower-screen__card--next">
              <p className="higher-lower-screen__card-label">Next</p>
              <div className="higher-lower-screen__player-row">
                <HigherLowerPlayerPhoto key={round.nextComparator.playerId} photoUrl={round.nextComparator.photoUrl} />
                <p className="higher-lower-screen__player-name">{round.nextComparator.name}</p>
              </div>
            </div>
          </div>

          <div className="higher-lower-screen__actions">
            <button
              type="button"
              className="higher-lower-screen__guess-button"
              onClick={() => handleGuess(HigherLowerDirection.Higher)}
              disabled={submitting}
            >
              Higher
            </button>
            <button
              type="button"
              className="higher-lower-screen__guess-button"
              onClick={() => handleGuess(HigherLowerDirection.Lower)}
              disabled={submitting}
            >
              Lower
            </button>
          </div>
        </>
      ) : (
        // Defensive fallback only — the backend contract guarantees
        // `nextComparator` is non-null whenever `hasEnded` is false
        // (types.ts's own doc comment), so this branch shouldn't be
        // reachable in practice. Still renders the Baseline card alone
        // rather than nothing, mirroring this component's pre-REQ-1508
        // behavior of always rendering it unconditionally.
        baselineCard
      )}

      {error && <p className="higher-lower-screen__error">{error}</p>}
    </div>
  );
}

// REQ-1508 (S-233): a small, decorative player-photo slot reused on the
// Baseline card, the Next card, and the post-guess outcome line — three
// independent instances (each call site above passes its own `key`, so each
// gets its own mount and its own `failed` state; a load failure on one can
// never affect another, the same independence CellState.tsx's own
// photoFailed/incorrectMatchedPhotoFailed pair already documents for its two
// photo slots). Renders nothing at all (not a broken-image icon, not a
// placeholder silhouette — REQ-1508 deliberately mirrors REQ-214's
// name-only fallback, not REQ-216's placeholder-avatar one, since there is
// no "guess submitted, but wrong" state on this screen for a placeholder to
// signal) whenever `photoUrl` is absent or a load already failed this
// mount. `alt=""`/`aria-hidden="true"`: decorative only, matching
// CellPhoto's/PlayerAvatar's own convention — the adjacent name text is what
// carries this player's accessible identity (§6). 64x64px reuses the
// existing avatar-thumbnail dimension PlayerAvatar.tsx/design-document.md
// SCREEN-08 already document, rather than inventing a new size for this
// card context.
function HigherLowerPlayerPhoto({ photoUrl }: { photoUrl?: string | null }) {
  const [failed, setFailed] = useState(false);
  if (!photoUrl || failed) return null;
  return (
    <img
      className="higher-lower-screen__player-photo"
      src={photoUrl}
      alt=""
      aria-hidden="true"
      onError={() => setFailed(true)}
    />
  );
}
