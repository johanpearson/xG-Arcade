import { useCallback, useEffect, useState } from 'react';

// REQ-716/ADR-0034: an explicit System/Light/Dark toggle, persisted in
// localStorage under this key — device-local, same pattern ADR-0033 already
// established for the refresh token (see App.tsx's ACCESS_TOKEN_STORAGE_KEY/
// REFRESH_TOKEN_STORAGE_KEY). No User-level/account-synced preference for
// v1 — see ADR-0034 for the full alternatives-considered record.
export const THEME_PREFERENCE_STORAGE_KEY = 'xg-arcade-theme-preference';

export type ThemePreference = 'system' | 'light' | 'dark';
export type ResolvedTheme = 'light' | 'dark';

const VALID_PREFERENCES: readonly ThemePreference[] = ['system', 'light', 'dark'];

function isThemePreference(value: string | null): value is ThemePreference {
  return value !== null && (VALID_PREFERENCES as readonly string[]).includes(value);
}

// REQ-716: defaults to "system" when unset (or when the stored value is
// anything unrecognized) — a player who's never opened Settings still gets
// a sensible, lighting-condition-appropriate result (ADR-0034).
export function getStoredThemePreference(): ThemePreference {
  const stored = window.localStorage.getItem(THEME_PREFERENCE_STORAGE_KEY);
  return isThemePreference(stored) ? stored : 'system';
}

export function setStoredThemePreference(preference: ThemePreference): void {
  window.localStorage.setItem(THEME_PREFERENCE_STORAGE_KEY, preference);
}

// jsdom in this project's test environment does not implement
// `window.matchMedia` at all (see Grid.test.tsx/HeaderNav.test.tsx's own
// comments documenting this) — feature-detected here so every existing test
// that mounts App (and, through it, this module's useThemePreference hook)
// keeps working unstubbed, silently resolving "system" to "light" rather
// than throwing. Tests that need to exercise the real system-preference
// path stub `window.matchMedia` themselves (see theme.test.ts).
function systemPrefersDark(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return false;
  }
  return window.matchMedia('(prefers-color-scheme: dark)').matches;
}

// REQ-716/ADR-0034: "System" resolves prefers-color-scheme at the moment
// it's read; "Light"/"Dark" pin the theme regardless of the OS setting.
export function resolveTheme(preference: ThemePreference): ResolvedTheme {
  if (preference === 'system') {
    return systemPrefersDark() ? 'dark' : 'light';
  }
  return preference;
}

// REQ-716/ADR-0034: applied as a `data-theme` attribute on <html> — index.css's
// `:root[data-theme='dark']` block (design-document.md §2's "Dark theme"
// subsection) is what actually repaints the token values; this function is
// only the mechanism that flips the attribute.
export function applyResolvedTheme(resolved: ResolvedTheme): void {
  document.documentElement.dataset.theme = resolved;
}

// REQ-716/ADR-0034: called as early as possible (main.tsx, before the React
// tree mounts) so there's no flash of the wrong theme on first paint.
export function applyStoredThemePreference(): ResolvedTheme {
  const resolved = resolveTheme(getStoredThemePreference());
  applyResolvedTheme(resolved);
  return resolved;
}

// REQ-716: the React-side half of the mechanism — mounted once, at the top
// of App.tsx (so it's active regardless of which screen is showing, not
// only while SettingsScreen itself is mounted), it re-applies the resolved
// theme whenever the stored preference changes, and — while pinned to
// "system" specifically — reacts to the OS-level prefers-color-scheme
// changing without requiring a reload, per ADR-0034's mechanism decision.
export function useThemePreference(): {
  preference: ThemePreference;
  setPreference: (preference: ThemePreference) => void;
} {
  const [preference, setPreferenceState] = useState<ThemePreference>(() => getStoredThemePreference());

  // Keeps <html>'s data-theme in sync with whatever `preference` currently
  // is — a no-op on first render in the real app (main.tsx's
  // applyStoredThemePreference() already applied the same value before this
  // component ever mounted), but the single source of truth for every
  // change after that.
  useEffect(() => {
    applyResolvedTheme(resolveTheme(preference));
  }, [preference]);

  // REQ-716's own accept criterion: while set to "system", listen for the
  // `change` event on the same prefers-color-scheme media query and
  // re-resolve/re-apply reactively while the app is open, not just at load.
  // Only subscribed while `preference === 'system'` — pinned Light/Dark
  // never re-derives from the OS setting.
  useEffect(() => {
    if (preference !== 'system') return;
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;

    const mediaQuery = window.matchMedia('(prefers-color-scheme: dark)');
    const handleChange = () => applyResolvedTheme(resolveTheme('system'));

    mediaQuery.addEventListener('change', handleChange);
    return () => mediaQuery.removeEventListener('change', handleChange);
  }, [preference]);

  const setPreference = useCallback((next: ThemePreference) => {
    setStoredThemePreference(next);
    setPreferenceState(next);
  }, []);

  return { preference, setPreference };
}
