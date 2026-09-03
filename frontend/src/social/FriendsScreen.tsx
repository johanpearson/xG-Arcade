import { useState } from 'react';
import { ChallengesTab } from './ChallengesTab';
import { FriendsTab } from './FriendsTab';
import { MatchmakingTab } from './MatchmakingTab';
import './FriendsScreen.css';

export interface FriendsScreenProps {
  accessToken: string;
  onAuthError: () => void;
}

type FriendsTabKey = 'friends' | 'challenges' | 'matchmaking';

const TABS: Array<{ value: FriendsTabKey; label: string }> = [
  { value: 'friends', label: 'Friends' },
  { value: 'challenges', label: 'Challenges' },
  { value: 'matchmaking', label: 'Matchmaking' },
];

// SCREEN-15 (REQ-1401/1402/1403, S-217): the friends/challenges/
// matchmaking screen, reached via HeaderNav's new "Friends" entry. Same
// plain underline-tab pattern (role="tablist"/role="tab"/aria-selected,
// accent-green underline) UserStatsScreen.tsx's/LeaderboardScreen.tsx's own
// game switchers already established — "not a new control type," per
// design-document.md's own recurring rule. Each tab panel owns its own
// fetch/loading/error state independently (FriendsTab/ChallengesTab/
// MatchmakingTab) rather than one shared fetch, the same "independent
// sections" shape AdminScreen.tsx already uses — switching tabs never
// unmounts the others (see the `hidden` attribute below), so already-loaded
// data isn't refetched on every tab switch.
export function FriendsScreen({ accessToken, onAuthError }: FriendsScreenProps) {
  const [activeTab, setActiveTab] = useState<FriendsTabKey>('friends');

  return (
    <div className="friends-screen">
      <h2 className="friends-screen__title">Friends &amp; Challenges</h2>

      <div className="friends-screen__tabs" role="tablist" aria-label="Friends & Challenges">
        {TABS.map(({ value, label }) => (
          <button
            key={value}
            type="button"
            role="tab"
            aria-selected={activeTab === value}
            className={`friends-screen__tab ${activeTab === value ? 'friends-screen__tab--active' : ''}`}
            onClick={() => setActiveTab(value)}
          >
            {label}
          </button>
        ))}
      </div>

      <div hidden={activeTab !== 'friends'}>
        <FriendsTab accessToken={accessToken} onAuthError={onAuthError} />
      </div>
      <div hidden={activeTab !== 'challenges'}>
        <ChallengesTab accessToken={accessToken} onAuthError={onAuthError} />
      </div>
      <div hidden={activeTab !== 'matchmaking'}>
        <MatchmakingTab accessToken={accessToken} onAuthError={onAuthError} />
      </div>
    </div>
  );
}
