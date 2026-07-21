import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SettingsScreen } from './SettingsScreen';

// REQ-713: isolated coverage of SettingsScreen's own admin-link gating,
// mounted directly (no App/routing involved). App.test.tsx already covers
// SettingsScreen wired into the real app (navigating there via "Settings",
// then on to AdminScreen); this file is the component's own dedicated
// suite, matching the convention every other screen in this codebase
// already has. The wrapped DeleteAccountScreen is rendered for real (not
// mocked out) since it needs no fetch call until its form is submitted —
// same as DeleteAccountScreen.test.tsx's own "shows the irreversibility
// warning" case, which stubs fetch defensively but never calls it either.
// fetchImpl lets a test provide its own fetch mock (stubbed *before*
// rendering, e.g. for the REQ-714 submission tests below) — defaults to a
// bare vi.fn() for the REQ-713 tests that never expect fetch to be called
// at all. Always (re-)stubbing here, rather than only when unset, keeps
// this a single global-fetch source of truth per render — a test that
// wants its own mock passes it in instead of calling vi.stubGlobal itself.
function renderSettingsScreen(
  overrides: Partial<Parameters<typeof SettingsScreen>[0]> = {},
  fetchImpl: ReturnType<typeof vi.fn> = vi.fn(),
) {
  vi.stubGlobal('fetch', fetchImpl);

  const onAccountDeleted = vi.fn();
  const onCancel = vi.fn();
  const onAuthError = vi.fn();
  const onOpenAdmin = vi.fn();
  const onDisplayNameUpdated = vi.fn();
  const onThemePreferenceChange = vi.fn();

  render(
    <SettingsScreen
      accessToken="token"
      isAdmin={false}
      displayName="Current Name"
      onDisplayNameUpdated={onDisplayNameUpdated}
      onAccountDeleted={onAccountDeleted}
      onCancel={onCancel}
      onAuthError={onAuthError}
      onOpenAdmin={onOpenAdmin}
      themePreference="system"
      onThemePreferenceChange={onThemePreferenceChange}
      {...overrides}
    />,
  );

  return { onAccountDeleted, onCancel, onAuthError, onOpenAdmin, onDisplayNameUpdated, onThemePreferenceChange };
}

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

describe('SettingsScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-713: isAdmin=false renders no "Admin" button/link, or any other admin-referencing text, anywhere on the screen', () => {
    renderSettingsScreen({ isAdmin: false });

    expect(screen.queryByRole('button', { name: 'Admin' })).not.toBeInTheDocument();
    expect(screen.queryByText(/admin/i)).not.toBeInTheDocument();
  });

  it('REQ-713: isAdmin=true renders an "Admin" link that calls onOpenAdmin when clicked', async () => {
    const { onOpenAdmin } = renderSettingsScreen({ isAdmin: true });
    const user = userEvent.setup();

    const adminLink = screen.getByRole('button', { name: 'Admin' });
    expect(adminLink).toBeInTheDocument();

    await user.click(adminLink);

    expect(onOpenAdmin).toHaveBeenCalledTimes(1);
  });

  it('REQ-713: the delete-account UI (DeleteAccountScreen) is present when isAdmin=false', () => {
    renderSettingsScreen({ isAdmin: false });

    expect(
      screen.getByText('This permanently deletes your account. It cannot be undone.'),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Current password')).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Delete my account permanently' }),
    ).toBeInTheDocument();
  });

  it('REQ-713: the delete-account UI (DeleteAccountScreen) is present when isAdmin=true, alongside the admin link', () => {
    renderSettingsScreen({ isAdmin: true });

    expect(screen.getByRole('button', { name: 'Admin' })).toBeInTheDocument();
    expect(
      screen.getByText('This permanently deletes your account. It cannot be undone.'),
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Current password')).toBeInTheDocument();
  });

  it('REQ-713: renders a "Settings" heading', () => {
    renderSettingsScreen();

    expect(screen.getByRole('heading', { name: 'Settings' })).toBeInTheDocument();
  });

  // REQ-714: display-name edit form.
  it('REQ-714: pre-fills the display-name field with the current name', () => {
    renderSettingsScreen({ displayName: 'Current Name' });

    expect(screen.getByLabelText('Display name')).toHaveValue('Current Name');
  });

  it('REQ-714: rejects an empty display name client-side, without calling the API', async () => {
    const fetchMock = vi.fn();
    const user = userEvent.setup();
    const { onDisplayNameUpdated } = renderSettingsScreen({ displayName: 'Current Name' }, fetchMock);

    await user.clear(screen.getByLabelText('Display name'));
    await user.click(screen.getByRole('button', { name: 'Save name' }));

    expect(
      await screen.findByText('Display name must be between 1 and 30 characters.'),
    ).toBeInTheDocument();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(onDisplayNameUpdated).not.toHaveBeenCalled();
  });

  it('REQ-714: the display-name input has maxLength=30, the same length bound the client-side/server-side checks enforce (matching AuthScreen.tsx\'s signup field)', () => {
    renderSettingsScreen({ displayName: 'Current Name' });

    expect(screen.getByLabelText('Display name')).toHaveAttribute('maxLength', '30');
  });

  // Exact upper boundary (the valid edge): SettingsScreen.tsx's own client-
  // side check is `trimmed.length > 30`, so 30 characters exactly must be
  // accepted. The value is set with fireEvent.change directly rather than
  // userEvent.type/the maxLength=30 attribute above (REQ-714: the
  // maxLength=30 test at line ~132), so this proves the component's own JS
  // validation accepts the boundary rather than merely relying on a
  // browser-enforced HTML constraint that a bypass (e.g. pasting) wouldn't
  // go through.
  it('REQ-714: entering exactly 30 characters (set directly, bypassing the maxLength attribute) is accepted and submits successfully', async () => {
    const thirtyCharacterName = 'x'.repeat(30);
    const fetchMock = vi
      .fn()
      .mockImplementation(() => jsonResponse({ id: 'user-1', displayName: thirtyCharacterName }));
    const user = userEvent.setup();
    const { onDisplayNameUpdated } = renderSettingsScreen(
      { displayName: 'Current Name' },
      fetchMock,
    );

    const input = screen.getByLabelText('Display name');
    fireEvent.change(input, { target: { value: thirtyCharacterName } });
    await user.click(screen.getByRole('button', { name: 'Save name' }));

    await waitFor(() => expect(onDisplayNameUpdated).toHaveBeenCalledWith(thirtyCharacterName));
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/auth/display-name'),
      expect.objectContaining({
        method: 'PUT',
        body: JSON.stringify({ displayName: thirtyCharacterName }),
      }),
    );
    expect(
      screen.queryByText('Display name must be between 1 and 30 characters.'),
    ).not.toBeInTheDocument();
  });

  it('REQ-714: submitting a valid new name calls PUT /auth/display-name and, on success, calls onDisplayNameUpdated without a page reload', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(() => jsonResponse({ id: 'user-1', displayName: 'New Name' }));
    const user = userEvent.setup();
    const { onDisplayNameUpdated } = renderSettingsScreen(
      {
        accessToken: 'token-abc',
        displayName: 'Current Name',
      },
      fetchMock,
    );

    const input = screen.getByLabelText('Display name');
    await user.clear(input);
    await user.type(input, 'New Name');
    await user.click(screen.getByRole('button', { name: 'Save name' }));

    await waitFor(() => expect(onDisplayNameUpdated).toHaveBeenCalledWith('New Name'));
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/auth/display-name'),
      expect.objectContaining({
        method: 'PUT',
        headers: expect.objectContaining({ Authorization: 'Bearer token-abc' }),
        body: JSON.stringify({ displayName: 'New Name' }),
      }),
    );
    expect(await screen.findByText('Display name updated.')).toBeInTheDocument();
  });

  it('REQ-714: a 409 conflict shows the server\'s inline conflict error, not a generic failure banner, and does not call onDisplayNameUpdated', async () => {
    const fetchMock = vi.fn().mockImplementation(() =>
      jsonResponse(
        { title: 'Display name already in use', detail: 'That display name is already taken. Please choose another.' },
        409,
      ),
    );
    const user = userEvent.setup();
    const { onDisplayNameUpdated } = renderSettingsScreen({ displayName: 'Current Name' }, fetchMock);

    const input = screen.getByLabelText('Display name');
    await user.clear(input);
    await user.type(input, 'Taken Name');
    await user.click(screen.getByRole('button', { name: 'Save name' }));

    expect(
      await screen.findByText('That display name is already taken. Please choose another.'),
    ).toBeInTheDocument();
    expect(onDisplayNameUpdated).not.toHaveBeenCalled();
    // The form flips back to usable — not stuck showing "Saving…".
    expect(screen.getByRole('button', { name: 'Save name' })).not.toBeDisabled();
  });

  it('REQ-714: a 401 (dead session, not a conflict) calls onAuthError, not the inline conflict error', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401));
    const user = userEvent.setup();
    const { onAuthError, onDisplayNameUpdated } = renderSettingsScreen(
      { displayName: 'Current Name' },
      fetchMock,
    );

    const input = screen.getByLabelText('Display name');
    await user.clear(input);
    await user.type(input, 'New Name');
    await user.click(screen.getByRole('button', { name: 'Save name' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
    expect(onDisplayNameUpdated).not.toHaveBeenCalled();
    expect(screen.queryByText('That display name is already taken. Please choose another.')).not.toBeInTheDocument();
  });

  it('REQ-714: resubmitting the same name (no edits) is allowed through to the API, same as any other submission', async () => {
    const fetchMock = vi
      .fn()
      .mockImplementation(() => jsonResponse({ id: 'user-1', displayName: 'Current Name' }));
    const user = userEvent.setup();
    const { onDisplayNameUpdated } = renderSettingsScreen({ displayName: 'Current Name' }, fetchMock);

    await user.click(screen.getByRole('button', { name: 'Save name' }));

    await waitFor(() => expect(onDisplayNameUpdated).toHaveBeenCalledWith('Current Name'));
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/auth/display-name'),
      expect.objectContaining({ body: JSON.stringify({ displayName: 'Current Name' }) }),
    );
  });

  // REQ-716/ADR-0034: the System/Light/Dark toggle.
  describe('theme toggle (REQ-716)', () => {
    it('REQ-716: renders all three System/Light/Dark options as a radio group', () => {
      renderSettingsScreen({ themePreference: 'system' });

      const group = screen.getByRole('radiogroup', { name: 'Color theme' });
      expect(group).toBeInTheDocument();
      expect(screen.getByRole('radio', { name: 'System' })).toBeInTheDocument();
      expect(screen.getByRole('radio', { name: 'Light' })).toBeInTheDocument();
      expect(screen.getByRole('radio', { name: 'Dark' })).toBeInTheDocument();
    });

    it('REQ-716: checks the radio matching the current themePreference prop', () => {
      renderSettingsScreen({ themePreference: 'dark' });

      expect(screen.getByRole('radio', { name: 'Dark' })).toBeChecked();
      expect(screen.getByRole('radio', { name: 'System' })).not.toBeChecked();
      expect(screen.getByRole('radio', { name: 'Light' })).not.toBeChecked();
    });

    it('REQ-716: selecting "Dark" calls onThemePreferenceChange with "dark"', async () => {
      const user = userEvent.setup();
      const { onThemePreferenceChange } = renderSettingsScreen({ themePreference: 'system' });

      await user.click(screen.getByRole('radio', { name: 'Dark' }));

      expect(onThemePreferenceChange).toHaveBeenCalledTimes(1);
      expect(onThemePreferenceChange).toHaveBeenCalledWith('dark');
    });

    it('REQ-716: selecting "Light" calls onThemePreferenceChange with "light"', async () => {
      const user = userEvent.setup();
      const { onThemePreferenceChange } = renderSettingsScreen({ themePreference: 'dark' });

      await user.click(screen.getByRole('radio', { name: 'Light' }));

      expect(onThemePreferenceChange).toHaveBeenCalledTimes(1);
      expect(onThemePreferenceChange).toHaveBeenCalledWith('light');
    });

    it('REQ-716: selecting "System" calls onThemePreferenceChange with "system"', async () => {
      const user = userEvent.setup();
      const { onThemePreferenceChange } = renderSettingsScreen({ themePreference: 'light' });

      await user.click(screen.getByRole('radio', { name: 'System' }));

      expect(onThemePreferenceChange).toHaveBeenCalledTimes(1);
      expect(onThemePreferenceChange).toHaveBeenCalledWith('system');
    });

    it('REQ-716: each radio option meets the 44px touch-target-min height', () => {
      renderSettingsScreen({ themePreference: 'system' });

      for (const name of ['System', 'Light', 'Dark']) {
        const radio = screen.getByRole('radio', { name });
        const label = radio.closest('label');
        expect(label).not.toBeNull();
        expect(label).toHaveStyle({ minHeight: 'var(--touch-target-min)' });
      }
    });
  });
});
