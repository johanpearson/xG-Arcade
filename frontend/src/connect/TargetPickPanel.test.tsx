import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TargetPickPanel } from './TargetPickPanel';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function problemResponse(title: string, detail: string, status: number) {
  return jsonResponse({ title, detail }, status);
}

function renderPanel(overrides: Partial<Parameters<typeof TargetPickPanel>[0]> = {}, fetchMock = vi.fn()) {
  vi.stubGlobal('fetch', fetchMock);
  const onAuthError = vi.fn();
  const onSubmitted = vi.fn();
  render(
    <TargetPickPanel
      matchId="match-1"
      accessToken="token"
      myTargetPick={null}
      onAuthError={onAuthError}
      onSubmitted={onSubmitted}
      {...overrides}
    />,
  );
  return { onAuthError, onSubmitted };
}

async function pickSuggestion(user: ReturnType<typeof userEvent.setup>, name: string) {
  await user.type(screen.getByLabelText('Target player name'), name.slice(0, 2));
  await waitFor(() => expect(screen.getByRole('listbox')).toBeInTheDocument());
  await user.click(screen.getByText(name));
}

// REQ-1404 (design-document.md SCREEN-16's "Target-pick phase").
describe('TargetPickPanel', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-1404: shows a "waiting for your opponent" state once locked, with no form', () => {
    renderPanel({ myTargetPick: { targetPlayerId: 'p1', targetPlayerName: 'Lionel Messi', locked: true } });

    expect(screen.getByText(/Waiting for your opponent to lock in their target pick/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Target player name')).not.toBeInTheDocument();
  });

  it('REQ-1404: submitting a selected target calls the target-pick endpoint and notifies the parent on success', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([{ playerId: 'p1', name: 'Lionel Messi' }]);
      if (url.endsWith('/matches/match-1/target-pick')) {
        return jsonResponse({ targetPlayerId: 'p1', selectedAt: '2026-09-03T00:00:00Z', locked: false });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onSubmitted } = renderPanel({}, fetchMock);

    await pickSuggestion(user, 'Lionel Messi');
    await user.click(screen.getByRole('button', { name: 'Set target pick' }));

    await waitFor(() => expect(onSubmitted).toHaveBeenCalledTimes(1));
    const postCall = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST');
    // No wikidataQid on the mocked suggestion above (a row indexed before
    // ADR-0107's column existed) — null is sent, not omitted, so the server
    // falls back to name-only resolution rather than erroring on a missing
    // field.
    expect(JSON.parse(postCall![1].body as string)).toEqual({ targetPlayerName: 'Lionel Messi', targetWikidataQid: null });
  });

  // Bug fix (2026-09-05, ADR-0107): proves the suggestion's wikidataQid — not
  // just its name — reaches the server, which is what lets it resolve the
  // exact real person unambiguously (the real, reported incident this closes:
  // two different real footballers both named "Jonas Olsson").
  it('ADR-0107: submitting a selected target includes the suggestion\'s wikidataQid when it has one', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) {
        return jsonResponse([{ playerId: 'p1', name: 'Jonas Olsson', birthYear: 1983, wikidataQid: 'Q1533537' }]);
      }
      if (url.endsWith('/matches/match-1/target-pick')) {
        return jsonResponse({ targetPlayerId: 'p1', selectedAt: '2026-09-05T00:00:00Z', locked: false });
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onSubmitted } = renderPanel({}, fetchMock);

    await pickSuggestion(user, 'Jonas Olsson');
    await user.click(screen.getByRole('button', { name: 'Set target pick' }));

    await waitFor(() => expect(onSubmitted).toHaveBeenCalledTimes(1));
    const postCall = fetchMock.mock.calls.find(([, init]) => init?.method === 'POST');
    expect(JSON.parse(postCall![1].body as string)).toEqual({
      targetPlayerName: 'Jonas Olsson',
      targetWikidataQid: 'Q1533537',
    });
  });

  it('REQ-1404: a "Target player not found" 404 shows the server\'s own detail text and lets the player pick again', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([{ playerId: 'p1', name: 'Lionel Messi' }]);
      if (url.endsWith('/matches/match-1/target-pick')) {
        return problemResponse(
          'Target player not found',
          'No known player matches that name. Check the spelling and try again.',
          404,
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onSubmitted } = renderPanel({}, fetchMock);

    await pickSuggestion(user, 'Lionel Messi');
    await user.click(screen.getByRole('button', { name: 'Set target pick' }));

    expect(await screen.findByText(/Check the spelling and try again/)).toBeInTheDocument();
    expect(onSubmitted).not.toHaveBeenCalled();
    // The field is cleared so a genuinely different target must be searched.
    expect((screen.getByLabelText('Target player name') as HTMLInputElement).value).toBe('');
  });

  it('REQ-1404: a trivially-connected rejection shows the server\'s own detail text and lets the player pick again', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([{ playerId: 'p1', name: 'Lionel Messi' }]);
      if (url.endsWith('/matches/match-1/target-pick')) {
        return problemResponse(
          'Target picks are already connected',
          'These two target players already share a club with an overlapping time period. Pick a different target instead.',
          409,
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onSubmitted } = renderPanel({}, fetchMock);

    await pickSuggestion(user, 'Lionel Messi');
    await user.click(screen.getByRole('button', { name: 'Set target pick' }));

    expect(await screen.findByText(/Pick a different target instead/)).toBeInTheDocument();
    expect(onSubmitted).not.toHaveBeenCalled();
    // The field is cleared so a genuinely different target must be searched.
    expect((screen.getByLabelText('Target player name') as HTMLInputElement).value).toBe('');
  });

  it('REQ-1404: a 503 live-lookup-unavailable failure shows a retry message', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([{ playerId: 'p1', name: 'Lionel Messi' }]);
      if (url.endsWith('/matches/match-1/target-pick')) {
        return problemResponse(
          'Live verification unavailable',
          "We couldn't verify this target pick against our live data source in time. Please try again.",
          503,
        );
      }
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    renderPanel({}, fetchMock);

    await pickSuggestion(user, 'Lionel Messi');
    await user.click(screen.getByRole('button', { name: 'Set target pick' }));

    expect(await screen.findByText(/Please try again/)).toBeInTheDocument();
  });

  it('REQ-1404: shows the current (unlocked) pick and still allows changing it', () => {
    renderPanel({ myTargetPick: { targetPlayerId: 'p1', targetPlayerName: 'Lionel Messi', locked: false } });

    expect(screen.getByText(/Current pick:/)).toBeInTheDocument();
    expect(screen.getByText('Lionel Messi')).toBeInTheDocument();
    expect(screen.getByLabelText('Target player name')).toBeInTheDocument();
  });

  it('a 401 while submitting calls onAuthError', async () => {
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/players/autocomplete')) return jsonResponse([{ playerId: 'p1', name: 'Lionel Messi' }]);
      if (url.endsWith('/matches/match-1/target-pick')) return problemResponse('Unauthorized', 'Unauthorized', 401);
      throw new Error(`Unexpected fetch: ${url}`);
    });
    const user = userEvent.setup();
    const { onAuthError } = renderPanel({}, fetchMock);

    await pickSuggestion(user, 'Lionel Messi');
    await user.click(screen.getByRole('button', { name: 'Set target pick' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});
