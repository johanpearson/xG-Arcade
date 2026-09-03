import { useCallback, useState } from 'react';
import { acceptChallenge, declineChallenge, fetchPendingChallenges } from '../lib/challenges';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import { useSubmitAction } from '../lib/useSubmitAction';
import type { ChallengeResponse } from '../lib/types';
import { FetchListSection } from './FetchListSection';
import { shortUserId } from './shortUserId';

export interface ChallengesTabProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-1402 (S-217, design-document.md SCREEN-15's "Challenges tab"): every
// Pending challenge where the caller is the challenged party. A successful
// accept creates a real ConnectMatch server-side (resultingMatchId on the
// response) — this story stops at acknowledging that, never navigating
// into any match/gameplay UI (S-218's separate, not-yet-built scope).
export function ChallengesTab({ accessToken, onAuthError }: ChallengesTabProps) {
  const fetchFn = useCallback(() => fetchPendingChallenges(accessToken), [accessToken]);
  const { data: pending, loadError, refetch } = useAuthedFetch(fetchFn, { onAuthError });
  // REQ-1402: a single, persistent-until-the-next-accept acknowledgment
  // banner, not tied to any one row (a resolved/accepted row disappears
  // from `pending` on refetch, so the row itself can't be where this
  // lives) — see design-document.md SCREEN-15's own note for why this is
  // deliberately just an honest acknowledgment, not a link into gameplay.
  const [matchCreatedMessage, setMatchCreatedMessage] = useState<string | null>(null);

  async function handleAccept(id: string) {
    await acceptChallenge(accessToken, id);
    setMatchCreatedMessage("Match started! You'll be able to play it soon.");
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
        {matchCreatedMessage && <p className="friends-screen__success">{matchCreatedMessage}</p>}
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
      <span className="friends-screen__row-name">{shortUserId(challenge.challengerUserId)} challenged you</span>
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
