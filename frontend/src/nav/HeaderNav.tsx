import { useState } from 'react';
import './HeaderNav.css';

export interface HeaderNavProps {
  isLeaderboardCurrent: boolean;
  isLeaguesCurrent: boolean;
  isSettingsCurrent: boolean;
  onSelectLeaderboard: () => void;
  onSelectLeagues: () => void;
  onSelectSettings: () => void;
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
  onSelectLeaderboard,
  onSelectLeagues,
  onSelectSettings,
  onLogout,
}: HeaderNavProps) {
  const [open, setOpen] = useState(false);

  function selectAndClose(action: () => void) {
    setOpen(false);
    action();
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
        onClick={() => setOpen((current) => !current)}
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
