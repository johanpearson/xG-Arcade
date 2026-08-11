import { useCallback } from 'react';
import { fetchAdminXGPathCycle } from '../lib/api';
import { useAdminSectionFetch } from './useAdminSectionFetch';

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
export function XGPathCycleSection({ accessToken, onAuthError }: XGPathCycleSectionProps) {
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
