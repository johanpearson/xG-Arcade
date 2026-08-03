// Bug fix (2026-08-03, user-tester report): flags used to render as Unicode
// emoji directly from this file (FLAG_EMOJI_BY_COUNTRY/flagEmojiFor) — moved
// to lib/countryFlags.tsx as bundled SVGs instead, since the OS/browser font
// dependency emoji rendering carries broke flags entirely on Windows Chrome/
// Edge (see that file's own top-of-file comment for the full reasoning).
// This file (categoryDisplay.ts) stays plain .ts/JSX-free, so the flag
// lookup — which needs to return actual markup, not a string — lives in the
// .tsx sibling instead.

// First 1-2 letters of the club name, used inside the circular placeholder
// badge (design-document.md §1's "Imagery note" — the real v1 design, not a
// temporary stand-in). Multi-word names use one initial per word (up to 2);
// single-word names use its first two letters.
export function clubInitials(clubName: string): string {
  const words = clubName.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return '';
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase();
  return (words[0][0] + words[1][0]).toUpperCase();
}
