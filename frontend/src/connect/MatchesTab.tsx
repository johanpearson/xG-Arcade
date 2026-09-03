import { useCallback } from 'react';
import { fetchConnectMatches } from '../lib/connectMatches';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import type { ConnectMatchListItem } from '../lib/types';
import { FetchListSection } from '../social/FetchListSection';
import { shortUserIdOrDeleted } from '../social/shortUserId';

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

// REQ-1404/1411 (design-document.md SCREEN-16's "Matches tab"): the entry
// point into a match — GET /matches is currently the ONLY way a player
// discovers which matchIds belong to them (see ConnectMatchQueryEndpoints's
// own S-218-prep comment). Reuses FetchListSection/friends-screen__* — same
// card shell every other FriendsScreen tab already uses, since this is
// simply a fourth tab on that same screen.
export function MatchesTab({ accessToken, onAuthError, onOpenMatch }: MatchesTabProps) {
  const fetchFn = useCallback(() => fetchConnectMatches(accessToken), [accessToken]);
  const { data: matches, loadError } = useAuthedFetch(fetchFn, { onAuthError });

  return (
    <div className="friends-screen__tab-panel">
      <section className="friends-screen__section">
        <h3 className="friends-screen__section-title">Your xG Connect matches</h3>
        <FetchListSection
          data={matches}
          loadError={loadError}
          emptyMessage="You don't have any xG Connect matches yet. Challenge a friend or opt into matchmaking to start one."
          renderList={(list: ConnectMatchListItem[]) => (
            <ul className="friends-screen__list">
              {list.map((match) => (
                <li key={match.matchId} className="friends-screen__row">
                  <span className="friends-screen__row-name">
                    {shortUserIdOrDeleted(match.opponentUserId)}
                    {' — '}
                    {statusLabel(match.status)}
                    {match.status === 'Resolved' && outcomeLabel(match.outcome) && ` (${outcomeLabel(match.outcome)})`}
                    {match.awaitingMyAction && (
                      <span className="friends-screen__success"> — Your move</span>
                    )}
                  </span>
                  <span className="friends-screen__row-actions">
                    <button type="button" onClick={() => onOpenMatch(match.matchId)}>
                      View match
                    </button>
                  </span>
                </li>
              ))}
            </ul>
          )}
        />
      </section>
    </div>
  );
}
