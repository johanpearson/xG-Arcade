import { useState } from 'react';
import { optInToMatchmaking } from '../lib/matchmaking';
import type { MatchmakingOptInResponse } from '../lib/types';
import { useSubmitAction } from '../lib/useSubmitAction';

export interface MatchmakingTabProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-1403 (S-217, design-document.md SCREEN-15's "Matchmaking tab").
// Opting in IS the consent — no accept/decline step, so this is a single
// one-shot action, not a form. There is no GET/listing endpoint for this
// resource, so the "you're in the pool until…" status below is
// deliberately session-local-only component state, not fetched — see this
// component's own render for the disclosure note that says so.
export function MatchmakingTab({ accessToken, onAuthError }: MatchmakingTabProps) {
  const [expiresAt, setExpiresAt] = useState<string | null>(null);
  const { submitting, error, run } = useSubmitAction<MatchmakingOptInResponse>({ onAuthError });

  function handleOptIn() {
    run(
      () => optInToMatchmaking(accessToken),
      (result) => setExpiresAt(result.expiresAt),
    );
  }

  return (
    <div className="friends-screen__tab-panel">
      <section className="friends-screen__section">
        <h3 className="friends-screen__section-title">Random matchmaking</h3>
        <p className="friends-screen__description">
          Get matched with a random opponent for a new xG Connect match.
        </p>

        {expiresAt === null ? (
          <button type="button" disabled={submitting} onClick={handleOptIn}>
            {submitting ? 'Opting in…' : 'Opt in'}
          </button>
        ) : (
          <>
            <p className="friends-screen__success">
              You&apos;re in the matchmaking pool until {new Date(expiresAt).toLocaleString()}.
            </p>
            {/* REQ-1403's own scope note: no GET/listing endpoint exists for
                this resource, so this status is only ever known for the
                lifetime of this page — flagged plainly rather than implying
                it's tracked anywhere durable. */}
            <p className="friends-screen__hint">This won&apos;t be visible after you leave this screen.</p>
          </>
        )}

        {error && (
          <p className="friends-screen__error" role="alert">
            {error}
          </p>
        )}
      </section>
    </div>
  );
}
