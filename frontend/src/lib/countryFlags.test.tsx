import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { CountryFlag, hasCountryFlag } from './countryFlags';

// Bug fix (2026-08-03, user-tester report): replaces the old Unicode-emoji
// flagEmojiFor tests (categoryDisplay.test.ts) — flags are now bundled SVGs
// so they render identically regardless of the host OS/browser's font
// support (see this file's own top-of-file comment for the Windows Chrome/
// Edge bug this fixes).
describe('hasCountryFlag', () => {
  it('REQ-107: true for a Tier 0 country', () => {
    expect(hasCountryFlag('France')).toBe(true);
  });

  it('REQ-107: false instead of blocking rendering for an unknown country', () => {
    expect(hasCountryFlag('Wakanda')).toBe(false);
  });
});

describe('CountryFlag', () => {
  it('REQ-107: renders an inline <svg>, never text that depends on the host font, for a known country', () => {
    const { container } = render(<CountryFlag countryName="United Kingdom" />);

    const svg = container.querySelector('svg');
    expect(svg).toBeInTheDocument();
    expect(svg?.textContent).toBe('');
  });

  it('REQ-107: renders nothing for an unknown country, never blocking rendering', () => {
    const { container } = render(<CountryFlag countryName="Wakanda" />);

    expect(container.querySelector('svg')).not.toBeInTheDocument();
    expect(container).toBeEmptyDOMElement();
  });

  it('renders every Tier 0 country without throwing', () => {
    const tier0Countries = [
      'Brazil', 'Argentina', 'France', 'Germany', 'Spain', 'United Kingdom',
      'Italy', 'Netherlands', 'Portugal', 'Belgium', 'Croatia', 'Uruguay',
      'Colombia', 'Nigeria', 'Senegal', 'Ivory Coast', 'Serbia', 'Poland',
      'Sweden', 'Denmark',
    ];

    for (const country of tier0Countries) {
      expect(hasCountryFlag(country), country).toBe(true);
      const { container } = render(<CountryFlag countryName={country} />);
      expect(container.querySelector('svg'), country).toBeInTheDocument();
    }
  });
});
