import { useCallback, useEffect, useState, type Dispatch, type SetStateAction } from 'react';
import { ApiError } from './apiClient';
import { fetchMe, logout, refreshAccessToken } from './auth';
import type { CurrentUser } from './types';

// S-158: extracted verbatim out of App.tsx (which mixed this self-contained
// auth-session lifecycle in with routing/dialog state) — see
// docs/backlog.md S-158 for why. Pure extraction, no behavior change: every
// REQ/ADR comment below is unchanged from its App.tsx origin except where a
// comment referenced "the effect below"/App.tsx-local code that moved here
// too, which has been reworded to still make sense from this file.
export const ACCESS_TOKEN_STORAGE_KEY = 'xg-arcade-access-token';
// REQ-715/ADR-0033: same localStorage mechanism as the access token above,
// under its own key — see that ADR for why localStorage (not a cookie) was
// chosen and the XSS trade-off that decision accepts.
export const REFRESH_TOKEN_STORAGE_KEY = 'xg-arcade-refresh-token';

export interface UseSessionResult {
  accessToken: string | null;
  currentUser: CurrentUser | null;
  setCurrentUser: Dispatch<SetStateAction<CurrentUser | null>>;
  // REQ-717/ADR-0036: mirrors User.IsGuest via MeResponse's isGuest field.
  isGuest: boolean;
  handleAuthenticated: (token: string, refreshToken: string | null) => void;
  handleLogout: () => void;
}

// REQ-715/REQ-718/REQ-719/REQ-721: the self-contained auth-session lifecycle
// — access/refresh token storage, the currently-signed-in user, silent
// refresh, and logout. Mounted once, at the top of App(), the same way
// useThemePreference (frontend/src/lib/theme.ts) is.
//
// `onLoggedOut` is called at the exact point in handleLogout's sequence
// (below) where App.tsx's own routing/dialog concerns used to sit inline —
// resetting `screen` back to 'game-select', hiding AuthScreen, and clearing
// the URL hash. Those three effects stay App.tsx's responsibility (this hook
// has no notion of `Screen` or the hash-per-screen mapping), so App.tsx
// passes a callback that does exactly what those three inline lines used to
// do, in the same order, at the same point in the sequence.
export function useSession(onLoggedOut: () => void): UseSessionResult {
  const [accessToken, setAccessToken] = useState<string | null>(() =>
    window.localStorage.getItem(ACCESS_TOKEN_STORAGE_KEY),
  );
  // REQ-504/REQ-713: the only signal for whether SettingsScreen shows its
  // admin-only link onward to AdminScreen — a non-admin must see no trace
  // of it anywhere (nav menu or Settings screen), regardless of state.
  const [currentUser, setCurrentUser] = useState<CurrentUser | null>(null);
  // REQ-717/ADR-0036: mirrors User.IsGuest via MeResponse's isGuest field.
  const isGuest = currentUser?.isGuest ?? false;

  // REQ-715: refreshToken may be null (Supabase can decline to issue one) —
  // that's a real, valid case, not an error; a null just means there's
  // nothing to persist for silent recovery later.
  //
  // S-158: only the token-storage/currentUser half of what App.tsx's own
  // AuthScreen onAuthenticated handler does — the REQ-303/S-021 navigation
  // to 'game-select' that always follows a fresh login/signup is a routing
  // concern (this hook has no notion of `Screen`), so App.tsx calls this and
  // then navigates itself, in that order, same as before the split.
  function handleAuthenticated(token: string, refreshToken: string | null) {
    window.localStorage.setItem(ACCESS_TOKEN_STORAGE_KEY, token);
    if (refreshToken) {
      window.localStorage.setItem(REFRESH_TOKEN_STORAGE_KEY, refreshToken);
    } else {
      window.localStorage.removeItem(REFRESH_TOKEN_STORAGE_KEY);
    }
    setAccessToken(token);
  }

  // REQ-715: logout (and, via the same handler, DeleteAccountScreen's
  // onAccountDeleted in App.tsx) clears the refresh token too, not only the
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
    // REQ-719/REQ-721/ADR-0039: hands off to App.tsx's onLoggedOut for the
    // routing/dialog side of logout — resetting `screen` to 'game-select',
    // hiding AuthScreen (back to the splash screen, not straight to it, the
    // same single unauthenticated entry point a first-time visitor sees),
    // and clearing the URL hash (rather than writing 'game-select's, since
    // the screen actually shown next is the splash screen, not part of the
    // Screen/SCREEN_HASHES mapping at all — a lingering authenticated
    // screen's hash would otherwise misdescribe what's on screen and could
    // be misread as a valid restore target on a later, separate load). This
    // handler is also what account deletion (onAccountDeleted) and a
    // failed/absent silent-refresh outcome (the fetchMe effect below) both
    // funnel through, so this one reset covers all three cases REQ-719
    // requires.
    onLoggedOut();

    if (tokenToLogOut) {
      logout(tokenToLogOut).catch((error: unknown) => {
        console.error('Best-effort backend logout call failed:', error);
      });
    }
  }, [accessToken, onLoggedOut]);

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

  return { accessToken, currentUser, setCurrentUser, isGuest, handleAuthenticated, handleLogout };
}
