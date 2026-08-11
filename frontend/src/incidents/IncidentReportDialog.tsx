import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError, describeError } from '../lib/apiClient';
import { reportIncident } from '../lib/incidents';
import {
  INCIDENT_REPORT_DEFAULT_SCREEN_OPTION,
  INCIDENT_REPORT_DESCRIPTION_PLACEHOLDER,
  INCIDENT_REPORT_GUEST_LOCKED_COPY,
  INCIDENT_REPORT_SCREEN_OPTIONS,
  INCIDENT_REPORT_SUBMITTED_COPY,
  INCIDENT_REPORT_TITLE_PLACEHOLDER,
} from '../lib/incidentReportCopy';
import './IncidentReportDialog.css';

// REQ-903/ADR-0064: match IncidentEndpoints' own limits exactly — client-side
// enforcement is defense in depth, not the primary guard (the server
// re-checks every one of these regardless of what the client sends).
const INCIDENT_REPORT_TITLE_MAX_LENGTH = 120;
const INCIDENT_REPORT_DESCRIPTION_MAX_LENGTH = 4000;

export interface IncidentReportDialogProps {
  accessToken: string;
  // REQ-903's "advertised, not hidden" guest rule (REQ-215's own
  // precedent): the dialog still opens for a guest, showing the
  // guest-locked copy with the form disabled, rather than the footer entry
  // point simply not existing for one.
  isGuest: boolean;
  // The current screen (App.tsx's own Screen union, passed through as a
  // plain string) at the moment this was opened — pre-selects the Screen
  // dropdown below, but the player can change it (the report might be
  // about a different screen than whatever's showing right now). Falls
  // back to "Something else / not sure" if it doesn't match a known
  // option.
  currentScreen: string;
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
//
// Structured-fields addition (2026-08-10, same day, requested directly):
// Title and Screen are now mandatory, separate fields instead of being
// folded into free-text Description, and Environment is captured
// automatically from window.location.origin rather than typed — so every
// report this dialog submits ends up formatted the same way on the GitHub
// side (IncidentReportService), regardless of what any individual player
// writes.
export function IncidentReportDialog({ accessToken, isGuest, currentScreen, onClose, onAuthError }: IncidentReportDialogProps) {
  const [title, setTitle] = useState('');
  const [screen, setScreen] = useState(() =>
    INCIDENT_REPORT_SCREEN_OPTIONS.some((option) => option.value === currentScreen)
      ? currentScreen
      : INCIDENT_REPORT_DEFAULT_SCREEN_OPTION,
  );
  const [description, setDescription] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [issueUrl, setIssueUrl] = useState<string | null>(null);
  const closeButtonRef = useRef<HTMLButtonElement>(null);

  // REQ-903: "found in environment... can be set in the background since
  // we know from what url" — computed once, read-only, never a form field
  // the player can edit. window.location.origin (e.g.
  // "https://xg-arcade-dev.azurestaticapps.net" or "http://localhost:5173")
  // is this frontend's own deployed origin, the most direct answer to
  // "which environment" available client-side without threading a new prop
  // through App.tsx.
  const environment = window.location.origin;

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
  // established; the disabled fields/submit button below are the primary
  // guard for a guest account, this is defense in depth only.
  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setIssueUrl(null);

    const trimmedTitle = title.trim();
    if (trimmedTitle.length === 0) {
      setError('Please add a short title.');
      return;
    }
    if (trimmedTitle.length > INCIDENT_REPORT_TITLE_MAX_LENGTH) {
      setError(`Please keep the title under ${INCIDENT_REPORT_TITLE_MAX_LENGTH} characters.`);
      return;
    }

    const trimmedDescription = description.trim();
    if (trimmedDescription.length === 0) {
      setError('Please describe the problem.');
      return;
    }
    if (trimmedDescription.length > INCIDENT_REPORT_DESCRIPTION_MAX_LENGTH) {
      setError(`Please keep the description under ${INCIDENT_REPORT_DESCRIPTION_MAX_LENGTH} characters.`);
      return;
    }

    setSubmitting(true);
    try {
      const created = await reportIncident(accessToken, trimmedTitle, trimmedDescription, screen, environment);
      setIssueUrl(created.issueUrl);
      setTitle('');
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
            <span>Title</span>
            <input
              type="text"
              className="incident-report-dialog__input"
              maxLength={INCIDENT_REPORT_TITLE_MAX_LENGTH}
              placeholder={INCIDENT_REPORT_TITLE_PLACEHOLDER}
              value={title}
              onChange={(event) => {
                setIssueUrl(null);
                setTitle(event.target.value);
              }}
              disabled={isGuest || submitting}
            />
          </label>

          <label className="incident-report-dialog__field">
            <span>Screen</span>
            <select
              className="incident-report-dialog__select"
              value={screen}
              onChange={(event) => setScreen(event.target.value)}
              disabled={isGuest || submitting}
            >
              {INCIDENT_REPORT_SCREEN_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>

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

          {/* REQ-903: environment is shown for transparency, but is never
              an editable field — see this component's own `environment`
              comment above for why. */}
          <p className="incident-report-dialog__environment" data-testid="incident-report-environment">
            Environment: {environment}
          </p>

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
