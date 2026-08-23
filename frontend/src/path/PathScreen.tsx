import { useCallback, useState } from 'react';
import { fetchCurrentPath } from '../lib/path';
import { submitGuess } from '../lib/rounds';
import type { CurrentPathGuess, CurrentPathResponse } from '../lib/types';
import { formatRoundEndTime, formatRoundEndTimeAccessibleLabel } from '../lib/roundTime';
import { computeRoundCompletion, useCompletionTransition, type CompletableItem } from '../lib/roundCompletion';
import { useRoundFetch, useAutocompleteWarmup } from '../lib/useRoundFetch';
import { XG_PATH_GAME_KEY } from '../games/GameSelectScreen';
import type { LeaderboardRoundTarget } from '../leaderboard/LeaderboardScreen';
import { RoundCompletionBanner } from '../components/RoundCompletionBanner';
import { PathGuessInput } from './PathGuessInput';
import { PathScoringExplainer } from './PathScoringExplainer';
import { PathTimeline } from './PathTimeline';
import './PathScreen.css';

export interface PathScreenProps {
  accessToken: string;
  // Called when the round fetch itself finds the token invalid (401) — same
  // contract as GridScreenProps.onAuthError (the caller owns logging the
  // user out, this component only reports it).
  onAuthError: () => void;
  // No isGuest prop, unlike GridScreenProps: nothing on this screen is
  // guest-gated — SCREEN-10 has no REQ-215 suggestion entry point (out of
  // this story's scope, see PathGuessInput.tsx's own comment) and no other
  // guest-specific behavior, so there's nothing here for it to control.
  // REQ-1210/ADR-0083: same contract as GridScreenProps.onViewRoundLeaderboard
  // — see that prop's own doc comment. Optional so existing test call sites
  // that never complete a full round don't need updating just to satisfy
  // this prop.
  onViewRoundLeaderboard?: (target: LeaderboardRoundTarget) => void;
}

// REQ-1210/ADR-0083: maps one puzzle's guess into the generic
// `{ locked, points }` shape `lib/roundCompletion.ts`'s shared computation
// understands — mirrors GridScreen.tsx's own toCompletableItem, but for xG
// Path's own scoring shape (REQ-1206): a locked puzzle's `points` is
// already the final, authoritative value (never a live estimate the way
// xG Grid's `livePoints` is), so this mapping is simpler than xG Grid's own
// — no correctness branch needed, `guess.points` is only ever non-null once
// `guess.locked` is true (see CurrentPathGuess.points's own doc comment).
function toCompletableItem(guess: CurrentPathGuess | null): CompletableItem {
  return { locked: guess?.locked ?? false, points: guess?.points ?? null };
}

export function PathScreen({ accessToken, onAuthError, onViewRoundLeaderboard }: PathScreenProps) {
  const { state, setState, checkRoundStillLive } = useRoundFetch(accessToken, fetchCurrentPath, onAuthError);
  // S-086: which of the round's puzzles is currently shown — purely
  // client-side, per SCREEN-10's "'Next puzzle' is an explicit action,
  // never automatic" requirement. GET /path/current always returns every
  // puzzle in the round at once (no per-puzzle fetch), so this is just an
  // index into that same array, never re-derived from the server.
  const [puzzleIndex, setPuzzleIndex] = useState(0);
  // REQ-213 (second consumer, 2026-08-08): independent of puzzleIndex/the
  // guess flow on purpose — same reasoning as GridScreen.tsx's own
  // explainerOpen state, so opening this never discards an in-progress
  // typed-but-not-yet-submitted guess.
  const [explainerOpen, setExplainerOpen] = useState(false);
  // Quality-gate fix (S-086 follow-up): distinct from PathGuessInput's own
  // `error` state. That one means "the guess submission itself failed" (the
  // POST never landed) and is shown *instead of* a scored outcome. This one
  // means the opposite: the guess WAS recorded server-side (the POST
  // succeeded, result.isCorrect is real) but the follow-up GET /path/current
  // that picks up the newly revealed clue failed — so the player must never
  // be told their guess failed here, only that the screen couldn't refresh.
  const [refetchWarning, setRefetchWarning] = useState<string | null>(null);

  // REQ-1210/ADR-0083: the generic completion signal — reads every puzzle
  // in the round (not just the currently-shown `puzzleIndex`), since
  // "complete" means every puzzle available to this player is locked, not
  // just the one on screen. Same null-while-not-loaded-yet contract as
  // GridScreen.tsx's own `completion` (see toCompletableItem/
  // useCompletionTransition's own doc comments).
  const completion =
    state.phase === 'ready' ? computeRoundCompletion(state.round.puzzles.map((puzzle) => toCompletableItem(puzzle.guess))) : null;
  const justCompletedRound = useCompletionTransition(completion ? completion.isComplete : null);
  const [completionBannerDismissed, setCompletionBannerDismissed] = useState(false);
  const [checkingLeaderboardTarget, setCheckingLeaderboardTarget] = useState(false);

  // REQ-1210: mirrors GridScreen.tsx's handleViewCompletedRoundLeaderboard
  // exactly (see that function's own doc comment for the full reasoning) —
  // re-asks GET /path/current (already used by this screen) rather than a
  // new endpoint to decide 'live' vs 'past'.
  const handleViewCompletedRoundLeaderboard = useCallback(async () => {
    if (state.phase !== 'ready' || !onViewRoundLeaderboard) return;
    const roundId = state.round.roundId;
    setCheckingLeaderboardTarget(true);
    const scope = await checkRoundStillLive(roundId);
    setCheckingLeaderboardTarget(false);
    onViewRoundLeaderboard({ gameKey: XG_PATH_GAME_KEY, scope, roundId });
  }, [state, onViewRoundLeaderboard, checkRoundStillLive]);

  // S-151/REQ-207: fire-and-forget cold-start warm-up, independent of the
  // round-fetch mount effect inside useRoundFetch — this must never gate or
  // affect the round load's own loading/error state (see
  // warmUpAutocomplete's own comment, frontend/src/lib/rounds.ts). Same call
  // as GridScreen.tsx's own — xG Path shares the same
  // PlayerNameIndex-backed autocomplete path (PathGuessInput.tsx), so it
  // needs the same warm-up.
  useAutocompleteWarmup(accessToken);

  // REQ-1203/1204 (S-086): xG Path's POST .../guesses response
  // (SubmitGuessResponse) carries isCorrect/attemptCount/locked but no clue
  // data at all — unlike GridScreen's handleSubmitGuess, which patches cell
  // state directly from that response, the only way to pick up the newly
  // revealed clue turn (or, for a correct guess, the frozen final turn plus
  // resolved name/photo) is a follow-up GET /path/current. This is exactly
  // what that endpoint's own doc comment means by "this response IS the
  // revealed-so-far state, no separate reveal endpoint" — the client re-asks
  // for it rather than the server pushing a delta. Resolves to whether the
  // guess was correct (PathGuessInput's own shake-cue trigger); throws on a
  // genuine request failure, same as GridScreen's onSubmit contract.
  const handleSubmitGuess = useCallback(
    async (submittedName: string): Promise<boolean> => {
      if (state.phase !== 'ready') return false;
      const puzzle = state.round.puzzles[puzzleIndex];
      if (!puzzle) return false;

      // Left to throw/propagate to PathGuessInput's own catch block — a
      // genuine submission failure (the POST never landed, no attempt was
      // consumed) is exactly what that error path is for.
      const result = await submitGuess(accessToken, state.round.roundId, puzzle.puzzleId, submittedName);

      // Quality-gate fix (S-086 follow-up): the guess above already
      // succeeded — REQ-1205's attempt cap was already consumed server-side
      // — so a failure in this follow-up GET must never surface as if the
      // whole submission failed (that would make a player retry and burn a
      // second attempt against the fixed 7-attempt cap without realizing one
      // was already spent). Handled separately from the submitGuess call
      // above, with its own try/catch, rather than letting it throw into the
      // same catch block PathGuessInput uses for a real submission failure.
      setRefetchWarning(null);
      let fresh: CurrentPathResponse | null;
      try {
        fresh = await fetchCurrentPath(accessToken);
      } catch {
        setRefetchWarning("Guess submitted, but couldn't refresh — try reloading this screen.");
        return result.isCorrect;
      }

      if (fresh) {
        setState({ phase: 'ready', round: fresh, roundEndTime: formatRoundEndTime(fresh.endTime, new Date()) });
      } else {
        // The round closed between the submit and this re-fetch — treat it
        // the same as any other "no active round" case rather than leaving
        // the stale pre-guess puzzle on screen indefinitely.
        setState({ phase: 'empty' });
      }

      return result.isCorrect;
    },
    [accessToken, state, puzzleIndex, setState],
  );

  if (state.phase === 'loading') {
    return <p className="path-screen__status">Loading this round…</p>;
  }

  if (state.phase === 'error') {
    return <p className="path-screen__status path-screen__status--error">{state.message}</p>;
  }

  // design-document.md §5: "empty states are invitations" — same calm,
  // non-error empty state GridScreen already uses for "no active round,"
  // reworded for xG Path's own puzzle vocabulary (no distinct SCREEN-10
  // empty-state copy is specified, so this reuses the established voice
  // rather than inventing new wording).
  if (state.phase === 'empty') {
    return (
      <div className="path-screen__empty">
        <h2>No puzzle to play right now</h2>
        <p>The next round is on its way — check back soon.</p>
      </div>
    );
  }

  const puzzles = state.round.puzzles;
  // Defensive clamp only — puzzles.length is fixed for the lifetime of a
  // round (REQ-1201: 3-5 puzzles generated once at round creation), so
  // puzzleIndex should never actually exceed it once initialized at 0.
  const clampedPuzzleIndex = Math.min(puzzleIndex, puzzles.length - 1);
  const puzzle = puzzles[clampedPuzzleIndex];
  const isLastPuzzle = clampedPuzzleIndex === puzzles.length - 1;
  const solved = puzzle.guess?.isCorrect ?? false;
  // REQ-1205 judgment call (flagged, not literal SCREEN-10 text): the
  // design doc only describes "Next puzzle" appearing once solved. A puzzle
  // that instead exhausts its fixed 7-attempt cap without a correct guess
  // (REQ-1205's own "locks it as unsolved" case) is an equally real,
  // reachable state this screen must not silently strand the player in —
  // so "Next puzzle" is shown whenever the puzzle is locked at all (solved
  // or exhausted), not only when solved.
  const locked = puzzle.guess?.locked ?? false;

  return (
    <div className="path-screen">
      <div className="path-screen__header">
        <div className="path-screen__title-row">
          <h2>xG Path</h2>
          {/* REQ-303: mirrors GridScreen.tsx's end-time indicator exactly —
              a relative-duration signal computed once at fetch-success time
              (state.roundEndTime above), never a live/ticking countdown.
              tabIndex=0 + aria-label make the exact local end date/time
              reachable by keyboard focus and screen readers, and `title`
              additionally surfaces it as a native tooltip for a sighted
              mouse user. Text-only signal (no color-only meaning). */}
          <span
            className="path-screen__end-time mono-figure"
            tabIndex={0}
            title={`Round ends ${state.roundEndTime.absoluteLabel}`}
            aria-label={formatRoundEndTimeAccessibleLabel(state.roundEndTime)}
          >
            {state.roundEndTime.text}
          </span>
          {/* REQ-213 (second consumer, 2026-08-08): opens SCREEN-10's own
              scoring explainer — mirrors GridScreen.tsx's
              `grid-screen__info-toggle` entry point exactly (same position
              in the title row, next to the end-time indicator, same plain/
              quiet unlabeled-button treatment), but opens PathScoringExplainer
              (xG Path's own rules), not xG Grid's ScoringExplainer — see
              that component's own doc comment for why. Reachable at any
              time an active round is shown, not gated behind attempting any
              particular puzzle. */}
          <button
            type="button"
            className="path-screen__info-toggle"
            onClick={() => setExplainerOpen(true)}
            aria-label="How scoring works"
          >
            ⓘ
          </button>
        </div>
        <p className="path-screen__puzzle-position mono-figure">
          Puzzle {clampedPuzzleIndex + 1} of {puzzles.length}
        </p>
      </div>

      {/* REQ-1210/ADR-0083/design-document.md SCREEN-12: same inline,
          non-blocking banner GridScreen.tsx renders — see that file's own
          comment for why it deliberately never backdrops the rest of the
          screen. xG Path's own plain "N pts" wording (REQ-1206), never
          "estimated" (that's xG Grid's own provisional-value wording). */}
      {justCompletedRound && !completionBannerDismissed && completion && onViewRoundLeaderboard && (
        <RoundCompletionBanner
          pointsText={`${completion.currentPoints} pts`}
          onViewLeaderboard={handleViewCompletedRoundLeaderboard}
          viewLeaderboardDisabled={checkingLeaderboardTarget}
          onDismiss={() => setCompletionBannerDismissed(true)}
        />
      )}

      {/* key={`${puzzle.puzzleId}-...`}: forces a clean remount on every
          puzzle switch — both components carry their own local state
          (typed-in guess text, the shake token, which nodes have already
          animated in) that must never leak from one puzzle into the next.
          Suffixed per component (quality-gate fix, S-086 follow-up) so the
          two sibling keys are never identical — an identical key on two
          sibling elements is a real React warning ("Encountered two
          children with the same key"), not just a cosmetic duplicate. */}
      <PathTimeline
        key={`${puzzle.puzzleId}-timeline`}
        clues={puzzle.clues}
        solved={solved}
        // User-testing fix (2026-08-02): previously only `solved` was
        // passed down, so a puzzle that locked unsolved (attempt cap
        // exhausted, REQ-1205) never got any reveal at all — see
        // PathTimeline.tsx's own comment on FailedRevealNode for the fix.
        // `locked` is already computed above for the "Next puzzle" button's
        // own gating; this reuses that exact same value, not a second
        // derivation.
        locked={locked}
        resolvedPlayerName={puzzle.guess?.resolvedPlayerName}
        resolvedPlayerPhotoUrl={puzzle.guess?.resolvedPlayerPhotoUrl}
        // REQ-1206 (2026-08-08 addition): same locked-only gating as
        // resolvedPlayerName/resolvedPlayerPhotoUrl above — `puzzle.guess`
        // itself is only non-null once a guess exists, and `points` on it is
        // only non-null once that guess's puzzle is locked (see
        // CurrentPathGuess.points's own doc comment).
        points={puzzle.guess?.points}
      />

      <PathGuessInput
        key={`${puzzle.puzzleId}-guess`}
        clueCount={puzzle.clues.length}
        guess={puzzle.guess}
        accessToken={accessToken}
        onSubmit={handleSubmitGuess}
      />

      {/* Quality-gate fix (S-086 follow-up): a successful guess whose
          follow-up re-fetch failed — the guess itself is not in question, so
          this is worded as a refresh problem, never as if the guess failed. */}
      {refetchWarning && <p className="path-screen__refetch-warning">{refetchWarning}</p>}

      {locked &&
        (isLastPuzzle ? (
          <p className="path-screen__complete">You&rsquo;ve completed every puzzle in this round.</p>
        ) : (
          <button
            type="button"
            className="path-screen__next-button"
            onClick={() => {
              setRefetchWarning(null);
              setPuzzleIndex((current) => Math.min(current + 1, puzzles.length - 1));
            }}
          >
            Next puzzle
          </button>
        ))}
      {explainerOpen && <PathScoringExplainer onClose={() => setExplainerOpen(false)} />}
    </div>
  );
}
