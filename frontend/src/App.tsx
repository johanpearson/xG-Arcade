import { useCallback, useEffect, useState } from 'react';
import './App.css';
import { AdminScreen } from './admin/AdminScreen';
import { ApiError, fetchMe } from './lib/api';
import type { CurrentUser } from './lib/types';
import { AuthScreen } from './auth/AuthScreen';
import { DeleteAccountScreen } from './auth/DeleteAccountScreen';
import { GameSelectScreen } from './games/GameSelectScreen';
import { GridScreen } from './grid/GridScreen';
import { LeaderboardScreen } from './leaderboard/LeaderboardScreen';

type HealthState =
  | { phase: 'loading' }
  | { phase: 'healthy'; status: string }
  | { phase: 'error'; message: string };

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';
const ACCESS_TOKEN_STORAGE_KEY = 'xg-arcade-access-token';

// REQ-303 (S-021): 'game-select' is the landing screen shown after login,
// before any game's grid — see docs/backlog.md S-021. 'delete-account'
// (S-039) is reachable only from the header, never a destination anything
// else navigates to. 'admin' (REQ-504, S-026) is likewise reachable only
// from the header's admin-only nav link, never a default destination.
type Screen = 'game-select' | 'grid' | 'leaderboard' | 'delete-account' | 'admin';

function App() {
  const [health, setHealth] = useState<HealthState>({ phase: 'loading' });
  const [accessToken, setAccessToken] = useState<string | null>(() =>
    window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY),
  );
  const [screen, setScreen] = useState<Screen>('game-select');
  // REQ-504: the only signal for whether to show the "Admin" nav link — a
  // non-admin must see no trace of it, regardless of screen size/state.
  const [currentUser, setCurrentUser] = useState<CurrentUser | null>(null);

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

  function handleAuthenticated(token: string) {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, token);
    setAccessToken(token);
    setScreen('game-select');
  }

  const handleLogout = useCallback(() => {
    window.localStorage.removeItem(ACCESS_TOKEN_STORAGE_KEY);
    setAccessToken(null);
    setCurrentUser(null);
    setScreen('game-select');
  }, []);

  // REQ-504: fetched both on a fresh login/signup (accessToken just set by
  // handleAuthenticated) and when restoring a token already in localStorage
  // on initial load — either way, this is the only place `isAdmin` is
  // learned. A 401 here means the token itself is dead, same as any other
  // authenticated call failing — log out rather than silently swallowing it.
  useEffect(() => {
    if (!accessToken) {
      setCurrentUser(null);
      return;
    }

    let cancelled = false;

    fetchMe(accessToken)
      .then((user) => {
        if (!cancelled) setCurrentUser(user);
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        if (error instanceof ApiError && error.status === 401) {
          handleLogout();
        }
        // Any other failure here just leaves currentUser null — the admin
        // nav link stays hidden, but the rest of the app is unaffected.
      });

    return () => {
      cancelled = true;
    };
  }, [accessToken, handleLogout]);

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
        {accessToken && (
          <div className="app__header-actions">
            <button
              type="button"
              className="app__nav-link"
              aria-current={screen === 'leaderboard' ? 'page' : undefined}
              onClick={() => setScreen('leaderboard')}
            >
              Leaderboard
            </button>
            {/* REQ-710 (S-039): the only entry point to account deletion —
                deliberately a plain nav link, not a general settings page,
                since delete-account is the only account action Tier 0 has. */}
            <button
              type="button"
              className="app__nav-link"
              aria-current={screen === 'delete-account' ? 'page' : undefined}
              onClick={() => setScreen('delete-account')}
            >
              Delete account
            </button>
            {/* REQ-504: the entire "no visible entry point" mechanism for a
                non-admin — rendered only once /auth/me confirms isAdmin. */}
            {currentUser?.isAdmin && (
              <button
                type="button"
                className="app__nav-link"
                aria-current={screen === 'admin' ? 'page' : undefined}
                onClick={() => setScreen('admin')}
              >
                Admin
              </button>
            )}
            <button type="button" className="app__logout" onClick={handleLogout}>
              Log out
            </button>
          </div>
        )}
      </header>

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
          ) : (
            // REQ-710: on success there's no account left to show anything
            // else on, so deletion signs out and lands back on AuthScreen —
            // the exact same effect handleLogout already produces.
            <DeleteAccountScreen
              accessToken={accessToken}
              onAccountDeleted={handleLogout}
              onCancel={() => setScreen('game-select')}
              onAuthError={handleLogout}
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
