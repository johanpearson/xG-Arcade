import { useState } from 'react';
import { formatMatchedClub, submitConnectChainStep } from '../lib/connectMatches';
import { useSubmitAction } from '../lib/useSubmitAction';
import type { ConnectChainStepView, ConnectTargetPickView, ConnectTerminalState, PlayerAutocompleteSuggestion } from '../lib/types';
import { ChainStepsList } from './ChainStepsList';
import { PlayerSearchField } from './PlayerSearchField';

export interface ChainBuilderProps {
  matchId: string;
  accessToken: string;
  myTargetPick: ConnectTargetPickView;
  opponentTargetPick: ConnectTargetPickView;
  myChainSteps: ConnectChainStepView[];
  myTerminalState: ConnectTerminalState;
  opponentTerminalState: ConnectTerminalState;
  deadlineUtc: string | null;
  onAuthError: () => void;
  // Called only after an outcome that actually changed persisted state
  // (accepted/closed step, or a bust) — a plain invalid attempt or a
  // "no such player" result changes nothing server-side, so those skip the
  // refetch (see this file's own submit handler for the exact split).
  onChanged: () => void;
}

function terminalStateLabel(state: ConnectTerminalState, whoseTurn: 'you' | 'opponent'): string | null {
  const subject = whoseTurn === 'you' ? 'You' : 'Your opponent';
  if (state.completed) return `${subject === 'You' ? 'You have' : 'Your opponent has'} finished their chain.`;
  if (state.busted) return `${subject} busted — two failed attempts at the same position.`;
  if (state.timedOut) return `${subject === 'You' ? 'You ran' : 'Your opponent ran'} out of time.`;
  return null;
}

// REQ-1406/1407 (design-document.md SCREEN-16's "Active/chain-building
// phase"): incremental connector submission with live per-submission
// feedback, and the two-strikes/bust terminal state. Deliberately shows no
// live countdown — see this component's own "Known limitation" comment
// below on `deadlineUtc`.
export function ChainBuilder({
  matchId,
  accessToken,
  myTargetPick,
  opponentTargetPick,
  myChainSteps,
  myTerminalState,
  opponentTerminalState,
  deadlineUtc,
  onAuthError,
  onChanged,
}: ChainBuilderProps) {
  const [candidateName, setCandidateName] = useState('');
  // Bug fix (2026-09-05, ADR-0107): a real, reported incident (two
  // different real footballers both named "Jonas Olsson") showed that
  // resolving a typed candidate name server-side, with no way to
  // disambiguate a same-name collision, is a genuine bug — not just a
  // theoretical edge case. This field now requires a real
  // /players/autocomplete suggestion to be clicked (same as
  // TargetPickPanel.tsx already requires) before it can be submitted.
  // candidateWikidataQid is carried through to submission when the
  // selected suggestion has one, letting the server resolve the exact real
  // person unambiguously; it can legitimately be null for a suggestion
  // indexed before that column existed (see PlayerNameIndex.WikidataQid's
  // own doc comment) — that still submits, just via the same name-only
  // fallback resolution as before this fix, so an as-yet-unbackfilled
  // player is never unplayable. hasSelectedCandidate is the actual
  // "may submit" gate (a null QID must not be mistaken for "nothing
  // selected"); both reset together whenever the typed text no longer
  // matches what was selected (see the PlayerSearchField's onValueChange
  // below) — an edited-after-selecting field must not silently keep
  // submitting the stale selection.
  const [candidateWikidataQid, setCandidateWikidataQid] = useState<string | null>(null);
  const [hasSelectedCandidate, setHasSelectedCandidate] = useState(false);
  const { submitting, error, run } = useSubmitAction<void>({ onAuthError });
  const [feedback, setFeedback] = useState<{ tone: 'success' | 'error'; text: string } | null>(null);

  const myTerminalLabel = terminalStateLabel(myTerminalState, 'you');
  const opponentTerminalLabel = terminalStateLabel(opponentTerminalState, 'opponent') ?? 'Your opponent is still playing.';
  const stillPlaying = myTerminalLabel === null;

  // S-218 bugfix (real product bug, not a test-only flake — see
  // ChainBuilder.tsx's git history / docs/design-document.md SCREEN-16's
  // own addendum for the CI trail that found it): the "Connected! Your
  // chain is complete." acknowledgment used to live ONLY in the local
  // `feedback` state set inside handleSubmit's async callback below. That
  // is fragile in exactly one real scenario — the submission that closes
  // MY chain, when my opponent had already reached their own terminal
  // state first. In that case, `ConnectChainStepService.SubmitChainStepAsync`
  // resolves the match server-side INLINE in the same request, so the very
  // next `onChanged()`-triggered refetch comes back `status: 'Resolved'`,
  // and `MatchScreen.tsx` immediately swaps this whole component out for
  // `MatchResolution` — destroying the local `feedback` state before the
  // player ever perceived it (see MatchResolution.tsx's own matching
  // acknowledgment for that case).
  //
  // `myTerminalState.completed` is itself derived from props, not local
  // state — it survives any concurrent re-render (a poll tick landing at
  // an awkward moment, React batching a parent update, etc.) as long as
  // this component stays mounted, unlike the one-shot `feedback` flag.
  // Using it directly here (rather than only the ephemeral `feedback`
  // value) makes the acknowledgment durable for the case where MY own
  // submission completed my chain but the match itself is still Active
  // (my opponent hasn't finished yet) — handleSubmit below deliberately
  // stops setting local `feedback` text for that specific outcome and
  // leaves this to take over instead.
  const myChainJustCompleted = myTerminalState.completed;

  function handleSelect(suggestion: PlayerAutocompleteSuggestion) {
    // ADR-0007's autocomplete/correctness separation still holds — seeing a
    // suggestion here is never itself confirmation the step will validate
    // (REQ-1406's own note), the server still runs the real live-overlap
    // check. But (bug fix, 2026-09-05, ADR-0107) which real person this
    // selection resolves to is no longer left ambiguous: the suggestion's
    // wikidataQid is carried through to submission alongside its name.
    setCandidateName(suggestion.name);
    setCandidateWikidataQid(suggestion.wikidataQid ?? null);
    setHasSelectedCandidate(true);
  }

  function handleSubmit() {
    const trimmedName = candidateName.trim();
    if (!trimmedName || !hasSelectedCandidate) return;
    setFeedback(null);
    run(async () => {
      const result = await submitConnectChainStep(accessToken, matchId, trimmedName, candidateWikidataQid);

      if (result.position === null) {
        // REQ-1406: the selected candidate didn't resolve to any known
        // player at all — nothing was persisted, this consumes no
        // attempt/strike. Rare now that a real suggestion must be selected
        // (ADR-0107): only reachable for a player indexed in autocomplete
        // (COMP-10) but never yet referenced by any game (COMP-06), on a
        // suggestion that also predates the WikidataQid backfill. Clear the
        // selection so the player tries a different search rather than
        // re-submitting the exact same one.
        setFeedback({ tone: 'error', text: `Couldn't find a match for "${trimmedName}" — try searching again.` });
        setCandidateName('');
        setCandidateWikidataQid(null);
        setHasSelectedCandidate(false);
        return;
      }

      if (result.busted) {
        setFeedback({ tone: 'error', text: 'Busted — that was a second failed attempt at this position. Your participation in this match has ended.' });
        setCandidateName('');
        setCandidateWikidataQid(null);
        setHasSelectedCandidate(false);
        onChanged();
        return;
      }

      if (!result.isValid) {
        setFeedback({
          tone: 'error',
          text: `${trimmedName} never shared a club with the previous player at an overlapping time. You have one more attempt at this position.`,
        });
        setCandidateName('');
        setCandidateWikidataQid(null);
        setHasSelectedCandidate(false);
        return;
      }

      setCandidateName('');
      setCandidateWikidataQid(null);
      setHasSelectedCandidate(false);
      if (result.chainComplete) {
        // Deliberately NOT set here — see `myChainJustCompleted` above for
        // why the completion acknowledgment is derived from props instead
        // of this ephemeral local state.
        setFeedback(null);
      } else {
        // Design change (2026-09-04, REQ-1406, ADR-0104): the player no
        // longer claims a club, so confirm which one the server matched —
        // same wording ChainStepsList.tsx's own historical render uses.
        const matched = formatMatchedClub(result.matchedClubName, result.matchedOverlapStartYear, result.matchedOverlapEndYear);
        setFeedback({ tone: 'success', text: `Connector accepted — ${matched}.` });
      }
      onChanged();
    });
  }

  return (
    <section className="connect-match__section">
      <h3 className="connect-match__section-title">Build your chain</h3>
      <p className="connect-match__description">
        Connect <strong>{myTargetPick.targetPlayerName}</strong> to <strong>{opponentTargetPick.targetPlayerName}</strong> —
        one played-together connector at a time.
      </p>
      {deadlineUtc && (
        <p className="connect-match__deadline mono-figure">Deadline: {new Date(deadlineUtc).toLocaleString()}</p>
      )}

      <ChainStepsList
        targetPlayerName={myTargetPick.targetPlayerName}
        otherTargetPlayerName={opponentTargetPick.targetPlayerName}
        steps={myChainSteps}
      />

      <p className="connect-match__status">{opponentTerminalLabel}</p>

      {myTerminalLabel ? (
        <p className="connect-match__status" role="status">
          {myTerminalLabel}
        </p>
      ) : (
        <div className="connect-match__chain-form">
          <PlayerSearchField
            id="chain-step-candidate"
            label="Candidate player name"
            accessToken={accessToken}
            value={candidateName}
            onValueChange={(value) => {
              setCandidateName(value);
              // Bug fix (2026-09-05, ADR-0107): editing the text after a
              // selection must not leave a stale wikidataQid submittable
              // for text that no longer matches it.
              setCandidateWikidataQid(null);
              setHasSelectedCandidate(false);
            }}
            onSelect={handleSelect}
            placeholder="Candidate player…"
            disabled={submitting}
          />
          <button
            type="button"
            className="connect-match__button"
            disabled={submitting || !candidateName.trim() || !hasSelectedCandidate}
            onClick={handleSubmit}
          >
            {submitting ? 'Checking…' : 'Submit connector'}
          </button>
        </div>
      )}

      {myChainJustCompleted && (
        <p className="connect-match__success" role="status">
          Connected! Your chain is complete.
        </p>
      )}
      {feedback && (
        <p className={feedback.tone === 'error' ? 'connect-match__error' : 'connect-match__success'} role="status">
          {feedback.text}
        </p>
      )}
      {error && (
        <p className="connect-match__error" role="alert">
          {error}
        </p>
      )}
      {!stillPlaying && (
        <p className="connect-match__hint">No further steps can be submitted — your participation has ended.</p>
      )}
    </section>
  );
}
