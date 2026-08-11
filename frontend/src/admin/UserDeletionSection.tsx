import { useState } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { deleteUserByEmail } from '../lib/admin';

interface UserDeletionSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-506: same visibility gate as RoundControlSection (both are hidden
// together by AdminScreen's activeRound !== null check, since they share
// the same Production environment gate server-side).
export function UserDeletionSection({ accessToken, onAuthError }: UserDeletionSectionProps) {
  const [email, setEmail] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function handleDeleteConfirmed() {
    setDeleting(true);
    setError(null);
    setMessage(null);
    try {
      const result = await deleteUserByEmail(accessToken, email);
      setConfirming(false);
      if (result === 'not-found') {
        setError('No user found with that email.');
      } else {
        setEmail('');
        setMessage('Deleted.');
      }
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setError(describeError(err));
    } finally {
      setDeleting(false);
    }
  }

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Delete a user</h3>
      <label className="admin-screen__field">
        <span>Email</span>
        <input
          type="email"
          required
          value={email}
          onChange={(event) => {
            setEmail(event.target.value);
            setMessage(null);
            setError(null);
          }}
          disabled={deleting}
        />
      </label>

      {error && (
        <p className="admin-screen__error" role="alert">
          {error}
        </p>
      )}
      {message && <p className="admin-screen__confirmation">{message}</p>}

      <div className="admin-screen__action-group">
        {confirming ? (
          <div className="admin-screen__confirm-row">
            <button type="button" onClick={handleDeleteConfirmed} disabled={deleting || !email}>
              {deleting ? 'Deleting…' : 'Yes, delete this user permanently'}
            </button>
            <button type="button" onClick={() => setConfirming(false)} disabled={deleting}>
              Cancel
            </button>
          </div>
        ) : (
          <button type="button" onClick={() => setConfirming(true)} disabled={!email}>
            Delete user
          </button>
        )}
      </div>
    </section>
  );
}
