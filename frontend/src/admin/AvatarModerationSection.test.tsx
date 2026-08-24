import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AvatarModerationSection } from './AvatarModerationSection';

// S-183 (REQ-517): smoke coverage for the avatar-moderation queue —
// mirrors PlayerSuggestionsEntry.test.tsx/AccountMetricsSection.test.tsx's
// own stub-fetch shape. Not exhaustive (a fuller pass is a separate
// test-writer delegation) — covers the queue rendering with a preview, the
// "(N)"/no-"(0)" badge convention, approve/reject removing a row, and the
// 409-conflict distinct message.

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve({
    ok: status >= 200 && status < 300,
    status,
    json: () => Promise.resolve(body),
  } as Response);
}

function submission(id: string, overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id,
    imagePreviewUrl: `https://example.test/preview/${id}`,
    submittingUserId: `user-${id}`,
    submittingUserDisplayName: 'Player One',
    createdAt: '2026-08-01T00:00:00Z',
    ...overrides,
  };
}

function stubFetch(routes: Record<string, (path: string) => Promise<Response>>) {
  vi.stubGlobal(
    'fetch',
    vi.fn().mockImplementation((url: string) => {
      const path = String(url);
      const match = Object.entries(routes).find(([suffix]) => path.includes(suffix));
      if (match) return match[1](path);
      throw new Error(`Unexpected fetch: ${path}`);
    }),
  );
}

describe('AvatarModerationSection', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('REQ-517: shows "Avatar moderation (2)" and a preview image per row', async () => {
    stubFetch({
      '/admin/avatar-submissions': () => jsonResponse([submission('a-1'), submission('a-2')]),
    });

    render(<AvatarModerationSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Avatar moderation (2)')).toBeInTheDocument();
    const images = screen.getAllByRole('img');
    expect(images).toHaveLength(2);
    expect(images[0]).toHaveAttribute('src', 'https://example.test/preview/a-1');
  });

  it('REQ-517: shows plain "Avatar moderation" with no "(0)" and an empty-state message when nothing is pending', async () => {
    stubFetch({
      '/admin/avatar-submissions': () => jsonResponse([]),
    });

    render(<AvatarModerationSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Avatar moderation')).toBeInTheDocument();
    expect(screen.getByText('No pending avatar submissions to review.')).toBeInTheDocument();
  });

  it('REQ-710: falls back to "a deleted user" when submittingUserDisplayName is null', async () => {
    stubFetch({
      '/admin/avatar-submissions': () =>
        jsonResponse([submission('a-1', { submittingUserDisplayName: null })]),
    });

    render(<AvatarModerationSection accessToken="token" onAuthError={vi.fn()} />);

    expect(await screen.findByText('Submitted by a deleted user')).toBeInTheDocument();
  });

  it('REQ-517: approving a submission removes it from the pending list', async () => {
    let listCallCount = 0;
    stubFetch({
      '/admin/avatar-submissions/a-1/approve': () => jsonResponse(undefined, 204),
      '/admin/avatar-submissions': () => {
        listCallCount += 1;
        return jsonResponse(listCallCount === 1 ? [submission('a-1')] : []);
      },
    });
    const user = userEvent.setup();

    render(<AvatarModerationSection accessToken="token" onAuthError={vi.fn()} />);

    await screen.findByText('Avatar moderation (1)');
    await user.click(screen.getByRole('button', { name: 'Approve' }));

    await waitFor(() => expect(screen.getByText('Avatar moderation')).toBeInTheDocument());
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });

  it('REQ-517: rejecting a submission removes it from the pending list', async () => {
    let listCallCount = 0;
    stubFetch({
      '/admin/avatar-submissions/a-1/reject': () => jsonResponse(undefined, 204),
      '/admin/avatar-submissions': () => {
        listCallCount += 1;
        return jsonResponse(listCallCount === 1 ? [submission('a-1')] : []);
      },
    });
    const user = userEvent.setup();

    render(<AvatarModerationSection accessToken="token" onAuthError={vi.fn()} />);

    await screen.findByText('Avatar moderation (1)');
    await user.click(screen.getByRole('button', { name: 'Reject' }));

    await waitFor(() => expect(screen.getByText('Avatar moderation')).toBeInTheDocument());
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });

  it('REQ-517: a 409 on approve shows a distinct "already resolved" message, not a generic error', async () => {
    stubFetch({
      '/admin/avatar-submissions/a-1/approve': () =>
        jsonResponse({ title: 'Avatar submission already resolved' }, 409),
      '/admin/avatar-submissions': () => jsonResponse([submission('a-1')]),
    });
    const user = userEvent.setup();

    render(<AvatarModerationSection accessToken="token" onAuthError={vi.fn()} />);

    await screen.findByText('Avatar moderation (1)');
    await user.click(screen.getByRole('button', { name: 'Approve' }));

    expect(
      await screen.findByText('Already resolved by another admin — refresh to see the current state.'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument();
  });

  it('REQ-517: a 403 from the list fetch hides the section entirely', async () => {
    stubFetch({
      '/admin/avatar-submissions': () => jsonResponse({ title: 'Forbidden', detail: 'Admins only.' }, 403),
    });

    const { container } = render(<AvatarModerationSection accessToken="token" onAuthError={vi.fn()} />);

    await waitFor(() => expect(container).toBeEmptyDOMElement());
  });

  it('REQ-517: a 401 from the list fetch calls onAuthError', async () => {
    const onAuthError = vi.fn();
    stubFetch({
      '/admin/avatar-submissions': () =>
        jsonResponse({ title: 'Unauthorized', detail: 'Session expired.' }, 401),
    });

    render(<AvatarModerationSection accessToken="token" onAuthError={onAuthError} />);

    await waitFor(() => expect(onAuthError).toHaveBeenCalledTimes(1));
  });
});
