import { useEffect, useState } from 'react';
import { ApiError, describeError, fetchLeaderboard } from '../lib/api';
import type { LeaderboardRow } from '../lib/types';
import './LeaderboardScreen.css';

export interface LeaderboardScreenProps {
  accessToken: string;
  onAuthError: () => void;
}

type LoadState =
  | { phase: 'loading' }
  | { phase: 'error'; message: string }
  | { phase: 'ready'; rows: LeaderboardRow[] };

// SCREEN-03 (REQ-401/404's Tier 0 slice): the global league is the only one
// that exists yet — custom leagues' "[My League ▾] [+ New]" tabs (REQ-402-
// 404) are deferred per MVP-SCOPE.md, so this shows only the Global list,
// no tab switcher.
export function LeaderboardScreen({ accessToken, onAuthError }: LeaderboardScreenProps) {
  const [state, setState] = useState<LoadState>({ phase: 'loading' });

  useEffect(() => {
    let cancelled = false;

    fetchLeaderboard(accessToken)
      .then((response) => {
        if (!cancelled) setState({ phase: 'ready', rows: response.rows });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        if (error instanceof ApiError && error.status === 401) {
          onAuthError();
          return;
        }
        setState({ phase: 'error', message: describeError(error) });
      });

    return () => {
      cancelled = true;
    };
  }, [accessToken, onAuthError]);

  if (state.phase === 'loading') {
    return <p className="leaderboard-screen__status">Loading the leaderboard…</p>;
  }

  if (state.phase === 'error') {
    return <p className="leaderboard-screen__status leaderboard-screen__status--error">{state.message}</p>;
  }

  return (
    <div className="leaderboard-screen">
      <div className="leaderboard-screen__header">
        <h2>Global leaderboard</h2>
      </div>
      {state.rows.length === 0 ? (
        // design-document.md §5: "empty states are invitations."
        <p className="leaderboard-screen__empty">No scores yet — be the first to play a round.</p>
      ) : (
        <ol className="leaderboard-screen__list">
          {state.rows.map((row, index) => (
            <li
              key={row.userId}
              className={`leaderboard-screen__row ${row.isRequestingUser ? 'leaderboard-screen__row--you' : ''}`}
            >
              <span className="leaderboard-screen__rank mono-figure">{index + 1}</span>
              <span className="leaderboard-screen__name">{row.displayName}</span>
              <span className="leaderboard-screen__points mono-figure">{row.totalPoints} pts</span>
              {/* Text, not color-only (design-document.md §6). */}
              {row.isRequestingUser && <span className="leaderboard-screen__you-tag">you</span>}
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}
