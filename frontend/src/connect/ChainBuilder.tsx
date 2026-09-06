import { useEffect, useRef, useState } from 'react';
import { formatMatchedClub, raiseChainStepDispute, submitConnectChainStep } from '../lib/connectMatches';
import { useSubmitAction } from '../lib/useSubmitAction';
import type {
  ChainStepDisputeResponse,
  ConnectChainStepView,
  ConnectTargetPickView,
  ConnectTerminalState,
  PlayerAutocompleteSuggestion,
} from '../lib/types';
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

  // REQ-1412/ADR-0109: the most recent failed submission's own step id —
  // set right after `!result.isValid` or `result.busted` below, whether
  // this is the player's first failure at this position (a REQ-1407 retry
  // would normally follow) or the bust-causing second one. Non-null only
  // while that specific failure hasn't itself been disputed yet — an old,
  // superseded failure is never disputable (StepSuperseded, server-side),
  // so this is always overwritten (never merged/appended) by the outcome
  // of the player's very next submission.
  const [disputableStep, setDisputableStep] = useState<{ chainStepId: string } | null>(null);
  const [claimedClubName, setClaimedClubName] = useState('');
  const disputeAction = useSubmitAction<ChainStepDisputeResponse>({ onAuthError });
  // Set once raiseChainStepDispute succeeds — the caller's own ephemeral
  // acknowledgment of a dispute they just raised (REQ-1412's own "You
  // disputed this ruling..." text, design-document.md SCREEN-16 addendum).
  // Deliberately local/ephemeral, not re-derived from a poll: on a fresh
  // mount/page reload this is empty even for a genuinely still-Pending
  // dispute — DisputeReview.tsx's own independent GET
  // /matches/{matchId}/disputes fetch is the durable, always-accurate
  // source for that case (see its own top-of-file comment), this is only
  // the immediate feedback for the raise action itself.
  const [raisedDispute, setRaisedDispute] = useState<{ claimedClubName: string } | null>(null);

  const myTerminalLabel = terminalStateLabel(myTerminalState, 'you');
  const opponentTerminalLabel = terminalStateLabel(opponentTerminalState, 'opponent') ?? 'Your opponent is still playing.';

  // REQ-1412/1413: raising ANY dispute (first-failure or bust-causing)
  // unconditionally marks the caller's slot busted server-side the instant
  // it's raised (requirements-document.md REQ-1412's own status note) —
  // so `myTerminalState.busted` reads true here even for a still-Pending,
  // potentially reversible provisional bust, indistinguishable on the wire
  // from a genuine, permanent two-strikes bust. Showing the generic
  // "Busted — two failed attempts..." wording in that case would be
  // actively misleading (it may not have been a real second failure at
  // all, and the opponent's own review can still clear it) — while this
  // component itself just raised a dispute that hasn't since cleared, the
  // dispute-specific "waiting for review" text takes over entirely instead
  // of the generic terminal label below, and the ordinary retry form and
  // the "your participation has ended" hint are both suppressed (REQ-1412:
  // the player may recover from this, it isn't a real forfeit yet).
  //
  // Known limitation, flagged rather than hidden (see `raisedDispute`'s own
  // comment above): this override only applies for as long as THIS
  // component instance remembers raising the dispute. A page reload loses
  // it, after which a still-Pending (or even Denied) dispute reads as the
  // plain "Busted — two failed attempts..." text again — DisputeReview.tsx
  // (rendered alongside this component) is the durable, always-accurate
  // status source regardless, so the real information is never lost, only
  // this component's own copy of it.
  const awaitingOwnDisputeReview = raisedDispute !== null && myTerminalState.busted;
  const stillPlaying = myTerminalLabel === null || awaitingOwnDisputeReview;

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

  // REQ-1413's Approve path clears the caller's provisional bust
  // (`ClearPlayerBustedAsync`) — the very next refetch delivers
  // `myTerminalState.busted: false` here. Detects that busted→not-busted
  // transition to drop this component's own stale `raisedDispute`/
  // `disputableStep` state, so ordinary submission resumes automatically
  // rather than staying stuck showing "waiting for review" forever once
  // it's actually been resolved in the player's favor.
  const wasBustedRef = useRef(myTerminalState.busted);
  useEffect(() => {
    if (wasBustedRef.current && !myTerminalState.busted) {
      setRaisedDispute(null);
      setDisputableStep(null);
    }
    wasBustedRef.current = myTerminalState.busted;
  }, [myTerminalState.busted]);

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
        // REQ-1412: a Busted result still carries a real chainStepId (it's
        // only null for the "no such player" case handled above) — the
        // player may dispute the bust-causing failure instead of accepting
        // the forfeit. Overwrites any earlier disputable step (an old,
        // superseded failure could never be disputed anyway).
        setDisputableStep(result.chainStepId ? { chainStepId: result.chainStepId } : null);
        setRaisedDispute(null);
        setClaimedClubName('');
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
        // REQ-1412: an ordinary first-attempt failure may also be disputed
        // instead of retried — offered alongside the retry form, not
        // instead of it.
        setDisputableStep(result.chainStepId ? { chainStepId: result.chainStepId } : null);
        setRaisedDispute(null);
        setClaimedClubName('');
        return;
      }

      setCandidateName('');
      setCandidateWikidataQid(null);
      setHasSelectedCandidate(false);
      // A new position was reached — any earlier failure at the previous
      // position is now moot (either it was retried successfully here, or
      // it's simply no longer the frontier), so drop any stale disputable/
      // raised-dispute state left over from it.
      setDisputableStep(null);
      setRaisedDispute(null);
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

  // REQ-1412/ADR-0109: raises a dispute against the most recent failed
  // submission (`disputableStep`), naming the specific club the player
  // believes connects the two players — the one place in xG Connect a
  // player types a club at all. Always refetches on success: raising a
  // dispute always changes persisted state (a new Pending
  // ConnectChainStepDispute row, and — for a first-failure dispute — a
  // provisional bust that wasn't there before), the same "changed
  // persisted state, so refetch" rule handleSubmit's own accepted/busted
  // branches already follow.
  function handleRaiseDispute() {
    if (!disputableStep) return;
    const trimmedClub = claimedClubName.trim();
    if (!trimmedClub) return;
    disputeAction.run(
      () => raiseChainStepDispute(accessToken, matchId, disputableStep.chainStepId, trimmedClub),
      async (dispute) => {
        setRaisedDispute({ claimedClubName: dispute.claimedClubName });
        setDisputableStep(null);
        setClaimedClubName('');
        onChanged();
      },
    );
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

      {awaitingOwnDisputeReview && raisedDispute ? (
        // REQ-1412/1413: takes over from the generic terminal label AND the
        // ordinary submission form below while this component itself still
        // remembers raising a dispute that hasn't since cleared — see
        // `awaitingOwnDisputeReview`'s own comment above for why the
        // generic "Busted..." wording would be misleading here.
        <p className="connect-match__status" role="status">
          You disputed this ruling, claiming they played together at {raisedDispute.claimedClubName}. Waiting for
          your opponent to review it.
        </p>
      ) : myTerminalLabel ? (
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

      {/* REQ-1412/ADR-0109: offered right after either kind of failure
          (`disputableStep` set by handleSubmit above) — alongside the
          ordinary retry form for a first failure, or alongside the
          "Busted..." terminal text for the bust-causing one. Disappears
          once the dispute is actually raised (replaced by the
          `awaitingOwnDisputeReview` acknowledgment above), or once a
          different submission supersedes this failure. */}
      {disputableStep && (
        <div className="connect-match__chain-form connect-match__dispute-form">
          <label className="connect-match__hint" htmlFor="dispute-claimed-club">
            Dispute this ruling — name the club you believe connects them
          </label>
          <input
            id="dispute-claimed-club"
            type="text"
            className="connect-match__search-input"
            value={claimedClubName}
            onChange={(event) => setClaimedClubName(event.target.value)}
            placeholder="Claimed club…"
            disabled={disputeAction.submitting}
          />
          <button
            type="button"
            className="connect-match__button"
            disabled={disputeAction.submitting || !claimedClubName.trim()}
            onClick={handleRaiseDispute}
          >
            {disputeAction.submitting ? 'Disputing…' : 'Dispute this ruling'}
          </button>
          {disputeAction.error && (
            <p className="connect-match__error" role="alert">
              {disputeAction.error}
            </p>
          )}
        </div>
      )}

      {!stillPlaying && (
        <p className="connect-match__hint">No further steps can be submitted — your participation has ended.</p>
      )}
    </section>
  );
}
