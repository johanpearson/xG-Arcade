import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { IncidentReportDialog } from './IncidentReportDialog';

// REQ-903/ADR-0064: the footer-accessible incident-report entry point
// (moved out of SettingsScreen.tsx, 2026-08-10, into this standalone
// modal so it's reachable from whatever screen a player is actually
// looking at) — App.test.tsx covers the footer button that opens this;
// this file covers the dialog's own self-contained behavior. Same-day
// structured-fields addition: Title/Screen are now mandatory, separate
// fields, and Environment is auto-captured from window.location.origin
// rather than typed.
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
      currentScreen="grid"
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

async function fillValidForm(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Title'), 'Grid freezes on submit');
  await user.type(screen.getByLabelText('What went wrong?'), 'The grid froze after I submitted a guess.');
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
      <IncidentReportDialog accessToken="token-abc" isGuest={false} currentScreen="grid" onClose={onClose} onAuthError={vi.fn()} />,
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
          {open && <IncidentReportDialog accessToken="token-abc" isGuest={false} currentScreen="grid" onClose={vi.fn()} onAuthError={vi.fn()} />}
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

  // ---- REQ-903: guest gating (now covering all three fields) ----------

  it('REQ-903: isGuest=false renders an enabled report form', () => {
    renderDialog({ isGuest: false });

    expect(screen.getByLabelText('Title')).toBeEnabled();
    expect(screen.getByLabelText('Screen')).toBeEnabled();
    expect(screen.getByLabelText('What went wrong?')).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Send report' })).toBeEnabled();
    expect(screen.queryByTestId('incident-report-guest-locked-copy')).not.toBeInTheDocument();
  });

  it('REQ-903: isGuest=true renders every field disabled, alongside the guest-locked copy', () => {
    renderDialog({ isGuest: true });

    expect(screen.getByLabelText('Title')).toBeDisabled();
    expect(screen.getByLabelText('Screen')).toBeDisabled();
    expect(screen.getByLabelText('What went wrong?')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Send report' })).toBeDisabled();
    expect(screen.getByTestId('incident-report-guest-locked-copy')).toBeInTheDocument();
  });

  // ---- REQ-903: example guidance in the placeholders -------------------

  it('REQ-903: the title and description fields show example guidance as placeholder text', () => {
    renderDialog();

    const title = screen.getByLabelText('Title') as HTMLInputElement;
    const description = screen.getByLabelText('What went wrong?') as HTMLTextAreaElement;
    expect(title.placeholder.length).toBeGreaterThan(0);
    expect(description.placeholder).toMatch(/steps to reproduce/i);
  });

  // ---- REQ-903: the Screen dropdown defaults to the current screen -----

  it('REQ-903: the Screen dropdown defaults to the screen the dialog was opened from', () => {
    renderDialog({ currentScreen: 'leaderboard' });

    expect((screen.getByLabelText('Screen') as HTMLSelectElement).value).toBe('leaderboard');
  });

  it('REQ-903: the Screen dropdown falls back to "Something else / not sure" for an unrecognized current screen', () => {
    renderDialog({ currentScreen: 'not-a-real-screen' });

    expect((screen.getByLabelText('Screen') as HTMLSelectElement).value).toBe('other');
  });

  it('REQ-903: the player can change the Screen dropdown away from its default', async () => {
    const user = userEvent.setup();
    renderDialog({ currentScreen: 'grid' });

    await user.selectOptions(screen.getByLabelText('Screen'), 'settings');

    expect((screen.getByLabelText('Screen') as HTMLSelectElement).value).toBe('settings');
  });

  // ---- REQ-903: environment is shown, never editable -------------------

  it('REQ-903: shows the current origin as a read-only environment value, not an editable field', () => {
    renderDialog();

    const environment = screen.getByTestId('incident-report-environment');
    expect(environment).toHaveTextContent(window.location.origin);
    expect(screen.queryByLabelText('Environment')).not.toBeInTheDocument();
  });

  // ---- REQ-903: validation ---------------------------------------------

  it('REQ-903: rejects an empty title client-side, without calling the API', async () => {
    const fetchMock = vi.fn();
    const user = userEvent.setup();
    renderDialog({}, fetchMock);

    await user.type(screen.getByLabelText('What went wrong?'), 'Something broke.');
    await user.click(screen.getByRole('button', { name: 'Send report' }));

    expect(await screen.findByText('Please add a short title.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('REQ-903: rejects an empty description client-side, without calling the API', async () => {
    const fetchMock = vi.fn();
    const user = userEvent.setup();
    renderDialog({}, fetchMock);

    await user.type(screen.getByLabelText('Title'), 'Grid freezes on submit');
    await user.click(screen.getByRole('button', { name: 'Send report' }));

    expect(await screen.findByText('Please describe the problem.')).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  // ---- REQ-903: happy path, including title/screen/environment ---------

  it('REQ-903: submitting a valid report calls POST /incidents with title, description, the selected screen, and the current origin as environment', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse({ issueUrl: 'https://github.com/johanpearson/xg-arcade/issues/7' }),
    );
    const user = userEvent.setup();
    renderDialog({ currentScreen: 'leaderboard' }, fetchMock);

    await fillValidForm(user);
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
        body: JSON.stringify({
          title: 'Grid freezes on submit',
          description: 'The grid froze after I submitted a guess.',
          screen: 'leaderboard',
          environment: window.location.origin,
        }),
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

    await fillValidForm(user);
    await user.click(screen.getByRole('button', { name: 'Send report' }));

    expect(
      await screen.findByText("You've submitted several reports recently. Please wait a bit before submitting another."),
    ).toBeInTheDocument();
  });

  it('REQ-903: a 401 (dead session) calls onAuthError, not the inline report error', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401));
    const user = userEvent.setup();
    const { onAuthError } = renderDialog({}, fetchMock);

    await fillValidForm(user);
    await user.click(screen.getByRole('button', { name: 'Send report' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalled());
  });
});
