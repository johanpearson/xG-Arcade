import { useEffect, useState } from 'react';

// design-document.md §2/§6, SCREEN-10 (S-086): a small JS-side companion to
// this app's existing CSS-only `@media (prefers-reduced-motion: reduce)`
// fallback (see CellState.css's badge-dock/shake keyframes) — needed here,
// specifically, so a reduced-motion preference can gate which *class* a
// component applies at all (no partial-motion state, per SCREEN-10's own
// "nodes just appear" requirement) rather than relying solely on a CSS
// override to neutralize an animation that was still applied. Follows
// lib/theme.ts's `systemPrefersDark`/`useThemePreference` feature-detection
// pattern exactly: jsdom in this project's test environment does not
// implement `window.matchMedia` at all (see Grid.test.tsx/HeaderNav.test.tsx/
// theme.ts's own comments on this), so this resolves to `false` (motion
// allowed) when the API is unavailable, rather than throwing — the same
// "default assumes no preference expressed" reasoning theme.ts's
// `systemPrefersDark` uses for prefers-color-scheme. Tests that need to
// exercise the reduced-motion path stub `window.matchMedia` themselves, same
// as theme.test.ts's `stubMatchMedia` helper does for the dark-theme case.
function prefersReducedMotionNow(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return false;
  }
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

// Reactive: also listens for the OS preference changing while mounted (same
// "system" reactivity useThemePreference already provides for dark mode),
// not just read once at mount.
export function usePrefersReducedMotion(): boolean {
  const [reduced, setReduced] = useState<boolean>(() => prefersReducedMotionNow());

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;

    const mediaQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
    const handleChange = () => setReduced(mediaQuery.matches);

    handleChange();
    mediaQuery.addEventListener('change', handleChange);
    return () => mediaQuery.removeEventListener('change', handleChange);
  }, []);

  return reduced;
}
