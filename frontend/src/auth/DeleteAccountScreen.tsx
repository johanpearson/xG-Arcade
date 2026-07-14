import { useState, type FormEvent } from 'react';
import { ApiError, deleteAccount, describeError } from '../lib/api';
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
// same as every other authenticated screen.
export function DeleteAccountScreen({
  accessToken,
  onAccountDeleted,
  onCancel,
  onAuthError,
}: DeleteAccountScreenProps) {
  const [password, setPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      await deleteAccount(accessToken, password);
      onAccountDeleted();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401 && err.title !== 'Incorrect password') {
        onAuthError();
        return;
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
