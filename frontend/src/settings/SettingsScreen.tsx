import { useEffect, useState, type FormEvent } from 'react';
import { ApiError, describeError, updateDisplayName } from '../lib/api';
import { DeleteAccountScreen } from '../auth/DeleteAccountScreen';
import './SettingsScreen.css';

export interface SettingsScreenProps {
  accessToken: string;
  isAdmin: boolean;
  // REQ-714: the account's current DisplayName, pre-filled into the edit
  // form below — sourced from App.tsx's own GET /auth/me-backed
  // `currentUser` state, so an empty string here only ever means that
  // fetch hasn't resolved yet, not "no name."
  displayName: string;
  // REQ-714: called with the server's own confirmed new name (the PUT
  // response, not just what was typed) on a successful edit, so App.tsx can
  // update its `currentUser` state directly — no GET /auth/me refetch, no
  // full page reload, needed for the new name to be reflected everywhere
  // this account's identity is read from that state.
  onDisplayNameUpdated: (displayName: string) => void;
  onAccountDeleted: () => void;
  onCancel: () => void;
  onAuthError: () => void;
  onOpenAdmin: () => void;
}

const DISPLAY_NAME_MAX_LENGTH = 30;

// SCREEN-08 (design-document.md §3), REQ-713: the single "Settings" nav
// entry's destination, consolidating what used to be two standalone
// top-level header links — "Delete account" (REQ-710) and, admin-only,
// "Admin" (REQ-504) — into one screen. `DeleteAccountScreen` itself is
// rendered unmodified (same props, same component) so its own REQ-710
// behavior/tests are untouched by this relocation; this component only
// adds the surrounding "Settings" framing and, admin-only, a link out to
// `AdminScreen` (a link, not admin controls embedded inline here — REQ-713
// is explicit that Settings itself never gains admin UI of its own). A
// non-admin renders nothing from the `isAdmin` branch at all, matching
// REQ-504's existing "no visible entry point" guarantee for its own screen.
//
// REQ-714 (2026-07-20): also hosts the display-name edit form — same
// 1-30 character bound and inline-error convention AuthScreen.tsx's signup
// form already established for the same field, and the same "server's own
// detail text shown inline, not a generic failure banner" convention
// DeleteAccountScreen.tsx already uses for its own 401/409-shaped errors.
export function SettingsScreen({
  accessToken,
  isAdmin,
  displayName,
  onDisplayNameUpdated,
  onAccountDeleted,
  onCancel,
  onAuthError,
  onOpenAdmin,
}: SettingsScreenProps) {
  const [newDisplayName, setNewDisplayName] = useState(displayName);
  // Tracks whether the person has started editing, so a `displayName` prop
  // update arriving after this component already mounted (e.g. App.tsx's
  // GET /auth/me hadn't resolved yet when Settings was first opened) can
  // still fill the field in — without clobbering text someone's already
  // typing.
  const [touched, setTouched] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (!touched) {
      setNewDisplayName(displayName);
    }
  }, [displayName, touched]);

  async function handleDisplayNameSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setSaved(false);

    // REQ-714/701: the same 1-30 character bound checked client-side
    // before any request, matching AuthScreen.tsx's signup form and the
    // server's own check order (free checks before a database write).
    const trimmed = newDisplayName.trim();
    if (trimmed.length === 0 || trimmed.length > DISPLAY_NAME_MAX_LENGTH) {
      setError('Display name must be between 1 and 30 characters.');
      return;
    }

    setSubmitting(true);
    try {
      const updated = await updateDisplayName(accessToken, trimmed);
      setNewDisplayName(updated.displayName);
      setTouched(false);
      setSaved(true);
      onDisplayNameUpdated(updated.displayName);
    } catch (err) {
      // A 401 here means the session itself is dead (there's no
      // "wrong password" analog on this endpoint the way DeleteAccountScreen
      // has to special-case) — same "any other 401 is a dead token" handling
      // every other authenticated screen in this app already uses.
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      // REQ-714: a 409 (name taken by a different account) surfaces here
      // with the server's own specific detail text — describeError already
      // prefers ApiError.detail over a generic message, so no special-casing
      // is needed for the conflict case specifically.
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="settings-screen">
      <h2 className="settings-screen__title">Settings</h2>

      {isAdmin && (
        <section className="settings-screen__section">
          <button type="button" className="settings-screen__admin-link" onClick={onOpenAdmin}>
            Admin
          </button>
        </section>
      )}

      <section className="settings-screen__section settings-screen__section--display-name">
        <h3 className="settings-screen__section-title">Display name</h3>
        <form className="settings-screen__display-name-form" onSubmit={handleDisplayNameSubmit}>
          <label className="settings-screen__field">
            <span>Display name</span>
            <input
              type="text"
              maxLength={DISPLAY_NAME_MAX_LENGTH}
              value={newDisplayName}
              onChange={(event) => {
                setTouched(true);
                setSaved(false);
                setNewDisplayName(event.target.value);
              }}
              disabled={submitting}
            />
          </label>

          {error && (
            <p className="settings-screen__display-name-error" role="alert">
              {error}
            </p>
          )}

          {saved && !error && (
            <p className="settings-screen__display-name-success" role="status">
              Display name updated.
            </p>
          )}

          <button type="submit" className="settings-screen__display-name-submit" disabled={submitting}>
            {submitting ? 'Saving…' : 'Save name'}
          </button>
        </form>
      </section>

      <DeleteAccountScreen
        accessToken={accessToken}
        onAccountDeleted={onAccountDeleted}
        onCancel={onCancel}
        onAuthError={onAuthError}
      />
    </div>
  );
}
