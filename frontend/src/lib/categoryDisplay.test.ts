import { describe, expect, it } from 'vitest';
import { clubInitials, flagEmojiFor } from './categoryDisplay';

describe('flagEmojiFor', () => {
  it('REQ-107: returns a flag emoji for a Tier 0 country', () => {
    expect(flagEmojiFor('France')).toBe('🇫🇷');
  });

  it('REQ-107: returns null instead of blocking rendering for an unknown country', () => {
    expect(flagEmojiFor('Wakanda')).toBeNull();
  });
});

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
