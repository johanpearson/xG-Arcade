# ADR-0034: Dark mode is an explicit System/Light/Dark toggle, stored in localStorage

- **Status:** Accepted
- **Date:** 2026-07-20
- **Related requirements:** REQ-716
- **Related components:** CONT-01 (Web Frontend)

## Context

REQ-716 asks for a selectable color theme — the player's own words: "I want to
**choose** a different color theme... to match my own preference." `design-document.md`
had left this as an open question since v1 (§7): whether a dark theme is ever offered,
and if so, whether it follows `prefers-color-scheme` automatically or is an explicit,
player-controlled setting.

Two real options exist for the mechanism:
1. Automatic-only: honor `prefers-color-scheme: dark`, no in-app control at all.
2. An explicit toggle (System/Light/Dark), overriding the OS preference when the
   player picks Light or Dark specifically.

And, if a toggle is chosen, two real options for where the choice lives:
1. `localStorage`, device-local, read at app startup — the same pattern ADR-0033
   already established for the refresh token, and used by the existing access-token
   storage before that.
2. A `User`-level column, synced through the backend so the preference follows a
   player across devices.

This is a Tier 0, solo/small-scale product (`MVP-SCOPE.md`) — the same scale
consideration ADR-0033 already weighed.

## Decision

**Mechanism:** an explicit three-state toggle (System / Light / Dark) on
`SettingsScreen.tsx`, not an automatic-only `prefers-color-scheme` switch. REQ-716's
own request text asks to *choose* a theme, which an automatic-only approach doesn't
satisfy — a player has no way to pin Dark if their OS is set to Light (or vice versa).
"System" is the default so a player who never opens Settings still gets a
sensible, lighting-condition-appropriate result; the toggle exists for the player who
wants to override that, not as the only path to a dark UI at all.

**Storage:** `localStorage`, under a new key, read/applied at startup the same way
`App.tsx` already reads `ACCESS_TOKEN_STORAGE_KEY` — device-local, no `User`-level
column, no new backend surface. "System" resolves `prefers-color-scheme` at load and
reactively via its `change` event; "Light"/"Dark" pin the theme regardless of the OS
setting.

## Alternatives considered

| Option | Pros | Cons | Why not chosen |
|---|---|---|---|
| Automatic-only (`prefers-color-scheme`, no toggle) | Zero new state to persist; simplest possible implementation | Doesn't satisfy REQ-716's actual request — a player can't choose a theme independent of their OS setting | REQ-716 explicitly asks to *choose*, not just "support" a theme |
| Explicit toggle, `localStorage` (chosen) | Matches ADR-0033's existing device-local-preference pattern; no new backend/API surface; one `localStorage` key | Preference doesn't follow a player to a new device | Low-stakes, cosmetic preference — not worth a backend change at this scale; revisit if actually requested |
| Explicit toggle, `User`-level column (account-synced) | Preference follows the player across devices | New migration, new endpoint, new sync logic, for a preference nothing has asked to be cross-device yet | More new surface than this stage's needs justify, same reasoning ADR-0033 used for token storage |

## Consequences

- Positive: no new backend surface at all — this is a pure frontend change (a
  `localStorage` key, a `data-theme` attribute on `<html>`, a Settings control) on top
  of the token values `design-document.md` §2's new "Dark theme" subsection already
  specifies and contrast-verifies.
- Negative / trade-offs accepted: the preference is device-local — a player who picks
  Dark on their phone sees System/Light-default on a new device or browser until they
  set it again there too. Accepted deliberately, not an oversight.
- Follow-up: revisit device-local storage if a player ever asks for the preference to
  follow them across devices — at that point a `User`-level column becomes easy to
  justify against a real, observed request, same "small now, revisit on evidence"
  pattern ADR-0016/0019/0021/ADR-0033 already established.

## For AI agents

Dark mode's mechanism (explicit toggle, not automatic-only) and storage (`localStorage`,
not a `User` column) are both deliberate choices, not oversights — do not "simplify" to
automatic-only detection, and do not add a `User`-level theme column without a new ADR
superseding this one. The token values themselves live in `design-document.md` §2's
"Dark theme" subsection, already contrast-verified — implementation should consume
those values directly rather than re-deriving them.
