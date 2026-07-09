import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import App from '../../src/App'

// Placeholder for S-001 (repo/pipeline skeleton, docs/backlog.md) — no
// REQ-xxx exists yet for this project's actual behavior, so this proves
// Vitest + Testing Library render a component until real UI lands (S-010).
describe('App', () => {
  it('renders without crashing', () => {
    render(<App />)

    expect(screen.getByText('Get started')).toBeInTheDocument()
  })
})
