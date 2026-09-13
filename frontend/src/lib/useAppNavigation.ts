import { useCallback, useEffect, useState } from 'react';
import { ACCESS_TOKEN_STORAGE_KEY } from './authStorage';

// S-235: extracted out of App.tsx (which mixed this self-contained
// hash-routing mechanism in with per-screen seed state and the render tree
// itself) — see docs/backlog.md S-235 for why. Pure extraction, no
// behavior change: every REQ/ADR comment below is unchanged from its
// App.tsx origin except where a comment referenced "this file"/App.tsx-
// local code that moved here too, which has been reworded to still make
// sense from this file. Mirrors useSession.ts's own S-158 extraction: same
// file placement (frontend/src/lib/), same "export a result object from a
// single top-of-component hook call" shape, same comment style.
//
// What this hook deliberately does NOT own, and why:
//
// - `showAuthScreen` — App.tsx's own state, not this hook's. It gates
//   whether the unauthenticated splash screen or AuthScreen is shown
//   (REQ-719); this hook only knows about the authenticated `Screen` union
//   below. Folding it in here would make this hook responsible for a
//   decision (splash vs. auth form) that has nothing to do with hash
//   routing.
//
// - `useSession` itself — this hook's initializer and effects read
//   `ACCESS_TOKEN_STORAGE_KEY` directly from localStorage (the same "is
//   there a stored token" signal useSession's own initializer uses), but
//   never calls into useSession or receives it as a dependency. Both hooks
//   import that constant from the neutral `authStorage.ts` module rather
//   than one importing it from the other, so there's no
//   useAppNavigation -> useSession (or reverse) module dependency at all.
//   The dependency direction is, and must stay, App.tsx -> useSession and
//   App.tsx -> useAppNavigation, never useAppNavigation -> useSession:
//   App.tsx's own `handleLoggedOut` is passed *into* useSession, and that
//   callback needs both this hook's `resetToLoggedOut` (below) and
//   App.tsx's own `setShowAuthScreen` — a combination only App.tsx can
//   make, so `handleLoggedOut` itself stays there rather than moving into
//   either hook.
//
// - The four screens' own seed state (`leaderboardInitial`, `statsTarget`
//   + `statsReturnScreen`, `friendsInitialTab`) — deliberately left in
//   App.tsx rather than folded in here. Considered and rejected: unlike
//   useRoundFetch.ts's `checkRoundStillLive` (S-169), which was folded in
//   because it reused the exact same fetch-and-compare code the mount
//   effect already had, these four seeds share no common type or consumer
//   — they're four unrelated shapes (`LeaderboardRoundTarget`,
//   `{userId,displayName}` + a `Screen`, `FriendsTabKey`) each read by a
//   single, different screen component's own props. Generalizing them into
//   this hook would need either an untyped `unknown` bucket per screen or
//   four separate type parameters, which doesn't remove any duplication —
//   each was already exactly one `useState` line, with no repeated
//   boilerplate around it to eliminate. The actual duplicated *shape* the
//   S-235 story flagged is the "seed something, then navigate" wrapper
//   function, not the seed state's storage location — `seedAndNavigate`
//   below eliminates that duplication directly, without requiring the
//   seed state to move anywhere. (`handleOpenOwnStats`, one of the four
//   wrappers, also needs `currentUser` from `useSession` to build its seed
//   — another concrete reason not to pull it in here, since that would
//   give this hook a reason to know about session data it otherwise never
//   touches.)
//
// REQ-303 (S-021): 'game-select' is the landing screen shown after login,
// before any game's own screen — see docs/backlog.md S-021. 'settings'
// (REQ-713, superseding S-039's standalone 'delete-account' screen) is
// reachable only from the header's "Settings" nav entry, never a
// destination anything else navigates to — it hosts the unchanged
// delete-account flow plus, for admins only, a link onward to 'admin'.
// 'admin' (REQ-504, S-026) is in turn reachable only from that
// Settings-screen link, never a default destination. 'leagues'
// (REQ-402/403) is reachable from the header's "Leagues" nav entry —
// create/join a custom league and see which ones the player belongs to; no
// per-league leaderboard yet (REQ-404's separate, tracked follow-up work).
// 'path' (S-085/SCREEN-09) is xG Path's own destination, reached the same
// way 'grid' is — GameSelectScreen's second tile or HeaderNav's "Games" →
// "xG Path" entry. It renders only a placeholder today: the real
// clue-reveal UI (SCREEN-10) is S-086's separate, not-yet-built work.
// 'predict' (REQ-1301/1302/1303/1306, SCREEN-14) is xG Predict's own
// destination — reached the same way 'grid'/'path' are, via
// GameSelectScreen's third tile or HeaderNav's "Games" → "xG Predict"
// entry (added same-story, closing the gap the SCREEN-14 status note had
// flagged as a scope boundary).
// 'admin-suggestions' (REQ-509/REQ-510, S-090, ADR-0053) is
// SuggestionsScreen's own destination — reachable only via a link inside
// AdminScreen itself, one hop further than 'admin', mirroring how 'admin'
// is in turn only reachable from 'settings'. Never a default destination
// and never given its own top-level nav entry, per ADR-0053's "a new,
// separate screen... reached the same gated way" framing.
// 'admin-connect-dispute-suggestions' (REQ-1414, ADR-0053/ADR-0109) is
// ConnectDisputeSuggestionsScreen's own destination — same "reachable only
// via a link inside AdminScreen, never a top-level nav entry" shape as
// 'admin-suggestions' immediately above, for the same ADR-0053 reason (a
// new, separate, read-only admin surface, never folded into the existing
// one).
// 'stats' (REQ-411, S-179, SCREEN-13) is UserStatsScreen's own destination —
// reachable from Settings' "My stats" link (own stats) or from any
// leaderboard row's display name (another player's stats), never given its
// own top-level nav entry either, same "reached only from an existing
// screen, not HeaderNav" precedent 'admin'/'admin-suggestions' already set
// (see REQ-712/713's own header-overflow rationale, restated on
// SettingsScreen's `onOpenStats` prop).
// 'friends' (REQ-1401/1402/1403, S-217, SCREEN-15) is FriendsScreen's own
// destination — reachable from the header's new "Friends" nav entry
// (REQ-1411's own notification badge lives on that entry, not this Screen
// value itself), optionally from UserStatsScreen's "Respond in Friends &
// Challenges" link (onOpenFriends) when the viewed player already sent the
// viewer a pending friend request, and (REQ-1415) from
// 'xg-connect-entry's two choice buttons, which seed `friendsInitialTab`
// first (see App.tsx's handleOpenFriendsTab, reused as-is for this new
// caller). S-218 (SCREEN-16) added a fourth "Matches" tab inside
// FriendsScreen itself (not a new top-level Screen/hash route) — see
// FriendsScreen.tsx's own comment on why the match/gameplay drill-down is
// component-local state, not App-level navigation.
// 'xg-connect-entry' (REQ-1415, SCREEN-17) is ConnectEntryScreen's own
// destination — reached via GameSelectScreen's fourth tile or HeaderNav's
// "Games" -> "xG Connect" entry, the same way 'grid'/'path'/'predict' are
// reached via their own tile/nav-entry. Deliberately the one game tile that
// does NOT go straight into that game's own play screen (see
// GameSelectScreen.tsx's own doc comment for why) — instead it shows two
// choices ("Challenge a friend" / "Challenge random player"), each of which
// hands off to 'friends' with a pre-seeded tab, per REQ-1415's own
// "don't restate the underlying flows" requirement.
// 'higher-lower' (REQ-1504/1505, SCREEN-18, S-228) is HigherLowerScreen's
// own destination — reached the same way 'grid'/'path'/'predict' are, via
// GameSelectScreen's fifth tile or HeaderNav's "Games" -> "xG Higher/Lower"
// entry (added same-story, so the SCREEN-14-style "tile wired, nav entry
// flagged as a gap" split never happens here).
export type Screen =
  | 'game-select'
  | 'grid'
  | 'path'
  | 'predict'
  | 'xg-connect-entry'
  | 'higher-lower'
  | 'leaderboard'
  | 'leagues'
  | 'friends'
  | 'settings'
  | 'admin'
  | 'admin-suggestions'
  | 'admin-connect-dispute-suggestions'
  | 'stats';

// REQ-721/ADR-0039: hash-based, hand-rolled URL-per-screen mapping — see
// that ADR for why (hash not path, no router library, no popstate/
// hashchange listener; back/forward is explicitly out of scope). This is
// the entire mechanism: one lookup table, read once on mount below, written
// at every navigateTo() call site.
const SCREEN_HASHES: Record<Screen, string> = {
  'game-select': '#/game-select',
  grid: '#/grid',
  path: '#/path',
  predict: '#/predict',
  'xg-connect-entry': '#/xg-connect',
  'higher-lower': '#/higher-lower',
  leaderboard: '#/leaderboard',
  leagues: '#/leagues',
  friends: '#/friends',
  settings: '#/settings',
  admin: '#/admin',
  'admin-suggestions': '#/admin/suggestions',
  'admin-connect-dispute-suggestions': '#/admin/connect-dispute-suggestions',
  stats: '#/stats',
};

const HASH_TO_SCREEN: Partial<Record<string, Screen>> = Object.fromEntries(
  Object.entries(SCREEN_HASHES).map(([screenName, hash]) => [hash, screenName as Screen]),
);

function screenForHash(hash: string): Screen | null {
  return HASH_TO_SCREEN[hash] ?? null;
}

export interface UseAppNavigationResult {
  screen: Screen;
  // REQ-721/ADR-0039: the one place `screen` state and `location.hash`
  // change together — every in-app navigation calls this instead of
  // setting `screen` directly. `resetToLoggedOut` below is the deliberate
  // exception: it clears the hash rather than writing 'game-select's,
  // since the screen shown right after logout is the splash screen, not
  // game-select (see that function's own comment).
  navigateTo: (next: Screen) => void;
  // S-235: the generic "seed something, then navigate" helper the four
  // per-screen handlers in App.tsx (handleViewRoundLeaderboard,
  // handleOpenOwnStats/handleSelectPlayerStats, handleOpenFriendsTab) each
  // become a thin wrapper around — `seed` runs first (exactly the existing
  // "setXInitial(...); navigateTo(...)" order every one of those handlers
  // already used), then `navigateTo` fires. Deliberately generic over the
  // *seed's* shape (a plain callback, not a typed value) rather than over
  // a shared seed-state type — see this file's own top-of-file comment for
  // why the seed state itself stays in App.tsx instead of moving here.
  seedAndNavigate: (next: Screen, seed: () => void) => void;
  // REQ-719/REQ-721/ADR-0039: resets `screen` back to 'game-select' and
  // clears the URL hash — the navigation half of what App.tsx's
  // `handleLoggedOut` does on every path that ends in a logout (an
  // explicit "Log out" click, account deletion, a failed/absent
  // silent-refresh outcome). The other half, `setShowAuthScreen(false)`,
  // isn't navigation (see this file's own top-of-file comment) and stays
  // in App.tsx, which calls both together. The hash is cleared rather than
  // set to 'game-select's, since the screen shown next is the splash
  // screen, which isn't part of the Screen/SCREEN_HASHES mapping at all —
  // a lingering authenticated screen's hash would otherwise misdescribe
  // what's on screen.
  resetToLoggedOut: () => void;
}

// REQ-715/REQ-718/REQ-719/REQ-721: the self-contained hash-routing
// mechanism — which authenticated `Screen` is showing, keeping
// `location.hash` in sync with it, and the generic seed-then-navigate
// wrapper shape every seeded destination reuses. Mounted once, at the top
// of App(), the same way useSession/useThemePreference are.
export function useAppNavigation(): UseAppNavigationResult {
  const [screen, setScreen] = useState<Screen>(() => {
    // REQ-721/ADR-0039: URL restoration applies only to a reload of an
    // already-authenticated, already-valid session — never to an
    // unauthenticated visitor (must never bypass REQ-719's splash gate) and
    // never to a fresh login/signup (App.tsx's AuthScreen onAuthenticated
    // handler always navigates to 'game-select' unconditionally, regardless
    // of the hash).
    // A stored access token at mount is the same "authenticated" signal the
    // rest of App already renders on optimistically, with no separate
    // loading state — if that token later turns out to be invalid, the
    // existing 401/silent-refresh-failure path calls handleLogout(), which
    // resets both `screen` and the hash (via resetToLoggedOut below)
    // regardless of what was read here, so an authenticated screen restored
    // from a stale URL can never outlive that check.
    const hasStoredAccessToken = Boolean(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY));
    if (!hasStoredAccessToken) return 'game-select';
    return screenForHash(window.location.hash) ?? 'game-select';
  });

  // REQ-721/ADR-0039: keeps location.hash matching `screen` from the very
  // first render, not only from the next explicit navigateTo() call —
  // covers both "no hash was present" and "the hash present didn't map to
  // a real Screen" (the initializer above already fell back to
  // 'game-select' in both cases). Deliberately mount-only (empty dep
  // array): `screen`'s value here is whatever the lazy initializer already
  // computed once at mount, and every later change already goes through
  // navigateTo, which writes the hash itself. Gated the same way the
  // initializer is — never runs for an unauthenticated visitor, so it can
  // never write an authenticated screen's hash while the splash screen (not
  // part of the Screen/SCREEN_HASHES mapping) is what's actually showing.
  useEffect(() => {
    if (window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY)) {
      window.location.hash = SCREEN_HASHES[screen];
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const navigateTo = useCallback((next: Screen) => {
    setScreen(next);
    window.location.hash = SCREEN_HASHES[next];
  }, []);

  const seedAndNavigate = useCallback(
    (next: Screen, seed: () => void) => {
      seed();
      navigateTo(next);
    },
    [navigateTo],
  );

  // useCallback (stable identity across renders): App.tsx's own
  // `handleLoggedOut` depends on this, and useSession's `handleLogout` in
  // turn depends on `handleLoggedOut` — an unstable identity here would
  // give `handleLoggedOut` a new identity on every App render, the same
  // re-render-cascade risk useSession.ts's own comment on its
  // `handleLoggedOut` parameter warns about.
  const resetToLoggedOut = useCallback(() => {
    setScreen('game-select');
    window.location.hash = '';
  }, []);

  return { screen, navigateTo, seedAndNavigate, resetToLoggedOut };
}
