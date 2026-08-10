import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SuggestionEntry } from './SuggestionEntry';
import { SUGGESTION_GUEST_LOCKED_COPY, SUGGESTION_SUBMITTED_COPY } from '../lib/suggestionCopy';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// REQ-215 (S-089): SuggestionEntry.tsx's own unit coverage — the form
// GuessInput.tsx mounts at its two trigger sites (an incorrect scored
// guess, or a REQ-211 live-lookup timeout). Same fetch-stub convention as
// GuessInput.test.tsx (submitSuggestion calls fetch directly, via
// lib/api.ts — no mocking framework in this codebase, per docs/coding-
// guidelines.md).
describe('SuggestionEntry', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // ---- REQ-215: guest vs. non-guest visibility ----------------------------

  it('REQ215_guest_seesEntryPointPresentButDisabled_withRegistrationCopy_andClickingDoesNothing', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    render(
      <SuggestionEntry roundId="round-1" cellId="cell-1" accessToken="token" playerName="Thierry Henry" isGuest />,
    );

    const entryPoint = screen.getByTestId('suggestion-entry-point');
    expect(entryPoint).toBeInTheDocument();
    expect(entryPoint).toBeDisabled();
    expect(screen.getByTestId('suggestion-guest-copy')).toHaveTextContent(SUGGESTION_GUEST_LOCKED_COPY);

    await user.click(entryPoint);

    expect(screen.queryByTestId('suggestion-clubs-input')).not.toBeInTheDocument();
    expect(screen.queryByTestId('suggestion-nationality-input')).not.toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ215_nonGuest_seesEntryPointEnabled_andClickingExpandsTheForm', async () => {
    vi.stubGlobal('fetch', vi.fn());
    const user = userEvent.setup();
    render(
      <SuggestionEntry
        roundId="round-1"
        cellId="cell-1"
        accessToken="token"
        playerName="Thierry Henry"
        isGuest={false}
      />,
    );

    const entryPoint = screen.getByTestId('suggestion-entry-point');
    expect(entryPoint).toBeEnabled();

    await user.click(entryPoint);

    // Read-only player name (already known from the triggering guess, never
    // re-entered by the player) plus the clubs/nationality fields.
    const playerNameField = screen.getByTestId('suggestion-player-name') as HTMLInputElement;
    expect(playerNameField.value).toBe('Thierry Henry');
    expect(playerNameField).toHaveAttribute('readonly');
    expect(screen.getByTestId('suggestion-clubs-input')).toBeInTheDocument();
    expect(screen.getByTestId('suggestion-nationality-input')).toBeInTheDocument();
  });

  // ---- REQ-215: client-side validation ------------------------------------

  it('REQ215_submittingWithEmptyClubsField_showsValidationError_andDoesNotCallTheApi', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    render(
      <SuggestionEntry roundId="round-1" cellId="cell-1" accessToken="token" playerName="Thierry Henry" isGuest={false} />,
    );
    await user.click(screen.getByTestId('suggestion-entry-point'));
    await user.type(screen.getByTestId('suggestion-nationality-input'), 'France');

    await user.click(screen.getByTestId('suggestion-submit'));

    expect(screen.getByText('Enter at least one club.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ215_submittingWithOnlyBlankClubsEntries_showsValidationError_andDoesNotCallTheApi', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    render(
      <SuggestionEntry roundId="round-1" cellId="cell-1" accessToken="token" playerName="Thierry Henry" isGuest={false} />,
    );
    await user.click(screen.getByTestId('suggestion-entry-point'));
    await user.type(screen.getByTestId('suggestion-clubs-input'), ' , ,  ');
    await user.type(screen.getByTestId('suggestion-nationality-input'), 'France');

    await user.click(screen.getByTestId('suggestion-submit'));

    expect(screen.getByText('Enter at least one club.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ215_submittingWithEmptyNationalityField_showsValidationError_andDoesNotCallTheApi', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    render(
      <SuggestionEntry roundId="round-1" cellId="cell-1" accessToken="token" playerName="Thierry Henry" isGuest={false} />,
    );
    await user.click(screen.getByTestId('suggestion-entry-point'));
    await user.type(screen.getByTestId('suggestion-clubs-input'), 'Arsenal');

    await user.click(screen.getByTestId('suggestion-submit'));

    expect(screen.getByText('Enter the nationality you believe is correct.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  // ---- REQ-215: valid submission -------------------------------------------

  it('REQ215_validSubmission_splitsAndTrimsCommaSeparatedClubs_callsTheApi_andShowsConfirmationOnSuccess', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string, init: RequestInit) => {
      expect(url).toBe('/rounds/round-1/cells/cell-1/suggestions');
      const body = JSON.parse(init.body as string);
      expect(body).toEqual({
        playerName: 'Thierry Henry',
        clubs: ['Arsenal', 'Monaco'],
        nationality: 'France',
      });
      expect((init.headers as Record<string, string>).Authorization).toBe('Bearer token');
      return jsonResponse(
        {
          id: 'suggestion-1',
          playerName: 'Thierry Henry',
          assertedClubs: ['Arsenal', 'Monaco'],
          assertedNationality: 'France',
          status: 'Pending',
          createdAt: '2026-08-01T00:00:00Z',
        },
        201,
      );
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    render(
      <SuggestionEntry roundId="round-1" cellId="cell-1" accessToken="token" playerName="Thierry Henry" isGuest={false} />,
    );
    await user.click(screen.getByTestId('suggestion-entry-point'));
    await user.type(screen.getByTestId('suggestion-clubs-input'), ' Arsenal ,  Monaco ');
    await user.type(screen.getByTestId('suggestion-nationality-input'), 'France');

    await user.click(screen.getByTestId('suggestion-submit'));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByTestId('suggestion-confirmation')).toHaveTextContent(SUGGESTION_SUBMITTED_COPY));
    expect(screen.queryByTestId('suggestion-clubs-input')).not.toBeInTheDocument();
  });

  // ---- REQ-215: failed submission -------------------------------------------

  it('REQ215_failedSubmission_400_showsInlineErrorViaDescribeError_andDoesNotShowConfirmation', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse({ title: 'At least one club is required', detail: 'clubs must contain at least one non-empty value.' }, 400),
    );
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    render(
      <SuggestionEntry roundId="round-1" cellId="cell-1" accessToken="token" playerName="Thierry Henry" isGuest={false} />,
    );
    await user.click(screen.getByTestId('suggestion-entry-point'));
    await user.type(screen.getByTestId('suggestion-clubs-input'), 'Arsenal');
    await user.type(screen.getByTestId('suggestion-nationality-input'), 'France');

    await user.click(screen.getByTestId('suggestion-submit'));

    await waitFor(() => expect(screen.getByText('clubs must contain at least one non-empty value.')).toBeInTheDocument());
    expect(screen.queryByTestId('suggestion-confirmation')).not.toBeInTheDocument();
  });

  it('REQ215_failedSubmission_500_showsInlineErrorViaDescribeError', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({}, 500));
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    render(
      <SuggestionEntry roundId="round-1" cellId="cell-1" accessToken="token" playerName="Thierry Henry" isGuest={false} />,
    );
    await user.click(screen.getByTestId('suggestion-entry-point'));
    await user.type(screen.getByTestId('suggestion-clubs-input'), 'Arsenal');
    await user.type(screen.getByTestId('suggestion-nationality-input'), 'France');

    await user.click(screen.getByTestId('suggestion-submit'));

    await waitFor(() => expect(screen.getByText('Request failed')).toBeInTheDocument());
    expect(screen.queryByTestId('suggestion-confirmation')).not.toBeInTheDocument();
  });

  it('REQ215_cancelReturnsToTheIdleEntryPoint_withoutSubmitting', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();
    render(
      <SuggestionEntry roundId="round-1" cellId="cell-1" accessToken="token" playerName="Thierry Henry" isGuest={false} />,
    );
    await user.click(screen.getByTestId('suggestion-entry-point'));
    await user.type(screen.getByTestId('suggestion-clubs-input'), 'Arsenal');

    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.getByTestId('suggestion-entry-point')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
