import { describe, expect, it } from 'vitest';
import { clubInitials } from './categoryDisplay';

// Bug fix (2026-08-03): flag lookup (formerly flagEmojiFor here) moved to
// countryFlags.test.tsx alongside its new SVG implementation — see that
// file's own top-of-file comment.

describe('clubInitials', () => {
  it('REQ-107: uses the first two letters of a single-word club name', () => {
    expect(clubInitials('Arsenal')).toBe('AR');
  });

  it('REQ-107: uses one initial per word for a multi-word club name', () => {
    expect(clubInitials('Manchester United')).toBe('MU');
  });

  it('REQ-107: handles extra whitespace without throwing', () => {
    expect(clubInitials('  Bayern   Munich ')).toBe('BM');
  });
});
