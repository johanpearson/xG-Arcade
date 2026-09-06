import { useState } from 'react';
import { ConnectScoringExplainer } from './ConnectScoringExplainer';
import './ConnectEntryScreen.css';

export interface ConnectEntryScreenProps {
  // App.tsx wires these to setFriendsInitialTab('friends'|'matchmaking') +
  // navigateTo('friends') — the exact same two-step "seed a tab, navigate"
  // mechanism handleOpenFriendsTab already established for the notification
  // badge dropdown (see App.tsx's own comment on that function). This
  // component itself starts nothing and knows nothing about friendship
  // rules, the matchmaking window, etc. — see the top-of-file comment below.
  onChallengeFriend: () => void;
  onChallengeRandomPlayer: () => void;
}

// SCREEN-17 (REQ-1415): xG Connect's own lighter-weight front door, reached
// via GameSelectScreen's fourth tile or HeaderNav's "Games" -> "xG Connect"
// entry — the one tile/entry that does NOT navigate straight into gameplay
// (see GameSelectScreen.tsx's own doc comment on that deliberate
// exception). Presents exactly two choices and nothing else that itself
// starts a match. Selecting either hands off to the already-built
// REQ-1401/REQ-1403 flows living inside FriendsScreen (its Friends tab's
// per-row "Challenge" action, and its Matchmaking tab's opt-in button,
// respectively) — this screen restates none of those flows' own business
// rules (friendship required, duplicate-pending rejection, the 12-hour
// matchmaking window, etc.), it only routes to them. The existing
// "Friends" header-nav entry, and FriendsScreen's own Friends/Challenges/
// Matchmaking/Matches tabs, remain reachable and unchanged — this is an
// additional entry point in front of them, never a replacement.
export function ConnectEntryScreen({ onChallengeFriend, onChallengeRandomPlayer }: ConnectEntryScreenProps) {
  // REQ-1416: gates ConnectScoringExplainer — same "own local disclosure
  // state, nothing else on this screen depends on it" shape GridScreen.tsx/
  // PathScreen.tsx already use for their own (ⓘ) toggles.
  const [explainerOpen, setExplainerOpen] = useState(false);

  return (
    <div className="connect-entry-screen">
      <div className="connect-entry-screen__title-row">
        <h2>xG Connect</h2>
        {/* REQ-1416: reachable here — before a match has even started, so a
            player deciding whether to try xG Connect can learn the rules
            first — and again from an active match's own chain-building
            screen (MatchScreen.tsx's own trigger). Same (ⓘ) convention
            REQ-213 and its per-game siblings (ScoringExplainer.tsx/
            PathScoringExplainer.tsx/PredictScoringExplainer.tsx) already
            establish, including reusing their "How scoring works" label
            rather than inventing new wording for the same affordance. */}
        <button
          type="button"
          className="connect-entry-screen__info-toggle"
          onClick={() => setExplainerOpen(true)}
          aria-label="How scoring works"
        >
          ⓘ
        </button>
      </div>
      <p className="connect-entry-screen__intro">Who do you want to challenge?</p>
      <div className="connect-entry-screen__choices">
        {/* aria-label/aria-describedby split mirrors GameSelectScreen.tsx's
            own tiles exactly (see that component's own comment on why): the
            accessible name stays pinned to just the choice's name, while
            the description span is still exposed to assistive tech as the
            button's accessible description, not dropped. */}
        <button
          type="button"
          className="connect-entry-screen__choice"
          aria-label="Challenge a friend"
          aria-describedby="connect-entry-choice-friend-desc"
          onClick={onChallengeFriend}
        >
          <span className="connect-entry-screen__choice-name">Challenge a friend</span>
          <span id="connect-entry-choice-friend-desc" className="connect-entry-screen__choice-description">
            Pick someone from your friends list for a head-to-head match
          </span>
        </button>
        <button
          type="button"
          className="connect-entry-screen__choice"
          aria-label="Challenge random player"
          aria-describedby="connect-entry-choice-random-desc"
          onClick={onChallengeRandomPlayer}
        >
          <span className="connect-entry-screen__choice-name">Challenge random player</span>
          <span id="connect-entry-choice-random-desc" className="connect-entry-screen__choice-description">
            Opt in and get paired with another player looking for a match
          </span>
        </button>
      </div>
      {explainerOpen && <ConnectScoringExplainer onClose={() => setExplainerOpen(false)} />}
    </div>
  );
}
