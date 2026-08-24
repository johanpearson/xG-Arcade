import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SettingsScreen } from './SettingsScreen';
import { GUEST_EXPIRY_COPY } from '../lib/guestExpiryCopy';

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
  const onOpenStats = vi.fn();
  const onDisplayNameUpdated = vi.fn();
  const onAccountClaimed = vi.fn();
  const onThemePreferenceChange = vi.fn();

  render(
    <SettingsScreen
      accessToken="token"
      isAdmin={false}
      isGuest={false}
      displayName="Current Name"
      onDisplayNameUpdated={onDisplayNameUpdated}
      onAccountClaimed={onAccountClaimed}
      onAccountDeleted={onAccountDeleted}
      onCancel={onCancel}
      onAuthError={onAuthError}
      onOpenAdmin={onOpenAdmin}
      onOpenStats={onOpenStats}
      themePreference="system"
      onThemePreferenceChange={onThemePreferenceChange}
      {...overrides}
    />,
  );

  return {
    onAccountDeleted,
    onCancel,
    onAuthError,
    onOpenAdmin,
    onOpenStats,
    onDisplayNameUpdated,
    onAccountClaimed,
    onThemePreferenceChange,
  };
}

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// REQ-722/S-182: SettingsScreen now also fetches GET /users/me/avatar on
// mount (the new avatar section below), so a bare `expect(fetchMock).
// not.toHaveBeenCalled()` no longer holds for tests asserting a specific
// OTHER submission never reached the network — those now check no call was
// made to that specific path instead, via this helper, rather than "fetch
// was never called at all."
function calledWithPathContaining(fetchMock: ReturnType<typeof vi.fn>, substring: string): boolean {
  return fetchMock.mock.calls.some(([url]) => typeof url === 'string' && url.includes(substring));
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

  // REQ-411 (S-179): the own-stats entry point — unconditional (not
  // isAdmin/isGuest-gated), unlike the "Admin" link above.
  it('REQ-411: renders a "My stats" link that calls onOpenStats when clicked, for both a guest and a claimed, non-admin account', async () => {
    const user = userEvent.setup();
    const { onOpenStats } = renderSettingsScreen({ isAdmin: false, isGuest: true });

    const statsLink = screen.getByRole('button', { name: 'My stats' });
    expect(statsLink).toBeInTheDocument();

    await user.click(statsLink);

    expect(onOpenStats).toHaveBeenCalledTimes(1);
  });

  it('REQ-411: the "My stats" link is present regardless of isAdmin', () => {
    renderSettingsScreen({ isAdmin: true });

    expect(screen.getByRole('button', { name: 'My stats' })).toBeInTheDocument();
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
    expect(calledWithPathContaining(fetchMock, '/auth/display-name')).toBe(false);
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
    // REQ-722/S-182: URL-aware now that the avatar section's own mount-time
    // GET /users/me/avatar also goes through this same fetchMock — a single
    // response for every URL would otherwise leak this test's 409 body into
    // the avatar section's own error text too, duplicating the message this
    // test asserts on (Found multiple elements).
    const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
      if (String(input).includes('/users/me/avatar')) {
        return jsonResponse({ pending: null, rejected: null, approved: null });
      }
      return jsonResponse(
        { title: 'Display name already in use', detail: 'That display name is already taken. Please choose another.' },
        409,
      );
    });
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

    // REQ-722/S-182: this test's fetchMock returns a 401 for every call
    // regardless of path, so the avatar section's own mount-time GET
    // /users/me/avatar also independently observes the dead session and
    // calls onAuthError — asserting it was called at least once (not
    // exactly once) is the correct bar here, since onAuthError is meant to
    // be idempotent (App.tsx's dead-session recovery), and two independent
    // sections detecting the same dead session is expected, not a bug.
    await waitFor(() => expect(onAuthError).toHaveBeenCalled());
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

  // REQ-717/ADR-0036: the guest claim/upgrade section.
  describe('claim/upgrade (REQ-717)', () => {
    it('REQ-717: isGuest=false renders no claim section at all', () => {
      renderSettingsScreen({ isGuest: false });

      expect(screen.queryByText('Save your progress')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Save my progress' })).not.toBeInTheDocument();
    });

    it('REQ-717: isGuest=true renders the claim form', () => {
      renderSettingsScreen({ isGuest: true });

      expect(screen.getByText('Save your progress')).toBeInTheDocument();
      expect(screen.getByLabelText('Email')).toBeInTheDocument();
      expect(screen.getByLabelText('Password')).toBeInTheDocument();
      expect(screen.getByLabelText('Confirm password')).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'Save my progress' })).toBeInTheDocument();
    });

    it('REQ-701/717: blocks the claim client-side when the password is under 8 characters, without calling the API', async () => {
      const fetchMock = vi.fn();
      const user = userEvent.setup();
      renderSettingsScreen({ isGuest: true }, fetchMock);

      await user.type(screen.getByLabelText('Email'), 'player@example.com');
      await user.type(screen.getByLabelText('Password'), 'short12');
      await user.type(screen.getByLabelText('Confirm password'), 'short12');
      await user.click(screen.getByRole('button', { name: 'Save my progress' }));

      expect(await screen.findByText('Password must be at least 8 characters.')).toBeInTheDocument();
      expect(calledWithPathContaining(fetchMock, '/auth/claim')).toBe(false);
    });

    it('REQ-701/717: blocks the claim client-side when confirm password does not match, without calling the API', async () => {
      const fetchMock = vi.fn();
      const user = userEvent.setup();
      renderSettingsScreen({ isGuest: true }, fetchMock);

      await user.type(screen.getByLabelText('Email'), 'player@example.com');
      await user.type(screen.getByLabelText('Password'), 'password123');
      await user.type(screen.getByLabelText('Confirm password'), 'password456');
      await user.click(screen.getByRole('button', { name: 'Save my progress' }));

      expect(await screen.findByText('Passwords do not match.')).toBeInTheDocument();
      expect(calledWithPathContaining(fetchMock, '/auth/claim')).toBe(false);
    });

    it('REQ-717: submitting a valid claim calls POST /auth/claim and, on success, calls onAccountClaimed with the server response', async () => {
      const fetchMock = vi.fn().mockImplementation(() =>
        jsonResponse({
          id: 'user-1',
          email: 'player@example.com',
          displayName: 'Guest8317',
          emailConfirmed: true,
          isAdmin: false,
        }),
      );
      const user = userEvent.setup();
      const { onAccountClaimed } = renderSettingsScreen(
        { isGuest: true, accessToken: 'token-abc' },
        fetchMock,
      );

      await user.type(screen.getByLabelText('Email'), 'player@example.com');
      await user.type(screen.getByLabelText('Password'), 'password123');
      await user.type(screen.getByLabelText('Confirm password'), 'password123');
      await user.click(screen.getByRole('button', { name: 'Save my progress' }));

      await waitFor(() =>
        expect(onAccountClaimed).toHaveBeenCalledWith({
          id: 'user-1',
          email: 'player@example.com',
          displayName: 'Guest8317',
          emailConfirmed: true,
          isAdmin: false,
        }),
      );
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/auth/claim'),
        expect.objectContaining({
          method: 'POST',
          headers: expect.objectContaining({ Authorization: 'Bearer token-abc' }),
          body: JSON.stringify({
            email: 'player@example.com',
            password: 'password123',
            confirmPassword: 'password123',
          }),
        }),
      );
    });

    it('REQ-717: a 400 (not currently a guest, or email already in use) shows the server\'s inline error, not a generic failure banner', async () => {
      // REQ-722/S-182: URL-aware now that the avatar section's own
      // mount-time GET /users/me/avatar also goes through this same
      // fetchMock — a single response for every URL would otherwise leak
      // this test's 400 body into the avatar section's own error text too,
      // duplicating the message this test asserts on (Found multiple
      // elements).
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
        if (String(input).includes('/users/me/avatar')) {
          return jsonResponse({ pending: null, rejected: null, approved: null });
        }
        return jsonResponse(
          {
            title: 'Claim could not be completed',
            detail: 'Could not add an email and password to this account. The email may already be in use.',
          },
          400,
        );
      });
      const user = userEvent.setup();
      const { onAccountClaimed } = renderSettingsScreen({ isGuest: true }, fetchMock);

      await user.type(screen.getByLabelText('Email'), 'taken@example.com');
      await user.type(screen.getByLabelText('Password'), 'password123');
      await user.type(screen.getByLabelText('Confirm password'), 'password123');
      await user.click(screen.getByRole('button', { name: 'Save my progress' }));

      expect(
        await screen.findByText(
          'Could not add an email and password to this account. The email may already be in use.',
        ),
      ).toBeInTheDocument();
      expect(onAccountClaimed).not.toHaveBeenCalled();
    });

    it('REQ-717: a 401 (dead session) calls onAuthError, not the inline claim error', async () => {
      const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401));
      const user = userEvent.setup();
      const { onAuthError, onAccountClaimed } = renderSettingsScreen({ isGuest: true }, fetchMock);

      await user.type(screen.getByLabelText('Email'), 'player@example.com');
      await user.type(screen.getByLabelText('Password'), 'password123');
      await user.type(screen.getByLabelText('Confirm password'), 'password123');
      await user.click(screen.getByRole('button', { name: 'Save my progress' }));

      // REQ-722/S-182: same reasoning as the REQ-714 401 test above — this
      // fetchMock 401s on every path, so the avatar section's own
      // mount-time GET /users/me/avatar independently observes the dead
      // session too; asserting "called" rather than "called exactly once"
      // is correct now that a second section can also trigger it.
      await waitFor(() => expect(onAuthError).toHaveBeenCalled());
      expect(onAccountClaimed).not.toHaveBeenCalled();
    });
  });

  // REQ-718 UI addendum (rule 5, 2026-08-01): the guest-expiry copy
  // (GUEST_EXPIRY_COPY) rendered alongside the claim section — same isGuest
  // gate as the "Save your progress" section itself (REQ-717's own test
  // above already covers the section as a whole appearing/disappearing;
  // these two are scoped specifically to the expiry-policy sentence).
  describe('guest-expiry copy (REQ-718 rule 5)', () => {
    it('REQ-718: isGuest=true renders the guest-expiry copy stating the actual 7-day/30-day policy', () => {
      renderSettingsScreen({ isGuest: true });

      const expiryCopy = screen.getByTestId('guest-expiry-copy-settings');
      expect(expiryCopy).toBeInTheDocument();
      expect(expiryCopy).toHaveTextContent(GUEST_EXPIRY_COPY);
    });

    it('REQ-718: isGuest=false renders no guest-expiry copy at all', () => {
      renderSettingsScreen({ isGuest: false });

      expect(screen.queryByTestId('guest-expiry-copy-settings')).not.toBeInTheDocument();
      expect(screen.queryByText(GUEST_EXPIRY_COPY)).not.toBeInTheDocument();
    });
  });

  // REQ-722/S-182: light sanity coverage of the new "My avatar" section —
  // the full REQ-722 acceptance-criteria suite is a separate, dedicated
  // task (test-writer); these just confirm this implementation's own
  // fetch-on-mount/upload/preview wiring behaves as built. jsdom has no
  // native URL.createObjectURL/revokeObjectURL (unlike a real browser), so
  // both are stubbed here — fetchAvatarImageObjectUrl (lib/avatar.ts) calls
  // them directly.
  describe('avatar section (REQ-722)', () => {
    afterEach(() => {
      vi.unstubAllGlobals();
    });

    function stubAvatarObjectUrls() {
      vi.stubGlobal('URL', {
        ...URL,
        createObjectURL: vi.fn(() => 'blob:mock-preview-url'),
        revokeObjectURL: vi.fn(),
      });
    }

    function avatarStatusFetch(input: RequestInfo | URL) {
      if (String(input).includes('/users/me/avatar/')) {
        return Promise.resolve({
          ok: true,
          status: 200,
          blob: () => Promise.resolve(new Blob(['fake-image'], { type: 'image/png' })),
        } as unknown as Response);
      }
      return undefined;
    }

    it('REQ-722: fetches GET /users/me/avatar on mount and shows "no avatar yet" when all three slots are null', async () => {
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
        if (String(input).includes('/users/me/avatar')) {
          return jsonResponse({ pending: null, rejected: null, approved: null });
        }
        return jsonResponse({});
      });
      renderSettingsScreen({}, fetchMock);

      expect(await screen.findByTestId('avatar-section-none')).toHaveTextContent(
        "You haven't uploaded an avatar yet.",
      );
      expect(screen.queryByTestId('avatar-section-pending')).not.toBeInTheDocument();
      expect(screen.queryByTestId('avatar-section-rejected')).not.toBeInTheDocument();
      expect(screen.queryByTestId('avatar-section-approved')).not.toBeInTheDocument();
    });

    it('REQ-722: renders pending, rejected, AND approved simultaneously, with a preview image for each, when all three are present', async () => {
      stubAvatarObjectUrls();
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/users/me/avatar/')) return avatarStatusFetch(input);
        if (url.includes('/users/me/avatar')) {
          return jsonResponse({
            pending: { id: 'p1', createdAt: '2026-08-24T00:00:00Z', imageUrl: '/users/me/avatar/p1/image' },
            rejected: { id: 'r1', createdAt: '2026-08-20T00:00:00Z', imageUrl: '/users/me/avatar/r1/image' },
            approved: { id: 'a1', createdAt: '2026-08-01T00:00:00Z', imageUrl: '/users/me/avatar/a1/image' },
          });
        }
        return jsonResponse({});
      });
      renderSettingsScreen({}, fetchMock);

      const pending = await screen.findByTestId('avatar-section-pending');
      expect(pending).toHaveTextContent('Pending review');
      const rejected = await screen.findByTestId('avatar-section-rejected');
      expect(rejected).toHaveTextContent('Rejected');
      const approved = await screen.findByTestId('avatar-section-approved');
      expect(approved).toHaveTextContent('Currently visible to other players');

      expect(await screen.findByTestId('avatar-section-pending-image')).toHaveAttribute(
        'src',
        'blob:mock-preview-url',
      );
      expect(await screen.findByTestId('avatar-section-rejected-image')).toHaveAttribute(
        'src',
        'blob:mock-preview-url',
      );
      expect(await screen.findByTestId('avatar-section-approved-image')).toHaveAttribute(
        'src',
        'blob:mock-preview-url',
      );
      expect(screen.queryByTestId('avatar-section-none')).not.toBeInTheDocument();
    });

    it('REQ-722: rejects an oversized file client-side, without calling POST /users/me/avatar, showing a specific message', async () => {
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) =>
        String(input).includes('/users/me/avatar') ? jsonResponse({ pending: null, rejected: null, approved: null }) : jsonResponse({}),
      );
      const user = userEvent.setup();
      renderSettingsScreen({}, fetchMock);
      await screen.findByTestId('avatar-section-none');

      // 6 MB, over the 5 MB client-side pre-check bound — same MIME type
      // the accept attribute already allows, so userEvent.upload (which
      // itself respects the input's `accept` attribute, same as a real
      // browser) doesn't filter this one out the way a mismatched MIME
      // type would.
      const oversizedFile = new File([new Uint8Array(6 * 1024 * 1024)], 'avatar.png', { type: 'image/png' });
      const input = screen.getByTestId('avatar-section-upload-input') as HTMLInputElement;
      await user.upload(input, oversizedFile);
      await user.click(screen.getByTestId('avatar-section-upload-button'));

      expect(await screen.findByText('That image is too large. Choose one under 5 MB.')).toBeInTheDocument();
      expect(
        fetchMock.mock.calls.some(
          ([callUrl, callInit]) =>
            String(callUrl).includes('/users/me/avatar') && (callInit as RequestInit | undefined)?.method === 'POST',
        ),
      ).toBe(false);
    });

    it('REQ-722: a successful upload posts multipart form data, shows a confirmation, and refetches status', async () => {
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url.includes('/users/me/avatar') && init?.method === 'POST') {
          return jsonResponse({ id: 'new-1', status: 'Pending', createdAt: '2026-08-24T12:00:00Z' }, 201);
        }
        if (url.includes('/users/me/avatar')) {
          return jsonResponse({ pending: null, rejected: null, approved: null });
        }
        return jsonResponse({});
      });
      const user = userEvent.setup();
      renderSettingsScreen({ accessToken: 'token-abc' }, fetchMock);
      await screen.findByTestId('avatar-section-none');

      const file = new File(['fake'], 'avatar.png', { type: 'image/png' });
      const input = screen.getByTestId('avatar-section-upload-input') as HTMLInputElement;
      await user.upload(input, file);
      await user.click(screen.getByTestId('avatar-section-upload-button'));

      expect(await screen.findByText('Avatar submitted for review.')).toBeInTheDocument();

      const postCall = fetchMock.mock.calls.find(
        ([callUrl, callInit]) => String(callUrl).includes('/users/me/avatar') && (callInit as RequestInit | undefined)?.method === 'POST',
      );
      expect(postCall).toBeDefined();
      const [, postInit] = postCall!;
      expect((postInit as RequestInit).headers).toMatchObject({ Authorization: 'Bearer token-abc' });
      expect((postInit as RequestInit).body).toBeInstanceOf(FormData);

      // Refetches GET /users/me/avatar after the upload (at least twice:
      // once on mount, once after the successful upload).
      const getCalls = fetchMock.mock.calls.filter(
        ([callUrl, callInit]) =>
          String(callUrl).includes('/users/me/avatar') && (callInit as RequestInit | undefined)?.method !== 'POST',
      );
      expect(getCalls.length).toBeGreaterThanOrEqual(2);
    });

    it('REQ-722: a 401 on GET /users/me/avatar calls onAuthError', async () => {
      const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized' }, 401));
      const { onAuthError } = renderSettingsScreen({}, fetchMock);

      await waitFor(() => expect(onAuthError).toHaveBeenCalled());
    });

    // REQ-722's "Test level" section requires each of the four states
    // (none/pending/approved/rejected) to render distinctly. The tests
    // above already cover "none" and "all three at once"; these isolate
    // each of the other three states on its own, so a bug that makes one
    // section's presence leak into/depend on another can't hide behind the
    // "all three at once" test alone.
    it('REQ-722: renders only the pending state when only a Pending submission exists', async () => {
      stubAvatarObjectUrls();
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/users/me/avatar/')) return avatarStatusFetch(input);
        if (url.includes('/users/me/avatar')) {
          return jsonResponse({
            pending: { id: 'p1', createdAt: '2026-08-24T00:00:00Z', imageUrl: '/users/me/avatar/p1/image' },
            rejected: null,
            approved: null,
          });
        }
        return jsonResponse({});
      });
      renderSettingsScreen({}, fetchMock);

      expect(await screen.findByTestId('avatar-section-pending')).toHaveTextContent('Pending review');
      expect(await screen.findByTestId('avatar-section-pending-image')).toHaveAttribute(
        'src',
        'blob:mock-preview-url',
      );
      expect(screen.queryByTestId('avatar-section-rejected')).not.toBeInTheDocument();
      expect(screen.queryByTestId('avatar-section-approved')).not.toBeInTheDocument();
      expect(screen.queryByTestId('avatar-section-none')).not.toBeInTheDocument();
    });

    it('REQ-722: renders only the rejected state when only a Rejected submission exists', async () => {
      stubAvatarObjectUrls();
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/users/me/avatar/')) return avatarStatusFetch(input);
        if (url.includes('/users/me/avatar')) {
          return jsonResponse({
            pending: null,
            rejected: { id: 'r1', createdAt: '2026-08-20T00:00:00Z', imageUrl: '/users/me/avatar/r1/image' },
            approved: null,
          });
        }
        return jsonResponse({});
      });
      renderSettingsScreen({}, fetchMock);

      expect(await screen.findByTestId('avatar-section-rejected')).toHaveTextContent('Rejected');
      expect(await screen.findByTestId('avatar-section-rejected-image')).toHaveAttribute(
        'src',
        'blob:mock-preview-url',
      );
      expect(screen.queryByTestId('avatar-section-pending')).not.toBeInTheDocument();
      expect(screen.queryByTestId('avatar-section-approved')).not.toBeInTheDocument();
      expect(screen.queryByTestId('avatar-section-none')).not.toBeInTheDocument();
    });

    it('REQ-722: renders only the approved state when only an Approved submission exists', async () => {
      stubAvatarObjectUrls();
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/users/me/avatar/')) return avatarStatusFetch(input);
        if (url.includes('/users/me/avatar')) {
          return jsonResponse({
            pending: null,
            rejected: null,
            approved: { id: 'a1', createdAt: '2026-08-01T00:00:00Z', imageUrl: '/users/me/avatar/a1/image' },
          });
        }
        return jsonResponse({});
      });
      renderSettingsScreen({}, fetchMock);

      expect(await screen.findByTestId('avatar-section-approved')).toHaveTextContent(
        'Currently visible to other players',
      );
      expect(await screen.findByTestId('avatar-section-approved-image')).toHaveAttribute(
        'src',
        'blob:mock-preview-url',
      );
      expect(screen.queryByTestId('avatar-section-pending')).not.toBeInTheDocument();
      expect(screen.queryByTestId('avatar-section-rejected')).not.toBeInTheDocument();
      expect(screen.queryByTestId('avatar-section-none')).not.toBeInTheDocument();
    });

    // REQ-722's literal "a Rejected status does not remove or affect a
    // separately-existing Approved avatar" acceptance criterion, isolated
    // from the "all three at once" test above — no Pending row exists here
    // at all, so this proves the Approved preview survives specifically
    // alongside a Rejected one, not merely alongside a Pending one too.
    it('REQ-722: renders Rejected and Approved simultaneously with no Pending — a Rejected status does not remove/affect a separately-existing Approved avatar', async () => {
      stubAvatarObjectUrls();
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) => {
        const url = String(input);
        if (url.includes('/users/me/avatar/')) return avatarStatusFetch(input);
        if (url.includes('/users/me/avatar')) {
          return jsonResponse({
            pending: null,
            rejected: { id: 'r1', createdAt: '2026-08-20T00:00:00Z', imageUrl: '/users/me/avatar/r1/image' },
            approved: { id: 'a1', createdAt: '2026-08-01T00:00:00Z', imageUrl: '/users/me/avatar/a1/image' },
          });
        }
        return jsonResponse({});
      });
      renderSettingsScreen({}, fetchMock);

      expect(await screen.findByTestId('avatar-section-rejected')).toHaveTextContent('Rejected');
      expect(await screen.findByTestId('avatar-section-approved')).toHaveTextContent(
        'Currently visible to other players',
      );
      expect(await screen.findByTestId('avatar-section-approved-image')).toHaveAttribute(
        'src',
        'blob:mock-preview-url',
      );
      expect(screen.queryByTestId('avatar-section-pending')).not.toBeInTheDocument();
      expect(screen.queryByTestId('avatar-section-none')).not.toBeInTheDocument();
    });

    // Mirrors the oversized-file test's structure above, but for an
    // unsupported MIME type (image/gif — deliberately excluded per
    // AVATAR_ALLOWED_TYPES/ADR-0087's SVG-exclusion reasoning, applied here
    // to GIF too). `applyAccept: false` is required on this userEvent
    // instance: the real input's `accept="image/jpeg,image/png,image/webp"`
    // attribute makes user-event's own upload() silently drop a
    // non-matching file (same browser-level filtering a real file picker
    // does), which would leave avatarFile null and produce "Choose an
    // image to upload." instead of exercising this component's own
    // AVATAR_ALLOWED_TYPES check — same "bypass the browser-level
    // constraint to test the JS check directly" reasoning as the
    // exactly-30-characters display-name test above (which uses
    // fireEvent.change instead, since that constraint is a maxLength
    // attribute rather than an upload-time filter).
    it('REQ-722: rejects an unsupported file type (image/gif) client-side, without calling POST /users/me/avatar, showing a specific message', async () => {
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL) =>
        String(input).includes('/users/me/avatar') ? jsonResponse({ pending: null, rejected: null, approved: null }) : jsonResponse({}),
      );
      const user = userEvent.setup({ applyAccept: false });
      renderSettingsScreen({}, fetchMock);
      await screen.findByTestId('avatar-section-none');

      const gifFile = new File(['fake-gif-bytes'], 'avatar.gif', { type: 'image/gif' });
      const input = screen.getByTestId('avatar-section-upload-input') as HTMLInputElement;
      await user.upload(input, gifFile);
      await user.click(screen.getByTestId('avatar-section-upload-button'));

      expect(await screen.findByText('Choose a JPEG, PNG, or WEBP image.')).toBeInTheDocument();
      expect(
        fetchMock.mock.calls.some(
          ([callUrl, callInit]) =>
            String(callUrl).includes('/users/me/avatar') && (callInit as RequestInit | undefined)?.method === 'POST',
        ),
      ).toBe(false);
    });

    // UI-level half of S-182's "uploading while pending replaces rather
    // than queues a second submission" acceptance criterion — the
    // server-side replace itself is already covered by
    // AvatarEndpointTests.cs's
    // REQ722_Avatar_Post_SecondUploadWhilePending_ReplacesRatherThanDuplicates;
    // this only confirms the UI reflects that single resulting row. Uses
    // a distinguishable per-id Blob marker (rather than the shared
    // stubAvatarObjectUrls's constant 'blob:mock-preview-url') so the
    // preview's src can actually prove which submission it came from.
    it('REQ-722: uploading while a Pending submission is showing replaces it in place — never a second avatar-section-pending element, and the preview reflects the new submission', async () => {
      vi.stubGlobal('URL', {
        ...URL,
        createObjectURL: vi.fn((blob: { marker?: string }) => `blob:${blob.marker ?? 'unknown'}`),
        revokeObjectURL: vi.fn(),
      });

      let avatarStatusCallCount = 0;
      const fetchMock = vi.fn().mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
        const url = String(input);
        if (url.includes('/users/me/avatar/p1/image')) {
          return Promise.resolve({
            ok: true,
            status: 200,
            blob: () => Promise.resolve({ marker: 'p1' }),
          } as unknown as Response);
        }
        if (url.includes('/users/me/avatar/p2/image')) {
          return Promise.resolve({
            ok: true,
            status: 200,
            blob: () => Promise.resolve({ marker: 'p2' }),
          } as unknown as Response);
        }
        if (url.includes('/users/me/avatar') && init?.method === 'POST') {
          return jsonResponse({ id: 'p2', status: 'Pending', createdAt: '2026-08-24T12:00:00Z' }, 201);
        }
        if (url.includes('/users/me/avatar')) {
          avatarStatusCallCount += 1;
          if (avatarStatusCallCount === 1) {
            return jsonResponse({
              pending: { id: 'p1', createdAt: '2026-08-01T00:00:00Z', imageUrl: '/users/me/avatar/p1/image' },
              rejected: null,
              approved: null,
            });
          }
          // The refetch after the upload — the server's own single
          // resulting Pending row, now p2, not p1.
          return jsonResponse({
            pending: { id: 'p2', createdAt: '2026-08-24T12:00:00Z', imageUrl: '/users/me/avatar/p2/image' },
            rejected: null,
            approved: null,
          });
        }
        return jsonResponse({});
      });
      const user = userEvent.setup();
      renderSettingsScreen({ accessToken: 'token-abc' }, fetchMock);

      expect(await screen.findByTestId('avatar-section-pending')).toBeInTheDocument();
      await waitFor(() =>
        expect(screen.getByTestId('avatar-section-pending-image')).toHaveAttribute('src', 'blob:p1'),
      );
      expect(screen.getAllByTestId('avatar-section-pending')).toHaveLength(1);

      const file = new File(['fake'], 'avatar.png', { type: 'image/png' });
      const input = screen.getByTestId('avatar-section-upload-input') as HTMLInputElement;
      await user.upload(input, file);
      await user.click(screen.getByTestId('avatar-section-upload-button'));

      await waitFor(() =>
        expect(screen.getByTestId('avatar-section-pending-image')).toHaveAttribute('src', 'blob:p2'),
      );
      expect(screen.getAllByTestId('avatar-section-pending')).toHaveLength(1);
    });
  });
});

// REQ-903/ADR-0064: the incident-report entry point moved out of Settings
// (2026-08-10, same day as its original build) into a footer-accessible
// modal (IncidentReportDialog.tsx, App.tsx) reachable from any
// authenticated screen — see that component's own test file for its
// coverage. SettingsScreen no longer renders or knows about it at all.
