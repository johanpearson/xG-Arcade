import { useCallback, useEffect, useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { fetchUserStats } from '../lib/userStats';
import { PlayerAvatar } from '../components/PlayerAvatar';
import type { UserStatsResponse } from '../lib/types';
// REQ-410/ADR-0043 (S-087), widened to a third game by REQ-411/REQ-1304
// (S-202, mirroring LeaderboardScreen.tsx's own S-198 extension): the same
// client-side `GameKey` constants GameSelectScreen/HeaderNav/
// LeaderboardScreen already use — no new/duplicate string literal per this
// repo's own established convention (see GameSelectScreen.tsx's own comment
// on why these stay plain constants rather than API-sourced). `GameKey`
// itself is re-exported from LeaderboardScreen.tsx (its own home) rather
// than redefined here.
import { XG_GRID_GAME_KEY, XG_PATH_GAME_KEY, XG_PREDICT_GAME_KEY } from '../games/GameSelectScreen';
import type { GameKey } from '../leaderboard/LeaderboardScreen';
import { SendFriendRequestAction } from '../social/SendFriendRequestAction';
import './UserStatsScreen.css';

export interface UserStatsScreenProps {
  accessToken: string;
  // REQ-411: the player whose stats this instance shows — the *only* thing
  // that distinguishes "own stats" from "another player's stats" (see this
  // component's own top-of-file doc note below). App.tsx passes
  // `currentUser.id`/`currentUser.displayName` for "own stats" and whatever
  // a leaderboard row's `onSelectPlayer` handed it for "another player's."
  userId: string;
  displayName: string;
  onAuthError: () => void;
  onBack: () => void;
  // REQ-1401 (S-217, design-document.md SCREEN-13's 2026-09-03 status
  // note): the currently signed-in account's own id — optional and
  // defaults to hidden (SendFriendRequestAction never mounts) so every
  // existing caller/test that predates this addition is unaffected.
  // App.tsx passes currentUser?.id; only ever mounts the "Send friend
  // request" action when this differs from `userId` above.
  viewerUserId?: string;
  // Optional: lets SendFriendRequestAction link out to SCREEN-15 when the
  // viewed player already sent the viewer a pending request.
  onOpenFriends?: () => void;
}

// REQ-411 (S-179, frontend half of S-178's backend work): SCREEN-13's
// stats/profile view. Deliberately the SAME component for "own stats" and
// "another player's stats" — REQ-411 has no write/own-only action at all
// (its "Viewing another player's stats" acceptance criteria: "no action is
// available on someone else's stats beyond viewing them," and there's no
// own-stats-only action either), so this screen is purely read-only
// regardless of whose `userId` it's showing. The only own-vs-other signal is
// the `userId`/`displayName` props themselves, both seeded once by App.tsx's
// in-memory `statsTarget` state per ADR-0039's "no router library" hash-
// routing convention (see App.tsx's own comment on the same pattern
// `leaderboardInitial`/`LeaderboardRoundTarget` already established).
const GAME_TABS: Array<{ value: GameKey; label: string }> = [
  { value: XG_GRID_GAME_KEY, label: 'xG Grid' },
  { value: XG_PATH_GAME_KEY, label: 'xG Path' },
  // REQ-411/REQ-1304 (S-202): third tab, same order as GameSelectScreen's
  // tiles/HeaderNav's "Games" list/LeaderboardScreen.tsx's own GAME_TABS —
  // unlike LeaderboardScreen.tsx's own xG Predict tab (S-198), this one has
  // no known "renders empty" gap: `GET /users/{userId}/stats` already
  // allowlists xg-predict and its backing `GetUserStatsAsync` was wired to
  // `IRoundScoreSourceResolver` by S-199/ADR-0100, so xG Predict user stats
  // render real figures from day one.
  { value: XG_PREDICT_GAME_KEY, label: 'xG Predict' },
];

// REQ-411/REQ-1304 (S-202), mirroring LeaderboardScreen.tsx's own
// subtitleForGameKey (REQ-404/ADR-0095, S-198): an exhaustive switch, not an
// if/else or ternary chain, so a fourth `GameKey` added later is a compile
// error here too.
function subtitleForGameKey(gameKey: GameKey): string {
  switch (gameKey) {
    case XG_GRID_GAME_KEY:
    case XG_PATH_GAME_KEY:
      return 'Lowest total wins';
    case XG_PREDICT_GAME_KEY:
      return 'Highest total wins';
    default: {
      const _exhaustive: never = gameKey;
      return _exhaustive;
    }
  }
}

type StatsState =
  | { phase: 'loading' }
  | { phase: 'not-found' }
  | { phase: 'error'; message: string }
  | { phase: 'ready'; stats: UserStatsResponse };

// REQ-411: average FinalPoints is a real (non-integer) number server-side —
// shown to one decimal place, trimmed when it's a whole number (e.g. "120
// pts avg", not "120.0 pts avg"), so it reads as a plain figure rather than
// implying false precision beyond what a "points" total normally carries
// elsewhere in this app (leaderboard rows always render whole points).
function formatAverage(average: number): string {
  const rounded = Math.round(average * 10) / 10;
  return Number.isInteger(rounded) ? String(rounded) : rounded.toFixed(1);
}

export function UserStatsScreen({
  accessToken,
  userId,
  displayName,
  onAuthError,
  onBack,
  viewerUserId,
  onOpenFriends,
}: UserStatsScreenProps) {
  const [gameKey, setGameKey] = useState<GameKey>(XG_GRID_GAME_KEY);
  const [state, setState] = useState<StatsState>({ phase: 'loading' });

  const handleAuthError = useCallback(
    (error: unknown): boolean => {
      if (error instanceof ApiError && error.status === 401) {
        onAuthError();
        return true;
      }
      return false;
    },
    [onAuthError],
  );

  // REQ-411: re-fetches on every game-tab switch (and whenever `userId`
  // itself changes, though in practice App.tsx always mounts a fresh
  // instance of this screen per navigation rather than updating `userId` on
  // an already-mounted one — see App.tsx's own `statsTarget` comment) —
  // same "switching games re-fetches, scoped to the new game" rule
  // LeaderboardScreen.tsx's own game switcher already establishes.
  useEffect(() => {
    let cancelled = false;
    setState({ phase: 'loading' });

    fetchUserStats(accessToken, userId, gameKey)
      .then((stats) => {
        if (cancelled) return;
        setState({ phase: 'ready', stats });
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        if (handleAuthError(error)) return;
        // REQ-411: a nonexistent userId is a real, distinct error state —
        // never the same "no rounds played yet" empty state a real player
        // with zero qualifying rounds gets (that's `hasRoundsPlayed: false`
        // on an otherwise-200 response, handled in the 'ready' branch below,
        // not this catch at all).
        if (error instanceof ApiError && error.status === 404) {
          setState({ phase: 'not-found' });
          return;
        }
        setState({ phase: 'error', message: describeError(error) });
      });

    return () => {
      cancelled = true;
    };
  }, [accessToken, userId, gameKey, handleAuthError]);

  return (
    <div className="user-stats-screen">
      <div className="user-stats-screen__header">
        <button type="button" className="user-stats-screen__back" onClick={onBack}>
          Back
        </button>
        {/* REQ-722/S-184: the viewed player's avatar, alongside the heading —
            SCREEN-13's own status note (design-document.md) records this
            addition. Same component, same read-only "no own-vs-other
            concept" rule as the heading text next to it: renders identically
            whether userId is the viewer's own account or another player's. */}
        <div className="user-stats-screen__identity">
          <PlayerAvatar accessToken={accessToken} userId={userId} displayName={displayName} />
          {/* REQ-411: the viewed player's DisplayName in the heading, so it's
              unambiguous whose stats are on screen whether this is "own
              stats" or "another player's" — same heading either way, no
              separate "Your stats" copy branch (this screen has no concept of
              "is this me" beyond the userId/displayName props it was handed). */}
          <h2 className="user-stats-screen__title">{displayName}&apos;s stats</h2>
        </div>
        {/* REQ-1401 (S-217): mounted only when viewerUserId is known and
            differs from the player being viewed — own-profile hidden
            entirely, and every existing caller/test that never passes
            viewerUserId sees no change at all. */}
        {viewerUserId && viewerUserId !== userId && (
          <SendFriendRequestAction
            accessToken={accessToken}
            viewerUserId={viewerUserId}
            targetUserId={userId}
            onAuthError={onAuthError}
            onOpenFriends={onOpenFriends}
          />
        )}
      </div>
      {/* ADR-0021/design-document.md SCREEN-03: same "lowest total wins"
          framing the leaderboard already leads with, under the header —
          the figures below are the same FinalPoints/median metric that note
          already applies to, so the same correction against the natural
          "higher number = better" assumption belongs here too. Shown
          unconditionally (not gated on the ready/hasRoundsPlayed state)
          since it describes how to read points on this screen in general,
          same as SCREEN-03's own placement directly under its title.
          REQ-411/REQ-1304 (S-202): xG Predict is a named exception, same as
          LeaderboardScreen.tsx's own subtitleForGameKey (REQ-404/ADR-0095,
          S-198) — conventional higher-is-better scoring, not golf-style —
          so this line must read the opposite way whenever that tab is
          selected. */}
      <p className="user-stats-screen__subtitle">{subtitleForGameKey(gameKey)}</p>

      <div className="user-stats-screen__game-tabs" role="tablist" aria-label="Game">
        {GAME_TABS.map(({ value, label }) => (
          <button
            key={value}
            type="button"
            role="tab"
            aria-selected={gameKey === value}
            className={`user-stats-screen__game-tab ${gameKey === value ? 'user-stats-screen__game-tab--active' : ''}`}
            onClick={() => setGameKey(value)}
          >
            {label}
          </button>
        ))}
      </div>

      {state.phase === 'loading' && <p className="user-stats-screen__status">Loading stats…</p>}

      {state.phase === 'not-found' && (
        <p className="user-stats-screen__status user-stats-screen__status--error">
          This player couldn&apos;t be found.
        </p>
      )}

      {state.phase === 'error' && (
        <p className="user-stats-screen__status user-stats-screen__status--error">{state.message}</p>
      )}

      {state.phase === 'ready' && !state.stats.hasRoundsPlayed && (
        // design-document.md §5: "empty states are invitations" — distinct,
        // non-blank, non-zero-filled rendering for zero qualifying rounds
        // (REQ-411), never a blank screen and never a row of "0 pts" figures
        // that would misread as a real, played score of zero.
        <p className="user-stats-screen__empty">No rounds played yet for this game.</p>
      )}

      {state.phase === 'ready' && state.stats.hasRoundsPlayed && (
        <dl className="user-stats-screen__stats">
          <div className="user-stats-screen__stat">
            <dt>Rounds played</dt>
            <dd className="mono-figure">{state.stats.roundsPlayed}</dd>
          </div>
          {/* REQ-411 (`UserStatsResponse`'s own contract): bestFinalPoints/
              averageFinalPoints are only nullable in the TYPE because they
              share the shape with the `hasRoundsPlayed: false` case above —
              this branch only ever renders once `hasRoundsPlayed` is true,
              which the backend guarantees means both are actually set (`as
              number` below reflects that guarantee, not a runtime guess). */}
          <div className="user-stats-screen__stat">
            <dt>Best round</dt>
            <dd className="mono-figure">{state.stats.bestFinalPoints as number} pts</dd>
          </div>
          <div className="user-stats-screen__stat">
            <dt>Average round</dt>
            <dd className="mono-figure">{formatAverage(state.stats.averageFinalPoints as number)} pts</dd>
          </div>
          {/* REQ-411: `rank` can independently be null (below REQ-409's
              5-round ranking minimum) even while every other figure above is
              present — omitted cleanly here, never rendered as an error or a
              fabricated "no rank" figure. */}
          {state.stats.rank !== null && (
            <div className="user-stats-screen__stat">
              <dt>All-time rank</dt>
              <dd className="mono-figure">#{state.stats.rank}</dd>
            </div>
          )}
        </dl>
      )}
    </div>
  );
}
