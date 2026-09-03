import { useCallback, useEffect, useState } from 'react';
import './App.css';
import { AdminScreen } from './admin/AdminScreen';
import { SuggestionsScreen } from './admin/SuggestionsScreen';
import { AuthScreen } from './auth/AuthScreen';
import { AnnouncementBanner } from './components/AnnouncementBanner';
import { Logo } from './components/Logo';
import { GameSelectScreen, XG_GRID_GAME_KEY, XG_PATH_GAME_KEY, XG_PREDICT_GAME_KEY } from './games/GameSelectScreen';
import { GridScreen } from './grid/GridScreen';
import { IncidentReportDialog } from './incidents/IncidentReportDialog';
import { GuestLogoutConfirm } from './nav/GuestLogoutConfirm';
import { HeaderNav } from './nav/HeaderNav';
import { LeaderboardScreen, type LeaderboardRoundTarget } from './leaderboard/LeaderboardScreen';
import { LeaguesScreen } from './leagues/LeaguesScreen';
import { PathScreen } from './path/PathScreen';
import { PredictScreen } from './predict/PredictScreen';
import { SettingsScreen } from './settings/SettingsScreen';
import { SplashScreen } from './splash/SplashScreen';
import { FriendsScreen } from './social/FriendsScreen';
import { UserStatsScreen } from './users/UserStatsScreen';
import { GUEST_EXPIRY_COPY } from './lib/guestExpiryCopy';
import { useThemePreference } from './lib/theme';
import { useNotificationSummary } from './lib/useNotificationSummary';
import { ACCESS_TOKEN_STORAGE_KEY, useSession } from './lib/useSession';

type HealthState =
  | { phase: 'loading' }
  | { phase: 'healthy'; status: string }
  | { phase: 'error'; message: string };

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';

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
// value itself) and, optionally, from UserStatsScreen's "Respond in
// Friends & Challenges" link (onOpenFriends) when the viewed player already
// sent the viewer a pending friend request. S-218 (SCREEN-16) added a
// fourth "Matches" tab inside FriendsScreen itself (not a new top-level
// Screen/hash route) — see FriendsScreen.tsx's own comment on why the
// match/gameplay drill-down is component-local state, not App-level
// navigation.
type Screen =
  | 'game-select'
  | 'grid'
  | 'path'
  | 'predict'
  | 'leaderboard'
  | 'leagues'
  | 'friends'
  | 'settings'
  | 'admin'
  | 'admin-suggestions'
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
  leaderboard: '#/leaderboard',
  leagues: '#/leagues',
  friends: '#/friends',
  settings: '#/settings',
  admin: '#/admin',
  'admin-suggestions': '#/admin/suggestions',
  stats: '#/stats',
};

const HASH_TO_SCREEN: Partial<Record<string, Screen>> = Object.fromEntries(
  Object.entries(SCREEN_HASHES).map(([screenName, hash]) => [hash, screenName as Screen]),
);

function screenForHash(hash: string): Screen | null {
  return HASH_TO_SCREEN[hash] ?? null;
}

// REQ-718 UI addendum (rule 5, 2026-08-25): the guest banner's disclosure
// toggle icon — a small filled caret, decorative on its own (the wrapping
// <button> carries the real accessible name via aria-label, same split as
// SettingsScreen.tsx's EditPencilIcon). Right-pointing while collapsed
// (points toward the hidden content), down-pointing once revealed — the
// glyph itself swaps on click rather than animating/rotating, matching this
// banner's existing "no new motion" constraint (design-document.md).
function GuestBannerChevronIcon({ open }: { open: boolean }) {
  return (
    <svg className="app__guest-banner-toggle-icon" viewBox="0 0 24 24" focusable="false" aria-hidden="true">
      <path d={open ? 'M6 9l6 6 6-6H6z' : 'M9 6l6 6-6 6V6z'} fill="currentColor" />
    </svg>
  );
}

function App() {
  const [health, setHealth] = useState<HealthState>({ phase: 'loading' });
  const [screen, setScreen] = useState<Screen>(() => {
    // REQ-721/ADR-0039: URL restoration applies only to a reload of an
    // already-authenticated, already-valid session — never to an
    // unauthenticated visitor (must never bypass REQ-719's splash gate) and
    // never to a fresh login/signup (the AuthScreen onAuthenticated handler
    // below always navigates to 'game-select' unconditionally, regardless of
    // the hash).
    // A stored access token at mount is the same "authenticated" signal the
    // rest of this component already renders on optimistically, with no
    // separate loading state — if that token later turns out to be
    // invalid, the existing 401/silent-refresh-failure path calls
    // handleLogout(), which resets both `screen` and the hash regardless of
    // what was read here, so an authenticated screen restored from a stale
    // URL can never outlive that check.
    const hasStoredAccessToken = Boolean(window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY));
    if (!hasStoredAccessToken) return 'game-select';
    return screenForHash(window.location.hash) ?? 'game-select';
  });
  // REQ-719: the unauthenticated splash/landing screen is what renders
  // whenever there's no accessToken, until this flips true — starts false
  // on every mount (no persisted "already seen it" flag, deliberately —
  // see requirements-document.md REQ-719 §5) and is reset back to false by
  // the useSession() onLoggedOut callback below, which is also what fires
  // on account deletion and a failed/absent silent-refresh outcome (see
  // useSession's own handleLogout for why) — so every one of those returns
  // to the splash screen, never straight to AuthScreen.
  const [showAuthScreen, setShowAuthScreen] = useState(false);
  // S-158: the auth-session lifecycle (access/refresh token, currentUser,
  // silent refresh, logout) lives in useSession (frontend/src/lib/
  // useSession.ts) — this component only supplies the routing/dialog
  // reaction to a logout: resetting `screen` back to 'game-select', hiding
  // AuthScreen (back to the splash screen — see showAuthScreen's own
  // declaration above for why), and clearing the URL hash (REQ-721/
  // ADR-0039: the screen shown next is the splash screen, not part of the
  // Screen/SCREEN_HASHES mapping, so a lingering authenticated screen's hash
  // would otherwise misdescribe what's on screen). This same handoff covers
  // every path that ends in a logout — an explicit "Log out" click, account
  // deletion, and a failed/absent silent-refresh outcome — since all three
  // funnel through useSession's single handleLogout.
  // useCallback (stable identity across renders, not just useState setters'
  // own already-stable identities) matters here: useSession's handleLogout
  // depends on this callback, and the fetchMe effect in turn depends on
  // handleLogout — an unmemoized inline function here would give
  // handleLogout a new identity on every App render, re-running that effect
  // (and re-fetching /auth/me, clobbering any local currentUser update such
  // as SettingsScreen's onAccountClaimed) far more often than intended.
  const handleLoggedOut = useCallback(() => {
    setScreen('game-select');
    setShowAuthScreen(false);
    window.location.hash = '';
  }, []);
  const { accessToken, currentUser, setCurrentUser, isGuest, handleAuthenticated, handleLogout } =
    useSession(handleLoggedOut);
  // REQ-718 UI addendum (rule 4, 2026-08-01): true only while the
  // confirmation prompt gating a guest's "Log out" click is open — see
  // handleLogoutClick below. Never true for a non-guest account, since
  // that branch calls handleLogout directly and never sets this.
  const [guestLogoutConfirmOpen, setGuestLogoutConfirmOpen] = useState(false);
  // REQ-718 UI addendum (rule 5, 2026-08-25): gates whether the guest
  // banner's expiry sentence is shown — collapsed by default so the banner
  // stays a single line on narrow/mobile viewports (see App.css's
  // .app__guest-banner-expiry rule). Never persisted across sessions;
  // resets to collapsed on every fresh mount, same as every other
  // disclosure toggle in this codebase (HeaderNav's `open`/`gamesOpen`).
  const [guestExpiryOpen, setGuestExpiryOpen] = useState(false);
  // REQ-903/ADR-0064: gates IncidentReportDialog, opened from the footer's
  // "Report a problem" button — deliberately state here (not inside any
  // one screen component) so it's reachable regardless of which screen is
  // currently showing, same reasoning as the theme preference below.
  const [incidentReportOpen, setIncidentReportOpen] = useState(false);
  // REQ-716/ADR-0034: mounted here (not inside SettingsScreen) so the
  // "system" preference's reactive prefers-color-scheme listener stays
  // active regardless of which screen is showing, not only while Settings
  // itself is open. main.tsx's applyStoredThemePreference() already applied
  // the same value before this component ever mounted, so this isn't the
  // first paint of the theme — it's what keeps it in sync after that.
  const { preference: themePreference, setPreference: setThemePreference } = useThemePreference();
  // REQ-1411/S-217: mounted here (not inside FriendsScreen/HeaderNav) so
  // HeaderNav's "Friends" badge stays current regardless of which screen is
  // showing, the same "regardless of which screen is showing" placement
  // themePreference/incidentReportOpen above already use.
  const notificationSummary = useNotificationSummary(accessToken, handleLogout);
  // REQ-1210/ADR-0083: seeds LeaderboardScreen's own `initial*` props the
  // one time it's set here (by handleViewRoundLeaderboard below, called
  // from GridScreen/PathScreen's round-completion banner) — read only at
  // LeaderboardScreen's own mount (its useState initializer), so this is
  // safe to leave set afterward without re-triggering anything on that
  // already-mounted instance. Explicitly cleared by the header nav's own
  // "Leaderboard" entry point (onSelectLeaderboard below) so a later,
  // ordinary manual visit never silently re-jumps to a stale round.
  const [leaderboardInitial, setLeaderboardInitial] = useState<LeaderboardRoundTarget | null>(null);
  // REQ-411/ADR-0083-style seed (S-179): which player's stats 'stats'
  // should show and which screen "Back" should return to — same in-memory,
  // read-once-at-navigation pattern `leaderboardInitial` above already
  // establishes (ADR-0039: no router library, no URL param for this).
  // `statsTarget` is set by both entry points below (Settings' "My stats"
  // and a leaderboard row's display name) immediately before navigating to
  // 'stats', so UserStatsScreen is never mounted without a target — the
  // `null` default only ever exists before either entry point has fired
  // once, i.e. it's never actually read while `screen === 'stats'`.
  const [statsTarget, setStatsTarget] = useState<{ userId: string; displayName: string } | null>(null);
  const [statsReturnScreen, setStatsReturnScreen] = useState<Screen>('game-select');

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

  useEffect(() => {
    let cancelled = false

    fetch(`${API_BASE_URL}/health`)
      .then((response) => {
        if (!response.ok) {
          throw new Error(`API responded with ${response.status}`)
        }
        return response.json() as Promise<{ status: string }>
      })
      .then((body) => {
        if (!cancelled) setHealth({ phase: 'healthy', status: body.status })
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          const message = error instanceof Error ? error.message : 'Unknown error'
          setHealth({ phase: 'error', message })
        }
      })

    return () => {
      cancelled = true
    }
  }, [])

  // REQ-721/ADR-0039: the one place `screen` state and `location.hash`
  // change together — every in-app navigation below calls this instead of
  // setScreen directly. handleLogout is the deliberate exception: it clears
  // the hash rather than writing 'game-select's, since the screen shown
  // right after logout is the splash screen, not game-select (see its own
  // comment).
  function navigateTo(next: Screen) {
    setScreen(next);
    window.location.hash = SCREEN_HASHES[next];
  }

  // REQ-1210/ADR-0083: GridScreen/PathScreen's round-completion banner
  // calls this with the specific round+game+scope it already resolved
  // (see either screen's own handleViewCompletedRoundLeaderboard) — this
  // seeds `leaderboardInitial` and navigates in the same update, so the
  // freshly-mounted LeaderboardScreen (grid/path and leaderboard are
  // mutually exclusive Screen branches, so this is always a real
  // mount, never a same-instance prop update) reads the target on its own
  // first render.
  function handleViewRoundLeaderboard(target: LeaderboardRoundTarget) {
    setLeaderboardInitial(target);
    navigateTo('leaderboard');
  }

  // REQ-411 (S-179): Settings' "My stats" link — seeds `statsTarget` with
  // the current account's own id/name (the only source App.tsx has for
  // "own stats"; UserStatsScreen itself has no own-vs-other concept, see
  // its own doc comment) and remembers 'settings' as where "Back" should
  // return to.
  function handleOpenOwnStats() {
    if (!currentUser) return;
    setStatsTarget({ userId: currentUser.id, displayName: currentUser.displayName });
    setStatsReturnScreen('settings');
    navigateTo('stats');
  }

  // REQ-411 (S-179): a leaderboard row's display name — seeds `statsTarget`
  // with whichever player's row was selected and remembers 'leaderboard' as
  // where "Back" should return to.
  function handleSelectPlayerStats(userId: string, displayName: string) {
    setStatsTarget({ userId, displayName });
    setStatsReturnScreen('leaderboard');
    navigateTo('stats');
  }

  // REQ-718 UI addendum (rule 4, 2026-08-01): the actual onClick handler
  // wired to HeaderNav's "Log out" button — gates *when* handleLogout above
  // fires, without changing anything about handleLogout itself. A guest
  // account (isGuest) only opens the confirmation prompt here; the prompt's
  // own onConfirm (below, in the render) is what actually calls
  // handleLogout. A non-guest account calls handleLogout directly, exactly
  // as before this addition — same call, same timing, no prompt.
  function handleLogoutClick() {
    if (isGuest) {
      setGuestLogoutConfirmOpen(true);
      return;
    }
    handleLogout();
  }

  return (
    <div className="app">
      {/* REQ-511: rendered above <header>, outside every auth-gated branch
          below — the one place in this tree that renders identically
          whether the visitor is logged in, a guest, or fully logged out
          with no session at all (splash/auth screen). Fetches its own
          data independently and renders nothing while inactive/loading, so
          it never affects any of the loading/auth logic elsewhere in this
          component. */}
      <AnnouncementBanner />
      <header className="app__header">
        {/* REQ-720: "xG Arcade" continues to route to the full
            landing/picker screen (GameSelectScreen) exactly as before —
            kept deliberately alongside the header nav's new "Games"
            quick-jump entry below, not replaced by it (see that
            requirement's own explicit non-duplication note: this title is
            the room-to-grow landing screen, "Games" is a same-place
            shortcut).

            2026-07-26: the plain-text title is now the shared `Logo`
            (frontend/src/components/Logo.tsx, same mark SplashScreen
            uses) — it sizes from the header's existing 22px
            `.app__title` font-size, no separate prop needed. Its
            accessible name is still "xG Arcade" either way ("x"/"G"/
            "Arcade" are all real text), so every existing
            `getByRole('button'|'heading', { name: 'xG Arcade' })` query
            elsewhere in this file/tests is unaffected. */}
        {accessToken ? (
          <button type="button" className="app__title app__title--link" onClick={() => navigateTo('game-select')}>
            <Logo />
          </button>
        ) : (
          <h1 className="app__title">
            <Logo />
          </h1>
        )}
        {/* REQ-712/REQ-713/REQ-720: the header's only nav surface —
            collapses behind a single toggle below the mobile breakpoint
            (HeaderNav.css), renders as the same horizontal row as before
            at/above it. "Settings" (REQ-713) replaces the previously
            separate "Delete account" and admin-only "Admin" top-level
            links; the admin gate itself now lives in SettingsScreen, not
            here — currentUser?.isAdmin is passed straight through, same
            source of truth REQ-504 already used. "Games" (REQ-720,
            extended by S-085) is a non-navigating disclosure listing one
            entry per game xG Arcade currently hosts; isGridCurrent/
            isPathCurrent drive each entry's own aria-current the same way
            the other flags already do. */}
        {accessToken && (
          <HeaderNav
            isLeaderboardCurrent={screen === 'leaderboard'}
            isLeaguesCurrent={screen === 'leagues'}
            isFriendsCurrent={screen === 'friends'}
            isSettingsCurrent={screen === 'settings'}
            isGridCurrent={screen === 'grid'}
            isPathCurrent={screen === 'path'}
            isPredictCurrent={screen === 'predict'}
            onSelectLeaderboard={() => {
              // REQ-1210/ADR-0083: a normal, explicit nav-menu visit always
              // clears any completion-banner-seeded target — otherwise a
              // player who later revisits the leaderboard via this button
              // would silently be re-jumped into a stale round/scope
              // instead of the plain 'all-time' default this entry point
              // has always shown.
              setLeaderboardInitial(null);
              navigateTo('leaderboard');
            }}
            onSelectLeagues={() => navigateTo('leagues')}
            onSelectFriends={() => navigateTo('friends')}
            friendsNotificationCount={
              notificationSummary.pendingFriendRequestCount +
              notificationSummary.pendingChallengeCount +
              notificationSummary.matchesAwaitingActionCount
            }
            onSelectSettings={() => navigateTo('settings')}
            onSelectGrid={() => navigateTo('grid')}
            onSelectPath={() => navigateTo('path')}
            onSelectPredict={() => navigateTo('predict')}
            onLogout={handleLogoutClick}
          />
        )}
      </header>

      {/* REQ-718 UI addendum (rule 4, 2026-08-01): only ever open via
          handleLogoutClick's isGuest branch above, so a non-guest account
          never mounts this at all — logout for that account still calls
          handleLogout directly, with no prompt in between. Cancelling
          closes this and does nothing else; confirming closes this and
          calls the same handleLogout a non-guest's logout already uses,
          unmodified. */}
      {guestLogoutConfirmOpen && (
        <GuestLogoutConfirm
          onCancel={() => setGuestLogoutConfirmOpen(false)}
          onConfirm={() => {
            setGuestLogoutConfirmOpen(false);
            handleLogout();
          }}
        />
      )}

      {/* REQ-717/ADR-0036: a low-effort nudge, not a redesign — no SCREEN-xx
          entry mandates this, but a guest playing without realizing their
          progress isn't tied to a recoverable account is a real gap this
          closes cheaply. Only ever renders once currentUser has actually
          resolved to a guest (never during the brief window before GET
          /auth/me returns, same as the admin nav link's own gating). */}
      {accessToken && isGuest && (
        <div className="app__guest-banner">
          <span>Playing as {currentUser?.displayName ?? 'Guest'}.</span>
          {/* REQ-718 UI addendum (rule 5, 2026-08-25; icon revision
              2026-08-25): a collapsible disclosure toggle, same accessible
              pattern as HeaderNav's (a real focusable <button>,
              aria-expanded reflecting state, aria-controls pointing at the
              sentence it reveals) — added because the always-visible expiry
              sentence forced this banner onto two lines on narrow/mobile
              viewports, taking up disproportionate screen space. A small
              chevron icon, not a text label — a visible "Guest account
              details" label was itself wide enough to keep the collapsed
              row wrapping onto two lines, defeating the point of collapsing
              it; the accessible name lives entirely in aria-label instead,
              same icon-only-button pattern SettingsScreen.tsx's profile
              edit button already established (decorative inline SVG,
              currentColor, aria-hidden, wrapped by a labelled button).
              Collapsed by default; the sentence itself stays mounted in the
              DOM at all times (only its CSS display toggles) so
              GUEST_EXPIRY_COPY's text is never re-fetched/re-rendered by
              the toggle, just shown or hidden. */}
          <button
            type="button"
            className="app__guest-banner-toggle"
            aria-expanded={guestExpiryOpen}
            aria-controls="guest-expiry-copy"
            aria-label={guestExpiryOpen ? 'Hide guest account details' : 'Show guest account details'}
            onClick={() => setGuestExpiryOpen((open) => !open)}
            data-testid="guest-expiry-toggle"
          >
            <GuestBannerChevronIcon open={guestExpiryOpen} />
          </button>
          {/* REQ-718 UI addendum (rule 5, 2026-08-01): the actual 7-day/
              30-day policy, not a vague "temporary account" statement —
              GUEST_EXPIRY_COPY is the single source of this sentence so it
              can never drift out of sync with rules 2/3's own numbers (see
              that constant's own comment). Never rendered for a non-guest
              account, same isGuest gate as the rest of this banner. */}
          <span
            id="guest-expiry-copy"
            className={`app__guest-banner-expiry${guestExpiryOpen ? ' app__guest-banner-expiry--open' : ''}`}
            data-testid="guest-expiry-copy"
          >
            {GUEST_EXPIRY_COPY}
          </span>
          <button
            type="button"
            className="app__guest-banner-action"
            onClick={() => navigateTo('settings')}
          >
            Save your progress
          </button>
        </div>
      )}

      <main className="app__main">
        {accessToken ? (
          screen === 'game-select' ? (
            // S-085/SCREEN-09: now dispatches on the passed gameKey — xG
            // Grid's tile/nav-entry still routes to 'grid' exactly as
            // before; xG Path's new tile/nav-entry routes to 'path'. A
            // switch over the two-member literal union (quality-gate
            // follow-up, S-085) rather than an if/else-if chain — a third
            // game key added to that union without a matching case here is
            // now a compile error (the `never` assignment below), not a
            // silent no-op.
            <GameSelectScreen
              onSelectGame={(gameKey) => {
                switch (gameKey) {
                  case XG_GRID_GAME_KEY:
                    navigateTo('grid');
                    break;
                  case XG_PATH_GAME_KEY:
                    navigateTo('path');
                    break;
                  case XG_PREDICT_GAME_KEY:
                    navigateTo('predict');
                    break;
                  default: {
                    const _exhaustive: never = gameKey;
                    return _exhaustive;
                  }
                }
              }}
            />
          ) : screen === 'grid' ? (
            <GridScreen
              accessToken={accessToken}
              onAuthError={handleLogout}
              isGuest={isGuest}
              onViewRoundLeaderboard={handleViewRoundLeaderboard}
            />
          ) : screen === 'path' ? (
            // S-086: the real SCREEN-10 clue-reveal UI — replaces S-085's
            // "coming soon" placeholder now that it's built. No isGuest prop
            // (see PathScreenProps' own doc comment for why).
            <PathScreen
              accessToken={accessToken}
              onAuthError={handleLogout}
              onViewRoundLeaderboard={handleViewRoundLeaderboard}
            />
          ) : screen === 'predict' ? (
            // REQ-1301/1302/1303/1306, SCREEN-14: xG Predict's own round
            // screen. No isGuest prop (nothing here is guest-gated) and no
            // onViewRoundLeaderboard prop (REQ-1210's completion celebration
            // deliberately does not apply to xG Predict — see
            // PredictScreenProps' own doc comment for why).
            <PredictScreen accessToken={accessToken} onAuthError={handleLogout} />
          ) : screen === 'leaderboard' ? (
            <LeaderboardScreen
              accessToken={accessToken}
              onAuthError={handleLogout}
              initialGameKey={leaderboardInitial?.gameKey}
              initialScope={leaderboardInitial?.scope}
              initialRoundId={leaderboardInitial?.roundId}
              onSelectPlayer={handleSelectPlayerStats}
            />
          ) : screen === 'stats' ? (
            // REQ-411 (S-179): read-only regardless of whose stats
            // `statsTarget` names — see UserStatsScreen's own top-of-file
            // doc comment. Falls back to the current account's own
            // id/displayName if this screen is ever reached with no target
            // seeded (defensive only — both entry points below always set
            // `statsTarget` immediately before navigating here).
            <UserStatsScreen
              accessToken={accessToken}
              userId={statsTarget?.userId ?? currentUser?.id ?? ''}
              displayName={statsTarget?.displayName ?? currentUser?.displayName ?? ''}
              onAuthError={handleLogout}
              onBack={() => navigateTo(statsReturnScreen)}
              viewerUserId={currentUser?.id}
              onOpenFriends={() => navigateTo('friends')}
            />
          ) : screen === 'friends' ? (
            <FriendsScreen accessToken={accessToken} viewerUserId={currentUser?.id} onAuthError={handleLogout} />
          ) : screen === 'admin' ? (
            <AdminScreen
              accessToken={accessToken}
              onAuthError={handleLogout}
              onOpenSuggestions={() => navigateTo('admin-suggestions')}
            />
          ) : screen === 'admin-suggestions' ? (
            <SuggestionsScreen
              accessToken={accessToken}
              onAuthError={handleLogout}
              onBackToAdmin={() => navigateTo('admin')}
            />
          ) : screen === 'leagues' ? (
            <LeaguesScreen accessToken={accessToken} onAuthError={handleLogout} />
          ) : (
            // REQ-713: the "Settings" nav entry's destination — hosts
            // REQ-710's unchanged delete-account flow plus, admin-only, the
            // link onward to 'admin'. onAccountDeleted/onAuthError route
            // through the same handleLogout() as before (REQ-710: no
            // account left to show anything else on, so deletion signs out
            // and lands back on the splash screen, not AuthScreen (REQ-719)).
            <SettingsScreen
              accessToken={accessToken}
              isAdmin={currentUser?.isAdmin ?? false}
              isGuest={isGuest}
              displayName={currentUser?.displayName ?? ''}
              onDisplayNameUpdated={(displayName) =>
                setCurrentUser((current) => (current ? { ...current, displayName } : current))
              }
              // REQ-717/ADR-0036: the claim response is the full, current
              // MeResponse (email now set, effectively isGuest=false) — a
              // wholesale replace, not a partial patch like
              // onDisplayNameUpdated above, since every field in it is
              // already the server's own confirmed new state.
              onAccountClaimed={(user) => setCurrentUser(user)}
              onAccountDeleted={handleLogout}
              onCancel={() => navigateTo('game-select')}
              onAuthError={handleLogout}
              onOpenAdmin={() => navigateTo('admin')}
              onOpenStats={handleOpenOwnStats}
              themePreference={themePreference}
              onThemePreferenceChange={setThemePreference}
            />
          )
        ) : showAuthScreen ? (
          <AuthScreen
            onAuthenticated={(token, refreshToken) => {
              handleAuthenticated(token, refreshToken);
              // REQ-303/S-021, unchanged by REQ-721: a fresh login/signup
              // always lands on game-select, regardless of whatever hash was
              // present beforehand. Routing, so it stays here (S-158) rather
              // than inside useSession's own handleAuthenticated, which only
              // owns the token-storage/currentUser half of this.
              navigateTo('game-select');
            }}
          />
        ) : (
          // REQ-719: shown before AuthScreen for every unauthenticated
          // render — see showAuthScreen's own declaration above for why
          // this is never skipped on a later visit.
          <SplashScreen onGetStarted={() => setShowAuthScreen(true)} />
        )}
      </main>

      <footer className="app__footer">
        API status: <code data-testid="health-status">{describeHealth(health)}</code>
        {/* REQ-903/ADR-0064 (moved 2026-08-10 out of Settings): always in
            the footer once logged in — reachable from whatever screen a
            player is actually looking at when something goes wrong, rather
            than only from Settings. No session at all (unauthenticated,
            splash/auth screen) means no entry point at all, matching
            REQ-903's own 401 rule — a guest still sees it (isGuest below
            disables the dialog's form, never hides the button itself, same
            "advertised, not hidden" rule REQ-215 established). */}
        {accessToken && (
          <button
            type="button"
            className="app__footer-report-link"
            onClick={() => setIncidentReportOpen(true)}
          >
            Report a problem
          </button>
        )}
      </footer>

      {incidentReportOpen && accessToken && (
        <IncidentReportDialog
          accessToken={accessToken}
          isGuest={isGuest}
          currentScreen={screen}
          onClose={() => setIncidentReportOpen(false)}
          onAuthError={() => {
            setIncidentReportOpen(false);
            handleLogout();
          }}
        />
      )}
    </div>
  )
}

function describeHealth(health: HealthState): string {
  switch (health.phase) {
    case 'loading':
      return 'checking…'
    case 'healthy':
      return health.status
    case 'error':
      return `unreachable (${health.message})`
  }
}

export default App
