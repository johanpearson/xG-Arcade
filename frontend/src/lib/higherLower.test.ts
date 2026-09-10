import { describe, expect, it } from 'vitest';
import { higherLowerCategoryLabel, higherLowerValueLabel } from './higherLower';

describe('higherLowerCategoryLabel', () => {
  it('REQ-1504 gap-fill (2026-09-10): humanizes the two real ADR-0111 category strings', () => {
    expect(higherLowerCategoryLabel('club')).toBe('number of clubs played for');
    expect(higherLowerCategoryLabel('trophy')).toBe('number of trophies won');
  });

  it('REQ-1504 gap-fill: falls back to the raw string for an unrecognized category, rather than blocking rendering', () => {
    expect(higherLowerCategoryLabel('caps')).toBe('caps');
  });
});

describe('higherLowerValueLabel', () => {
  it('REQ-1504 gap-fill: uses the singular unit for a value of exactly 1', () => {
    expect(higherLowerValueLabel('club', 1)).toBe('1 club');
    expect(higherLowerValueLabel('trophy', 1)).toBe('1 trophy');
  });

  it('REQ-1504 gap-fill: uses the plural unit for any other value, including 0', () => {
    expect(higherLowerValueLabel('club', 3)).toBe('3 clubs');
    expect(higherLowerValueLabel('trophy', 0)).toBe('0 trophies');
  });

  it('REQ-1504 gap-fill: falls back to the bare number for an unrecognized category, rather than blocking rendering', () => {
    expect(higherLowerValueLabel('caps', 42)).toBe('42');
  });
});
