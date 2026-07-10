import { useEffect, useState } from 'react';
import './App.css';
import { AuthScreen } from './auth/AuthScreen';
import { GridScreen } from './grid/GridScreen';

type HealthState =
  | { phase: 'loading' }
  | { phase: 'healthy'; status: string }
  | { phase: 'error'; message: string };

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';
const ACCESS_TOKEN_STORAGE_KEY = 'xg-arcade-access-token';

function App() {
  const [health, setHealth] = useState<HealthState>({ phase: 'loading' });
  const [accessToken, setAccessToken] = useState<string | null>(() =>
    window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY),
  );

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
  }

  function handleLogout() {
    window.localStorage.removeItem(ACCESS_TOKEN_STORAGE_KEY);
    setAccessToken(null);
  }

  return (
    <div className="app">
      <header className="app__header">
        <h1 className="app__title">xG Arcade</h1>
        {accessToken && (
          <button type="button" className="app__logout" onClick={handleLogout}>
            Log out
          </button>
        )}
      </header>

      <main className="app__main">
        {accessToken ? (
          <GridScreen accessToken={accessToken} onAuthError={handleLogout} />
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
