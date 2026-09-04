import { useCallback, useState } from 'react';
import { acceptChallenge, declineChallenge, fetchPendingChallenges } from '../lib/challenges';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import { useSubmitAction } from '../lib/useSubmitAction';
import type { ChallengeResponse } from '../lib/types';
import { FetchListSection } from './FetchListSection';

export interface ChallengesTabProps {
  accessToken: string;
  onAuthError: () => void;
  // S-218 (design-document.md SCREEN-16): switches FriendsScreen to the new
  // "Matches" tab — the actual gameplay screen this banner used to have
  // nowhere to point to (see this file's own S-217-era comment below, now
  // resolved).
  onViewMatches: () => void;
}

// REQ-1402 (S-217, design-document.md SCREEN-15's "Challenges tab"): every
// Pending challenge where the caller is the challenged party. A successful
// accept creates a real ConnectMatch server-side (resultingMatchId on the
// response). S-218 (SCREEN-16) closed the "never navigating into gameplay"
// gap S-217 deliberately left open — the banner below now points at the new
// "Matches" tab (onViewMatches) instead of only acknowledging the match
// exists.
export function ChallengesTab({ accessToken, onAuthError, onViewMatches }: ChallengesTabProps) {
  const fetchFn = useCallback(() => fetchPendingChallenges(accessToken), [accessToken]);
  const { data: pending, loadError, refetch } = useAuthedFetch(fetchFn, { onAuthError });
  // REQ-1402: a single, persistent-until-the-next-accept acknowledgment
  // banner, not tied to any one row (a resolved/accepted row disappears
  // from `pending` on refetch, so the row itself can't be where this
  // lives).
  const [matchCreatedMessage, setMatchCreatedMessage] = useState<string | null>(null);

  async function handleAccept(id: string) {
    await acceptChallenge(accessToken, id);
    setMatchCreatedMessage('Match started!');
    await refetch();
  }

  async function handleDecline(id: string) {
    await declineChallenge(accessToken, id);
    await refetch();
  }

  return (
    <div className="friends-screen__tab-panel">
      <section className="friends-screen__section">
        <h3 className="friends-screen__section-title">
          Challenges{pending && pending.length > 0 ? ` (${pending.length})` : ''}
        </h3>
        {matchCreatedMessage && (
          <p className="friends-screen__success">
            {matchCreatedMessage}{' '}
            <button type="button" className="friends-screen__link-button" onClick={onViewMatches}>
              View your matches
            </button>
          </p>
        )}
        <FetchListSection
          data={pending}
          loadError={loadError}
          // Not styled as an "invitation" (design-document.md §5) — there's
          // no single action here that resolves the emptiness, unlike the
          // friends-list empty state.
          emptyMessage="No pending challenges."
          renderList={(challenges) => (
            <ul className="friends-screen__list">
              {challenges.map((challenge) => (
                <PendingChallengeRow
                  key={challenge.id}
                  challenge={challenge}
                  onAuthError={onAuthError}
                  onAccept={() => handleAccept(challenge.id)}
                  onDecline={() => handleDecline(challenge.id)}
                />
              ))}
            </ul>
          )}
        />
      </section>
    </div>
  );
}

interface PendingChallengeRowProps {
  challenge: ChallengeResponse;
  onAuthError: () => void;
  onAccept: () => Promise<void>;
  onDecline: () => Promise<void>;
}

function PendingChallengeRow({ challenge, onAuthError, onAccept, onDecline }: PendingChallengeRowProps) {
  const { submitting, error, run } = useSubmitAction<void>({ onAuthError });

  function resolve(action: () => Promise<void>) {
    run(action);
  }

  return (
    <li className="friends-screen__row">
      <span className="friends-screen__row-name">{challenge.challengerDisplayName} challenged you</span>
      <span className="friends-screen__row-actions">
        <button type="button" disabled={submitting} onClick={() => resolve(onAccept)}>
          Accept
        </button>
        <button type="button" disabled={submitting} onClick={() => resolve(onDecline)}>
          Decline
        </button>
      </span>
      {error && (
        <p className="friends-screen__error" role="alert">
          {error}
        </p>
      )}
    </li>
  );
}
