import { useState } from 'react';
import './HeaderNav.css';

export interface HeaderNavProps {
  isLeaderboardCurrent: boolean;
  isLeaguesCurrent: boolean;
  isSettingsCurrent: boolean;
  // REQ-720: whether xG Grid's own screen is currently showing — Tier 0's
  // only game, so this was the only per-game aria-current flag at first;
  // S-085 adds isPathCurrent alongside it, not in place of it, once xG Path
  // exists as a second game.
  isGridCurrent: boolean;
  // S-085/SCREEN-09: mirrors isGridCurrent above for xG Path — whether xG
  // Path's own screen is currently showing.
  isPathCurrent: boolean;
  onSelectLeaderboard: () => void;
  onSelectLeagues: () => void;
  onSelectSettings: () => void;
  // REQ-720: selecting "xG Grid" from the "Games" list — same destination
  // GameSelectScreen's own "xG Grid" tile already triggers.
  onSelectGrid: () => void;
  // S-085/SCREEN-09: mirrors onSelectGrid above for xG Path — same
  // destination GameSelectScreen's own "xG Path" tile already triggers.
  // Keeps this list and GameSelectScreen's tile order in agreement (xG Grid
  // first, xG Path second).
  onSelectPath: () => void;
  onLogout: () => void;
}

// REQ-712 (design-document.md §4's new "Header nav breakpoint" note): below
// 480px — reusing the same "narrow phone" breakpoint value SCREEN-01's
// header-label wrapping already uses for an analogous narrow-viewport
// overflow problem, rather than inventing a second one — every nav entry
// (Leaderboard, Settings, Log out) collapses behind this single toggle so
// the header row never wraps/overflows no matter how many entries exist.
// This component's own DOM output never changes with viewport width; only
// `HeaderNav.css`'s `@media (max-width: 480px)` block decides which of the
// toggle button or the plain horizontal row is actually visible — the same
// CSS-only responsive approach the rest of the app already uses (Grid.css,
// App.css's own `@media` blocks), not a new JS viewport-detection pattern.
// `open` starts `false` and is irrelevant at/above the breakpoint (the CSS
// there forces the row visible regardless of this state).
export function HeaderNav({
  isLeaderboardCurrent,
  isLeaguesCurrent,
  isSettingsCurrent,
  isGridCurrent,
  isPathCurrent,
  onSelectLeaderboard,
  onSelectLeagues,
  onSelectSettings,
  onSelectGrid,
  onSelectPath,
  onLogout,
}: HeaderNavProps) {
  const [open, setOpen] = useState(false);
  // REQ-720: independent of `open` above — a nested disclosure within the
  // outer one on mobile, but its own separate toggle at/above the
  // breakpoint too (see design-document.md's updated SCREEN-07). Reset to
  // closed whenever the outer menu is closed (toggleOuter below) or when
  // any entry is selected (selectAndClose), so it never lingers open the
  // next time either menu is reopened.
  const [gamesOpen, setGamesOpen] = useState(false);

  function selectAndClose(action: () => void) {
    setOpen(false);
    setGamesOpen(false);
    action();
  }

  function toggleOuter() {
    setOpen((current) => {
      const next = !current;
      if (!next) setGamesOpen(false);
      return next;
    });
  }

  function toggleGames() {
    setGamesOpen((current) => !current);
  }

  return (
    <nav className="header-nav">
      {/* A real, focusable <button> (Tab-reachable, Enter/Space-activatable
          by default) — the same accessible-disclosure pattern REQ-204's
          reveal toggles already established (GridCell.tsx): aria-expanded
          reflects open/closed state, and the toggle is the thing that
          changes, not a second custom widget. Hidden entirely (not merely
          inert) at/above the breakpoint via HeaderNav.css. */}
      <button
        type="button"
        className="header-nav__toggle"
        aria-expanded={open}
        aria-controls="header-nav-menu"
        onClick={toggleOuter}
        data-testid="header-nav-toggle"
      >
        <span aria-hidden="true" className="header-nav__toggle-icon">
          ☰
        </span>
        Menu
      </button>
      <div
        id="header-nav-menu"
        className={`header-nav__menu${open ? ' header-nav__menu--open' : ''}`}
      >
        {/* REQ-720: a disclosure control, not a link — activating it never
            navigates, it only shows/hides the per-game list below. Same
            accessible-disclosure pattern as the outer toggle above
            (aria-expanded, a real focusable <button>). Nested inside the
            outer mobile menu but rendered identically at/above the
            breakpoint too, since the flat row there is just this same
            `header-nav__menu` markup made visible by CSS. */}
        <div className="header-nav__games">
          <button
            type="button"
            className="header-nav__link header-nav__games-toggle"
            aria-expanded={gamesOpen}
            aria-controls="header-nav-games-list"
            onClick={toggleGames}
            data-testid="header-nav-games-toggle"
          >
            Games
          </button>
          <div
            id="header-nav-games-list"
            className={`header-nav__games-list${gamesOpen ? ' header-nav__games-list--open' : ''}`}
          >
            {/* REQ-720/S-085: one entry per game xG Arcade currently
                hosts, xG Grid first (the original game) — see
                requirements-document.md REQ-720's "one entry per game"
                acceptance criterion. */}
            <button
              type="button"
              className="header-nav__link header-nav__games-item"
              aria-current={isGridCurrent ? 'page' : undefined}
              onClick={() => selectAndClose(onSelectGrid)}
            >
              xG Grid
            </button>
            {/* S-085/SCREEN-09: mirrors the xG Grid entry above, positioned
                second — keeps this list and GameSelectScreen's tile order
                in agreement (never alphabetical/recency). */}
            <button
              type="button"
              className="header-nav__link header-nav__games-item"
              aria-current={isPathCurrent ? 'page' : undefined}
              onClick={() => selectAndClose(onSelectPath)}
            >
              xG Path
            </button>
          </div>
        </div>
        <button
          type="button"
          className="header-nav__link"
          aria-current={isLeaderboardCurrent ? 'page' : undefined}
          onClick={() => selectAndClose(onSelectLeaderboard)}
        >
          Leaderboard
        </button>
        {/* REQ-402/403: a player's custom leagues — create, join, and see
            which ones they belong to. */}
        <button
          type="button"
          className="header-nav__link"
          aria-current={isLeaguesCurrent ? 'page' : undefined}
          onClick={() => selectAndClose(onSelectLeagues)}
        >
          Leagues
        </button>
        {/* REQ-713: replaces the previously separate "Delete account" and
            (admin-only) "Admin" top-level links with this one entry. */}
        <button
          type="button"
          className="header-nav__link"
          aria-current={isSettingsCurrent ? 'page' : undefined}
          onClick={() => selectAndClose(onSelectSettings)}
        >
          Settings
        </button>
        <button
          type="button"
          className="header-nav__logout"
          onClick={() => selectAndClose(onLogout)}
        >
          Log out
        </button>
      </div>
    </nav>
  );
}
