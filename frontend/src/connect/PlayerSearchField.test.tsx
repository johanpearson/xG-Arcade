import { useState } from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PlayerSearchField } from './PlayerSearchField';
import type { PlayerAutocompleteSuggestion } from '../lib/types';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// Controlled-component harness — PlayerSearchField owns no state of its own
// (value/onValueChange are the caller's), same as GuessInput.tsx's/
// PathGuessInput.tsx's own uncontrolled field but pushed one level up here
// since two different callers (TargetPickPanel/ChainBuilder) need different
// "what does a selection mean" behavior.
function Harness({ onSelect = vi.fn() }: { onSelect?: (s: PlayerAutocompleteSuggestion) => void }) {
  const [value, setValue] = useState('');
  return (
    <PlayerSearchField
      id="test-search"
      label="Player name"
      accessToken="token"
      value={value}
      onValueChange={setValue}
      onSelect={onSelect}
    />
  );
}

// S-218 (design-document.md SCREEN-16): the shared search input behind
// TargetPickPanel/ChainBuilder — REQ-1406's own "search-pattern precedent"
// note. Mirrors GuessInput.test.tsx's/PathGuessInput.test.tsx's own
// REQ207-prefixed coverage of the identical shared endpoint.
describe('PlayerSearchField', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('REQ-207: fetches and renders suggestions once the trimmed query reaches the 2-character minimum', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse([
        { playerId: 'p1', name: 'Thierry Henry', birthYear: 1977 },
        { playerId: 'p2', name: 'Theo Hernandez' },
      ]),
    );
    vi.stubGlobal('fetch', fetchMock);

    render(<Harness />);

    await user.type(screen.getByLabelText('Player name'), 'T');
    await vi.advanceTimersByTimeAsync(500);
    expect(fetchMock).not.toHaveBeenCalled();

    await user.type(screen.getByLabelText('Player name'), 'h');
    await vi.advanceTimersByTimeAsync(500);

    await waitFor(() => expect(screen.getByRole('listbox')).toBeInTheDocument());
    const list = within(screen.getByRole('listbox'));
    expect(list.getByText('Thierry Henry')).toBeInTheDocument();
    expect(list.getByText('Theo Hernandez')).toBeInTheDocument();
  });

  it('REQ-207/S-218: selecting a suggestion fills the field and calls onSelect with the full suggestion', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    const suggestion = { playerId: 'p1', name: 'Thierry Henry', birthYear: 1977 };
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse([suggestion])));
    const onSelect = vi.fn();

    render(<Harness onSelect={onSelect} />);

    await user.type(screen.getByLabelText('Player name'), 'Th');
    await vi.advanceTimersByTimeAsync(500);
    await waitFor(() => expect(screen.getByRole('listbox')).toBeInTheDocument());

    await user.click(screen.getByText('Thierry Henry'));

    expect((screen.getByLabelText('Player name') as HTMLInputElement).value).toBe('Thierry Henry');
    expect(onSelect).toHaveBeenCalledWith(suggestion);
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('REQ-207: a failed suggestions fetch shows no suggestions, never an error', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const user = userEvent.setup();
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('network down')));

    render(<Harness />);

    await user.type(screen.getByLabelText('Player name'), 'Th');
    await vi.advanceTimersByTimeAsync(500);
    await vi.advanceTimersByTimeAsync(0);

    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
