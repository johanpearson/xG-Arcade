import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

afterEach(cleanup)

// REQ-721/ADR-0039: App.tsx reads/writes `location.hash` for URL-per-screen
// support. jsdom's `window.location` persists across tests within the same
// file (it's the same jsdom window instance, not reset per test), so a hash
// written by one test would otherwise leak into the next — reset it
// globally here rather than in every individual test file's own afterEach.
afterEach(() => {
  window.location.hash = ''
})
