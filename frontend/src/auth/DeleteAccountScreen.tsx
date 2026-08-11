import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { deleteAccount } from '../lib/auth';
import { getTurnstileToken, preloadTurnstileScript, resetTurnstileWidget } from '../lib/turnstile';
import './DeleteAccountScreen.css';

export interface DeleteAccountScreenProps {
  accessToken: string;
  onAccountDeleted: () => void;
  onCancel: () => void;
  onAuthError: () => void;
}

// SCREEN-05 (design-document.md §3), REQ-710: irreversible, so the current
// password is re-entered and re-verified server-side before anything is
// touched — no bare confirmation checkbox. A wrong password (401, title
// "Incorrect password") shows inline and deletes nothing; any other 401
// (the JWT itself is no longer valid) goes through onAuthError instead,
// same as every other authenticated screen. A captcha rejection (400,
// title "Captcha verification failed" — REQ-710's 2026-07-25 addition /
// ADR-0037's second amendment) is distinct from both of those: it shows
// inline like a wrong password, but also resets the Turnstile widget so
// the next attempt gets a fresh token, same as AuthScreen.tsx's
// handlePlayAsGuest.
//
// Sign-in/latency fix (2026-07-25, ADR-0037's third amendment): the widget
// is now a real, visible checkbox rendered inline into this screen's own
// `turnstileContainerRef` container (see the render below), not an
// invisible widget hidden in a shared body-level div — and the script
// download itself starts on mount (`preloadTurnstileScript`), well before
// the person re-types their password, rather than only starting once they
// submit.
export function DeleteAccountScreen({
  accessToken,
  onAccountDeleted,
  onCancel,
  onAuthError,
}: DeleteAccountScreenProps) {
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const turnstileContainerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    preloadTurnstileScript();
  }, []);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      const captchaToken = await getTurnstileToken(turnstileContainerRef.current!);
      await deleteAccount(accessToken, password, captchaToken);
      onAccountDeleted();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401 && err.title !== 'Incorrect password') {
        onAuthError();
        return;
      }
      if (err instanceof ApiError && err.title === 'Captcha verification failed') {
        resetTurnstileWidget();
      }
      setError(describeError(err));
      setSubmitting(false);
    }
  }

  return (
    <div className="delete-account-screen">
      <h2 className="delete-account-screen__title">Delete account</h2>

      <p className="delete-account-screen__warning" role="alert">
        This permanently deletes your account. It cannot be undone.
      </p>

      <form className="delete-account-screen__form" onSubmit={handleSubmit}>
        <label className="delete-account-screen__field">
          <span>Current password</span>
          <input
            type="password"
            required
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            disabled={submitting}
          />
        </label>

        {error && (
          <p className="delete-account-screen__error" role="alert">
            {error}
          </p>
        )}

        {/* Empty until submit — getTurnstileToken() renders the real,
            visible checkbox into this container only then, never before
            (see this file's top-of-file comment). */}
        <div className="delete-account-screen__turnstile" ref={turnstileContainerRef} />

        <div className="delete-account-screen__actions">
          <button
            type="button"
            className="delete-account-screen__cancel"
            onClick={onCancel}
            disabled={submitting}
          >
            Cancel
          </button>
          <button type="submit" className="delete-account-screen__confirm" disabled={submitting}>
            {submitting ? 'Deleting…' : 'Delete my account permanently'}
          </button>
        </div>
      </form>
    </div>
  );
}
