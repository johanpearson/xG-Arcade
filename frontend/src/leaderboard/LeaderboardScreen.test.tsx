import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LeaderboardScreen } from './LeaderboardScreen';
// REQ-213 (S-068): only needed for the "both entry points render identical
// content" test below, which renders GridScreen alongside LeaderboardScreen
// to compare their explainer's actual DOM output.
import { GridScreen } from '../grid/GridScreen';
import { defaultAllTimeRoute, jsonResponse, routedFetch, row } from './leaderboardTestHelpers';

// S-121: this file now holds only the cross-cutting concerns that live in
// LeaderboardScreen.tsx itself (the thin orchestrator) — the scoring
// explainer modal and the game switcher. Per-scope coverage (all-time/live/
// past-rounds/windowed fetch, poll, pagination, and tab-switch behavior)
// moved to AllTimeLeaderboard.test.tsx / LiveLeaderboard.test.tsx /
// PastRoundsLeaderboard.test.tsx / WindowedLeaderboard.test.tsx, matching
// the component split.

describe('LeaderboardScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // REQ-213 (S-068): the leaderboard screen's own `(ⓘ)` entry point for the
  // exact same ScoringExplainer GridScreen.tsx already uses. Exhaustive
  // content/scope-independence coverage per REQ-213's "Test level" note
  // (docs/requirements-document.md ~line 1561-1568).
  describe('scoring explainer entry point', () => {
    it('REQ-213: the (ⓘ) button is present and opens the shared ScoringExplainer before any leaderboard data has loaded (the all-time scope is still "loading" at click time)', () => {
      // Deliberately never resolving — the entry point must not depend on
      // any scope's fetch having completed (or even been started). This
      // also exercises the "opens while loading" branch of REQ-213's Test
      // level note, since the all-time scope's own status text
      // ("Loading the leaderboard…") is what's on screen at the moment the
      // button is clicked below.
      vi.stubGlobal('fetch', vi.fn().mockImplementation(() => new Promise(() => {})));

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);

      expect(screen.getByText('Loading the leaderboard…')).toBeInTheDocument();

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));

      const dialog = screen.getByRole('dialog', { name: 'How scoring works' });
      expect(dialog).toHaveAttribute('aria-modal', 'true');
    });

    it('REQ-213: opens while the all-time scope (the scope fetched on mount) is in an error state', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          Promise.resolve({ ok: false, status: 500, json: () => Promise.resolve({}) } as Response),
        ),
      );

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() =>
        expect(document.querySelector('.leaderboard-screen__status--error')).not.toBeNull(),
      );

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      expect(screen.getByRole('dialog', { name: 'How scoring works' })).toBeInTheDocument();
    });

    it('REQ-213: opens regardless of which scope tab is active — "Current Round" (a non-default scope, empty/loaded state)', async () => {
      const fetchMock = routedFetch([
        [
          '/leagues/global/leaderboard/active-round',
          () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ],
        defaultAllTimeRoute,
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));
      await waitFor(() => expect(screen.getByText('No one has played this round yet — be the first.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      expect(screen.getByRole('dialog', { name: 'How scoring works' })).toBeInTheDocument();
    });

    it('REQ-213: opens regardless of which scope tab is active — "Previous Rounds"', async () => {
      const fetchMock = routedFetch([
        [
          '/leagues/global/leaderboard/closed-rounds',
          () => jsonResponse({ rounds: [], nextCursor: null, hasMore: false }),
        ],
        defaultAllTimeRoute,
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('tab', { name: 'Previous Rounds' }));
      await waitFor(() => expect(screen.getByText('No rounds have closed yet.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      expect(screen.getByRole('dialog', { name: 'How scoring works' })).toBeInTheDocument();
    });

    it('REQ-213: opens regardless of which scope tab is active — "Time Windows"', async () => {
      const fetchMock = routedFetch([
        [
          '/leagues/global/leaderboard/window/round',
          () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ],
        defaultAllTimeRoute,
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('tab', { name: 'Time Windows' }));
      await waitFor(() => expect(screen.getByText('No one scored in this window yet.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      expect(screen.getByRole('dialog', { name: 'How scoring works' })).toBeInTheDocument();
    });

    it('REQ-213: opened from the leaderboard screen, the explainer contains all nine required content points — the original six (REQ-204/205/210/ADR-0021/ADR-0025) plus the three added for the leaderboard (REQ-409/404/406/407)', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ),
      );

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      const dialog = screen.getByRole('dialog');

      // Original six (mirrors ScoringExplainer.test.tsx's own presence
      // checks — kept in sync here since this is the leaderboard's own
      // entry point, not just the component in isolation).
      expect(dialog.textContent).toMatch(/live estimate/i);
      expect(dialog.textContent).toMatch(/round closes/i);
      expect(dialog.textContent).toMatch(/locked/i);
      expect(dialog.textContent).toMatch(/won't change again|does not change/i);
      expect(dialog.textContent).toMatch(/golf/i);
      expect(dialog.textContent).toMatch(/lower is better/i);
      expect(dialog.textContent).toMatch(/2 attempts per cell/i);
      expect(dialog.textContent).toMatch(/wrong guess/i);
      expect(dialog.textContent).toMatch(/maximum score/i);
      expect(dialog.textContent).toMatch(/not guessing at all/i);
      expect(dialog.textContent).toMatch(/male/i);
      expect(dialog.textContent).toMatch(/born in 1939 or later/i);

      // The three 2026-07-21 additions.
      expect(dialog.textContent).toMatch(/median/i);
      expect(dialog.textContent).toMatch(/not a total/i);
      expect(dialog.textContent).toMatch(/at least 5 qualifying rounds/i);
      expect(dialog.textContent).toMatch(/never submitted a single guess/i);
      expect(dialog.textContent).toMatch(/current round/i);
      expect(dialog.textContent).toMatch(/every other cell/i);
    });

    it('REQ-213: the grid-screen and leaderboard-screen entry points open the exact same component with identical content — neither is a subset of the other', async () => {
      // Leaderboard screen's dialog text.
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ),
      );
      const { unmount: unmountLeaderboard } = render(
        <LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />,
      );
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());
      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      const leaderboardDialogText = screen.getByRole('dialog').textContent;
      unmountLeaderboard();
      vi.unstubAllGlobals();

      // Grid screen's dialog text.
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({
            roundId: 'round-1',
            startTime: '2026-07-10T00:00:00Z',
            endTime: '2026-07-11T00:00:00Z',
            allowGuessChange: false,
            cells: [
              {
                cellId: 'cell-1',
                row: 0,
                col: 0,
                rowCategoryType: 'country',
                rowCategoryValue: 'France',
                colCategoryType: 'club',
                colCategoryValue: 'Arsenal',
                guess: null,
              },
            ],
          }),
        ),
      );
      const { unmount: unmountGrid } = render(<GridScreen accessToken="token" onAuthError={vi.fn()} />);
      await screen.findByRole('button', { name: 'Guess France × Arsenal' });
      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      const gridDialogText = screen.getByRole('dialog').textContent;
      unmountGrid();

      expect(gridDialogText).toBe(leaderboardDialogText);
    });

    it('REQ-213: closing the explainer from the leaderboard screen does not discard the currently selected scope tab', async () => {
      const fetchMock = routedFetch([
        [
          '/leagues/global/leaderboard/active-round',
          () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ],
        defaultAllTimeRoute,
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));
      await waitFor(() => expect(screen.getByText('No one has played this round yet — be the first.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      expect(screen.getByRole('dialog', { name: 'How scoring works' })).toBeInTheDocument();

      fireEvent.click(screen.getByRole('button', { name: 'Close' }));

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
      // The "Current Round" tab is still selected, and its already-loaded
      // (empty) state is still what's rendered — opening/closing the
      // explainer touched neither.
      expect(screen.getByRole('tab', { name: 'Current Round' })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByText('No one has played this round yet — be the first.')).toBeInTheDocument();
    });

    it('REQ-213: closing the explainer from the leaderboard screen does not discard an already-loaded "Load more" page', async () => {
      const fetchMock = vi
        .fn()
        .mockImplementationOnce(() =>
          jsonResponse({
            rows: [row(1, 'user-1', 'Alex', 10)],
            requestingUserRow: null,
            nextCursor: 50,
            hasMore: true,
          }),
        )
        .mockImplementationOnce(() =>
          jsonResponse({
            rows: [row(2, 'user-2', 'Blair', 20)],
            requestingUserRow: null,
            nextCursor: null,
            hasMore: false,
          }),
        );
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('button', { name: 'Load more' }));
      await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      expect(screen.getByRole('dialog', { name: 'How scoring works' })).toBeInTheDocument();

      fireEvent.click(screen.getByRole('button', { name: 'Close' }));

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
      // Both the original page and the "Load more" page's row are still
      // present, in order — opening/closing the explainer discarded
      // neither.
      const rows = screen.getAllByRole('listitem');
      expect(rows).toHaveLength(2);
      expect(rows[0]).toHaveTextContent('Alex');
      expect(rows[1]).toHaveTextContent('Blair');
    });
  });

  // REQ-213 (2026-08-08, second-consumer follow-up): the `(ⓘ)` entry
  // point's modal is now game-aware — xG Grid's `ScoringExplainer` vs. xG
  // Path's `PathScoringExplainer`, chosen by `gameKey` — closing the gap
  // flagged (not fixed) in `PathScoringExplainer.tsx`'s own 2026-08-08
  // doc comment and `docs/requirements-document.md`'s matching REQ-213
  // note. Distinguishes the two explainers by content unique to each
  // (Grid's "attempts per cell"/"median" ranking language vs. Path's
  // "attempts per puzzle" clue-sequence language) rather than by DOM
  // structure, since both render the same `role="dialog"`/
  // `aria-label="How scoring works"` shell.
  describe('game-aware scoring explainer', () => {
    it('REQ-213: xG Grid tab (the default) + (ⓘ) opens the Grid ScoringExplainer, not the Path one', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ),
      );

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());
      expect(screen.getByRole('tab', { name: 'xG Grid' })).toHaveAttribute('aria-selected', 'true');

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));

      const dialog = screen.getByRole('dialog', { name: 'How scoring works' });
      expect(dialog.textContent).toMatch(/2 attempts per cell/i);
      expect(dialog.textContent).toMatch(/median/i);
      expect(dialog.textContent).not.toMatch(/attempts per puzzle/i);
      expect(dialog.textContent).not.toMatch(/clue/i);
    });

    it('REQ-213: switching to the xG Path tab + (ⓘ) opens PathScoringExplainer, not the Grid one', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ),
      );

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('tab', { name: 'xG Path' }));
      await waitFor(() => expect(screen.getByRole('tab', { name: 'xG Path' })).toHaveAttribute('aria-selected', 'true'));

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));

      const dialog = screen.getByRole('dialog', { name: 'How scoring works' });
      expect(dialog.textContent).toMatch(/attempts per puzzle/i);
      expect(dialog.textContent).toMatch(/golf/i);
      expect(dialog.textContent).not.toMatch(/median/i);
      expect(dialog.textContent).not.toMatch(/2 attempts per cell/i);
      expect(dialog.textContent).not.toMatch(/uniqueness|unique/i);
    });

    it('REQ-213: switching games while the explainer is open closes it (does not silently swap its content under the player)', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ),
      );

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      expect(screen.getByRole('dialog', { name: 'How scoring works' })).toBeInTheDocument();

      fireEvent.click(screen.getByRole('tab', { name: 'xG Path' }));

      // The modal is gone — not left open with Grid's now-stale content,
      // and not silently swapped to Path's content while still open.
      await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
      // The game switch itself still took effect normally.
      await waitFor(() => expect(screen.getByRole('tab', { name: 'xG Path' })).toHaveAttribute('aria-selected', 'true'));

      // Re-opening it now shows the newly selected game's explainer.
      fireEvent.click(screen.getByRole('button', { name: 'How scoring works' }));
      const reopenedDialog = screen.getByRole('dialog', { name: 'How scoring works' });
      expect(reopenedDialog.textContent).toMatch(/attempts per puzzle/i);
    });

    it('REQ-213: switching games while the explainer is closed has no effect on it (it stays closed)', async () => {
      vi.stubGlobal(
        'fetch',
        vi.fn().mockImplementation(() =>
          jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ),
      );

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
      fireEvent.click(screen.getByRole('tab', { name: 'xG Path' }));
      await waitFor(() => expect(screen.getByRole('tab', { name: 'xG Path' })).toHaveAttribute('aria-selected', 'true'));
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  // REQ-410/ADR-0043 (S-087): the game switcher tab row above the scope
  // tabs — selecting a game re-fetches whichever scope is currently active
  // with the new `gameKey`, and never resets the selected scope tab.
  describe('game switcher', () => {
    it('REQ410: selecting "xG Path" while on the default All-time scope re-fetches the all-time endpoint with gameKey=xg-path', async () => {
      const fetchMock = routedFetch([
        [
          /gameKey=xg-path/,
          () =>
            jsonResponse({
              rows: [row(1, 'user-2', 'Blair', 20)],
              requestingUserRow: null,
              nextCursor: null,
              hasMore: false,
            }),
        ],
        [/gameKey=xg-grid/, () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false })],
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('tab', { name: 'xG Path' }));

      await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());
      const xgPathCalls = fetchMock.mock.calls.filter((call) => String(call[0]).includes('gameKey=xg-path'));
      expect(xgPathCalls.length).toBeGreaterThan(0);
      expect(String(xgPathCalls[0][0])).toContain('/leagues/global/leaderboard');
    });

    it('REQ410: switching games while on "Current Round" scope re-fetches the active-round endpoint with the new gameKey, keeping the scope tab selected', async () => {
      const fetchMock = routedFetch([
        [
          /active-round\?gameKey=xg-path/,
          () =>
            jsonResponse({
              rows: [row(1, 'user-2', 'Blair', 5)],
              requestingUserRow: null,
              nextCursor: null,
              hasMore: false,
            }),
        ],
        [
          '/leagues/global/leaderboard/active-round',
          () => jsonResponse({ rows: [], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ],
        defaultAllTimeRoute,
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('tab', { name: 'Current Round' }));
      await waitFor(() => expect(screen.getByText('No one has played this round yet — be the first.')).toBeInTheDocument());

      fireEvent.click(screen.getByRole('tab', { name: 'xG Path' }));

      await waitFor(() => expect(screen.getByText('Blair')).toBeInTheDocument());

      // Scope preserved across the game switch — still "Current Round",
      // never silently reset to "All-time".
      expect(screen.getByRole('tab', { name: 'Current Round' })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByRole('tab', { name: 'All-time' })).toHaveAttribute('aria-selected', 'false');
    });

    it('REQ410: the game tab row marks the selected game, defaulting to "xG Grid"', async () => {
      const fetchMock = routedFetch([defaultAllTimeRoute]);
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);
      await waitFor(() => expect(screen.getByText('No scores yet — be the first to play a round.')).toBeInTheDocument());

      expect(screen.getByRole('tab', { name: 'xG Grid' })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByRole('tab', { name: 'xG Path' })).toHaveAttribute('aria-selected', 'false');

      fireEvent.click(screen.getByRole('tab', { name: 'xG Path' }));

      await waitFor(() => expect(screen.getByRole('tab', { name: 'xG Path' })).toHaveAttribute('aria-selected', 'true'));
      expect(screen.getByRole('tab', { name: 'xG Grid' })).toHaveAttribute('aria-selected', 'false');
    });
  });

  // REQ-1210/ADR-0083: `initial*` props seed this screen's own scope/game
  // state at mount, for the round-completion banner's leaderboard link —
  // see PastRoundsLeaderboard.test.tsx for the "past" + initialRoundId
  // drill-in case specifically.
  describe('REQ-1210: initialGameKey/initialScope/initialRoundId seed the screen at mount', () => {
    it('initialScope "live" starts on the "Current Round" tab already selected and fetches it without any click', async () => {
      const fetchMock = routedFetch([
        [
          '/leagues/global/leaderboard/active-round',
          () => jsonResponse({ rows: [row(1, 'user-1', 'Alex', 12)], requestingUserRow: null, nextCursor: null, hasMore: false }),
        ],
        defaultAllTimeRoute,
      ]);
      vi.stubGlobal('fetch', fetchMock);

      render(
        <LeaderboardScreen accessToken="token" onAuthError={vi.fn()} initialScope="live" initialGameKey="xg-path" />,
      );

      expect(screen.getByRole('tab', { name: 'Current Round' })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByRole('tab', { name: 'xG Path' })).toHaveAttribute('aria-selected', 'true');
      await waitFor(() => expect(screen.getByText('Alex')).toBeInTheDocument());
    });

    it('a normal manual visit (no initial* props) still defaults exactly as before — xG Grid, All-time', async () => {
      const fetchMock = routedFetch([defaultAllTimeRoute]);
      vi.stubGlobal('fetch', fetchMock);

      render(<LeaderboardScreen accessToken="token" onAuthError={vi.fn()} />);

      expect(screen.getByRole('tab', { name: 'All-time' })).toHaveAttribute('aria-selected', 'true');
      expect(screen.getByRole('tab', { name: 'xG Grid' })).toHaveAttribute('aria-selected', 'true');
    });
  });
});
