import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { RoundControlSection } from './RoundControlSection';
import type { AdminActiveRound } from '../lib/types';

// S-156 (docs/backlog.md): dedicated isolation coverage for
// RoundControlSection, extracted from AdminScreen.tsx by S-103 with no
// behavior change. Mirrors AdminScreen.test.tsx's former REQ-505 assertions
// (now removed there as redundant, apart from the two cases that genuinely
// test AdminScreen's own activeRound-gating composition). Renders the
// component directly with `activeRound` supplied as a prop (never fetched by
// this component itself) — only /admin/rounds/xg-grid/* action routes need
// stubbing here.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

const activeRound: AdminActiveRound = {
  hasActiveRound: true,
  round: {
    roundId: 'round-1',
    sequenceNumber: 12,
    gameKey: 'xg-grid',
    startTime: '2026-07-19T00:00:00Z',
    endTime: '2026-07-20T00:00:00Z',
  },
};

const noActiveRound: AdminActiveRound = { hasActiveRound: false, round: null };

describe('RoundControlSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-505/REQ-304: renders "Grid Round #N · ends {endTime}" for an active round, never the raw roundId GUID', () => {
    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={vi.fn()}
        onRefresh={vi.fn()}
      />,
    );

    expect(screen.getByText('Round control — xg-grid')).toBeInTheDocument();
    expect(screen.getByText('Grid Round #12 · ends 2026-07-20T00:00:00Z')).toBeInTheDocument();
    expect(screen.queryByText(/round-1/)).not.toBeInTheDocument();
  });

  it('REQ-505: shows "No active round right now." and no "End round now" action when hasActiveRound is false', () => {
    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={noActiveRound}
        onAuthError={vi.fn()}
        onRefresh={vi.fn()}
      />,
    );

    expect(screen.getByText('No active round right now.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'End round now' })).not.toBeInTheDocument();
    // The end-time update form is unrelated to hasActiveRound and still renders.
    expect(screen.getByLabelText('New end time')).toBeInTheDocument();
  });

  it('REQ-505: "End round now" requires a second, explicit confirm click before calling the close endpoint', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse(activeRound.round));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={vi.fn()}
        onRefresh={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'End round now' }));
    expect(fetchMock).not.toHaveBeenCalledWith(expect.stringContaining('/close'), expect.anything());
    expect(screen.getByRole('button', { name: 'Yes, end round now' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Yes, end round now' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/admin/rounds/xg-grid/close'),
        expect.objectContaining({ method: 'POST' }),
      ),
    );
  });

  it('REQ-505: "Cancel" during the end-round confirm step does not call the close endpoint', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={vi.fn()}
        onRefresh={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'End round now' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.getByRole('button', { name: 'End round now' })).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ-505: a successful "Yes, end round now" calls onRefresh', async () => {
    const onRefresh = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse(activeRound.round)));
    const user = userEvent.setup();

    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={vi.fn()}
        onRefresh={onRefresh}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'End round now' }));
    await user.click(screen.getByRole('button', { name: 'Yes, end round now' }));

    await waitFor(() => expect(onRefresh).toHaveBeenCalledTimes(1));
  });

  it('REQ-505: a 401 while ending the round calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );
    const user = userEvent.setup();

    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={onAuthError}
        onRefresh={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'End round now' }));
    await user.click(screen.getByRole('button', { name: 'Yes, end round now' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-505: a non-401 error ending the round shows an inline error and keeps the confirm prompt open', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Server error', detail: 'Close failed.' }, 500)),
    );
    const user = userEvent.setup();

    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={vi.fn()}
        onRefresh={vi.fn()}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'End round now' }));
    await user.click(screen.getByRole('button', { name: 'Yes, end round now' }));

    expect(await screen.findByText('Close failed.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Yes, end round now' })).toBeInTheDocument();
  });

  it('REQ-505: "Update end time" is required and disabled until a value is entered', () => {
    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={vi.fn()}
        onRefresh={vi.fn()}
      />,
    );

    expect(screen.getByLabelText('New end time')).toBeRequired();
  });

  it('REQ-505: submitting a new end time sends it as ISO via PUT, clears the field, and calls onRefresh', async () => {
    const onRefresh = vi.fn().mockResolvedValue(undefined);
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ ...activeRound.round, endTime: '2026-07-21T00:00:00Z' }));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={vi.fn()}
        onRefresh={onRefresh}
      />,
    );

    const input = screen.getByLabelText('New end time');
    await user.type(input, '2026-07-21T09:30');
    await user.click(screen.getByRole('button', { name: 'Update end time' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/admin/rounds/xg-grid/end-time'),
        expect.objectContaining({ method: 'PUT' }),
      ),
    );
    await waitFor(() => expect(onRefresh).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByLabelText('New end time')).toHaveValue(''));
  });

  it('REQ-505: a 400 (invalid end time) is shown inline without crashing', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({ title: 'Bad Request', detail: 'End time must be after the round start time.' }, 400),
      ),
    );
    const user = userEvent.setup();

    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={vi.fn()}
        onRefresh={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText('New end time'), '2020-01-01T00:00');
    await user.click(screen.getByRole('button', { name: 'Update end time' }));

    expect(await screen.findByText('End time must be after the round start time.')).toBeInTheDocument();
  });

  it('REQ-505: a 401 while updating the end time calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );
    const user = userEvent.setup();

    render(
      <RoundControlSection
        accessToken="token"
        gameKey="xg-grid"
        roundLabel="Grid Round"
        activeRound={activeRound}
        onAuthError={onAuthError}
        onRefresh={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText('New end time'), '2026-07-21T09:30');
    await user.click(screen.getByRole('button', { name: 'Update end time' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});
