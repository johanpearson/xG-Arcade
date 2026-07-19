import { render, screen } from '@testing-library/react';
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
function renderSettingsScreen(overrides: Partial<Parameters<typeof SettingsScreen>[0]> = {}) {
  vi.stubGlobal('fetch', vi.fn());

  const onAccountDeleted = vi.fn();
  const onCancel = vi.fn();
  const onAuthError = vi.fn();
  const onOpenAdmin = vi.fn();

  render(
    <SettingsScreen
      accessToken="token"
      isAdmin={false}
      onAccountDeleted={onAccountDeleted}
      onCancel={onCancel}
      onAuthError={onAuthError}
      onOpenAdmin={onOpenAdmin}
      {...overrides}
    />,
  );

  return { onAccountDeleted, onCancel, onAuthError, onOpenAdmin };
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
});
