import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { claimAccount, updateDisplayName } from '../lib/auth';
import { fetchAvatarImageObjectUrl, fetchAvatarStatus, submitAvatar } from '../lib/avatar';
import { DeleteAccountScreen } from '../auth/DeleteAccountScreen';
import type { AvatarStatusResponse, CurrentUser } from '../lib/types';
import type { ThemePreference } from '../lib/theme';
import { GUEST_EXPIRY_COPY } from '../lib/guestExpiryCopy';
import './SettingsScreen.css';
// REQ-722/S-184: reused for the new profile header's avatar preview
// (ProfileAvatarPreview below) — same classnames PlayerAvatar.tsx itself
// renders with (.player-avatar/.player-avatar--placeholder/
// .player-avatar__placeholder-svg), so this self-view's avatar looks
// identical to PlayerAvatar's own rendering without actually mounting
// PlayerAvatar (which would refetch via the cross-user
// GET /users/{userId}/avatar/image endpoint this screen doesn't need — see
// ProfileAvatarPreview's own comment below for why).
import '../components/PlayerAvatar.css';

// REQ-716: the toggle's own option list — order matches the three-state
// spec exactly (System first/default, per ADR-0034).
const THEME_OPTIONS: ReadonlyArray<{ value: ThemePreference; label: string }> = [
  { value: 'system', label: 'System' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
];

export interface SettingsScreenProps {
  accessToken: string;
  isAdmin: boolean;
  // REQ-717/ADR-0036: true only while the current account is a guest
  // (App.tsx passes `currentUser.isGuest` — MeResponse's own field).
  // Gates the "Save your progress" claim section below — a claimed
  // (non-guest) account renders none of it, same "no visible trace when
  // not applicable" pattern REQ-504's admin-link gating already uses.
  isGuest: boolean;
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
  // REQ-717/ADR-0036: called with the server's own confirmed MeResponse
  // (email now set, effectively isGuest=false) on a successful claim, so
  // App.tsx can replace its `currentUser` state wholesale — this response
  // already carries every field that state needs, unlike
  // onDisplayNameUpdated above, which only ever carries the one field that
  // changed.
  onAccountClaimed: (user: CurrentUser) => void;
  onAccountDeleted: () => void;
  onCancel: () => void;
  onAuthError: () => void;
  onOpenAdmin: () => void;
  // REQ-411 (S-179): opens SCREEN-13's stats/profile view scoped to the
  // current account's own id — the "own stats" entry point REQ-411's UI
  // acceptance criteria requires. Deliberately here, not a new top-level
  // `HeaderNav` entry — REQ-712/713 already consolidated standalone
  // top-level links into Settings specifically to stop header overflow;
  // adding a new one here would reintroduce exactly that regression. Not
  // gated by `isAdmin`/`isGuest` — every account (guest or claimed) can
  // view its own stats, same as REQ-411's own "Own stats" acceptance
  // criteria ("Given a logged-in player (guest or claimed account)").
  onOpenStats: () => void;
  // REQ-716/ADR-0034: the player's own choice (System/Light/Dark) — the
  // resolved light/dark value itself isn't a prop here, since App.tsx's
  // useThemePreference already owns applying it to <html>; this component
  // only needs the preference to know which radio is checked and to hand a
  // new choice back up.
  themePreference: ThemePreference;
  onThemePreferenceChange: (preference: ThemePreference) => void;
}

const DISPLAY_NAME_MAX_LENGTH = 30;

// REQ-722/S-182: client-side pre-check only, matching the server's own
// known limits (backend/src/XGArcade.Api/Avatars/AvatarEndpoints.cs's
// MaxImageSizeBytes/AllowedContentTypes) as a UX nicety — the server is
// still the real enforcement, and its own 400 detail text (surfaced via
// describeError in handleAvatarSubmit below) is what's shown on rejection,
// never a duplicated client-side message standing in for it.
const AVATAR_MAX_SIZE_BYTES = 5 * 1024 * 1024;
const AVATAR_ALLOWED_TYPES = ['image/jpeg', 'image/png', 'image/webp'];

// REQ-722/S-182: fetches an authenticated object-URL for a given
// AvatarSubmissionSummary.imageUrl (GET /users/me/avatar/{id}/image streams
// raw bytes and requires a bearer token an <img src> can't carry — see
// fetchAvatarImageObjectUrl's own doc comment in lib/avatar.ts). Re-fetches
// whenever imageUrl changes (e.g. a new upload replaces the Pending row's
// id/imageUrl) and revokes the previously-created object URL in this
// effect's cleanup — both on unmount and on every imageUrl change — so this
// never leaks blob URLs across re-renders or across accessTokens
// (accessToken is in the dependency array too, since a stale token should
// never be reused for a refetch).
function useAvatarObjectUrl(accessToken: string, imageUrl: string | null): string | null {
  const [objectUrl, setObjectUrl] = useState<string | null>(null);

  useEffect(() => {
    if (!imageUrl) {
      setObjectUrl(null);
      return;
    }

    let cancelled = false;
    let createdUrl: string | null = null;

    fetchAvatarImageObjectUrl(accessToken, imageUrl)
      .then((url) => {
        if (cancelled) {
          URL.revokeObjectURL(url);
          return;
        }
        createdUrl = url;
        setObjectUrl(url);
      })
      .catch(() => {
        // REQ-722: a failed preview fetch (e.g. the image was since removed)
        // degrades to "no preview" rather than surfacing a second error
        // banner alongside the status row's own label — the label text
        // ("Pending review"/"Rejected"/"Currently visible to other players")
        // already carries the meaningful information on its own.
        if (!cancelled) setObjectUrl(null);
      });

    return () => {
      cancelled = true;
      if (createdUrl) URL.revokeObjectURL(createdUrl);
    };
  }, [accessToken, imageUrl]);

  return objectUrl;
}

// REQ-722/S-184: the new profile header's avatar (rendered by
// SettingsScreen below, first thing under the "Settings" heading). This is a
// self-view — the account's OWN current avatar — so it reuses the
// `approvedImageUrl` this screen already resolves via useAvatarObjectUrl
// above for the "Currently visible to other players" status row further
// down, rather than mounting `PlayerAvatar` (frontend/src/components/) and
// re-fetching the same image a second time through the cross-user
// GET /users/{userId}/avatar/image endpoint — this screen's own
// already-fetched approved-image data is authoritative for "my own current
// avatar." Shares PlayerAvatar.css's classnames so the rendered result — a
// 64×64px circle, bordered/backed the same way, or the same placeholder
// silhouette when there's no approved avatar yet — is visually identical to
// what PlayerAvatar itself would render, without the duplicate fetch.
// Decorative only (`alt=""`/`aria-hidden`), same pairing rule §6 requires —
// the plain-text display name rendered alongside it (SettingsScreen's own
// render below) carries the accessible identity.
function ProfileAvatarPreview({ imageUrl }: { imageUrl: string | null }) {
  if (!imageUrl) {
    return (
      <div
        className="player-avatar player-avatar--placeholder settings-screen__profile-avatar"
        data-testid="settings-profile-avatar-placeholder"
        aria-hidden="true"
      >
        <svg className="player-avatar__placeholder-svg" viewBox="0 0 24 24" focusable="false">
          <circle cx="12" cy="8" r="4" fill="currentColor" />
          <path d="M4 21c0-4.42 3.58-8 8-8s8 3.58 8 8" fill="currentColor" />
        </svg>
      </div>
    );
  }

  return (
    <img
      className="player-avatar settings-screen__profile-avatar"
      src={imageUrl}
      alt=""
      aria-hidden="true"
      data-testid="settings-profile-avatar-image"
    />
  );
}

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
//
// REQ-717/ADR-0036 (2026-07-21): also hosts the guest claim/upgrade
// section, gated on the new `isGuest` prop — the one place in this screen
// with a real visibility gate beyond `isAdmin`'s. No SCREEN-08 wireframe
// update accompanies this in design-document.md yet beyond a short prose
// addition (same "built functionally, flagged as a doc gap" situation
// AuthScreen.tsx's own top-of-file note already describes for the
// login/signup screen as a whole).
export function SettingsScreen({
  accessToken,
  isAdmin,
  isGuest,
  displayName,
  onDisplayNameUpdated,
  onAccountClaimed,
  onAccountDeleted,
  onCancel,
  onAuthError,
  onOpenAdmin,
  onOpenStats,
  themePreference,
  onThemePreferenceChange,
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

  // REQ-717/ADR-0036: the claim/upgrade form's own state, separate from the
  // display-name form above — a different submit action, a different error
  // surface, no shared state between the two.
  const [claimEmail, setClaimEmail] = useState('');
  const [claimPassword, setClaimPassword] = useState('');
  const [claimConfirmPassword, setClaimConfirmPassword] = useState('');
  const [claimSubmitting, setClaimSubmitting] = useState(false);
  const [claimError, setClaimError] = useState<string | null>(null);

  // REQ-722/S-182: the avatar section's own state, separate from every form
  // above — a different submit action, a different error surface, no
  // shared state with the other sections. `avatarRefreshKey` is bumped
  // after a successful upload to trigger a re-fetch of GET /users/me/avatar
  // (rather than hand-constructing the post-upload state client-side) so
  // this section reflects the server's own single resulting Pending row —
  // REQ-722's "uploading while pending replaces rather than queues a second
  // submission" is already the server's behavior; this only needs to
  // re-read it.
  const [avatarStatus, setAvatarStatus] = useState<AvatarStatusResponse | null>(null);
  const [avatarStatusError, setAvatarStatusError] = useState<string | null>(null);
  const [avatarRefreshKey, setAvatarRefreshKey] = useState(0);
  const [avatarFile, setAvatarFile] = useState<File | null>(null);
  const [avatarSubmitting, setAvatarSubmitting] = useState(false);
  const [avatarError, setAvatarError] = useState<string | null>(null);
  const [avatarSaved, setAvatarSaved] = useState(false);
  const avatarFileInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (!touched) {
      setNewDisplayName(displayName);
    }
  }, [displayName, touched]);

  // REQ-722/S-182: fetched on mount, and again whenever avatarRefreshKey
  // changes (after a successful upload) — same "any other 401 is a dead
  // token" handling every other authenticated fetch in this screen already
  // uses.
  useEffect(() => {
    let cancelled = false;
    fetchAvatarStatus(accessToken)
      .then((result) => {
        if (cancelled) return;
        setAvatarStatus(result);
        setAvatarStatusError(null);
      })
      .catch((err) => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        setAvatarStatusError(describeError(err));
      });
    return () => {
      cancelled = true;
    };
  }, [accessToken, onAuthError, avatarRefreshKey]);

  // REQ-722/S-182: all three fields are independent (see AvatarStatusResponse's
  // own doc comment in lib/types.ts) — a Rejected preview and an Approved
  // preview can both need fetching alongside a Pending one at the same time,
  // so each gets its own call to the shared hook rather than one call
  // switched on a single "current status".
  const pendingImageUrl = useAvatarObjectUrl(accessToken, avatarStatus?.pending?.imageUrl ?? null);
  const rejectedImageUrl = useAvatarObjectUrl(accessToken, avatarStatus?.rejected?.imageUrl ?? null);
  const approvedImageUrl = useAvatarObjectUrl(accessToken, avatarStatus?.approved?.imageUrl ?? null);

  async function handleAvatarSubmit(event: FormEvent) {
    event.preventDefault();
    setAvatarError(null);
    setAvatarSaved(false);

    if (!avatarFile) {
      setAvatarError('Choose an image to upload.');
      return;
    }

    // REQ-722: free, local checks before any request — same "cheap check
    // before a network call" order as handleDisplayNameSubmit/
    // handleClaimSubmit above. Not the only enforcement; see
    // AVATAR_MAX_SIZE_BYTES/AVATAR_ALLOWED_TYPES's own doc comment.
    if (!AVATAR_ALLOWED_TYPES.includes(avatarFile.type)) {
      setAvatarError('Choose a JPEG, PNG, or WEBP image.');
      return;
    }
    if (avatarFile.size > AVATAR_MAX_SIZE_BYTES) {
      setAvatarError('That image is too large. Choose one under 5 MB.');
      return;
    }

    setAvatarSubmitting(true);
    try {
      await submitAvatar(accessToken, avatarFile);
      setAvatarSaved(true);
      setAvatarFile(null);
      if (avatarFileInputRef.current) avatarFileInputRef.current.value = '';
      setAvatarRefreshKey((key) => key + 1);
    } catch (err) {
      // A 401 here means the session itself is dead, same meaning as every
      // other authenticated screen in this app.
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      // REQ-722: a 400 (empty file, over 5 MB, or an unsupported type)
      // surfaces here with the server's own specific detail text —
      // describeError already prefers ApiError.detail over a generic
      // message, so the server's real limits are what's shown, not the
      // client-side pre-check copy above (which only fires before any
      // request is even sent).
      setAvatarError(describeError(err));
    } finally {
      setAvatarSubmitting(false);
    }
  }

  async function handleClaimSubmit(event: FormEvent) {
    event.preventDefault();
    setClaimError(null);

    // REQ-701 password policy, same client-side check/order as
    // AuthScreen.tsx's signup form and the server's own
    // (AuthController.Claim): free, local checks before any request.
    if (claimPassword.length < 8) {
      setClaimError('Password must be at least 8 characters.');
      return;
    }

    if (claimPassword !== claimConfirmPassword) {
      setClaimError('Passwords do not match.');
      return;
    }

    setClaimSubmitting(true);
    try {
      const updated = await claimAccount(accessToken, claimEmail, claimPassword, claimConfirmPassword);
      onAccountClaimed(updated);
      setClaimPassword('');
      setClaimConfirmPassword('');
    } catch (err) {
      // A 401 here means the session itself is dead — same "any other 401
      // is a dead token" handling every other authenticated screen in this
      // app already uses (handleDisplayNameSubmit below, DeleteAccountScreen).
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      // REQ-717: a 400 (not currently a guest, or email already in use)
      // surfaces here with the server's own specific detail text —
      // describeError already prefers ApiError.detail over a generic
      // message, no special-casing needed.
      setClaimError(describeError(err));
    } finally {
      setClaimSubmitting(false);
    }
  }

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

      {/* REQ-722/S-184: the profile header — the account's own current
          avatar plus its current display name, as PLAIN TEXT (not an
          editable field; the "Display name" section further down stays the
          actual edit form, unchanged). Placed first, directly under the
          "Settings" heading and above every other section (including the
          guest-claim call-to-action below it), since this is purely
          identity context for the rest of the screen, not an action of its
          own. See design-document.md's SCREEN-08 section for the full
          spec. */}
      <section className="settings-screen__section settings-screen__section--profile" data-testid="settings-profile-header">
        <ProfileAvatarPreview imageUrl={approvedImageUrl} />
        <span className="settings-screen__profile-name">{displayName}</span>
      </section>

      {/* REQ-717/ADR-0036: the claim/upgrade section — rendered only while
          the account is still a guest (isGuest), placed first since it's
          this screen's primary call to action for that account. Once
          claimed, onAccountClaimed's response flips isGuest to false and
          this whole section disappears — no page reload needed, same
          "caller's own state updates immediately from the server's
          confirmed response" convention the display-name form below already
          established. */}
      {isGuest && (
        <section className="settings-screen__section settings-screen__section--claim">
          <h3 className="settings-screen__section-title">Save your progress</h3>
          <p className="settings-screen__claim-hint">
            You&apos;re playing as a guest. Add an email and password to keep
            your scores and log back in from any device.
          </p>
          {/* REQ-718 UI addendum (rule 5, 2026-08-01): the actual 7-day/
              30-day removal policy, alongside (not replacing) the claim
              hint above — GUEST_EXPIRY_COPY is the single source of this
              sentence, shared with App.tsx's guest banner, so it can never
              drift out of sync with REQ-718 rules 2/3's own numbers. This
              whole section only renders while isGuest is true, so a
              non-guest account never sees this either. */}
          <p className="settings-screen__claim-hint" data-testid="guest-expiry-copy-settings">
            {GUEST_EXPIRY_COPY}
          </p>
          <form className="settings-screen__claim-form" onSubmit={handleClaimSubmit}>
            <label className="settings-screen__field">
              <span>Email</span>
              <input
                type="email"
                required
                value={claimEmail}
                onChange={(event) => setClaimEmail(event.target.value)}
                disabled={claimSubmitting}
              />
            </label>

            <label className="settings-screen__field">
              <span>Password</span>
              {/* No native `minLength` here on purpose (REQ-701), same
                  reasoning as AuthScreen.tsx's signup form — the JS check
                  above shows a specific message rather than the browser's
                  generic validation popup. */}
              <input
                type="password"
                required
                value={claimPassword}
                onChange={(event) => setClaimPassword(event.target.value)}
                disabled={claimSubmitting}
              />
            </label>

            <label className="settings-screen__field">
              <span>Confirm password</span>
              <input
                type="password"
                value={claimConfirmPassword}
                onChange={(event) => setClaimConfirmPassword(event.target.value)}
                disabled={claimSubmitting}
              />
            </label>

            {claimError && (
              <p className="settings-screen__claim-error" role="alert">
                {claimError}
              </p>
            )}

            <button
              type="submit"
              className="settings-screen__claim-submit"
              disabled={claimSubmitting}
            >
              {claimSubmitting ? 'Saving…' : 'Save my progress'}
            </button>
          </form>
        </section>
      )}

      {/* REQ-411 (S-179): "My stats" — the own-stats entry point, styled/
          structured exactly like the admin-only link below (same bordered-
          row section, same plain-link button treatment), but unconditional:
          every account (guest or claimed, admin or not) can view its own
          stats, so unlike the admin link this renders for everyone. */}
      <section className="settings-screen__section">
        <button type="button" className="settings-screen__stats-link" onClick={onOpenStats}>
          My stats
        </button>
      </section>

      {isAdmin && (
        <section className="settings-screen__section">
          <button type="button" className="settings-screen__admin-link" onClick={onOpenAdmin}>
            Admin
          </button>
        </section>
      )}

      {/* REQ-716/ADR-0034: System/Light/Dark toggle — reuses
          .settings-screen__section's existing bordered-row treatment (same
          tokens as the admin-link/display-name rows), no new visual
          treatment. Placed ahead of the account-identity sections below
          since it's a device display preference, not an account setting. */}
      <section className="settings-screen__section settings-screen__section--appearance">
        <h3 className="settings-screen__section-title">Appearance</h3>
        <div className="settings-screen__theme-options" role="radiogroup" aria-label="Color theme">
          {THEME_OPTIONS.map((option) => (
            <label key={option.value} className="settings-screen__theme-option">
              <input
                type="radio"
                name="theme-preference"
                value={option.value}
                checked={themePreference === option.value}
                onChange={() => onThemePreferenceChange(option.value)}
              />
              <span>{option.label}</span>
            </label>
          ))}
        </div>
      </section>

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

      {/* REQ-722/S-182: the avatar upload/status section — reuses
          .settings-screen__section's existing bordered-row treatment plus
          the same field/error/success/submit-button pattern the
          display-name form above already established. Uploading and
          status-viewing are both here, since REQ-722 has no separate
          "review" surface for the player's own submissions the way
          REQ-517's admin queue does. */}
      <section className="settings-screen__section settings-screen__section--avatar">
        <h3 className="settings-screen__section-title">My avatar</h3>

        <form className="settings-screen__avatar-form" onSubmit={handleAvatarSubmit}>
          <label className="settings-screen__field">
            <span>Upload a new avatar</span>
            <input
              type="file"
              accept="image/jpeg,image/png,image/webp"
              ref={avatarFileInputRef}
              data-testid="avatar-section-upload-input"
              onChange={(event) => {
                setAvatarError(null);
                setAvatarSaved(false);
                setAvatarFile(event.target.files?.[0] ?? null);
              }}
              disabled={avatarSubmitting}
            />
          </label>

          {avatarError && (
            <p className="settings-screen__avatar-error" role="alert">
              {avatarError}
            </p>
          )}

          {avatarSaved && !avatarError && (
            <p className="settings-screen__avatar-success" role="status">
              Avatar submitted for review.
            </p>
          )}

          <button
            type="submit"
            className="settings-screen__avatar-submit"
            data-testid="avatar-section-upload-button"
            disabled={avatarSubmitting}
          >
            {avatarSubmitting ? 'Uploading…' : 'Upload avatar'}
          </button>
        </form>

        <div className="settings-screen__avatar-status">
          {avatarStatusError && (
            <p className="settings-screen__avatar-error" role="alert">
              {avatarStatusError}
            </p>
          )}

          {/* REQ-722: all three rows below are independent, not a single
              mutually-exclusive switch — a Rejected row never implies the
              Approved row is hidden, and vice versa (see
              AvatarStatusResponse's own doc comment in lib/types.ts). */}
          {avatarStatus?.pending && (
            <div className="settings-screen__avatar-status-row" data-testid="avatar-section-pending">
              <span className="settings-screen__avatar-status-label">Pending review</span>
              {pendingImageUrl && (
                <img
                  className="settings-screen__avatar-preview"
                  src={pendingImageUrl}
                  alt="Your pending avatar submission, awaiting admin review"
                  data-testid="avatar-section-pending-image"
                />
              )}
            </div>
          )}

          {avatarStatus?.rejected && (
            <div className="settings-screen__avatar-status-row" data-testid="avatar-section-rejected">
              <span className="settings-screen__avatar-status-label settings-screen__avatar-status-label--rejected">
                Rejected
              </span>
              {rejectedImageUrl && (
                <img
                  className="settings-screen__avatar-preview"
                  src={rejectedImageUrl}
                  alt="Your rejected avatar submission"
                  data-testid="avatar-section-rejected-image"
                />
              )}
            </div>
          )}

          {avatarStatus?.approved && (
            <div className="settings-screen__avatar-status-row" data-testid="avatar-section-approved">
              <span className="settings-screen__avatar-status-label">Currently visible to other players</span>
              {approvedImageUrl && (
                <img
                  className="settings-screen__avatar-preview"
                  src={approvedImageUrl}
                  alt="Your current avatar, visible to other players"
                  data-testid="avatar-section-approved-image"
                />
              )}
            </div>
          )}

          {avatarStatus &&
            !avatarStatus.pending &&
            !avatarStatus.rejected &&
            !avatarStatus.approved && (
              <p className="settings-screen__avatar-empty" data-testid="avatar-section-none">
                You haven&apos;t uploaded an avatar yet.
              </p>
            )}
        </div>
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
