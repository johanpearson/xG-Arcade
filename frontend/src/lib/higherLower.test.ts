import { describe, expect, it } from 'vitest';
import { higherLowerCategoryLabel, higherLowerValueLabel } from './higherLower';

describe('higherLowerCategoryLabel', () => {
  it('REQ-1504 gap-fill (2026-09-10), updated S-231 (ADR-0112): humanizes the three real category strings', () => {
    expect(higherLowerCategoryLabel('trophy')).toBe('number of trophies won');
    expect(higherLowerCategoryLabel('international-caps')).toBe('number of international caps');
    expect(higherLowerCategoryLabel('international-goals')).toBe('number of international goals');
  });

  it('REQ-1504 gap-fill: falls back to the raw string for an unrecognized category, rather than blocking rendering', () => {
    expect(higherLowerCategoryLabel('club')).toBe('club');
  });
});

describe('higherLowerValueLabel', () => {
  it('REQ-1504 gap-fill: uses the singular unit for a value of exactly 1', () => {
    expect(higherLowerValueLabel('trophy', 1)).toBe('1 trophy');
    expect(higherLowerValueLabel('international-caps', 1)).toBe('1 cap');
    expect(higherLowerValueLabel('international-goals', 1)).toBe('1 goal');
  });

  it('REQ-1504 gap-fill: uses the plural unit for any other value, including 0', () => {
    expect(higherLowerValueLabel('trophy', 0)).toBe('0 trophies');
    expect(higherLowerValueLabel('international-caps', 91)).toBe('91 caps');
    expect(higherLowerValueLabel('international-goals', 0)).toBe('0 goals');
  });

  it('REQ-1504 gap-fill: falls back to the bare number for an unrecognized category, rather than blocking rendering', () => {
    expect(higherLowerValueLabel('club', 42)).toBe('42');
  });
});
