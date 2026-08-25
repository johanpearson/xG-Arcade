import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { PlayerAvatar } from './PlayerAvatar';

// REQ-722/S-184: PlayerAvatar is the other-players-facing avatar surface
// that requirements-document.md's REQ-722 status note (S-182) flagged as
// unbuilt. It fetches GET /users/{userId}/avatar/image directly (not via
// PlayerData/PlayerOverride — see ADR-0007's boundary, irrelevant here since
// this is avatar image data, not name/correctness data) and degrades
// quietly to a placeholder on any failure. jsdom has no native
// URL.createObjectURL/revokeObjectURL, so both are stubbed here, same
// convention SettingsScreen.test.tsx's own avatar-section suite already
// uses.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function imageResponse() {
  return Promise.resolve({
    ok: true,
    status: 200,
    blob: () => Promise.resolve(new Blob(['fake-image'], { type: 'image/png' })),
  } as unknown as Response);
}

describe('PlayerAvatar', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ722_PlayerAvatar_RendersImage_WhenTargetUserHasApprovedAvatar', async () => {
    vi.stubGlobal('URL', {
      ...URL,
      createObjectURL: vi.fn(() => 'blob:mock-avatar-url'),
      revokeObjectURL: vi.fn(),
    });
    const fetchMock = vi.fn().mockImplementation(() => imageResponse());
    vi.stubGlobal('fetch', fetchMock);

    render(<PlayerAvatar accessToken="token" userId="user-2" displayName="Sam" />);

    const image = await screen.findByTestId('player-avatar-image');
    expect(image).toHaveAttribute('src', 'blob:mock-avatar-url');
    expect(image).toHaveAttribute('alt', '');
    expect(screen.queryByTestId('player-avatar-placeholder')).not.toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining('/users/user-2/avatar/image'),
      expect.objectContaining({ headers: { Authorization: 'Bearer token' } }),
    );
  });

  it('REQ722_PlayerAvatar_RendersPlaceholder_WhenFetchFails', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ title: 'Not Found' }, 404));
    vi.stubGlobal('fetch', fetchMock);

    render(<PlayerAvatar accessToken="token" userId="user-3" displayName="Blair" />);

    expect(await screen.findByTestId('player-avatar-placeholder')).toBeInTheDocument();
    expect(screen.queryByTestId('player-avatar-image')).not.toBeInTheDocument();
    // No visible error text anywhere — a 404 (no Approved avatar) degrades
    // silently, same convention SettingsScreen.tsx's useAvatarObjectUrl
    // already uses for its own preview fetches.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('REQ722_PlayerAvatar_RendersPlaceholder_WhenFetchThrowsNetworkError', async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.reject(new Error('network down')));
    vi.stubGlobal('fetch', fetchMock);

    render(<PlayerAvatar accessToken="token" userId="user-4" displayName="Robin" />);

    expect(await screen.findByTestId('player-avatar-placeholder')).toBeInTheDocument();
  });

  it('REQ722_PlayerAvatar_RevokesObjectUrl_OnUnmountAndOnUserIdChange', async () => {
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', {
      ...URL,
      createObjectURL: vi.fn(() => 'blob:mock-avatar-url'),
      revokeObjectURL,
    });
    const fetchMock = vi.fn().mockImplementation(() => imageResponse());
    vi.stubGlobal('fetch', fetchMock);

    const { rerender, unmount } = render(
      <PlayerAvatar accessToken="token" userId="user-5" displayName="Alex" />,
    );
    await screen.findByTestId('player-avatar-image');

    rerender(<PlayerAvatar accessToken="token" userId="user-6" displayName="Alex" />);
    await waitFor(() => expect(revokeObjectURL).toHaveBeenCalledWith('blob:mock-avatar-url'));

    await screen.findByTestId('player-avatar-image');
    unmount();
    expect(revokeObjectURL).toHaveBeenCalledTimes(2);
  });

  it('REQ722_PlayerAvatar_UsesDefaultSize_WhenNoSizeProvided', async () => {
    vi.stubGlobal('URL', {
      ...URL,
      createObjectURL: vi.fn(() => 'blob:mock-avatar-url'),
      revokeObjectURL: vi.fn(),
    });
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => imageResponse()));

    render(<PlayerAvatar accessToken="token" userId="user-7" displayName="Jordan" />);

    const image = await screen.findByTestId('player-avatar-image');
    expect(image).toHaveStyle({ width: '64px', height: '64px' });
  });
});
