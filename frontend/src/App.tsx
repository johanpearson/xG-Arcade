import { useCallback, useEffect, useState } from 'react';
import './App.css';
import { AdminScreen } from './admin/AdminScreen';
import { ConnectDisputeSuggestionsScreen } from './admin/ConnectDisputeSuggestionsScreen';
import { SuggestionsScreen } from './admin/SuggestionsScreen';
import { AuthScreen } from './auth/AuthScreen';
import { AnnouncementBanner } from './components/AnnouncementBanner';
import { Logo } from './components/Logo';
import {
  GameSelectScreen,
  XG_GRID_GAME_KEY,
  XG_PATH_GAME_KEY,
  XG_PREDICT_GAME_KEY,
  XG_CONNECT_GAME_KEY,
  XG_HIGHER_LOWER_GAME_KEY,
} from './games/GameSelectScreen';
import { ConnectEntryScreen } from './connect/ConnectEntryScreen';
import { GridScreen } from './grid/GridScreen';
import { HigherLowerScreen } from './higherlower/HigherLowerScreen';
import { IncidentReportDialog } from './incidents/IncidentReportDialog';
import { GuestLogoutConfirm } from './nav/GuestLogoutConfirm';
import { HeaderNav } from './nav/HeaderNav';
import { LeaderboardScreen, type LeaderboardRoundTarget } from './leaderboard/LeaderboardScreen';
import { LeaguesScreen } from './leagues/LeaguesScreen';
import { PathScreen } from './path/PathScreen';
import { PredictScreen } from './predict/PredictScreen';
import { SettingsScreen } from './settings/SettingsScreen';
import { SplashScreen } from './splash/SplashScreen';
import { FriendsScreen, type FriendsTabKey } from './social/FriendsScreen';
import { UserStatsScreen } from './users/UserStatsScreen';
import { GUEST_EXPIRY_COPY } from './lib/guestExpiryCopy';
import { useThemePreference } from './lib/theme';
import { useNotificationSummary } from './lib/useNotificationSummary';
import { useSession } from './lib/useSession';
import { useAppNavigation, type Screen } from './lib/useAppNavigation';

type HealthState =
  | { phase: 'loading' }
  | { phase: 'healthy'; status: string }
  | { phase: 'error'; message: string };

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';

// S-235: the `Screen` union itself, the hash-per-screen mapping, and the
// mechanics of navigating between screens (navigateTo/seedAndNavigate) all
// moved to frontend/src/lib/useAppNavigation.ts — see that file's own
// top-of-file comment (which also carries the per-Screen-value reachability
// documentation that used to sit here) for the full picture, and for why
// each screen's own seed state (leaderboardInitial, statsTarget, etc.)
// deliberately stayed here rather than moving with it.

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
  // S-235: `screen` itself, the hash-sync mechanism, and navigateTo/
  // seedAndNavigate all live in useAppNavigation now — see that hook's own
  // top-of-file comment for the full reasoning, including why
  // `resetToLoggedOut` (used by handleLoggedOut below) lives there too but
  // `handleLoggedOut` itself stays here.
  const { screen, navigateTo, seedAndNavigate, resetToLoggedOut } = useAppNavigation();
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
  // reaction to a logout: resetting `screen` back to 'game-select' and
  // clearing the URL hash (both now useAppNavigation's `resetToLoggedOut`,
  // S-235 — see that function's own comment), plus hiding AuthScreen (back
  // to the splash screen — see showAuthScreen's own declaration above for
  // why), which stays here since it isn't a navigation concern. This same
  // handoff covers every path that ends in a logout — an explicit "Log
  // out" click, account deletion, and a failed/absent silent-refresh
  // outcome — since all three funnel through useSession's single
  // handleLogout.
  // useCallback (stable identity across renders, not just useState setters'
  // own already-stable identities) matters here: useSession's handleLogout
  // depends on this callback, and the fetchMe effect in turn depends on
  // handleLogout — an unmemoized inline function here would give
  // handleLogout a new identity on every App render, re-running that effect
  // (and re-fetching /auth/me, clobbering any local currentUser update such
  // as SettingsScreen's onAccountClaimed) far more often than intended.
  // `resetToLoggedOut` is itself stable (useAppNavigation's own useCallback,
  // empty deps), so listing it as this callback's only dependency doesn't
  // break that stability chain.
  const handleLoggedOut = useCallback(() => {
    resetToLoggedOut();
    setShowAuthScreen(false);
  }, [resetToLoggedOut]);
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
  // REQ-1411 (design-document.md SCREEN-07's 2026-09-03 badge-redesign
  // status note): which tab 'friends' should open on — same in-memory,
  // read-once-at-mount seed pattern `leaderboardInitial`/`statsTarget` above
  // already establish (FriendsScreen's own `activeTab` useState initializer
  // is what actually reads this, once, per REQ-1411's own handler below).
  // `null` means "default to FriendsScreen's own 'friends' tab" — the plain
  // "Friends" nav entry (onSelectFriends below) always clears this back to
  // null first, the same "a plain manual visit never silently re-jumps to a
  // stale target" discipline `onSelectLeaderboard`'s own
  // `setLeaderboardInitial(null)` already follows.
  const [friendsInitialTab, setFriendsInitialTab] = useState<FriendsTabKey | null>(null);

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

  // S-235: `navigateTo` itself now lives in useAppNavigation — see that
  // hook's own comment. The four handlers below each became a thin wrapper
  // around that hook's `seedAndNavigate`, unchanged in what they seed or
  // which screen they navigate to.

  // REQ-1210/ADR-0083: GridScreen/PathScreen's round-completion banner
  // calls this with the specific round+game+scope it already resolved
  // (see either screen's own handleViewCompletedRoundLeaderboard) — this
  // seeds `leaderboardInitial` and navigates in the same update, so the
  // freshly-mounted LeaderboardScreen (grid/path and leaderboard are
  // mutually exclusive Screen branches, so this is always a real
  // mount, never a same-instance prop update) reads the target on its own
  // first render.
  function handleViewRoundLeaderboard(target: LeaderboardRoundTarget) {
    seedAndNavigate('leaderboard', () => setLeaderboardInitial(target));
  }

  // REQ-411 (S-179): Settings' "My stats" link — seeds `statsTarget` with
  // the current account's own id/name (the only source App.tsx has for
  // "own stats"; UserStatsScreen itself has no own-vs-other concept, see
  // its own doc comment) and remembers 'settings' as where "Back" should
  // return to.
  function handleOpenOwnStats() {
    if (!currentUser) return;
    seedAndNavigate('stats', () => {
      setStatsTarget({ userId: currentUser.id, displayName: currentUser.displayName });
      setStatsReturnScreen('settings');
    });
  }

  // REQ-411 (S-179, generalized 2026-09-03 for direct user feedback: "click
  // a friend in the list to go to their profile"): a leaderboard row's (or,
  // now, a friend row's) display name — seeds `statsTarget` with whichever
  // player was selected and remembers `returnScreen` as where "Back" should
  // return to. `returnScreen` defaults to 'leaderboard', preserving every
  // existing call site's behavior unchanged (LeaderboardScreen's own
  // `onSelectPlayer` below still calls this with no third argument);
  // FriendsScreen's `onSelectPlayer` is the one new caller that passes
  // 'friends' explicitly.
  function handleSelectPlayerStats(userId: string, displayName: string, returnScreen: Screen = 'leaderboard') {
    seedAndNavigate('stats', () => {
      setStatsTarget({ userId, displayName });
      setStatsReturnScreen(returnScreen);
    });
  }

  // REQ-1411 (design-document.md SCREEN-07's 2026-09-03 badge-redesign
  // status note): NotificationBadge's own "Friend requests"/"Challenges"
  // category links call this — seeds `friendsInitialTab` and navigates in
  // the same update, so a freshly-mounted FriendsScreen reads the target on
  // its own first render (FriendsScreen's `activeTab` useState initializer
  // is what actually reads `initialTab`, same "read once at mount" shape
  // `leaderboardInitial`/LeaderboardScreen already establish — see that
  // seed's own comment). Known, accepted limitation shared with that same
  // precedent, not a new gap: clicking a category link while FriendsScreen
  // is *already* the showing screen doesn't re-seed an already-mounted
  // instance's tab (`navigateTo('friends')` is a no-op when `screen` is
  // already `'friends'`) — exactly as true today of `onSelectLeaderboard`'s
  // completion-banner-seeded target while already on the leaderboard.
  function handleOpenFriendsTab(tab: FriendsTabKey) {
    seedAndNavigate('friends', () => setFriendsInitialTab(tab));
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
            isConnectCurrent={screen === 'xg-connect-entry'}
            isHigherLowerCurrent={screen === 'higher-lower'}
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
            onSelectFriends={() => {
              // REQ-1411: same "a plain manual visit always clears a
              // previously-seeded target" discipline as
              // onSelectLeaderboard's setLeaderboardInitial(null) above —
              // otherwise a later ordinary visit via this nav entry could
              // silently re-jump to a stale badge-seeded tab.
              setFriendsInitialTab(null);
              navigateTo('friends');
            }}
            pendingFriendRequestCount={notificationSummary.pendingFriendRequestCount}
            pendingChallengeCount={notificationSummary.pendingChallengeCount}
            matchesAwaitingActionCount={notificationSummary.matchesAwaitingActionCount}
            onOpenFriendsTab={handleOpenFriendsTab}
            onSelectSettings={() => navigateTo('settings')}
            onSelectGrid={() => navigateTo('grid')}
            onSelectPath={() => navigateTo('path')}
            onSelectPredict={() => navigateTo('predict')}
            onSelectConnect={() => navigateTo('xg-connect-entry')}
            onSelectHigherLower={() => navigateTo('higher-lower')}
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
            // before; xG Path's tile/nav-entry routes to 'path'; xG
            // Predict's to 'predict'. A switch over the literal union
            // (quality-gate follow-up, S-085) rather than an if/else-if
            // chain — a new game key added to that union without a matching
            // case here is now a compile error (the `never` assignment
            // below), not a silent no-op. REQ-1415 added xG Connect's own
            // case as the deliberate exception noted just below.
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
                  case XG_CONNECT_GAME_KEY:
                    // REQ-1415: the deliberate exception — routes to the
                    // two-choice entry screen, not directly into gameplay.
                    navigateTo('xg-connect-entry');
                    break;
                  case XG_HIGHER_LOWER_GAME_KEY:
                    navigateTo('higher-lower');
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
          ) : screen === 'xg-connect-entry' ? (
            // REQ-1415/SCREEN-17: the two choices each seed
            // `friendsInitialTab` and navigate to 'friends', reusing
            // handleOpenFriendsTab exactly as the notification-badge
            // dropdown already does (same two-step "seed a tab, navigate"
            // mechanism, see that function's own comment) — this is what
            // makes REQ-1415's "don't restate the underlying flows' own
            // business rules" and "Matches tab stays reachable" criteria
            // hold for free, since FriendsScreen always renders all four
            // tabs regardless of which one is initially active.
            <ConnectEntryScreen
              onChallengeFriend={() => handleOpenFriendsTab('friends')}
              onChallengeRandomPlayer={() => handleOpenFriendsTab('matchmaking')}
            />
          ) : screen === 'higher-lower' ? (
            // REQ-1504/1505, SCREEN-18 (S-228): xG Higher/Lower's own round
            // screen. No isGuest prop (nothing here is guest-gated — see
            // HigherLowerScreenProps' own doc comment). onViewRoundLeaderboard
            // IS wired, unlike 'predict' above — this game's HasEnded is a
            // synchronous, immediate "you just finished" moment (the same
            // shape Grid/Path have), so REQ-1210's completion banner applies
            // here, deliberately diverging from xG Predict's exclusion (see
            // design-document.md SCREEN-18's own status note).
            <HigherLowerScreen
              accessToken={accessToken}
              onAuthError={handleLogout}
              onViewRoundLeaderboard={handleViewRoundLeaderboard}
            />
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
            <FriendsScreen
              accessToken={accessToken}
              viewerUserId={currentUser?.id}
              onAuthError={handleLogout}
              initialTab={friendsInitialTab ?? undefined}
              onSelectPlayer={(userId, displayName) => handleSelectPlayerStats(userId, displayName, 'friends')}
            />
          ) : screen === 'admin' ? (
            <AdminScreen
              accessToken={accessToken}
              onAuthError={handleLogout}
              onOpenSuggestions={() => navigateTo('admin-suggestions')}
              onOpenConnectDisputeSuggestions={() => navigateTo('admin-connect-dispute-suggestions')}
            />
          ) : screen === 'admin-suggestions' ? (
            <SuggestionsScreen
              accessToken={accessToken}
              onAuthError={handleLogout}
              onBackToAdmin={() => navigateTo('admin')}
            />
          ) : screen === 'admin-connect-dispute-suggestions' ? (
            <ConnectDisputeSuggestionsScreen
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
