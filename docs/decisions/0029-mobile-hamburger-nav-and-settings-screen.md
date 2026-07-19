# ADR-0029: Mobile hamburger nav toggle, and a consolidated Settings screen replacing standalone header links

- **Status:** Accepted
- **Date:** 2026-07-19
- **Related requirements:** REQ-712, REQ-713, REQ-504, REQ-710
- **Related components:** None (frontend-only; no `XGArcade.Core`/game/backend component boundary changed)

## Context

The authenticated header nav in `frontend/src/App.tsx` had grown to four
top-level items: "Leaderboard," "Delete account" (REQ-710, S-039),
"Admin" (REQ-504, S-026, admin-only), and "Log out." This had already
overflowed once before — S-029 fixed it by trimming duplicate items (a
"Games" link folded into the app title) — but it regressed as REQ-504 and
REQ-710 each independently added their own standalone top-level link
afterward, with no mechanism to keep the header row from wrapping/
overflowing as more items accumulate over time. A direct user report
("the menu bar gets bloated.. and goes off screen in mobile") confirmed
this in practice.

The user also asked for a single "Profile menu item or settings" that
would hold "delete account," and, for an admin, the admin controls too —
a deliberate reversal of `docs/design-document.md` SCREEN-05's prior
"deliberately not a general profile/settings page (none exists in Tier
0)" note. That note documented a real prior decision, not an oversight, so
overturning it needed its own record rather than a silent edit.

## Decision

1. Below a 480px viewport (reusing the existing "narrow phone" breakpoint
   value already established by `Grid.css`'s header-label wrapping, not a
   new threshold), the header nav collapses behind a single toggle button
   (`HeaderNav.tsx`/`.css`, REQ-712) — a real, focusable, keyboard-operable
   `<button>` exposing `aria-expanded`, matching REQ-204's existing
   accessible-disclosure pattern. At/above 480px, the nav renders exactly
   as before (a plain horizontal row) — this is a mobile-only layout
   change, not a universal pattern change. The mechanism is CSS-only (no
   JS viewport detection), matching this app's existing responsive
   approach elsewhere.
2. The standalone "Delete account" and (admin-only) "Admin" top-level
   links are replaced by a single "Settings" nav entry (`SettingsScreen
   .tsx`/`.css`, REQ-713). Selecting it opens a screen that hosts REQ-710's
   delete-account flow completely unchanged, plus — only for an admin,
   same `isAdmin` check REQ-504 already used — a link that navigates to
   the existing, unchanged `AdminScreen` (REQ-504). This is a link out,
   never admin controls embedded inline on the Settings screen itself. A
   non-admin sees no trace of the link, matching REQ-504's existing
   guarantee.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Trim nav items again (repeat S-029's fix) | No new component, smallest possible change | Doesn't address the actual mechanism — the header will overflow again the next time any REQ adds one more top-level item, exactly as happened after S-029 | Not chosen: fixes the symptom again, not the recurring cause |
| Universal hamburger (collapse at every viewport width, including desktop) | One consistent nav pattern to maintain; simpler code (no breakpoint-conditional row) | Changes the desktop experience for no desktop-side problem — desktop never overflowed | Not chosen: explicit user direction to keep desktop's existing horizontal row and only collapse on mobile, where the actual problem is |
| Embed admin controls (round control, data review, user deletion) directly inside the Settings screen | One fewer navigation hop for an admin | Folds two independently-scoped screens (REQ-504/505/506's `AdminScreen` and REQ-713's Settings) into one, duplicating or entangling logic that currently has its own screen, its own tests, and its own non-Production gating | Not chosen: explicit user direction to link out to the existing `AdminScreen` unchanged, reusing REQ-504/505/506 as-is rather than merging screens |
| Reuse the existing 960px desktop-cap breakpoint for the nav collapse, instead of 480px | Already a token value used elsewhere in the app (`.app`'s desktop cap) | Demarcates an unrelated concern ("wide desktop gets more breathing room," not overflow prevention); would collapse the nav behind a toggle on ordinary tablets/small laptops where the three-item row already fits comfortably | Not chosen: 480px is the value this codebase already treats as "narrow phone" specifically, and the overflow problem here is the same class of problem that value was chosen for elsewhere |

## Consequences

- Positive: the header nav is now structurally robust to future growth —
  a future REQ that needs another authenticated-user-only nav entry adds
  it to the collapsed list rather than risking another overflow
  regression, without needing a repeat of this decision.
- Positive: REQ-504's and REQ-710's own screens, tests, authorization
  checks, and non-Production gating are completely unchanged — only their
  entry point moved. No backend surface, and no `XGArcade.Core`/game
  boundary, was touched by this change.
- Negative / trade-off accepted: reaching "Delete account" or "Admin" now
  takes one more click/tap (nav → Settings → the desired action/link) than
  the previous flat top-level links did. Accepted as the direct cost of
  consolidating two links into one nav entry, and considered acceptable
  given both are already deliberately low-frequency, not primary-flow
  actions.
- Negative / trade-off accepted: `docs/design-document.md` SCREEN-05's
  "no general profile/settings page exists in Tier 0" note is now
  outdated — status notes were added to SCREEN-04/SCREEN-05 pointing at
  this ADR and at the new SCREEN-07/SCREEN-08 entries, rather than
  rewriting the historical note itself.
- Follow-up: if a genuine profile/account-fields page is ever needed
  (beyond delete-account and the admin link), SCREEN-08 already exists as
  the natural place to extend rather than adding a third nav entry — worth
  checking this ADR and REQ-713 first before doing so, since REQ-713 is
  explicit that Settings intentionally hosts nothing else today.

## For AI agents

If code you are about to write would contradict this decision, stop and
flag it rather than silently working around it — either the decision needs
a new ADR that supersedes this one, or the approach needs to change.
