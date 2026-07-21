import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { applyStoredThemePreference } from './lib/theme.ts'

// REQ-716/ADR-0034: applied before the React tree mounts (and before
// index.css's dark-theme block can meaningfully repaint anything) so
// there's no flash of the wrong theme on first paint.
applyStoredThemePreference()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
