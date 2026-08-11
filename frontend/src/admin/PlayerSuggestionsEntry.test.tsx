import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PlayerSuggestionsEntry } from './PlayerSuggestionsEntry';

// S-108 (docs/backlog.md): dedicated isolation coverage for
// PlayerSuggestionsEntry, extracted from AdminScreen.tsx by S-103 with no
// behavior change. Mirrors AdminScreen.test.tsx's REQ-512 assertions, but
// renders the component directly rather than through the full AdminScreen
// tree — only /admin/suggestions needs stubbing here, none of AdminScreen's
// other sibling-section routes.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function pendingSuggestion(id: string) {
  return {
    id,
    playerName: 'Someone Player',
    assertedClubs: ['Some Club'],
    assertedNationality: 'Some Country',
    submittingUserId: 'user-1',
    submittingUserDisplayName: 'Player One',
    rowCategoryType: 'Nationality',
    colCategoryType: 'Club',
    createdAt: '2026-08-01T00:00:00Z',
  };
}

describe('PlayerSuggestionsEntry', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-512: shows "Player suggestions (3)" when 3 suggestions are pending', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() =>
        jsonResponse([pendingSuggestion('s-1'), pendingSuggestion('s-2'), pendingSuggestion('s-3')]),
      ),
    );

    render(
      <PlayerSuggestionsEntry accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />,
    );

    expect(await screen.findByRole('button', { name: 'Player suggestions (3)' })).toBeInTheDocument();
  });

  it('REQ-512: shows plain "Player suggestions" with no "(0)" when zero suggestions are pending', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse([])));

    render(
      <PlayerSuggestionsEntry accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={vi.fn()} />,
    );

    await waitFor(() =>
      expect(screen.getByRole('button', { name: 'Player suggestions' })).toBeInTheDocument(),
    );
  });

  it('REQ-512: a 401 from the suggestions fetch calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );

    render(
      <PlayerSuggestionsEntry accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />,
    );

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-512: a 403 leaves the button showing plain "Player suggestions", with no error banner', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403)),
    );

    render(
      <PlayerSuggestionsEntry accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />,
    );

    expect(await screen.findByRole('button', { name: 'Player suggestions' })).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-512: a non-401/403 error shows an inline error message, with no badge and no onAuthError call', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Server error', detail: 'Something broke.' }, 500)),
    );

    render(
      <PlayerSuggestionsEntry accessToken="token" onAuthError={onAuthError} onOpenSuggestions={vi.fn()} />,
    );

    expect(await screen.findByText('Something broke.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Player suggestions' })).toBeInTheDocument();
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-512: clicking "Player suggestions" calls onOpenSuggestions regardless of badge state', async () => {
    const onOpenSuggestions = vi.fn();
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse([pendingSuggestion('s-1')])));
    const user = userEvent.setup();

    render(
      <PlayerSuggestionsEntry accessToken="token" onAuthError={vi.fn()} onOpenSuggestions={onOpenSuggestions} />,
    );

    const button = await screen.findByRole('button', { name: 'Player suggestions (1)' });
    await user.click(button);

    expect(onOpenSuggestions).toHaveBeenCalledTimes(1);
  });
});
