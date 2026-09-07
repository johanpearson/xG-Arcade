import { useCallback, useState } from 'react';
import { fetchConnectMatches } from '../lib/connectMatches';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import type { ConnectMatchListItem } from '../lib/types';
import { FetchListSection } from '../social/FetchListSection';

export interface MatchesTabProps {
  accessToken: string;
  onAuthError: () => void;
  onOpenMatch: (matchId: string) => void;
}

function statusLabel(status: string): string {
  switch (status) {
    case 'AwaitingTargetPicks':
      return 'Awaiting target picks';
    case 'Active':
      return 'Active';
    case 'Resolved':
      return 'Resolved';
    default:
      return status;
  }
}

function outcomeLabel(outcome: string): string | null {
  switch (outcome) {
    case 'Win':
      return 'You won';
    case 'Loss':
      return 'You lost';
    case 'Draw':
      return 'Draw';
    default:
      return null;
  }
}

// REQ-1417: the three sub-tabs a match's `status` buckets into — mutually
// exclusive by construction (ConnectMatch.Status itself only ever holds one
// of these three values at a time), so a match can only ever match exactly
// one entry here.
type MatchesSubTabKey = 'notStarted' | 'ongoing' | 'completed';

const SUB_TABS: Array<{ value: MatchesSubTabKey; label: string; status: string }> = [
  { value: 'notStarted', label: 'Not Started', status: 'AwaitingTargetPicks' },
  { value: 'ongoing', label: 'Ongoing', status: 'Active' },
  { value: 'completed', label: 'Completed', status: 'Resolved' },
];

// REQ-1417: this tab's own empty-state text, distinct from the whole-list
// empty message below (FetchListSection's emptyMessage) — shown when the
// overall list is non-empty but this particular sub-tab has nothing in it,
// still pointing at the same two match-creating actions so the player isn't
// left wondering whether the sub-tab loaded correctly.
const SUB_TAB_EMPTY_MESSAGE =
  "No matches in this list. Challenge a friend or opt into matchmaking to start one.";

function MatchRow({ match, onOpenMatch }: { match: ConnectMatchListItem; onOpenMatch: (matchId: string) => void }) {
  return (
    <li className="friends-screen__row">
      <span className="friends-screen__row-name">
        {match.opponentDisplayName ?? 'a deleted user'}
        {' — '}
        {statusLabel(match.status)}
        {match.status === 'Resolved' && outcomeLabel(match.outcome) && ` (${outcomeLabel(match.outcome)})`}
        {match.awaitingMyAction && <span className="friends-screen__success"> — Your move</span>}
      </span>
      <span className="friends-screen__row-actions">
        <button type="button" onClick={() => onOpenMatch(match.matchId)}>
          View match
        </button>
      </span>
    </li>
  );
}

// REQ-1404/1411/1417 (design-document.md SCREEN-16's "Matches tab" — the
// entry point into a match, GET /matches is currently the ONLY way a player
// discovers which matchIds belong to them (see ConnectMatchQueryEndpoints's
// own S-218-prep comment). Reuses FetchListSection/friends-screen__* — same
// card shell every other FriendsScreen tab already uses, since this is
// simply a fourth tab on that same screen.
//
// REQ-1417: the fetched list is bucketed into three sub-tabs — "Not
// Started"/"Ongoing"/"Completed" — purely by client-side filtering of the
// single `matches` array useAuthedFetch already fetched once on mount.
// Switching `subTab` only changes which already-fetched rows are rendered;
// it never triggers a new GET /matches (no fetchFn call, no dependency on
// `matches` re-fetching). Reuses FriendsScreen.tsx's own
// `friends-screen__tabs`/`friends-screen__tab` sub-tab-button pattern
// (SCREEN-16's own "reuse that same pattern" note) rather than inventing a
// second tab control.
export function MatchesTab({ accessToken, onAuthError, onOpenMatch }: MatchesTabProps) {
  const fetchFn = useCallback(() => fetchConnectMatches(accessToken), [accessToken]);
  const { data: matches, loadError } = useAuthedFetch(fetchFn, { onAuthError });
  const [subTab, setSubTab] = useState<MatchesSubTabKey>('notStarted');

  return (
    <div className="friends-screen__tab-panel">
      <section className="friends-screen__section">
        <h3 className="friends-screen__section-title">Your xG Connect matches</h3>
        <FetchListSection
          data={matches}
          loadError={loadError}
          emptyMessage="You don't have any xG Connect matches yet. Challenge a friend or opt into matchmaking to start one."
          renderList={(list: ConnectMatchListItem[]) => {
            const activeSubTab = SUB_TABS.find((tab) => tab.value === subTab) ?? SUB_TABS[0];
            const bucketed = list.filter((match) => match.status === activeSubTab.status);

            return (
              <>
                <div className="friends-screen__tabs" role="tablist" aria-label="Matches by status">
                  {SUB_TABS.map(({ value, label }) => (
                    <button
                      key={value}
                      type="button"
                      role="tab"
                      aria-selected={subTab === value}
                      className={`friends-screen__tab ${subTab === value ? 'friends-screen__tab--active' : ''}`}
                      onClick={() => setSubTab(value)}
                    >
                      {label}
                    </button>
                  ))}
                </div>
                {bucketed.length === 0 ? (
                  <p className="friends-screen__empty">{SUB_TAB_EMPTY_MESSAGE}</p>
                ) : (
                  <ul className="friends-screen__list">
                    {bucketed.map((match) => (
                      <MatchRow key={match.matchId} match={match} onOpenMatch={onOpenMatch} />
                    ))}
                  </ul>
                )}
              </>
            );
          }}
        />
      </section>
    </div>
  );
}
