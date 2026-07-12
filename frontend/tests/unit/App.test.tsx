import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from '../../src/App'

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response)
}

// Shared by the REQ-303 cases below: a single fetch mock that answers every
// endpoint App's authenticated tree can reach (health check, login, and the
// current round), so logging in and then navigating doesn't need per-test
// wiring beyond the login credentials.
function stubAuthenticatedFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn().mockImplementation((url: string) => {
      const path = String(url)
      if (path.endsWith('/health')) {
        return jsonResponse({ status: 'healthy' })
      }
      if (path.endsWith('/auth/login')) {
        return jsonResponse({ accessToken: 'token-abc', refreshToken: null })
      }
      if (path.endsWith('/rounds/current')) {
        return jsonResponse({ title: 'No active round' }, 404)
      }
      throw new Error(`Unexpected fetch: ${path}`)
    }),
  )
}

async function logIn(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Email'), 'player@example.com')
  await user.type(screen.getByLabelText('Password'), 'password123')
  await user.click(screen.getByRole('button', { name: 'Log in' }))
}

// S-002 (repo/pipeline skeleton -> trivial end-to-end slice, docs/backlog.md):
// no REQ-xxx exists for the health check itself (pure infra, not user-facing
// behavior), so these are named descriptively rather than REQ-prefixed.
describe('App', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows the API health status once the health check resolves', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        json: () => Promise.resolve({ status: 'healthy' }),
      }),
    )

    render(<App />)

    await waitFor(() =>
      expect(screen.getByTestId('health-status')).toHaveTextContent('healthy'),
    )
  })

  it('shows an unreachable status when the health check fails', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('network error')))

    render(<App />)

    await waitFor(() =>
      expect(screen.getByTestId('health-status')).toHaveTextContent('unreachable'),
    )
  })
})

// REQ-303 (S-021): the post-login landing screen is game-selection, not the
// grid, and selecting xG Grid is what navigates to SCREEN-01/GET
// /rounds/current.
describe('App game-selection routing', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    window.localStorage.clear()
  })

  it('REQ-303: lands on the game-selection screen (not the grid) right after login', async () => {
    stubAuthenticatedFetch()
    const user = userEvent.setup()

    render(<App />)
    await logIn(user)

    expect(await screen.findByText('Choose a game')).toBeInTheDocument()
    expect(screen.queryByText('No round to play right now')).not.toBeInTheDocument()
  })

  it('REQ-303: selecting xG Grid navigates from the game-selection screen to the grid', async () => {
    stubAuthenticatedFetch()
    const user = userEvent.setup()

    render(<App />)
    await logIn(user)
    await screen.findByText('Choose a game')

    await user.click(screen.getByRole('button', { name: 'xG Grid' }))

    await waitFor(() =>
      expect(screen.getByText('No round to play right now')).toBeInTheDocument(),
    )
    expect(screen.queryByText('Choose a game')).not.toBeInTheDocument()
  })
})
