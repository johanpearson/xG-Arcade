import { useState } from 'react';
import { MatchScreen } from '../connect/MatchScreen';
import { MatchesTab } from '../connect/MatchesTab';
import { ChallengesTab } from './ChallengesTab';
import { FriendsTab } from './FriendsTab';
import { MatchmakingTab } from './MatchmakingTab';
import './FriendsScreen.css';

export interface FriendsScreenProps {
  accessToken: string;
  // S-218: needed only by the "Matches" tab's chat (MatchChat.tsx), to tell
  // the caller's own messages apart from the opponent's. Optional — every
  // other tab on this screen has no use for it.
  viewerUserId?: string;
  onAuthError: () => void;
  // REQ-1411 (design-document.md SCREEN-07's badge-redesign status note,
  // 2026-09-03): lets a caller open this screen with a specific tab already
  // active — the header nav's own notification-badge dropdown uses this to
  // jump straight to "Friend requests"/"Challenges" instead of always
  // landing on the default "Friends" tab. Optional and defaults to
  // `activeTab`'s own useState initializer ('friends') when omitted, so
  // every existing caller (the plain "Friends" nav entry) is unaffected.
  initialTab?: FriendsTabKey;
  // Direct user feedback (2026-09-03): threaded straight through to
  // FriendsTab's own `onSelectPlayer` — see that prop's doc comment.
  onSelectPlayer?: (userId: string, displayName: string) => void;
}

export type FriendsTabKey = 'friends' | 'challenges' | 'matchmaking' | 'matches';

const TABS: Array<{ value: FriendsTabKey; label: string }> = [
  { value: 'friends', label: 'Friends' },
  { value: 'challenges', label: 'Challenges' },
  { value: 'matchmaking', label: 'Matchmaking' },
  { value: 'matches', label: 'Matches' },
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
// unmounts Friends/Matchmaking (see the `hidden` attribute below), so
// already-loaded data isn't refetched on every tab switch.
//
// The "matches" and "challenges" tabs are a deliberate exception to that
// (design-document.md SCREEN-16, S-218 bugfix; extended to "challenges" by
// the REQ-1402 sent-challenges visibility fix, S-230): their content is
// truly conditionally rendered (mounted only while activeTab matches),
// not kept alive under `hidden`, because their data can change from
// something the user did elsewhere on this same screen — accepting a
// challenge in ChallengesTab or a matchmaking sweep pairing changes
// MatchesTab's data, and sending a challenge from FriendsTab's own
// "Challenge" button changes ChallengesTab's sent-challenges list — while
// each sits mounted-but-hidden. useAuthedFetch only fetches on mount, so a
// `hidden`-only tab would capture its GET response once — often before the
// triggering action happened — and never refetch it, leaving the tab
// showing stale (usually empty) data. Remounting on every switch to either
// tab is what makes it refetch each time, which is the correct behavior
// specifically for these two. Do not apply this pattern to the remaining
// two tabs without a reason as concrete as this one.
//
// `initialTab` (REQ-1411) and the S-218 "matches" tab/drill-down are
// independent additions that landed in parallel (S-217's notification-
// badge dropdown vs. S-218's gameplay screen) — both apply together here:
// the badge dropdown can deep-link into any of the four tabs, "matches"
// included, exactly like the other three.
export function FriendsScreen({
  accessToken,
  viewerUserId,
  onAuthError,
  initialTab,
  onSelectPlayer,
}: FriendsScreenProps) {
  const [activeTab, setActiveTab] = useState<FriendsTabKey>(initialTab ?? 'friends');
  // S-218 (design-document.md SCREEN-16's "Matches tab" placement note):
  // drill-down state lives here, at the tab-container level, rather than
  // as a fifth App.tsx-level Screen/hash route (ADR-0039) — a match has no
  // meaningful standalone URL of its own yet (no deep-linking requirement
  // in REQ-1404-1411), so this mirrors the simplest thing that works:
  // `null` shows the matches list, a real id shows that one match's detail.
  const [selectedMatchId, setSelectedMatchId] = useState<string | null>(null);

  // Used by ChallengesTab's post-accept banner and MatchmakingTab's opt-in
  // success state (design-document.md SCREEN-16's own "View your matches"
  // note) — always resets to the list, never a stale previously-open match.
  function handleViewMatches() {
    setSelectedMatchId(null);
    setActiveTab('matches');
  }

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
        <FriendsTab accessToken={accessToken} onAuthError={onAuthError} onSelectPlayer={onSelectPlayer} />
      </div>
      {activeTab === 'challenges' && (
        <ChallengesTab accessToken={accessToken} onAuthError={onAuthError} onViewMatches={handleViewMatches} />
      )}
      <div hidden={activeTab !== 'matchmaking'}>
        <MatchmakingTab accessToken={accessToken} onAuthError={onAuthError} onViewMatches={handleViewMatches} />
      </div>
      {activeTab === 'matches' &&
        (selectedMatchId ? (
          <MatchScreen
            matchId={selectedMatchId}
            accessToken={accessToken}
            viewerUserId={viewerUserId}
            onAuthError={onAuthError}
            onBack={() => setSelectedMatchId(null)}
          />
        ) : (
          <MatchesTab accessToken={accessToken} onAuthError={onAuthError} onOpenMatch={setSelectedMatchId} />
        ))}
    </div>
  );
}
