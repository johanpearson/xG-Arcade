import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AnnouncementBanner } from './AnnouncementBanner';

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

// REQ-511 ("Test level: ... UI (an active banner is visible to a logged-in
// user, a guest, and a fully logged-out visitor...")): AnnouncementBanner
// itself has no notion of auth state at all — it's mounted above every
// auth-gated branch in App.tsx and never reads a token — so its own unit
// coverage is "does it fetch/render correctly", the "visible regardless of
// session" half of REQ-511 is what App.tsx's own placement (outside any
// auth-gated branch) guarantees; this file also asserts no Authorization
// header is ever sent, the concrete, testable proxy for "requires no
// authentication of any kind."
describe('AnnouncementBanner', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-511: renders the message when the fetched banner is active', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ active: true, message: 'Scheduled maintenance tonight at 10pm UTC.' })),
    );

    render(<AnnouncementBanner />);

    expect(await screen.findByText('Scheduled maintenance tonight at 10pm UTC.')).toBeInTheDocument();
    expect(screen.getByRole('status')).toBeInTheDocument();
  });

  it('REQ-511: fetches with no Authorization header, so it works for a fully logged-out visitor', async () => {
    const fetchMock = vi.fn().mockImplementation(() => jsonResponse({ active: true, message: 'Hello' }));
    vi.stubGlobal('fetch', fetchMock);

    render(<AnnouncementBanner />);

    await screen.findByText('Hello');
    expect(fetchMock).toHaveBeenCalledWith(expect.stringContaining('/announcement-banner'));
    const [, init] = fetchMock.mock.calls[0];
    expect(init).toBeUndefined();
  });

  it('REQ-511: renders nothing when no banner has ever been created ({ active: false, message: null })', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => jsonResponse({ active: false, message: null })));

    const { container } = render(<AnnouncementBanner />);

    // No fixed element to findBy here (the whole point is nothing renders),
    // so wait on the same promise microtask the fetch resolves through
    // before asserting the container stayed empty.
    await waitFor(() => expect(fetch).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('REQ-511: renders nothing when the only banner on record is inactive', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockImplementation(() => jsonResponse({ active: false, message: null })),
    );

    const { container } = render(<AnnouncementBanner />);

    await waitFor(() => expect(fetch).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it('REQ-511: a fetch failure renders nothing rather than crashing the page', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.reject(new Error('network down'))));

    const { container } = render(<AnnouncementBanner />);

    await waitFor(() => expect(fetch).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});
