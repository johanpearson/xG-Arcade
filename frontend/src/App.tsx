import { useEffect, useState } from 'react'
import './App.css'

type HealthState =
  | { phase: 'loading' }
  | { phase: 'healthy'; status: string }
  | { phase: 'error'; message: string }

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? ''

function App() {
  const [health, setHealth] = useState<HealthState>({ phase: 'loading' })

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

  return (
    <section id="center">
      <h1>xG Arcade</h1>
      <p>
        API status: <code data-testid="health-status">{describeHealth(health)}</code>
      </p>
    </section>
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
