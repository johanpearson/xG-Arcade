import { useState, type FormEvent } from 'react';
import { describeError, login, playAsGuest, signup } from '../lib/api';
import './AuthScreen.css';

export interface AuthScreenProps {
  // REQ-715/ADR-0033: refreshToken is passed through alongside accessToken
  // (previously discarded here) so App.tsx can persist it for silent
  // session recovery — null is a real, valid case (Supabase can decline to
  // issue one), not an error.
  onAuthenticated: (accessToken: string, refreshToken: string | null) => void;
}

type Mode = 'login' | 'signup';

// No SCREEN-xx spec exists for this in design-document.md yet — a real gap
// flagged for a documentation follow-up (see the frontend report). Built
// with only the existing §2 token system, no new ad-hoc values.
export function AuthScreen({ onAuthenticated }: AuthScreenProps) {
  const [mode, setMode] = useState<Mode>('login');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [ageConfirmed, setAgeConfirmed] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  // REQ-717: a separate submitting flag from the login/signup form's own
  // `submitting` above — "Play as guest" isn't part of that form and must
  // stay independently disable-able (and vice versa) rather than sharing
  // one flag that could leave either control in the wrong state.
  const [guestSubmitting, setGuestSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const busy = submitting || guestSubmitting;

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);

    // REQ-701 password policy: minimum 8 characters, no forced complexity
    // rules — blocked client-side before the confirm-password check below,
    // same reasoning as the server-side ordering (AuthController.Signup):
    // checking a mismatch between two already-invalid passwords isn't
    // useful. Not just relying on the server's 400.
    if (mode === 'signup' && password.length < 8) {
      setError('Password must be at least 8 characters.');
      return;
    }

    // REQ-701: signup is blocked client-side when the passwords don't
    // match, not just relying on the server's 400.
    if (mode === 'signup' && password !== confirmPassword) {
      setError('Passwords do not match.');
      return;
    }

    // REQ-401/404: signup is blocked client-side without a display name, not
    // just relying on the server's 400 — this is what a leaderboard shows
    // another player instead of their email.
    if (mode === 'signup' && displayName.trim().length === 0) {
      setError('Choose a display name.');
      return;
    }

    // REQ-701: signup is blocked client-side without the age checkbox, not
    // just relying on the server's 400.
    if (mode === 'signup' && !ageConfirmed) {
      setError('Confirm you are at least 16 years old to create an account.');
      return;
    }

    setSubmitting(true);
    try {
      if (mode === 'signup') {
        await signup(email, password, confirmPassword, displayName, ageConfirmed);
        // Tier 0 UX: auto-login with the same credentials rather than
        // forcing the player through the form twice.
        const { accessToken, refreshToken } = await login(email, password);
        onAuthenticated(accessToken, refreshToken);
      } else {
        const { accessToken, refreshToken } = await login(email, password);
        onAuthenticated(accessToken, refreshToken);
      }
    } catch (err) {
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  // REQ-717/ADR-0036: zero-friction entry — no fields to fill in, so there's
  // nothing to client-side-validate before calling the API, unlike
  // handleSubmit above. On success, routes through the exact same
  // onAuthenticated callback a normal login/signup uses (App.tsx's
  // handleAuthenticated) — a guest session is stored and treated
  // identically from this point on, no separate "guest mode" path.
  async function handlePlayAsGuest() {
    setError(null);
    setGuestSubmitting(true);
    try {
      const { accessToken, refreshToken } = await playAsGuest();
      onAuthenticated(accessToken, refreshToken);
    } catch (err) {
      setError(describeError(err));
    } finally {
      setGuestSubmitting(false);
    }
  }

  return (
    <div className="auth-screen">
      <div className="auth-screen__tabs" role="tablist" aria-label="Account action">
        <button
          type="button"
          role="tab"
          aria-selected={mode === 'login'}
          className={`auth-screen__tab ${mode === 'login' ? 'auth-screen__tab--active' : ''}`}
          onClick={() => {
            setMode('login');
            setError(null);
          }}
        >
          Log in
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={mode === 'signup'}
          className={`auth-screen__tab ${mode === 'signup' ? 'auth-screen__tab--active' : ''}`}
          onClick={() => {
            setMode('signup');
            setError(null);
          }}
        >
          Sign up
        </button>
      </div>

      <form className="auth-screen__form" onSubmit={handleSubmit}>
        <label className="auth-screen__field">
          <span>Email</span>
          <input
            type="email"
            required
            value={email}
            onChange={(event) => setEmail(event.target.value)}
            disabled={busy}
          />
        </label>

        <label className="auth-screen__field">
          <span>Password</span>
          {/* No native `minLength` here on purpose (REQ-701) — same reasoning
              as Confirm password/Display name/the age checkbox below: the JS
              check above shows a specific message ("Password must be at
              least 8 characters.") rather than the browser's generic
              validation popup, which would otherwise block handleSubmit from
              running at all. `required` is kept — that's just "not empty",
              unrelated to the length policy. */}
          <input
            type="password"
            required
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            disabled={busy}
          />
        </label>

        {mode === 'signup' && (
          <label className="auth-screen__field">
            <span>Confirm password</span>
            {/* No native `required`/`minLength` here on purpose, same
                reasoning as the age checkbox below — the JS check above
                shows a specific message rather than the browser's generic
                validation popup, which would otherwise block handleSubmit
                from running at all. */}
            <input
              type="password"
              value={confirmPassword}
              onChange={(event) => setConfirmPassword(event.target.value)}
              disabled={busy}
            />
          </label>
        )}

        {mode === 'signup' && (
          <label className="auth-screen__field">
            <span>Display name</span>
            {/* No native `required` here on purpose, same reasoning as the
                age checkbox below — the JS check above shows a specific
                message rather than the browser's generic validation popup,
                which would otherwise block handleSubmit from running at all. */}
            <input
              type="text"
              maxLength={30}
              value={displayName}
              onChange={(event) => setDisplayName(event.target.value)}
              disabled={busy}
            />
          </label>
        )}

        {mode === 'signup' && (
          <label className="auth-screen__checkbox">
            {/* No native `required` here on purpose — the JS check above
                shows a specific, on-brand message (design-document.md §5)
                rather than the browser's generic validation popup. */}
            <input
              type="checkbox"
              checked={ageConfirmed}
              onChange={(event) => setAgeConfirmed(event.target.checked)}
              disabled={busy}
            />
            <span>I confirm I&apos;m at least 16 years old.</span>
          </label>
        )}

        {error && (
          <p className="auth-screen__error" role="alert">
            {error}
          </p>
        )}

        <button type="submit" className="auth-screen__submit" disabled={busy}>
          {submitting
            ? 'Working…'
            : mode === 'signup'
              ? 'Create account'
              : 'Log in'}
        </button>
      </form>

      {/* REQ-717/ADR-0036: zero-friction guest entry, sitting below (not
          inside) the log-in/sign-up form since it needs none of that form's
          fields — a real, separate action, not a third tab, since it has no
          form state of its own to switch between. Styled with only the
          existing token system, same as the rest of this unreviewed screen
          (see this file's own top-of-file note on the missing SCREEN-xx spec,
          now also covering this addition). */}
      <div className="auth-screen__divider" role="separator" aria-label="or">
        <span>or</span>
      </div>
      <button
        type="button"
        className="auth-screen__guest"
        onClick={handlePlayAsGuest}
        disabled={busy}
      >
        {guestSubmitting ? 'Starting…' : 'Play as guest'}
      </button>
      <p className="auth-screen__guest-hint">
        No email needed. You can save your progress and pick a real account
        any time from Settings.
      </p>
    </div>
  );
}
