import { useCallback } from 'react';
import { fetchAdminAccountMetrics } from '../lib/admin';
import { useAuthedFetch } from '../lib/useAuthedFetch';
import { GuestClearSection } from './GuestClearSection';

interface AccountMetricsSectionProps {
  accessToken: string;
  onAuthError: () => void;
}

// REQ-507 (metrics) / REQ-508 (bulk guest-clear). Rendered unconditionally by
// AdminScreen (see the render-site comment there) — never gated by the
// Non-Production-only activeRound probe RoundControlSection/
// UserDeletionSection share, since both REQs are explicitly Production-
// visible. Uses the shared useAuthedFetch hook for its fetch/error
// state, independently of AdminScreen's top-level PageState: a 401 here
// escalates via onAuthError like every other admin action in this file, but
// a 403 only hides this section (`hidden`) rather than flipping the whole
// page to access-denied — REQ-501/502/503's unverified-data fetch already
// owns that page-level decision, and in practice a 403 here for a genuinely
// non-admin caller can't happen without the unverified-data fetch (same
// "Admin" policy) having already 403'd and flipped the page first. Handled
// defensively anyway, per the explicit instruction not to rely on that
// ordering.
export function AccountMetricsSection({ accessToken, onAuthError }: AccountMetricsSectionProps) {
  const fetchFn = useCallback(() => fetchAdminAccountMetrics(accessToken), [accessToken]);
  const { data: metrics, hidden, loadError, refetch } = useAuthedFetch(fetchFn, { onAuthError });

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
