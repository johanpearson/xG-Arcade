import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  THEME_PREFERENCE_STORAGE_KEY,
  applyResolvedTheme,
  applyStoredThemePreference,
  getStoredThemePreference,
  resolveTheme,
  setStoredThemePreference,
  useThemePreference,
} from './theme';

// REQ-716/ADR-0034: a minimal fake MediaQueryList — real jsdom in this
// project doesn't implement window.matchMedia at all (see
// Grid.test.tsx/HeaderNav.test.tsx's own comments on this), so every test
// here that needs the "system" path stubs it directly. `setMatches` lets a
// test flip the simulated OS preference and fire the same `change` event a
// real browser would.
function stubMatchMedia(initialMatches: boolean) {
  let matches = initialMatches;
  const listeners = new Set<() => void>();

  const mediaQueryList = {
    get matches() {
      return matches;
    },
    media: '(prefers-color-scheme: dark)',
    addEventListener: (_event: 'change', listener: () => void) => {
      listeners.add(listener);
    },
    removeEventListener: (_event: 'change', listener: () => void) => {
      listeners.delete(listener);
    },
  };

  vi.stubGlobal(
    'matchMedia',
    vi.fn().mockImplementation(() => mediaQueryList),
  );

  return {
    setMatches: (next: boolean) => {
      matches = next;
      listeners.forEach((listener) => listener());
    },
    listenerCount: () => listeners.size,
  };
}

describe('theme (REQ-716)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
    delete document.documentElement.dataset.theme;
  });

  describe('getStoredThemePreference', () => {
    it('REQ-716: defaults to "system" when nothing is stored', () => {
      expect(window.localStorage.getItem(THEME_PREFERENCE_STORAGE_KEY)).toBeNull();
      expect(getStoredThemePreference()).toBe('system');
    });

    it('REQ-716: defaults to "system" for an unrecognized stored value, rather than throwing', () => {
      window.localStorage.setItem(THEME_PREFERENCE_STORAGE_KEY, 'sepia');

      expect(getStoredThemePreference()).toBe('system');
    });

    it('REQ-716: returns a validly stored "light"/"dark" preference as-is', () => {
      window.localStorage.setItem(THEME_PREFERENCE_STORAGE_KEY, 'dark');
      expect(getStoredThemePreference()).toBe('dark');

      window.localStorage.setItem(THEME_PREFERENCE_STORAGE_KEY, 'light');
      expect(getStoredThemePreference()).toBe('light');
    });
  });

  describe('setStoredThemePreference', () => {
    it('REQ-716: persists the preference under THEME_PREFERENCE_STORAGE_KEY', () => {
      setStoredThemePreference('dark');
      expect(window.localStorage.getItem(THEME_PREFERENCE_STORAGE_KEY)).toBe('dark');
    });
  });

  describe('resolveTheme', () => {
    it('REQ-716: "light" and "dark" pin regardless of the OS setting', () => {
      stubMatchMedia(true);
      expect(resolveTheme('light')).toBe('light');
      expect(resolveTheme('dark')).toBe('dark');
    });

    it('REQ-716: "system" resolves to "dark" when the OS prefers dark', () => {
      stubMatchMedia(true);
      expect(resolveTheme('system')).toBe('dark');
    });

    it('REQ-716: "system" resolves to "light" when the OS prefers light', () => {
      stubMatchMedia(false);
      expect(resolveTheme('system')).toBe('light');
    });

    it('REQ-716: "system" falls back to "light" (not a throw) when matchMedia is unavailable, e.g. this project\'s default jsdom test environment', () => {
      expect(typeof window.matchMedia).toBe('undefined');
      expect(resolveTheme('system')).toBe('light');
    });
  });

  describe('applyResolvedTheme / applyStoredThemePreference', () => {
    it('REQ-716: applyResolvedTheme sets data-theme on <html> to the resolved value', () => {
      applyResolvedTheme('dark');
      expect(document.documentElement.dataset.theme).toBe('dark');

      applyResolvedTheme('light');
      expect(document.documentElement.dataset.theme).toBe('light');
    });

    it('REQ-716: applyStoredThemePreference reads localStorage, resolves it, applies it to <html>, and returns the resolved value', () => {
      stubMatchMedia(true);
      window.localStorage.setItem(THEME_PREFERENCE_STORAGE_KEY, 'system');

      const resolved = applyStoredThemePreference();

      expect(resolved).toBe('dark');
      expect(document.documentElement.dataset.theme).toBe('dark');
    });

    it('REQ-716: applyStoredThemePreference applies an explicit "dark" pin even when the OS prefers light', () => {
      stubMatchMedia(false);
      window.localStorage.setItem(THEME_PREFERENCE_STORAGE_KEY, 'dark');

      expect(applyStoredThemePreference()).toBe('dark');
      expect(document.documentElement.dataset.theme).toBe('dark');
    });
  });

  describe('useThemePreference', () => {
    beforeEach(() => {
      window.localStorage.clear();
      delete document.documentElement.dataset.theme;
    });

    it('REQ-716: initializes from the stored preference and applies the resolved theme to <html>', () => {
      stubMatchMedia(false);
      window.localStorage.setItem(THEME_PREFERENCE_STORAGE_KEY, 'dark');

      const { result } = renderHook(() => useThemePreference());

      expect(result.current.preference).toBe('dark');
      expect(document.documentElement.dataset.theme).toBe('dark');
    });

    it('REQ-716: setPreference("light") persists to localStorage, updates state, and re-applies data-theme immediately', () => {
      stubMatchMedia(true);
      const { result } = renderHook(() => useThemePreference());

      act(() => {
        result.current.setPreference('light');
      });

      expect(result.current.preference).toBe('light');
      expect(window.localStorage.getItem(THEME_PREFERENCE_STORAGE_KEY)).toBe('light');
      expect(document.documentElement.dataset.theme).toBe('light');
    });

    it('REQ-716: setPreference("dark") persists, updates state, and applies "dark" regardless of the OS setting', () => {
      stubMatchMedia(false);
      const { result } = renderHook(() => useThemePreference());

      act(() => {
        result.current.setPreference('dark');
      });

      expect(result.current.preference).toBe('dark');
      expect(window.localStorage.getItem(THEME_PREFERENCE_STORAGE_KEY)).toBe('dark');
      expect(document.documentElement.dataset.theme).toBe('dark');
    });

    it('REQ-716: while set to "system", reacts to a prefers-color-scheme change event and re-applies the newly resolved theme without needing setPreference called again', () => {
      const media = stubMatchMedia(false);
      window.localStorage.setItem(THEME_PREFERENCE_STORAGE_KEY, 'system');

      renderHook(() => useThemePreference());

      expect(document.documentElement.dataset.theme).toBe('light');

      act(() => {
        media.setMatches(true);
      });

      expect(document.documentElement.dataset.theme).toBe('dark');
    });

    it('REQ-716: once pinned to "light"/"dark", a subsequent OS-level prefers-color-scheme change is ignored (no listener left active)', () => {
      const media = stubMatchMedia(false);
      const { result } = renderHook(() => useThemePreference());

      act(() => {
        result.current.setPreference('light');
      });

      expect(media.listenerCount()).toBe(0);

      act(() => {
        media.setMatches(true);
      });

      // Still "light" — the OS flipping to "dark" has no effect once pinned.
      expect(document.documentElement.dataset.theme).toBe('light');
    });
  });
});
