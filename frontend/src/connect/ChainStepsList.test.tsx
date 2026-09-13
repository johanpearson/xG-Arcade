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
    closingClubName: null,
    closingOverlapStartYear: null,
    closingOverlapEndYear: null,
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
    const steps = [
      step({
        position: 1,
        candidatePlayerName: 'Bridge Player',
        closesChain: true,
        closingClubName: 'Chelsea',
        closingOverlapStartYear: 2012,
        closingOverlapEndYear: 2019,
      }),
    ];

    render(
      <ChainStepsList targetPlayerName="Lionel Messi" otherTargetPlayerName="Cristiano Ronaldo" steps={steps} />,
    );

    expect(screen.getByText(/connects to your target/)).toBeInTheDocument();
    const items = screen.getAllByRole('listitem').map((item) => item.textContent);
    expect(items[items.length - 1]).toBe('Cristiano Ronaldo');
    expect(items[items.length - 1]).not.toContain('not yet connected');
  });

  it('REQ-1406 gap-fill (2026-09-09): shows the closing club and overlap years, not a bare "connects to your target" label', () => {
    const steps = [
      step({
        position: 1,
        candidatePlayerName: 'Bridge Player',
        closesChain: true,
        closingClubName: 'Chelsea',
        closingOverlapStartYear: 2012,
        closingOverlapEndYear: 2019,
      }),
    ];

    render(
      <ChainStepsList targetPlayerName="Lionel Messi" otherTargetPlayerName="Cristiano Ronaldo" steps={steps} />,
    );

    expect(screen.getByText(/connects to your target \(Chelsea, 2012-2019\)/)).toBeInTheDocument();
  });

  it('REQ-1406: shows a bounded overlap as a year range, and an ongoing overlap as ending "present"', () => {
    expect(formatMatchedClub('Chelsea', 2012, 2019)).toBe('Chelsea, 2012-2019');
    expect(formatMatchedClub('Chelsea', 2012, null)).toBe('Chelsea, 2012-present');
  });

  // REQ-1420 display fix: a step whose dispute was Approved sets
  // MatchedClubName but deliberately never populates the overlap years — the
  // club name alone must still render, never a blank/broken output.
  it('REQ-1420: formatMatchedClub returns the club name alone when overlap years are null but the club name is present, and empty only when the club name itself is null', () => {
    expect(formatMatchedClub('Chelsea', null, null)).toBe('Chelsea');
    expect(formatMatchedClub(null, null, null)).toBe('');
  });

  it('REQ-1420: renders no empty "()" for a matched-club (approved-dispute) step whose overlap years are null', () => {
    const steps = [
      step({
        position: 1,
        candidatePlayerName: 'Approved Dispute Link',
        matchedClubName: 'Chelsea',
        matchedOverlapStartYear: null,
        matchedOverlapEndYear: null,
      }),
    ];

    render(
      <ChainStepsList targetPlayerName="Lionel Messi" otherTargetPlayerName="Cristiano Ronaldo" steps={steps} />,
    );

    expect(screen.getByText(/Approved Dispute Link/).textContent).toBe('Approved Dispute Link (Chelsea)');
  });

  it('REQ-1420: renders no empty "()" for the closing-club span when the formatted result is empty', () => {
    const steps = [
      step({
        position: 1,
        candidatePlayerName: 'Bridge Player',
        closesChain: true,
        closingClubName: null,
        closingOverlapStartYear: null,
        closingOverlapEndYear: null,
      }),
    ];

    render(
      <ChainStepsList targetPlayerName="Lionel Messi" otherTargetPlayerName="Cristiano Ronaldo" steps={steps} />,
    );

    const closingItem = screen.getByText(/Bridge Player/);
    expect(closingItem.textContent).toBe('Bridge Player (Some Club, 2010-2015) — connects to your target');
    expect(closingItem.textContent).not.toContain('()');
  });
});
