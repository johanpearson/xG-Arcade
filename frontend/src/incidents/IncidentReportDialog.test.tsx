import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { IncidentReportDialog } from './IncidentReportDialog';

// REQ-903/ADR-0064: the footer-accessible incident-report entry point
// (moved out of SettingsScreen.tsx, 2026-08-10, into this standalone
// modal so it's reachable from whatever screen a player is actually
// looking at) — App.test.tsx covers the footer button that opens this;
// this file covers the dialog's own self-contained behavior.
function renderDialog(
  overrides: Partial<Parameters<typeof IncidentReportDialog>[0]> = {},
  fetchImpl: ReturnType<typeof vi.fn> = vi.fn(),
) {
  vi.stubGlobal('fetch', fetchImpl);

  const onClose = vi.fn();
  const onAuthError = vi.fn();

  render(
    <IncidentReportDialog
      accessToken="token-abc"
      isGuest={false}
      route="grid"
      onClose={onClose}
      onAuthError={onAuthError}
      {...overrides}
    />,
  );

  return { onClose, onAuthError };
}

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

describe('IncidentReportDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // ---- Dialog structure/behavior (mirrors ScoringExplainer.test.tsx's
  // own coverage of the same pattern) ----------------------------------

  it('REQ-903: renders as a labeled dialog', () => {
    renderDialog();

    const dialog = screen.getByRole('dialog', { name: 'Report a problem' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
  });

  it('REQ-903: clicking the backdrop calls onClose', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const { container } = render(
      <IncidentReportDialog accessToken="token-abc" isGuest={false} route="grid" onClose={onClose} onAuthError={vi.fn()} />,
    );

    const backdrop = container.querySelector('.incident-report-dialog-backdrop');
    expect(backdrop).not.toBeNull();
    await user.click(backdrop as Element);

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('REQ-903: clicking inside the dialog itself does not call onClose', async () => {
    const user = userEvent.setup();
    const { onClose } = renderDialog();

    await user.click(screen.getByRole('dialog'));

    expect(onClose).not.toHaveBeenCalled();
  });

  it('REQ-903: clicking the [×] close button calls onClose', async () => {
    const user = userEvent.setup();
    const { onClose } = renderDialog();

    await user.click(screen.getByRole('button', { name: 'Close' }));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('REQ-903: pressing Escape calls onClose', () => {
    const { onClose } = renderDialog();

    fireEvent.keyDown(document, { key: 'Escape' });

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('REQ-903: mounting moves focus into the dialog, and unmounting restores focus to whatever was focused before it mounted', () => {
    function Harness({ open }: { open: boolean }) {
      return (
        <div>
          <button type="button">Report a problem</button>
          {open && <IncidentReportDialog accessToken="t" isGuest={false} route="grid" onClose={vi.fn()} onAuthError={vi.fn()} />}
        </div>
      );
    }
    const { rerender } = render(<Harness open={false} />);
    const openButton = screen.getByRole('button', { name: 'Report a problem' });
    openButton.focus();
    expect(openButton).toHaveFocus();

    rerender(<Harness open />);
    expect(screen.getByRole('button', { name: 'Close' })).toHaveFocus();

    const restoreFocusSpy = vi.spyOn(openButton, 'focus');
    rerender(<Harness open={false} />);
    expect(restoreFocusSpy).toHaveBeenCalled();
  });

  // ---- REQ-903: guest gating ------------------------------------------

  it('REQ-903: isGuest=false renders an enabled report form', () => {
    renderDialog({ isGuest: false });

    expect(screen.getByRole('button', { name: 'Send report' })).toBeEnabled();
    expect(screen.getByLabelText('What went wrong?')).toBeEnabled();
    expect(screen.queryByTestId('incident-report-guest-locked-copy')).not.toBeInTheDocument();
  });

  it('REQ-903: isGuest=true renders the form present but disabled, alongside the guest-locked copy', () => {
    renderDialog({ isGuest: true });

    expect(screen.getByRole('button', { name: 'Send report' })).toBeDisabled();
    expect(screen.getByLabelText('What went wrong?')).toBeDisabled();
    expect(screen.getByTestId('incident-report-guest-locked-copy')).toBeInTheDocument();
  });

  // ---- REQ-903: example guidance in the textarea placeholder ----------

  it('REQ-903: the description field shows example guidance as placeholder text', () => {
    renderDialog();

    const textarea = screen.getByLabelText('What went wrong?') as HTMLTextAreaElement;
    expect(textarea.placeholder.length).toBeGreaterThan(0);
    expect(textarea.placeholder).toMatch(/e\.g\./i);
  });

  // ---- REQ-903: validation ---------------------------------------------

  it('REQ-903: rejects an empty description client-side, without calling the API', async () => {
    const fetchMock = vi.fn();
    const user = userEvent.setup();
    renderDialog({}, fetchMock);

    await user.click(screen.getByRole('button', { name: 'Send report' }));

    expect(await screen.findByText('Please describe the problem.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  // ---- REQ-903: happy path, including the current screen as `route` ---

  it('REQ-903: submitting a valid report calls POST /incidents with the current route, and shows the created issue URL on success', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse({ issueUrl: 'https://github.com/johanpearson/xg-arcade/issues/7' }),
    );
    const user = userEvent.setup();
    renderDialog({ route: 'leaderboard' }, fetchMock);

    await user.type(screen.getByLabelText('What went wrong?'), 'The grid froze after I submitted a guess.');
    await user.click(screen.getByRole('button', { name: 'Send report' }));

    expect(await screen.findByText('Thanks — your report was filed.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'View report' })).toHaveAttribute(
      'href',
      'https://github.com/johanpearson/xg-arcade/issues/7',
    );
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/incidents'),
      expect.objectContaining({
        method: 'POST',
        headers: expect.objectContaining({ Authorization: 'Bearer token-abc' }),
        body: JSON.stringify({ description: 'The grid froze after I submitted a guess.', route: 'leaderboard' }),
      }),
    );
  });

  it('REQ-903: a 429 (rate limit) shows the server\'s inline error, not a generic failure banner', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse(
        { title: 'Too many reports', detail: "You've submitted several reports recently. Please wait a bit before submitting another." },
        429,
      ),
    );
    const user = userEvent.setup();
    renderDialog({}, fetchMock);

    await user.type(screen.getByLabelText('What went wrong?'), 'Something broke.');
    await user.click(screen.getByRole('button', { name: 'Send report' }));

    expect(
      await screen.findByText("You've submitted several reports recently. Please wait a bit before submitting another."),
    ).toBeInTheDocument();
  });

  it('REQ-903: a 401 (dead session) calls onAuthError, not the inline report error', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401));
    const user = userEvent.setup();
    const { onAuthError } = renderDialog({}, fetchMock);

    await user.type(screen.getByLabelText('What went wrong?'), 'Something broke.');
    await user.click(screen.getByRole('button', { name: 'Send report' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalled());
  });
});
