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

// Shared by the REQ-303/REQ-710/REQ-504 cases below: a single fetch mock
// that answers every endpoint App's authenticated tree can reach (health
// check, login, /auth/me, and the current round), so logging in and then
// navigating doesn't need per-test wiring beyond the login credentials.
// `extraRoutes` lets a test add or override endpoints (e.g. /auth/account
// for the delete-account flow, or /auth/me for an admin user) without
// re-implementing the base routing from scratch. /auth/me defaults to a
// non-admin response, since that's the common case across the existing
// REQ-303/REQ-710 suites below.
function stubAuthenticatedFetch(extraRoutes: Record<string, () => Promise<Response>> = {}) {
  vi.stubGlobal(
    'fetch',
    vi.fn().mockImplementation((url: string) => {
      const path = String(url)
      const extraRoute = Object.entries(extraRoutes).find(([suffix]) => path.endsWith(suffix))
      if (extraRoute) return extraRoute[1]()
      if (path.endsWith('/health')) {
        return jsonResponse({ status: 'healthy' })
      }
      if (path.endsWith('/auth/login')) {
        return jsonResponse({ accessToken: 'token-abc', refreshToken: null })
      }
      if (path.endsWith('/auth/me')) {
        return jsonResponse({
          id: 'user-1',
          email: 'player@example.com',
          displayName: 'Player',
          emailConfirmed: true,
          isAdmin: false,
        })
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

  it('REQ-303: the header\'s "xG Arcade" title returns from the grid to the game-selection (landing) screen — nav no longer has separate "Games"/"Grid" links', async () => {
    stubAuthenticatedFetch()
    const user = userEvent.setup()

    render(<App />)
    await logIn(user)
    await screen.findByText('Choose a game')
    await user.click(screen.getByRole('button', { name: 'xG Grid' }))
    await waitFor(() =>
      expect(screen.getByText('No round to play right now')).toBeInTheDocument(),
    )

    expect(screen.queryByRole('button', { name: 'Games' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Grid' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'xG Arcade' }))

    expect(screen.getByText('Choose a game')).toBeInTheDocument()
    expect(screen.queryByText('No round to play right now')).not.toBeInTheDocument()
  })

  it('REQ-303/REQ-710: the nav offers Leaderboard, Delete account, and Log out once authenticated', async () => {
    stubAuthenticatedFetch()
    const user = userEvent.setup()

    render(<App />)
    await logIn(user)
    await screen.findByText('Choose a game')

    expect(screen.getByRole('button', { name: 'Leaderboard' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete account' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Log out' })).toBeInTheDocument()
  })
})

// REQ-710 (S-039): wiring DeleteAccountScreen into App — the nav entry point,
// and both onAccountDeleted/onAuthError routing back through the same
// handleLogout() that clears the stored token and returns to AuthScreen.
describe('App delete-account routing', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    window.localStorage.clear()
  })

  it('REQ-710: clicking "Delete account" in the header navigates to the delete-account screen', async () => {
    stubAuthenticatedFetch()
    const user = userEvent.setup()

    render(<App />)
    await logIn(user)
    await screen.findByText('Choose a game')

    await user.click(screen.getByRole('button', { name: 'Delete account' }))

    expect(
      await screen.findByText('This permanently deletes your account. It cannot be undone.'),
    ).toBeInTheDocument()
    expect(screen.getByLabelText('Current password')).toBeInTheDocument()
    expect(screen.queryByText('Choose a game')).not.toBeInTheDocument()
  })

  it('REQ-710: a successful deletion returns to the logged-out AuthScreen and clears the stored access token', async () => {
    stubAuthenticatedFetch({ '/auth/account': () => jsonResponse(null, 204) })
    const user = userEvent.setup()

    render(<App />)
    await logIn(user)
    await screen.findByText('Choose a game')
    await user.click(screen.getByRole('button', { name: 'Delete account' }))
    await screen.findByLabelText('Current password')

    await user.type(screen.getByLabelText('Current password'), 'correct-password')
    await user.click(screen.getByRole('button', { name: 'Delete my account permanently' }))

    expect(await screen.findByRole('tab', { name: 'Log in' })).toBeInTheDocument()
    expect(window.localStorage.getItem('xg-arcade-access-token')).toBeNull()
  })
})

// REQ-504 (S-026): the entire "no visible entry point" mechanism for a
// non-admin is that the Admin nav link only renders once /auth/me confirms
// isAdmin — these cases prove both sides of that gate.
describe('App admin nav routing', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    window.localStorage.clear()
  })

  it('REQ-504: a non-admin /auth/me response never shows an "Admin" nav link', async () => {
    stubAuthenticatedFetch()
    const user = userEvent.setup()

    render(<App />)
    await logIn(user)
    await screen.findByText('Choose a game')

    expect(screen.queryByRole('button', { name: 'Admin' })).not.toBeInTheDocument()
  })

  it('REQ-504: an admin /auth/me response shows an "Admin" nav link that navigates to the admin screen', async () => {
    stubAuthenticatedFetch({
      '/auth/me': () =>
        jsonResponse({
          id: 'user-2',
          email: 'admin@example.com',
          displayName: 'Admin',
          emailConfirmed: true,
          isAdmin: true,
        }),
      '/admin/player-data/unverified': () => jsonResponse([]),
      '/admin/rounds/xg-grid/active': () => jsonResponse({ title: 'Not found' }, 404),
    })
    const user = userEvent.setup()

    render(<App />)
    await logIn(user)
    await screen.findByText('Choose a game')

    const adminLink = await screen.findByRole('button', { name: 'Admin' })
    await user.click(adminLink)

    expect(await screen.findByText('No unverified data to review.')).toBeInTheDocument()
  })
})
