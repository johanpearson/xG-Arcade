import { useCallback, useEffect, useState, type FormEvent } from 'react';
import {
  activateAnnouncementBanner,
  ApiError,
  deactivateAnnouncementBanner,
  describeError,
  fetchAdminAnnouncementBanner,
  upsertAnnouncementBanner,
} from '../lib/api';
import type { AdminAnnouncementBanner } from '../lib/types';
import { useAdminSectionFetch } from './useAdminSectionFetch';

interface AnnouncementBannerSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-511 (SCREEN-04): the admin-only create/edit/activate/deactivate
// section for the site-wide announcement banner — `App.tsx`'s own
// `AnnouncementBanner` component is the public, read-only half every
// visitor sees. Built as an inline section here (like
// AccountMetricsSection/XGPathCycleSection), not a separate linked screen
// like SuggestionsScreen — a single message field plus two activate/
// deactivate buttons doesn't warrant its own screen/nav hop the way
// REQ-509/510's multi-row suggestion review queue does (ADR-0053).
// Uses the shared useAdminSectionFetch hook for its fetch/cancel/401/403/
// error handling, same resilience pattern as AccountMetricsSection/
// XGPathCycleSection: a 401 escalates via onAuthError, a 403 hides this
// section only, and any other load failure shows inline — never blocking
// or blocked by any other admin section. "No banner has ever been created
// yet" (the GET's own 404, surfaced as `{ banner: null }` by fetchFn below)
// is a distinct state from "a banner exists but is currently inactive":
// `banner?.isActive` carries that second distinction, so the two are never
// collapsed the way the public GET /announcement-banner response
// deliberately does for a visitor.
export function AnnouncementBannerSection({ accessToken, onAuthError }: AnnouncementBannerSectionProps) {
  const fetchFn = useCallback(async () => {
    const banner = await fetchAdminAnnouncementBanner(accessToken);
    // Wrapped in a container so `data` can distinguish "not loaded yet"
    // (null) from "loaded, no banner exists yet" (a non-null container
    // holding a null banner) — useAdminSectionFetch's `data: T | null`
    // can't otherwise tell those apart when T itself is nullable.
    return { banner };
  }, [accessToken]);
  const { data, hidden, loadError } = useAdminSectionFetch(fetchFn, { onAuthError });

  // Save/activate/deactivate write their response straight into this local
  // override instead of triggering a second fetch — same no-round-trip
  // behavior as before this hook extraction. `undefined` means "no mutation
  // has happened yet, defer to the hook's own fetched value."
  const [savedBanner, setSavedBanner] = useState<AdminAnnouncementBanner | null | undefined>(undefined);
  const banner = savedBanner !== undefined ? savedBanner : data ? data.banner : null;
  const loading = data === null && !hidden && !loadError;

  const [messageInput, setMessageInput] = useState('');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [togglingActive, setTogglingActive] = useState(false);
  const [toggleError, setToggleError] = useState<string | null>(null);

  useEffect(() => {
    setMessageInput(banner ? banner.message : '');
  }, [banner]);

  // REQ-511: "a blank/empty message is rejected with a validation error
  // and does not change the stored banner" — the server is the actual
  // guard (400 on blank/whitespace-only); this client-side check just
  // keeps the submit button from firing an obviously-empty request, same
  // "defense in depth, not the primary guard" convention as
  // lookupPlayerByName's own blank-name check.
  async function handleSave(event: FormEvent) {
    event.preventDefault();
    if (!messageInput.trim()) return;
    setSaving(true);
    setSaveError(null);
    try {
      const saved = await upsertAnnouncementBanner(accessToken, messageInput);
      setSavedBanner(saved);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setSaveError(describeError(err));
    } finally {
      setSaving(false);
    }
  }

  async function handleToggleActive(nextActive: boolean) {
    setTogglingActive(true);
    setToggleError(null);
    try {
      const saved = nextActive
        ? await activateAnnouncementBanner(accessToken)
        : await deactivateAnnouncementBanner(accessToken);
      setSavedBanner(saved);
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setToggleError(describeError(err));
    } finally {
      setTogglingActive(false);
    }
  }

  if (hidden) return null;

  const formDisabled = saving || loading;

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Site-wide announcement banner</h3>

      {loading && <p className="admin-screen__empty">Loading…</p>}
      {loadError && (
        <p className="admin-screen__error" role="alert">
          {loadError}
        </p>
      )}
      {!loading && !loadError && banner === null && (
        <p className="admin-screen__empty">No banner has been created yet — write one below.</p>
      )}
      {banner && (
        <p className="admin-screen__row-summary">
          Status: {banner.isActive ? 'Active — visible to every visitor' : 'Inactive — not shown to visitors'}
        </p>
      )}

      <form className="admin-screen__inline-form" onSubmit={handleSave}>
        <label className="admin-screen__field">
          <span>Message</span>
          <textarea
            required
            maxLength={500}
            rows={3}
            value={messageInput}
            onChange={(event) => setMessageInput(event.target.value)}
            disabled={formDisabled}
          />
        </label>
        {saveError && (
          <p className="admin-screen__error" role="alert">
            {saveError}
          </p>
        )}
        <button type="submit" disabled={formDisabled || !messageInput.trim()}>
          {saving ? 'Saving…' : banner ? 'Save changes' : 'Create banner'}
        </button>
      </form>

      {banner && (
        <div className="admin-screen__action-group">
          {toggleError && (
            <p className="admin-screen__error" role="alert">
              {toggleError}
            </p>
          )}
          {banner.isActive ? (
            <button type="button" onClick={() => handleToggleActive(false)} disabled={togglingActive}>
              {togglingActive ? 'Deactivating…' : 'Deactivate'}
            </button>
          ) : (
            <button type="button" onClick={() => handleToggleActive(true)} disabled={togglingActive}>
              {togglingActive ? 'Activating…' : 'Activate'}
            </button>
          )}
        </div>
      )}
    </section>
  );
}
