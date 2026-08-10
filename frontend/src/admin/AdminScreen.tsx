import { useCallback, useEffect, useState, type FormEvent } from 'react';
import {
  activateAnnouncementBanner,
  ApiError,
  approvePlayerData,
  clearGuestAccounts,
  closeAdminRound,
  createPlayerOverride,
  deactivateAnnouncementBanner,
  deleteUserByEmail,
  describeError,
  fetchActiveAdminRound,
  fetchAdminAccountMetrics,
  fetchAdminAnnouncementBanner,
  fetchAdminIncidentReports,
  fetchAdminXGPathCycle,
  fetchGuestAccountCount,
  fetchPendingSuggestions,
  fetchUnverifiedPlayerData,
  removePlayerData,
  updateAdminRoundEndTime,
  upsertAnnouncementBanner,
} from '../lib/api';
import type {
  AdminActiveRound,
  AdminAnnouncementBanner,
  ClearGuestAccountResult,
  UnverifiedPlayerData,
} from '../lib/types';
import { XG_GRID_GAME_KEY } from '../games/GameSelectScreen';
import './AdminScreen.css';

export interface AdminScreenProps {
  accessToken: string;
  onAuthError: () => void;
  // REQ-509/REQ-510 (S-090)/ADR-0053: the only entry point into
  // SuggestionsScreen — App.tsx wires this to navigateTo('admin-suggestions'),
  // mirroring how SettingsScreen's own onOpenAdmin link is this screen's own
  // entry point. Never a standalone top-level nav entry (ADR-0053).
  onOpenSuggestions: () => void;
}

type PageState =
  | { phase: 'loading' }
  | { phase: 'access-denied' }
  | { phase: 'error'; message: string }
  | { phase: 'ready' };

// SCREEN-04, REQ-504: the admin page S-012 deliberately deferred. Reached
// only via App.tsx's admin-only nav link (REQ-504's "no visible entry
// point" half); this component provides the other half — every underlying
// endpoint 403s a non-admin token directly, and the unverified-data fetch's
// own 403 is what flips this whole page to an access-denied message,
// independent of the nav-hiding.
export function AdminScreen({ accessToken, onAuthError, onOpenSuggestions }: AdminScreenProps) {
  const [pageState, setPageState] = useState<PageState>({ phase: 'loading' });
  const [unverifiedRows, setUnverifiedRows] = useState<UnverifiedPlayerData[]>([]);
  // null both while the round-control/user-deletion feature is genuinely
  // absent (404 probe) and before the first load resolves — pageState.phase
  // gates the "still loading" case, so by the time pageState is 'ready',
  // null here always means "hidden", never "not fetched yet".
  const [activeRound, setActiveRound] = useState<AdminActiveRound | null>(null);

  const refreshUnverified = useCallback(async () => {
    const rows = await fetchUnverifiedPlayerData(accessToken);
    setUnverifiedRows(rows);
  }, [accessToken]);

  const refreshActiveRound = useCallback(async () => {
    const probe = await fetchActiveAdminRound(accessToken, XG_GRID_GAME_KEY);
    setActiveRound(probe);
  }, [accessToken]);

  useEffect(() => {
    let cancelled = false;

    async function load() {
      const [unverifiedResult, activeRoundResult] = await Promise.allSettled([
        fetchUnverifiedPlayerData(accessToken),
        fetchActiveAdminRound(accessToken, XG_GRID_GAME_KEY),
      ]);
      if (cancelled) return;

      if (unverifiedResult.status === 'rejected') {
        const err = unverifiedResult.reason;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setPageState({ phase: 'access-denied' });
          return;
        }
        setPageState({ phase: 'error', message: describeError(err) });
        return;
      }
      setUnverifiedRows(unverifiedResult.value);

      if (activeRoundResult.status === 'rejected') {
        const err = activeRoundResult.reason;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setPageState({ phase: 'access-denied' });
          return;
        }
        // Non-fatal for the page as a whole — the round-control/user-deletion
        // sections just stay hidden, same as a genuine 404 probe result.
        setActiveRound(null);
      } else {
        setActiveRound(activeRoundResult.value);
      }

      setPageState({ phase: 'ready' });
    }

    load();

    return () => {
      cancelled = true;
    };
  }, [accessToken, onAuthError]);

  if (pageState.phase === 'loading') {
    return <p className="admin-screen__status">Loading…</p>;
  }

  if (pageState.phase === 'access-denied') {
    // REQ-504: the defense-in-depth half — reachable even if a non-admin
    // somehow lands on this screen directly, independent of App.tsx's
    // nav-hiding.
    return <p className="admin-screen__status">You don't have access to this page.</p>;
  }

  if (pageState.phase === 'error') {
    return <p className="admin-screen__status admin-screen__status--error">{pageState.message}</p>;
  }

  return (
    <div className="admin-screen">
      <h2 className="admin-screen__title">Admin</h2>

      {/* REQ-509/REQ-510 (S-090)/ADR-0053: the only entry point into
          SuggestionsScreen — a separate screen/file per that ADR, never
          folded into this one's sections below. Mirrors SettingsScreen's own
          "onOpenAdmin" link-out pattern, one level deeper. REQ-512 adds the
          pending-count badge shown alongside it. */}
      <PlayerSuggestionsEntry
        accessToken={accessToken}
        onAuthError={onAuthError}
        onOpenSuggestions={onOpenSuggestions}
      />

      {/* REQ-904/ADR-0066: the sibling "admin notification" entry point from
          the same S-096/S-097/S-098 grouping as PlayerSuggestionsEntry above
          — placed directly after it for that reason. Unlike
          PlayerSuggestionsEntry, there is no in-app screen to navigate to
          (ADR-0064's "no review queue" boundary), so this renders as a
          passive entry (heading + optional count + external link), not a
          button. */}
      <IncidentReportsEntry accessToken={accessToken} onAuthError={onAuthError} />

      {/* REQ-511: own fetch/state, same resilience pattern as
          AccountMetricsSection/XGPathCycleSection below — rendered
          unconditionally (this endpoint, like those, is registered in
          every environment), never gated by the Non-Production-only
          activeRound probe. */}
      <AnnouncementBannerSection accessToken={accessToken} onAuthError={onAuthError} />

      <UnverifiedDataSection
        accessToken={accessToken}
        rows={unverifiedRows}
        onAuthError={onAuthError}
        onRefresh={refreshUnverified}
      />

      {/* REQ-507/508: unlike RoundControlSection/UserDeletionSection below,
          this section is NOT gated by `activeRound !== null` — that gate
          exists only because the round-control/user-deletion probe 404s in
          Production (REQ-505/506's non-Production-only scope). REQ-507's
          metrics view and REQ-508's bulk guest-clear are both explicitly
          visible in every environment, including Production, so this section
          renders (and attempts its own fetch) unconditionally. */}
      <AccountMetricsSection accessToken={accessToken} onAuthError={onAuthError} />

      {/* REQ-1209: same "own fetch, own gating, never blocks or is blocked by
          any other admin section" pattern as AccountMetricsSection above —
          rendered unconditionally (this endpoint is registered in every
          environment, including Production, same as REQ-507/508's), not
          gated by the Non-Production-only `activeRound` probe below. */}
      <XGPathCycleSection accessToken={accessToken} onAuthError={onAuthError} />

      {activeRound !== null && (
        <>
          <RoundControlSection
            accessToken={accessToken}
            activeRound={activeRound}
            onAuthError={onAuthError}
            onRefresh={refreshActiveRound}
          />
          <UserDeletionSection accessToken={accessToken} onAuthError={onAuthError} />
        </>
      )}
    </div>
  );
}

interface UseAdminSectionFetchOptions {
  onAuthError: () => void;
}

interface UseAdminSectionFetchResult<T> {
  data: T | null;
  hidden: boolean;
  loadError: string | null;
  refetch: () => Promise<void>;
}

// Shared fetch/cancel/401/403/thrown-error shape used by every admin-screen
// section that owns its own independent fetch-on-mount. Originally
// duplicated four times (PlayerSuggestionsEntry/REQ-512,
// AnnouncementBannerSection/REQ-511, AccountMetricsSection/REQ-507,
// XGPathCycleSection/REQ-1209) and flagged as a rule-of-three-plus
// duplication candidate during REQ-512's quality gate; extracted once a
// fifth near-identical instance (IncidentReportsEntry/REQ-904) made it
// concrete. A 401 escalates via onAuthError; a 403 sets `hidden` (the
// caller decides what to do with that — usually returning null); any other
// thrown error is captured as `loadError`; unmount-during-fetch is guarded
// internally so no caller needs its own local `cancelled` flag. `refetch`
// re-runs fetchFn on demand (e.g. AccountMetricsSection passes it down as
// GuestClearSection's onCleared) and resolves once the resulting state
// update has been applied, matching what each caller's own hand-rolled
// refresh function used to do.
//
// Deliberately does NOT own any state that arises from a *successful*
// response — XGPathCycleSection's `hasData` and IncidentReportsEntry's
// `available` are both business-level "is there real data yet" distinctions
// that live inside `data` and are branched on by the caller, never inside
// this hook. Folding those in would conflate "the fetch itself
// succeeded/failed" (this hook's whole job) with "what the successful
// response means" (the caller's job) — see IncidentReportsEntry's own
// comment for why that distinction matters (REQ-904's `available: false`
// must never read as a thrown error or as a hidden section).
function useAdminSectionFetch<T>(
  fetchFn: () => Promise<T>,
  { onAuthError }: UseAdminSectionFetchOptions,
): UseAdminSectionFetchResult<T> {
  const [data, setData] = useState<T | null>(null);
  const [hidden, setHidden] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);

  const runFetch = useCallback(
    async (isCancelled: () => boolean) => {
      try {
        const result = await fetchFn();
        if (isCancelled()) return;
        setData(result);
        setLoadError(null);
      } catch (err) {
        if (isCancelled()) return;
        if (err instanceof ApiError && err.status === 401) {
          onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setHidden(true);
          return;
        }
        setLoadError(describeError(err));
      }
    },
    [fetchFn, onAuthError],
  );

  useEffect(() => {
    let cancelled = false;
    runFetch(() => cancelled);
    return () => {
      cancelled = true;
    };
  }, [runFetch]);

  const refetch = useCallback(() => runFetch(() => false), [runFetch]);

  return { data, hidden, loadError, refetch };
}

interface PlayerSuggestionsEntryProps {
  accessToken: string;
  onAuthError: () => void;
  onOpenSuggestions: () => void;
}

// REQ-512: the "Player suggestions" entry point's pending-count badge.
// Reuses REQ-509's existing GET /admin/suggestions data (fetchPendingSuggestions)
// — no new endpoint, no second data source. Uses the shared
// useAdminSectionFetch hook (same resilience pattern as
// AccountMetricsSection/XGPathCycleSection below): a 401 escalates via
// onAuthError, a 403 leaves the count absent silently (this section never
// erroring or flipping the whole page to access-denied — the button itself
// still works regardless, since SuggestionsScreen enforces its own access
// checks), and anything else (500, network failure, parse error, etc.) is
// surfaced inline via loadError rather than silently read as "nothing
// pending" — the one failure mode this badge can't afford, since its whole
// purpose is letting an admin trust it without opening the screen.
// Fetch-on-load only, per REQ-512's "no polling/websocket" scope: App.tsx's
// screen ternary unmounts AdminScreen while SuggestionsScreen is open and
// remounts it on the way back, so returning from resolving a suggestion
// there naturally re-triggers this fetch with no extra refresh plumbing.
// Renders the count as plain text next to the button label (e.g. "Player
// suggestions (3)"), the same convention UnverifiedDataSection's own
// "Unverified data (N)" heading already uses in this file — deliberately not
// a colored pill/badge, since design-document.md §2 has no token for one and
// this avoids introducing an ad-hoc color per CLAUDE.md's token rule.
function PlayerSuggestionsEntry({ accessToken, onAuthError, onOpenSuggestions }: PlayerSuggestionsEntryProps) {
  const fetchFn = useCallback(() => fetchPendingSuggestions(accessToken), [accessToken]);
  // `hidden` is deliberately unused here — a 403 just leaves `data` null,
  // the same way any other unfetched state does, and that alone already
  // produces REQ-512's "no badge/count shown" behavior. Unlike
  // AccountMetricsSection/XGPathCycleSection below, this section never hides
  // itself outright — the button must keep rendering regardless of the
  // fetch's outcome, since SuggestionsScreen enforces its own access checks.
  const { data, loadError } = useAdminSectionFetch(fetchFn, { onAuthError });
  const pendingCount = data ? data.length : null;

  return (
    <section className="admin-screen__section">
      <button type="button" onClick={onOpenSuggestions}>
        Player suggestions{pendingCount !== null && pendingCount > 0 ? ` (${pendingCount})` : ''}
      </button>
      {loadError && (
        <p className="admin-screen__error" role="alert">
          {loadError}
        </p>
      )}
    </section>
  );
}

// REQ-904/ADR-0064/ADR-0066: this repo's fixed, server-configured owner/repo/
// label (same values Program.cs's GitHubIncidentReportOptions defaults to,
// and the same ones the backend itself already writes issues to/reads issues
// from) — hard-coded here as a display-only link, never accepted as a prop
// or sourced from anything dynamic, matching REQ-904's "no client-supplied
// repo/label" rule and ADR-0064's "target repo and label are hard-coded
// server-side" boundary. This is not a request parameter to any endpoint, so
// hard-coding a second copy on the frontend doesn't violate that boundary —
// it's just where GitHub's own filtered issue list already lives.
const INCIDENT_REPORTS_GITHUB_URL =
  'https://github.com/johanpearson/xg-arcade/issues?q=is%3Aissue+is%3Aopen+label%3Auser-reported';

interface IncidentReportsEntryProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-904/ADR-0066 (S-098): fetch-on-load only (no polling/websocket —
// REQ-904's own freshness model), using the shared useAdminSectionFetch hook
// for the transport half (401/403/thrown-error/cancel). Three renderable
// states, not PlayerSuggestionsEntry's two above, because a GitHub-poll
// failure (`available: false`) is a real, distinct failure/unknown state —
// never conflated with "you're not an admin" (403, handled identically to
// AccountMetricsSection/XGPathCycleSection's own hide-quietly pattern below,
// since this section — unlike PlayerSuggestionsEntry's button — has no
// separately-gated destination screen to fall back on) and never conflated
// with a genuine zero count. A 401 escalates via onAuthError; a 403 hides
// this section only; a GitHub-poll failure (`available: false` in a normal
// 200 body, per ADR-0066 — never a thrown error) renders a distinct inline
// message, branched on locally rather than inside the hook (see
// useAdminSectionFetch's own doc comment for why); any other failure (500,
// network, parse) also renders inline rather than silently reading as
// "nothing open", the one failure mode this entry point can't afford per
// REQ-904's "never a false zero-count" rule. Renders the count next to the
// heading the same way UnverifiedDataSection's own "Unverified data (N)"
// heading does, except the count itself is omitted entirely at zero
// (REQ-904/REQ-512's shared "absence, not '0'" convention) rather than
// always shown.
function IncidentReportsEntry({ accessToken, onAuthError }: IncidentReportsEntryProps) {
  const fetchFn = useCallback(() => fetchAdminIncidentReports(accessToken), [accessToken]);
  const { data, hidden, loadError } = useAdminSectionFetch(fetchFn, { onAuthError });

  if (hidden) return null;

  // ADR-0066: `available: false` is a business-level state carried inside a
  // normal 200 response body, not a thrown error, so it's branched on here
  // rather than inside useAdminSectionFetch (which only owns transport-level
  // states). Never rendered as openCount: 0.
  const openCount = data && data.available ? data.openCount : null;
  const unavailable = data !== null && !data.available;

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">
        Incident reports{openCount !== null && openCount > 0 ? ` (${openCount})` : ''}
      </h3>
      {openCount !== null && openCount > 0 && (
        <a
          className="admin-screen__link"
          href={INCIDENT_REPORTS_GITHUB_URL}
          target="_blank"
          rel="noreferrer"
        >
          View open reports on GitHub
        </a>
      )}
      {unavailable && (
        <p className="admin-screen__error" role="alert">
          Couldn't check GitHub for open incident reports right now — this doesn't mean there are none, try
          reloading in a minute.
        </p>
      )}
      {loadError && (
        <p className="admin-screen__error" role="alert">
          {loadError}
        </p>
      )}
    </section>
  );
}

interface AnnouncementBannerSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-511 (SCREEN-04): the admin-only create/edit/activate/deactivate
// section for the site-wide announcement banner — `App.tsx`'s own
// `AnnouncementBanner` component is the public, read-only half every
// visitor sees. Built as an inline section here (like
// AccountMetricsSection/XGPathCycleSection below), not a separate linked
// screen like SuggestionsScreen — a single message field plus two
// activate/deactivate buttons doesn't warrant its own screen/nav hop the
// way REQ-509/510's multi-row suggestion review queue does (ADR-0053).
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
function AnnouncementBannerSection({ accessToken, onAuthError }: AnnouncementBannerSectionProps) {
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

interface UnverifiedDataSectionProps {
  accessToken: string;
  rows: UnverifiedPlayerData[];
  onAuthError: () => void;
  onRefresh: () => Promise<void>;
}

// REQ-501/502/503 (SCREEN-04): "Correct" (creates a PlayerOverride, requires
// a reason), "Approve"/"Approve selected" (flips confidence to "verified"
// in bulk, including select-all, no reason field), and "Remove selected"
// (hard-deletes the selected rows in bulk, also no reason field — REQ-503's
// 2026-07-20 "remove" extension, a sibling of "approve" in every respect
// except what it does to the row) are all real backend actions.
function UnverifiedDataSection({ accessToken, rows, onAuthError, onRefresh }: UnverifiedDataSectionProps) {
  const [openRowId, setOpenRowId] = useState<string | null>(null);
  const [value, setValue] = useState('');
  const [reason, setReason] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [approving, setApproving] = useState(false);
  const [approveError, setApproveError] = useState<string | null>(null);
  const [approvalResults, setApprovalResults] = useState<RowApprovalResult[] | null>(null);

  const [removing, setRemoving] = useState(false);
  const [removeError, setRemoveError] = useState<string | null>(null);
  const [removalResults, setRemovalResults] = useState<RowRemovalResult[] | null>(null);

  // Drops any selected id that's no longer in the current row list (e.g.
  // after a refetch removes an approved row) — otherwise a stale id could
  // sit selected-but-invisible and get resubmitted on the next approve.
  useEffect(() => {
    setSelectedIds((prev) => {
      const rowIds = new Set(rows.map((row) => row.id));
      const filtered = new Set([...prev].filter((id) => rowIds.has(id)));
      return filtered.size === prev.size ? prev : filtered;
    });
  }, [rows]);

  function openCorrection(row: UnverifiedPlayerData) {
    setOpenRowId(row.id);
    setValue(row.value);
    setReason('');
    setError(null);
  }

  function closeCorrection() {
    setOpenRowId(null);
    setError(null);
  }

  async function handleSubmit(event: FormEvent, row: UnverifiedPlayerData) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await createPlayerOverride(accessToken, row.playerId, row.field, value, reason);
      setOpenRowId(null);
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      // REQ-501: a 409 (override already exists for this playerId/field)
      // surfaces here via its own detail text — there's no "edit an
      // existing override" UI to route to instead (S-012 never built a
      // browsable override list).
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  function toggleRowSelected(id: string) {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  const allSelected = rows.length > 0 && selectedIds.size === rows.length;

  function toggleSelectAll() {
    setSelectedIds(allSelected ? new Set() : new Set(rows.map((row) => row.id)));
  }

  // REQ-503 (2026-07-20 extension): submits the whole selection as one bulk
  // call — a single selected row is just the N=1 case of the same action,
  // not a separate code path. Always shows a per-row outcome afterward
  // (never reads as a full success or a full failure when it's actually a
  // partial one), and always refetches the list the same way "Correct"'s
  // successful submit already does above, regardless of whether every row
  // in the selection succeeded.
  async function handleApprove() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    setApproving(true);
    setApproveError(null);
    try {
      const rowsById = new Map(rows.map((row) => [row.id, row]));
      const response = await approvePlayerData(accessToken, ids);
      const results = response.results.map((result) => {
        const row = rowsById.get(result.playerDataId);
        return {
          id: result.playerDataId,
          summary: row
            ? `${row.playerFullName} · ${row.field} · ${row.value}`
            : result.playerDataId,
          approved: result.approved,
          failureReason: result.failureReason,
        };
      });
      setApprovalResults(results);
      setSelectedIds(new Set());
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setApproveError(describeError(err));
    } finally {
      setApproving(false);
    }
  }

  // REQ-503 (2026-07-20 extension): sibling to handleApprove above in every
  // respect except which endpoint it calls and that removal has only one
  // failure reason ("NotFound" — see describeRemovalFailure). Same bulk
  // semantics: a single selected row is just the N=1 case, always shows a
  // per-row outcome, and always refetches the list afterward regardless of
  // whether every row in the selection succeeded.
  async function handleRemove() {
    const ids = Array.from(selectedIds);
    if (ids.length === 0) return;

    setRemoving(true);
    setRemoveError(null);
    try {
      const rowsById = new Map(rows.map((row) => [row.id, row]));
      const response = await removePlayerData(accessToken, ids);
      const results = response.results.map((result) => {
        const row = rowsById.get(result.playerDataId);
        return {
          id: result.playerDataId,
          summary: row
            ? `${row.playerFullName} · ${row.field} · ${row.value}`
            : result.playerDataId,
          removed: result.removed,
          failureReason: result.failureReason,
        };
      });
      setRemovalResults(results);
      setSelectedIds(new Set());
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setRemoveError(describeError(err));
    } finally {
      setRemoving(false);
    }
  }

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Unverified data ({rows.length})</h3>
      {rows.length === 0 ? (
        <p className="admin-screen__empty">No unverified data to review.</p>
      ) : (
        <>
          <div className="admin-screen__bulk-bar">
            <label className="admin-screen__checkbox">
              <input
                type="checkbox"
                checked={allSelected}
                onChange={toggleSelectAll}
                disabled={approving || removing}
              />
              <span>Select all</span>
            </label>
            <span className="admin-screen__selected-count">{selectedIds.size} selected</span>
            <button type="button" onClick={handleApprove} disabled={selectedIds.size === 0 || approving || removing}>
              {approving ? 'Approving…' : 'Approve selected'}
            </button>
            <button type="button" onClick={handleRemove} disabled={selectedIds.size === 0 || approving || removing}>
              {removing ? 'Removing…' : 'Remove selected'}
            </button>
          </div>

          {approveError && (
            <p className="admin-screen__error" role="alert">
              {approveError}
            </p>
          )}

          {removeError && (
            <p className="admin-screen__error" role="alert">
              {removeError}
            </p>
          )}

          {approvalResults && (
            <div className="admin-screen__approval-results">
              <ul className="admin-screen__list">
                {approvalResults.map((result) => (
                  <li
                    key={result.id}
                    className={
                      result.approved
                        ? 'admin-screen__approval-result'
                        : 'admin-screen__approval-result admin-screen__approval-result--failed'
                    }
                  >
                    {result.summary} — {result.approved ? 'Approved.' : describeApprovalFailure(result.failureReason)}
                  </li>
                ))}
              </ul>
              <button type="button" onClick={() => setApprovalResults(null)}>
                Dismiss
              </button>
            </div>
          )}

          {removalResults && (
            <div className="admin-screen__approval-results">
              <ul className="admin-screen__list">
                {removalResults.map((result) => (
                  <li
                    key={result.id}
                    className={
                      result.removed
                        ? 'admin-screen__approval-result'
                        : 'admin-screen__approval-result admin-screen__approval-result--failed'
                    }
                  >
                    {result.summary} — {result.removed ? 'Removed.' : describeRemovalFailure(result.failureReason)}
                  </li>
                ))}
              </ul>
              <button type="button" onClick={() => setRemovalResults(null)}>
                Dismiss
              </button>
            </div>
          )}

          <ul className="admin-screen__list">
            {rows.map((row) => (
              <li key={row.id} className="admin-screen__row">
                <label className="admin-screen__checkbox">
                  <input
                    type="checkbox"
                    checked={selectedIds.has(row.id)}
                    onChange={() => toggleRowSelected(row.id)}
                    disabled={approving || removing}
                    aria-label={`Select ${row.playerFullName} · ${row.field} · ${row.value}`}
                  />
                  <span className="admin-screen__row-summary">
                    {row.playerFullName} · {row.field} · {row.value} · {row.source}
                  </span>
                </label>
                {openRowId === row.id ? (
                  <form className="admin-screen__inline-form" onSubmit={(event) => handleSubmit(event, row)}>
                    <label className="admin-screen__field">
                      <span>Value</span>
                      <input
                        type="text"
                        required
                        value={value}
                        onChange={(event) => setValue(event.target.value)}
                        disabled={submitting}
                      />
                    </label>
                    <label className="admin-screen__field">
                      <span>Reason</span>
                      <input
                        type="text"
                        required
                        value={reason}
                        onChange={(event) => setReason(event.target.value)}
                        disabled={submitting}
                      />
                    </label>
                    {error && (
                      <p className="admin-screen__error" role="alert">
                        {error}
                      </p>
                    )}
                    <div className="admin-screen__inline-form-actions">
                      <button type="button" onClick={closeCorrection} disabled={submitting}>
                        Cancel
                      </button>
                      <button type="submit" disabled={submitting}>
                        {submitting ? 'Saving…' : 'Save correction'}
                      </button>
                    </div>
                  </form>
                ) : (
                  <button type="button" onClick={() => openCorrection(row)}>
                    Correct
                  </button>
                )}
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

interface RowApprovalResult {
  id: string;
  summary: string;
  approved: boolean;
  failureReason: string | null;
}

// REQ-503 (2026-07-20 extension): turns the backend's two known
// `failureReason` values into copy that states what happened, per
// design-document.md §5 — never a generic "failed" with no explanation, and
// never the raw enum string shown to an admin as-is.
function describeApprovalFailure(failureReason: string | null): string {
  switch (failureReason) {
    case 'NotFound':
      return 'Not approved — this row no longer exists.';
    case 'NotUnverified':
      return 'Not approved — already reviewed by someone else.';
    default:
      return 'Not approved.';
  }
}

interface RowRemovalResult {
  id: string;
  summary: string;
  removed: boolean;
  failureReason: string | null;
}

// REQ-503 (2026-07-20 extension): sibling to describeApprovalFailure above —
// removal has only one known `failureReason` ("NotFound", since removing a
// row has no "must still be unverified" precondition the way approving
// does), but keeps the same never-a-raw-enum-string, never-a-bare-"failed"
// shape for consistency and to degrade gracefully if the backend ever adds
// a new reason.
function describeRemovalFailure(failureReason: string | null): string {
  switch (failureReason) {
    case 'NotFound':
      return 'Not removed — this row no longer exists.';
    default:
      return 'Not removed.';
  }
}

interface RoundControlSectionProps {
  accessToken: string;
  activeRound: AdminActiveRound;
  onAuthError: () => void;
  onRefresh: () => Promise<void>;
}

// REQ-505: rendered only when the round-control/user-deletion probe found
// the feature present (AdminScreen's `activeRound !== null` gate) — never
// disabled-but-visible in Production, since the probe itself 404s there.
function RoundControlSection({ accessToken, activeRound, onAuthError, onRefresh }: RoundControlSectionProps) {
  const [confirmingEnd, setConfirmingEnd] = useState(false);
  const [ending, setEnding] = useState(false);
  const [endError, setEndError] = useState<string | null>(null);

  const [newEndTime, setNewEndTime] = useState('');
  const [updating, setUpdating] = useState(false);
  const [updateError, setUpdateError] = useState<string | null>(null);

  async function handleEndRoundConfirmed() {
    setEnding(true);
    setEndError(null);
    try {
      await closeAdminRound(accessToken, XG_GRID_GAME_KEY);
      setConfirmingEnd(false);
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setEndError(describeError(err));
    } finally {
      setEnding(false);
    }
  }

  async function handleUpdateEndTime(event: FormEvent) {
    event.preventDefault();
    if (!newEndTime) return;
    setUpdating(true);
    setUpdateError(null);
    try {
      const endTimeIso = new Date(newEndTime).toISOString();
      await updateAdminRoundEndTime(accessToken, XG_GRID_GAME_KEY, endTimeIso);
      setNewEndTime('');
      await onRefresh();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setUpdateError(describeError(err));
    } finally {
      setUpdating(false);
    }
  }

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Round control — {XG_GRID_GAME_KEY}</h3>
      {activeRound.hasActiveRound && activeRound.round ? (
        <p className="admin-screen__row-summary">
          Round {activeRound.round.roundId} · ends {activeRound.round.endTime}
        </p>
      ) : (
        <p className="admin-screen__empty">No active round right now.</p>
      )}

      {activeRound.hasActiveRound && (
        <div className="admin-screen__action-group">
          {confirmingEnd ? (
            <div className="admin-screen__confirm-row">
              <button type="button" onClick={handleEndRoundConfirmed} disabled={ending}>
                {ending ? 'Ending…' : 'Yes, end round now'}
              </button>
              <button type="button" onClick={() => setConfirmingEnd(false)} disabled={ending}>
                Cancel
              </button>
            </div>
          ) : (
            <button type="button" onClick={() => setConfirmingEnd(true)}>
              End round now
            </button>
          )}
          {endError && (
            <p className="admin-screen__error" role="alert">
              {endError}
            </p>
          )}
        </div>
      )}

      <form className="admin-screen__inline-form" onSubmit={handleUpdateEndTime}>
        <label className="admin-screen__field">
          <span>New end time</span>
          <input
            type="datetime-local"
            required
            value={newEndTime}
            onChange={(event) => setNewEndTime(event.target.value)}
            disabled={updating}
          />
        </label>
        {updateError && (
          <p className="admin-screen__error" role="alert">
            {updateError}
          </p>
        )}
        <button type="submit" disabled={updating}>
          {updating ? 'Updating…' : 'Update end time'}
        </button>
      </form>
    </section>
  );
}

interface UserDeletionSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-506: same visibility gate as RoundControlSection above (both are
// hidden together by AdminScreen's activeRound !== null check, since they
// share the same Production environment gate server-side).
function UserDeletionSection({ accessToken, onAuthError }: UserDeletionSectionProps) {
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

interface AccountMetricsSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-507 (metrics) / REQ-508 (bulk guest-clear). Rendered unconditionally by
// AdminScreen (see the render-site comment above) — never gated by the
// Non-Production-only activeRound probe RoundControlSection/
// UserDeletionSection share, since both REQs are explicitly Production-
// visible. Uses the shared useAdminSectionFetch hook for its fetch/error
// state, independently of AdminScreen's top-level PageState: a 401 here
// escalates via onAuthError like every other admin action in this file, but
// a 403 only hides this section (`hidden`) rather than flipping the whole
// page to access-denied — REQ-501/502/503's unverified-data fetch already
// owns that page-level decision, and in practice a 403 here for a genuinely
// non-admin caller can't happen without the unverified-data fetch (same
// "Admin" policy) having already 403'd and flipped the page first. Handled
// defensively anyway, per the explicit instruction not to rely on that
// ordering.
function AccountMetricsSection({ accessToken, onAuthError }: AccountMetricsSectionProps) {
  const fetchFn = useCallback(() => fetchAdminAccountMetrics(accessToken), [accessToken]);
  const { data: metrics, hidden, loadError, refetch } = useAdminSectionFetch(fetchFn, { onAuthError });

  if (hidden) return null;

  return (
    <>
      <section className="admin-screen__section">
        <h3 className="admin-screen__section-title">Accounts</h3>
        {loadError && (
          <p className="admin-screen__error" role="alert">
            {loadError}
          </p>
        )}
        {metrics ? (
          <dl className="admin-screen__metrics">
            <div className="admin-screen__metric">
              <dt className="admin-screen__metric-label">Total users</dt>
              <dd className="admin-screen__metric-value mono-figure">{metrics.totalUserCount}</dd>
            </div>
            <div className="admin-screen__metric">
              <dt className="admin-screen__metric-label">Current guests</dt>
              <dd className="admin-screen__metric-value mono-figure">{metrics.currentGuestCount}</dd>
            </div>
            <div className="admin-screen__metric">
              <dt className="admin-screen__metric-label">Claimed guests</dt>
              <dd className="admin-screen__metric-value mono-figure">{metrics.claimedGuestCount}</dd>
            </div>
          </dl>
        ) : (
          !loadError && <p className="admin-screen__empty">Loading account metrics…</p>
        )}
      </section>

      <GuestClearSection accessToken={accessToken} onAuthError={onAuthError} onCleared={refetch} />
    </>
  );
}

interface GuestClearSectionProps {
  accessToken: string;
  onAuthError: () => void;
  onCleared: () => Promise<void>;
}

type GuestClearPhase =
  | { phase: 'idle' }
  | { phase: 'counting' }
  | { phase: 'confirming'; count: number }
  | { phase: 'clearing'; count: number };

// REQ-508: the bulk force-clear-guests action — a stronger two-step confirm
// than RoundControlSection/UserDeletionSection's own ("Yes, end round now" /
// "Yes, delete this user permanently"), since here the confirm step must
// itself show the dry-run count so the admin confirms a known, specific
// number of accounts, not an open-ended action. Reports a per-account
// outcome afterward, same "never a single pass/fail for the whole batch"
// discipline UnverifiedDataSection's bulk approve/remove already establishes
// above.
function GuestClearSection({ accessToken, onAuthError, onCleared }: GuestClearSectionProps) {
  const [phase, setPhase] = useState<GuestClearPhase>({ phase: 'idle' });
  const [clearError, setClearError] = useState<string | null>(null);
  const [zeroGuestsMessage, setZeroGuestsMessage] = useState<string | null>(null);
  const [results, setResults] = useState<ClearGuestAccountResult[] | null>(null);

  async function handleForceClearClick() {
    setClearError(null);
    setZeroGuestsMessage(null);
    setPhase({ phase: 'counting' });
    try {
      const count = await fetchGuestAccountCount(accessToken);
      if (count === 0) {
        // Nothing to confirm — showing "Yes, delete all 0 guest accounts"
        // would be an odd, actionable-looking prompt for an action that
        // would do nothing.
        setZeroGuestsMessage('No guest accounts to clear right now.');
        setPhase({ phase: 'idle' });
        return;
      }
      setPhase({ phase: 'confirming', count });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setClearError(describeError(err));
      setPhase({ phase: 'idle' });
    }
  }

  function handleCancelClear() {
    setPhase({ phase: 'idle' });
    setClearError(null);
  }

  async function handleConfirmClear(count: number) {
    setPhase({ phase: 'clearing', count });
    setClearError(null);
    try {
      const response = await clearGuestAccounts(accessToken);
      setResults(response.results);
      setPhase({ phase: 'idle' });
      await onCleared();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      setClearError(describeError(err));
      setPhase({ phase: 'confirming', count });
    }
  }

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">Guest accounts</h3>
      <p className="admin-screen__empty">
        Deletes every current guest account immediately — a manual remedy you can use any time, separate from the
        scheduled automatic purge.
      </p>

      {clearError && (
        <p className="admin-screen__error" role="alert">
          {clearError}
        </p>
      )}

      {zeroGuestsMessage && <p className="admin-screen__empty">{zeroGuestsMessage}</p>}

      {results && (
        <div className="admin-screen__approval-results">
          <ul className="admin-screen__list">
            {results.map((result) => (
              <li
                key={result.userId}
                className={
                  result.outcome === 'Succeeded'
                    ? 'admin-screen__approval-result'
                    : 'admin-screen__approval-result admin-screen__approval-result--failed'
                }
              >
                {result.userId} — {describeGuestClearOutcome(result)}
              </li>
            ))}
          </ul>
          <button type="button" onClick={() => setResults(null)}>
            Dismiss
          </button>
        </div>
      )}

      <div className="admin-screen__action-group">
        {phase.phase === 'confirming' || phase.phase === 'clearing' ? (
          <div className="admin-screen__confirm-row">
            <button
              type="button"
              onClick={() => handleConfirmClear(phase.count)}
              disabled={phase.phase === 'clearing'}
            >
              {phase.phase === 'clearing'
                ? 'Clearing…'
                : `Yes, delete all ${phase.count} guest account${phase.count === 1 ? '' : 's'}`}
            </button>
            <button type="button" onClick={handleCancelClear} disabled={phase.phase === 'clearing'}>
              Cancel
            </button>
          </div>
        ) : (
          <button type="button" onClick={handleForceClearClick} disabled={phase.phase === 'counting'}>
            {phase.phase === 'counting' ? 'Checking…' : 'Force clear guests'}
          </button>
        )}
      </div>
    </section>
  );
}

// REQ-508: turns the backend's three known `outcome` values into copy that
// states what happened, per design-document.md §5 — never a generic
// "failed" with no explanation, and never the raw enum string shown to an
// admin as-is. Mirrors describeApprovalFailure/describeRemovalFailure above,
// but for a three-outcome (not two-outcome, success-implied-by-absence)
// shape.
function describeGuestClearOutcome(result: ClearGuestAccountResult): string {
  switch (result.outcome) {
    case 'Succeeded':
      return 'Cleared.';
    case 'NotFound':
      return 'Not cleared — this account no longer exists.';
    case 'Failed':
      return result.errorMessage ? `Not cleared — ${result.errorMessage}` : 'Not cleared.';
    default:
      return 'Not cleared.';
  }
}

interface XGPathCycleSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-1209/ADR-0058: read-only visibility into xG Path's REQ-1208
// target-selection cycle state, mirroring AccountMetricsSection's shape
// exactly (both built on the shared useAdminSectionFetch hook, independent
// of AdminScreen's top-level PageState) — a 401 escalates via onAuthError
// like every other admin action in this file, a 403 only hides this
// section, and any other error shows inline rather than failing the whole
// page. Rendered unconditionally by AdminScreen (see the render-site
// comment there), so its fetch/render never blocks, and is never blocked
// by, any other admin section's state.
function XGPathCycleSection({ accessToken, onAuthError }: XGPathCycleSectionProps) {
  const fetchFn = useCallback(() => fetchAdminXGPathCycle(accessToken), [accessToken]);
  const { data: cycleState, hidden, loadError } = useAdminSectionFetch(fetchFn, { onAuthError });

  if (hidden) return null;

  return (
    <section className="admin-screen__section">
      <h3 className="admin-screen__section-title">xG Path target cycle</h3>
      {loadError && (
        <p className="admin-screen__error" role="alert">
          {loadError}
        </p>
      )}
      {!loadError && cycleState === null && (
        <p className="admin-screen__empty">Loading xG Path cycle status…</p>
      )}
      {!loadError && cycleState !== null && !cycleState.hasData && (
        // REQ-1209: "no xG Path round has ever generated yet" — a clear
        // no-data state, never an error and never a blank section.
        <p className="admin-screen__empty">No xG Path round has generated yet — no cycle data to show.</p>
      )}
      {!loadError && cycleState !== null && cycleState.hasData && (
        <dl className="admin-screen__metrics">
          <div className="admin-screen__metric">
            <dt className="admin-screen__metric-label">Current cycle</dt>
            <dd className="admin-screen__metric-value mono-figure">{cycleState.cycleNumber}</dd>
          </div>
          <div className="admin-screen__metric">
            <dt className="admin-screen__metric-label">Eligible pool size (as of last generation)</dt>
            <dd className="admin-screen__metric-value mono-figure">{cycleState.observedPoolSize}</dd>
          </div>
          <div className="admin-screen__metric">
            <dt className="admin-screen__metric-label">Used this cycle</dt>
            <dd className="admin-screen__metric-value mono-figure">{cycleState.usedInCycleCount}</dd>
          </div>
          <div className="admin-screen__metric">
            <dt className="admin-screen__metric-label">Remaining this cycle</dt>
            <dd className="admin-screen__metric-value mono-figure">{cycleState.remainingInCycleCount}</dd>
          </div>
          <div className="admin-screen__metric">
            <dt className="admin-screen__metric-label">Last cycle completed</dt>
            <dd className="admin-screen__metric-value">
              {cycleState.lastCycleCompletedAt ?? 'No cycle has completed yet'}
            </dd>
          </div>
        </dl>
      )}
    </section>
  );
}
