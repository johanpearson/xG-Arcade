import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError, describeError, reportIncident } from '../lib/api';
import { INCIDENT_REPORT_DESCRIPTION_PLACEHOLDER, INCIDENT_REPORT_GUEST_LOCKED_COPY, INCIDENT_REPORT_SUBMITTED_COPY } from '../lib/incidentReportCopy';
import './IncidentReportDialog.css';

// REQ-903/ADR-0064: matches IncidentEndpoints.DescriptionMaxLength on the
// backend — client-side enforcement is defense in depth, not the primary
// guard (the server re-checks this regardless of what the client sends).
const INCIDENT_REPORT_DESCRIPTION_MAX_LENGTH = 4000;

export interface IncidentReportDialogProps {
  accessToken: string;
  // REQ-903's "advertised, not hidden" guest rule (REQ-215's own
  // precedent): the dialog still opens for a guest, showing the
  // guest-locked copy with the form disabled, rather than the footer entry
  // point simply not existing for one.
  isGuest: boolean;
  // The current screen (App.tsx's own Screen union, passed through as a
  // plain string) at the moment this was opened — optional triage context
  // only (REQ-903), never trusted for anything beyond display in the
  // created issue's body.
  route: string;
  onClose: () => void;
  onAuthError: () => void;
}

// REQ-903/ADR-0064 (moved 2026-08-10, same day as the original build): a
// footer-triggered modal (App.tsx's "Report a problem" button, always in
// the footer, so this is reachable from whatever screen a player is
// actually looking at when something goes wrong) — previously a section
// embedded in SettingsScreen.tsx only, which meant navigating away from
// whatever was broken before it could be reported. Structural/
// accessibility pattern taken from ScoringExplainer.tsx (SCREEN-06):
// role="dialog", aria-modal, backdrop-click-to-close, Escape-to-close,
// header with an "×" close button, focus-in-on-open/focus-return-to-
// opener-on-close. Tokens only — no new color, typeface, or animation.
export function IncidentReportDialog({ accessToken, isGuest, route, onClose, onAuthError }: IncidentReportDialogProps) {
  const [description, setDescription] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [issueUrl, setIssueUrl] = useState<string | null>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') onClose();
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [onClose]);

  useEffect(() => {
    const previouslyFocused = document.activeElement as HTMLElement | null;
    closeButtonRef.current?.focus();
    return () => {
      previouslyFocused?.focus();
    };
  }, []);

  // REQ-903/ADR-0064: submits an in-app bug report — server-rejected for a
  // guest (403) regardless of what the client sends, same "advertised, not
  // hidden, but disabled" gating REQ-215's suggestion entry point already
  // established; the disabled `<textarea>`/submit button below is the
  // primary guard for a guest account, this is defense in depth only.
  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setIssueUrl(null);

    const trimmed = description.trim();
    if (trimmed.length === 0) {
      setError('Please describe the problem.');
      return;
    }
    if (trimmed.length > INCIDENT_REPORT_DESCRIPTION_MAX_LENGTH) {
      setError(`Please keep the description under ${INCIDENT_REPORT_DESCRIPTION_MAX_LENGTH} characters.`);
      return;
    }

    setSubmitting(true);
    try {
      const created = await reportIncident(accessToken, trimmed, route);
      setIssueUrl(created.issueUrl);
      setDescription('');
    } catch (err) {
      // Same "any other 401 is a dead token" handling every other
      // authenticated screen in this app already uses.
      if (err instanceof ApiError && err.status === 401) {
        onAuthError();
        return;
      }
      // A 429 (rate limit) or 503 (GitHub call failed) both surface here
      // with the server's own detail text — describeError already prefers
      // ApiError.detail over a generic message, no special-casing needed.
      setError(describeError(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="incident-report-dialog-backdrop" onClick={onClose}>
      <div
        className="incident-report-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="incident-report-dialog-title"
        data-testid="incident-report-dialog"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="incident-report-dialog__header">
          <h3 id="incident-report-dialog-title">Report a problem</h3>
          <button
            ref={closeButtonRef}
            type="button"
            className="incident-report-dialog__close"
            onClick={onClose}
            aria-label="Close"
          >
            ×
          </button>
        </div>

        {isGuest && (
          <p className="incident-report-dialog__hint" data-testid="incident-report-guest-locked-copy">
            {INCIDENT_REPORT_GUEST_LOCKED_COPY}
          </p>
        )}

        <form className="incident-report-dialog__form" onSubmit={handleSubmit}>
          <label className="incident-report-dialog__field">
            <span>What went wrong?</span>
            <textarea
              className="incident-report-dialog__textarea"
              maxLength={INCIDENT_REPORT_DESCRIPTION_MAX_LENGTH}
              rows={5}
              placeholder={INCIDENT_REPORT_DESCRIPTION_PLACEHOLDER}
              value={description}
              onChange={(event) => {
                setIssueUrl(null);
                setDescription(event.target.value);
              }}
              disabled={isGuest || submitting}
            />
          </label>

          {error && (
            <p className="incident-report-dialog__error" role="alert">
              {error}
            </p>
          )}

          {issueUrl && !error && (
            <p className="incident-report-dialog__success" role="status">
              {INCIDENT_REPORT_SUBMITTED_COPY}{' '}
              <a href={issueUrl} target="_blank" rel="noreferrer">
                View report
              </a>
            </p>
          )}

          <button
            type="submit"
            className="incident-report-dialog__submit"
            disabled={isGuest || submitting}
          >
            {submitting ? 'Sending…' : 'Send report'}
          </button>
        </form>
      </div>
    </div>
  );
}
