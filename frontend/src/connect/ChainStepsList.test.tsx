import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { formatMatchedClub } from '../lib/connectMatches';
import { ChainStepsList } from './ChainStepsList';
import type { ConnectChainStepView } from '../lib/types';

function step(overrides: Partial<ConnectChainStepView> = {}): ConnectChainStepView {
  return {
    position: 1,
    attemptNumber: 1,
    candidatePlayerId: 'p1',
    candidatePlayerName: 'Some Player',
    matchedClubName: 'Some Club',
    matchedOverlapStartYear: 2010,
    matchedOverlapEndYear: 2015,
    isValid: true,
    closesChain: false,
    submittedAt: '2026-09-03T00:00:00Z',
    ...overrides,
  };
}

// S-218 (design-document.md SCREEN-16): the shared "your chain so far"
// render used by ChainBuilder (mid-match) and MatchResolution (post-match).
describe('ChainStepsList', () => {
  it('REQ-1406: renders only the valid steps, in position order, between the two target players', () => {
    const steps = [
      step({ position: 2, candidatePlayerName: 'Second Link', matchedClubName: 'Club B' }),
      step({ position: 1, candidatePlayerName: 'First Link', matchedClubName: 'Club A' }),
      step({
        position: 1, attemptNumber: 2, isValid: false, candidatePlayerName: 'Failed Guess',
        matchedClubName: null, matchedOverlapStartYear: null, matchedOverlapEndYear: null,
      }),
    ];

    render(
      <ChainStepsList targetPlayerName="Lionel Messi" otherTargetPlayerName="Cristiano Ronaldo" steps={steps} />,
    );

    const items = screen.getAllByRole('listitem').map((item) => item.textContent);
    expect(items[0]).toBe('Lionel Messi');
    expect(items[1]).toContain('First Link');
    expect(items[2]).toContain('Second Link');
    expect(screen.queryByText(/Failed Guess/)).not.toBeInTheDocument();
    expect(items[3]).toContain('Cristiano Ronaldo');
    expect(items[3]).toContain('not yet connected');
  });

  it('REQ-1406: marks the closing step and shows the other target as connected, not pending', () => {
    const steps = [step({ position: 1, candidatePlayerName: 'Bridge Player', closesChain: true })];

    render(
      <ChainStepsList targetPlayerName="Lionel Messi" otherTargetPlayerName="Cristiano Ronaldo" steps={steps} />,
    );

    expect(screen.getByText(/connects to your target/)).toBeInTheDocument();
    const items = screen.getAllByRole('listitem').map((item) => item.textContent);
    expect(items[items.length - 1]).toBe('Cristiano Ronaldo');
    expect(items[items.length - 1]).not.toContain('not yet connected');
  });

  it('REQ-1406: shows a bounded overlap as a year range, and an ongoing overlap as ending "present"', () => {
    expect(formatMatchedClub('Chelsea', 2012, 2019)).toBe('Chelsea, 2012-2019');
    expect(formatMatchedClub('Chelsea', 2012, null)).toBe('Chelsea, 2012-present');
  });
});
