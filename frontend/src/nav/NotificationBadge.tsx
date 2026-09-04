import { useState } from 'react';
import './NotificationBadge.css';

export interface NotificationBadgeProps {
  pendingFriendRequestCount: number;
  pendingChallengeCount: number;
  matchesAwaitingActionCount: number;
  // REQ-1411 (design-document.md SCREEN-07's 2026-09-03 badge-redesign
  // status note): opens SCREEN-15 already on the matching tab. "Matches
  // awaiting your move" used to render as plain, non-interactive text
  // because S-218's match/gameplay screen didn't exist yet — S-218 has
  // since shipped SCREEN-15's "Matches" tab (with its own drill-down into
  // a single match, `FriendsScreen.tsx`), so all three categories are now
  // real navigation targets.
  onOpenFriendsTab: (tab: 'friends' | 'challenges' | 'matches') => void;
}

// design-document.md SCREEN-07 (2026-09-03 badge-redesign, direct user
// feedback: "I want the notification to be visible from the main screen,
// like a green or red icon with a white number... and then when clicking
// it, showing where the notification is"). Rendered unconditionally
// visible in the header (not nested inside HeaderNav's own collapsible
// mobile menu/outer toggle), immediately beside the "☰ Menu" toggle at
// every viewport width — the point is visibility without first opening the
// nav menu at all, which the previous "Friends (N)" text-in-parens label
// (buried inside the collapsed menu below 480px) didn't give.
//
// REQ-1411's own "no indicator at zero" rule still applies: this renders
// nothing at all when every count is zero, exactly like the header nav's
// previous inline "(N)" convention did.
export function NotificationBadge({
  pendingFriendRequestCount,
  pendingChallengeCount,
  matchesAwaitingActionCount,
  onOpenFriendsTab,
}: NotificationBadgeProps) {
  const [open, setOpen] = useState(false);
  const total = pendingFriendRequestCount + pendingChallengeCount + matchesAwaitingActionCount;

  if (total === 0) return null;

  function toggleOpen() {
    setOpen((current) => !current);
  }

  function handleSelect(tab: 'friends' | 'challenges' | 'matches') {
    setOpen(false);
    onOpenFriendsTab(tab);
  }

  return (
    <div className="notification-badge">
      {/* A real, focusable <button> (Tab-reachable, Enter/Space-activatable
          by native HTML button semantics) exposing aria-expanded for its
          open/closed state — the same accessible-disclosure pattern
          HeaderNav's own outer/"Games" toggles already establish. The
          visible numeral is real text (never color-only, design-document.md
          §6) wrapped in a decorative-sized pill; the button's own
          aria-label carries the full accessible name so a screen reader
          announces "3 notifications — view details," not just "3." */}
      <button
        type="button"
        className="notification-badge__toggle"
        aria-expanded={open}
        aria-controls="notification-badge-panel"
        aria-label={`${total} notification${total === 1 ? '' : 's'} — view details`}
        onClick={toggleOpen}
        data-testid="notification-badge-toggle"
      >
        <span aria-hidden="true" className="notification-badge__count">
          {total}
        </span>
      </button>
      {open && (
        <div id="notification-badge-panel" className="notification-badge__panel" role="menu">
          {pendingFriendRequestCount > 0 && (
            <button
              type="button"
              role="menuitem"
              className="notification-badge__item notification-badge__item--link"
              onClick={() => handleSelect('friends')}
            >
              Friend requests ({pendingFriendRequestCount})
            </button>
          )}
          {pendingChallengeCount > 0 && (
            <button
              type="button"
              role="menuitem"
              className="notification-badge__item notification-badge__item--link"
              onClick={() => handleSelect('challenges')}
            >
              Challenges ({pendingChallengeCount})
            </button>
          )}
          {matchesAwaitingActionCount > 0 && (
            <button
              type="button"
              role="menuitem"
              className="notification-badge__item notification-badge__item--link"
              onClick={() => handleSelect('matches')}
            >
              Matches awaiting your move ({matchesAwaitingActionCount})
            </button>
          )}
        </div>
      )}
    </div>
  );
}
