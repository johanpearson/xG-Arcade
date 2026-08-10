import { useCallback, useEffect, useState } from 'react';
import './App.css';
import { AdminScreen } from './admin/AdminScreen';
import { SuggestionsScreen } from './admin/SuggestionsScreen';
import { ApiError, fetchMe, logout, refreshAccessToken } from './lib/api';
import type { CurrentUser } from './lib/types';
import { AuthScreen } from './auth/AuthScreen';
import { AnnouncementBanner } from './components/AnnouncementBanner';
import { Logo } from './components/Logo';
import { GameSelectScreen, XG_GRID_GAME_KEY, XG_PATH_GAME_KEY } from './games/GameSelectScreen';
import { GridScreen } from './grid/GridScreen';
import { IncidentReportDialog } from './incidents/IncidentReportDialog';
import { GuestLogoutConfirm } from './nav/GuestLogoutConfirm';
import { HeaderNav } from './nav/HeaderNav';
import { LeaderboardScreen } from './leaderboard/LeaderboardScreen';
import { LeaguesScreen } from './leagues/LeaguesScreen';
import { PathScreen } from './path/PathScreen';
import { SettingsScreen } from './settings/SettingsScreen';
import { SplashScreen } from './splash/SplashScreen';
import { GUEST_EXPIRY_COPY } from './lib/guestExpiryCopy';
import { useThemePreference } from './lib/theme';

type HealthState =
  | { phase: 'loading' }
  | { phase: 'healthy'; status: string }
  | { phase: 'error'; message: string };

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';
const ACCESS_TOKEN_STORAGE_KEY = 'xg-arcade-access-token';
// REQ-715/ADR-0033: same localStorage mechanism as the access token above,
// under its own key — see that ADR for why localStorage (not a cookie) was
// chosen and the XSS trade-off that decision accepts.
const REFRESH_TOKEN_STORAGE_KEY = 'xg-arcade-refresh-token';

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
// 'admin-suggestions' (REQ-509/REQ-510, S-090, ADR-0053) is
// SuggestionsScreen's own destination — reachable only via a link inside
// AdminScreen itself, one hop further than 'admin', mirroring how 'admin'
// is in turn only reachable from 'settings'. Never a default destination
// and never given its own top-level nav entry, per ADR-0053's "a new,
// separate screen... reached the same gated way" framing.
type Screen =
  | 'game-select'
  | 'grid'
  | 'path'
  | 'leaderboard'
  | 'leagues'
  | 'settings'
  | 'admin'
  | 'admin-suggestions';

// REQ-721/ADR-0039: hash-based, hand-rolled URL-per-screen mapping — see
// that ADR for why (hash not path, no router library, no popstate/
// hashchange listener; back/forward is explicitly out of scope). This is
// the entire mechanism: one lookup table, read once on mount below, written
// at every navigateTo() call site.
const SCREEN_HASHES: Record<Screen, string> = {
  'game-select': '#/game-select',
  grid: '#/grid',
  path: '#/path',
  leaderboard: '#/leaderboard',
  leagues: '#/leagues',
  settings: '#/settings',
  admin: '#/admin',
  'admin-suggestions': '#/admin/suggestions',
};

const HASH_TO_SCREEN: Partial<Record<string, Screen>> = Object.fromEntries(
  Object.entries(SCREEN_HASHES).map(([screenName, hash]) => [hash, screenName as Screen]),
);

function screenForHash(hash: string): Screen | null {
  return HASH_TO_SCREEN[hash] ?? null;
}

function App() {
  const [health, setHealth] = useState<HealthState>({ phase: 'loading' });
  const [accessToken, setAccessToken] = useState<string | null>(() =>
    window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY),
  );
  const [screen, setScreen] = useState<Screen>(() => {
    // REQ-721/ADR-0039: URL restoration applies only to a reload of an
    // already-authenticated, already-valid session — never to an
    // unauthenticated visitor (must never bypass REQ-719's splash gate) and
    // never to a fresh login/signup (handleAuthenticated below always
    // navigates to 'game-select' unconditionally, regardless of the hash).
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
  // handleLogout below, which is also what fires on account deletion and a
  // failed/absent silent-refresh outcome (see that handler's own
  // reasoning) — so every one of those returns to the splash screen, never
  // straight to AuthScreen.
  const [showAuthScreen, setShowAuthScreen] = useState(false);
  // REQ-504/REQ-713: the only signal for whether SettingsScreen shows its
  // admin-only link onward to AdminScreen — a non-admin must see no trace
  // of it anywhere (nav menu or Settings screen), regardless of state.
  const [currentUser, setCurrentUser] = useState<CurrentUser | null>(null);
  // REQ-717/ADR-0036: mirrors User.IsGuest via MeResponse's isGuest field.
  const isGuest = currentUser?.isGuest ?? false;
  // REQ-718 UI addendum (rule 4, 2026-08-01): true only while the
  // confirmation prompt gating a guest's "Log out" click is open — see
  // handleLogoutClick below. Never true for a non-guest account, since
  // that branch calls handleLogout directly and never sets this.
  const [guestLogoutConfirmOpen, setGuestLogoutConfirmOpen] = useState(false);
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

  // REQ-715: refreshToken may be null (Supabase can decline to issue one) —
  // that's a real, valid case, not an error; a null just means there's
  // nothing to persist for silent recovery later.
  function handleAuthenticated(token: string, refreshToken: string | null) {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, token);
    if (refreshToken) {
      window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, refreshToken);
    } else {
      window.localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY);
    }
    setAccessToken(token);
    // REQ-303/S-021, unchanged by REQ-721: a fresh login/signup always
    // lands on game-select, regardless of whatever hash was present
    // beforehand.
    navigateTo('game-select');
  }

  // REQ-715: logout (and, via the same handler, DeleteAccountScreen's
  // onAccountDeleted below) clears the refresh token too, not only the
  // access token — a stale refresh token must never outlive an explicit
  // logout.
  //
  // REQ-718/ADR-0038: also fires a best-effort POST /auth/logout so an
  // unclaimed guest account gets deleted server-side. Deliberately not
  // awaited — the local clear-and-reset below (REQ-715's existing, instant
  // logout UX for every account, guest or not) must never be delayed or
  // blocked by that network call being slow or failing; any failure is
  // caught and logged rather than surfaced, since rule 3's 7-day inactivity
  // purge independently catches this account if the call never completes.
  // The token is captured before state is cleared since accessToken becomes
  // null immediately below.
  const handleLogout = useCallback(() => {
    const tokenToLogOut = accessToken;

    window.localStorage.removeItem(ACCESS_TOKEN_STORAGE_KEY);
    window.localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY);
    setAccessToken(null);
    setCurrentUser(null);
    setScreen('game-select');
    // REQ-719: back to the splash screen, not straight to AuthScreen — the
    // same single unauthenticated entry point a first-time visitor sees.
    // This handler is also what account deletion (onAccountDeleted) and a
    // failed/absent silent-refresh outcome (the effect below) both funnel
    // through, so this one reset covers all three cases REQ-719 requires.
    setShowAuthScreen(false);
    // REQ-721/ADR-0039: clear the hash rather than writing 'game-select's —
    // the screen actually shown next is the splash screen (not part of the
    // Screen/SCREEN_HASHES mapping at all), so a lingering authenticated
    // screen's hash would otherwise misdescribe what's on screen and could
    // be misread as a valid restore target on a later, separate load.
    window.location.hash = '';

    if (tokenToLogOut) {
      logout(tokenToLogOut).catch((error: unknown) => {
        console.error('Best-effort backend logout call failed:', error);
      });
    }
  }, [accessToken]);

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

  // REQ-715/ADR-0033: the one place a stored refresh token is exchanged for
  // a new access token — mediated through POST /auth/refresh exactly like
  // login/signup (ADR-0013), never a direct frontend-to-Supabase call. On
  // success, stores the new access token (and, if Supabase's rotation
  // returned one, a new refresh token — otherwise the existing stored
  // refresh token is left untouched rather than assumed dead) and returns
  // it; on any failure (including "no stored refresh token to try")
  // resolves to null so callers can fall through to a full logout without
  // an infinite retry.
  const attemptSilentRefresh = useCallback(async (): Promise<string | null> => {
    const storedRefreshToken = window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY);
    if (!storedRefreshToken) return null;

    try {
      const refreshed = await refreshAccessToken(storedRefreshToken);
      window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, refreshed.accessToken);
      if (refreshed.refreshToken) {
        window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, refreshed.refreshToken);
      }
      setAccessToken(refreshed.accessToken);
      return refreshed.accessToken;
    } catch {
      // Invalid/expired/revoked — the caller falls through to handleLogout,
      // which clears the now-dead refresh token too.
      return null;
    }
  }, []);

  // REQ-504/REQ-715: fetched on a fresh login/signup (accessToken just set
  // by handleAuthenticated), on restoring a token already in localStorage on
  // initial load, and — new for REQ-715 — this is also where a missing or
  // 401'd access token triggers a silent refresh attempt before falling
  // back to a full logout, rather than logging out unconditionally.
  //
  // Both branches below funnel through the same attemptSilentRefresh: on
  // success it calls setAccessToken with the new token, which changes this
  // effect's own dependency and re-runs it — that re-run *is* the retry
  // (fetchMe naturally gets called again with the new token), so there's no
  // separate manual retry path to maintain here.
  useEffect(() => {
    let cancelled = false;

    if (!accessToken) {
      setCurrentUser(null);

      if (!window.localStorage.getItem(REFRESH_TOKEN_STORAGE_KEY)) {
        return;
      }

      attemptSilentRefresh().then((refreshed) => {
        if (!cancelled && !refreshed) {
          handleLogout();
        }
      });

      return () => {
        cancelled = true;
      };
    }

    fetchMe(accessToken)
      .then((user) => {
        if (!cancelled) setCurrentUser(user);
      })
      .catch(async (error: unknown) => {
        if (cancelled) return;
        if (error instanceof ApiError && error.status === 401) {
          const refreshed = await attemptSilentRefresh();
          if (cancelled) return;
          if (!refreshed) {
            handleLogout();
          }
        }
        // Any other failure here just leaves currentUser null — the admin
        // nav link stays hidden, but the rest of the app is unaffected.
      });

    return () => {
      cancelled = true;
    };
  }, [accessToken, handleLogout, attemptSilentRefresh]);

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
            isSettingsCurrent={screen === 'settings'}
            isGridCurrent={screen === 'grid'}
            isPathCurrent={screen === 'path'}
            onSelectLeaderboard={() => navigateTo('leaderboard')}
            onSelectLeagues={() => navigateTo('leagues')}
            onSelectSettings={() => navigateTo('settings')}
            onSelectGrid={() => navigateTo('grid')}
            onSelectPath={() => navigateTo('path')}
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
          {/* REQ-718 UI addendum (rule 5, 2026-08-01): the actual 7-day/
              30-day policy, not a vague "temporary account" statement —
              GUEST_EXPIRY_COPY is the single source of this sentence so it
              can never drift out of sync with rules 2/3's own numbers (see
              that constant's own comment). Never rendered for a non-guest
              account, same isGuest gate as the rest of this banner. */}
          <span className="app__guest-banner-expiry" data-testid="guest-expiry-copy">
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
                  default: {
                    const _exhaustive: never = gameKey;
                    return _exhaustive;
                  }
                }
              }}
            />
          ) : screen === 'grid' ? (
            <GridScreen accessToken={accessToken} onAuthError={handleLogout} isGuest={isGuest} />
          ) : screen === 'path' ? (
            // S-086: the real SCREEN-10 clue-reveal UI — replaces S-085's
            // "coming soon" placeholder now that it's built. No isGuest prop
            // (see PathScreenProps' own doc comment for why).
            <PathScreen accessToken={accessToken} onAuthError={handleLogout} />
          ) : screen === 'leaderboard' ? (
            <LeaderboardScreen accessToken={accessToken} onAuthError={handleLogout} />
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
              themePreference={themePreference}
              onThemePreferenceChange={setThemePreference}
            />
          )
        ) : showAuthScreen ? (
          <AuthScreen onAuthenticated={handleAuthenticated} />
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
