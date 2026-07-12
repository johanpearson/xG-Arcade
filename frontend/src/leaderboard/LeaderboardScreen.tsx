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

// Rows shown here are already REQ-401/404's locked totals
// (SUM(FinalPoints), never in-progress/live points — see REQ-205/S-018's
// "provisional, never a promise" rule) — polling keeps that locked total
// current as rounds close elsewhere, it does not add live points into it.
const REFRESH_INTERVAL_MS = 15_000;

// SCREEN-03 (REQ-401/404's Tier 0 slice): the global league is the only one
// that exists yet — custom leagues' "[My League ▾] [+ New]" tabs (REQ-402-
// 404) are deferred per MVP-SCOPE.md, so this shows only the Global list,
// no tab switcher.
export function LeaderboardScreen({ accessToken, onAuthError }: LeaderboardScreenProps) {
  const [state, setState] = useState<LoadState>({ phase: 'loading' });

  useEffect(() => {
    let cancelled = false;
    let timeoutId: number | undefined;

    // showLoadingState is only true for the initial mount fetch — a
    // background poll tick must never flash the "Loading…" state over an
    // already-rendered leaderboard, and a transient poll failure must never
    // replace a good, already-displayed leaderboard with an error message.
    //
    // Self-rescheduling via setTimeout (rather than setInterval) rather than
    // a single "sequencing" pass, since it guarantees only one fetch is ever
    // in flight — the next poll is scheduled only after the previous one
    // resolves, so a slow response can never overlap with, or be overtaken
    // by, a later one.
    function load(showLoadingState: boolean) {
      if (showLoadingState) setState({ phase: 'loading' });

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
          if (showLoadingState) {
            setState({ phase: 'error', message: describeError(error) });
          } else {
            // Never replace an already-displayed leaderboard with an error
            // over a transient background hiccup, but a failure with no
            // trace anywhere is hard to debug — at least log it.
            console.error('Leaderboard background refresh failed:', error);
          }
        })
        .finally(() => {
          if (!cancelled) timeoutId = window.setTimeout(() => load(false), REFRESH_INTERVAL_MS);
        });
    }

    load(true);

    return () => {
      cancelled = true;
      if (timeoutId != null) window.clearTimeout(timeoutId);
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
        {/* ADR-0021/design-document.md SCREEN-03: scored like golf — this
            corrects the natural "higher number = better" assumption before
            a player reads any rank. Must never be omitted or left implicit
            in the ranking order alone. */}
        <p className="leaderboard-screen__subtitle">Lowest total wins</p>
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
