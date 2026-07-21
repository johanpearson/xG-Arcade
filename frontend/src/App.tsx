import { useCallback, useEffect, useState } from 'react';
import './App.css';
import { AdminScreen } from './admin/AdminScreen';
import { ApiError, fetchMe, refreshAccessToken } from './lib/api';
import type { CurrentUser } from './lib/types';
import { AuthScreen } from './auth/AuthScreen';
import { GameSelectScreen } from './games/GameSelectScreen';
import { GridScreen } from './grid/GridScreen';
import { HeaderNav } from './nav/HeaderNav';
import { LeaderboardScreen } from './leaderboard/LeaderboardScreen';
import { LeaguesScreen } from './leagues/LeaguesScreen';
import { SettingsScreen } from './settings/SettingsScreen';
import { useThemePreference } from './lib/theme';

type HealthState =
  | { phase: 'loading' }
  | { phase: 'healthy'; status: string }
  | { phase: 'error'; message: string };

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';
const ACCESS_TOKEN_STORAGE_KEY = 'xg-arcade-access-token';
// REQ-715/ADR-0033: same localStorage mechanism as the access token above,
// under its own key — see that ADR for why localStorage (not a cookie) was
// chosen and the XSS trade-off that decision accepts.
const REFRESH_TOKEN_STORAGE_KEY = 'xg-arcade-refresh-token';

// REQ-303 (S-021): 'game-select' is the landing screen shown after login,
// before any game's grid — see docs/backlog.md S-021. 'settings' (REQ-713,
// superseding S-039's standalone 'delete-account' screen) is reachable only
// from the header's "Settings" nav entry, never a destination anything else
// navigates to — it hosts the unchanged delete-account flow plus, for
// admins only, a link onward to 'admin'. 'admin' (REQ-504, S-026) is in
// turn reachable only from that Settings-screen link, never a default
// destination. 'leagues' (REQ-402/403) is reachable from the header's
// "Leagues" nav entry — create/join a custom league and see which ones the
// player belongs to; no per-league leaderboard yet (REQ-404's separate,
// tracked follow-up work).
type Screen = 'game-select' | 'grid' | 'leaderboard' | 'leagues' | 'settings' | 'admin';

function App() {
  const [health, setHealth] = useState<HealthState>({ phase: 'loading' });
  const [accessToken, setAccessToken] = useState<string | null>(() =>
    window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY),
  );
  const [screen, setScreen] = useState<Screen>('game-select');
  // REQ-504/REQ-713: the only signal for whether SettingsScreen shows its
  // admin-only link onward to AdminScreen — a non-admin must see no trace
  // of it anywhere (nav menu or Settings screen), regardless of state.
  const [currentUser, setCurrentUser] = useState<CurrentUser | null>(null);
  // REQ-717/ADR-0036: see the note on `CurrentUser` in lib/types.ts — the
  // backend's MeResponse has no dedicated `isGuest` field yet (a flagged
  // gap), so this derives it from the one signal that does exist and holds
  // exactly for that purpose today: only a guest account has a null email.
  const isGuest = currentUser !== null && currentUser.email === null;
  // REQ-716/ADR-0034: mounted here (not inside SettingsScreen) so the
  // "system" preference's reactive prefers-color-scheme listener stays
  // active regardless of which screen is showing, not only while Settings
  // itself is open. main.tsx's applyStoredThemePreference() already applied
  // the same value before this component ever mounted, so this isn't the
  // first paint of the theme — it's what keeps it in sync after that.
  const { preference: themePreference, setPreference: setThemePreference } = useThemePreference();

  useEffect(() => {
    let cancelled = false

    fetch(`${API_BASE_URL}/health`)
      .then((response) => {
        if (!response.ok) {
          throw new Error(`API responded with ${response.status}`)
        }
        return response.json() as Promise<{ status: string }>
      })
      .then((body) => {
        if (!cancelled) setHealth({ phase: 'healthy', status: body.status })
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          const message = error instanceof Error ? error.message : 'Unknown error'
          setHealth({ phase: 'error', message })
        }
      })

    return () => {
      cancelled = true
    }
  }, [])

  // REQ-715: refreshToken may be null (Supabase can decline to issue one) —
  // that's a real, valid case, not an error; a null just means there's
  // nothing to persist for silent recovery later.
  function handleAuthenticated(token: string, refreshToken: string | null) {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, token);
    if (refreshToken) {
      window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, refreshToken);
    } else {
      window.localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY);
    }
    setAccessToken(token);
    setScreen('game-select');
  }

  // REQ-715: logout (and, via the same handler, DeleteAccountScreen's
  // onAccountDeleted below) clears the refresh token too, not only the
  // access token — a stale refresh token must never outlive an explicit
  // logout.
  const handleLogout = useCallback(() => {
    window.localStorage.removeItem(ACCESS_TOKEN_STORAGE_KEY);
    window.localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY);
    setAccessToken(null);
    setCurrentUser(null);
    setScreen('game-select');
  }, []);

  // REQ-715/ADR-0033: the one place a stored refresh token is exchanged for
  // a new access token — mediated through POST /auth/refresh exactly like
  // login/signup (ADR-0013), never a direct frontend-to-Supabase call. On
  // success, stores the new access token (and, if Supabase's rotation
  // returned one, a new refresh token — otherwise the existing stored
  // refresh token is left untouched rather than assumed dead) and returns
  // it; on any failure (including "no stored refresh token to try")
  // resolves to null so callers can fall through to a full logout without
  // an infinite retry.
  const attemptSilentRefresh = useCallback(async (): Promise<string | null> => {
    const storedRefreshToken = window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY);
    if (!storedRefreshToken) return null;

    try {
      const refreshed = await refreshAccessToken(storedRefreshToken);
      window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, refreshed.accessToken);
      if (refreshed.refreshToken) {
        window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, refreshed.refreshToken);
      }
      setAccessToken(refreshed.accessToken);
      return refreshed.accessToken;
    } catch {
      // Invalid/expired/revoked — the caller falls through to handleLogout,
      // which clears the now-dead refresh token too.
      return null;
    }
  }, []);

  // REQ-504/REQ-715: fetched on a fresh login/signup (accessToken just set
  // by handleAuthenticated), on restoring a token already in localStorage on
  // initial load, and — new for REQ-715 — this is also where a missing or
  // 401'd access token triggers a silent refresh attempt before falling
  // back to a full logout, rather than logging out unconditionally.
  //
  // Both branches below funnel through the same attemptSilentRefresh: on
  // success it calls setAccessToken with the new token, which changes this
  // effect's own dependency and re-runs it — that re-run *is* the retry
  // (fetchMe naturally gets called again with the new token), so there's no
  // separate manual retry path to maintain here.
  useEffect(() => {
    let cancelled = false;

    if (!accessToken) {
      setCurrentUser(null);

      if (!window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)) {
        return;
      }

      attemptSilentRefresh().then((refreshed) => {
        if (!cancelled && !refreshed) {
          handleLogout();
        }
      });

      return () => {
        cancelled = true;
      };
    }

    fetchMe(accessToken)
      .then((user) => {
        if (!cancelled) setCurrentUser(user);
      })
      .catch(async (error: unknown) => {
        if (cancelled) return;
        if (error instanceof ApiError && error.status === 401) {
          const refreshed = await attemptSilentRefresh();
          if (cancelled) return;
          if (!refreshed) {
            handleLogout();
          }
        }
        // Any other failure here just leaves currentUser null — the admin
        // nav link stays hidden, but the rest of the app is unaffected.
      });

    return () => {
      cancelled = true;
    };
  }, [accessToken, handleLogout, attemptSilentRefresh]);

  return (
    <div className="app">
      <header className="app__header">
        {/* xG Arcade is the landing page (S-021's game-select screen) — the
            title itself is the way back to it, so a separate "Games" nav
            link isn't needed alongside it (and "Grid" isn't either: picking
            a game from that landing page is how a player gets there). Fewer
            nav items also means the header no longer wraps onto a second
            line on a narrow phone. */}
        {accessToken ? (
          <button type="button" className="app__title app__title--link" onClick={() => setScreen('game-select')}>
            xG Arcade
          </button>
        ) : (
          <h1 className="app__title">xG Arcade</h1>
        )}
        {/* REQ-712/REQ-713: the header's only nav surface — collapses behind
            a single toggle below the mobile breakpoint (HeaderNav.css),
            renders as the same horizontal row as before at/above it.
            "Settings" (REQ-713) replaces the previously separate "Delete
            account" and admin-only "Admin" top-level links; the admin gate
            itself now lives in SettingsScreen, not here — currentUser?.isAdmin
            is passed straight through, same source of truth REQ-504 already
            used. */}
        {accessToken && (
          <HeaderNav
            isLeaderboardCurrent={screen === 'leaderboard'}
            isLeaguesCurrent={screen === 'leagues'}
            isSettingsCurrent={screen === 'settings'}
            onSelectLeaderboard={() => setScreen('leaderboard')}
            onSelectLeagues={() => setScreen('leagues')}
            onSelectSettings={() => setScreen('settings')}
            onLogout={handleLogout}
          />
        )}
      </header>

      {/* REQ-717/ADR-0036: a low-effort nudge, not a redesign — no SCREEN-xx
          entry mandates this, but a guest playing without realizing their
          progress isn't tied to a recoverable account is a real gap this
          closes cheaply. Only ever renders once currentUser has actually
          resolved to a guest (never during the brief window before GET
          /auth/me returns, same as the admin nav link's own gating). */}
      {accessToken && isGuest && (
        <div className="app__guest-banner">
          <span>Playing as {currentUser?.displayName ?? 'Guest'}.</span>
          <button
            type="button"
            className="app__guest-banner-action"
            onClick={() => setScreen('settings')}
          >
            Save your progress
          </button>
        </div>
      )}

      <main className="app__main">
        {accessToken ? (
          screen === 'game-select' ? (
            // Tier 0 has exactly one game, so any selection routes to
            // 'grid' — the gameKey argument goes unused until a second
            // game module exists to switch on it.
            <GameSelectScreen onSelectGame={() => setScreen('grid')} />
          ) : screen === 'grid' ? (
            <GridScreen accessToken={accessToken} onAuthError={handleLogout} />
          ) : screen === 'leaderboard' ? (
            <LeaderboardScreen accessToken={accessToken} onAuthError={handleLogout} />
          ) : screen === 'admin' ? (
            <AdminScreen accessToken={accessToken} onAuthError={handleLogout} />
          ) : screen === 'leagues' ? (
            <LeaguesScreen accessToken={accessToken} onAuthError={handleLogout} />
          ) : (
            // REQ-713: the "Settings" nav entry's destination — hosts
            // REQ-710's unchanged delete-account flow plus, admin-only, the
            // link onward to 'admin'. onAccountDeleted/onAuthError route
            // through the same handleLogout() as before (REQ-710: no
            // account left to show anything else on, so deletion signs out
            // and lands back on AuthScreen).
            <SettingsScreen
              accessToken={accessToken}
              isAdmin={currentUser?.isAdmin ?? false}
              isGuest={isGuest}
              displayName={currentUser?.displayName ?? ''}
              onDisplayNameUpdated={(displayName) =>
                setCurrentUser((current) => (current ? { ...current, displayName } : current))
              }
              // REQ-717/ADR-0036: the claim response is the full, current
              // MeResponse (email now set, effectively isGuest=false) — a
              // wholesale replace, not a partial patch like
              // onDisplayNameUpdated above, since every field in it is
              // already the server's own confirmed new state.
              onAccountClaimed={(user) => setCurrentUser(user)}
              onAccountDeleted={handleLogout}
              onCancel={() => setScreen('game-select')}
              onAuthError={handleLogout}
              onOpenAdmin={() => setScreen('admin')}
              themePreference={themePreference}
              onThemePreferenceChange={setThemePreference}
            />
          )
        ) : (
          <AuthScreen onAuthenticated={handleAuthenticated} />
        )}
      </main>

      <footer className="app__footer">
        API status: <code data-testid="health-status">{describeHealth(health)}</code>
      </footer>
    </div>
  )
}

function describeHealth(health: HealthState): string {
  switch (health.phase) {
    case 'loading':
      return 'checking…'
    case 'healthy':
      return health.status
    case 'error':
      return `unreachable (${health.message})`
  }
}

export default App
