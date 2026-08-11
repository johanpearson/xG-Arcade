import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AnnouncementBannerSection } from './AnnouncementBannerSection';

// S-108 (docs/backlog.md): dedicated isolation coverage for
// AnnouncementBannerSection, extracted from AdminScreen.tsx by S-103 with no
// behavior change. Mirrors AdminScreen.test.tsx's REQ-511 assertions, but
// renders the component directly — only /admin/announcement-banner* routes
// need stubbing here.

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

const loadedActiveBanner = {
  id: 'banner-1',
  message: 'Scheduled maintenance tonight at 10pm UTC.',
  isActive: true,
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-01T00:00:00Z',
  lastUpdatedByAdminId: 'admin-1',
};

const loadedInactiveBanner = { ...loadedActiveBanner, isActive: false };

describe('AnnouncementBannerSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-511: shows "No banner has been created yet" when the GET endpoint 404s (no-data-yet state)', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => bareNotFound()));

    render(<AnnouncementBannerSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Site-wide announcement banner')).toBeInTheDocument();
    expect(await screen.findByText('No banner has been created yet — write one below.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create banner' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Activate' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Deactivate' })).not.toBeInTheDocument();
  });

  it('REQ-511: shows the current message and "Active" status for a loaded, active banner', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse(loadedActiveBanner)));

    render(<AnnouncementBannerSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Status: Active — visible to every visitor')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue(loadedActiveBanner.message));
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Deactivate' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Activate' })).not.toBeInTheDocument();
  });

  it('REQ-511: shows "Inactive" status and an "Activate" button for a loaded, inactive banner', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse(loadedInactiveBanner)));

    render(<AnnouncementBannerSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Status: Inactive — not shown to visitors')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Activate' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Deactivate' })).not.toBeInTheDocument();
  });

  it('REQ-511: a 403 from the fetch hides the section entirely', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403)),
    );

    const { container } = render(<AnnouncementBannerSection accessToken="token" onAuthError={onAuthError} />);

    await waitFor(() => expect(container).toBeEmptyDOMElement());
    expect(onAuthError).not.toHaveBeenCalled();
  });

  it('REQ-511: a 401 from the fetch calls onAuthError', async () => {
    const onAuthError = vi.fn();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401)),
    );

    render(<AnnouncementBannerSection accessToken="token" onAuthError={onAuthError} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });

  it('REQ-511: the "Create banner" submit button is disabled while the message input is blank', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => bareNotFound()));

    render(<AnnouncementBannerSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByRole('button', { name: 'Create banner' })).toBeDisabled();
  });

  it('REQ-511: creating a banner (none exists yet) submits the typed message via PUT and shows the saved, inactive result', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      if (path.includes('/admin/announcement-banner')) {
        if (init?.method === 'PUT') return jsonResponse(loadedInactiveBanner);
        return bareNotFound();
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AnnouncementBannerSection accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByText('No banner has been created yet — write one below.');

    await user.type(screen.getByLabelText('Message'), loadedInactiveBanner.message);
    await user.click(screen.getByRole('button', { name: 'Create banner' }));

    await waitFor(() => {
      const putCall = fetchMock.mock.calls.find(
        ([callUrl, callInit]) =>
          String(callUrl).includes('/admin/announcement-banner') && (callInit as RequestInit)?.method === 'PUT',
      );
      expect(putCall).toBeDefined();
      const body = JSON.parse((putCall![1] as RequestInit).body as string);
      expect(body).toEqual({ message: loadedInactiveBanner.message });
    });

    expect(await screen.findByText('Status: Inactive — not shown to visitors')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Activate' })).toBeInTheDocument();
  });

  it('REQ-511: a save error (e.g. blank-message 400) is shown inline without crashing', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string, init?: RequestInit) => {
      const path = String(url);
      if (path.includes('/admin/announcement-banner')) {
        if (init?.method === 'PUT') return jsonResponse({ title: 'Bad Request', detail: 'Message cannot be blank.' }, 400);
        return bareNotFound();
      }
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AnnouncementBannerSection accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByText('No banner has been created yet — write one below.');

    await user.type(screen.getByLabelText('Message'), 'A new message');
    await user.click(screen.getByRole('button', { name: 'Create banner' }));

    expect(await screen.findByText('Message cannot be blank.')).toBeInTheDocument();
    expect(screen.getByText('No banner has been created yet — write one below.')).toBeInTheDocument();
  });

  it('REQ-511: "Activate" calls the activate endpoint and flips the shown status to Active', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/announcement-banner/activate')) {
        return jsonResponse({ ...loadedInactiveBanner, isActive: true });
      }
      if (path.includes('/admin/announcement-banner')) return jsonResponse(loadedInactiveBanner);
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AnnouncementBannerSection accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByText('Status: Inactive — not shown to visitors');

    await user.click(screen.getByRole('button', { name: 'Activate' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/admin/announcement-banner/activate'),
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(await screen.findByText('Status: Active — visible to every visitor')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Deactivate' })).toBeInTheDocument();
  });

  it('REQ-511: "Deactivate" calls the deactivate endpoint, flips the shown status to Inactive, and keeps the saved message', async () => {
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/announcement-banner/deactivate')) {
        return jsonResponse({ ...loadedActiveBanner, isActive: false });
      }
      if (path.includes('/admin/announcement-banner')) return jsonResponse(loadedActiveBanner);
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AnnouncementBannerSection accessToken="token" onAuthError={vi.fn()} />);
    await screen.findByText('Status: Active — visible to every visitor');

    await user.click(screen.getByRole('button', { name: 'Deactivate' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining('/admin/announcement-banner/deactivate'),
        expect.objectContaining({ method: 'POST' }),
      ),
    );
    expect(await screen.findByText('Status: Inactive — not shown to visitors')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue(loadedActiveBanner.message));
    expect(screen.getByRole('button', { name: 'Activate' })).toBeInTheDocument();
  });

  it('REQ-511: a 401 while activating calls onAuthError', async () => {
    const onAuthError = vi.fn();
    const fetchMock = vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      if (path.includes('/admin/announcement-banner/activate')) {
        return jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401);
      }
      if (path.includes('/admin/announcement-banner')) return jsonResponse(loadedInactiveBanner);
      throw new Error(`Unexpected fetch: ${path}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    const user = userEvent.setup();

    render(<AnnouncementBannerSection accessToken="token" onAuthError={onAuthError} />);
    await screen.findByText('Status: Inactive — not shown to visitors');

    await user.click(screen.getByRole('button', { name: 'Activate' }));

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});
