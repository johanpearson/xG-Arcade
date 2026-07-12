import { useEffect, useState } from 'react';
import './App.css';
import { AuthScreen } from './auth/AuthScreen';
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
// before any game's grid — see docs/backlog.md S-021.
type Screen = 'game-select' | 'grid' | 'leaderboard';

function App() {
  const [health, setHealth] = useState<HealthState>({ phase: 'loading' });
  const [accessToken, setAccessToken] = useState<string | null>(() =>
    window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY),
  );
  const [screen, setScreen] = useState<Screen>('game-select');

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

  function handleLogout() {
    window.localStorage.removeItem(ACCESS_TOKEN_STORAGE_KEY);
    setAccessToken(null);
    setScreen('game-select');
  }

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
          ) : (
            <LeaderboardScreen accessToken={accessToken} onAuthError={handleLogout} />
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
