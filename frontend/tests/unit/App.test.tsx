import { render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import App from '../../src/App'

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
