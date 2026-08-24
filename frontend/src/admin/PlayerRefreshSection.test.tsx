import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PlayerRefreshSection } from './PlayerRefreshSection';

// REQ-513/514: dedicated isolation coverage for PlayerRefreshSection,
// mirroring UserDeletionSection.test.tsx's own shape (renders the
// component directly, stubs global fetch per test) — this is the closest
// precedent per REQ-514's own note.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function bareNotFound() {
  return Promise.resolve({
    ok: false,
    status: 404,
    json: () => Promise.reject(new Error('no body')),
  } as unknown as Response);
}

const PLAYER_ID = '3fa85f64-5717-4562-b3fc-2c963f66afa6';

describe('PlayerRefreshSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-514: renders "Refresh a player from Wikidata" with a Player id field, and the button starts disabled', () => {
    render(<PlayerRefreshSection accessToken="token" onAuthError={vi.fn()} />);

    expect(screen.getByText('Refresh a player from Wikidata')).toBeInTheDocument();
    expect(screen.getByLabelText('Player id')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh from Wikidata' })).toBeDisabled();
  });

  it('REQ-514: typing a player id enables the refresh button, with no confirm step needed', async () => {
    const user = userEvent.setup();
    render(<PlayerRefreshSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Player id'), PLAYER_ID);

    expect(screen.getByRole('button', { name: 'Refresh from Wikidata' })).toBeEnabled();
  });

  it('REQ-514: submitting calls the refresh endpoint directly (no confirm click) and shows a pending, disabled state while in flight', async () => {
    let resolveFetch: (value: Response) => void = () => {};
    const fetchMock = vi.fn().mockImplementation(
      () =>
        new Promise<Response>((resolve) => {
          resolveFetch = resolve;
        }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<PlayerRefreshSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Player id'), PLAYER_ID);
    await user.click(screen.getByRole('button', { name: 'Refresh from Wikidata' }));

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`/admin/players/${PLAYER_ID}/refresh-from-wikidata`),
      expect.objectContaining({ method: 'POST' }),
    );
    expect(screen.getByRole('button', { name: 'Refreshing…' })).toBeDisabled();
    expect(screen.getByLabelText('Player id')).toBeDisabled();

    resolveFetch(
      (await jsonResponse({
        playerId: PLAYER_ID,
        wikidataQid: 'Q123',
        fields: [
          { field: 'fullName', changed: false, oldValue: 'Thierry Henry', newValue: null },
          { field: 'position', changed: false, oldValue: 'Forward', newValue: null },
          { field: 'birthYear', changed: false, oldValue: '1977', newValue: null },
          { field: 'photoUrl', changed: false, oldValue: null, newValue: null },
        ],
      })) as unknown as Response,
    );

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Refresh from Wikidata' })).toBeEnabled(),
    );
  });

  it('REQ-514: a successful response with a changed field shows old/new values, and unchanged fields are visibly distinguished', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          playerId: PLAYER_ID,
          wikidataQid: 'Q123',
          fields: [
            { field: 'fullName', changed: true, oldValue: 'Thiery Henri', newValue: 'Thierry Henry' },
            { field: 'position', changed: false, oldValue: 'Forward', newValue: null },
            { field: 'birthYear', changed: false, oldValue: '1977', newValue: null },
            { field: 'photoUrl', changed: false, oldValue: null, newValue: null },
          ],
        }),
      ),
    );
    const user = userEvent.setup();

    render(<PlayerRefreshSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Player id'), PLAYER_ID);
    await user.click(screen.getByRole('button', { name: 'Refresh from Wikidata' }));

    expect(
      await screen.findByText('Full name: Changed — "Thiery Henri" → "Thierry Henry"'),
    ).toBeInTheDocument();
    expect(screen.getByText('Position: Unchanged — "Forward"')).toBeInTheDocument();
    expect(screen.getByText('Birth year: Unchanged — "1977"')).toBeInTheDocument();
    expect(screen.getByText('Photo URL: Unchanged — "(none)"')).toBeInTheDocument();
  });

  it('REQ-514: a successful response with zero changed fields still renders all four fields as unchanged, with their current stored values', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse({
          playerId: PLAYER_ID,
          wikidataQid: 'Q123',
          fields: [
            { field: 'fullName', changed: false, oldValue: 'Thierry Henry', newValue: null },
            { field: 'position', changed: false, oldValue: 'Forward', newValue: null },
            { field: 'birthYear', changed: false, oldValue: '1977', newValue: null },
            { field: 'photoUrl', changed: false, oldValue: 'https://example.com/henry.jpg', newValue: null },
          ],
        }),
      ),
    );
    const user = userEvent.setup();

    render(<PlayerRefreshSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Player id'), PLAYER_ID);
    await user.click(screen.getByRole('button', { name: 'Refresh from Wikidata' }));

    expect(await screen.findByText('Full name: Unchanged — "Thierry Henry"')).toBeInTheDocument();
    expect(screen.getByText('Position: Unchanged — "Forward"')).toBeInTheDocument();
    expect(screen.getByText('Birth year: Unchanged — "1977"')).toBeInTheDocument();
    expect(
      screen.getByText('Photo URL: Unchanged — "https://example.com/henry.jpg"'),
    ).toBeInTheDocument();
    expect(screen.queryByText(/Changed —/)).not.toBeInTheDocument();
  });

  it('REQ-514: a 404 (player not found) shows a specific message, not a generic error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => bareNotFound()));
    const user = userEvent.setup();

    render(<PlayerRefreshSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Player id'), PLAYER_ID);
    await user.click(screen.getByRole('button', { name: 'Refresh from Wikidata' }));

    expect(await screen.findByText('No player found with that id.')).toBeInTheDocument();
  });

  it('REQ-514: a 409 (no WikidataQid) shows a specific message, not a generic error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse(
          {
            title: 'No Wikidata QID to refresh from',
            detail: 'This player has no WikidataQid on record — there is nothing to refresh from.',
          },
          409,
        ),
      ),
    );
    const user = userEvent.setup();

    render(<PlayerRefreshSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Player id'), PLAYER_ID);
    await user.click(screen.getByRole('button', { name: 'Refresh from Wikidata' }));

    expect(
      await screen.findByText('This player has no Wikidata id to refresh from.'),
    ).toBeInTheDocument();
  });

  it('REQ-514: a 503 (lookup unavailable) shows a specific message, not a generic error', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse(
          {
            title: 'Live verification unavailable',
            detail: "We couldn't reach Wikidata to refresh this player. Please try again.",
          },
          503,
        ),
      ),
    );
    const user = userEvent.setup();

    render(<PlayerRefreshSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Player id'), PLAYER_ID);
    await user.click(screen.getByRole('button', { name: 'Refresh from Wikidata' }));

    expect(
      await screen.findByText("We couldn't reach Wikidata to refresh this player. Please try again."),
    ).toBeInTheDocument();
  });

  it('REQ-514: a 401 calls onAuthError instead of showing a section-local message', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );
    const user = userEvent.setup();

    render(<PlayerRefreshSection accessToken="token" onAuthError={onAuthError} />);

    await user.type(screen.getByLabelText('Player id'), PLAYER_ID);
    await user.click(screen.getByRole('button', { name: 'Refresh from Wikidata' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('REQ-514: editing the player id after a result clears both the result and any error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => bareNotFound()));
    const user = userEvent.setup();

    render(<PlayerRefreshSection accessToken="token" onAuthError={vi.fn()} />);

    await user.type(screen.getByLabelText('Player id'), PLAYER_ID);
    await user.click(screen.getByRole('button', { name: 'Refresh from Wikidata' }));
    await screen.findByText('No player found with that id.');

    await user.type(screen.getByLabelText('Player id'), 'x');

    expect(screen.queryByText('No player found with that id.')).not.toBeInTheDocument();
  });
});
