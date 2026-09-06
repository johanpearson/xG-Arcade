import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConnectDisputeSuggestionsScreen } from './ConnectDisputeSuggestionsScreen';
import type { ConnectDisputeDataCorrectionSuggestion } from '../lib/types';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

const suggestion: ConnectDisputeDataCorrectionSuggestion = {
  id: 'sugg-1',
  connectMatchId: 'match-1',
  connectChainStepId: 'step-1',
  connectChainStepDisputeId: 'dispute-1',
  candidatePlayerId: 'player-1',
  candidatePlayerName: 'Some Candidate',
  precedingPlayerId: 'player-2',
  precedingPlayerName: 'Preceding Player',
  claimedClubName: 'Chelsea',
  createdAt: '2026-09-01T00:00:00Z',
};

// REQ-1414: a new, standalone, deliberately read-only admin screen — mirrors
// SuggestionsScreen.test.tsx's own loading/access-denied/error/ready-state
// conventions for the top-section states, but none of its review/commit/
// reject test cases (this screen has no workflow of its own to exercise).
describe('ConnectDisputeSuggestionsScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1414: shows a loading state while the fetch is in flight', () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => new Promise(() => {})));

    render(<ConnectDisputeSuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);

    expect(screen.getByText('Loading…')).toBeInTheDocument();
  });

  it('REQ-1414: a 403 renders the access-denied message', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403)));

    render(<ConnectDisputeSuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);

    expect(await screen.findByText("You don't have access to this page.")).toBeInTheDocument();
  });

  it('REQ-1414: a 401 calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );

    render(<ConnectDisputeSuggestionsScreen accessToken="token" onAuthError={onAuthError} onBackToAdmin={vi.fn()} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-1414: any other error renders the error message', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500)),
    );

    render(<ConnectDisputeSuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);

    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
  });

  it('REQ-1414: a successful fetch with rows renders them in the table, with no actionable elements besides "Back to admin"', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse([suggestion])));

    render(<ConnectDisputeSuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);

    expect(await screen.findByText('Some Candidate')).toBeInTheDocument();
    expect(screen.getByText('Preceding Player')).toBeInTheDocument();
    expect(screen.getByText('Chelsea')).toBeInTheDocument();
    expect(screen.getByText('match-1')).toBeInTheDocument();
    expect(screen.getByText('2026-09-01T00:00:00Z')).toBeInTheDocument();

    // REQ-1414's own "no workflow" rule — nothing clickable anywhere on the
    // page except the pre-existing navigation button back to admin.
    const buttons = screen.getAllByRole('button');
    expect(buttons).toHaveLength(1);
    expect(buttons[0]).toHaveTextContent('Back to admin');
  });

  it('REQ-1414: an empty list renders the "No dispute suggestions recorded yet." message', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse([])));

    render(<ConnectDisputeSuggestionsScreen accessToken="token" onAuthError={vi.fn()} onBackToAdmin={vi.fn()} />);

    expect(await screen.findByText('No dispute suggestions recorded yet.')).toBeInTheDocument();
  });
});
