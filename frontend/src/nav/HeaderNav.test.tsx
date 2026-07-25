import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { HeaderNav } from './HeaderNav';

// REQ-712/REQ-713: isolated coverage of HeaderNav's own toggle/selection
// behavior, mounted directly with plain props/callbacks — no App/fetch
// involved. App.test.tsx already covers HeaderNav wired into the real app
// (routing, screen changes); this file is the component's own dedicated
// suite, matching the convention every other screen/component in this
// codebase already has (AdminScreen.test.tsx, DeleteAccountScreen.test.tsx,
// etc.).
function renderHeaderNav(overrides: Partial<Parameters<typeof HeaderNav>[0]> = {}) {
  const onSelectLeaderboard = vi.fn();
  const onSelectLeagues = vi.fn();
  const onSelectSettings = vi.fn();
  const onSelectGrid = vi.fn();
  const onLogout = vi.fn();

  render(
    <HeaderNav
      isLeaderboardCurrent={false}
      isLeaguesCurrent={false}
      isSettingsCurrent={false}
      isGridCurrent={false}
      onSelectLeaderboard={onSelectLeaderboard}
      onSelectLeagues={onSelectLeagues}
      onSelectSettings={onSelectSettings}
      onSelectGrid={onSelectGrid}
      onLogout={onLogout}
      {...overrides}
    />,
  );

  return { onSelectLeaderboard, onSelectLeagues, onSelectSettings, onSelectGrid, onLogout };
}

describe('HeaderNav', () => {
  it('REQ-712: the toggle starts with aria-expanded="false" and flips to "true" then back to "false" on repeated clicks', async () => {
    renderHeaderNav();
    const user = userEvent.setup();
    const toggle = screen.getByTestId('header-nav-toggle');

    expect(toggle).toHaveAttribute('aria-expanded', 'false');

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('REQ-712: the toggle is a real, focusable button exposing aria-controls for the menu it discloses', () => {
    renderHeaderNav();

    const toggle = screen.getByTestId('header-nav-toggle');
    expect(toggle.tagName).toBe('BUTTON');
    expect(toggle).toHaveAttribute('aria-controls', 'header-nav-menu');
  });

  // REQ-712's "reachable via Tab" clause is deliberately NOT tested here:
  // HeaderNav.css hides the toggle via its un-media-queried base rule
  // (`display: none`) outside the `@media (max-width: 480px)` block, and
  // jsdom in this project never evaluates `@media` (no `window.matchMedia`
  // at all — see App.test.tsx's REQ-712 comment, which documents the same
  // limitation). `user.tab()` correctly refuses to focus a display:none
  // element, exactly as a real browser would above the breakpoint, so
  // Tab-reachability can only be verified where the toggle is actually
  // visible: a real narrow viewport. See
  // tests/e2e/header-nav.spec.ts's "reachable via Tab and activates via
  // Enter/Space" test for that coverage. Keyboard *activation* semantics
  // (Enter/Space triggering onClick) fall out of using a real
  // `<button type="button">` — already asserted by the "real, focusable
  // button" test above — so no jsdom-level Enter/Space test is added here
  // either; faking Tab/focus in jsdom (e.g. calling `.focus()` directly, or
  // stubbing `matchMedia`) would pass regardless of real Tab-reachability
  // and so would not actually cover the acceptance criterion.

  it('REQ-712/REQ-713: clicking "Leaderboard" calls onSelectLeaderboard and closes the menu (selectAndClose)', async () => {
    const { onSelectLeaderboard, onSelectLeagues, onSelectSettings, onLogout } = renderHeaderNav();
    const user = userEvent.setup();
    const toggle = screen.getByTestId('header-nav-toggle');

    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');

    await user.click(screen.getByRole('button', { name: 'Leaderboard' }));

    expect(onSelectLeaderboard).toHaveBeenCalledTimes(1);
    expect(onSelectLeagues).not.toHaveBeenCalled();
    expect(onSelectSettings).not.toHaveBeenCalled();
    expect(onLogout).not.toHaveBeenCalled();
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  // REQ-402/403: same "click, calls its own handler, closes the menu" shape
  // as every other nav entry above/below.
  it('REQ-402/403: clicking "Leagues" calls onSelectLeagues and closes the menu (selectAndClose)', async () => {
    const { onSelectLeaderboard, onSelectLeagues, onSelectSettings, onLogout } = renderHeaderNav();
    const user = userEvent.setup();
    const toggle = screen.getByTestId('header-nav-toggle');

    await user.click(toggle);
    await user.click(screen.getByRole('button', { name: 'Leagues' }));

    expect(onSelectLeagues).toHaveBeenCalledTimes(1);
    expect(onSelectLeaderboard).not.toHaveBeenCalled();
    expect(onSelectSettings).not.toHaveBeenCalled();
    expect(onLogout).not.toHaveBeenCalled();
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('REQ-712/REQ-713: clicking "Settings" calls onSelectSettings and closes the menu (selectAndClose)', async () => {
    const { onSelectLeaderboard, onSelectLeagues, onSelectSettings, onLogout } = renderHeaderNav();
    const user = userEvent.setup();
    const toggle = screen.getByTestId('header-nav-toggle');

    await user.click(toggle);
    await user.click(screen.getByRole('button', { name: 'Settings' }));

    expect(onSelectSettings).toHaveBeenCalledTimes(1);
    expect(onSelectLeaderboard).not.toHaveBeenCalled();
    expect(onSelectLeagues).not.toHaveBeenCalled();
    expect(onLogout).not.toHaveBeenCalled();
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('REQ-712: clicking "Log out" calls onLogout and closes the menu (selectAndClose)', async () => {
    const { onSelectLeaderboard, onSelectLeagues, onSelectSettings, onLogout } = renderHeaderNav();
    const user = userEvent.setup();
    const toggle = screen.getByTestId('header-nav-toggle');

    await user.click(toggle);
    await user.click(screen.getByRole('button', { name: 'Log out' }));

    expect(onLogout).toHaveBeenCalledTimes(1);
    expect(onSelectLeaderboard).not.toHaveBeenCalled();
    expect(onSelectLeagues).not.toHaveBeenCalled();
    expect(onSelectSettings).not.toHaveBeenCalled();
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('REQ-712: aria-current="page" reflects isLeaderboardCurrent/isLeaguesCurrent/isSettingsCurrent — none current by default', () => {
    renderHeaderNav();

    expect(screen.getByRole('button', { name: 'Leaderboard' })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('button', { name: 'Leagues' })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('button', { name: 'Settings' })).not.toHaveAttribute('aria-current');
  });

  it('REQ-712: aria-current="page" is set on "Leaderboard" when isLeaderboardCurrent is true, and not on "Leagues"/"Settings"', () => {
    renderHeaderNav({ isLeaderboardCurrent: true });

    expect(screen.getByRole('button', { name: 'Leaderboard' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('button', { name: 'Leagues' })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('button', { name: 'Settings' })).not.toHaveAttribute('aria-current');
  });

  // REQ-402/403: same aria-current wiring as the other two nav entries.
  it('REQ-402/403: aria-current="page" is set on "Leagues" when isLeaguesCurrent is true, and not on "Leaderboard"/"Settings"', () => {
    renderHeaderNav({ isLeaguesCurrent: true });

    expect(screen.getByRole('button', { name: 'Leagues' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('button', { name: 'Leaderboard' })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('button', { name: 'Settings' })).not.toHaveAttribute('aria-current');
  });

  it('REQ-712: aria-current="page" is set on "Settings" when isSettingsCurrent is true, and not on "Leaderboard"/"Leagues"', () => {
    renderHeaderNav({ isSettingsCurrent: true });

    expect(screen.getByRole('button', { name: 'Settings' })).toHaveAttribute('aria-current', 'page');
    expect(screen.getByRole('button', { name: 'Leaderboard' })).not.toHaveAttribute('aria-current');
    expect(screen.getByRole('button', { name: 'Leagues' })).not.toHaveAttribute('aria-current');
  });
});

// REQ-720: the "Games" entry — a disclosure toggle, not a link, nested
// inside HeaderNav's own outer toggle/menu. Isolated coverage of its own
// open/close, aria-expanded/aria-current, and non-navigating behavior;
// App.test.tsx separately covers it wired into the real app (navigating to
// the grid screen and updating the URL).
describe('HeaderNav (REQ-720: "Games" nav entry)', () => {
  it('REQ-720: the "Games" toggle starts with aria-expanded="false", flips to "true" then back on repeated clicks, independent of the outer toggle', async () => {
    renderHeaderNav();
    const user = userEvent.setup();
    const outerToggle = screen.getByTestId('header-nav-toggle');
    const gamesToggle = screen.getByTestId('header-nav-games-toggle');

    expect(gamesToggle).toHaveAttribute('aria-expanded', 'false');

    // Opening the outer menu does not itself open the nested Games list.
    await user.click(outerToggle);
    expect(gamesToggle).toHaveAttribute('aria-expanded', 'false');

    await user.click(gamesToggle);
    expect(gamesToggle).toHaveAttribute('aria-expanded', 'true');
    // The outer toggle's own state is unaffected by the nested one.
    expect(outerToggle).toHaveAttribute('aria-expanded', 'true');

    await user.click(gamesToggle);
    expect(gamesToggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('REQ-720: the "Games" toggle is a real, focusable button exposing aria-controls, and never itself calls a navigation handler', async () => {
    const { onSelectLeaderboard, onSelectLeagues, onSelectSettings, onSelectGrid, onLogout } = renderHeaderNav();
    const user = userEvent.setup();
    const gamesToggle = screen.getByTestId('header-nav-games-toggle');

    expect(gamesToggle.tagName).toBe('BUTTON');
    expect(gamesToggle).toHaveAttribute('aria-controls', 'header-nav-games-list');

    await user.click(gamesToggle);
    await user.click(gamesToggle);

    expect(onSelectLeaderboard).not.toHaveBeenCalled();
    expect(onSelectLeagues).not.toHaveBeenCalled();
    expect(onSelectSettings).not.toHaveBeenCalled();
    expect(onSelectGrid).not.toHaveBeenCalled();
    expect(onLogout).not.toHaveBeenCalled();
  });

  it('REQ-720: selecting "xG Grid" calls onSelectGrid and closes both the Games list and the outer menu', async () => {
    const { onSelectGrid, onSelectLeaderboard, onSelectLeagues, onSelectSettings, onLogout } = renderHeaderNav();
    const user = userEvent.setup();
    const outerToggle = screen.getByTestId('header-nav-toggle');
    const gamesToggle = screen.getByTestId('header-nav-games-toggle');

    await user.click(outerToggle);
    await user.click(gamesToggle);
    expect(gamesToggle).toHaveAttribute('aria-expanded', 'true');

    await user.click(screen.getByRole('button', { name: 'xG Grid' }));

    expect(onSelectGrid).toHaveBeenCalledTimes(1);
    expect(onSelectLeaderboard).not.toHaveBeenCalled();
    expect(onSelectLeagues).not.toHaveBeenCalled();
    expect(onSelectSettings).not.toHaveBeenCalled();
    expect(onLogout).not.toHaveBeenCalled();
    expect(outerToggle).toHaveAttribute('aria-expanded', 'false');
    expect(gamesToggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('REQ-720: closing the outer menu also closes the nested Games list', async () => {
    renderHeaderNav();
    const user = userEvent.setup();
    const outerToggle = screen.getByTestId('header-nav-toggle');
    const gamesToggle = screen.getByTestId('header-nav-games-toggle');

    await user.click(outerToggle);
    await user.click(gamesToggle);
    expect(gamesToggle).toHaveAttribute('aria-expanded', 'true');

    await user.click(outerToggle);
    expect(outerToggle).toHaveAttribute('aria-expanded', 'false');
    expect(gamesToggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('REQ-720: aria-current="page" is not set on "xG Grid" by default, and is set when isGridCurrent is true', async () => {
    const user = userEvent.setup();

    const { unmount } = render(
      <HeaderNav
        isLeaderboardCurrent={false}
        isLeaguesCurrent={false}
        isSettingsCurrent={false}
        isGridCurrent={false}
        onSelectLeaderboard={vi.fn()}
        onSelectLeagues={vi.fn()}
        onSelectSettings={vi.fn()}
        onSelectGrid={vi.fn()}
        onLogout={vi.fn()}
      />,
    );
    await user.click(screen.getByTestId('header-nav-games-toggle'));
    expect(screen.getByRole('button', { name: 'xG Grid' })).not.toHaveAttribute('aria-current');
    unmount();

    render(
      <HeaderNav
        isLeaderboardCurrent={false}
        isLeaguesCurrent={false}
        isSettingsCurrent={false}
        isGridCurrent
        onSelectLeaderboard={vi.fn()}
        onSelectLeagues={vi.fn()}
        onSelectSettings={vi.fn()}
        onSelectGrid={vi.fn()}
        onLogout={vi.fn()}
      />,
    );
    await user.click(screen.getByTestId('header-nav-games-toggle'));
    expect(screen.getByRole('button', { name: 'xG Grid' })).toHaveAttribute('aria-current', 'page');
  });
});
